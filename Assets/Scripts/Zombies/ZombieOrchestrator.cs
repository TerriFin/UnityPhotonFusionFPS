using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Serialization;

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

		// A collected spawn marker plus its cached NavMesh-validated position and region. Marker positions and the
		// baked NavMesh are static after generation, so this validation (NavMesh.SamplePosition plus up to 12
		// connectivity path probes per marker) is computed once at collection time instead of every spawn pulse.
		private struct SpawnPointEntry
		{
			public ZombieSpawnPoint Point;
			public Vector3 Position;
			public int Region;
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

		[Header("Climb Surfaces")]
		// Generates broad zombie-climbable faces across terrain ledges. Zombies still steer directly toward explicit
		// goals; these faces only decide whether the wall in front of them is allowed to become a climb.
		[FormerlySerializedAs("BuildClimbLinks")]
		public bool BuildTerrainClimbSurfaces = true;
		// Former ClimbLinkCost: the terrain ledge shortcut is allowed only when the normal NavMesh route is at least
		// this many metres longer than direct movement through the ledge face.
		[FormerlySerializedAs("ClimbLinkCost")]
		public float TerrainClimbShortcutMinPathSavings = 4f;
		[FormerlySerializedAs("ClimbLinkWidthFactor")]
		public float TerrainClimbSurfaceWidthFactor = 0.9f;
		[FormerlySerializedAs("ClimbLinkTopInset")]
		[Tooltip("How far onto the landing side a generated terrain ledge mantle ends. Lower this if zombies hoist too far inward.")]
		public float TerrainClimbLandingInset = 0.75f;

		private readonly List<SpawnPointEntry> _spawnPoints = new();
		private readonly List<SpawnCandidate> _candidates = new();
		private readonly List<int> _bestRegions = new();
		private readonly List<NetworkObject> _spawnedZombies = new();
		private int[] _regionZombieCounts;
		private int[] _regionSpawnCounts;
		private NavMeshPath _spawnValidationPath;
		private Bounds _spawnBounds;
		private System.Random _random;
		private float _nextPulseTime;
		private float _spawnRemainder;
		// A spawn pulse's whole budget is drained a few zombies per simulation tick (Settings.MaxSpawnsPerTick)
		// instead of in one frame, so a large overtime budget no longer instantiates dozens of NetworkObjects in a
		// single Update. The pulse fully drains long before the next pulse interval, so pulses never overlap.
		private int _pulseSpawnsRemaining;
		private int _pulseSpawnedCount;
		private int _filteredSpawnPointCount;
		private bool _hasSpawnBounds;
		private bool _isOvertime;
		private float _overtimeStartTime;
		private bool _isStarted;
		private bool _hasRunInitialPopulation;
		private bool _hasMatchStartReroll;
		private bool _isPrimary = true;
		private bool _loggedMissingRunner;
		private bool _loggedNotSceneAuthority;
		private bool _loggedNoSpawnPoints;

		public bool IsOvertime => _isOvertime;
		public bool HasUsableSettings => Settings != null && Settings.ZombiePrefab != null;

		private void Awake()
		{
			_isPrimary = IsPrimaryInstanceInScene();
			if (_isPrimary)
				return;

			Debug.LogError($"{nameof(ZombieOrchestrator)} expects exactly one active instance per scene. Disabling duplicate on '{name}'.", this);
			enabled = false;
		}

		private bool IsPrimaryInstanceInScene()
		{
			ZombieOrchestrator primary = null;
			foreach (GameObject root in gameObject.scene.GetRootGameObjects())
			{
				var orchestrators = root.GetComponentsInChildren<ZombieOrchestrator>(true);
				for (int i = 0; i < orchestrators.Length; i++)
				{
					var orchestrator = orchestrators[i];
					if (orchestrator == null)
						continue;
					if (primary == null || orchestrator.GetInstanceID() < primary.GetInstanceID())
						primary = orchestrator;
				}
			}

			return primary == null || primary == this;
		}

		private IEnumerator Start()
		{
			if (_isPrimary == false)
				yield break;

			if (CollectSpawnPointsOnStart)
			{
				yield return WaitForGeneratedBuildings();
				CollectSpawnPoints();
			}

			_isStarted = SpawnOnStart;
			_nextPulseTime = Time.timeSinceLevelLoad + GetPulseInterval();
		}

		private void OnDisable()
		{
			// Drop the generated terrain climb surfaces when the primary orchestrator goes away (match end, scene
			// unload) so they cannot leak into a later session if domain reload is disabled. A fresh match rebuilds.
			if (_isPrimary)
				ZombieClimbSurfaces.ClearTerrain();
		}

		private void Update()
		{
			if (_isPrimary == false)
				return;

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

			// Generation has produced spawn points, so the height snapshot is ready. Build the zombie ledge climb
			// surfaces from it. Idempotent per generated map, so re-running it picks up regeneration.
			RefreshClimbSurfaces();

			// Match-start reroll, mirroring the neutral survivor orchestrator: once the match leaves skirmish, clear
			// the skirmish horde and re-seed a fresh match-start layout. Until then, run the normal one-time initial
			// population (which happens during skirmish). Gameplay.IsRunning is guarded against the pre-Spawned()
			// window, so it is safe to read here.
			if (_hasMatchStartReroll == false && Gameplay != null && Gameplay.IsRunning)
				RerollZombiesForMatchStart();
			else
				TryRunInitialPopulation();

			if (ShouldRunSpawner() == false)
				return;

			// Finish draining an in-progress pulse a few spawns per tick before considering a new one. A pulse always
			// fully drains within a handful of frames, well before the next pulse interval, so pulses never overlap.
			if (_pulseSpawnsRemaining > 0)
			{
				DrainSpawnPulse(runner);
				return;
			}

			if (Time.timeSinceLevelLoad < _nextPulseTime)
				return;

			_nextPulseTime = Time.timeSinceLevelLoad + GetPulseInterval();
			BeginSpawnPulse(runner);
			DrainSpawnPulse(runner);
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

			// Spawn bounds are final now, so each marker's region (static, derived from position + bounds) can be
			// cached. This removes the per-candidate GetRegion call from every spawn pulse.
			CacheSpawnPointRegions();

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

				_spawnPoints.Add(new SpawnPointEntry { Point = marker, Position = navMeshPosition, Region = 0 });
				EncapsulateSpawnPoint(navMeshPosition);
			}
		}

		private void CacheSpawnPointRegions()
		{
			for (int i = 0; i < _spawnPoints.Count; i++)
			{
				SpawnPointEntry entry = _spawnPoints[i];
				entry.Region = entry.Point != null ? GetRegion(entry.Point.transform.position) : 0;
				_spawnPoints[i] = entry;
			}
		}

		[ContextMenu("Start Zombie Spawning")]
		public void StartSpawning()
		{
			_isStarted = true;
			_nextPulseTime = Time.timeSinceLevelLoad;
		}

		public void ClearZombiesNear(Vector3 center, float radius)
		{
			NetworkRunner runner = GetRunner();
			if (runner == null || runner.IsSceneAuthority == false || radius <= 0f)
				return;

			float radiusSqr = radius * radius;
			for (int i = ZombieCharacter.ActiveZombies.Count - 1; i >= 0; i--)
			{
				var zombie = ZombieCharacter.ActiveZombies[i];
				if (zombie == null)
				{
					ZombieCharacter.ActiveZombies.RemoveAt(i);
					continue;
				}
				if (zombie.Object == null || zombie.Object.IsValid == false)
					continue;
				if (FlatDistanceSqr(zombie.transform.position, center) > radiusSqr)
					continue;

				runner.Despawn(zombie.Object);
			}
		}

		public void TryRunInitialPopulation()
		{
			if (_hasRunInitialPopulation)
				return;

			NetworkRunner runner = GetRunner();
			if (runner == null || runner.IsSceneAuthority == false)
				return;
			if (HasUsableSettings == false)
				return;

			if (_spawnPoints.Count == 0)
				CollectSpawnPoints();
			if (_spawnPoints.Count == 0)
				return;

			RunInitialPopulation(runner);
		}

		// When the match transitions out of skirmish, despawn the skirmish horde and re-seed a fresh layout with the
		// match-start offset, then re-run the initial population. Mirrors NeutralSurvivorOrchestrator's match-start
		// re-roll so the live match does not inherit the skirmish-preview zombies.
		[ContextMenu("Reroll Zombies For Match Start")]
		public void RerollZombiesForMatchStart()
		{
			NetworkRunner runner = GetRunner();
			if (runner == null || runner.IsSceneAuthority == false)
				return;
			if (HasUsableSettings == false)
				return;

			_hasMatchStartReroll = true;

			ClearAllZombies(runner);

			if (_spawnPoints.Count == 0)
				CollectSpawnPoints();
			if (_spawnPoints.Count == 0)
				return;

			// Re-seed with the match-start offset so the layout differs from the skirmish preview, then allow the
			// initial population burst to run again from the clean slate.
			_random = new System.Random(GetSeed() + (Settings != null ? Settings.MatchStartSeedOffset : 0));
			_hasRunInitialPopulation = false;
			_spawnRemainder = 0f;
			_pulseSpawnsRemaining = 0; // drop any in-progress pulse so it cannot keep draining after the reroll wipe
			_nextPulseTime = Time.timeSinceLevelLoad + GetPulseInterval();

			RunInitialPopulation(runner);
		}

		private void ClearAllZombies(NetworkRunner runner)
		{
			if (runner == null || runner.IsSceneAuthority == false)
				return;

			for (int i = ZombieCharacter.ActiveZombies.Count - 1; i >= 0; i--)
			{
				var zombie = ZombieCharacter.ActiveZombies[i];
				if (zombie == null)
				{
					ZombieCharacter.ActiveZombies.RemoveAt(i);
					continue;
				}
				if (zombie.Object == null || zombie.Object.IsValid == false)
					continue;

				runner.Despawn(zombie.Object);
			}

			_spawnedZombies.Clear();
		}

		[ContextMenu("Start Overtime")]
		public void StartOvertime()
		{
			if (_isOvertime || Settings == null)
				return;

			_isOvertime = true;
			_overtimeStartTime = Time.timeSinceLevelLoad;
			// Re-arm the pulse cadence so the first overtime pulse lands one interval after overtime begins, rather
			// than immediately (timer already elapsed) or several seconds late (timer mid-interval). See the audit's
			// "Overtime Spawns Arrive As One-Frame Bursts" finding.
			_nextPulseTime = Time.timeSinceLevelLoad + GetPulseInterval();
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

		// Compute this pulse's budget and candidate set once, then hand the budget to DrainSpawnPulse to spend across
		// ticks. Candidates (and their cached NavMesh-validated positions/regions) are built a single time per pulse.
		private void BeginSpawnPulse(NetworkRunner runner)
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

			_pulseSpawnsRemaining = spawnBudget;
			_pulseSpawnedCount = 0;
		}

		// Spend up to Settings.MaxSpawnsPerTick of the current pulse's budget this tick. Called every Update until the
		// pulse drains, spreading the instantiation work (and its Fusion replication / CharacterSeparation / physics
		// ignore-pair costs) across frames instead of one same-frame burst.
		private void DrainSpawnPulse(NetworkRunner runner)
		{
			if (_pulseSpawnsRemaining <= 0)
				return;

			int maxPerTick = Settings != null ? Mathf.Max(1, Settings.MaxSpawnsPerTick) : 4;
			int spawnedThisTick = 0;

			while (spawnedThisTick < maxPerTick && _pulseSpawnsRemaining > 0 && _candidates.Count > 0)
			{
				int candidateIndex = ChooseCandidateIndex();
				if (candidateIndex < 0 || candidateIndex >= _candidates.Count)
					break;

				SpawnCandidate candidate = _candidates[candidateIndex];
				SpawnZombie(runner, candidate);
				spawnedThisTick++;
				_pulseSpawnsRemaining--;
				_pulseSpawnedCount++;

				candidate.Remaining--;
				if (candidate.Remaining <= 0)
					_candidates.RemoveAt(candidateIndex);
				else
					_candidates[candidateIndex] = candidate;
			}

			// The pulse is finished when the budget is spent, candidates run out, or no candidate could be chosen this
			// tick. Preserve the original remainder-reset: if a whole pulse spawned nothing (every marker blocked),
			// drop the fractional spawn accumulator so blocked pulses cannot build a pent-up burst.
			if (_pulseSpawnsRemaining <= 0 || _candidates.Count == 0 || spawnedThisTick == 0)
			{
				if (_pulseSpawnedCount == 0)
					_spawnRemainder = 0f;
				_pulseSpawnsRemaining = 0;
			}
		}

		private void RunInitialPopulation(NetworkRunner runner)
		{
			_hasRunInitialPopulation = true;

			if (Settings == null || Settings.InitialPopulation <= 0f)
				return;

			int targetCount = Mathf.RoundToInt(Settings.StartMaxZombies * Mathf.Clamp01(Settings.InitialPopulation));
			int spawnBudget = Mathf.Max(0, targetCount - CountAliveZombies());
			if (spawnBudget <= 0)
				return;

			BuildSpawnCandidates();
			if (_candidates.Count == 0)
				return;

			for (int i = 0; i < spawnBudget && _candidates.Count > 0; i++)
			{
				int candidateIndex = ChooseCandidateIndex();
				if (candidateIndex < 0 || candidateIndex >= _candidates.Count)
					break;

				SpawnCandidate candidate = _candidates[candidateIndex];
				SpawnZombie(runner, candidate);

				candidate.Remaining--;
				if (candidate.Remaining <= 0)
					_candidates.RemoveAt(candidateIndex);
				else
					_candidates[candidateIndex] = candidate;
			}
		}

		private void SpawnZombie(NetworkRunner runner, SpawnCandidate candidate)
		{
			ZombieStats stats = _isOvertime ? GetOvertimeSpawnStats() : GetCurrentStats();
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
				SpawnPointEntry entry = _spawnPoints[i];
				var point = entry.Point;
				if (point == null)
					continue;
				// Survivor-proximity blocking is dynamic and must stay per pulse. The NavMesh position/region are
				// static and cached at collection, so they are reused instead of re-validated every pulse.
				if (IsSpawnPointBlocked(point))
					continue;

				_candidates.Add(new SpawnCandidate
				{
					Point = point,
					Position = entry.Position,
					Rotation = point.transform.rotation,
					Region = entry.Region,
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

			// Reuse the count buffers across calls (this runs per-candidate during a pulse) instead of allocating two
			// int[] every time. Resized only when the region grid changes; cleared each computation.
			if (_regionZombieCounts == null || _regionZombieCounts.Length != regionCount)
			{
				_regionZombieCounts = new int[regionCount];
				_regionSpawnCounts = new int[regionCount];
			}
			System.Array.Clear(_regionZombieCounts, 0, regionCount);
			System.Array.Clear(_regionSpawnCounts, 0, regionCount);

			int[] zombieCounts = _regionZombieCounts;
			int[] spawnCounts = _regionSpawnCounts;

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
				if (survivor == null || survivor.Object == null || survivor.Object.IsValid == false)
					continue;
				if (survivor.Health == null || survivor.Health.IsAlive == false)
					continue;
				// Only player-owned survivors keep zombies from spawning. Neutral survivors are meant to be
				// threatened by the horde, so they must not suppress nearby zombie spawns.
				if (CharacterFactionUtility.IsPlayerOwnedSurvivor(survivor) == false)
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

			if (TryGetGameplayState(out EGameplayState state) == false)
				return SpawnDuringSkirmish;
			if (state == EGameplayState.Running)
				return true;
			if (state == EGameplayState.Skirmish)
				return SpawnDuringSkirmish;

			return false;
		}

		// Gameplay.State (a [Networked] property) throws until the gameplay object is Spawned(). Treat an unspawned
		// gameplay as "no readable state yet" so spawn logic falls back to its pre-match behaviour instead of crashing
		// during the brief window before Fusion spawns the scene gameplay object.
		private bool TryGetGameplayState(out EGameplayState state)
		{
			state = default;
			ResolveGameplay();
			if (Gameplay == null || Gameplay.Object == null || Gameplay.Object.IsValid == false)
				return false;

			state = Gameplay.State;
			return true;
		}

		private int GetSpawnBudget()
		{
			float interval = GetPulseInterval();
			// Spawn rate is per connected player: every additional player raises the effective rate by the
			// configured per-player rate. A two-player match spawns twice as fast as a one-player match.
			float ratePerPlayer = _isOvertime ? Settings.OvertimeSpawnRatePerMinute : Mathf.Lerp(Settings.StartSpawnRatePerMinute, Settings.EndSpawnRatePerMinute, GetProgress01());
			float rate = Mathf.Max(0f, ratePerPlayer) * Mathf.Max(1, GetConnectedPlayerCount());
			_spawnRemainder += rate * interval / 60f;

			int budget = Mathf.FloorToInt(_spawnRemainder);
			_spawnRemainder -= budget;

			return Mathf.Max(0, budget);
		}

		private int GetConnectedPlayerCount()
		{
			// PlayerData is also a [Networked] property; guard the pre-Spawned() window like the State reads.
			if (Gameplay == null || Gameplay.Object == null || Gameplay.Object.IsValid == false)
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

		private ZombieStats GetOvertimeSpawnStats()
		{
			ZombieStats stats = GetOvertimeStats();
			float increasePerStep = Mathf.Max(0f, Settings.OvertimeNewZombieHealthIncreasePer10Seconds);
			if (increasePerStep <= 0f)
				return stats;

			float elapsed = Mathf.Max(0f, Time.timeSinceLevelLoad - _overtimeStartTime);
			int completedSteps = Mathf.FloorToInt(elapsed / 10f);
			stats.MaxHealth += completedSteps * increasePerStep;
			return stats;
		}

		private float GetProgress01()
		{
			float duration = GetMatchDuration();
			if (duration <= 0f)
				return 0f;

			if (TryGetGameplayState(out EGameplayState state))
			{
				if (state == EGameplayState.Skirmish && Settings.ScaleDuringSkirmish == false)
					return 0f;

				if (state == EGameplayState.Running)
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

		// Generate broad zombie-climbable faces from the generated height snapshot. Runs only here on scene authority
		// (zombie AI is authority-only, so the generated terrain registry is needed nowhere else) and is idempotent per
		// generated map. HeightGenerator is already resolved by CollectSpawnPoints. See ZombieClimbSurfaces.
		private void RefreshClimbSurfaces()
		{
			RefreshRoadGridSnapshot();

			if (BuildTerrainClimbSurfaces == false)
				return;

			if (HeightGenerator == null)
				GetGeneratedHeightRoot();
			if (HeightGenerator == null)
				return;

			if (HeightGenerator.TryGetHeightSnapshot(out WorldHeightSnapshot snapshot) == false || snapshot.IsValid == false)
				return;

			ZombieClimbSurfaces.BuildTerrain(snapshot, new ZombieClimbSurfaces.TerrainBuildConfig
			{
				WidthFactor = TerrainClimbSurfaceWidthFactor,
				LandingInset = TerrainClimbLandingInset,
				ShortcutMinPathSavings = TerrainClimbShortcutMinPathSavings,
			});
		}

		private void RefreshRoadGridSnapshot()
		{
			RoadGridGenerator roadGenerator = null;
			if (BuildingGenerator != null)
				roadGenerator = BuildingGenerator.RoadGenerator;
			if (roadGenerator == null)
				roadGenerator = GetComponent<RoadGridGenerator>();
			if (roadGenerator == null)
				roadGenerator = FindObjectOfType<RoadGridGenerator>();

			if (roadGenerator != null && roadGenerator.TryGetWorldGridSnapshot(out WorldGridSnapshot snapshot) && snapshot.IsValid)
			{
				ZombieClimbSurfaces.SetRoadGrid(snapshot);
				return;
			}

			ZombieClimbSurfaces.ClearRoadGrid();
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
