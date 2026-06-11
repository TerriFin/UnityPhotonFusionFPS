using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SimpleFPS
{
	public enum ESpectatorMode
	{
		None,
		RaidCommander,
		DefeatedSpectator,
	}

	/// <summary>
	/// Local (per-peer) controller that drives the shared <see cref="SpectatorCamera"/> for the two view-only
	/// modes — the raid host (<see cref="ESpectatorMode.RaidCommander"/>) and any defeated player while the match
	/// is still running (<see cref="ESpectatorMode.DefeatedSpectator"/>). Both modes inspect a survivor with an
	/// orbit camera and switch survivors with Ctrl/Shift (within the inspected survivor's team) or by selecting on
	/// the map and pressing Space. They differ only in which survivors are inspectable, whether commands are
	/// allowed, the minimap, and map reveal — those differences live in the systems that read <see cref="Mode"/>.
	/// See <c>Docs/RaidMode.md</c> and <c>Docs/SpectateMode.md</c>.
	///
	/// The raid host is detected as <c>RaidMode &amp;&amp; HasStateAuthority</c> (uniquely true on the host peer).
	/// A defeated player is detected from their replicated <see cref="PlayerData"/> (had a team, none alive now),
	/// which also catches a defeated raid host and drops them into spectate.
	/// </summary>
	[DisallowMultipleComponent]
	[RequireComponent(typeof(SpectatorCamera))]
	public sealed class SpectatorController : MonoBehaviour
	{
		[Tooltip("The shared orbit camera. Auto-resolved from the required SpectatorCamera component on this object; all camera/orbit/collision tunables (distance, min/max pitch, yaw/pitch speeds, FOV, damping, etc.) live there.")]
		public SpectatorCamera Camera;

		public ESpectatorMode Mode { get; private set; }
		public bool IsActive => Mode != ESpectatorMode.None;
		public bool IsRaidCommander => Mode == ESpectatorMode.RaidCommander;
		public bool CanInspectAnyTeam => Mode == ESpectatorMode.DefeatedSpectator;
		public bool CanIssueCommands => Mode != ESpectatorMode.DefeatedSpectator;
		public Survivor InspectTarget => _inspectTarget;

		private Survivor _inspectTarget;
		private PlayerRef _spectatedOwner;
		private bool _hasAutoOpenedMap;

		// Called by the map's Space handler to switch the inspected survivor without closing the map.
		public void SetInspectTarget(Survivor survivor)
		{
			if (survivor != null)
				_inspectTarget = survivor;
		}

		public void Tick(Gameplay gameplay, NetworkRunner runner, GameMapView mapView, bool gameplayActive)
		{
			Mode = DetermineMode(gameplay, runner, gameplayActive);

			if (Mode == ESpectatorMode.None)
			{
				DisableSpectator();
				return;
			}

			UpdateInspect(gameplay, runner, mapView);
		}

		private ESpectatorMode DetermineMode(Gameplay gameplay, NetworkRunner runner, bool gameplayActive)
		{
			if (gameplayActive == false || gameplay == null || gameplay.Object == null || runner == null)
				return ESpectatorMode.None;

			if (gameplay.PlayerData.TryGet(runner.LocalPlayer, out var data) == false)
				return ESpectatorMode.None;

			// Alive raid host commands their team; once they lose it they fall through to the defeated path.
			if (gameplay.RaidMode && gameplay.HasStateAuthority && data.IsAlive)
				return ESpectatorMode.RaidCommander;

			// Had a team, none alive now (covers normal players and a defeated raid host).
			if (data.IsConnected && data.CharacterCount > 0 && data.IsAlive == false)
				return ESpectatorMode.DefeatedSpectator;

			return ESpectatorMode.None;
		}

		private void UpdateInspect(Gameplay gameplay, NetworkRunner runner, GameMapView mapView)
		{
			EnsureCamera();

			bool mapOpen = mapView != null && mapView.IsMapOpen;

			// Ctrl/Shift switch survivor and WASD orbit only in the 3D view; while the map is open those keys
			// belong to the map (selection cycling and panning).
			if (mapOpen == false)
			{
				HandleCycleInput(gameplay, runner);
				Camera.HandleOrbitInput();
			}

			if (IsInspectable(_inspectTarget, runner) == false)
				_inspectTarget = FindReplacementInspectTarget(gameplay, runner);

			if (_inspectTarget == null)
			{
				DisableSpectator();
				return;
			}

			_spectatedOwner = _inspectTarget.OwnerRef;

			// The raid host starts in the map (their RTS tool) but can close it; spectators start in the 3D view.
			if (IsRaidCommander && _hasAutoOpenedMap == false && mapView != null)
			{
				mapView.OpenMap();
				_hasAutoOpenedMap = true;
			}

			Camera.SetTarget(_inspectTarget);
		}

		private void HandleCycleInput(Gameplay gameplay, NetworkRunner runner)
		{
			var keyboard = Keyboard.current;
			if (keyboard == null)
				return;

			bool nextPressed = keyboard.leftCtrlKey.wasPressedThisFrame || keyboard.rightCtrlKey.wasPressedThisFrame;
			bool previousPressed = keyboard.leftShiftKey.wasPressedThisFrame || keyboard.rightShiftKey.wasPressedThisFrame;
			if (nextPressed == previousPressed)
				return;

			Survivor next = FindAdjacentInTeam(gameplay, runner, nextPressed ? 1 : -1);
			if (next != null)
				_inspectTarget = next;
		}

		private bool IsInspectable(Survivor survivor, NetworkRunner runner)
		{
			if (survivor == null || survivor.Object == null || survivor.Object.IsValid == false)
				return false;
			if (survivor.Health == null || survivor.Health.IsAlive == false)
				return false;
			if (CharacterFactionUtility.IsPlayerOwnedSurvivor(survivor) == false)
				return false; // never inspect neutrals
			// Raid host watches only its own team; a defeated spectator watches any team.
			if (Mode == ESpectatorMode.RaidCommander && survivor.OwnerRef != runner.LocalPlayer)
				return false;

			return true;
		}

		// Replacement when the current target dies/disappears: stay within the watched team if it still has
		// anyone, otherwise (defeated spectator) fall back to any inspectable survivor on the map.
		private Survivor FindReplacementInspectTarget(Gameplay gameplay, NetworkRunner runner)
		{
			PlayerRef preferredOwner = IsRaidCommander ? runner.LocalPlayer : _spectatedOwner;
			if (preferredOwner.IsRealPlayer)
			{
				var sameTeam = FindFirstAliveOfOwner(gameplay, runner, preferredOwner);
				if (sameTeam != null)
					return sameTeam;
			}

			return CanInspectAnyTeam ? FindAnyInspectable(runner) : null;
		}

		private Survivor FindFirstAliveOfOwner(Gameplay gameplay, NetworkRunner runner, PlayerRef owner)
		{
			if (gameplay.PlayerData.TryGet(owner, out var data) == false)
				return null;

			for (int i = 0; i < data.CharacterCount; i++)
			{
				var survivor = gameplay.GetSurvivor(owner, i);
				if (IsInspectable(survivor, runner))
					return survivor;
			}

			return null;
		}

		// Next/previous inspectable survivor within the current target's owner team, wrapping around.
		private Survivor FindAdjacentInTeam(Gameplay gameplay, NetworkRunner runner, int direction)
		{
			if (_inspectTarget == null)
				return null;

			PlayerRef owner = _inspectTarget.OwnerRef;
			if (gameplay.PlayerData.TryGet(owner, out var data) == false)
				return null;

			int count = Mathf.Max(0, data.CharacterCount);
			if (count <= 0)
				return null;

			int startIndex = _inspectTarget.CharacterIndex;
			for (int step = 1; step <= count; step++)
			{
				int index = ((startIndex + direction * step) % count + count) % count;
				var survivor = gameplay.GetSurvivor(owner, index);
				if (IsInspectable(survivor, runner))
					return survivor;
			}

			return null;
		}

		private Survivor FindAnyInspectable(NetworkRunner runner)
		{
			var sensors = CharacterSensor.ActiveSensors;
			for (int i = sensors.Count - 1; i >= 0; i--)
			{
				var sensor = sensors[i];
				if (sensor == null)
				{
					sensors.RemoveAt(i);
					continue;
				}

				if (IsInspectable(sensor.Survivor, runner))
					return sensor.Survivor;
			}

			return null;
		}

		private void EnsureCamera()
		{
			if (Camera == null)
				Camera = GetComponent<SpectatorCamera>() ?? gameObject.AddComponent<SpectatorCamera>();
		}

		// Editor convenience: pre-wire the reference to the required SpectatorCamera when this component is added,
		// so its tunables are visible/editable next to the controller.
		private void Reset()
		{
			Camera = GetComponent<SpectatorCamera>();
		}

		private void DisableSpectator()
		{
			_inspectTarget = null;
			_spectatedOwner = default;
			_hasAutoOpenedMap = false;

			if (Camera != null)
				Camera.Disable();
		}
	}
}
