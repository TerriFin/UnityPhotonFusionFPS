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

		[Header("Performance")]
		public float InitialReevaluationStaggerMax = 0.75f;
		public float ReevaluateIntervalJitter = 0.25f;
		public int MaxCachedAllies = 16;

		[Header("Line Of Fire")]
		public bool RequireCandidateLineOfFire = true;
		public float PartialCoverProbeOffset = 0.6f;

		[Header("Target Loss Blacklist")]
		public float TargetLossBlacklistDuration = 4f;
		public float TargetLossBlacklistRadius = 2f;
		public float TargetLossRecentDestinationTime = 3f;

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
		private float _destinationSetTime;
		private Vector3 _blacklistedDestination;
		private bool _hasBlacklistedDestination;
		private float _blacklistUntil;
		private bool _hasScheduledInitialEvaluation;
		private Vector3[] _cachedAllyPositions;
		private float[] _cachedAllyDistanceSqr;
		private int _cachedAllyCount;
		private int _cachedAllyLimit;

		public void ClearTask(CharacterNavigator navigator)
		{
			_hasDestination = false;
			_nextReevaluateTime = 0f;
			_hasScheduledInitialEvaluation = false;
			navigator?.ClearDestination();
		}

		public void NotifyTargetLost(Survivor survivor)
		{
			if (_hasDestination == false || TargetLossBlacklistDuration <= 0f || TargetLossBlacklistRadius <= 0f)
				return;

			float now = Time.timeSinceLevelLoad;
			bool recentlySelected = now <= _destinationSetTime + Mathf.Max(0f, TargetLossRecentDestinationTime);
			bool closeToDestination = survivor != null &&
			                          FlatDistanceSqr(survivor.transform.position, _destination) <=
			                          Mathf.Max(StoppingDistance, TargetLossBlacklistRadius) *
			                          Mathf.Max(StoppingDistance, TargetLossBlacklistRadius);

			if (recentlySelected == false && closeToDestination == false)
				return;

			BlacklistDestination(_destination, now, survivor != null ? survivor.Navigator : null);
		}

		public bool TryGetMoveDirection(Survivor survivor, KnownEnemyInfo enemy, out Vector3 moveDirection)
		{
			moveDirection = default;
			if (survivor == null || survivor.Navigator == null)
				return false;

			float now = Time.timeSinceLevelLoad;
			if (_hasDestination == false)
			{
				if (_hasScheduledInitialEvaluation == false)
					ScheduleInitialEvaluation(now);
				if (now < _nextReevaluateTime)
					return false;
			}

			if (_hasDestination == false || now >= _nextReevaluateTime)
			{
				EvaluateDestination(survivor, enemy);
				_nextReevaluateTime = now + GetNextReevaluationDelay();
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

			float now = Time.timeSinceLevelLoad;
			ExpireBlacklist(now);

			Vector3 currentPosition = survivor.transform.position;
			float startSampleDistance = Mathf.Max(0.01f, navigator.SampleMaxDistance);
			if (NavMesh.SamplePosition(currentPosition, out var startHit, startSampleDistance, navigator.AreaMask) == false)
				return;

			Vector3 enemyPosition = GetEnemyPosition(enemy);
			if (_hasDestination && RequireCandidateLineOfFire && HasLineOfFireFromPoint(survivor, _destination, enemyPosition) == false)
			{
				BlacklistDestination(_destination, now, navigator);
			}

			WeaponRange range = GetPreferredRange(survivor);
			CacheNearbyAllies(survivor, currentPosition);

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
				if (IsPointBlacklisted(reachablePoint, now))
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
			_destinationSetTime = now;
			navigator.SetDestination(_destination);
		}

		private void BlacklistDestination(Vector3 destination, float now, CharacterNavigator navigator)
		{
			_blacklistedDestination = destination;
			_blacklistUntil = now + Mathf.Max(0f, TargetLossBlacklistDuration);
			_hasBlacklistedDestination = true;
			_hasDestination = false;
			ScheduleInitialEvaluation(now);
			navigator?.ClearDestination();
		}

		private void ScheduleInitialEvaluation(float now)
		{
			_hasScheduledInitialEvaluation = true;

			float stagger = Mathf.Max(0f, InitialReevaluationStaggerMax);
			_nextReevaluateTime = stagger > 0f ? now + Random.Range(0f, stagger) : now;
		}

		private float GetNextReevaluationDelay()
		{
			float delay = Mathf.Max(0.05f, ReevaluateInterval);
			float jitter = Mathf.Max(0f, ReevaluateIntervalJitter);
			return jitter > 0f ? delay + Random.Range(0f, jitter) : delay;
		}

		private bool IsPointBlacklisted(Vector3 point, float now)
		{
			ExpireBlacklist(now);
			if (_hasBlacklistedDestination == false)
				return false;

			float radius = Mathf.Max(0f, TargetLossBlacklistRadius);
			return FlatDistanceSqr(point, _blacklistedDestination) <= radius * radius;
		}

		private void ExpireBlacklist(float now)
		{
			if (_hasBlacklistedDestination && now >= _blacklistUntil)
				_hasBlacklistedDestination = false;
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
			score -= GetAllySpacingPenalty(point) * Mathf.Max(0f, AllySpacingWeight);
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

		private void CacheNearbyAllies(Survivor survivor, Vector3 currentPosition)
		{
			_cachedAllyCount = 0;
			if (survivor == null || AllySpacingWeight <= 0f || AllySpacingRadius <= 0f || MaxCachedAllies <= 0)
				return;

			_cachedAllyLimit = Mathf.Max(1, MaxCachedAllies);
			EnsureAllyCacheCapacity(_cachedAllyLimit);

			float cacheRadius = Mathf.Max(0.01f, SearchRadius + AllySpacingRadius);
			float cacheRadiusSqr = cacheRadius * cacheRadius;

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

				Vector3 allyPosition = ally.transform.position;
				float distanceSqr = FlatDistanceSqr(currentPosition, allyPosition);
				if (distanceSqr >= cacheRadiusSqr)
					continue;

				AddCachedAlly(allyPosition, distanceSqr);
			}
		}

		private void EnsureAllyCacheCapacity(int capacity)
		{
			if (_cachedAllyPositions != null && _cachedAllyPositions.Length >= capacity)
				return;

			_cachedAllyPositions = new Vector3[capacity];
			_cachedAllyDistanceSqr = new float[capacity];
		}

		private void AddCachedAlly(Vector3 position, float distanceSqr)
		{
			if (_cachedAllyCount < _cachedAllyLimit)
			{
				_cachedAllyPositions[_cachedAllyCount] = position;
				_cachedAllyDistanceSqr[_cachedAllyCount] = distanceSqr;
				_cachedAllyCount++;
				return;
			}

			int farthestIndex = 0;
			float farthestDistanceSqr = _cachedAllyDistanceSqr[0];
			for (int i = 1; i < _cachedAllyCount; i++)
			{
				if (_cachedAllyDistanceSqr[i] <= farthestDistanceSqr)
					continue;

				farthestDistanceSqr = _cachedAllyDistanceSqr[i];
				farthestIndex = i;
			}

			if (distanceSqr >= farthestDistanceSqr)
				return;

			_cachedAllyPositions[farthestIndex] = position;
			_cachedAllyDistanceSqr[farthestIndex] = distanceSqr;
		}

		private float GetAllySpacingPenalty(Vector3 point)
		{
			if (_cachedAllyCount <= 0 || AllySpacingRadius <= 0f)
				return 0f;

			float radius = Mathf.Max(0.01f, AllySpacingRadius);
			float radiusSqr = radius * radius;
			float penalty = 0f;

			for (int i = 0; i < _cachedAllyCount; i++)
			{
				float distanceSqr = FlatDistanceSqr(point, _cachedAllyPositions[i]);
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
