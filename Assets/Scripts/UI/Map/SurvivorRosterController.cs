using System.Collections.Generic;
using Fusion;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SimpleFPS
{
	public sealed class SurvivorRosterController : MonoBehaviour
	{
		[Header("References")]
		public GameMapView MapView;
		public GameMapSelectionController SelectionController;
		public GameMapIconController IconController;
		public RectTransform RosterRoot;
		[Tooltip("Rect that blocks map clicks while the pointer is over the roster. Leave empty to use the scroll view when available, otherwise the roster root.")]
		public RectTransform PointerBlocker;
		public ScrollRect ScrollRect;
		public RectTransform Content;
		public ResponsiveRosterGrid ResponsiveGrid;
		public SurvivorRosterEntry EntryPrefab;

		[Header("Bulk Toggles")]
		public Toggle BulkCollectPickupsToggle;
		public Toggle BulkInvestigateToggle;
		public Toggle BulkRecruitToggle;
		public Toggle BulkCombatActivationToggle;
		[HideInInspector]
		public Toggle BulkCombatMovementToggle;
		public SurvivorWeaponPreferenceControl BulkWeaponPreferenceControl;

		[Header("Hover Link")]
		public RectTransform HoverLine;
		public Image HoverLineImage;
		public Color HoverLineColor = new Color(0.5f, 0.9f, 1f, 0.8f);
		public float HoverLineThickness = 2f;

		[Header("Runtime UI")]
		public bool CreateRuntimeUIIfMissing = true;
		public float RuntimePanelWidth = 330f;
		public float RefreshInterval = 0.12f;

		private readonly Dictionary<Survivor, SurvivorRosterEntry> _entries = new();
		private readonly List<Survivor> _survivors = new(32);
		private readonly List<Survivor> _staleSurvivors = new(32);
		private readonly List<KnownEnemyInfo> _knownEnemies = new(8);
		private readonly List<SurvivorRosterEntry> _entryOrder = new(32);
		private Gameplay _gameplay;
		private NetworkRunner _runner;
		private Survivor _hoveredSurvivor;
		private Survivor _pendingScrollSurvivor;
		private bool _listenersAttached;
		private bool _suppressBulkToggleEvents;
		private bool _wasMapOpen;
		private float _nextRefreshTime;

		public void Tick(GameMapView mapView, Gameplay gameplay, NetworkRunner runner)
		{
			MapView = mapView != null ? mapView : MapView;
			_gameplay = gameplay;
			_runner = runner;

			if (MapView == null || gameplay == null || runner == null)
				return;

			EnsureSetup();
			if (RosterRoot == null)
				return;

			if (_wasMapOpen == false && MapView.IsMapOpen)
			{
				RefreshRoster(true);
				RefreshBulkSnapshot();
			}
			_wasMapOpen = MapView.IsMapOpen;

			if (Time.unscaledTime >= _nextRefreshTime)
			{
				RefreshRoster(false);
				_nextRefreshTime = Time.unscaledTime + Mathf.Max(0.02f, RefreshInterval);
			}

			if (_pendingScrollSurvivor != null)
			{
				ScrollToSurvivor(_pendingScrollSurvivor);
				_pendingScrollSurvivor = null;
			}

			UpdateHoverLine();
		}

		private void OnDisable()
		{
			if (SelectionController != null)
			{
				SelectionController.SelectionChanged -= HandleSelectionChanged;
				SelectionController.SurvivorSelected -= HandleSurvivorSelected;
			}

			_listenersAttached = false;
		}

		private void EnsureSetup()
		{
			DisableLegacyCombatMovementToggle();

			if (MapView == null)
				MapView = GetComponentInParent<GameMapView>();
			if (SelectionController == null && MapView != null)
				SelectionController = MapView.SelectionController;
			if (IconController == null && MapView != null)
				IconController = MapView.IconController;

			if (CreateRuntimeUIIfMissing)
				EnsureRuntimeUI();
			else
				EnsureHoverLine();

			if (SelectionController != null)
				SelectionController.InputBlocker = GetPointerBlocker();

			AttachListeners();
		}

		private RectTransform GetPointerBlocker()
		{
			if (PointerBlocker != null)
				return PointerBlocker;
			if (ScrollRect != null)
				return ScrollRect.transform as RectTransform;

			return RosterRoot;
		}

		private void AttachListeners()
		{
			if (_listenersAttached)
				return;

			if (SelectionController != null)
			{
				SelectionController.SelectionChanged += HandleSelectionChanged;
				SelectionController.SurvivorSelected += HandleSurvivorSelected;
			}

			AddBulkToggleListener(BulkCollectPickupsToggle, ESurvivorAISetting.CollectVisiblePickups);
			AddBulkToggleListener(BulkInvestigateToggle, ESurvivorAISetting.InvestigateSuspiciousStimuli);
			AddBulkToggleListener(BulkRecruitToggle, ESurvivorAISetting.RecruitNeutralSurvivors);
			AddBulkToggleListener(BulkCombatActivationToggle, ESurvivorAISetting.AllowCombatAIActivation);
			if (BulkWeaponPreferenceControl != null)
				BulkWeaponPreferenceControl.ValueChanged += HandleBulkWeaponPreferenceChanged;

			_listenersAttached = true;
		}

		private void AddBulkToggleListener(Toggle toggle, ESurvivorAISetting setting)
		{
			if (toggle == null)
				return;

			toggle.onValueChanged.AddListener(value => HandleBulkToggleChanged(setting, value));
		}

		private void RefreshRoster(bool forceLayout)
		{
			if (_gameplay == null || _runner == null)
				return;

			CollectLocalSurvivors(_survivors);
			_staleSurvivors.Clear();
			_staleSurvivors.AddRange(_entries.Keys);

			for (int i = 0; i < _survivors.Count; i++)
			{
				Survivor survivor = _survivors[i];
				if (_entries.TryGetValue(survivor, out var entry) == false || entry == null)
				{
					entry = CreateEntry();
					_entries[survivor] = entry;
					entry.Bind(survivor);
					entry.Clicked += HandleEntryClicked;
					entry.HoverChanged += HandleEntryHoverChanged;
					entry.SettingChanged += HandleEntrySettingChanged;
					entry.WeaponPreferenceChanged += HandleEntryWeaponPreferenceChanged;
					forceLayout = true;
				}

				_staleSurvivors.Remove(survivor);
				bool selected = SelectionController != null && SelectionController.IsSelected(survivor);
				bool possessed = IsPossessed(survivor);
				entry.Refresh(selected, possessed, GetThreatState(survivor));
			}

			for (int i = 0; i < _staleSurvivors.Count; i++)
			{
				Survivor survivor = _staleSurvivors[i];
				if (_entries.TryGetValue(survivor, out var entry) && entry != null)
					Destroy(entry.gameObject);
				_entries.Remove(survivor);
			}

			_staleSurvivors.Clear();
			if (forceLayout)
				SortEntriesAndRebuild();
		}

		private SurvivorRosterEntry CreateEntry()
		{
			SurvivorRosterEntry entry;
			if (EntryPrefab != null)
			{
				entry = Instantiate(EntryPrefab, Content);
			}
			else
			{
				var entryObject = new GameObject("Survivor Roster Entry", typeof(RectTransform), typeof(SurvivorRosterEntry));
				entryObject.transform.SetParent(Content, false);
				entry = entryObject.GetComponent<SurvivorRosterEntry>();
			}

			return entry;
		}

		private void SortEntriesAndRebuild()
		{
			_entryOrder.Clear();
			foreach (var pair in _entries)
			{
				if (pair.Value != null)
					_entryOrder.Add(pair.Value);
			}

			_entryOrder.Sort((a, b) =>
			{
				int aIndex = a != null && a.Survivor != null ? a.Survivor.CharacterIndex : int.MaxValue;
				int bIndex = b != null && b.Survivor != null ? b.Survivor.CharacterIndex : int.MaxValue;
				return aIndex.CompareTo(bIndex);
			});

			for (int i = 0; i < _entryOrder.Count; i++)
				_entryOrder[i].transform.SetSiblingIndex(i);

			ResponsiveGrid?.Rebuild();
		}

		private void HandleEntryClicked(Survivor survivor)
		{
			if (SelectionController == null || _gameplay == null || _runner == null)
				return;

			SelectionController.ToggleSelectionFromRoster(survivor, _gameplay, _runner);
		}

		private void HandleEntryHoverChanged(Survivor survivor, bool hovered)
		{
			_hoveredSurvivor = hovered ? survivor : (_hoveredSurvivor == survivor ? null : _hoveredSurvivor);
			if (hovered == false && HoverLine != null)
				HoverLine.gameObject.SetActive(false);
		}

		private void HandleEntrySettingChanged(Survivor survivor, ESurvivorAISetting setting, bool enabled)
		{
			if (_gameplay == null || survivor == null)
				return;

			var mask = new CharacterMask128();
			mask.Set(survivor.CharacterIndex, true);
			_gameplay.RequestMapAISetting(mask, setting, enabled);
		}

		private void HandleEntryWeaponPreferenceChanged(Survivor survivor, ESurvivorWeaponPreference preference)
		{
			if (_gameplay == null || survivor == null)
				return;

			var mask = new CharacterMask128();
			mask.Set(survivor.CharacterIndex, true);
			_gameplay.RequestMapWeaponPreference(mask, preference);
		}

		private void HandleBulkToggleChanged(ESurvivorAISetting setting, bool enabled)
		{
			if (_suppressBulkToggleEvents || _gameplay == null || _runner == null)
				return;

			CharacterMask128 mask = GetBulkTargetMask();
			if (mask.IsEmpty)
				return;

			_gameplay.RequestMapAISetting(mask, setting, enabled);
			SetBulkToggleVisual(setting, enabled);
		}

		private void HandleBulkWeaponPreferenceChanged(ESurvivorWeaponPreference preference)
		{
			if (_suppressBulkToggleEvents || _gameplay == null || _runner == null)
				return;

			CharacterMask128 mask = GetBulkTargetMask();
			if (mask.IsEmpty)
				return;

			_gameplay.RequestMapWeaponPreference(mask, preference);
			BulkWeaponPreferenceControl?.SetValueWithoutNotify(preference);
		}

		private void HandleSelectionChanged()
		{
			RefreshBulkSnapshot();
		}

		private void HandleSurvivorSelected(Survivor survivor)
		{
			_pendingScrollSurvivor = survivor;
		}

		private void RefreshBulkSnapshot()
		{
			if (_gameplay == null || _runner == null)
				return;

			CollectBulkTargets(_survivors);
			_suppressBulkToggleEvents = true;
			SetToggleWithoutNotify(BulkCollectPickupsToggle, GetMajorityValue(_survivors, ESurvivorAISetting.CollectVisiblePickups));
			SetToggleWithoutNotify(BulkInvestigateToggle, GetMajorityValue(_survivors, ESurvivorAISetting.InvestigateSuspiciousStimuli));
			SetToggleWithoutNotify(BulkRecruitToggle, GetMajorityValue(_survivors, ESurvivorAISetting.RecruitNeutralSurvivors));
			SetToggleWithoutNotify(BulkCombatActivationToggle, GetMajorityValue(_survivors, ESurvivorAISetting.AllowCombatAIActivation));
			BulkWeaponPreferenceControl?.SetValueWithoutNotify(GetMajorityWeaponPreference(_survivors));
			_suppressBulkToggleEvents = false;
		}

		private static ESurvivorWeaponPreference GetMajorityWeaponPreference(List<Survivor> survivors)
		{
			int automatic = 0;
			int strong = 0;
			int pistol = 0;
			int holdFire = 0;
			for (int i = 0; i < survivors.Count; i++)
			{
				switch (survivors[i].CombatAISettings.WeaponPreference)
				{
					case ESurvivorWeaponPreference.PreferStrongWeapons:
						strong++;
						break;
					case ESurvivorWeaponPreference.PreferPistol:
						pistol++;
						break;
					case ESurvivorWeaponPreference.HoldFire:
						holdFire++;
						break;
					default:
						automatic++;
						break;
				}
			}

			if (holdFire > automatic && holdFire > strong && holdFire > pistol)
				return ESurvivorWeaponPreference.HoldFire;
			if (strong > automatic && strong > pistol && strong > holdFire)
				return ESurvivorWeaponPreference.PreferStrongWeapons;
			if (pistol > automatic && pistol > strong && pistol > holdFire)
				return ESurvivorWeaponPreference.PreferPistol;
			return ESurvivorWeaponPreference.Automatic;
		}

		private bool GetMajorityValue(List<Survivor> survivors, ESurvivorAISetting setting)
		{
			int enabled = 0;
			int disabled = 0;
			for (int i = 0; i < survivors.Count; i++)
			{
				if (GetSettingValue(survivors[i], setting))
					enabled++;
				else
					disabled++;
			}

			return enabled > disabled;
		}

		private void SetBulkToggleVisual(ESurvivorAISetting setting, bool enabled)
		{
			_suppressBulkToggleEvents = true;
			switch (setting)
			{
				case ESurvivorAISetting.CollectVisiblePickups:
					SetToggleWithoutNotify(BulkCollectPickupsToggle, enabled);
					break;
				case ESurvivorAISetting.InvestigateSuspiciousStimuli:
					SetToggleWithoutNotify(BulkInvestigateToggle, enabled);
					break;
				case ESurvivorAISetting.RecruitNeutralSurvivors:
					SetToggleWithoutNotify(BulkRecruitToggle, enabled);
					break;
				case ESurvivorAISetting.AllowCombatAIActivation:
					SetToggleWithoutNotify(BulkCombatActivationToggle, enabled);
					break;
			}
			_suppressBulkToggleEvents = false;
		}

		private CharacterMask128 GetBulkTargetMask()
		{
			if (SelectionController != null)
			{
				CharacterMask128 selected = SelectionController.BuildSelectedCommandMask(_gameplay, _runner);
				if (selected.IsEmpty == false)
					return selected;
			}

			var mask = new CharacterMask128();
			if (_gameplay.PlayerData.TryGet(_runner.LocalPlayer, out var data) == false)
				return mask;

			for (int i = 0; i < data.CharacterCount; i++)
			{
				if (data.IsCharacterAlive(i))
					mask.Set(i, true);
			}

			return mask;
		}

		private void CollectBulkTargets(List<Survivor> results)
		{
			results.Clear();
			if (_gameplay == null || _runner == null)
				return;

			if (SelectionController != null)
			{
				foreach (var selected in SelectionController.SelectedSurvivors)
				{
					if (SelectionController.IsCommandSelectable(selected, _gameplay, _runner))
						results.Add(selected);
				}
			}

			if (results.Count > 0)
				return;

			CollectLocalSurvivors(results);
		}

		private void CollectLocalSurvivors(List<Survivor> results)
		{
			results.Clear();
			if (_gameplay == null || _runner == null)
				return;
			if (_gameplay.PlayerData.TryGet(_runner.LocalPlayer, out var data) == false)
				return;

			for (int i = 0; i < data.CharacterCount; i++)
			{
				if (data.IsCharacterAlive(i) == false)
					continue;

				Survivor survivor = _gameplay.GetSurvivor(_runner.LocalPlayer, i);
				if (survivor == null || survivor.Health == null || survivor.Health.IsAlive == false)
					continue;

				results.Add(survivor);
			}
		}

		private ESurvivorRosterThreatState GetThreatState(Survivor survivor)
		{
			if (survivor == null || survivor.Sensor == null)
				return ESurvivorRosterThreatState.None;

			bool seesZombie = false;
			_knownEnemies.Clear();
			survivor.Sensor.GetDirectKnownEnemies(_knownEnemies);

			for (int i = 0; i < _knownEnemies.Count; i++)
			{
				var obj = _knownEnemies[i].Object;
				if (obj == null)
					continue;

				if (obj.GetComponent<ZombieCharacter>() != null)
				{
					seesZombie = true;
					continue;
				}

				var otherSurvivor = obj.GetComponent<Survivor>();
				if (otherSurvivor != null &&
				    otherSurvivor.Health != null &&
				    otherSurvivor.Health.IsAlive &&
				    CharacterFactionUtility.CanSurvivorAutoAttack(survivor, obj))
				{
					_knownEnemies.Clear();
					return ESurvivorRosterThreatState.EnemySurvivor;
				}
			}

			_knownEnemies.Clear();
			return seesZombie ? ESurvivorRosterThreatState.ZombiesOnly : ESurvivorRosterThreatState.None;
		}

		private bool IsPossessed(Survivor survivor)
		{
			return survivor != null && _gameplay != null && _gameplay.PlayerData.TryGet(survivor.OwnerRef, out var data) && data.ActiveCharacterIndex == survivor.CharacterIndex;
		}

		private static bool GetSettingValue(Survivor survivor, ESurvivorAISetting setting)
		{
			if (survivor == null)
				return false;

			return setting switch
			{
				ESurvivorAISetting.CollectVisiblePickups => survivor.NonCombatAISettings.CollectVisiblePickups,
				ESurvivorAISetting.InvestigateSuspiciousStimuli => survivor.NonCombatAISettings.InvestigateSuspiciousStimuli,
				ESurvivorAISetting.RecruitNeutralSurvivors => survivor.NonCombatAISettings.RecruitNeutralSurvivors,
				ESurvivorAISetting.AllowCombatAIActivation => survivor.NonCombatAISettings.AllowCombatAIActivation,
				_ => false,
			};
		}

		private static void SetToggleWithoutNotify(Toggle toggle, bool value)
		{
			if (toggle != null)
				toggle.SetIsOnWithoutNotify(value);
		}

		private void ScrollToSurvivor(Survivor survivor)
		{
			if (survivor == null || ScrollRect == null || Content == null || ScrollRect.viewport == null)
				return;
			if (_entries.TryGetValue(survivor, out var entry) == false || entry == null)
				return;

			Canvas.ForceUpdateCanvases();
			ResponsiveGrid?.Rebuild();

			RectTransform entryRect = entry.RectTransform;
			if (entryRect == null)
				return;

			float viewportHeight = ScrollRect.viewport.rect.height;
			float contentHeight = Content.rect.height;
			if (contentHeight <= viewportHeight)
				return;

			float entryTop = -entryRect.anchoredPosition.y;
			float entryBottom = entryTop + entryRect.rect.height;
			float viewTop = Content.anchoredPosition.y;
			float viewBottom = viewTop + viewportHeight;
			float targetTop = viewTop;

			if (entryTop < viewTop)
				targetTop = entryTop;
			else if (entryBottom > viewBottom)
				targetTop = entryBottom - viewportHeight;
			else
				return;

			targetTop = Mathf.Clamp(targetTop, 0f, Mathf.Max(0f, contentHeight - viewportHeight));
			Content.anchoredPosition = new Vector2(Content.anchoredPosition.x, targetTop);
		}

		private void UpdateHoverLine()
		{
			Survivor hoverSurvivor = GetHoverLineSurvivor();
			if (hoverSurvivor == null || HoverLine == null || IconController == null || MapView == null)
			{
				if (HoverLine != null)
					HoverLine.gameObject.SetActive(false);
				return;
			}
			if (_entries.TryGetValue(hoverSurvivor, out var entry) == false || entry == null)
			{
				HoverLine.gameObject.SetActive(false);
				return;
			}
			if (IconController.TryGetVisibleSurvivorIconRect(hoverSurvivor, out var iconRect) == false)
			{
				HoverLine.gameObject.SetActive(false);
				return;
			}

			RectTransform lineParent = HoverLine.parent as RectTransform;
			if (lineParent == null)
				return;

			Camera eventCamera = MapView.GetEventCamera();
			Vector3[] cardCorners = new Vector3[4];
			entry.RectTransform.GetWorldCorners(cardCorners);
			Vector2 cardScreen = RectTransformUtility.WorldToScreenPoint(eventCamera, cardCorners[3]);
			Vector2 iconScreen = RectTransformUtility.WorldToScreenPoint(eventCamera, iconRect.position);

			if (RectTransformUtility.ScreenPointToLocalPointInRectangle(lineParent, cardScreen, eventCamera, out var cardLocal) == false ||
			    RectTransformUtility.ScreenPointToLocalPointInRectangle(lineParent, iconScreen, eventCamera, out var iconLocal) == false)
				return;

			Vector2 delta = iconLocal - cardLocal;
			float length = delta.magnitude;
			if (length <= 1f)
			{
				HoverLine.gameObject.SetActive(false);
				return;
			}

			HoverLine.gameObject.SetActive(true);
			HoverLine.SetAsLastSibling();
			HoverLine.anchoredPosition = (cardLocal + iconLocal) * 0.5f;
			HoverLine.sizeDelta = new Vector2(length, Mathf.Max(1f, HoverLineThickness));
			HoverLine.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
			if (HoverLineImage != null)
				HoverLineImage.color = HoverLineColor;
		}

		private Survivor GetHoverLineSurvivor()
		{
			if (_hoveredSurvivor != null)
				return _hoveredSurvivor;
			if (IconController == null || MapView == null)
				return null;

			var mouse = Mouse.current;
			if (mouse == null)
				return null;

			Vector2 mousePosition = mouse.position.ReadValue();
			Camera eventCamera = MapView.GetEventCamera();
			if (SelectionController != null && SelectionController.IsPointerBlocked(mousePosition, eventCamera))
				return null;

			GameMapIcon icon = IconController.FindOwnIconAt(mousePosition, eventCamera);
			Survivor survivor = icon != null ? icon.Survivor : null;
			return survivor != null && _entries.ContainsKey(survivor) ? survivor : null;
		}

		private void EnsureRuntimeUI()
		{
			if (RosterRoot != null && ScrollRect != null && Content != null)
			{
				if (ResponsiveGrid == null)
					ResponsiveGrid = Content.GetComponent<ResponsiveRosterGrid>();
				if (BulkWeaponPreferenceControl == null)
					EnsureRuntimeBulkBar();
				EnsureHoverLine();
				return;
			}
			if (MapView == null || MapView.MapRoot == null)
				return;

			RectTransform mapRoot = MapView.MapRoot.transform as RectTransform;
			if (mapRoot == null)
				return;

			if (RosterRoot == null)
			{
				var rootObject = new GameObject("SurvivorRosterPanel", typeof(RectTransform), typeof(Image));
				rootObject.transform.SetParent(mapRoot, false);
				RosterRoot = rootObject.GetComponent<RectTransform>();
				RosterRoot.anchorMin = new Vector2(0f, 0f);
				RosterRoot.anchorMax = new Vector2(0f, 1f);
				RosterRoot.pivot = new Vector2(0f, 0.5f);
				RosterRoot.offsetMin = new Vector2(12f, 12f);
				RosterRoot.offsetMax = new Vector2(12f + RuntimePanelWidth, -12f);
				var image = rootObject.GetComponent<Image>();
				image.color = new Color(0.02f, 0.03f, 0.04f, 0.72f);
				image.raycastTarget = true;
			}

			EnsureRuntimeBulkBar();
			EnsureRuntimeScrollView();
			EnsureHoverLine();
		}

		private void EnsureRuntimeBulkBar()
		{
			Transform existing = RosterRoot.Find("BulkToggleBar");
			RectTransform bar;
			if (existing != null)
			{
				bar = existing as RectTransform;
			}
			else
			{
				var barObject = new GameObject("BulkToggleBar", typeof(RectTransform), typeof(HorizontalLayoutGroup));
				barObject.transform.SetParent(RosterRoot, false);
				bar = barObject.GetComponent<RectTransform>();
				bar.anchorMin = new Vector2(0f, 1f);
				bar.anchorMax = new Vector2(1f, 1f);
				bar.pivot = new Vector2(0.5f, 1f);
				bar.offsetMin = new Vector2(8f, -40f);
				bar.offsetMax = new Vector2(-8f, -8f);
				var layout = barObject.GetComponent<HorizontalLayoutGroup>();
				layout.spacing = 5f;
				layout.childAlignment = TextAnchor.MiddleLeft;
				layout.childControlWidth = false;
				layout.childControlHeight = true;
				layout.childForceExpandWidth = false;
				layout.childForceExpandHeight = true;
			}

			BulkCollectPickupsToggle ??= CreateBulkToggle(bar, "P");
			BulkInvestigateToggle ??= CreateBulkToggle(bar, "?");
			BulkRecruitToggle ??= CreateBulkToggle(bar, "+");
			BulkCombatActivationToggle ??= CreateBulkToggle(bar, "!");
			BulkWeaponPreferenceControl ??= CreateBulkWeaponPreferenceControl(bar);
		}

		private void DisableLegacyCombatMovementToggle()
		{
			if (BulkCombatMovementToggle != null)
				BulkCombatMovementToggle.gameObject.SetActive(false);
		}

		private void EnsureRuntimeScrollView()
		{
			if (ScrollRect != null && Content != null)
				return;

			var scrollObject = new GameObject("Scroll View", typeof(RectTransform), typeof(ScrollRect));
			scrollObject.transform.SetParent(RosterRoot, false);
			var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
			scrollRectTransform.anchorMin = Vector2.zero;
			scrollRectTransform.anchorMax = Vector2.one;
			scrollRectTransform.offsetMin = new Vector2(8f, 8f);
			scrollRectTransform.offsetMax = new Vector2(-8f, -46f);

			var viewportObject = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
			viewportObject.transform.SetParent(scrollObject.transform, false);
			var viewport = viewportObject.GetComponent<RectTransform>();
			viewport.anchorMin = Vector2.zero;
			viewport.anchorMax = Vector2.one;
			viewport.offsetMin = Vector2.zero;
			viewport.offsetMax = Vector2.zero;
			viewportObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.12f);

			var contentObject = new GameObject("Content", typeof(RectTransform), typeof(GridLayoutGroup), typeof(ResponsiveRosterGrid));
			contentObject.transform.SetParent(viewportObject.transform, false);
			Content = contentObject.GetComponent<RectTransform>();
			Content.anchorMin = new Vector2(0f, 1f);
			Content.anchorMax = new Vector2(1f, 1f);
			Content.pivot = new Vector2(0.5f, 1f);
			Content.offsetMin = Vector2.zero;
			Content.offsetMax = Vector2.zero;

			ResponsiveGrid = contentObject.GetComponent<ResponsiveRosterGrid>();
			ResponsiveGrid.Viewport = viewport;
			ResponsiveGrid.Grid = contentObject.GetComponent<GridLayoutGroup>();
			ResponsiveGrid.Grid.padding = new RectOffset(4, 4, 4, 4);

			ScrollRect = scrollObject.GetComponent<ScrollRect>();
			ScrollRect.viewport = viewport;
			ScrollRect.content = Content;
			ScrollRect.horizontal = false;
			ScrollRect.vertical = true;
			ScrollRect.movementType = ScrollRect.MovementType.Clamped;
		}

		private Toggle CreateBulkToggle(RectTransform parent, string label)
		{
			var root = new GameObject(label, typeof(RectTransform), typeof(Toggle), typeof(Image), typeof(LayoutElement));
			root.transform.SetParent(parent, false);
			var rect = root.GetComponent<RectTransform>();
			rect.sizeDelta = new Vector2(36f, 28f);
			var layout = root.GetComponent<LayoutElement>();
			layout.preferredWidth = 36f;
			layout.preferredHeight = 28f;
			var background = root.GetComponent<Image>();
			background.color = new Color(0.08f, 0.12f, 0.15f, 0.95f);

			var checkObject = new GameObject("Check", typeof(RectTransform), typeof(Image));
			checkObject.transform.SetParent(root.transform, false);
			var checkRect = checkObject.GetComponent<RectTransform>();
			checkRect.anchorMin = new Vector2(0.14f, 0.14f);
			checkRect.anchorMax = new Vector2(0.86f, 0.86f);
			checkRect.offsetMin = Vector2.zero;
			checkRect.offsetMax = Vector2.zero;
			var check = checkObject.GetComponent<Image>();
			check.color = new Color(0.3f, 0.85f, 1f, 0.9f);

			var textObject = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
			textObject.transform.SetParent(root.transform, false);
			var textRect = textObject.GetComponent<RectTransform>();
			textRect.anchorMin = Vector2.zero;
			textRect.anchorMax = Vector2.one;
			textRect.offsetMin = Vector2.zero;
			textRect.offsetMax = Vector2.zero;
			var text = textObject.GetComponent<TextMeshProUGUI>();
			text.text = label;
			text.fontSize = 13;
			text.alignment = TextAlignmentOptions.Center;
			text.color = Color.white;
			text.raycastTarget = false;

			var toggle = root.GetComponent<Toggle>();
			toggle.targetGraphic = background;
			toggle.graphic = check;
			return toggle;
		}

		private SurvivorWeaponPreferenceControl CreateBulkWeaponPreferenceControl(RectTransform parent)
		{
			var root = new GameObject(
				"Weapon Preference",
				typeof(RectTransform),
				typeof(Image),
				typeof(Button),
				typeof(LayoutElement),
				typeof(SurvivorWeaponPreferenceControl));
			root.transform.SetParent(parent, false);
			root.GetComponent<RectTransform>().sizeDelta = new Vector2(68f, 28f);
			var layout = root.GetComponent<LayoutElement>();
			layout.preferredWidth = 68f;
			layout.preferredHeight = 28f;
			return root.GetComponent<SurvivorWeaponPreferenceControl>();
		}

		private void EnsureHoverLine()
		{
			if (HoverLine != null)
			{
				if (HoverLineImage == null)
					HoverLineImage = HoverLine.GetComponent<Image>();
				return;
			}
			if (MapView == null || MapView.MapRoot == null)
				return;

			var lineObject = new GameObject("RosterHoverLine", typeof(RectTransform), typeof(Image));
			lineObject.transform.SetParent(MapView.MapRoot.transform, false);
			HoverLine = lineObject.GetComponent<RectTransform>();
			HoverLine.anchorMin = new Vector2(0.5f, 0.5f);
			HoverLine.anchorMax = new Vector2(0.5f, 0.5f);
			HoverLine.pivot = new Vector2(0.5f, 0.5f);
			HoverLineImage = lineObject.GetComponent<Image>();
			HoverLineImage.color = HoverLineColor;
			HoverLineImage.raycastTarget = false;
			HoverLine.gameObject.SetActive(false);
		}
	}
}
