using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleFPS
{
	public sealed class GameMapIconController : MonoBehaviour
	{
		public RectTransform IconRoot;
		public GameMapAwarenessTracker AwarenessTracker;

		// When set (raid host "inspect" mode), this survivor is drawn enlarged instead of the networked active
		// character. Null for normal players, who keep the active-character highlight. Set by GameUI each frame.
		[HideInInspector]
		public Survivor InspectHighlightSurvivor;
		[Tooltip("Sprite drawn for survivor icons (both own team and detected enemies). The sprite is tinted with the team color, so it should be a light/white silhouette with transparent background and point upward so SetRotation maps yaw=0 to map-up. Leave empty to fall back to the legacy solid square.")]
		public Sprite SurvivorIconSprite;
		public Vector2 OwnIconSize = new Vector2(18f, 18f);
		public Vector2 EnemyIconSize = new Vector2(16f, 16f);
		public Vector2 PickupIconSize = new Vector2(12f, 12f);
		public Vector2 ZombieIconSize = new Vector2(12f, 12f);
		public Color FallbackOwnColor = Color.cyan;
		public Color FallbackEnemyColor = Color.red;
		public Color FallbackNeutralColor = Color.gray;
		public Color ZombieIconColor = new Color(0f, 0.28f, 0.08f, 1f);
		public Color ActiveWeaponPickupColor = Color.yellow;
		public Color InactiveWeaponPickupColor = new Color(1f, 0.9f, 0f, 0.35f);
		public Color ActiveHealthPickupColor = Color.green;
		public Color InactiveHealthPickupColor = new Color(0f, 1f, 0f, 0.35f);

		private readonly List<Survivor> _survivors = new(32);
		private readonly List<Survivor> _staleSurvivors = new(32);
		private readonly List<NetworkObject> _staleNetworkObjects = new(32);
		private readonly Dictionary<Survivor, GameMapIcon> _ownIcons = new();
		private readonly Dictionary<Survivor, GameMapIcon> _enemyIcons = new();
		private readonly Dictionary<NetworkObject, GameMapIcon> _pickupIcons = new();
		private readonly Dictionary<NetworkObject, GameMapIcon> _zombieIcons = new();
		private Sprite _pickupCircleSprite;

		public IEnumerable<GameMapIcon> OwnIcons => _ownIcons.Values;
		public IEnumerable<GameMapIcon> EnemyIcons => _enemyIcons.Values;

		public bool IsOwnSurvivorVisible(Survivor survivor)
		{
			return IsIconVisible(_ownIcons, survivor);
		}

		// Visible as either an own icon or an other-team (enemy/neutral) icon. Used by the defeated spectator,
		// who has no own survivors and selects any team's survivors from their revealed enemy icons.
		public bool IsSurvivorVisible(Survivor survivor)
		{
			return IsIconVisible(_ownIcons, survivor) || IsIconVisible(_enemyIcons, survivor);
		}

		private static bool IsIconVisible(Dictionary<Survivor, GameMapIcon> icons, Survivor survivor)
		{
			return survivor != null
				&& icons.TryGetValue(survivor, out var icon)
				&& icon != null
				&& icon.gameObject.activeSelf;
		}

		public void Tick(IGameMapView mapView, Gameplay gameplay, NetworkRunner runner, bool revealAll = false)
		{
			if (mapView == null || gameplay == null || runner == null)
				return;

			EnsureSetup(mapView);

			if (AwarenessTracker != null)
				AwarenessTracker.Tick(gameplay, runner, revealAll);

			UpdateOwnIcons(mapView, gameplay, runner);
			UpdateEnemyIcons(mapView, gameplay);
			UpdateZombieIcons(mapView);
			UpdatePickupIcons(mapView);
		}

		public GameMapIcon FindOwnIconAt(Vector2 screenPosition, Camera eventCamera)
		{
			return FindIconAt(_ownIcons, screenPosition, eventCamera);
		}

		// Finds a survivor icon under the cursor. When includeOtherTeams is set (defeated spectator), it also
		// searches the revealed enemy/neutral icons so any team's survivor can be picked.
		public GameMapIcon FindSelectableIconAt(Vector2 screenPosition, Camera eventCamera, bool includeOtherTeams)
		{
			GameMapIcon icon = FindIconAt(_ownIcons, screenPosition, eventCamera);
			if (icon == null && includeOtherTeams)
				icon = FindIconAt(_enemyIcons, screenPosition, eventCamera);

			return icon;
		}

		private static GameMapIcon FindIconAt(Dictionary<Survivor, GameMapIcon> icons, Vector2 screenPosition, Camera eventCamera)
		{
			foreach (var icon in icons.Values)
			{
				if (icon != null && icon.gameObject.activeSelf && icon.ContainsScreenPoint(screenPosition, eventCamera))
					return icon;
			}

			return null;
		}

		public void SetSelected(Survivor survivor, bool selected)
		{
			if (survivor == null)
				return;

			if (_ownIcons.TryGetValue(survivor, out var ownIcon) && ownIcon != null)
				ownIcon.SetSelected(selected);
			if (_enemyIcons.TryGetValue(survivor, out var enemyIcon) && enemyIcon != null)
				enemyIcon.SetSelected(selected);
		}

		public bool TryGetVisibleSurvivorIconRect(Survivor survivor, out RectTransform rectTransform)
		{
			rectTransform = null;
			if (survivor == null)
				return false;

			if (_ownIcons.TryGetValue(survivor, out var ownIcon) && ownIcon != null && ownIcon.gameObject.activeSelf)
			{
				rectTransform = ownIcon.RectTransform;
				return rectTransform != null;
			}

			if (_enemyIcons.TryGetValue(survivor, out var enemyIcon) && enemyIcon != null && enemyIcon.gameObject.activeSelf)
			{
				rectTransform = enemyIcon.RectTransform;
				return rectTransform != null;
			}

			return false;
		}

		private void EnsureSetup(IGameMapView mapView)
		{
			if (IconRoot == null)
			{
				var rootObject = new GameObject("IconRoot", typeof(RectTransform));
				IconRoot = rootObject.GetComponent<RectTransform>();
				IconRoot.SetParent(mapView.GetMapImage().rectTransform, false);
				IconRoot.anchorMin = Vector2.zero;
				IconRoot.anchorMax = Vector2.one;
				IconRoot.offsetMin = Vector2.zero;
				IconRoot.offsetMax = Vector2.zero;
			}

			if (AwarenessTracker == null)
				AwarenessTracker = GetComponent<GameMapAwarenessTracker>() ?? gameObject.AddComponent<GameMapAwarenessTracker>();
		}

		private void UpdateOwnIcons(IGameMapView mapView, Gameplay gameplay, NetworkRunner runner)
		{
			CollectSurvivors(_survivors);
			_staleSurvivors.Clear();
			_staleSurvivors.AddRange(_ownIcons.Keys);

			PlayerData localData = default;
			bool hasLocalData = gameplay.PlayerData.TryGet(runner.LocalPlayer, out localData);

			for (int i = 0; i < _survivors.Count; i++)
			{
				var survivor = _survivors[i];
				if (IsSpawnedSurvivor(survivor) == false || survivor.OwnerRef != runner.LocalPlayer || IsAlive(survivor) == false)
					continue;

				_staleSurvivors.Remove(survivor);

				if (_ownIcons.TryGetValue(survivor, out var icon) == false || icon == null)
				{
					icon = CreateIcon(survivor, GameMapIconKind.OwnSurvivor, GetTeamColor(gameplay, survivor, FallbackOwnColor), OwnIconSize);
					_ownIcons[survivor] = icon;
				}

				bool visible = mapView.IsWorldPositionVisibleOnMap(survivor.transform.position);
				icon.gameObject.SetActive(visible);
				if (visible == false)
					continue;

				UpdateIconTransform(mapView, icon, survivor.transform.position, survivor.transform.eulerAngles.y);
				bool highlighted = InspectHighlightSurvivor != null
					? survivor == InspectHighlightSurvivor
					: hasLocalData && localData.ActiveCharacterIndex == survivor.CharacterIndex;
				icon.SetActiveSurvivor(highlighted);
			}

			DestroyStaleIcons(_ownIcons, _staleSurvivors);
		}

		private void UpdateEnemyIcons(IGameMapView mapView, Gameplay gameplay)
		{
			_staleSurvivors.Clear();
			_staleSurvivors.AddRange(_enemyIcons.Keys);

			if (AwarenessTracker != null)
			{
				float now = Time.time;
				float forgetDelay = AwarenessTracker.EnemyIconForgetDelay;

				foreach (var pair in AwarenessTracker.EnemyMemory)
				{
					var memory = pair.Value;
					var survivor = memory.Survivor;
					if (IsSpawnedSurvivor(survivor) == false || IsAlive(survivor) == false)
						continue;

					_staleSurvivors.Remove(survivor);

					// Recompute appearance every tick. A revealed neutral can be recruited mid-session — its OwnerRef
					// changes, flipping it from the neutral icon to its new owner's team icon. Without this refresh the
					// icon stays frozen at whatever it was when first revealed, so a recruited survivor keeps showing
					// the neutral colour (only visible at all with the "reveal everything" debug / spectator view).
					GameMapIconKind kind = survivor.IsNeutral ? GameMapIconKind.NeutralSurvivor : GameMapIconKind.EnemySurvivor;
					Color fallbackColor = survivor.IsNeutral ? FallbackNeutralColor : FallbackEnemyColor;
					Color teamColor = GetTeamColor(gameplay, survivor, fallbackColor);

					if (_enemyIcons.TryGetValue(survivor, out var icon) == false || icon == null)
					{
						icon = CreateIcon(survivor, kind, teamColor, EnemyIconSize);
						_enemyIcons[survivor] = icon;
					}
					else
					{
						icon.SetSurvivorAppearance(kind, teamColor);
					}

					bool visible = mapView.IsWorldPositionVisibleOnMap(memory.LastKnownPosition);
					icon.gameObject.SetActive(visible);
					if (visible == false)
						continue;

					UpdateIconTransform(mapView, icon, memory.LastKnownPosition, memory.LastKnownRotationY);
					icon.SetOpacity(GameMapAwarenessTracker.ComputeOpacity(now, memory.LastSenseTime, forgetDelay));
					// Enlarge the inspected survivor for a defeated spectator, who watches other teams' icons.
					icon.SetActiveSurvivor(survivor == InspectHighlightSurvivor);
				}
			}

			DestroyStaleIcons(_enemyIcons, _staleSurvivors);
		}

		private void UpdatePickupIcons(IGameMapView mapView)
		{
			_staleNetworkObjects.Clear();
			_staleNetworkObjects.AddRange(_pickupIcons.Keys);

			if (AwarenessTracker != null)
			{
				float now = Time.time;
				float forgetDelay = AwarenessTracker.PickupIconForgetDelay;

				foreach (var pair in AwarenessTracker.PickupMemory)
				{
					var networkObject = pair.Key;
					var memory = pair.Value;
					if (networkObject == null || networkObject.IsValid == false)
						continue;

					_staleNetworkObjects.Remove(networkObject);

					if (_pickupIcons.TryGetValue(networkObject, out var icon) == false || icon == null)
					{
						icon = CreatePickupIcon(networkObject, GetPickupColor(memory), PickupIconSize);
						_pickupIcons[networkObject] = icon;
					}

					icon.SetColor(GetPickupColor(memory));

					bool visible = mapView.IsWorldPositionVisibleOnMap(memory.Position);
					icon.gameObject.SetActive(visible);
					if (visible == false)
						continue;

					icon.SetMapPosition(mapView.WorldToMapUI(memory.Position));
					icon.SetRotation(0f);
					icon.SetOpacity(GameMapAwarenessTracker.ComputeOpacity(now, memory.LastSenseTime, forgetDelay));
				}
			}

			DestroyStaleIcons(_pickupIcons, _staleNetworkObjects);
		}

		private void UpdateZombieIcons(IGameMapView mapView)
		{
			_staleNetworkObjects.Clear();
			_staleNetworkObjects.AddRange(_zombieIcons.Keys);

			if (AwarenessTracker != null)
			{
				float now = Time.time;
				float forgetDelay = AwarenessTracker.ZombieIconForgetDelay;

				foreach (var pair in AwarenessTracker.ZombieMemory)
				{
					var networkObject = pair.Key;
					var memory = pair.Value;
					if (networkObject == null || networkObject.IsValid == false)
						continue;

					_staleNetworkObjects.Remove(networkObject);

					if (_zombieIcons.TryGetValue(networkObject, out var icon) == false || icon == null)
					{
						icon = CreateCircleIcon(networkObject, GameMapIconKind.Zombie, ZombieIconColor, ZombieIconSize, "Zombie Icon");
						_zombieIcons[networkObject] = icon;
					}

					icon.SetColor(ZombieIconColor);

					bool visible = mapView.IsWorldPositionVisibleOnMap(memory.LastKnownPosition);
					icon.gameObject.SetActive(visible);
					if (visible == false)
						continue;

					icon.SetMapPosition(mapView.WorldToMapUI(memory.LastKnownPosition));
					icon.SetRotation(0f);
					icon.SetOpacity(GameMapAwarenessTracker.ComputeOpacity(now, memory.LastSenseTime, forgetDelay));
				}
			}

			DestroyStaleIcons(_zombieIcons, _staleNetworkObjects);
		}

		private GameMapIcon CreateIcon(Survivor survivor, GameMapIconKind kind, Color color, Vector2 size)
		{
			var iconObject = new GameObject($"{kind} Icon", typeof(RectTransform), typeof(Image), typeof(GameMapIcon));
			var rectTransform = iconObject.GetComponent<RectTransform>();
			rectTransform.SetParent(IconRoot, false);
			rectTransform.sizeDelta = size;
			rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
			rectTransform.anchorMax = new Vector2(0.5f, 0.5f);

			var image = iconObject.GetComponent<Image>();
			image.raycastTarget = false;
			if (SurvivorIconSprite != null)
				image.sprite = SurvivorIconSprite;

			var icon = iconObject.GetComponent<GameMapIcon>();
			icon.RectTransform = rectTransform;
			icon.Image = image;
			icon.Initialize(survivor, kind, color);
			return icon;
		}

		private GameMapIcon CreatePickupIcon(NetworkObject networkObject, Color color, Vector2 size)
		{
			return CreateCircleIcon(networkObject, GameMapIconKind.Pickup, color, size, "Pickup Icon");
		}

		private GameMapIcon CreateCircleIcon(NetworkObject networkObject, GameMapIconKind kind, Color color, Vector2 size, string name)
		{
			var iconObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(GameMapIcon));
			var rectTransform = iconObject.GetComponent<RectTransform>();
			rectTransform.SetParent(IconRoot, false);
			rectTransform.sizeDelta = size;
			rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
			rectTransform.anchorMax = new Vector2(0.5f, 0.5f);

			var image = iconObject.GetComponent<Image>();
			image.raycastTarget = false;
			image.sprite = GetPickupCircleSprite();

			var icon = iconObject.GetComponent<GameMapIcon>();
			icon.RectTransform = rectTransform;
			icon.Image = image;
			icon.Initialize(networkObject, kind, color);
			return icon;
		}

		private Color GetPickupColor(GameMapAwarenessTracker.PickupMapMemory memory)
		{
			if (memory.Type == EVisiblePickupType.Health)
				return memory.IsActive ? ActiveHealthPickupColor : InactiveHealthPickupColor;

			return memory.IsActive ? ActiveWeaponPickupColor : InactiveWeaponPickupColor;
		}

		private void UpdateIconTransform(IGameMapView mapView, GameMapIcon icon, Vector3 worldPosition, float yaw)
		{
			icon.SetMapPosition(mapView.WorldToMapUI(worldPosition));
			icon.SetRotation(yaw);
		}

		private static void DestroyStaleIcons(Dictionary<Survivor, GameMapIcon> icons, List<Survivor> stale)
		{
			for (int i = 0; i < stale.Count; i++)
			{
				var survivor = stale[i];
				if (icons.TryGetValue(survivor, out var icon) && icon != null)
					Destroy(icon.gameObject);

				icons.Remove(survivor);
			}
		}

		private static void DestroyStaleIcons(Dictionary<NetworkObject, GameMapIcon> icons, List<NetworkObject> stale)
		{
			for (int i = 0; i < stale.Count; i++)
			{
				var networkObject = stale[i];
				if (icons.TryGetValue(networkObject, out var icon) && icon != null)
					Destroy(icon.gameObject);

				icons.Remove(networkObject);
			}
		}

		private Sprite GetPickupCircleSprite()
		{
			if (_pickupCircleSprite != null)
				return _pickupCircleSprite;

			const int size = 32;
			var texture = new Texture2D(size, size, TextureFormat.ARGB32, false)
			{
				name = "Runtime Pickup Map Circle"
			};

			Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
			float radius = size * 0.45f;
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
			_pickupCircleSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
			_pickupCircleSprite.name = "Runtime Pickup Map Circle";
			return _pickupCircleSprite;
		}

		private static void CollectSurvivors(List<Survivor> results)
		{
			results.Clear();
			results.AddRange(FindObjectsOfType<Survivor>());
		}

		private static bool IsAlive(Survivor survivor)
		{
			return survivor.Health != null && survivor.Health.IsAlive;
		}

		private static bool IsSpawnedSurvivor(Survivor survivor)
		{
			return survivor != null && survivor.Object != null && survivor.Object.IsValid;
		}

		private static Color GetTeamColor(Gameplay gameplay, Survivor survivor, Color fallback)
		{
			if (gameplay == null || IsSpawnedSurvivor(survivor) == false)
				return fallback;

			if (gameplay.PlayerData.TryGet(survivor.OwnerRef, out var data) == false)
				return GetMaterialColor(gameplay.GetTeamColorMaterial(Gameplay.NeutralTeamColorIndex), fallback);

			Material material = gameplay.GetTeamColorMaterial(data.TeamColorIndex);
			return GetMaterialColor(material, fallback);
		}

		private static Color GetMaterialColor(Material material, Color fallback)
		{
			if (material == null)
				return fallback;
			if (material.HasProperty("_BaseColor"))
				return material.GetColor("_BaseColor");
			if (material.HasProperty("_Color"))
				return material.GetColor("_Color");

			return fallback;
		}
	}
}
