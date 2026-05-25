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
		public Vector2 OwnIconSize = new Vector2(18f, 18f);
		public Vector2 EnemyIconSize = new Vector2(16f, 16f);
		public Vector2 PickupIconSize = new Vector2(12f, 12f);
		public Vector2 ZombieIconSize = new Vector2(12f, 12f);
		public Color FallbackOwnColor = Color.cyan;
		public Color FallbackEnemyColor = Color.red;
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
			return survivor != null
				&& _ownIcons.TryGetValue(survivor, out var icon)
				&& icon != null
				&& icon.gameObject.activeSelf;
		}

		public void Tick(GameMapView mapView, Gameplay gameplay, NetworkRunner runner)
		{
			if (mapView == null || gameplay == null || runner == null)
				return;

			EnsureSetup(mapView);

			if (AwarenessTracker != null)
				AwarenessTracker.Tick(gameplay, runner);

			UpdateOwnIcons(mapView, gameplay, runner);
			UpdateEnemyIcons(mapView, gameplay);
			UpdateZombieIcons(mapView);
			UpdatePickupIcons(mapView);
		}

		public GameMapIcon FindOwnIconAt(Vector2 screenPosition, Camera eventCamera)
		{
			foreach (var icon in _ownIcons.Values)
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

			if (_ownIcons.TryGetValue(survivor, out var icon) && icon != null)
				icon.SetSelected(selected);
		}

		private void EnsureSetup(GameMapView mapView)
		{
			if (IconRoot == null)
			{
				var rootObject = new GameObject("IconRoot", typeof(RectTransform));
				IconRoot = rootObject.GetComponent<RectTransform>();
				IconRoot.SetParent(mapView.MapImage.rectTransform, false);
				IconRoot.anchorMin = Vector2.zero;
				IconRoot.anchorMax = Vector2.one;
				IconRoot.offsetMin = Vector2.zero;
				IconRoot.offsetMax = Vector2.zero;
			}

			if (AwarenessTracker == null)
				AwarenessTracker = GetComponent<GameMapAwarenessTracker>() ?? gameObject.AddComponent<GameMapAwarenessTracker>();
		}

		private void UpdateOwnIcons(GameMapView mapView, Gameplay gameplay, NetworkRunner runner)
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
				icon.SetActiveSurvivor(hasLocalData && localData.ActiveCharacterIndex == survivor.CharacterIndex);
			}

			DestroyStaleIcons(_ownIcons, _staleSurvivors);
		}

		private void UpdateEnemyIcons(GameMapView mapView, Gameplay gameplay)
		{
			_staleSurvivors.Clear();
			_staleSurvivors.AddRange(_enemyIcons.Keys);

			if (AwarenessTracker != null)
			{
				foreach (var pair in AwarenessTracker.EnemyMemory)
				{
					var memory = pair.Value;
					var survivor = memory.Survivor;
					if (IsSpawnedSurvivor(survivor) == false || IsAlive(survivor) == false)
						continue;

					_staleSurvivors.Remove(survivor);

					if (_enemyIcons.TryGetValue(survivor, out var icon) == false || icon == null)
					{
						icon = CreateIcon(survivor, GameMapIconKind.EnemySurvivor, GetTeamColor(gameplay, survivor, FallbackEnemyColor), EnemyIconSize);
						_enemyIcons[survivor] = icon;
					}

					bool visible = mapView.IsWorldPositionVisibleOnMap(memory.LastKnownPosition);
					icon.gameObject.SetActive(visible);
					if (visible == false)
						continue;

					UpdateIconTransform(mapView, icon, memory.LastKnownPosition, survivor.transform.eulerAngles.y);
				}
			}

			DestroyStaleIcons(_enemyIcons, _staleSurvivors);
		}

		private void UpdatePickupIcons(GameMapView mapView)
		{
			_staleNetworkObjects.Clear();
			_staleNetworkObjects.AddRange(_pickupIcons.Keys);

			if (AwarenessTracker != null)
			{
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
				}
			}

			DestroyStaleIcons(_pickupIcons, _staleNetworkObjects);
		}

		private void UpdateZombieIcons(GameMapView mapView)
		{
			_staleNetworkObjects.Clear();
			_staleNetworkObjects.AddRange(_zombieIcons.Keys);

			if (AwarenessTracker != null)
			{
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

		private void UpdateIconTransform(GameMapView mapView, GameMapIcon icon, Vector3 worldPosition, float yaw)
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
				return fallback;

			Material material = gameplay.GetTeamColorMaterial(data.TeamColorIndex);
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
