using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SimpleFPS
{
	public enum ESurvivorRosterThreatState
	{
		None,
		ZombiesOnly,
		EnemySurvivor,
	}

	public sealed class SurvivorRosterEntry : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
	{
		[Header("Root")]
		public Button SelectButton;
		public Image Background;
		public Outline BorderOutline;
		public Image BorderImage;

		[Header("Identity")]
		public Image FaceImage;
		public TextMeshProUGUI NameText;
		public GameObject CrownIcon;

		[Header("Health")]
		public Image HealthFill;

		[Header("Behavior Toggles")]
		public Toggle CollectPickupsToggle;
		public Toggle InvestigateToggle;
		public Toggle RecruitToggle;
		public Toggle CombatActivationToggle;
		[HideInInspector]
		public Toggle CombatMovementToggle;
		public SurvivorWeaponPreferenceControl WeaponPreferenceControl;

		[Header("Colors")]
		public Color NormalBackgroundColor = new Color(0.08f, 0.1f, 0.12f, 0.82f);
		public Color SelectedBackgroundColor = new Color(0.18f, 0.32f, 0.42f, 0.95f);
		public Color NormalBorderColor = new Color(0.45f, 0.5f, 0.55f, 0.75f);
		public Color ZombieBorderColor = new Color(1f, 0.82f, 0.15f, 1f);
		public Color EnemyBorderColor = new Color(1f, 0.18f, 0.12f, 1f);
		public Color HealthColor = new Color(0.1f, 0.85f, 0.2f, 1f);

		private Survivor _survivor;
		private bool _suppressToggleEvents;
		private bool _listenersAttached;

		public Survivor Survivor => _survivor;
		public RectTransform RectTransform => transform as RectTransform;

		public event Action<Survivor> Clicked;
		public event Action<Survivor, bool> HoverChanged;
		public event Action<Survivor, ESurvivorAISetting, bool> SettingChanged;
		public event Action<Survivor, ESurvivorWeaponPreference> WeaponPreferenceChanged;

		private void Awake()
		{
			DisableLegacyCombatMovementToggle();
			EnsureRuntimeLayout();
			AttachListeners();
		}

		private void OnDestroy()
		{
			if (_survivor != null)
				HoverChanged?.Invoke(_survivor, false);
		}

		public void Bind(Survivor survivor)
		{
			_survivor = survivor;
			name = survivor != null ? $"Roster Entry {survivor.CharacterIndex}" : "Roster Entry";
			RefreshStaticText();
		}

		public void Refresh(bool selected, bool possessed, ESurvivorRosterThreatState threatState)
		{
			EnsureRuntimeLayout();
			AttachListeners();
			RefreshStaticText();
			RefreshHealth();
			RefreshIdentity(selected, possessed, threatState);
			RefreshToggles();
		}

		public void OnPointerEnter(PointerEventData eventData)
		{
			if (_survivor != null)
				HoverChanged?.Invoke(_survivor, true);
		}

		public void OnPointerExit(PointerEventData eventData)
		{
			if (_survivor != null)
				HoverChanged?.Invoke(_survivor, false);
		}

		private void RefreshStaticText()
		{
			if (NameText != null)
				NameText.text = _survivor != null ? _survivor.name : "-";
		}

		private void RefreshHealth()
		{
			if (HealthFill == null || _survivor == null || _survivor.Health == null)
				return;

			float ratio = _survivor.Health.MaxHealth > 0f
				? Mathf.Clamp01(_survivor.Health.CurrentHealth / _survivor.Health.MaxHealth)
				: 0f;

			HealthFill.color = HealthColor;
			if (HealthFill.type == Image.Type.Filled)
			{
				HealthFill.fillAmount = ratio;
			}
			else
			{
				RectTransform rectTransform = HealthFill.rectTransform;
				rectTransform.anchorMin = Vector2.zero;
				rectTransform.anchorMax = new Vector2(ratio, 1f);
				rectTransform.offsetMin = Vector2.zero;
				rectTransform.offsetMax = Vector2.zero;
			}
		}

		private void RefreshIdentity(bool selected, bool possessed, ESurvivorRosterThreatState threatState)
		{
			if (Background != null)
				Background.color = selected ? SelectedBackgroundColor : NormalBackgroundColor;

			Color borderColor = threatState switch
			{
				ESurvivorRosterThreatState.EnemySurvivor => EnemyBorderColor,
				ESurvivorRosterThreatState.ZombiesOnly => ZombieBorderColor,
				_ => NormalBorderColor,
			};

			if (BorderOutline != null)
				BorderOutline.effectColor = borderColor;
			if (BorderImage != null)
				BorderImage.color = borderColor;
			if (CrownIcon != null)
				CrownIcon.SetActive(possessed);
		}

		private void RefreshToggles()
		{
			if (_survivor == null)
				return;

			_suppressToggleEvents = true;
			SetToggleWithoutNotify(CollectPickupsToggle, _survivor.NonCombatAISettings.CollectVisiblePickups);
			SetToggleWithoutNotify(InvestigateToggle, _survivor.NonCombatAISettings.InvestigateSuspiciousStimuli);
			SetToggleWithoutNotify(RecruitToggle, _survivor.NonCombatAISettings.RecruitNeutralSurvivors);
			SetToggleWithoutNotify(CombatActivationToggle, _survivor.NonCombatAISettings.AllowCombatAIActivation);
			WeaponPreferenceControl?.SetValueWithoutNotify(_survivor.CombatAISettings.WeaponPreference);
			_suppressToggleEvents = false;
		}

		private static void SetToggleWithoutNotify(Toggle toggle, bool value)
		{
			if (toggle != null)
				toggle.SetIsOnWithoutNotify(value);
		}

		private void AttachListeners()
		{
			if (_listenersAttached)
				return;

			if (SelectButton == null)
				SelectButton = GetComponent<Button>();
			if (SelectButton != null)
				SelectButton.onClick.AddListener(HandleClicked);

			AddToggleListener(CollectPickupsToggle, ESurvivorAISetting.CollectVisiblePickups);
			AddToggleListener(InvestigateToggle, ESurvivorAISetting.InvestigateSuspiciousStimuli);
			AddToggleListener(RecruitToggle, ESurvivorAISetting.RecruitNeutralSurvivors);
			AddToggleListener(CombatActivationToggle, ESurvivorAISetting.AllowCombatAIActivation);
			if (WeaponPreferenceControl != null)
				WeaponPreferenceControl.ValueChanged += HandleWeaponPreferenceChanged;

			_listenersAttached = true;
		}

		private void AddToggleListener(Toggle toggle, ESurvivorAISetting setting)
		{
			if (toggle == null)
				return;

			toggle.onValueChanged.AddListener(value => HandleToggleChanged(setting, value));
		}

		private void HandleClicked()
		{
			if (_survivor != null)
				Clicked?.Invoke(_survivor);
		}

		private void HandleToggleChanged(ESurvivorAISetting setting, bool value)
		{
			if (_suppressToggleEvents || _survivor == null)
				return;

			SettingChanged?.Invoke(_survivor, setting, value);
		}

		private void HandleWeaponPreferenceChanged(ESurvivorWeaponPreference preference)
		{
			if (_suppressToggleEvents || _survivor == null)
				return;

			WeaponPreferenceChanged?.Invoke(_survivor, preference);
		}

		private void EnsureRuntimeLayout()
		{
			if (Background == null)
			{
				Background = GetComponent<Image>();
				if (Background == null)
					Background = gameObject.AddComponent<Image>();
				Background.color = NormalBackgroundColor;
			}

			if (SelectButton == null)
			{
				SelectButton = GetComponent<Button>();
				if (SelectButton == null)
					SelectButton = gameObject.AddComponent<Button>();
			}

			if (BorderOutline == null)
			{
				BorderOutline = GetComponent<Outline>();
				if (BorderOutline == null)
					BorderOutline = gameObject.AddComponent<Outline>();
				BorderOutline.effectDistance = new Vector2(2f, -2f);
				BorderOutline.effectColor = NormalBorderColor;
			}

			RectTransform root = RectTransform;
			if (root != null)
			{
				root.anchorMin = new Vector2(0f, 1f);
				root.anchorMax = new Vector2(0f, 1f);
				root.pivot = new Vector2(0.5f, 0.5f);
			}

			if (FaceImage == null)
				FaceImage = CreateImage("Face", new Color(0.55f, 0.58f, 0.6f, 1f), new Vector2(6f, 22f), new Vector2(42f, 48f));
			if (NameText == null)
				NameText = CreateText("Name", new Vector2(54f, 56f), new Vector2(-58f, -6f), 13, TextAlignmentOptions.Left);
			if (CrownIcon == null)
				CrownIcon = CreateCrown();
			if (HealthFill == null)
				HealthFill = CreateHealthBar();

			CreateToggleRowIfNeeded();
		}

		private Image CreateImage(string objectName, Color color, Vector2 offsetMin, Vector2 offsetMax)
		{
			var child = new GameObject(objectName, typeof(RectTransform), typeof(Image));
			child.transform.SetParent(transform, false);
			var rect = child.GetComponent<RectTransform>();
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = offsetMin;
			rect.offsetMax = offsetMax;
			var image = child.GetComponent<Image>();
			image.color = color;
			image.raycastTarget = false;
			return image;
		}

		private TextMeshProUGUI CreateText(string objectName, Vector2 offsetMin, Vector2 offsetMax, int fontSize, TextAlignmentOptions alignment)
		{
			var child = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
			child.transform.SetParent(transform, false);
			var rect = child.GetComponent<RectTransform>();
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = offsetMin;
			rect.offsetMax = offsetMax;
			var text = child.GetComponent<TextMeshProUGUI>();
			text.fontSize = fontSize;
			text.alignment = alignment;
			text.color = Color.white;
			text.raycastTarget = false;
			text.enableWordWrapping = false;
			text.overflowMode = TextOverflowModes.Ellipsis;
			return text;
		}

		private GameObject CreateCrown()
		{
			var text = CreateText("Crown", new Vector2(-48f, 54f), new Vector2(-8f, -6f), 16, TextAlignmentOptions.Right);
			text.text = "^";
			return text.gameObject;
		}

		private Image CreateHealthBar()
		{
			var background = CreateImage("Health Background", new Color(0.45f, 0.05f, 0.05f, 1f), new Vector2(54f, 38f), new Vector2(-8f, -34f));
			var fillObject = new GameObject("Health Fill", typeof(RectTransform), typeof(Image));
			fillObject.transform.SetParent(background.transform, false);
			var rect = fillObject.GetComponent<RectTransform>();
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = Vector2.zero;
			rect.offsetMax = Vector2.zero;
			var fill = fillObject.GetComponent<Image>();
			fill.color = HealthColor;
			fill.raycastTarget = false;
			return fill;
		}

		private void CreateToggleRowIfNeeded()
		{
			if (CollectPickupsToggle != null &&
			    InvestigateToggle != null &&
			    RecruitToggle != null &&
			    CombatActivationToggle != null &&
			    WeaponPreferenceControl != null)
				return;

			Transform row = transform.Find("Toggle Row");
			if (row == null)
			{
				var rowObject = new GameObject("Toggle Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
				rowObject.transform.SetParent(transform, false);
				row = rowObject.transform;
				var rect = rowObject.GetComponent<RectTransform>();
				rect.anchorMin = new Vector2(0f, 0f);
				rect.anchorMax = new Vector2(1f, 0f);
				rect.pivot = new Vector2(0.5f, 0f);
				rect.offsetMin = new Vector2(54f, 6f);
				rect.offsetMax = new Vector2(-8f, 30f);
				var layout = rowObject.GetComponent<HorizontalLayoutGroup>();
				layout.spacing = 4f;
				layout.childAlignment = TextAnchor.MiddleLeft;
				layout.childControlWidth = false;
				layout.childControlHeight = true;
				layout.childForceExpandWidth = false;
				layout.childForceExpandHeight = true;
			}

			CollectPickupsToggle ??= CreateToggle(row, "P");
			InvestigateToggle ??= CreateToggle(row, "?");
			RecruitToggle ??= CreateToggle(row, "+");
			CombatActivationToggle ??= CreateToggle(row, "!");
			WeaponPreferenceControl ??= CreateWeaponPreferenceControl(row);
		}

		private void DisableLegacyCombatMovementToggle()
		{
			if (CombatMovementToggle != null)
				CombatMovementToggle.gameObject.SetActive(false);
		}

		private Toggle CreateToggle(Transform parent, string label)
		{
			var root = new GameObject(label, typeof(RectTransform), typeof(Toggle), typeof(Image), typeof(LayoutElement));
			root.transform.SetParent(parent, false);
			var rect = root.GetComponent<RectTransform>();
			rect.sizeDelta = new Vector2(24f, 22f);
			var layout = root.GetComponent<LayoutElement>();
			layout.preferredWidth = 24f;
			layout.preferredHeight = 22f;
			var background = root.GetComponent<Image>();
			background.color = new Color(0.12f, 0.15f, 0.18f, 0.9f);

			var checkObject = new GameObject("Check", typeof(RectTransform), typeof(Image));
			checkObject.transform.SetParent(root.transform, false);
			var checkRect = checkObject.GetComponent<RectTransform>();
			checkRect.anchorMin = new Vector2(0.18f, 0.18f);
			checkRect.anchorMax = new Vector2(0.82f, 0.82f);
			checkRect.offsetMin = Vector2.zero;
			checkRect.offsetMax = Vector2.zero;
			var check = checkObject.GetComponent<Image>();
			check.color = new Color(0.3f, 0.85f, 1f, 0.9f);

			var text = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
			text.transform.SetParent(root.transform, false);
			var textRect = text.GetComponent<RectTransform>();
			textRect.anchorMin = Vector2.zero;
			textRect.anchorMax = Vector2.one;
			textRect.offsetMin = Vector2.zero;
			textRect.offsetMax = Vector2.zero;
			var tmp = text.GetComponent<TextMeshProUGUI>();
			tmp.text = label;
			tmp.fontSize = 12;
			tmp.alignment = TextAlignmentOptions.Center;
			tmp.color = Color.white;
			tmp.raycastTarget = false;

			var toggle = root.GetComponent<Toggle>();
			toggle.targetGraphic = background;
			toggle.graphic = check;
			return toggle;
		}

		private SurvivorWeaponPreferenceControl CreateWeaponPreferenceControl(Transform parent)
		{
			var root = new GameObject(
				"Weapon Preference",
				typeof(RectTransform),
				typeof(Image),
				typeof(Button),
				typeof(LayoutElement),
				typeof(SurvivorWeaponPreferenceControl));
			root.transform.SetParent(parent, false);
			root.GetComponent<RectTransform>().sizeDelta = new Vector2(54f, 22f);
			var layout = root.GetComponent<LayoutElement>();
			layout.preferredWidth = 54f;
			layout.preferredHeight = 22f;
			return root.GetComponent<SurvivorWeaponPreferenceControl>();
		}
	}
}
