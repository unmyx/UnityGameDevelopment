using UnityEngine;
using Game.Input;
using Game.Core;
using Game.Minigames;

namespace Game.Player
{
    /// <summary>
    /// PlayerInputHandler manages all input for the player character.
    /// It serves as a bridge between the New Input System and the PlayerController.
    /// 
    /// This class:
    /// - Polls input from InputManager
    /// - Provides a clean interface for movement, look, jump, and sprint input
    /// - Handles input state management (jump press vs held, etc.)
    /// - Separates input logic from movement logic
    /// 
    /// The PlayerController consumes these inputs to move and control the character.
    /// </summary>
    public class PlayerInputHandler : MonoBehaviour
    {
        private const string LocalPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;

        public Vector2 MovementInput { get; private set; }
        public Vector2 LookInput { get; private set; }
        public bool JumpPressed { get; private set; }
        public bool SprintHeld { get; private set; }

        public bool CrouchPressed { get; private set; }

        private void OnEnable()
        {
            PlayerContextRegistry.RegisterOrUpdate(this, LocalPlayerId);
        }

        private void OnDisable()
        {
            PlayerContextRegistry.Unregister(this, LocalPlayerId);
        }

        private void Update()
        {
            PlayerContextRegistry.RegisterOrUpdate(this, LocalPlayerId);
            if (!IsLocallyOwned())
            {
                ResetInputs();
                return;
            }

            if (PauseManager.TryGetInstance(out PauseManager pauseManager) && pauseManager.IsPaused)
            {
                ResetInputs();
                return;
            }

            if (CanPollInput())
            {
                PollInput();
                return;
            }

            ResetInputs();
        }

        private void PollInput()
        {
            InputManager inputManager = InputManager.Instance;
            if (inputManager == null)
            {
                ResetInputs();
                return;
            }

            MovementInput = inputManager.GetMovementInput();
            LookInput = inputManager.GetLookInput();
            JumpPressed = inputManager.IsJumpPressed();
            SprintHeld = inputManager.IsSprintPressed();
            CrouchPressed = inputManager.IsCrouchPressed();
        }

        public void ResetFrameInputs()
        {
        }

        public Vector2 GetNormalizedMovement()
        {
            return MovementInput.normalized;
        }

        public bool IsMoving()
        {
            return MovementInput.magnitude > 0;
        }

        private void ResetInputs()
        {
            MovementInput = Vector2.zero;
            LookInput = Vector2.zero;
            JumpPressed = false;
            SprintHeld = false;
            CrouchPressed = false;
        }

        private bool CanPollInput()
        {
            if (PlayerContextLocator.TryGetLocalPresentationMode(out LocalPlayerPresentationMode mode)
                && mode != LocalPlayerPresentationMode.Unknown
                && mode != LocalPlayerPresentationMode.FreePlay)
            {
                return false;
            }

            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager != null && minigameManager.IsMinigameActiveForOwner(LocalPlayerId))
            {
                return false;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return true;
            }

            return gameManager.CurrentState == GameState.FreePlay;
        }

        private bool IsLocallyOwned()
        {
            return PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext)
                   && localContext != null
                   && localContext.InputHandler == this;
        }
    }
}

