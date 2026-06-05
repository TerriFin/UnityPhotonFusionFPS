using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.AI;

namespace SimpleFPS
{
	public sealed class ZombieOrchestrator : MonoBehaviour
	{
		private struct SpawnCandidate
		{
			public ZombieSpawnPoint Point;
			public Vector3 Position;
			public Quaternion Rotation;
			public int Region;
			public int Remaining;
		}

		[Header("Setup")]
		public BuildingPlacementGenerator BuildingGenerator;
		public HeightMapGenerator HeightGenerator;
		public ZombieOrchestratorSettings Settings;
		public NetworkRunner Runner;
		public Gameplay Gameplay;

		[Header("Runtime")]
		public bool FindRunnerIfMissing = true;
		public bool FindGameplayIfMissing = true;
		public bool CollectSpawnPointsOnStart = true;
		public bool SpawnOnStart = true;
		public bool SpawnDuringSkirmish;

		private readonly List<ZombieSpawnPoint> _spawnPoints = new();
		private readonly List<SpawnCandidate> _candidates = new();
		private readonly List<int> _bestRegions = new();
		private readonly List<NetworkObject> _spawnedZombies = new();
		private NavMeshPath _spawnValidationPath;
		private Bounds _spawnBounds;
		private System.Random _random;
		private float _nextPulseTime;
		private float _spawnRemainder;
		private int _filteredSpawnPointCount;
		private bool _hasSpawnBounds;
		private bool _isOvertime;
		private bool _isStarted;
		private bool _loggedMissingRunner;
		private bool _loggedNotSceneAuthority;
		private bool _loggedNoSpawnPoints;

		public bool IsOvertime => _isOvertime;
		public bool HasUsableSettings => Settings != null && Settings.ZombiePrefab != null;

		private IEnumerator Start()
		{
			if (CollectSpawnPointsOnStart)
			{
				yield return WaitForGeneratedBuildings();
				CollectSpawnPoints();
			}

			_isStarted = SpawnOnStart;
			_nextPulseTime = Time.timeSinceLevelLoad + GetPulseInterval();
		}

		private void Update()
		{
			if (_isStarted == false || HasUsableSettings == false)
				return;

			NetworkRunner runner = GetRunner();
			if (runner == null)
			{
				if (_loggedMissingRunner == false)
				{
					Debug.LogWarning($"{nameof(ZombieOrchestrator)} could not find a {nameof(NetworkRunner)} yet.", this);
					_loggedMissingRunner = true;
				}
				return;
			}

			_loggedMissingRunner = false;

			if (runner.IsSceneAuthority == false)
			{
				if (_loggedNotSceneAuthority == false)
				{
					Debug.Log($"{nameof(ZombieOrchestrator)} found runner on a non-scene-authority peer; zombie spawning will run on scene authority only.", this);
					_loggedNotSceneAuthority = true;
				}
				return;
			}

			_loggedNotSceneAuthority = false;

			ResolveGameplay();
			if (ShouldRunSpawner() == false)
				return;

			if (_spawnPoints.Count == 0)
				CollectSpawnPoints();

			if (_spawnPoints.Count == 0)
			{
				if (_loggedNoSpawnPoints == false)
				{
					Debug.LogWarning($"{nameof(ZombieOrchestrator)} found no {nameof(ZombieSpawnPoint)} markers under generated building or height roots.", this);
					_loggedNoSpawnPoints = true;
				}
				return;
			}

			_loggedNoSpawnPoints = false;

			if (Time.timeSinceLevelLoad < _nextPulseTime)
				return;

			_nextPulseTime = Time.timeSinceLevelLoad + GetPulseInterval();
			RunSpawnPulse(runner);
		}

		[ContextMenu("Collect Zombie Spawn Points")]
		public void CollectSpawnPoints()
		{
			_spawnPoints.Clear();
			_hasSpawnBounds = false;
			_filteredSpawnPointCount = 0;

			var seen = new HashSet<ZombieSpawnPoint>();
			CollectSpawnPointsFromRoot(GetGeneratedBuildingRoot(), seen);
			CollectSpawnPointsFromRoot(GetGeneratedHeightRoot(), seen);

			_random = new System.Random(GetSeed());

			if (_filteredSpawnPointCount > 0)
				Debug.Log($"{nameof(ZombieOrchestrator)} ignored {_filteredSpawnPointCount} zombie spawn point(s) on too-small or unreachable NavMesh islands.", this);
		}

		private void CollectSpawnPointsFromRoot(Transform root, HashSet<ZombieSpawnPoint> seen)
		{
			if (root == null)
				return;

			ZombieSpawnPoint[] markers = root.GetComponentsInChildren<ZombieSpawnPoint>(true);
			for (int i = 0; i < markers.Length; i++)
			{
				var marker = markers[i];
				if (marker == null)
					continue;
				if (seen.Add(marker) == false)
					continue;

				if (TryGetUsableSpawnPointPosition(marker, out var navMeshPosition) == false)
				{
					_filteredSpawnPointCount++;
					continue;
				}

				_spawnPoints.Add(marker);
				EncapsulateSpawnPoint(navMeshPosition);
			}
		}

		[ContextMenu("Start Zombie Spawning")]
		public void StartSpawning()
		{
			_isStarted = true;
			_nextPulseTime = Time.timeSinceLevelLoad;
		}

		[ContextMenu("Start Overtime")]
		public void StartOvertime()
		{
			if (_isOvertime || Settings == null)
				return;

			_isOvertime = true;
			ZombieStats stats = GetOvertimeStats();
			for (int i = ZombieCharacter.ActiveZombies.Count - 1; i >= 0; i--)
			{
				var zombie = ZombieCharacter.ActiveZombies[i];
				if (zombie == null)
				{
					ZombieCharacter.ActiveZombies.RemoveAt(i);
					continue;
				}
				if (zombie.Health == null || zombie.Health.IsAlive == false)
					continue;

				zombie.EnterOvertime(stats);
			}
		}

		private void RunSpawnPulse(NetworkRunner runner)
		{
			int aliveZombies = CountAliveZombies();
			int currentMaxZombies = GetCurrentMaxZombies();
			if (aliveZombies >= currentMaxZombies)
				return;

			int spawnBudget = Mathf.Min(GetSpawnBudget(), currentMaxZombies - aliveZombies);
			if (spawnBudget <= 0)
				return;

			BuildSpawnCandidates();
			if (_candidates.Count == 0)
				return;

			int spawnedThisPulse = 0;
			for (int i = 0; i < spawnBudget && _candidates.Count > 0; i++)
			{
				int candidateIndex = ChooseCandidateIndex();
				if (candidateIndex < 0 || candidateIndex >= _candidates.Count)
					break;

				SpawnCandidate candidate = _candidates[candidateIndex];
				SpawnZombie(runner, candidate);
				spawnedThisPulse++;

				candidate.Remaining--;
				if (candidate.Remaining <= 0)
					_candidates.RemoveAt(candidateIndex);
				else
					_candidates[candidateIndex] = candidate;
			}

			if (spawnedThisPulse == 0)
				_spawnRemainder = 0f;
		}

		private void SpawnZombie(NetworkRunner runner, SpawnCandidate candidate)
		{
			ZombieStats stats = _isOvertime ? GetOvertimeStats() : GetCurrentStats();
			runner.SpawnAsync(Settings.ZombiePrefab, candidate.Position, candidate.Rotation, null, null, default,
				spawn =>
				{
					var zombie = spawn.Object != null ? spawn.Object.GetComponent<ZombieCharacter>() : null;
					if (zombie == null)
						return;

					zombie.ApplyStats(stats, false);
					if (_isOvertime)
						zombie.EnterOvertime(stats);
					AddSpawnedZombie(spawn.Object);
				});
		}

		private void BuildSpawnCandidates()
		{
			_candidates.Clear();
			if (_spawnPoints.Count == 0)
				return;

			for (int i = 0; i < _spawnPoints.Count; i++)
			{
				var point = _spawnPoints[i];
				if (point == null)
					continue;
				if (IsSpawnPointBlocked(point))
					continue;
				if (TryGetUsableSpawnPointPosition(point, out var spawnPosition) == false)
					continue;

				_candidates.Add(new SpawnCandidate
				{
					Point = point,
					Position = spawnPosition,
					Rotation = point.transform.rotation,
					Region = GetRegion(point.transform.position),
					Remaining = Mathf.Max(1, point.MaxSpawnCountPerPulse),
				});
			}
		}

		private bool TryGetUsableSpawnPointPosition(ZombieSpawnPoint point, out Vector3 position)
		{
			position = default;
			if (point == null)
				return false;

			float sampleDistance = Settings != null ? Settings.SpawnNavMeshSampleDistance : 1.5f;
			if (NavMesh.SamplePosition(point.transform.position, out var hit, Mathf.Max(0.1f, sampleDistance), NavMesh.AllAreas) == false)
				return false;

			position = hit.position;
			float requiredRadius = Settings != null ? Settings.MinimumSpawnConnectedNavMeshRadius : 0f;
			return IsConnectedNavMeshLargeEnough(position, requiredRadius, sampleDistance);
		}

		private bool IsConnectedNavMeshLargeEnough(Vector3 navMeshPosition, float requiredRadius, float sampleDistance)
		{
			if (requiredRadius <= 0f)
				return true;

			if (_spawnValidationPath == null)
				_spawnValidationPath = new NavMeshPath();

			const int probeCount = 12;
			float radius = Mathf.Max(0.5f, requiredRadius);
			float navSampleDistance = Mathf.Max(0.5f, sampleDistance);

			for (int i = 0; i < probeCount; i++)
			{
				float angle = i * Mathf.PI * 2f / probeCount;
				Vector3 probe = navMeshPosition + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
				if (NavMesh.SamplePosition(probe, out var probeHit, navSampleDistance, NavMesh.AllAreas) == false)
					continue;
				if (FlatDistanceSqr(navMeshPosition, probeHit.position) < radius * radius * 0.65f)
					continue;
				if (NavMesh.CalculatePath(navMeshPosition, probeHit.position, NavMesh.AllAreas, _spawnValidationPath) == false)
					continue;
				if (_spawnValidationPath.status != NavMeshPathStatus.PathComplete)
					continue;

				return true;
			}

			return false;
		}

		private int ChooseCandidateIndex()
		{
			if (_candidates.Count == 0)
				return -1;
			if (_random == null)
				_random = new System.Random(GetSeed());

			float bias = Settings != null ? Mathf.Clamp01(Settings.UnderpopulatedRegionBias) : 0f;
			if (_random.NextDouble() >= bias)
				return _random.Next(_candidates.Count);

			FindBestUnderpopulatedRegions();
			if (_bestRegions.Count == 0)
				return _random.Next(_candidates.Count);

			int attempts = _candidates.Count * 2;
			for (int i = 0; i < attempts; i++)
			{
				int candidateIndex = _random.Next(_candidates.Count);
				if (_bestRegions.Contains(_candidates[candidateIndex].Region))
					return candidateIndex;
			}

			return _random.Next(_candidates.Count);
		}

		private void FindBestUnderpopulatedRegions()
		{
			_bestRegions.Clear();
			int regionCount = GetRegionCount();
			if (regionCount <= 0)
				return;

			int[] zombieCounts = new int[regionCount];
			int[] spawnCounts = new int[regionCount];

			for (int i = 0; i < ZombieCharacter.ActiveZombies.Count; i++)
			{
				var zombie = ZombieCharacter.ActiveZombies[i];
				if (zombie == null || zombie.Health == null || zombie.Health.IsAlive == false)
					continue;

				int region = GetRegion(zombie.transform.position);
				if (region >= 0 && region < zombieCounts.Length)
					zombieCounts[region]++;
			}

			for (int i = 0; i < _candidates.Count; i++)
			{
				int region = _candidates[i].Region;
				if (region >= 0 && region < spawnCounts.Length)
					spawnCounts[region]++;
			}

			float bestDeficit = 0f;
			int activeRegionCount = 0;
			for (int i = 0; i < spawnCounts.Length; i++)
			{
				if (spawnCounts[i] > 0)
					activeRegionCount++;
			}

			if (activeRegionCount <= 0)
				return;

			float desiredPerRegion = Mathf.Max(1f, CountAliveZombies() / (float)activeRegionCount);
			for (int i = 0; i < spawnCounts.Length; i++)
			{
				if (spawnCounts[i] <= 0)
					continue;

				float deficit = desiredPerRegion - zombieCounts[i];
				if (deficit < bestDeficit - 0.001f)
					continue;
				if (deficit > bestDeficit + 0.001f)
				{
					bestDeficit = deficit;
					_bestRegions.Clear();
				}

				_bestRegions.Add(i);
			}
		}

		private bool IsSpawnPointBlocked(ZombieSpawnPoint point)
		{
			if (point == null || point.IsForced)
				return false;

			float radius = point.NonForcedSurvivorBlockRadius;
			float radiusSqr = radius * radius;
			Vector3 origin = point.transform.position;

			for (int i = CharacterSensor.ActiveSensors.Count - 1; i >= 0; i--)
			{
				var sensor = CharacterSensor.ActiveSensors[i];
				if (sensor == null)
				{
					CharacterSensor.ActiveSensors.RemoveAt(i);
					continue;
				}

				var survivor = sensor.Survivor;
				if (survivor == null || survivor.Health == null || survivor.Health.IsAlive == false)
					continue;
				if ((survivor.transform.position - origin).sqrMagnitude <= radiusSqr)
					return true;
			}

			return false;
		}

		private bool ShouldRunSpawner()
		{
			if (_isOvertime)
				return true;

			ResolveGameplay();
			if (Gameplay == null)
				return SpawnDuringSkirmish;
			if (Gameplay.State == EGameplayState.Running)
				return true;
			if (Gameplay.State == EGameplayState.Skirmish)
				return SpawnDuringSkirmish;

			return false;
		}

		private int GetSpawnBudget()
		{
			float interval = GetPulseInterval();
			float rate = _isOvertime ? Settings.EndSpawnRatePerMinute : Mathf.Lerp(Settings.StartSpawnRatePerMinute, Settings.EndSpawnRatePerMinute, GetProgress01());
			_spawnRemainder += Mathf.Max(0f, rate) * interval / 60f;

			int budget = Mathf.FloorToInt(_spawnRemainder);
			_spawnRemainder -= budget;

			int pulseCap = Mathf.Max(0, Settings.MaxSpawnPerPulsePerPlayer) * Mathf.Max(1, GetConnectedPlayerCount());
			return Mathf.Min(Mathf.Max(0, budget), pulseCap);
		}

		private int GetConnectedPlayerCount()
		{
			if (Gameplay == null)
				return 0;

			int count = 0;
			foreach (var pair in Gameplay.PlayerData)
			{
				if (pair.Value.IsConnected)
					count++;
			}
			return count;
		}

		private int GetCurrentMaxZombies()
		{
			if (_isOvertime)
				return Mathf.Max(0, Settings.EndMaxZombies);

			return Mathf.RoundToInt(Mathf.Lerp(Settings.StartMaxZombies, Settings.EndMaxZombies, GetProgress01()));
		}

		private ZombieStats GetCurrentStats()
		{
			float progress = GetProgress01();
			return new ZombieStats
			{
				MaxHealth = Mathf.Lerp(Settings.StartHealth, Settings.EndHealth, progress),
				Damage = Mathf.Lerp(Settings.StartDamage, Settings.EndDamage, progress),
				MoveSpeed = Mathf.Lerp(Settings.StartMoveSpeed, Settings.EndMoveSpeed, progress),
				AlertRadius = Mathf.Lerp(Settings.StartAlertRadius, Settings.EndAlertRadius, progress),
				AttackRange = Settings.ZombiePrefab != null
					? Settings.ZombiePrefab.GetComponent<ZombieCharacter>()?.Stats.AttackRange ?? 1.2f
					: 1.2f,
				AttackCooldown = Settings.ZombiePrefab != null
					? Settings.ZombiePrefab.GetComponent<ZombieCharacter>()?.Stats.AttackCooldown ?? 1.1f
					: 1.1f,
			};
		}

		private ZombieStats GetOvertimeStats()
		{
			ZombieStats prefabStats = Settings.ZombiePrefab != null && Settings.ZombiePrefab.GetComponent<ZombieCharacter>() != null
				? Settings.ZombiePrefab.GetComponent<ZombieCharacter>().Stats
				: default;

			return new ZombieStats
			{
				MaxHealth = Settings.OvertimeHealth,
				Damage = Settings.OvertimeDamage,
				MoveSpeed = Settings.OvertimeMoveSpeed,
				AlertRadius = prefabStats.AlertRadius,
				AttackRange = prefabStats.AttackRange > 0f ? prefabStats.AttackRange : 1.2f,
				AttackCooldown = prefabStats.AttackCooldown > 0f ? prefabStats.AttackCooldown : 1.1f,
			};
		}

		private float GetProgress01()
		{
			float duration = GetMatchDuration();
			if (duration <= 0f)
				return 0f;

			ResolveGameplay();
			if (Gameplay != null)
			{
				if (Gameplay.State == EGameplayState.Skirmish && Settings.ScaleDuringSkirmish == false)
					return 0f;

				if (Gameplay.State == EGameplayState.Running)
				{
					float remaining = Gameplay.RemainingTime.RemainingTime(Gameplay.Runner).GetValueOrDefault();
					return Mathf.Clamp01((duration - remaining) / duration);
				}
			}

			return Mathf.Clamp01(Time.timeSinceLevelLoad / duration);
		}

		private float GetMatchDuration()
		{
			if (Settings == null)
				return 0f;
			if (Settings.MatchDurationSeconds > 0f)
				return Settings.MatchDurationSeconds;

			ResolveGameplay();
			return Gameplay != null ? Gameplay.GameDuration : 0f;
		}

		private int CountAliveZombies()
		{
			int count = 0;
			for (int i = ZombieCharacter.ActiveZombies.Count - 1; i >= 0; i--)
			{
				var zombie = ZombieCharacter.ActiveZombies[i];
				if (zombie == null)
				{
					ZombieCharacter.ActiveZombies.RemoveAt(i);
					continue;
				}
				if (zombie.Health != null && zombie.Health.IsAlive)
					count++;
			}

			return count;
		}

		private IEnumerator WaitForGeneratedBuildings()
		{
			if (BuildingGenerator == null)
				BuildingGenerator = GetComponent<BuildingPlacementGenerator>();
			if (BuildingGenerator == null)
				BuildingGenerator = FindObjectOfType<BuildingPlacementGenerator>();

			while (BuildingGenerator != null && BuildingGenerator.GenerateOnStart && BuildingGenerator.IsGenerationComplete == false)
				yield return null;
		}

		private Transform GetGeneratedBuildingRoot()
		{
			if (BuildingGenerator == null)
				BuildingGenerator = GetComponent<BuildingPlacementGenerator>();
			if (BuildingGenerator == null)
				BuildingGenerator = FindObjectOfType<BuildingPlacementGenerator>();

			return BuildingGenerator != null ? BuildingGenerator.GeneratedRoot : null;
		}

		private Transform GetGeneratedHeightRoot()
		{
			if (HeightGenerator == null && BuildingGenerator != null && BuildingGenerator.RoadGenerator != null)
				HeightGenerator = BuildingGenerator.RoadGenerator.HeightGenerator;
			if (HeightGenerator == null)
				HeightGenerator = GetComponent<HeightMapGenerator>();
			if (HeightGenerator == null)
				HeightGenerator = FindObjectOfType<HeightMapGenerator>();

			return HeightGenerator != null ? HeightGenerator.GeneratedRoot : null;
		}

		private NetworkRunner GetRunner()
		{
			if (Runner != null)
				return Runner;

			ResolveGameplay();
			if (Gameplay != null && Gameplay.Runner != null)
			{
				Runner = Gameplay.Runner;
				return Runner;
			}

			if (FindRunnerIfMissing)
			{
				var runners = FindObjectsOfType<NetworkRunner>(true);
				for (int i = 0; i < runners.Length; i++)
				{
					if (runners[i] == null)
						continue;

					Runner = runners[i];
					return Runner;
				}
			}

			return Runner;
		}

		private void ResolveGameplay()
		{
			if (Gameplay == null && FindGameplayIfMissing)
				Gameplay = FindObjectOfType<Gameplay>();
		}

		private float GetPulseInterval()
		{
			return Settings != null ? Mathf.Max(0.25f, Settings.SpawnPulseInterval) : 5f;
		}

		private int GetSeed()
		{
			int seed = Gameplay != null ? Gameplay.WorldSeed : 0;
			if (seed == 0 && BuildingGenerator != null && BuildingGenerator.RoadGenerator != null)
				seed = BuildingGenerator.RoadGenerator.Seed;
			return seed + (Settings != null ? Settings.SeedOffset : 0);
		}

		private void EncapsulateSpawnPoint(Vector3 position)
		{
			if (_hasSpawnBounds == false)
			{
				_spawnBounds = new Bounds(position, Vector3.one);
				_hasSpawnBounds = true;
				return;
			}

			_spawnBounds.Encapsulate(position);
		}

		private int GetRegion(Vector3 position)
		{
			if (_hasSpawnBounds == false)
				return 0;

			int grid = Mathf.Max(1, Settings != null ? Settings.RegionGridSize : 4);
			Vector3 min = _spawnBounds.min;
			Vector3 size = _spawnBounds.size;
			int x = size.x <= 0.001f ? 0 : Mathf.Clamp(Mathf.FloorToInt((position.x - min.x) / size.x * grid), 0, grid - 1);
			int z = size.z <= 0.001f ? 0 : Mathf.Clamp(Mathf.FloorToInt((position.z - min.z) / size.z * grid), 0, grid - 1);
			return z * grid + x;
		}

		private int GetRegionCount()
		{
			int grid = Mathf.Max(1, Settings != null ? Settings.RegionGridSize : 4);
			return grid * grid;
		}

		private void AddSpawnedZombie(NetworkObject zombie)
		{
			if (zombie != null && _spawnedZombies.Contains(zombie) == false)
				_spawnedZombies.Add(zombie);
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}
	}
}
