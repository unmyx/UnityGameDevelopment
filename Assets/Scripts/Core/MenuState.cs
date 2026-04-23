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
        [SerializeField] private PlayerInputHandler _playerInputHandler;
        [SerializeField] private FirstPersonCamera _firstPersonCamera;

        public void OnStateEnter()
        {
            PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.Menu);
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
    }
}

