using Game.Interaction;
using Game.Player;
using Unity.Netcode;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// Enables local-driving components only for the owning client.
    /// Keeps remote player replicas visual-only for MP-7 sandbox validation.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkPlayerOwnershipGate : NetworkBehaviour
    {
        private PlayerController _playerController;
        private PlayerInputHandler _inputHandler;
        private CharacterController _characterController;
        private FirstPersonCamera _firstPersonCamera;
        private InteractionSystem[] _interactionSystems;
        private Camera[] _cameras;
        private AudioListener[] _audioListeners;

        private bool _cached;

        private void Awake()
        {
            CacheComponents();
        }

        public override void OnNetworkSpawn()
        {
            CacheComponents();
            ApplyOwnershipState(IsOwner);

            if (IsOwner)
            {
                PlayerContextRegistry.TrySetLocalPlayerId(PlayerContextRegistry.DefaultLocalPlayerId);
            }
        }

        public override void OnGainedOwnership()
        {
            ApplyOwnershipState(true);
            PlayerContextRegistry.TrySetLocalPlayerId(PlayerContextRegistry.DefaultLocalPlayerId);
        }

        public override void OnLostOwnership()
        {
            ApplyOwnershipState(false);
            PlayerContextRegistry.ClearLocalPlayerIfMissing();
        }

        public override void OnNetworkDespawn()
        {
            ApplyOwnershipState(false);

            if (IsOwner)
            {
                PlayerContextRegistry.ClearLocalPlayerIfMissing();
            }
        }

        private void CacheComponents()
        {
            if (_cached)
            {
                return;
            }

            _playerController = GetComponent<PlayerController>();
            _inputHandler = GetComponent<PlayerInputHandler>();
            _characterController = GetComponent<CharacterController>();
            _firstPersonCamera = GetComponentInChildren<FirstPersonCamera>(true);
            _interactionSystems = GetComponentsInChildren<InteractionSystem>(true);
            _cameras = GetComponentsInChildren<Camera>(true);
            _audioListeners = GetComponentsInChildren<AudioListener>(true);
            _cached = true;
        }

        private void ApplyOwnershipState(bool isOwner)
        {
            if (_inputHandler != null)
            {
                _inputHandler.enabled = isOwner;
            }

            if (_playerController != null)
            {
                _playerController.enabled = isOwner;
            }

            if (_characterController != null)
            {
                _characterController.enabled = isOwner;
            }

            if (_firstPersonCamera != null)
            {
                _firstPersonCamera.enabled = isOwner;
            }

            if (_interactionSystems != null)
            {
                for (int i = 0; i < _interactionSystems.Length; i++)
                {
                    InteractionSystem interactionSystem = _interactionSystems[i];
                    if (interactionSystem != null)
                    {
                        interactionSystem.enabled = isOwner;
                    }
                }
            }

            if (_cameras != null)
            {
                for (int i = 0; i < _cameras.Length; i++)
                {
                    Camera cameraComponent = _cameras[i];
                    if (cameraComponent != null)
                    {
                        cameraComponent.enabled = isOwner;
                    }
                }
            }

            if (_audioListeners != null)
            {
                for (int i = 0; i < _audioListeners.Length; i++)
                {
                    AudioListener audioListener = _audioListeners[i];
                    if (audioListener != null)
                    {
                        audioListener.enabled = isOwner;
                    }
                }
            }
        }
    }
}
