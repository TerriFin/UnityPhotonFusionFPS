using Fusion;

namespace SimpleFPS
{
	/// <summary>
	/// Central rules for raid mode (the FPS-vs-RTS format). In raid mode the host is a pure RTS commander:
	/// they keep their full team but never possess (directly control) a survivor. See <c>Docs/RaidMode.md</c>.
	///
	/// Named <c>RaidModeRules</c> rather than <c>RaidMode</c> because <see cref="Gameplay"/> already has a
	/// <c>RaidMode</c> field, which would shadow a type of the same name inside that class.
	/// </summary>
	public static class RaidModeRules
	{
		/// <summary>
		/// True when <paramref name="playerRef"/> is the raid host — the RTS commander who must never possess
		/// a survivor.
		/// </summary>
		public static bool IsRaidControlledPlayer(Gameplay gameplay, PlayerRef playerRef)
		{
			return TryGetRaidHostPlayer(gameplay, out PlayerRef hostPlayer) && playerRef == hostPlayer;
		}

		/// <summary>
		/// Resolves the replicated host identity on every peer. Unlike <see cref="NetworkRunner.LocalPlayer"/>,
		/// this identifies the raid commander from clients as well as from state authority.
		/// </summary>
		public static bool TryGetRaidHostPlayer(Gameplay gameplay, out PlayerRef hostPlayer)
		{
			hostPlayer = PlayerRef.None;

			if (gameplay == null ||
			    gameplay.RaidMode == false ||
			    gameplay.Object == null ||
			    gameplay.Object.IsValid == false)
			{
				return false;
			}

			hostPlayer = gameplay.RaidHostPlayer;
			return hostPlayer.IsRealPlayer;
		}

		/// <summary>
		/// True while the raid host still owns at least one living survivor. During this phase all real,
		/// non-host player teams form a temporary combat alliance.
		/// </summary>
		public static bool IsClientAllianceActive(Gameplay gameplay)
		{
			if (TryGetActiveRaidHost(gameplay, out _, out PlayerData hostData) == false)
				return false;

			return hostData.IsConnected && hostData.IsAlive;
		}

		/// <summary>
		/// Returns whether two player-owned teams currently share a combat relationship. Ownership is not
		/// transferred: commands, possession, colors, scoring, and win-condition entries remain separate.
		/// </summary>
		public static bool ArePlayerTeamsAllied(Gameplay gameplay, PlayerRef first, PlayerRef second)
		{
			if (first.IsRealPlayer == false || second.IsRealPlayer == false)
				return false;
			if (first == second)
				return true;

			if (TryGetActiveRaidHost(gameplay, out PlayerRef hostPlayer, out PlayerData hostData) == false)
				return false;
			if (hostData.IsConnected == false || hostData.IsAlive == false)
				return false;

			return first != hostPlayer && second != hostPlayer;
		}

		private static bool TryGetActiveRaidHost(Gameplay gameplay, out PlayerRef hostPlayer, out PlayerData hostData)
		{
			hostData = default;

			if (TryGetRaidHostPlayer(gameplay, out hostPlayer) == false)
				return false;
			if (gameplay.Object == null || gameplay.Object.IsValid == false)
				return false;
			if (gameplay.State != EGameplayState.Running)
				return false;

			return gameplay.PlayerData.TryGet(hostPlayer, out hostData);
		}
	}
}
