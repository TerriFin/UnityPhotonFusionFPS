using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class ZombieClimbableSurface : MonoBehaviour
	{
		public ZombieClimbSurfaceUsage Usage = ZombieClimbSurfaceUsage.Rescue;
		public bool UseChildColliders = true;
		public bool IncludeTriggerColliders;
		public bool UseRenderersWhenNoColliders = true;
		[Tooltip("How far onto the landing side the mantle ends. Lower this if zombies hoist too far inward.")]
		public float LandingInset = 0.75f;
		public float ShortcutMinPathSavings = 8f;
		public float MinimumHeight = 0.5f;

		[Header("Manual Bounds")]
		public bool UseManualLocalBounds;
		public Vector3 ManualLocalCenter = Vector3.zero;
		public Vector3 ManualLocalSize = Vector3.one;

		private readonly List<ZombieClimbSurface> _surfaces = new();

		private void OnEnable()
		{
			RefreshRegistration();
		}

		private void Start()
		{
			RefreshRegistration();
		}

		private void OnDisable()
		{
			ZombieClimbSurfaces.Unregister(this);
		}

		[ContextMenu("Refresh Zombie Climb Surfaces")]
		public void RefreshRegistration()
		{
			_surfaces.Clear();

			if (Usage == ZombieClimbSurfaceUsage.None)
			{
				ZombieClimbSurfaces.Unregister(this);
				return;
			}

			if (TryGetWorldBounds(out Bounds bounds) == false)
			{
				ZombieClimbSurfaces.Unregister(this);
				return;
			}

			float height = bounds.size.y;
			if (height < Mathf.Max(0.05f, MinimumHeight))
			{
				ZombieClimbSurfaces.Unregister(this);
				return;
			}

			AddBoundsSide(bounds, Vector3.right, Vector3.back, bounds.extents.z, bounds.max.x);
			AddBoundsSide(bounds, Vector3.left, Vector3.forward, bounds.extents.z, bounds.min.x);
			AddBoundsSide(bounds, Vector3.forward, Vector3.right, bounds.extents.x, bounds.max.z);
			AddBoundsSide(bounds, Vector3.back, Vector3.left, bounds.extents.x, bounds.min.z);

			ZombieClimbSurfaces.Register(this, _surfaces);
		}

		private void AddBoundsSide(Bounds bounds, Vector3 outward, Vector3 axis, float halfLength, float planeCoordinate)
		{
			if (halfLength <= 0.05f)
				return;

			Vector3 center = bounds.center;
			if (Mathf.Abs(outward.x) > 0.5f)
				center.x = planeCoordinate;
			else
				center.z = planeCoordinate;

			_surfaces.Add(new ZombieClimbSurface(
				Usage,
				center,
				axis,
				-outward,
				halfLength,
				bounds.min.y,
				bounds.max.y,
				LandingInset,
				ShortcutMinPathSavings,
				this));
		}

		private bool TryGetWorldBounds(out Bounds bounds)
		{
			if (UseManualLocalBounds)
			{
				Vector3 worldCenter = transform.TransformPoint(ManualLocalCenter);
				Vector3 worldSize = Vector3.Scale(ManualLocalSize, Abs(transform.lossyScale));
				bounds = new Bounds(worldCenter, worldSize);
				return bounds.size.sqrMagnitude > 0.001f;
			}

			bool hasBounds = false;
			bounds = default;

			if (UseChildColliders)
			{
				var colliders = GetComponentsInChildren<Collider>(true);
				for (int i = 0; i < colliders.Length; i++)
				{
					var collider = colliders[i];
					if (collider == null)
						continue;
					if (IncludeTriggerColliders == false && collider.isTrigger)
						continue;

					if (hasBounds == false)
					{
						bounds = collider.bounds;
						hasBounds = true;
					}
					else
					{
						bounds.Encapsulate(collider.bounds);
					}
				}
			}

			if (hasBounds == false && UseRenderersWhenNoColliders)
			{
				var renderers = GetComponentsInChildren<Renderer>(true);
				for (int i = 0; i < renderers.Length; i++)
				{
					var renderer = renderers[i];
					if (renderer == null)
						continue;

					if (hasBounds == false)
					{
						bounds = renderer.bounds;
						hasBounds = true;
					}
					else
					{
						bounds.Encapsulate(renderer.bounds);
					}
				}
			}

			return hasBounds && bounds.size.sqrMagnitude > 0.001f;
		}

		private static Vector3 Abs(Vector3 value)
		{
			return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
		}
	}
}
