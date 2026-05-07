using UnityEngine;
using Game.Player;

namespace Game.Core
{
    /// <summary>
    /// MenuState implementation of IGameState.
    /// Disables player input and camera control when in menu.
    /// Responsibilities: enable/disable player controls, manage menu UI state.
    /// </summary>
    public class MenuState : MonoBehaviour, IGameState
    {
        private const string MenuSceneName = "Menu";
        private static bool _hasLoggedExpectedMenuOptionalBindings;
        private static bool _hasLoggedUnexpectedMissingBindings;

        // Optional in the Menu scene where no player rig exists.
        [SerializeField] private PlayerInputHandler _playerInputHandler;
        // Optional in the Menu scene where no first-person camera rig exists.
        [SerializeField] private FirstPersonCamera _firstPersonCamera;

        public void OnStateEnter()
        {
            PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.Menu);
            LogBindingExpectations();
            DisablePlayerInput();
        }

        public void OnStateUpdate()
        {
        }

        public void OnStateExit()
        {
            PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.FreePlay);
            EnablePlayerInput();
        }

        private void DisablePlayerInput()
        {
            if (_playerInputHandler != null)
            {
                _playerInputHandler.enabled = false;
            }

            if (_firstPersonCamera != null)
            {
                _firstPersonCamera.enabled = false;
            }
        }

        private void EnablePlayerInput()
        {
            if (_playerInputHandler != null)
            {
                _playerInputHandler.enabled = true;
            }

            if (_firstPersonCamera != null)
            {
                _firstPersonCamera.enabled = true;
            }
        }

        private void LogBindingExpectations()
        {
            bool missingAny = _playerInputHandler == null || _firstPersonCamera == null;
            if (!missingAny)
            {
                return;
            }

            string activeSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            bool isMenuScene = string.Equals(activeSceneName, MenuSceneName, System.StringComparison.Ordinal);
            if (isMenuScene)
            {
                if (_hasLoggedExpectedMenuOptionalBindings)
                {
                    return;
                }

                _hasLoggedExpectedMenuOptionalBindings = true;
                Debug.Log(
                    "[MenuState] PlayerInputHandler/FirstPersonCamera bindings are optional in Menu scene and may remain unassigned.",
                    this);
                return;
            }

            if (_hasLoggedUnexpectedMissingBindings)
            {
                return;
            }

            _hasLoggedUnexpectedMissingBindings = true;
            Debug.LogWarning(
                $"[MenuState] Missing MenuState bindings outside Menu scene '{activeSceneName}'. " +
                $"playerInputAssigned={_playerInputHandler != null}, firstPersonCameraAssigned={_firstPersonCamera != null}.",
                this);
        }
    }
}

