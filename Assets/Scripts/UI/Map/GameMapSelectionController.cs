using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SimpleFPS
{
	public sealed class GameMapSelectionController : MonoBehaviour
	{
		public GameMapIconController IconController;
		public RectTransform SelectionBox;
		public RectTransform AssignedAreaCircle;
		public RectTransform HomeBaseCircle;
		public RectTransform InputBlocker;
		public float DragThreshold = 8f;
		public float DoubleClickTime = 0.35f;
		public Color SelectionBoxColor = new Color(0.3f, 0.8f, 1f, 0.2f);
		public Color SelectionBoxBorderColor = new Color(0.3f, 0.8f, 1f, 0.8f);
		public float AssignedAreaMinRadius = 3f;
		public float AssignedAreaMaxRadius = 16f;
		public float AssignedAreaBorderThickness = 3f;
		public Color AssignedAreaFillColor = new Color(0.3f, 0.8f, 1f, 0.15f);
		public Color AssignedAreaBorderColor = new Color(0.3f, 0.8f, 1f, 0.9f);
		public Color HomeBaseFillColor = new Color(0.45f, 1f, 0.45f, 0.75f);
		public Color HomeBaseBorderColor = new Color(0.45f, 1f, 0.45f, 0.75f);

		private readonly HashSet<Survivor> _selected = new();
		private readonly List<Survivor> _selectionRemoval = new();
		private Vector2 _dragStart;
		private bool _dragging;
		private Vector3 _areaDragStartWorld;
		private float _currentAreaRadius;
		private bool _areaDragging;
		private bool _areaCircleVisible;
		private bool _editingHomeBase;
		private Survivor _lastClickedSurvivor;
		private float _lastClickTime;
		// Per-frame mode flags from the spectator controller: a defeated spectator can select any team's survivor
		// but cannot issue orders.
		private bool _canSelectAnyTeam;
		private bool _commandsAllowed = true;
		private bool _ignoreNextSelectionModifierRelease;
		private Image _assignedAreaFillImage;
		private Image _assignedAreaBorderImage;
		private Image _homeBaseFillImage;
		private Image _homeBaseBorderImage;
		private Sprite _circleSprite;
		private Sprite _ringSprite;

		public bool IsDraggingAssignedArea => _areaDragging;
		public IReadOnlyCollection<Survivor> SelectedSurvivors => _selected;
		public event Action SelectionChanged;
		public event Action<Survivor> SurvivorSelected;

		public bool IsSelected(Survivor survivor)
		{
			return survivor != null && _selected.Contains(survivor);
		}

		public void ToggleSelectionFromRoster(Survivor survivor, Gameplay gameplay, NetworkRunner runner)
		{
			if (survivor == null)
				return;

			if (IsSelectionModifierHeld())
				_ignoreNextSelectionModifierRelease = true;

			if (_selected.Contains(survivor) == false && IsShiftHeld() && _selected.Count > 0)
			{
				if (SelectRangeFromRoster(survivor, gameplay, runner))
					return;
			}

			if (_selected.Contains(survivor))
			{
				RemoveSelection(survivor);
				return;
			}

			ClearSelection(notify: false);
			if (AddSelection(survivor, gameplay, runner, requireMapVisibility: false, notify: false))
				NotifySelectionChanged();
		}

		public CharacterMask128 BuildSelectedCommandMask(Gameplay gameplay, NetworkRunner runner)
		{
			return BuildSelectedMask(gameplay, runner);
		}

		public bool IsCommandSelectable(Survivor survivor, Gameplay gameplay, NetworkRunner runner)
		{
			return IsSelectable(survivor, gameplay, runner, requireMapVisibility: false);
		}

		public void Tick(GameMapView mapView, Gameplay gameplay, NetworkRunner runner)
		{
			if (mapView == null || gameplay == null || runner == null || IconController == null)
				return;

			_canSelectAnyTeam = mapView.Spectator != null && mapView.Spectator.CanInspectAnyTeam;
			_commandsAllowed = mapView.Spectator == null || mapView.Spectator.CanIssueCommands;

			EnsureSelectionBox(mapView);
			EnsureAssignedAreaCircle(mapView);
			EnsureHomeBaseCircle(mapView);
			UpdateHomeBaseCircle(mapView, gameplay, runner);
			RemoveHiddenOrInvalidSelections(gameplay, runner);

			var mouse = Mouse.current;
			if (mouse == null)
				return;

			Vector2 mousePosition = mouse.position.ReadValue();
			Camera eventCamera = mapView.GetEventCamera();

			HandleKeyboardSelection(gameplay, runner);
			if (HandleKeyboardPossess(mapView, gameplay, runner))
				return;

			bool pointerBlocked = IsPointerBlocked(mousePosition, eventCamera);
			bool pointerOnMap = IsPointerOnMap(mapView, mousePosition, eventCamera);
			HandleRightMouseOrder(mapView, gameplay, runner, mousePosition, eventCamera, pointerBlocked == false && pointerOnMap);
			if (_areaDragging)
				return;

			if (mouse.leftButton.wasPressedThisFrame && pointerBlocked == false && pointerOnMap)
			{
				_dragStart = mousePosition;
				_dragging = true;
				SetSelectionBoxVisible(false);
			}

			if (_dragging && mouse.leftButton.isPressed)
			{
				float distance = Vector2.Distance(_dragStart, mousePosition);
				if (distance >= DragThreshold)
					UpdateSelectionBox(mapView, _dragStart, mousePosition);
			}

			if (_dragging && mouse.leftButton.wasReleasedThisFrame)
			{
				float distance = Vector2.Distance(_dragStart, mousePosition);
				SetSelectionBoxVisible(false);

				if (distance >= DragThreshold)
					SelectDragRect(gameplay, runner, mapView, _dragStart, mousePosition, eventCamera);
				else if (pointerBlocked == false && pointerOnMap)
					HandleClick(gameplay, runner, mousePosition, eventCamera);

				_dragging = false;
			}

			if (pointerBlocked || pointerOnMap == false)
			{
				_areaDragging = false;
				SetAssignedAreaCircleVisible(false);
				return;
			}

		}

		private void HandleClick(Gameplay gameplay, NetworkRunner runner, Vector2 screenPosition, Camera eventCamera)
		{
			GameMapIcon icon = IconController.FindSelectableIconAt(screenPosition, eventCamera, _canSelectAnyTeam);
			if (icon == null || IsSelectable(icon.Survivor, gameplay, runner) == false)
			{
				ClearSelection();
				return;
			}

			// Double-click select-all only makes sense for an own-team commander, not a spectator picking which
			// of many teams' survivors to watch.
			if (_canSelectAnyTeam == false && _lastClickedSurvivor == icon.Survivor && Time.unscaledTime - _lastClickTime <= DoubleClickTime)
			{
				SelectAllVisibleOwnIcons(gameplay, runner);
			}
			else
			{
				ClearSelection();
				AddSelection(icon.Survivor, gameplay, runner);
			}

			_lastClickedSurvivor = icon.Survivor;
			_lastClickTime = Time.unscaledTime;
		}

		private void SelectDragRect(Gameplay gameplay, NetworkRunner runner, GameMapView mapView, Vector2 start, Vector2 end, Camera eventCamera)
		{
			Rect screenRect = GetScreenRect(start, end);
			ClearSelection();

			foreach (var icon in IconController.OwnIcons)
			{
				if (icon == null || icon.Survivor == null)
					continue;
				if (icon.gameObject.activeSelf == false)
					continue;
				if (IsSelectable(icon.Survivor, gameplay, runner) == false)
					continue;

				Vector2 screenPosition = RectTransformUtility.WorldToScreenPoint(eventCamera, icon.RectTransform.position);
				if (screenRect.Contains(screenPosition) && IsPointerBlocked(screenPosition, eventCamera) == false)
					AddSelection(icon.Survivor, gameplay, runner);
			}
		}

		private void SelectAllVisibleOwnIcons(Gameplay gameplay, NetworkRunner runner)
		{
			ClearSelection();

			foreach (var icon in IconController.OwnIcons)
			{
				if (icon != null && icon.Survivor != null && icon.gameObject.activeSelf && IsSelectable(icon.Survivor, gameplay, runner))
					AddSelection(icon.Survivor, gameplay, runner);
			}
		}

		private void HandleRightMouseOrder(GameMapView mapView, Gameplay gameplay, NetworkRunner runner, Vector2 mousePosition, Camera eventCamera, bool canStartMapInput)
		{
			// Defeated spectators cannot issue orders (the server rejects them too); skip the whole interaction
			// so no order preview shows and no RPC is sent.
			if (_commandsAllowed == false)
				return;

			var mouse = Mouse.current;
			if (mouse == null)
				return;

			if (mouse.rightButton.wasPressedThisFrame)
			{
				_currentAreaRadius = 0f;
				_areaCircleVisible = false;
				_editingHomeBase = BuildSelectedMask(gameplay, runner).IsEmpty;
				_areaDragging = canStartMapInput && mapView.TryMapUIToWorld(mousePosition, out _areaDragStartWorld);
				SetAssignedAreaCircleVisible(false);
			}

			if (_areaDragging && mouse.rightButton.isPressed)
			{
				UpdateAssignedAreaPreview(mapView, gameplay, mousePosition);
			}

			if (_areaDragging && mouse.rightButton.wasReleasedThisFrame)
			{
				CharacterMask128 selectedMask = BuildSelectedMask(gameplay, runner);
				if (selectedMask.IsEmpty)
				{
					float radius = _areaCircleVisible ? _currentAreaRadius : GetAssignedAreaMinRadius(gameplay);
					gameplay.RequestSetHomeBase(_areaDragStartWorld, radius);
				}
				else if (_areaCircleVisible)
				{
					gameplay.RequestMapAssignedAreaOrder(selectedMask, _areaDragStartWorld, _currentAreaRadius);
				}
				else
				{
					IssuePointOrder(mapView, gameplay, runner, mousePosition, eventCamera);
				}

				_areaDragging = false;
				_areaCircleVisible = false;
				_editingHomeBase = false;
				SetAssignedAreaCircleVisible(false);
			}
		}

		private void IssuePointOrder(GameMapView mapView, Gameplay gameplay, NetworkRunner runner, Vector2 screenPosition, Camera eventCamera)
		{
			CharacterMask128 selectedMask = BuildSelectedMask(gameplay, runner);
			if (selectedMask.IsEmpty)
				return;

			GameMapIcon targetIcon = IconController.FindOwnIconAt(screenPosition, eventCamera);
			if (targetIcon != null && targetIcon.Survivor != null)
			{
				gameplay.RequestMapFollowOrder(selectedMask, targetIcon.Survivor.CharacterIndex);
				return;
			}

			if (mapView.TryMapUIToWorld(screenPosition, out Vector3 destination))
				gameplay.RequestMapMoveOrder(selectedMask, destination);
		}

		private void UpdateAssignedAreaPreview(GameMapView mapView, Gameplay gameplay, Vector2 mousePosition)
		{
			if (mapView.TryMapUIToWorld(mousePosition, out Vector3 currentWorld) == false)
			{
				_areaCircleVisible = false;
				SetAssignedAreaCircleVisible(false);
				return;
			}

			Vector3 offset = currentWorld - _areaDragStartWorld;
			offset.y = 0f;
			float unclampedRadius = offset.magnitude;
			float minRadius = GetAssignedAreaMinRadius(gameplay);
			float maxRadius = Mathf.Max(minRadius, GetAssignedAreaMaxRadius(gameplay));
			_currentAreaRadius = Mathf.Min(unclampedRadius, maxRadius);

			if (_currentAreaRadius < minRadius)
			{
				if (_editingHomeBase == false)
				{
					_areaCircleVisible = false;
					SetAssignedAreaCircleVisible(false);
					return;
				}

				_currentAreaRadius = minRadius;
			}

			Vector3 direction = offset.sqrMagnitude > 0.001f ? offset.normalized : Vector3.right;
			Vector2 localCenter = mapView.WorldToMapUI(_areaDragStartWorld);
			Vector2 localEdge = mapView.WorldToMapUI(_areaDragStartWorld + direction * _currentAreaRadius);
			float uiRadius = Vector2.Distance(localCenter, localEdge);

			AssignedAreaCircle.anchoredPosition = localCenter;
			AssignedAreaCircle.sizeDelta = new Vector2(uiRadius * 2f, uiRadius * 2f);
			UpdateAssignedAreaCircleImages();
			_areaCircleVisible = true;
			SetAssignedAreaCircleVisible(true);
		}

		private void UpdateHomeBaseCircle(GameMapView mapView, Gameplay gameplay, NetworkRunner runner)
		{
			if (HomeBaseCircle == null ||
			    gameplay == null ||
			    runner == null ||
			    gameplay.PlayerData.TryGet(runner.LocalPlayer, out PlayerData data) == false ||
			    data.IsAlive == false ||
			    gameplay.TryGetHomeBase(runner.LocalPlayer, out Vector3 center, out float radius) == false)
			{
				if (HomeBaseCircle != null)
					HomeBaseCircle.gameObject.SetActive(false);
				return;
			}

			Vector2 localCenter = mapView.WorldToMapUI(center);
			Vector2 localEdge = mapView.WorldToMapUI(center + Vector3.right * radius);
			float uiRadius = Vector2.Distance(localCenter, localEdge);
			HomeBaseCircle.anchoredPosition = localCenter;
			HomeBaseCircle.sizeDelta = new Vector2(uiRadius * 2f, uiRadius * 2f);
			HomeBaseCircle.gameObject.SetActive(true);
		}

		private float GetAssignedAreaMinRadius(Gameplay gameplay)
		{
			if (gameplay != null && gameplay.AICommandSettings != null)
				return Mathf.Max(0f, gameplay.AICommandSettings.AssignedAreaMinRadius);

			return Mathf.Max(0f, AssignedAreaMinRadius);
		}

		private float GetAssignedAreaMaxRadius(Gameplay gameplay)
		{
			if (gameplay != null && gameplay.AICommandSettings != null)
				return Mathf.Max(0f, gameplay.AICommandSettings.AssignedAreaMaxRadius);

			return Mathf.Max(0f, AssignedAreaMaxRadius);
		}

		private CharacterMask128 BuildSelectedMask(Gameplay gameplay, NetworkRunner runner)
		{
			if (gameplay.PlayerData.TryGet(runner.LocalPlayer, out var data) == false)
				return default;

			var mask = new CharacterMask128();
			foreach (var survivor in _selected)
			{
				if (IsCommandSelectable(survivor, gameplay, runner) == false)
					continue;

				mask.Set(survivor.CharacterIndex, true);
			}

			return mask;
		}

		private bool ClearSelection(bool notify = true)
		{
			if (_selected.Count == 0)
				return false;

			foreach (var survivor in _selected)
				IconController.SetSelected(survivor, false);

			_selected.Clear();
			if (notify)
				NotifySelectionChanged();

			return true;
		}

		private void RemoveHiddenOrInvalidSelections(Gameplay gameplay, NetworkRunner runner)
		{
			if (_selected.Count == 0)
				return;

			_selectionRemoval.Clear();
			foreach (var survivor in _selected)
			{
				if (IsCommandSelectable(survivor, gameplay, runner) == false)
				{
					_selectionRemoval.Add(survivor);
				}
			}

			for (int i = 0; i < _selectionRemoval.Count; i++)
			{
				_selected.Remove(_selectionRemoval[i]);
				IconController.SetSelected(_selectionRemoval[i], false);
			}

			if (_selectionRemoval.Count > 0)
				NotifySelectionChanged();

			_selectionRemoval.Clear();
		}

		private bool AddSelection(Survivor survivor, Gameplay gameplay, NetworkRunner runner, bool requireMapVisibility = true, bool notify = true)
		{
			if (IsSelectable(survivor, gameplay, runner, requireMapVisibility) == false || _selected.Add(survivor) == false)
				return false;

			IconController.SetSelected(survivor, true);
			SurvivorSelected?.Invoke(survivor);
			if (notify)
				NotifySelectionChanged();

			return true;
		}

		private bool SelectRangeFromRoster(Survivor target, Gameplay gameplay, NetworkRunner runner)
		{
			if (target == null || IsSelectable(target, gameplay, runner, requireMapVisibility: false) == false)
				return false;

			Survivor closest = null;
			int closestDistance = int.MaxValue;
			foreach (var selected in _selected)
			{
				if (selected == null || selected.OwnerRef != target.OwnerRef)
					continue;
				if (IsSelectable(selected, gameplay, runner, requireMapVisibility: false) == false)
					continue;

				int distance = Mathf.Abs(selected.CharacterIndex - target.CharacterIndex);
				if (distance < closestDistance)
				{
					closestDistance = distance;
					closest = selected;
				}
			}

			if (closest == null)
				return false;

			int minIndex = Mathf.Min(closest.CharacterIndex, target.CharacterIndex);
			int maxIndex = Mathf.Max(closest.CharacterIndex, target.CharacterIndex);
			bool changed = false;
			for (int index = minIndex; index <= maxIndex; index++)
			{
				var survivor = gameplay.GetSurvivor(target.OwnerRef, index);
				if (AddSelection(survivor, gameplay, runner, requireMapVisibility: false, notify: false))
					changed = true;
			}

			if (changed)
				NotifySelectionChanged();

			return changed;
		}

		private bool RemoveSelection(Survivor survivor, bool notify = true)
		{
			if (survivor == null || _selected.Remove(survivor) == false)
				return false;

			IconController.SetSelected(survivor, false);
			if (notify)
				NotifySelectionChanged();

			return true;
		}

		private void HandleKeyboardSelection(Gameplay gameplay, NetworkRunner runner)
		{
			var keyboard = Keyboard.current;
			if (keyboard == null)
				return;

			bool nextReleased = keyboard.leftShiftKey.wasReleasedThisFrame || keyboard.rightShiftKey.wasReleasedThisFrame;
			bool previousReleased = keyboard.leftCtrlKey.wasReleasedThisFrame || keyboard.rightCtrlKey.wasReleasedThisFrame;
			if (nextReleased || previousReleased)
			{
				if (_ignoreNextSelectionModifierRelease)
				{
					_ignoreNextSelectionModifierRelease = false;
					return;
				}
			}

			if (_selected.Count > 1)
				return;

			if (nextReleased == previousReleased)
				return;

			if (gameplay.PlayerData.TryGet(runner.LocalPlayer, out var data) == false)
				return;

			int startIndex = data.ActiveCharacterIndex;
			if (_selected.Count == 1)
			{
				foreach (var survivor in _selected)
				{
					if (survivor != null)
						startIndex = survivor.CharacterIndex;
					break;
				}
			}

			Survivor next = FindNextSelectableSurvivor(gameplay, runner, data, startIndex, nextReleased ? 1 : -1);
			ClearSelection();

			if (next != null)
				AddSelection(next, gameplay, runner);
		}

		private static bool IsShiftHeld()
		{
			var keyboard = Keyboard.current;
			return keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
		}

		private static bool IsSelectionModifierHeld()
		{
			var keyboard = Keyboard.current;
			return keyboard != null &&
			       (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed ||
			        keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed);
		}

		private bool HandleKeyboardPossess(GameMapView mapView, Gameplay gameplay, NetworkRunner runner)
		{
			if (_selected.Count != 1)
				return false;

			var keyboard = Keyboard.current;
			if (keyboard == null || keyboard.spaceKey.wasPressedThisFrame == false)
				return false;

			Survivor target = null;
			foreach (var survivor in _selected)
			{
				target = survivor;
				break;
			}

			if (target == null || IsSelectable(target, gameplay, runner) == false)
				return false;

			// Spectators (raid host or defeated) inspect instead of possessing: switch the camera's inspect target
			// and leave the map open. Normal players possess the survivor and close the map.
			if (mapView.Spectator != null && mapView.Spectator.IsActive)
			{
				mapView.Spectator.SetInspectTarget(target);
				return true;
			}

			gameplay.RequestSwitchActiveCharacter(target.CharacterIndex);
			mapView.CloseMap();
			return true;
		}

		private Survivor FindNextSelectableSurvivor(Gameplay gameplay, NetworkRunner runner, PlayerData data, int startIndex, int direction)
		{
			int count = Mathf.Max(0, data.CharacterCount);
			if (count <= 0)
				return null;

			for (int step = 1; step <= count; step++)
			{
				int index = ((startIndex + direction * step) % count + count) % count;
				var survivor = gameplay.GetSurvivor(runner.LocalPlayer, index);
				if (IsSelectable(survivor, gameplay, runner))
					return survivor;
			}

			return null;
		}

		private bool IsSelectable(Survivor survivor, Gameplay gameplay, NetworkRunner runner)
		{
			return IsSelectable(survivor, gameplay, runner, requireMapVisibility: true);
		}

		private bool IsSelectable(Survivor survivor, Gameplay gameplay, NetworkRunner runner, bool requireMapVisibility)
		{
			if (survivor == null || gameplay == null || runner == null)
				return false;
			if (survivor.Object == null || survivor.Object.IsValid == false)
				return false;
			if (survivor.Health == null || survivor.Health.IsAlive == false)
				return false;

			// Defeated spectator: any team's player survivor that is visible on the map (never neutrals).
			if (_canSelectAnyTeam)
				return CharacterFactionUtility.IsPlayerOwnedSurvivor(survivor) && (requireMapVisibility == false || IconController.IsSurvivorVisible(survivor));

			if (survivor.OwnerRef != runner.LocalPlayer)
				return false;
			if (requireMapVisibility && IconController.IsOwnSurvivorVisible(survivor) == false)
				return false;
			if (gameplay.PlayerData.TryGet(runner.LocalPlayer, out var data) == false)
				return false;

			return survivor.CharacterIndex != data.ActiveCharacterIndex;
		}

		private void NotifySelectionChanged()
		{
			SelectionChanged?.Invoke();
		}

		public bool IsPointerBlocked(Vector2 screenPosition, Camera eventCamera)
		{
			return InputBlocker != null &&
			       InputBlocker.gameObject.activeInHierarchy &&
			       RectTransformUtility.RectangleContainsScreenPoint(InputBlocker, screenPosition, eventCamera);
		}

		private static bool IsPointerOnMap(GameMapView mapView, Vector2 screenPosition, Camera eventCamera)
		{
			return mapView != null &&
			       mapView.MapImage != null &&
			       RectTransformUtility.RectangleContainsScreenPoint(mapView.MapImage.rectTransform, screenPosition, eventCamera);
		}

		private void EnsureSelectionBox(GameMapView mapView)
		{
			if (SelectionBox != null)
				return;

			var boxObject = new GameObject("SelectionBox", typeof(RectTransform), typeof(Image));
			SelectionBox = boxObject.GetComponent<RectTransform>();
			SelectionBox.SetParent(mapView.MapImage.rectTransform, false);
			SelectionBox.anchorMin = new Vector2(0.5f, 0.5f);
			SelectionBox.anchorMax = new Vector2(0.5f, 0.5f);

			var image = boxObject.GetComponent<Image>();
			image.color = SelectionBoxColor;
			image.raycastTarget = false;
			SetSelectionBoxVisible(false);
		}

		private void EnsureAssignedAreaCircle(GameMapView mapView)
		{
			if (mapView == null || mapView.MapImage == null)
				return;

			bool created = false;
			if (AssignedAreaCircle == null)
			{
				var circleObject = new GameObject("AssignedAreaCircle", typeof(RectTransform));
				AssignedAreaCircle = circleObject.GetComponent<RectTransform>();
				created = true;
			}

			if (AssignedAreaCircle.parent != mapView.MapImage.rectTransform)
				AssignedAreaCircle.SetParent(mapView.MapImage.rectTransform, false);

			AssignedAreaCircle.anchorMin = new Vector2(0.5f, 0.5f);
			AssignedAreaCircle.anchorMax = new Vector2(0.5f, 0.5f);
			AssignedAreaCircle.pivot = new Vector2(0.5f, 0.5f);

			EnsureAssignedAreaCircleImage(ref _assignedAreaBorderImage, "Border", 0f);
			EnsureAssignedAreaCircleImage(ref _assignedAreaFillImage, "Fill", Mathf.Max(0f, AssignedAreaBorderThickness));
			UpdateAssignedAreaCircleImages();

			if (created)
				SetAssignedAreaCircleVisible(false);
		}

		private void EnsureHomeBaseCircle(GameMapView mapView)
		{
			if (mapView == null || mapView.MapImage == null)
				return;

			if (HomeBaseCircle == null)
			{
				var circleObject = new GameObject("HomeBaseCircle", typeof(RectTransform));
				HomeBaseCircle = circleObject.GetComponent<RectTransform>();
			}

			if (HomeBaseCircle.parent != mapView.MapImage.rectTransform)
				HomeBaseCircle.SetParent(mapView.MapImage.rectTransform, false);

			HomeBaseCircle.anchorMin = new Vector2(0.5f, 0.5f);
			HomeBaseCircle.anchorMax = new Vector2(0.5f, 0.5f);
			HomeBaseCircle.pivot = new Vector2(0.5f, 0.5f);
			EnsureCircleImage(HomeBaseCircle, ref _homeBaseBorderImage, "Border", 0f, HomeBaseBorderColor);
			EnsureCircleImage(
				HomeBaseCircle,
				ref _homeBaseFillImage,
				"Fill",
				Mathf.Max(0f, AssignedAreaBorderThickness),
				HomeBaseFillColor);
		}

		private void EnsureAssignedAreaCircleImage(ref Image image, string name, float inset)
		{
			EnsureCircleImage(
				AssignedAreaCircle,
				ref image,
				name,
				inset,
				name == "Border" ? AssignedAreaBorderColor : AssignedAreaFillColor);
		}

		private void EnsureCircleImage(RectTransform circle, ref Image image, string name, float inset, Color color)
		{
			if (image == null)
			{
				Transform child = circle.Find(name);
				if (child != null)
					image = child.GetComponent<Image>();
			}

			if (image == null)
			{
				var imageObject = new GameObject(name, typeof(RectTransform), typeof(Image));
				imageObject.transform.SetParent(circle, false);
				image = imageObject.GetComponent<Image>();
			}

			var rectTransform = image.rectTransform;
			rectTransform.anchorMin = Vector2.zero;
			rectTransform.anchorMax = Vector2.one;
			rectTransform.offsetMin = new Vector2(inset, inset);
			rectTransform.offsetMax = new Vector2(-inset, -inset);

			image.sprite = name == "Border" ? GetRingSprite() : GetCircleSprite();
			image.type = Image.Type.Simple;
			image.color = color;
			image.raycastTarget = false;
		}

		private void UpdateAssignedAreaCircleImages()
		{
			if (_assignedAreaBorderImage != null)
				_assignedAreaBorderImage.color = _editingHomeBase ? HomeBaseBorderColor : AssignedAreaBorderColor;
			if (_assignedAreaFillImage != null)
				_assignedAreaFillImage.color = _editingHomeBase ? HomeBaseFillColor : AssignedAreaFillColor;
		}

		private Sprite GetCircleSprite()
		{
			if (_circleSprite != null)
				return _circleSprite;

			const int size = 64;
			var texture = new Texture2D(size, size, TextureFormat.ARGB32, false)
			{
				name = "Runtime Assigned Area Circle"
			};

			Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
			float radius = size * 0.48f;
			float radiusSqr = radius * radius;
			var pixels = new Color32[size * size];

			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float distanceSqr = (new Vector2(x, y) - center).sqrMagnitude;
					pixels[y * size + x] = distanceSqr <= radiusSqr ? Color.white : new Color(1f, 1f, 1f, 0f);
				}
			}

			texture.SetPixels32(pixels);
			texture.Apply(false, true);
			_circleSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
			_circleSprite.name = "Runtime Assigned Area Circle";
			return _circleSprite;
		}

		private Sprite GetRingSprite()
		{
			if (_ringSprite != null)
				return _ringSprite;

			const int size = 64;
			var texture = new Texture2D(size, size, TextureFormat.ARGB32, false)
			{
				name = "Runtime Assigned Area Ring"
			};

			Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
			float outerRadius = size * 0.48f;
			float innerRadius = size * 0.41f;
			float outerRadiusSqr = outerRadius * outerRadius;
			float innerRadiusSqr = innerRadius * innerRadius;
			var pixels = new Color32[size * size];

			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float distanceSqr = (new Vector2(x, y) - center).sqrMagnitude;
					pixels[y * size + x] = distanceSqr <= outerRadiusSqr && distanceSqr >= innerRadiusSqr
						? Color.white
						: new Color(1f, 1f, 1f, 0f);
				}
			}

			texture.SetPixels32(pixels);
			texture.Apply(false, true);
			_ringSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
			_ringSprite.name = "Runtime Assigned Area Ring";
			return _ringSprite;
		}

		private void UpdateSelectionBox(GameMapView mapView, Vector2 start, Vector2 end)
		{
			if (RectTransformUtility.ScreenPointToLocalPointInRectangle(mapView.MapImage.rectTransform, start, mapView.GetEventCamera(), out Vector2 localStart) == false)
				return;
			if (RectTransformUtility.ScreenPointToLocalPointInRectangle(mapView.MapImage.rectTransform, end, mapView.GetEventCamera(), out Vector2 localEnd) == false)
				return;

			Vector2 center = (localStart + localEnd) * 0.5f;
			Vector2 size = new Vector2(Mathf.Abs(localEnd.x - localStart.x), Mathf.Abs(localEnd.y - localStart.y));

			SelectionBox.anchoredPosition = center;
			SelectionBox.sizeDelta = size;
			SetSelectionBoxVisible(true);
		}

		private void SetSelectionBoxVisible(bool visible)
		{
			if (SelectionBox != null)
				SelectionBox.gameObject.SetActive(visible);
		}

		private void SetAssignedAreaCircleVisible(bool visible)
		{
			if (AssignedAreaCircle != null)
			{
				if (visible)
					AssignedAreaCircle.SetAsLastSibling();

				AssignedAreaCircle.gameObject.SetActive(visible);
			}
		}

		private static Rect GetScreenRect(Vector2 a, Vector2 b)
		{
			Vector2 min = Vector2.Min(a, b);
			Vector2 max = Vector2.Max(a, b);
			return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
		}
	}
}
