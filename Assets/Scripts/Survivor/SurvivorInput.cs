using UnityEngine;
using UnityEngine.InputSystem;
using Fusion;
using Fusion.Addons.SimpleKCC;

namespace SimpleFPS
{
	public enum EInputButton
	{
		Jump,
		Fire,
		Reload,
		Pistol,
		Rifle,
		Shotgun,
		Spray,
		CommandFollow,
		PrevCharacter,
		NextCharacter,
		CommandMove,
		CommandIdle,
		CommandEnableNonCombatAI,
		CommandDisableCombatAI,
		CommandEnableCombatAI,
	}

	/// <summary>
	/// Input structure sent over network to the server.
	/// </summary>
	public struct NetworkedInput : INetworkInput
	{
		public Vector2        MoveDirection;
		public Vector2        LookRotationDelta;
		public NetworkButtons Buttons;
	}

	/// <summary>
	/// Handles player input.
	/// </summary>
	[DefaultExecutionOrder(-10)]
	public sealed class SurvivorInput : NetworkBehaviour, IBeforeUpdate
	{
		public static float LookSensitivity;

		private NetworkedInput     _accumulatedInput;
		private Vector2Accumulator _lookRotationAccumulator = new Vector2Accumulator(0.02f, true);
		private Survivor           _survivor;
		private SceneObjects       _sceneObjects;
		private NetworkEvents      _networkEvents;
		private bool               _inputRegistered;

		public override void Spawned()
		{
			if (HasInputAuthority == false)
				return;

			_survivor = GetComponent<Survivor>();
			_sceneObjects = Runner.GetSingleton<SceneObjects>();

			SyncInputRegistration();

			Cursor.lockState = CursorLockMode.Locked;
			Cursor.visible = false;
		}

		public override void Despawned(NetworkRunner runner, bool hasState)
		{
			if (runner == null)
				return;

			UnregisterInput(runner);
		}

		void IBeforeUpdate.BeforeUpdate()
		{
			// This method is called BEFORE ANY FixedUpdateNetwork() and is used to accumulate input from Keyboard/Mouse.
			// Input accumulation is mandatory - this method is called multiple times before new forward FixedUpdateNetwork() - common if rendering speed is faster than Fusion simulation.

			if (HasInputAuthority == false)
				return;

			SyncInputRegistration();
			if (_inputRegistered == false)
				return;

			ClearFrameInput();

			if (GameMapView.IsAnyMapOpen)
			{
				ResetAccumulatedInput();
				return;
			}

			// Enter key is used for locking/unlocking cursor in game view.
			var keyboard = Keyboard.current;
			if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame))
			{
				if (Cursor.lockState == CursorLockMode.Locked)
				{
					Cursor.lockState = CursorLockMode.None;
					Cursor.visible = true;
				}
				else
				{
					Cursor.lockState = CursorLockMode.Locked;
					Cursor.visible = false;
				}
			}

			// Accumulate input only if the cursor is locked.
			if (Cursor.lockState != CursorLockMode.Locked)
				return;

			var mouse = Mouse.current;
			if (mouse != null)
			{
				var mouseDelta = mouse.delta.ReadValue();

				var lookRotationDelta = new Vector2(-mouseDelta.y, mouseDelta.x);
				lookRotationDelta *= LookSensitivity / 60f;
				_lookRotationAccumulator.Accumulate(lookRotationDelta);

				_accumulatedInput.Buttons.Set(EInputButton.Fire, mouse.leftButton.isPressed);
			}

			if (keyboard != null)
			{
				var moveDirection = Vector2.zero;

				if (keyboard.wKey.isPressed) { moveDirection += Vector2.up;    }
				if (keyboard.sKey.isPressed) { moveDirection += Vector2.down;  }
				if (keyboard.aKey.isPressed) { moveDirection += Vector2.left;  }
				if (keyboard.dKey.isPressed) { moveDirection += Vector2.right; }

				_accumulatedInput.MoveDirection = moveDirection.normalized;

				_accumulatedInput.Buttons.Set(EInputButton.Jump, keyboard.spaceKey.isPressed);
				_accumulatedInput.Buttons.Set(EInputButton.Reload, keyboard.rKey.isPressed);
				_accumulatedInput.Buttons.Set(EInputButton.Pistol, keyboard.digit1Key.isPressed || keyboard.numpad1Key.isPressed);
				_accumulatedInput.Buttons.Set(EInputButton.Rifle, keyboard.digit2Key.isPressed || keyboard.numpad2Key.isPressed);
				_accumulatedInput.Buttons.Set(EInputButton.Shotgun, keyboard.digit3Key.isPressed || keyboard.numpad3Key.isPressed);
				_accumulatedInput.Buttons.Set(EInputButton.CommandFollow, keyboard.fKey.isPressed);
				_accumulatedInput.Buttons.Set(EInputButton.CommandMove, keyboard.mKey.isPressed);
				_accumulatedInput.Buttons.Set(EInputButton.PrevCharacter, keyboard.leftShiftKey.isPressed);
				_accumulatedInput.Buttons.Set(EInputButton.NextCharacter, keyboard.leftCtrlKey.isPressed);
			}
		}

		private void OnInput(NetworkRunner runner, NetworkInput networkInput)
		{
			if (_inputRegistered == false || ShouldProvideInput() == false)
				return;

			if (GameMapView.IsAnyMapOpen)
			{
				networkInput.Set(default(NetworkedInput));
				return;
			}

			// Mouse movement (delta values) is aligned to engine update.
			// To get perfectly smooth interpolated look, we need to align the mouse input with Fusion ticks.
			_accumulatedInput.LookRotationDelta = _lookRotationAccumulator.ConsumeTickAligned(runner);

			// Fusion polls accumulated input. This callback can be executed multiple times in a row if there is a performance spike.
			networkInput.Set(_accumulatedInput);
		}

		private void SyncInputRegistration()
		{
			bool shouldRegister = ShouldProvideInput();
			if (shouldRegister == _inputRegistered)
				return;

			if (shouldRegister)
			{
				RegisterInput();
			}
			else
			{
				UnregisterInput(Runner);
			}
		}

		private bool ShouldProvideInput()
		{
			if (HasInputAuthority == false || Object == null || Runner == null)
				return false;

			if (Object.InputAuthority != Runner.LocalPlayer)
				return false;

			var playerObject = Runner.GetPlayerObject(Runner.LocalPlayer);
			if (playerObject != null)
				return playerObject == Object;

			// Fallback for the short spawn/switch window before PlayerObject is available locally.
			if (_survivor == null)
				_survivor = GetComponent<Survivor>();
			if (_survivor == null)
				return false;

			if (_sceneObjects == null && Runner != null)
				_sceneObjects = Runner.GetSingleton<SceneObjects>();

			var gameplay = _sceneObjects != null ? _sceneObjects.Gameplay : null;
			if (gameplay == null)
				return false;

			if (gameplay.PlayerData.TryGet(_survivor.OwnerRef, out var data) == false)
				return false;

			return data.IsAlive && data.ActiveCharacterIndex == _survivor.CharacterIndex;
		}

		private void ClearFrameInput()
		{
			_accumulatedInput.MoveDirection = default;
			_accumulatedInput.LookRotationDelta = default;
			_accumulatedInput.Buttons = default;
		}

		private void RegisterInput()
		{
			var networkEvents = Runner != null ? Runner.GetComponent<NetworkEvents>() : null;
			if (networkEvents == null)
				return;

			// Every team character has SurvivorInput; only the active one should provide
			// Fusion input for the local player.
			networkEvents.OnInput.AddListener(OnInput);
			_networkEvents = networkEvents;
			_inputRegistered = true;
		}

		private void UnregisterInput(NetworkRunner runner)
		{
			var networkEvents = _networkEvents != null ? _networkEvents : runner != null ? runner.GetComponent<NetworkEvents>() : null;
			if (networkEvents != null)
			{
				networkEvents.OnInput.RemoveListener(OnInput);
			}

			_networkEvents = null;
			_inputRegistered = false;
			ResetAccumulatedInput();
		}

		private void ResetAccumulatedInput()
		{
			_accumulatedInput = default;
			_lookRotationAccumulator = new Vector2Accumulator(0.02f, true);
		}
	}
}
