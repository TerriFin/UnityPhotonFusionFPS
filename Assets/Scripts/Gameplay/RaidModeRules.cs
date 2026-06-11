using Fusion;

namespace SimpleFPS
{
	/// <summary>
	/// Central rules for raid mode (the FPS-vs-RTS format). In raid mode the host is a pure RTS commander:
	/// they keep their full team but never possess (directly control) a survivor and stay locked into the
	/// tactical map. See <c>Docs/RaidMode.md</c>.
	///
	/// Named <c>RaidModeRules</c> rather than <c>RaidMode</c> because <see cref="Gameplay"/> already has a
	/// <c>RaidMode</c> field, which would shadow a type of the same name inside that class.
	/// </summary>
	public static class RaidModeRules
	{
		/// <summary>
		/// True when <paramref name="playerRef"/> is the raid host — the RTS commander who must never possess
		/// a survivor.
		///
		/// State-authority-only: this relies on <see cref="NetworkRunner.LocalPlayer"/> being the host, which
		/// is only true on the host/state-authority peer. It is meant to be called from state-authority gameplay
		/// code (team spawning, active-character switching), the same assumption the rest of the raid spawn logic
		/// already makes (see <c>Gameplay.GetStartingCharacterCount</c>). The host peer's own local view detects
		/// the raid host differently, via <c>Gameplay.RaidMode &amp;&amp; Gameplay.HasStateAuthority</c>
		/// (see <see cref="SpectatorController"/>).
		/// </summary>
		public static bool IsRaidControlledPlayer(Gameplay gameplay, PlayerRef playerRef)
		{
			return gameplay != null &&
			       gameplay.RaidMode &&
			       gameplay.Runner != null &&
			       playerRef == gameplay.Runner.LocalPlayer;
		}
	}
}
