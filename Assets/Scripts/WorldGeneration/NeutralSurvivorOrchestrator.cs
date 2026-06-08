using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.AI;

namespace SimpleFPS
{
	public sealed class NeutralSurvivorOrchestrator : MonoBehaviour
	{
		private struct ReservedSpawnPosition
		{
			public Vector3 Position;
			public float MinDistance;
			public float PatrolRadius;
		}

		private static readonly Dictionary<int, List<ReservedSpawnPosition>> ReservedSpawnPositionsByScene = new();

		private struct SpawnMarker
		{
			public NeutralSurvivorSpawnPoint Point;
			public Vector3 Position;
			public int SpawnCount;
		}

		[Header("Setup")]
		public BuildingPlacementGenerator BuildingGenerator;
		public RoadGridGenerator RoadGenerator;
		public NeutralSurvivorSpawnSettings Settings;
		[Tooltip("Optional. Neutral survivors are identical to player survivors (neutral vs owned is just a runtime OwnerRef), so this can be left empty to spawn the same prefab as Gameplay.SurvivorPrefab.")]
		public Survivor NeutralSurvivorPrefab;
		public NetworkRunner Runner;
		public Gameplay Gameplay;

		[Header("Runtime")]
		public bool FindRunnerIfMissing = true;
		public bool FindGameplayIfMissing = true;
		public bool CollectSpawnPointsOnStart = true;
		public bool SpawnOnStart = true;
		public bool ClearBeforeSpawn;

		private readonly List<SpawnMarker> _validMarkers = new();
		private readonly List<SpawnMarker> _selectedMarkers = new();
		private readonly List<RoamDestination> _roamDestinations = new();
		private readonly List<Survivor> _spawnedNeutralSurvivors = new();
		private readonly List<Survivor> _recruitmentCandidates = new();
		private readonly List<ReservedSpawnPosition> _reservedSpawnPositions = new();
		private NavMeshPath _spawnValidationPath;
		private System.Random _random;
		private float _nextRecruitmentCheckTime;
		private bool _isWorldReady;
		private bool _hasSpawned;
		private bool _hasMatchStartReroll;
		private bool _loggedMissingRunner;
		private bool _loggedNotSceneAuthority;
		private bool _loggedMissingSetup;
		private bool _loggedNoMarkers;
		private bool _isPrimaryOrchestrator = true;
		private int _runtimeDesiredNeutralSurvivorCount = -1;

		public void SetRuntimeDesiredNeutralSurvivorCount(int count)
		{
			_runtimeDesiredNeutralSurvivorCount = Mathf.Max(0, count);
		}

		private void Awake()
		{
			_isPrimaryOrchestrator = IsPrimaryOrchestratorInScene();
			if (_isPrimaryOrchestrator)
				return;

			Debug.LogError($"{nameof(NeutralSurvivorOrchestrator)} expects exactly one active instance per gameplay scene. Disabling duplicate on '{name}'.", this);
			enabled = false;
		}

		private IEnumerator Start()
		{
			if (_isPrimaryOrchestrator == false)
				yield break;

			yield return WaitForGeneratedWorld();
			_isWorldReady = true;

			if (CollectSpawnPointsOnStart)
				CollectSpawnPoints();
		}

		private void Update()
		{
			if (_isPrimaryOrchestrator == false)
				return;

			NetworkRunner runner = GetRunner();
			if (runner == null)
			{
				if (_loggedMissingRunner == false)
				{
					Debug.LogWarning($"{nameof(NeutralSurvivorOrchestrator)} could not find a {nameof(NetworkRunner)} yet.", this);
					_loggedMissingRunner = true;
				}
				return;
			}

			_loggedMissingRunner = false;

			if (runner.IsSceneAuthority == false)
			{
				if (_loggedNotSceneAuthority == false)
				{
					Debug.Log($"{nameof(NeutralSurvivorOrchestrator)} found runner on a non-scene-authority peer; neutral survivor spawning and recruitment will run on scene authority only.", this);
					_loggedNotSceneAuthority = true;
				}
				return;
			}

			_loggedNotSceneAuthority = false;
			ResolveGameplay();

			if (SpawnOnStart && _isWorldReady)
			{
				// Skirmish pass: spawn against whatever player spawns are in use so far (usually just the host).
				// Match-start pass: once the match begins all player spawns are assigned, so re-roll the layout
				// with a seed offset and prune against every in-use player spawn. If the match is already running
				// the first time we spawn, go straight to the match-start layout instead of flickering a skirmish set.
				if (_hasMatchStartReroll == false && IsMatchRunning())
					RerollNeutralSurvivorsForMatchStart();
				else if (_hasSpawned == false)
					SpawnNeutralSurvivors();
			}

			TickRecruitment();
		}

		private void OnDestroy()
		{
			ClearReservedSpawnPositions();
		}

		private bool IsPrimaryOrchestratorInScene()
		{
			NeutralSurvivorOrchestrator primary = null;
			foreach (GameObject root in gameObject.scene.GetRootGameObjects())
			{
				var orchestrators = root.GetComponentsInChildren<NeutralSurvivorOrchestrator>(true);
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

		[ContextMenu("Collect Neutral Survivor Spawn Points")]
		public void CollectSpawnPoints()
		{
			_validMarkers.Clear();
			var seen = new HashSet<NeutralSurvivorSpawnPoint>();
			CollectSpawnPointsFromRoot(GetGeneratedBuildingRoot(), seen);
			CollectSpawnPointsFromRoot(GetGeneratedRoadRoot(), seen);
			BuildRoamDestinations();
			_random = new System.Random(GetSeed());
		}

		// Roam destinations are every valid dynamic-spawn marker, whether or not it was selected to spawn survivors,
		// so roaming neutrals can wander to unused dynamic spawns too.
		private void BuildRoamDestinations()
		{
			_roamDestinations.Clear();
			for (int i = 0; i < _validMarkers.Count; i++)
			{
				var point = _validMarkers[i].Point;
				if (point == null || point.DynamicSpawn == false)
					continue;

				_roamDestinations.Add(new RoamDestination(_validMarkers[i].Position, Mathf.Max(0f, point.PatrolRadius)));
			}
		}

		[ContextMenu("Spawn Neutral Survivors")]
		public void SpawnNeutralSurvivors()
		{
			if (_hasSpawned && ClearBeforeSpawn == false)
				return;

			if (CanSpawn(out NetworkRunner runner) == false)
				return;

			if (ClearBeforeSpawn)
			{
				ClearSpawnedNeutralSurvivors(runner);
				ClearReservedSpawnPositions();
			}

			if (_validMarkers.Count == 0)
				CollectSpawnPoints();

			if (_validMarkers.Count == 0)
			{
				if (_loggedNoMarkers == false)
				{
					Debug.LogWarning($"{nameof(NeutralSurvivorOrchestrator)} found no valid {nameof(NeutralSurvivorSpawnPoint)} markers under generated building or road roots.", this);
					_loggedNoMarkers = true;
				}
				_hasSpawned = true;
				return;
			}

			_loggedNoMarkers = false;
			SelectSpawnMarkers();

			for (int i = 0; i < _selectedMarkers.Count; i++)
			{
				SpawnMarker marker = _selectedMarkers[i];
				for (int j = 0; j < marker.SpawnCount; j++)
				{
					SpawnNeutralSurvivor(runner, marker);
				}
			}

			_hasSpawned = true;
		}

		private bool IsMatchRunning()
		{
			ResolveGameplay();
			return Gameplay != null && Gameplay.State == EGameplayState.Running;
		}

		// When the match transitions out of skirmish, all participating player spawns have been assigned.
		// Despawn the skirmish-pass neutrals, drop their spacing reservations, and select a fresh layout with a
		// seed offset so it differs from the skirmish preview and avoids every in-use player spawn.
		[ContextMenu("Reroll Neutral Survivors For Match Start")]
		public void RerollNeutralSurvivorsForMatchStart()
		{
			if (CanSpawn(out NetworkRunner runner) == false)
				return;

			_hasMatchStartReroll = true;

			ClearSpawnedNeutralSurvivors(runner);
			ClearReservedSpawnPositions();

			// Collect first if needed (CollectSpawnPoints reseeds _random from the base seed), then apply the
			// match-start offset so SpawnNeutralSurvivors does not recollect and clobber the re-roll seed.
			if (_validMarkers.Count == 0)
				CollectSpawnPoints();

			_random = new System.Random(GetSeed() + (Settings != null ? Settings.MatchStartSeedOffset : 0));

			SpawnNeutralSurvivors();
		}

		private bool CanSpawn(out NetworkRunner runner)
		{
			runner = GetRunner();
			if (runner == null || runner.IsSceneAuthority == false)
				return false;
			if (Settings == null || GetNeutralSurvivorPrefab() == null)
			{
				if (_loggedMissingSetup == false)
				{
					Debug.LogWarning($"{nameof(NeutralSurvivorOrchestrator)} needs settings and a survivor prefab (its own or {nameof(Gameplay)}.{nameof(Gameplay.SurvivorPrefab)}) before spawning.", this);
					_loggedMissingSetup = true;
				}
				return false;
			}

			_loggedMissingSetup = false;
			return true;
		}

		// Neutral survivors use the same prefab as player survivors (the only difference is the runtime OwnerRef).
		// The orchestrator's own field is an optional override; when empty it falls back to Gameplay.SurvivorPrefab,
		// so a separate neutral prefab is not required.
		private Survivor GetNeutralSurvivorPrefab()
		{
			if (NeutralSurvivorPrefab != null)
				return NeutralSurvivorPrefab;

			ResolveGameplay();
			return Gameplay != null ? Gameplay.SurvivorPrefab : null;
		}

		private void SpawnNeutralSurvivor(NetworkRunner runner, SpawnMarker marker)
		{
			Survivor prefab = GetNeutralSurvivorPrefab();
			if (prefab == null)
				return;

			Vector3 patrolCenter = marker.Position;
			float patrolRadius = marker.Point != null ? Mathf.Max(0f, marker.Point.PatrolRadius) : 0f;
			bool isDynamic = marker.Point != null && marker.Point.DynamicSpawn;
			Quaternion rotation = marker.Point != null ? marker.Point.transform.rotation : Quaternion.identity;

			runner.Spawn(prefab, marker.Position, rotation, PlayerRef.None,
				(spawnRunner, obj) =>
				{
					var survivor = obj.GetComponent<Survivor>();
					if (survivor == null)
						return;

					survivor.OwnerRef = PlayerRef.None;
					survivor.CharacterIndex = -1;
					AddSpawnedNeutral(survivor);
					StartCoroutine(InitializeNeutralAfterSpawn(survivor, patrolCenter, patrolRadius, isDynamic));
				});
		}

		private IEnumerator InitializeNeutralAfterSpawn(Survivor survivor, Vector3 patrolCenter, float patrolRadius, bool isDynamic)
		{
			yield return null;

			if (survivor == null || survivor.Object == null || survivor.Object.IsValid == false)
				yield break;
			if (survivor.Health == null || survivor.Health.IsAlive == false)
				yield break;
			if (survivor.IsNeutral == false)
				yield break;

			var neutral = survivor.GetComponent<NeutralSurvivor>();
			if (neutral == null)
				neutral = survivor.gameObject.AddComponent<NeutralSurvivor>();
			neutral.Initialize(survivor, patrolCenter, patrolRadius);
			neutral.ApplyNeutralStats(Settings);

			survivor.SetNonCombatAISettings(SurvivorNonCombatAISettings.Default);
			survivor.SetCombatAISettings(SurvivorCombatAISettings.Default);

			// Dynamic-spawn survivors roam between dynamic areas instead of staying put. They are still registered
			// for recruitment below like any neutral, so players can recruit them mid-roam.
			bool roams = isDynamic && _roamDestinations.Count > 0;

			if (patrolRadius > 0f && SurvivorNonCombatAI.TryBuildAssignedAreaPatrolPoints(survivor, patrolCenter, patrolRadius, out Vector3[] patrolPoints))
			{
				Vector3 entryPoint = patrolPoints != null && patrolPoints.Length > 0 ? patrolPoints[0] : patrolCenter;
				survivor.SetAI(roams
					? SurvivorNonCombatAI.RoamArea(survivor, patrolCenter, patrolRadius, entryPoint, patrolPoints, survivor.NonCombatAISettings)
					: SurvivorNonCombatAI.AssignedArea(survivor, patrolCenter, patrolRadius, entryPoint, patrolPoints, survivor.NonCombatAISettings));
			}
			else
			{
				survivor.SetIdleAI();
			}

			if (roams)
				neutral.EnableRoaming(_roamDestinations);

			ResolveGameplay();
			Gameplay?.RegisterSurvivor(survivor);
		}

		private void TickRecruitment()
		{
			if (Gameplay == null || Time.timeSinceLevelLoad < _nextRecruitmentCheckTime)
				return;

			float interval = Settings != null ? Settings.RecruitmentCheckInterval : 0.25f;
			_nextRecruitmentCheckTime = Time.timeSinceLevelLoad + Mathf.Max(0.05f, interval);

			float radius = Settings != null ? Settings.RecruitmentRadius : 3f;
			float radiusSqr = Mathf.Max(0.01f, radius * radius);

			_recruitmentCandidates.Clear();
			if (Gameplay.NeutralSurvivors != null && Gameplay.NeutralSurvivors.Count > 0)
			{
				for (int i = 0; i < Gameplay.NeutralSurvivors.Count; i++)
				{
					_recruitmentCandidates.Add(Gameplay.NeutralSurvivors[i]);
				}
			}
			else
			{
				for (int i = 0; i < _spawnedNeutralSurvivors.Count; i++)
				{
					_recruitmentCandidates.Add(_spawnedNeutralSurvivors[i]);
				}
			}

			for (int i = _recruitmentCandidates.Count - 1; i >= 0; i--)
			{
				Survivor neutral = _recruitmentCandidates[i];
				if (neutral == null || neutral.Object == null || neutral.Object.IsValid == false)
				{
					_spawnedNeutralSurvivors.Remove(neutral);
					continue;
				}

				if (neutral.Health == null || neutral.Health.IsAlive == false || neutral.IsNeutral == false)
				{
					_spawnedNeutralSurvivors.Remove(neutral);
					continue;
				}

				if (TryFindRecruiter(neutral, radiusSqr, out Survivor recruiter) &&
				    Gameplay.TryRecruitNeutralSurvivor(neutral, recruiter))
				{
					neutral.GetComponent<NeutralSurvivor>()?.RestoreOriginalStats();
					_spawnedNeutralSurvivors.Remove(neutral);
				}
			}

			_recruitmentCandidates.Clear();
		}

		private bool TryFindRecruiter(Survivor neutral, float radiusSqr, out Survivor recruiter)
		{
			recruiter = null;
			float closestDistanceSqr = float.MaxValue;

			for (int i = CharacterSensor.ActiveSensors.Count - 1; i >= 0; i--)
			{
				var sensor = CharacterSensor.ActiveSensors[i];
				if (sensor == null)
				{
					CharacterSensor.ActiveSensors.RemoveAt(i);
					continue;
				}

				var candidate = sensor.Survivor;
				if (candidate == null || candidate == neutral)
					continue;
				if (CharacterFactionUtility.IsPlayerOwnedSurvivor(candidate) == false)
					continue;
				if (candidate.Health == null || candidate.Health.IsAlive == false)
					continue;

				float distanceSqr = FlatDistanceSqr(candidate.transform.position, neutral.transform.position);
				if (distanceSqr > radiusSqr || distanceSqr >= closestDistanceSqr)
					continue;

				recruiter = candidate;
				closestDistanceSqr = distanceSqr;
			}

			return recruiter != null;
		}

		private void CollectSpawnPointsFromRoot(Transform root, HashSet<NeutralSurvivorSpawnPoint> seen)
		{
			if (root == null)
				return;

			var markers = root.GetComponentsInChildren<NeutralSurvivorSpawnPoint>(true);
			for (int i = 0; i < markers.Length; i++)
			{
				var marker = markers[i];
				if (marker == null || seen.Add(marker) == false)
					continue;
				if (TryGetUsableSpawnPointPosition(marker, out Vector3 position) == false)
					continue;

				_validMarkers.Add(new SpawnMarker
				{
					Point = marker,
					Position = position,
				});
			}
		}

		private bool TryGetUsableSpawnPointPosition(NeutralSurvivorSpawnPoint point, out Vector3 position)
		{
			position = default;
			if (point == null || Settings == null)
				return false;

			float sampleDistance = Mathf.Max(0.1f, Settings.SpawnNavMeshSampleDistance);
			if (NavMesh.SamplePosition(point.transform.position, out var hit, sampleDistance, NavMesh.AllAreas) == false)
				return false;

			position = hit.position;
			return IsConnectedNavMeshLargeEnough(position, Settings.MinimumSpawnConnectedNavMeshRadius, sampleDistance);
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

		private void SelectSpawnMarkers()
		{
			_selectedMarkers.Clear();
			if (_validMarkers.Count == 0 || Settings == null)
				return;

			if (_random == null)
				_random = new System.Random(GetSeed());

			int desiredSurvivorCount = GetDesiredNeutralSurvivorCount();
			if (desiredSurvivorCount == 0)
				return;

			var candidates = new List<SpawnMarker>(_validMarkers);
			Shuffle(candidates, _random);

			float minDistance = GetEffectiveMinDistanceBetweenSelectedSpawnPoints();
			List<ReservedSpawnPosition> reservedPositions = GetReservedSpawnPositionsForScene();

			// Select spaced markers and accumulate their rolled survivor counts until we reach the desired total.
			// The final marker is capped so the total lands exactly on the desired count; if markers run out first
			// we simply spawn fewer.
			int selectedSurvivorCount = 0;
			for (int i = 0; i < candidates.Count && selectedSurvivorCount < desiredSurvivorCount; i++)
			{
				SpawnMarker candidate = candidates[i];
				if (CanMarkerSpawnAnySurvivors(candidate.Point) == false)
					continue;
				float candidateRadius = GetMarkerSpacingRadius(candidate.Point);
				if (IsFarEnoughFromSelected(candidate.Position, candidateRadius, minDistance, reservedPositions) == false)
					continue;
				if (IsFarEnoughFromActivePlayerSpawns(candidate.Position, candidateRadius) == false)
					continue;

				int markerCount = GetSpawnCount(candidate.Point);
				if (markerCount <= 0)
					continue;
				markerCount = Mathf.Min(markerCount, desiredSurvivorCount - selectedSurvivorCount);
				candidate.SpawnCount = markerCount;

				_selectedMarkers.Add(candidate);
				selectedSurvivorCount += markerCount;
				var reserved = new ReservedSpawnPosition
				{
					Position = candidate.Position,
					MinDistance = minDistance,
					PatrolRadius = candidateRadius,
				};
				reservedPositions.Add(reserved);
				_reservedSpawnPositions.Add(reserved);
			}
		}

		private bool IsFarEnoughFromSelected(Vector3 position, float patrolRadius, float minDistance, List<ReservedSpawnPosition> reservedPositions)
		{
			for (int i = 0; i < _selectedMarkers.Count; i++)
			{
				float requiredDistance = minDistance + patrolRadius + GetMarkerSpacingRadius(_selectedMarkers[i].Point);
				if (FlatDistanceSqr(position, _selectedMarkers[i].Position) < requiredDistance * requiredDistance)
					return false;
			}

			if (reservedPositions != null)
			{
				for (int i = 0; i < reservedPositions.Count; i++)
				{
					float strictestDistance = Mathf.Max(minDistance, reservedPositions[i].MinDistance);
					float requiredDistance = strictestDistance + patrolRadius + reservedPositions[i].PatrolRadius;
					if (requiredDistance <= 0f)
						continue;
					if (FlatDistanceSqr(position, reservedPositions[i].Position) < requiredDistance * requiredDistance)
						return false;
				}
			}

			return true;
		}

		private bool IsFarEnoughFromActivePlayerSpawns(Vector3 position, float patrolRadius)
		{
			if (Settings == null || Settings.MinDistanceToActivePlayerSpawns <= 0f)
				return true;

			ResolveGameplay();
			if (Gameplay == null)
				return true;

			float requiredDistance = Settings.MinDistanceToActivePlayerSpawns + Mathf.Max(0f, patrolRadius);
			return Gameplay.IsWithinActivePlayerSpawn(position, requiredDistance) == false;
		}

		private static bool CanMarkerSpawnAnySurvivors(NeutralSurvivorSpawnPoint point)
		{
			if (point == null)
				return false;

			return Mathf.Max(point.MinSpawnCount, point.MaxSpawnCount) > 0;
		}

		private static float GetMarkerSpacingRadius(NeutralSurvivorSpawnPoint point)
		{
			return point != null ? Mathf.Max(0f, point.PatrolRadius) : 0f;
		}

		private float GetEffectiveMinDistanceBetweenSelectedSpawnPoints()
		{
			float minDistance = Settings != null ? Mathf.Max(0f, Settings.MinDistanceBetweenSelectedSpawnPoints) : 0f;
			foreach (GameObject root in gameObject.scene.GetRootGameObjects())
			{
				var orchestrators = root.GetComponentsInChildren<NeutralSurvivorOrchestrator>(true);
				for (int i = 0; i < orchestrators.Length; i++)
				{
					var orchestrator = orchestrators[i];
					if (orchestrator != null && orchestrator.Settings != null)
						minDistance = Mathf.Max(minDistance, orchestrator.Settings.MinDistanceBetweenSelectedSpawnPoints);
				}
			}

			return minDistance;
		}

		private List<ReservedSpawnPosition> GetReservedSpawnPositionsForScene()
		{
			int sceneHandle = gameObject.scene.handle;
			if (ReservedSpawnPositionsByScene.TryGetValue(sceneHandle, out var positions) == false)
			{
				positions = new List<ReservedSpawnPosition>();
				ReservedSpawnPositionsByScene[sceneHandle] = positions;
			}

			return positions;
		}

		private void ClearReservedSpawnPositions()
		{
			if (_reservedSpawnPositions.Count == 0)
				return;

			int sceneHandle = gameObject.scene.handle;
			if (ReservedSpawnPositionsByScene.TryGetValue(sceneHandle, out var positions))
			{
				for (int i = 0; i < _reservedSpawnPositions.Count; i++)
				{
					Vector3 reserved = _reservedSpawnPositions[i].Position;
					for (int j = positions.Count - 1; j >= 0; j--)
					{
						if (FlatDistanceSqr(reserved, positions[j].Position) < 0.01f)
							positions.RemoveAt(j);
					}
				}

				if (positions.Count == 0)
					ReservedSpawnPositionsByScene.Remove(sceneHandle);
			}

			_reservedSpawnPositions.Clear();
		}

		private int GetSpawnCount(NeutralSurvivorSpawnPoint point)
		{
			if (point == null)
				return 0;

			int min = Mathf.Max(0, point.MinSpawnCount);
			int max = Mathf.Max(min, point.MaxSpawnCount);
			if (max <= 0)
				return 0;
			if (min == max)
				return min;

			if (_random == null)
				_random = new System.Random(GetSeed());
			return _random.Next(min, max + 1);
		}

		private int GetDesiredNeutralSurvivorCount()
		{
			if (_runtimeDesiredNeutralSurvivorCount >= 0)
				return _runtimeDesiredNeutralSurvivorCount;

			return Settings != null ? Mathf.Max(0, Settings.DesiredNeutralSurvivorCount) : 0;
		}

		private void AddSpawnedNeutral(Survivor survivor)
		{
			if (survivor != null && _spawnedNeutralSurvivors.Contains(survivor) == false)
				_spawnedNeutralSurvivors.Add(survivor);
		}

		private void ClearSpawnedNeutralSurvivors(NetworkRunner runner)
		{
			for (int i = _spawnedNeutralSurvivors.Count - 1; i >= 0; i--)
			{
				Survivor survivor = _spawnedNeutralSurvivors[i];
				if (survivor != null && survivor.Object != null)
					runner.Despawn(survivor.Object);
			}

			_spawnedNeutralSurvivors.Clear();
			_hasSpawned = false;
		}

		private IEnumerator WaitForGeneratedWorld()
		{
			ResolveGenerators();
			while (Application.isPlaying &&
			       ((RoadGenerator != null && RoadGenerator.GenerateOnStart && RoadGenerator.IsGenerationComplete == false) ||
			        (BuildingGenerator != null && BuildingGenerator.GenerateOnStart && BuildingGenerator.IsGenerationComplete == false)))
			{
				yield return null;
			}
		}

		private void ResolveGenerators()
		{
			if (BuildingGenerator == null)
				BuildingGenerator = GetComponent<BuildingPlacementGenerator>() ?? FindObjectOfType<BuildingPlacementGenerator>();
			if (RoadGenerator == null && BuildingGenerator != null)
				RoadGenerator = BuildingGenerator.RoadGenerator;
			if (RoadGenerator == null)
				RoadGenerator = GetComponent<RoadGridGenerator>() ?? FindObjectOfType<RoadGridGenerator>();
		}

		private Transform GetGeneratedBuildingRoot()
		{
			ResolveGenerators();
			return BuildingGenerator != null ? BuildingGenerator.GeneratedRoot : null;
		}

		private Transform GetGeneratedRoadRoot()
		{
			ResolveGenerators();
			return RoadGenerator != null ? RoadGenerator.GeneratedRoot : null;
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

		private int GetSeed()
		{
			ResolveGameplay();
			ResolveGenerators();

			int seed = Gameplay != null ? Gameplay.WorldSeed : 0;
			if (seed == 0 && RoadGenerator != null)
				seed = RoadGenerator.Seed;

			return seed + (Settings != null ? Settings.SeedOffset : 0);
		}

		private static void Shuffle<T>(List<T> list, System.Random random)
		{
			if (random == null)
				return;

			for (int i = list.Count - 1; i > 0; i--)
			{
				int j = random.Next(i + 1);
				(list[i], list[j]) = (list[j], list[i]);
			}
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}
	}
}
