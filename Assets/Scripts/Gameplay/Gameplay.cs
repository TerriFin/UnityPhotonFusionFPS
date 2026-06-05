using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using Fusion;

#if !UNITY_EDITOR && (UNITY_WEBGL || UNITY_ANDROID || UNITY_IOS)
#error This sample doesn't support currently selected platform, please switch to Windows, Mac, Linux in Build Settings.
#endif

namespace SimpleFPS
{
	/// <summary>
	/// Runtime data structure to hold player information which must survive events like player death/disconnect.
	/// </summary>
	public struct PlayerData : INetworkStruct
	{
		[Networked, Capacity(24)]
		public string    Nickname { get => default; set {} }
		public PlayerRef PlayerRef;
		public int       Kills;
		public int       Deaths;
		public int       LastKillTick;
		public int       StatisticPosition;
		public bool      IsAlive;
		public bool      IsConnected;

		public int       ActiveCharacterIndex;
		public int       AliveCharMaskLow;    // bits 0-31, serialized as int by Fusion
		public int       AliveCharMaskHigh;   // bits 32-63, serialized as int by Fusion
		public int       CharacterCount;
		public int       TeamColorIndex;

		// Convenience wrapper — NOT a Fusion-serialized field (no [Networked] attribute).
		// Fusion only serializes public fields, not computed properties, so this property is
		// transparent to the network. All existing code using AliveCharacterMask is unchanged.
		public long AliveCharacterMask
		{
			get => ((long)(uint)AliveCharMaskHigh << 32) | (uint)AliveCharMaskLow;
			set { AliveCharMaskLow = (int)value; AliveCharMaskHigh = (int)((uint)(value >> 32)); }
		}
	}

	public enum EGameplayState
	{
		Skirmish = 0,
		Running  = 1,
		Finished = 2,
	}

	/// <summary>
	/// Drives gameplay logic - state, timing, handles player connect/disconnect/spawn/despawn/death, calculates statistics.
	/// </summary>
	public class Gameplay : NetworkBehaviour
	{
		public const int NeutralTeamColorIndex = -1;

		public GameUI GameUI;
		[FormerlySerializedAs("PlayerPrefab")]
		public Survivor SurvivorPrefab;
		public float  GameDuration = 180f;
		public float  DoubleDamageDuration = 30f;

		[Header("Team Setup")]
		public int    StartingCharacterCount = 5;
		public bool   RaidMode;
		public int    RaidModeClientStartingCharacterCount = 1;
		public float  SpawnClusterRadius = 1.5f;
		public TeamColorPalette TeamColorPalette;

		[Header("Survivor Commands")]
		public SurvivorAICommandSettings AICommandSettings = new();

		[Networked][Capacity(32)][HideInInspector]
		public NetworkDictionary<PlayerRef, PlayerData> PlayerData { get; }
		[Networked][HideInInspector]
		public TickTimer RemainingTime { get; set; }
		[Networked][HideInInspector]
		public EGameplayState State { get; set; }
		[Networked][HideInInspector]
		public int WorldSeed { get; set; }

		public bool DoubleDamageActive => State == EGameplayState.Running && RemainingTime.RemainingTime(Runner).GetValueOrDefault() < DoubleDamageDuration;

		private bool _isNicknameSent;
		private float _runningStateTime;
		private List<Survivor> _spawnedPlayers = new(16);
		private List<PlayerRef> _pendingPlayers = new(16);
		private List<PlayerData> _tempPlayerData = new(16);
		private readonly Dictionary<PlayerRef, Transform> _spawnPointsByPlayer = new();
		private readonly List<PlayerRef> _tempSpawnAssignmentRemovals = new(16);

		// Non-networked survivor lookup, populated via Register/Unregister from Survivor.Spawned/Despawned
		// on every peer. Stays in sync because spawn/despawn events fire everywhere via state replication.
		private readonly Dictionary<PlayerRef, Dictionary<int, Survivor>> _characterCache = new();

		// State-authority-only debounce: prevents a held Shift/Ctrl from triggering multiple switches
		// when _previousButtons on the newly-active character hasn't seen the button yet.
		private readonly Dictionary<PlayerRef, int> _lastSwitchTicks = new();

		// Scratch list used when iterating characters during teardown — lets us despawn without
		// mutating _characterCache while iterating.
		private readonly List<Survivor> _tempCharacters = new(16);
		private SurvivorAICommandService _survivorAICommands;
		private RoadGridGenerator _roadGridGenerator;
		private BuildingPlacementGenerator _buildingPlacementGenerator;
		private ZombieOrchestrator _zombieOrchestrator;
		private bool _loggedWaitingForWorldGeneration;

		private const int SwitchCooldownTicks = 10;

		public SurvivorAICommandService SurvivorAICommands
		{
			get
			{
				if (_survivorAICommands == null)
				{
					if (AICommandSettings == null)
						AICommandSettings = new SurvivorAICommandSettings();

					_survivorAICommands = new SurvivorAICommandService(this, _characterCache, AICommandSettings);
				}

				return _survivorAICommands;
			}
		}

		public void RegisterSurvivor(Survivor character)
		{
			if (_characterCache.TryGetValue(character.OwnerRef, out var dict) == false)
			{
				dict = new Dictionary<int, Survivor>();
				_characterCache[character.OwnerRef] = dict;
			}
			dict[character.CharacterIndex] = character;
		}

		public void UnregisterSurvivor(Survivor character)
		{
			if (_characterCache.TryGetValue(character.OwnerRef, out var dict))
			{
				if (dict.TryGetValue(character.CharacterIndex, out var existing) && existing == character)
				{
					dict.Remove(character.CharacterIndex);
				}
			}
		}

		public Survivor GetSurvivor(PlayerRef owner, int index)
		{
			if (_characterCache.TryGetValue(owner, out var dict) && dict.TryGetValue(index, out var character))
				return character;
			return null;
		}

		public Material GetTeamColorMaterial(int teamColorIndex)
		{
			if (TeamColorPalette == null)
				return null;

			return TeamColorPalette.GetMaterial(teamColorIndex);
		}

		public void RequestMapMoveOrder(long selectedCharacterMask, Vector3 destination)
		{
			if (HasStateAuthority)
			{
				if (IsFinite(destination))
					SurvivorAICommands.ApplySelectedTeamCommand(Runner.LocalPlayer, selectedCharacterMask, SurvivorAICommand.MoveTo(destination));
				return;
			}

			int maskLow = (int)selectedCharacterMask;
			int maskHigh = (int)(selectedCharacterMask >> 32);
			RPC_RequestMapMoveOrder(maskLow, maskHigh, destination);
		}

		public void RequestMapAssignedAreaOrder(long selectedCharacterMask, Vector3 center, float radius)
		{
			if (HasStateAuthority)
			{
				if (IsFinite(center) && TryGetValidAssignedAreaRadius(radius, out radius))
					SurvivorAICommands.ApplySelectedTeamAssignedArea(Runner.LocalPlayer, selectedCharacterMask, SnapAssignedAreaCenterToHeightMap(center), radius);
				return;
			}

			int maskLow = (int)selectedCharacterMask;
			int maskHigh = (int)(selectedCharacterMask >> 32);
			RPC_RequestMapAssignedAreaOrder(maskLow, maskHigh, center, radius);
		}

		public void RequestMapFollowOrder(long selectedCharacterMask, int targetCharacterIndex)
		{
			if (HasStateAuthority)
			{
				SurvivorAICommands.ApplySelectedTeamFollow(Runner.LocalPlayer, selectedCharacterMask, targetCharacterIndex);
				return;
			}

			int maskLow = (int)selectedCharacterMask;
			int maskHigh = (int)(selectedCharacterMask >> 32);
			RPC_RequestMapFollowOrder(maskLow, maskHigh, targetCharacterIndex);
		}

		public void RequestMapNonCombatSettings(long selectedCharacterMask, bool enabled)
		{
			if (HasStateAuthority)
			{
				SurvivorAICommands.ApplySelectedTeamNonCombatSettings(Runner.LocalPlayer, selectedCharacterMask, enabled);
				return;
			}

			int maskLow = (int)selectedCharacterMask;
			int maskHigh = (int)(selectedCharacterMask >> 32);
			RPC_RequestMapNonCombatSettings(maskLow, maskHigh, enabled);
		}

		public void RequestMapCombatSettings(long selectedCharacterMask, bool enabled)
		{
			if (HasStateAuthority)
			{
				SurvivorAICommands.ApplySelectedTeamCombatSettings(Runner.LocalPlayer, selectedCharacterMask, enabled);
				return;
			}

			int maskLow = (int)selectedCharacterMask;
			int maskHigh = (int)(selectedCharacterMask >> 32);
			RPC_RequestMapCombatSettings(maskLow, maskHigh, enabled);
		}

		public void RequestSwitchActiveCharacter(int targetCharacterIndex)
		{
			if (HasStateAuthority)
			{
				SwitchToCharacter(Runner.LocalPlayer, targetCharacterIndex);
				return;
			}

			RPC_RequestSwitchActiveCharacter(targetCharacterIndex);
		}

		public void CharacterKilled(PlayerRef killerPlayerRef, PlayerRef ownerRef, int characterIndex, EWeaponType weaponType, bool isCriticalKill)
		{
			if (HasStateAuthority == false)
				return;

			// Update statistics of the killer player.
			if (PlayerData.TryGet(killerPlayerRef, out PlayerData killerData))
			{
				killerData.Kills++;
				killerData.LastKillTick = Runner.Tick;
				PlayerData.Set(killerPlayerRef, killerData);
			}

			// Mark the character as dead.
			var victimData = PlayerData.Get(ownerRef);
			victimData.AliveCharacterMask &= ~(1L << characterIndex);
			victimData.Deaths++;

			// Transfer control if the active character just died.
			if (victimData.ActiveCharacterIndex == characterIndex)
			{
				victimData.ActiveCharacterIndex = FindClosestAliveCharacter(ownerRef, victimData, characterIndex);
				// -1 means no alive characters remain — the player has lost.
			}

			victimData.IsAlive = victimData.AliveCharacterMask != 0;
			PlayerData.Set(ownerRef, victimData);

			UpdatePlayerObject(ownerRef, victimData);

			// Inform all clients about the kill via RPC.
			RPC_PlayerKilled(killerPlayerRef, ownerRef, weaponType, isCriticalKill);

			CheckWinCondition();
			RecalculateStatisticPositions();
		}

		public void SwitchActiveCharacter(PlayerRef owner, int direction)
		{
			if (HasStateAuthority == false)
				return;

			if (_lastSwitchTicks.TryGetValue(owner, out int lastTick) && Runner.Tick - lastTick < SwitchCooldownTicks)
				return;

			if (PlayerData.TryGet(owner, out var data) == false)
				return;

			int next = FindNextAliveCharacter(data.AliveCharacterMask, data.CharacterCount,
				data.ActiveCharacterIndex, direction);
			if (next < 0 || next == data.ActiveCharacterIndex)
				return;

			ApplyActiveCharacterSwitch(owner, data, next);
		}

		public void SwitchToCharacter(PlayerRef owner, int targetCharacterIndex)
		{
			if (HasStateAuthority == false)
				return;

			if (_lastSwitchTicks.TryGetValue(owner, out int lastTick) && Runner.Tick - lastTick < SwitchCooldownTicks)
				return;

			if (PlayerData.TryGet(owner, out var data) == false)
				return;

			if (targetCharacterIndex < 0 || targetCharacterIndex >= data.CharacterCount)
				return;

			if ((data.AliveCharacterMask & (1L << targetCharacterIndex)) == 0)
				return;

			if (targetCharacterIndex == data.ActiveCharacterIndex)
				return;

			ApplyActiveCharacterSwitch(owner, data, targetCharacterIndex);
		}

		private void ApplyActiveCharacterSwitch(PlayerRef owner, PlayerData data, int nextIndex)
		{
			var previousCharacter = GetSurvivor(owner, data.ActiveCharacterIndex);
			if (previousCharacter != null)
			{
				previousCharacter.ResetVerticalLook();
				previousCharacter.SetIdleAI();
			}

			data.ActiveCharacterIndex = nextIndex;
			PlayerData.Set(owner, data);
			UpdatePlayerObject(owner, data);

			var activeCharacter = GetSurvivor(owner, data.ActiveCharacterIndex);
			if (activeCharacter != null)
			{
				activeCharacter.SetIdleAI();
			}

			_lastSwitchTicks[owner] = Runner.Tick;
		}

		public override void Spawned()
		{
			MatchRuntimeSettings.ApplyToScene(gameObject.scene);

			if (Runner.Mode == SimulationModes.Server)
			{
				Application.targetFrameRate = TickRate.Resolve(Runner.Config.Simulation.TickRateSelection).Server;
			}

			if (Runner.GameMode == GameMode.Shared)
			{
				throw new System.NotSupportedException("This sample doesn't support Shared Mode, please start the game as Server, Host or Client.");
			}

			if (HasStateAuthority)
				InitializeWorldSeed();
		}

		public override void FixedUpdateNetwork()
		{
			if (HasStateAuthority == false)
				return;

			if (IsWorldReadyForSpawning() == false)
				return;

			// SurvivorConnectionManager maps player connections to their currently active survivor teams.
			SurvivorConnectionManager.UpdatePlayerConnections(Runner, SpawnPlayer, DespawnPlayer);

			// Start gameplay when there are enough players connected.
			if (State == EGameplayState.Skirmish && PlayerData.Count > 1)
			{
				StartGameplay();
			}

			if (State == EGameplayState.Running)
			{
				_runningStateTime += Runner.DeltaTime;

				var sessionInfo = Runner.SessionInfo;

				// Hide the match after 60 seconds. Players won't be able to randomly connect to existing game and start new one instead.
				// Joining via party code should work.
				if (sessionInfo.IsVisible && (_runningStateTime > 60f || sessionInfo.PlayerCount >= sessionInfo.MaxPlayers))
				{
					sessionInfo.IsVisible = false;
				}

				if (RemainingTime.Expired(Runner))
				{
					if (TryStartZombieOvertime() == false)
					{
						StopGameplay();
					}
				}
			}
		}

		public override void Render()
		{
			if (Runner.Mode == SimulationModes.Server)
				return;

			// Every client must send its nickname to the server when the game is started.
			if (_isNicknameSent == false)
			{
				if (PlayerData.TryGet(Runner.LocalPlayer, out _) == false)
					return;

				RPC_SetPlayerNickname(Runner.LocalPlayer, PlayerPrefs.GetString("Photon.Menu.Username"));
				_isNicknameSent = true;
			}
		}

		private void SpawnPlayer(PlayerRef playerRef)
		{
			if (PlayerData.TryGet(playerRef, out var playerData) == false)
			{
				playerData = new PlayerData();
				playerData.PlayerRef = playerRef;
				playerData.Nickname = playerRef.ToString();
				playerData.StatisticPosition = int.MaxValue;
				playerData.IsAlive = false;
				playerData.IsConnected = false;
				playerData.TeamColorIndex = NeutralTeamColorIndex;
			}

			if (playerData.IsConnected == true)
				return;

			Debug.LogWarning($"{playerRef} connected.");

			playerData.IsConnected = true;
			playerData.TeamColorIndex = GetAssignedTeamColorIndex(playerData.TeamColorIndex);

			PlayerData.Set(playerRef, playerData);

			SpawnTeam(playerRef);

			RecalculateStatisticPositions();
		}

		private bool IsWorldReadyForSpawning()
		{
			ResolveWorldGenerators();

			if (_roadGridGenerator != null && _roadGridGenerator.GenerateOnStart && _roadGridGenerator.IsGenerationComplete == false)
				return LogWaitingForWorldGeneration();

			if (_buildingPlacementGenerator != null && _buildingPlacementGenerator.GenerateOnStart && _buildingPlacementGenerator.IsGenerationComplete == false)
				return LogWaitingForWorldGeneration();

			if (Runner.SimulationUnityScene.GetComponents<SpawnPoint>(false).Length == 0)
				return LogWaitingForWorldGeneration();

			_loggedWaitingForWorldGeneration = false;
			return true;
		}

		private bool LogWaitingForWorldGeneration()
		{
			if (_loggedWaitingForWorldGeneration == false)
			{
				Debug.Log($"{nameof(Gameplay)} is waiting for generated spawn points before spawning players.", this);
				_loggedWaitingForWorldGeneration = true;
			}

			return false;
		}

		private void ResolveWorldGenerators()
		{
			if (_roadGridGenerator == null)
				_roadGridGenerator = FindObjectOfType<RoadGridGenerator>();
			if (_buildingPlacementGenerator == null)
				_buildingPlacementGenerator = FindObjectOfType<BuildingPlacementGenerator>();
		}

		private void DespawnPlayer(PlayerRef playerRef, Survivor survivor)
		{
			if (PlayerData.TryGet(playerRef, out var playerData) == true)
			{
				if (playerData.IsConnected == true)
				{
					Debug.LogWarning($"{playerRef} disconnected.");
				}

				playerData.IsConnected = false;
				playerData.IsAlive = false;
				playerData.AliveCharacterMask = 0;
				PlayerData.Set(playerRef, playerData);
			}

			DespawnTeam(playerRef);
			_spawnPointsByPlayer.Remove(playerRef);
			_lastSwitchTicks.Remove(playerRef);

			RecalculateStatisticPositions();
		}

		private void SpawnTeam(PlayerRef playerRef)
		{
			int count = GetStartingCharacterCount(playerRef);
			var spawnPoint = GetSpawnPoint(playerRef);
			var offsets = GetClusterOffsets(count, SpawnClusterRadius);

			for (int i = 0; i < count; i++)
			{
				int capturedIndex = i;
				Vector3 position = spawnPoint.position + offsets[i];

				Runner.Spawn(SurvivorPrefab, position, spawnPoint.rotation, playerRef,
					(runner, obj) =>
					{
						var character = obj.GetComponent<Survivor>();
						character.OwnerRef       = playerRef;
						character.CharacterIndex = capturedIndex;
					});
			}

			var playerData = PlayerData.Get(playerRef);
			playerData.ActiveCharacterIndex = 0;
			playerData.AliveCharacterMask   = count >= 64 ? -1L : (1L << count) - 1L;
			playerData.CharacterCount       = count;
			playerData.IsAlive              = true;
			PlayerData.Set(playerRef, playerData);

			UpdatePlayerObject(playerRef, playerData);
		}

		private int GetStartingCharacterCount(PlayerRef playerRef)
		{
			if (RaidMode && playerRef != Runner.LocalPlayer)
				return Mathf.Clamp(RaidModeClientStartingCharacterCount, 1, 64);

			return Mathf.Clamp(StartingCharacterCount, 1, 64);
		}

		private void InitializeWorldSeed()
		{
			if (WorldSeed != 0)
				return;

			var roadGridGenerator = FindObjectOfType<RoadGridGenerator>();

			int seed = roadGridGenerator != null ? roadGridGenerator.Seed : 12345;
			if (roadGridGenerator != null && roadGridGenerator.RandomizeSeedOnGenerate)
			{
				string sessionName = Runner != null && Runner.SessionInfo != null ? Runner.SessionInfo.Name : string.Empty;
				seed = GetStableWorldSeed(sessionName, roadGridGenerator.Width, roadGridGenerator.Height);
			}

			if (seed == 0)
				seed = 1;

			WorldSeed = seed;
		}

		private static int GetStableWorldSeed(string sessionName, int width, int height)
		{
			unchecked
			{
				const int fnvOffsetBasis = -2128831035;
				const int fnvPrime = 16777619;

				int hash = fnvOffsetBasis;
				if (string.IsNullOrWhiteSpace(sessionName) == false)
				{
					for (int i = 0; i < sessionName.Length; i++)
					{
						hash ^= sessionName[i];
						hash *= fnvPrime;
					}
				}

				hash ^= Mathf.Max(3, width);
				hash *= fnvPrime;
				hash ^= Mathf.Max(3, height);
				hash *= fnvPrime;

				return hash;
			}
		}

		private void DespawnTeam(PlayerRef playerRef)
		{
			if (_characterCache.TryGetValue(playerRef, out var dict) == false)
				return;

			_tempCharacters.Clear();
			foreach (var character in dict.Values)
			{
				if (character != null)
					_tempCharacters.Add(character);
			}

			for (int i = 0; i < _tempCharacters.Count; i++)
			{
				var character = _tempCharacters[i];
				if (character != null && character.Object != null)
				{
					Runner.Despawn(character.Object);
				}
			}

			_tempCharacters.Clear();
		}

		private void UpdatePlayerObject(PlayerRef playerRef, PlayerData data)
		{
			if (data.ActiveCharacterIndex < 0)
				return;

			var character = GetSurvivor(playerRef, data.ActiveCharacterIndex);
			if (character != null)
			{
				Runner.SetPlayerObject(playerRef, character.Object);
			}
		}

		private static int FindNextAliveCharacter(long aliveMask, int characterCount, int startIndex, int direction)
		{
			if (characterCount <= 0)
				return -1;

			for (int i = 1; i <= characterCount; i++)
			{
				int candidate = ((startIndex + direction * i) % characterCount + characterCount) % characterCount;
				if ((aliveMask & (1L << candidate)) != 0)
					return candidate;
			}
			return -1;
		}

		private int FindClosestAliveCharacter(PlayerRef owner, PlayerData data, int deadCharacterIndex)
		{
			if (data.AliveCharacterMask == 0 || data.CharacterCount <= 0)
				return -1;

			var deadCharacter = GetSurvivor(owner, deadCharacterIndex);
			if (deadCharacter == null)
				return FindNextAliveCharacter(data.AliveCharacterMask, data.CharacterCount, deadCharacterIndex, 1);

			Vector3 origin = deadCharacter.transform.position;
			int closestIndex = -1;
			float closestDistanceSqr = float.MaxValue;

			for (int i = 0; i < data.CharacterCount; i++)
			{
				if ((data.AliveCharacterMask & (1L << i)) == 0)
					continue;

				var candidate = GetSurvivor(owner, i);
				if (candidate == null)
					continue;

				float distanceSqr = FlatDistanceSqr(origin, candidate.transform.position);
				if (distanceSqr >= closestDistanceSqr)
					continue;

				closestDistanceSqr = distanceSqr;
				closestIndex = i;
			}

			return closestIndex >= 0
				? closestIndex
				: FindNextAliveCharacter(data.AliveCharacterMask, data.CharacterCount, deadCharacterIndex, 1);
		}

		private static float FlatDistanceSqr(Vector3 a, Vector3 b)
		{
			a.y = 0f;
			b.y = 0f;
			return (a - b).sqrMagnitude;
		}

		private static Vector3[] GetClusterOffsets(int count, float radius)
		{
			if (count == 1)
				return new[] { Vector3.zero };

			var offsets = new Vector3[count];
			for (int i = 0; i < count; i++)
			{
				float angle = i * Mathf.PI * 2f / count;
				offsets[i] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
			}
			return offsets;
		}

		private void CheckWinCondition()
		{
			if (State != EGameplayState.Running)
				return;

			int teamsAlive = 0;
			foreach (var pair in PlayerData)
			{
				if (pair.Value.IsAlive)
					teamsAlive++;
			}

			if (teamsAlive <= 1)
			{
				StopGameplay();
			}
		}

		private Transform GetSpawnPoint(PlayerRef playerRef)
		{
			var spawnPoints = Runner.SimulationUnityScene.GetComponents<SpawnPoint>(false);
			if (spawnPoints == null || spawnPoints.Length == 0)
				return transform;

			PruneMissingAssignedSpawnPoints(spawnPoints);

			if (_spawnPointsByPlayer.TryGetValue(playerRef, out Transform existing) && IsSpawnPointAvailable(existing, spawnPoints))
				return existing;

			Transform spawnPoint = ChooseSpreadSpawnPoint(spawnPoints);
			_spawnPointsByPlayer[playerRef] = spawnPoint;
			return spawnPoint;
		}

		private Transform ChooseSpreadSpawnPoint(SpawnPoint[] spawnPoints)
		{
			if (_spawnPointsByPlayer.Count == 0)
				return spawnPoints[Random.Range(0, spawnPoints.Length)].transform;

			Transform best = null;
			Transform secondBest = null;
			float bestScore = float.NegativeInfinity;
			float secondBestScore = float.NegativeInfinity;
			float bestTieBreaker = 0f;
			float secondBestTieBreaker = 0f;

			for (int i = 0; i < spawnPoints.Length; i++)
			{
				Transform candidate = spawnPoints[i].transform;
				if (IsSpawnAlreadyAssigned(candidate))
					continue;

				float score = GetMinimumAssignedSpawnDistanceSqr(candidate.position);
				float tieBreaker = Random.value;

				if (score > bestScore || Mathf.Approximately(score, bestScore) && tieBreaker > bestTieBreaker)
				{
					secondBest = best;
					secondBestScore = bestScore;
					secondBestTieBreaker = bestTieBreaker;
					best = candidate;
					bestScore = score;
					bestTieBreaker = tieBreaker;
				}
				else if (score > secondBestScore || Mathf.Approximately(score, secondBestScore) && tieBreaker > secondBestTieBreaker)
				{
					secondBest = candidate;
					secondBestScore = score;
					secondBestTieBreaker = tieBreaker;
				}
			}

			if (best == null)
				return spawnPoints[Random.Range(0, spawnPoints.Length)].transform;
			if (secondBest == null)
				return best;

			return Random.value < 0.5f ? best : secondBest;
		}

		private float GetMinimumAssignedSpawnDistanceSqr(Vector3 candidatePosition)
		{
			float minDistanceSqr = float.PositiveInfinity;

			foreach (var pair in _spawnPointsByPlayer)
			{
				if (pair.Value == null)
					continue;

				float distanceSqr = FlatDistanceSqr(candidatePosition, pair.Value.position);
				if (distanceSqr < minDistanceSqr)
					minDistanceSqr = distanceSqr;
			}

			return minDistanceSqr;
		}

		private bool IsSpawnAlreadyAssigned(Transform spawnPoint)
		{
			foreach (var pair in _spawnPointsByPlayer)
			{
				if (pair.Value == spawnPoint)
					return true;
			}

			return false;
		}

		private void PruneMissingAssignedSpawnPoints(SpawnPoint[] spawnPoints)
		{
			_tempSpawnAssignmentRemovals.Clear();

			foreach (var pair in _spawnPointsByPlayer)
			{
				if (pair.Value == null || IsSpawnPointAvailable(pair.Value, spawnPoints) == false)
					_tempSpawnAssignmentRemovals.Add(pair.Key);
			}

			for (int i = 0; i < _tempSpawnAssignmentRemovals.Count; i++)
			{
				_spawnPointsByPlayer.Remove(_tempSpawnAssignmentRemovals[i]);
			}

			_tempSpawnAssignmentRemovals.Clear();
		}

		private static bool IsSpawnPointAvailable(Transform spawnPoint, SpawnPoint[] spawnPoints)
		{
			if (spawnPoint == null || spawnPoints == null)
				return false;

			for (int i = 0; i < spawnPoints.Length; i++)
			{
				if (spawnPoints[i] != null && spawnPoints[i].transform == spawnPoint)
					return true;
			}

			return false;
		}

		private void StartGameplay()
		{
			State = EGameplayState.Running;
			RemainingTime = TickTimer.CreateFromSeconds(Runner, GameDuration);
			_spawnPointsByPlayer.Clear();

			// Collect keys first — SpawnTeam / DespawnTeam mutate networked state we iterate over.
			_pendingPlayers.Clear();
			foreach (var pair in PlayerData)
			{
				_pendingPlayers.Add(pair.Key);
			}

			for (int i = 0; i < _pendingPlayers.Count; i++)
			{
				var playerRef = _pendingPlayers[i];
				var data = PlayerData.Get(playerRef);

				data.Kills = 0;
				data.Deaths = 0;
				data.StatisticPosition = int.MaxValue;
				data.IsAlive = false;

				PlayerData.Set(playerRef, data);

				if (data.IsConnected == false)
					continue;

				DespawnTeam(playerRef);
				SpawnTeam(playerRef);
			}

			_pendingPlayers.Clear();
		}

		private void StopGameplay()
		{
			RecalculateStatisticPositions();

			State = EGameplayState.Finished;
		}

		private bool TryStartZombieOvertime()
		{
			if (_zombieOrchestrator == null)
				_zombieOrchestrator = FindObjectOfType<ZombieOrchestrator>();

			if (_zombieOrchestrator == null || _zombieOrchestrator.HasUsableSettings == false)
				return false;

			_zombieOrchestrator.StartOvertime();
			return true;
		}

		private void RecalculateStatisticPositions()
		{
			if (State == EGameplayState.Finished)
				return;

			_tempPlayerData.Clear();

			foreach (var pair in PlayerData)
			{
				_tempPlayerData.Add(pair.Value);
			}

			_tempPlayerData.Sort((a, b) =>
			{
				if (a.Kills != b.Kills)
					return b.Kills.CompareTo(a.Kills);

				return a.LastKillTick.CompareTo(b.LastKillTick);
			});

			for (int i = 0; i < _tempPlayerData.Count; i++)
			{
				var playerData = _tempPlayerData[i];
				playerData.StatisticPosition = playerData.Kills > 0 ? i + 1 : int.MaxValue;

				PlayerData.Set(playerData.PlayerRef, playerData);
			}
		}

		[Rpc(RpcSources.StateAuthority, RpcTargets.All, Channel = RpcChannel.Reliable)]
		private void RPC_PlayerKilled(PlayerRef killerPlayerRef, PlayerRef victimPlayerRef, EWeaponType weaponType, bool isCriticalKill)
		{
			string killerNickname = "";
			string victimNickname = "";

			if (PlayerData.TryGet(killerPlayerRef, out PlayerData killerData))
			{
				killerNickname = killerData.Nickname;
			}

			if (PlayerData.TryGet(victimPlayerRef, out PlayerData victimData))
			{
				victimNickname = victimData.Nickname;
			}

			GameUI.GameplayView.KillFeed.ShowKill(killerNickname, victimNickname, weaponType, isCriticalKill);
		}

		[Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
		private void RPC_SetPlayerNickname(PlayerRef playerRef, string nickname)
		{
			if (PlayerData.TryGet(playerRef, out var playerData) == false)
				return;

			playerData.Nickname = nickname;
			PlayerData.Set(playerRef, playerData);
		}

		[Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
		private void RPC_RequestMapMoveOrder(int selectedMaskLow, int selectedMaskHigh, Vector3 destination, RpcInfo info = default)
		{
			if (IsValidMapOrderSource(info.Source) == false)
				return;

			long selectedCharacterMask = ((long)(uint)selectedMaskHigh << 32) | (uint)selectedMaskLow;
			if (IsFinite(destination) == false)
				return;

			SurvivorAICommands.ApplySelectedTeamCommand(info.Source, selectedCharacterMask, SurvivorAICommand.MoveTo(destination));
		}

		[Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
		private void RPC_RequestMapAssignedAreaOrder(int selectedMaskLow, int selectedMaskHigh, Vector3 center, float radius, RpcInfo info = default)
		{
			if (IsValidMapOrderSource(info.Source) == false)
				return;

			long selectedCharacterMask = ((long)(uint)selectedMaskHigh << 32) | (uint)selectedMaskLow;
			if (IsFinite(center) == false || TryGetValidAssignedAreaRadius(radius, out radius) == false)
				return;

			SurvivorAICommands.ApplySelectedTeamAssignedArea(info.Source, selectedCharacterMask, SnapAssignedAreaCenterToHeightMap(center), radius);
		}

		[Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
		private void RPC_RequestMapFollowOrder(int selectedMaskLow, int selectedMaskHigh, int targetCharacterIndex, RpcInfo info = default)
		{
			if (IsValidMapOrderSource(info.Source) == false)
				return;

			long selectedCharacterMask = ((long)(uint)selectedMaskHigh << 32) | (uint)selectedMaskLow;
			SurvivorAICommands.ApplySelectedTeamFollow(info.Source, selectedCharacterMask, targetCharacterIndex);
		}

		[Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
		private void RPC_RequestMapNonCombatSettings(int selectedMaskLow, int selectedMaskHigh, bool enabled, RpcInfo info = default)
		{
			if (IsValidMapOrderSource(info.Source) == false)
				return;

			long selectedCharacterMask = ((long)(uint)selectedMaskHigh << 32) | (uint)selectedMaskLow;
			SurvivorAICommands.ApplySelectedTeamNonCombatSettings(info.Source, selectedCharacterMask, enabled);
		}

		[Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
		private void RPC_RequestMapCombatSettings(int selectedMaskLow, int selectedMaskHigh, bool enabled, RpcInfo info = default)
		{
			if (IsValidMapOrderSource(info.Source) == false)
				return;

			long selectedCharacterMask = ((long)(uint)selectedMaskHigh << 32) | (uint)selectedMaskLow;
			SurvivorAICommands.ApplySelectedTeamCombatSettings(info.Source, selectedCharacterMask, enabled);
		}

		[Rpc(RpcSources.All, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
		private void RPC_RequestSwitchActiveCharacter(int targetCharacterIndex, RpcInfo info = default)
		{
			if (IsValidMapOrderSource(info.Source) == false)
				return;

			SwitchToCharacter(info.Source, targetCharacterIndex);
		}

		private bool IsValidMapOrderSource(PlayerRef source)
		{
			if (source.IsRealPlayer == false)
				return false;

			return PlayerData.TryGet(source, out var data) && data.IsConnected && data.IsAlive;
		}

		private static bool IsFinite(Vector3 value)
		{
			return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
		}

		private bool TryGetValidAssignedAreaRadius(float requestedRadius, out float radius)
		{
			radius = 0f;
			if (float.IsFinite(requestedRadius) == false)
				return false;

			float minRadius = AICommandSettings != null ? Mathf.Max(0f, AICommandSettings.AssignedAreaMinRadius) : 0f;
			float maxRadius = AICommandSettings != null ? Mathf.Max(minRadius, AICommandSettings.AssignedAreaMaxRadius) : requestedRadius;
			if (requestedRadius < minRadius)
				return false;

			radius = Mathf.Min(requestedRadius, maxRadius);
			return radius > 0f;
		}

		// Replace the raw map-raycast Y (which may have hit a roof, car, or other scenery) with the ground Y of
		// the underlying height cell. This way the AI's patrol-point sampler resolves at the correct floor,
		// even if the player dragged the circle over something the survivors cannot climb on top of.
		private Vector3 SnapAssignedAreaCenterToHeightMap(Vector3 center)
		{
			if (_heightMapGenerator == null)
				_heightMapGenerator = FindObjectOfType<HeightMapGenerator>();

			if (_heightMapGenerator == null)
				return center;

			if (_heightMapGenerator.TryGetHeightSnapshot(out WorldHeightSnapshot snapshot) == false || snapshot.IsValid == false || snapshot.TileSize <= 0f)
				return center;

			Vector3 local = center - snapshot.Origin;
			Vector2Int cellPosition = new Vector2Int(
				Mathf.RoundToInt(local.x / snapshot.TileSize),
				Mathf.RoundToInt(local.z / snapshot.TileSize));

			if (snapshot.TryGetCell(cellPosition, out WorldHeightCell cell) == false)
				return center;

			center.y = snapshot.Origin.y + cell.HeightLevel * snapshot.HeightLevelWorldUnits;
			return center;
		}

		private HeightMapGenerator _heightMapGenerator;

		private int GetAssignedTeamColorIndex(int currentIndex)
		{
			int colorCount = TeamColorPalette != null ? TeamColorPalette.TeamColorCount : 0;
			if (colorCount <= 0)
				return NeutralTeamColorIndex;
			if (currentIndex >= 0 && currentIndex < colorCount)
				return currentIndex;

			int startIndex = Random.Range(0, colorCount);
			for (int i = 0; i < colorCount; i++)
			{
				int candidate = (startIndex + i) % colorCount;
				if (IsTeamColorInUse(candidate) == false)
					return candidate;
			}

			return startIndex;
		}

		private bool IsTeamColorInUse(int teamColorIndex)
		{
			foreach (var pair in PlayerData)
			{
				var data = pair.Value;
				if (data.IsConnected && data.TeamColorIndex == teamColorIndex)
					return true;
			}

			return false;
		}
	}
}
