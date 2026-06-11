using UnityEngine;

namespace SimpleFPS
{
	public sealed class GameMapCameraController : MonoBehaviour
	{
		[Header("Setup")]
		public Camera MapCamera;
		public RoadGridGenerator RoadGenerator;
		public Transform FallbackBoundsRoot;
		public LayerMask GroundRaycastMask = ~0;

		[Header("View")]
		public float MinOrthographicSize = 25f;
		public float MaxOrthographicSize = 200f;
		public float ZoomSpeed = 20f;
		public float PanSpeed = 80f;
		public float CameraHeight = 300f;

		private Bounds _bounds;
		private bool _hasBounds;

		public Camera Camera => MapCamera;

		private void Awake()
		{
			if (MapCamera == null)
				MapCamera = GetComponent<Camera>();

			if (MapCamera != null)
				MapCamera.enabled = false;
		}

		public void SetMapActive(bool active)
		{
			EnsureCamera();

			if (MapCamera != null)
				MapCamera.enabled = active;
		}

		public void SetTargetTexture(RenderTexture renderTexture)
		{
			EnsureCamera();

			if (MapCamera != null)
				MapCamera.targetTexture = renderTexture;
		}

		public void OpenAt(Vector3 worldPosition)
		{
			EnsureCamera();
			RefreshBounds();

			if (MapCamera == null)
				return;

			MapCamera.orthographic = true;
			MapCamera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
			MapCamera.orthographicSize = GetInitialOrthographicSize();

			Vector3 center = _hasBounds ? ClampToBounds(worldPosition) : worldPosition;
			SetCameraCenter(center);
			ClampCameraToBounds();
		}

		public void Tick(float deltaTime, Vector2 panInput, float zoomInput)
		{
			EnsureCamera();

			if (MapCamera == null)
				return;

			if (Mathf.Abs(zoomInput) > 0.001f)
			{
				float size = MapCamera.orthographicSize - zoomInput * ZoomSpeed;
				MapCamera.orthographicSize = Mathf.Clamp(size, MinOrthographicSize, MaxOrthographicSize);
				ClampCameraToBounds();
			}

			if (panInput.sqrMagnitude > 0.001f)
			{
				float zoomRatio = MaxOrthographicSize > 0f ? MapCamera.orthographicSize / MaxOrthographicSize : 1f;
				Vector3 movement = new Vector3(panInput.x, 0f, panInput.y) * (PanSpeed * Mathf.Max(0.25f, zoomRatio) * deltaTime);
				SetCameraCenter(GetCameraCenter() + movement);
				ClampCameraToBounds();
			}
		}

		public bool TryMapViewportToWorld(Vector2 viewportPosition, out Vector3 worldPosition)
		{
			worldPosition = default;
			EnsureCamera();

			if (MapCamera == null)
				return false;

			Ray ray = MapCamera.ViewportPointToRay(new Vector3(viewportPosition.x, viewportPosition.y, 0f));
			if (Physics.Raycast(ray, out RaycastHit hit, CameraHeight * 2f, GroundRaycastMask, QueryTriggerInteraction.Ignore))
			{
				worldPosition = hit.point;
				return true;
			}

			var plane = new Plane(Vector3.up, Vector3.zero);
			if (plane.Raycast(ray, out float enter) == false)
				return false;

			worldPosition = ray.GetPoint(enter);
			return true;
		}

		public Vector2 WorldToMapViewport(Vector3 worldPosition)
		{
			EnsureCamera();

			if (MapCamera == null)
				return default;

			Vector3 viewport = MapCamera.WorldToViewportPoint(worldPosition);
			return new Vector2(viewport.x, viewport.y);
		}

		private void RefreshBounds()
		{
			if (RoadGenerator == null)
				RoadGenerator = FindObjectOfType<RoadGridGenerator>();

			if (RoadGenerator != null)
			{
				float width = Mathf.Max(1, RoadGenerator.Width) * RoadGenerator.TileSize;
				float height = Mathf.Max(1, RoadGenerator.Height) * RoadGenerator.TileSize;
				Vector3 center = RoadGenerator.transform.position + new Vector3((width - RoadGenerator.TileSize) * 0.5f, 0f, (height - RoadGenerator.TileSize) * 0.5f);
				_bounds = new Bounds(center, new Vector3(width, 1f, height));
				_hasBounds = true;
				return;
			}

			if (FallbackBoundsRoot != null && TryGetRendererBounds(FallbackBoundsRoot, out Bounds rendererBounds))
			{
				_bounds = rendererBounds;
				_hasBounds = true;
				return;
			}

			_hasBounds = false;
		}

		private bool TryGetRendererBounds(Transform root, out Bounds bounds)
		{
			var renderers = root.GetComponentsInChildren<Renderer>();
			bounds = default;

			if (renderers.Length == 0)
				return false;

			bounds = renderers[0].bounds;
			for (int i = 1; i < renderers.Length; i++)
				bounds.Encapsulate(renderers[i].bounds);

			return true;
		}

		private float GetInitialOrthographicSize()
		{
			if (_hasBounds == false)
				return Mathf.Clamp(MaxOrthographicSize, MinOrthographicSize, MaxOrthographicSize);

			float size = Mathf.Max(_bounds.extents.z, _bounds.extents.x / Mathf.Max(0.1f, MapCamera.aspect));
			MaxOrthographicSize = Mathf.Max(MaxOrthographicSize, size);
			return Mathf.Clamp(size, MinOrthographicSize, MaxOrthographicSize);
		}

		private void SetCameraCenter(Vector3 center)
		{
			MapCamera.transform.position = new Vector3(center.x, CameraHeight, center.z);
		}

		private Vector3 GetCameraCenter()
		{
			Vector3 position = MapCamera.transform.position;
			return new Vector3(position.x, 0f, position.z);
		}

		private Vector3 ClampToBounds(Vector3 position)
		{
			if (_hasBounds == false)
				return position;

			return new Vector3(
				Mathf.Clamp(position.x, _bounds.min.x, _bounds.max.x),
				position.y,
				Mathf.Clamp(position.z, _bounds.min.z, _bounds.max.z));
		}

		private void ClampCameraToBounds()
		{
			if (_hasBounds == false || MapCamera == null)
				return;

			float verticalExtent = MapCamera.orthographicSize;
			float horizontalExtent = verticalExtent * MapCamera.aspect;

			float minX = _bounds.min.x + horizontalExtent;
			float maxX = _bounds.max.x - horizontalExtent;
			float minZ = _bounds.min.z + verticalExtent;
			float maxZ = _bounds.max.z - verticalExtent;

			Vector3 center = GetCameraCenter();
			center.x = minX <= maxX ? Mathf.Clamp(center.x, minX, maxX) : _bounds.center.x;
			center.z = minZ <= maxZ ? Mathf.Clamp(center.z, minZ, maxZ) : _bounds.center.z;
			SetCameraCenter(center);
		}

		private void EnsureCamera()
		{
			if (MapCamera == null)
				MapCamera = GetComponent<Camera>();
		}
	}
}
