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
		}

		[Header("Setup")]
		public BuildingPlacementGenerator BuildingGenerator;
		public RoadGridGenerator RoadGenerator;
		public NeutralSurvivorSpawnSettings Settings;
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
		private readonly List<Survivor> _spawnedNeutralSurvivors = new();
		private readonly List<Survivor> _recruitmentCandidates = new();
		private readonly List<ReservedSpawnPosition> _reservedSpawnPositions = new();
		private NavMeshPath _spawnValidationPath;
		private System.Random _random;
		private float _nextRecruitmentCheckTime;
		private bool _isWorldReady;
		private bool _hasSpawned;
		private bool _loggedMissingRunner;
		private bool _loggedNotSceneAuthority;
		private bool _loggedMissingSetup;
		private bool _loggedNoMarkers;
		private bool _isPrimaryOrchestrator = true;

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

			if (SpawnOnStart && _hasSpawned == false && _isWorldReady)
				SpawnNeutralSurvivors();

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
			_random = new System.Random(GetSeed());
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
				int spawnCount = GetSpawnCount(marker.Point);
				for (int j = 0; j < spawnCount; j++)
				{
					SpawnNeutralSurvivor(runner, marker);
				}
			}

			_hasSpawned = true;
		}

		private bool CanSpawn(out NetworkRunner runner)
		{
			runner = GetRunner();
			if (runner == null || runner.IsSceneAuthority == false)
				return false;
			if (Settings == null || NeutralSurvivorPrefab == null)
			{
				if (_loggedMissingSetup == false)
				{
					Debug.LogWarning($"{nameof(NeutralSurvivorOrchestrator)} needs both settings and a neutral survivor prefab before spawning.", this);
					_loggedMissingSetup = true;
				}
				return false;
			}

			_loggedMissingSetup = false;
			return true;
		}

		private void SpawnNeutralSurvivor(NetworkRunner runner, SpawnMarker marker)
		{
			Vector3 patrolCenter = marker.Position;
			float patrolRadius = marker.Point != null ? Mathf.Max(0f, marker.Point.PatrolRadius) : 0f;
			Quaternion rotation = marker.Point != null ? marker.Point.transform.rotation : Quaternion.identity;

			runner.Spawn(NeutralSurvivorPrefab, marker.Position, rotation, PlayerRef.None,
				(spawnRunner, obj) =>
				{
					var survivor = obj.GetComponent<Survivor>();
					if (survivor == null)
						return;

					survivor.OwnerRef = PlayerRef.None;
					survivor.CharacterIndex = -1;
					AddSpawnedNeutral(survivor);
					StartCoroutine(InitializeNeutralAfterSpawn(survivor, patrolCenter, patrolRadius));
				});
		}

		private IEnumerator InitializeNeutralAfterSpawn(Survivor survivor, Vector3 patrolCenter, float patrolRadius)
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

			survivor.SetNonCombatAISettings(SurvivorNonCombatAISettings.Default);
			survivor.SetCombatAISettings(SurvivorCombatAISettings.Default);

			if (patrolRadius > 0f && SurvivorNonCombatAI.TryBuildAssignedAreaPatrolPoints(survivor, patrolCenter, patrolRadius, out Vector3[] patrolPoints))
			{
				Vector3 entryPoint = patrolPoints != null && patrolPoints.Length > 0 ? patrolPoints[0] : patrolCenter;
				survivor.SetAI(SurvivorNonCombatAI.AssignedArea(survivor, patrolCenter, patrolRadius, entryPoint, patrolPoints, survivor.NonCombatAISettings));
			}
			else
			{
				survivor.SetIdleAI();
			}

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

			var candidates = new List<SpawnMarker>(_validMarkers);
			Shuffle(candidates, _random);

			int targetCount = Mathf.Clamp(Mathf.RoundToInt(candidates.Count * Mathf.Clamp01(Settings.SpawnPointUsage)), 0, candidates.Count);
			float minDistance = GetEffectiveMinDistanceBetweenSelectedSpawnPoints();
			List<ReservedSpawnPosition> reservedPositions = GetReservedSpawnPositionsForScene();

			for (int i = 0; i < candidates.Count && _selectedMarkers.Count < targetCount; i++)
			{
				SpawnMarker candidate = candidates[i];
				if (CanMarkerSpawnAnySurvivors(candidate.Point) == false)
					continue;
				float candidateRadius = GetMarkerSpacingRadius(candidate.Point);
				if (IsFarEnoughFromSelected(candidate.Position, candidateRadius, minDistance, reservedPositions) == false)
					continue;

				_selectedMarkers.Add(candidate);
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
