using UnityEngine;
using UnityEngine.AI;

namespace SimpleFPS
{
	[DisallowMultipleComponent]
	public sealed class SurvivorCombatMovementAI : MonoBehaviour
	{
		[Header("Search")]
		public float ReevaluateInterval = 1f;
		public int CandidateCount = 10;
		public float SearchRadius = 8f;
		public float NavMeshSampleDistance = 2f;
		public float StoppingDistance = 1.25f;
		public float MinimumMoveDistance = 1.5f;
		public float DestinationRefreshDistance = 1f;
		public float DirectFallbackDistance = 4f;
		public float RequiredScoreImprovement = 0.25f;

		[Header("Line Of Fire")]
		public bool RequireCandidateLineOfFire = true;
		public float PartialCoverProbeOffset = 0.6f;

		[Header("Scoring")]
		public float CoverWeight = 4f;
		public float AllySpacingWeight = 2f;
		public float PreferredRangeWeight = 2f;
		public float MoveCostWeight = 0.35f;
		public float AllySpacingRadius = 4f;

		[Header("Weapon Ranges")]
		public float PistolPreferredRangeMin = 6f;
		public float PistolPreferredRangeMax = 18f;
		public float ShotgunPreferredRangeMin = 3f;
		public float ShotgunPreferredRangeMax = 10f;
		public float RiflePreferredRangeMin = 10f;
		public float RiflePreferredRangeMax = 28f;

		private NavMeshPath _scratchPath;
		private Vector3 _destination;
		private bool _hasDestination;
		private float _nextReevaluateTime;
		private float _candidateAngleOffset;

		public void ClearTask(CharacterNavigator navigator)
		{
			_hasDestination = false;
			_nextReevaluateTime = 0f;
			navigator?.ClearDestination();
		}

		public bool TryGetMoveDirection(Survivor survivor, KnownEnemyInfo enemy, out Vector3 moveDirection)
		{
			moveDirection = default;
			if (survivor == null || survivor.Navigator == null)
				return false;

			float now = Time.timeSinceLevelLoad;
			if (_hasDestination == false || now >= _nextReevaluateTime)
			{
				_nextReevaluateTime = now + Mathf.Max(0.05f, ReevaluateInterval);
				EvaluateDestination(survivor, enemy);
			}

			if (_hasDestination == false)
				return false;

			if (FlatDistanceSqr(survivor.transform.position, _destination) <= StoppingDistance * StoppingDistance)
			{
				survivor.Navigator.ClearDestination();
				return false;
			}

			Vector3 steeringTarget;
			var navigator = survivor.Navigator;
			navigator.SetDestination(_destination);
			navigator.Tick(survivor.transform.position);
			if (navigator.TryGetSteeringTarget(survivor.transform.position, out steeringTarget) == false)
			{
				if (FlatDistanceSqr(survivor.transform.position, _destination) > DirectFallbackDistance * DirectFallbackDistance)
					return false;

				steeringTarget = _destination;
			}

			moveDirection = steeringTarget - survivor.transform.position;
			moveDirection.y = 0f;
			if (moveDirection.sqrMagnitude < 0.001f)
				return false;

			moveDirection.Normalize();
			return true;
		}

		private void Awake()
		{
			_candidateAngleOffset = Random.Range(0f, Mathf.PI * 2f);
		}

		private void EvaluateDestination(Survivor survivor, KnownEnemyInfo enemy)
		{
			var navigator = survivor.Navigator;
			if (navigator == null)
				return;

			if (_scratchPath == null)
				_scratchPath = new NavMeshPath();

			Vector3 currentPosition = survivor.transform.position;
			float startSampleDistance = Mathf.Max(0.01f, navigator.SampleMaxDistance);
			if (NavMesh.SamplePosition(currentPosition, out var startHit, startSampleDistance, navigator.AreaMask) == false)
				return;

			Vector3 enemyPosition = GetEnemyPosition(enemy);
			if (_hasDestination && RequireCandidateLineOfFire && HasLineOfFireFromPoint(survivor, _destination, enemyPosition) == false)
			{
				_hasDestination = false;
				navigator.ClearDestination();
			}

			WeaponRange range = GetPreferredRange(survivor);
			float currentScore = ScorePoint(survivor, currentPosition, currentPosition, enemyPosition, range);
			float bestScore = currentScore;
			Vector3 bestPoint = currentPosition;

			int count = Mathf.Max(1, CandidateCount);
			float searchRadius = Mathf.Max(0.1f, SearchRadius);
			float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));
			for (int i = 0; i < count; i++)
			{
				float normalized = (i + 0.5f) / count;
				float radius = Mathf.Sqrt(normalized) * searchRadius;
				float angle = _candidateAngleOffset + i * goldenAngle;
				Vector3 candidate = currentPosition + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

				if (TryFindReachablePoint(navigator, startHit.position, candidate, out var reachablePoint) == false)
					continue;
				if (RequireCandidateLineOfFire && HasLineOfFireFromPoint(survivor, reachablePoint, enemyPosition) == false)
					continue;

				float score = ScorePoint(survivor, currentPosition, reachablePoint, enemyPosition, range);
				if (score <= bestScore)
					continue;

				bestScore = score;
				bestPoint = reachablePoint;
			}

			if (FlatDistanceSqr(currentPosition, bestPoint) < MinimumMoveDistance * MinimumMoveDistance)
				return;
			if (bestScore < currentScore + RequiredScoreImprovement)
				return;
			if (_hasDestination && FlatDistanceSqr(_destination, bestPoint) <= DestinationRefreshDistance * DestinationRefreshDistance)
				return;

			_destination = bestPoint;
			_hasDestination = true;
			navigator.SetDestination(_destination);
		}

		private bool TryFindReachablePoint(CharacterNavigator navigator, Vector3 startPosition, Vector3 candidate, out Vector3 reachablePoint)
		{
			reachablePoint = default;
			float sampleDistance = Mathf.Max(0.01f, NavMeshSampleDistance);
			if (NavMesh.SamplePosition(candidate, out var targetHit, sampleDistance, navigator.AreaMask) == false)
				return false;
			if (NavMesh.CalculatePath(startPosition, targetHit.position, navigator.AreaMask, _scratchPath) == false)
				return false;
			if (_scratchPath.status != NavMeshPathStatus.PathComplete)
				return false;

			reachablePoint = targetHit.position;
			return true;
		}

		private float ScorePoint(Survivor survivor, Vector3 currentPosition, Vector3 point, Vector3 enemyPosition, WeaponRange range)
		{
			float score = 0f;
			score += GetCoverScore(survivor, point, enemyPosition) * Mathf.Max(0f, CoverWeight);
			score += GetPreferredRangeScore(point, enemyPosition, range) * Mathf.Max(0f, PreferredRangeWeight);
			score -= GetAllySpacingPenalty(survivor, point) * Mathf.Max(0f, AllySpacingWeight);
			score -= GetMoveCost(currentPosition, point) * Mathf.Max(0f, MoveCostWeight);
			return score;
		}

		private float GetCoverScore(Survivor survivor, Vector3 point, Vector3 enemyPosition)
		{
			var sensor = survivor != null ? survivor.Sensor : null;
			if (sensor == null || sensor.VisionBlockers.value == 0)
				return 0f;
			if (HasLineOfFireFromPoint(survivor, point, enemyPosition) == false)
				return 0f;

			Vector3 toEnemy = enemyPosition - point;
			toEnemy.y = 0f;
			if (toEnemy.sqrMagnitude < 0.001f)
				return 0f;

			float offset = Mathf.Max(0f, PartialCoverProbeOffset);
			if (offset <= 0f)
				return 0f;

			Vector3 right = Vector3.Cross(Vector3.up, toEnemy.normalized);
			float blockedSideCount = 0f;
			if (IsBlockedFromEnemy(sensor, point + right * offset, enemyPosition))
				blockedSideCount += 1f;
			if (IsBlockedFromEnemy(sensor, point - right * offset, enemyPosition))
				blockedSideCount += 1f;

			return blockedSideCount * 0.5f;
		}

		private bool HasLineOfFireFromPoint(Survivor survivor, Vector3 point, Vector3 enemyPosition)
		{
			var sensor = survivor != null ? survivor.Sensor : null;
			if (sensor == null || sensor.VisionBlockers.value == 0)
				return true;

			return IsBlockedFromEnemy(sensor, point, enemyPosition) == false;
		}

		private static bool IsBlockedFromEnemy(CharacterSensor sensor, Vector3 point, Vector3 enemyPosition)
		{
			float eyeHeight = Mathf.Max(0.5f, sensor.EyeHeight);
			Vector3 enemyEye = enemyPosition + Vector3.up * eyeHeight;
			Vector3 pointEye = point + Vector3.up * eyeHeight;
			return Physics.Linecast(enemyEye, pointEye, sensor.VisionBlockers, QueryTriggerInteraction.Ignore);
		}

		private float GetPreferredRangeScore(Vector3 point, Vector3 enemyPosition, WeaponRange range)
		{
			float distance = FlatDistance(point, enemyPosition);
			float min = Mathf.Max(0f, range.Min);
			float max = Mathf.Max(min + 0.01f, range.Max);

			if (distance >= min && distance <= max)
				return 1f;

			float miss = distance < min ? min - distance : distance - max;
			float tolerance = Mathf.Max(1f, max - min);
			return Mathf.Clamp01(1f - miss / tolerance);
		}

		private float GetAllySpacingPenalty(Survivor survivor, Vector3 point)
		{
			if (survivor == null || AllySpacingRadius <= 0f)
				return 0f;

			float radius = Mathf.Max(0.01f, AllySpacingRadius);
			float radiusSqr = radius * radius;
			float penalty = 0f;

			for (int i = CharacterSensor.ActiveSensors.Count - 1; i >= 0; i--)
			{
				var sensor = CharacterSensor.ActiveSensors[i];
				if (sensor == null)
				{
					CharacterSensor.ActiveSensors.RemoveAt(i);
					continue;
				}

				var ally = sensor.Survivor;
				if (ally == null || ally == survivor || ally.OwnerRef != survivor.OwnerRef)
					continue;
				if (ally.Health == null || ally.Health.IsAlive == false)
					continue;

				float distanceSqr = FlatDistanceSqr(point, ally.transform.position);
				if (distanceSqr >= radiusSqr)
					continue;

				float distance = Mathf.Sqrt(distanceSqr);
				penalty += 1f - distance / radius;
			}

			return penalty;
		}

		private float GetMoveCost(Vector3 currentPosition, Vector3 point)
		{
			return Mathf.Clamp01(FlatDistance(currentPosition, point) / Mathf.Max(0.1f, SearchRadius));
		}

		private WeaponRange GetPreferredRange(Survivor survivor)
		{
			EWeaponType weaponType = survivor != null &&
			                         survivor.Weapons != null &&
			                         survivor.Weapons.CurrentWeapon != null
				? survivor.Weapons.CurrentWeapon.Type
				: EWeaponType.Pistol;

			switch (weaponType)
			{
				case EWeaponType.Shotgun:
					return new WeaponRange(ShotgunPreferredRangeMin, ShotgunPreferredRangeMax);
				case EWeaponType.Rifle:
					return new WeaponRange(RiflePreferredRangeMin, RiflePreferredRangeMax);
				case EWeaponType.Pistol:
				default:
					return new WeaponRange(PistolPreferredRangeMin, PistolPreferredRangeMax);
			}
		}

		private static Vector3 GetEnemyPosition(KnownEnemyInfo enemy)
		{
			return enemy.Object != null ? enemy.Object.transform.position : enemy.LastKnownPosition;
		}

		private static float FlatDistance(Vector3 a, Vector3 b)
		{
			return Mathf.Sqrt(FlatDistanceSqr(a, b));
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}

		private readonly struct WeaponRange
		{
			public readonly float Min;
			public readonly float Max;

			public WeaponRange(float min, float max)
			{
				Min = min;
				Max = max;
			}
		}
	}
}
