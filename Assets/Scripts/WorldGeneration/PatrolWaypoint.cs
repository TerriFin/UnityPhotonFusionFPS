using System.Collections.Generic;
using UnityEngine;

namespace SimpleFPS
{
	// Prefab-authored patrol point. Place these as children of building prefabs (rooftops, windows, doors,
	// stairwells, points of interest) so every generated map instance carries its own garrison points, exactly
	// like the neutral-survivor / zombie / pickup spawn markers.
	//
	// When a patrol / assigned-area circle covers a waypoint's XZ position, that waypoint is forced into the
	// patrol set (see SurvivorAssignedAreaAI.TryBuildReachablePointSet). This lets survivors garrison complex,
	// multi-level buildings whose upper floors the automatic ground-level point sampler cannot discover.
	//
	// Reachability is the author's responsibility: waypoints are NOT reachability-tested at runtime. Place them
	// on the walkable surface (within ~1 m of the floor's NavMesh) so the navigator resolves the intended level,
	// and make sure a survivor can actually path to each one — an unreachable waypoint leaves a survivor stuck
	// trying to reach it.
	[DisallowMultipleComponent]
	public sealed class PatrolWaypoint : MonoBehaviour
	{
		private static readonly List<PatrolWaypoint> ActiveWaypoints = new();

		[Tooltip("Editor gizmo radius for this patrol waypoint. Visual only; does not affect behavior.")]
		public float GizmoRadius = 0.4f;

		private void OnEnable()
		{
			ActiveWaypoints.Add(this);
		}

		private void OnDisable()
		{
			ActiveWaypoints.Remove(this);
		}

		// Append the world position of every active waypoint whose XZ falls within `radius` of `center`, and
		// return how many were added. Height is ignored on purpose: a top-down patrol circle covers a whole
		// building footprint, so all of its vertically stacked waypoints (each floor's windows, the roof) are
		// captured at once.
		public static int CollectInsideCircle(Vector3 center, float radius, List<Vector3> results)
		{
			if (results == null || radius <= 0f)
				return 0;

			float radiusSqr = radius * radius;
			int added = 0;
			for (int i = 0; i < ActiveWaypoints.Count; i++)
			{
				var waypoint = ActiveWaypoints[i];
				if (waypoint == null)
					continue;

				Vector3 position = waypoint.transform.position;
				float dx = position.x - center.x;
				float dz = position.z - center.z;
				if (dx * dx + dz * dz > radiusSqr)
					continue;

				results.Add(position);
				added++;
			}

			return added;
		}

		private void OnDrawGizmos()
		{
			Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.9f);
			float radius = Mathf.Max(0.05f, GizmoRadius);
			Gizmos.DrawWireSphere(transform.position, radius);
			Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.5f);
		}
	}
}
