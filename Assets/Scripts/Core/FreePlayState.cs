using UnityEngine;
using Game.Player;

namespace Game.Core
{
    /// <summary>
    /// FreePlayState implementation of IGameState.
    /// This state handles the main gameplay loop where the player controls the character.
    /// </summary>
    public class FreePlayState : MonoBehaviour, IGameState
    {
        [SerializeField] private PlayerInputHandler _playerInputHandler;
        [SerializeField] private FirstPersonCamera _firstPersonCamera;
        [SerializeField] private ObjectiveManager _objectiveManager;

        public void OnStateEnter()
        {
            if (!ValidateConfiguration(out string failureReason))
            {
                Debug.LogError($"[FreePlayState] Missing required setup: {failureReason}", this);
                return;
            }

            PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.FreePlay);
            SetPlayerControlEnabled(true);
        }

        public void OnStateUpdate()
        {
        }

        public void OnStateExit()
        {
        }

        public void EnterMinigame()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                Debug.LogError("[FreePlayState] Cannot enter minigame because GameManager is unavailable.", this);
                return;
            }

            gameManager.RequestEnterMinigame();
        }

        public void ReturnToMenu()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                Debug.LogError("[FreePlayState] Cannot return to menu because GameManager is unavailable.", this);
                return;
            }

            gameManager.RequestReturnToMenu();
        }

        public bool ValidateConfiguration(out string failureReason)
        {
            System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>(3);
            if (_playerInputHandler == null)
            {
                missing.Add("PlayerInputHandler reference");
            }

            if (_firstPersonCamera == null)
            {
                missing.Add("FirstPersonCamera reference");
            }

            if (_objectiveManager == null)
            {
                missing.Add("ObjectiveManager reference");
            }

            if (missing.Count == 0)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason = string.Join(", ", missing);
            return false;
        }

        public void RebindObjectiveManager(ObjectiveManager objectiveManager)
        {
            if (objectiveManager == null)
            {
                return;
            }

            _objectiveManager = objectiveManager;
        }

        public bool TryGetAuthoritativePlayerTransform(out Transform playerTransform)
        {
            if (_playerInputHandler != null)
            {
                playerTransform = _playerInputHandler.transform;
                return playerTransform != null;
            }

            if (_firstPersonCamera != null)
            {
                Transform search = _firstPersonCamera.transform;
                while (search != null)
                {
                    if (search.GetComponent<PlayerInputHandler>() != null || search.GetComponent<PlayerController>() != null)
                    {
                        playerTransform = search;
                        return true;
                    }

                    search = search.parent;
                }
            }

            playerTransform = null;
            return false;
        }

        private void SetPlayerControlEnabled(bool enabled)
        {
            if (_playerInputHandler != null)
            {
                _playerInputHandler.enabled = enabled;
            }

            if (_firstPersonCamera != null)
            {
                _firstPersonCamera.enabled = enabled;
            }
        }
    }
}
