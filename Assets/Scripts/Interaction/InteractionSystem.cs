using Game.Core;
using Game.Input;
using Game.Minigames;
using Game.Networking;
using Game.Player;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// InteractionSystem handles both raycast and trigger-based interactions.
    ///
    /// Features:
    /// - Raycasts from camera center to detect interactables
    /// - Configurable raycast range
    /// - Highlights/displays current interactable target
    /// - Debug visualization of raycasts
    /// - Input handling (E key)
    ///
    /// Architecture:
    /// - Raycast Interaction: Primary (direct interaction with objects)
    ///
    /// Setup:
    /// 1. Place this on the player or a child object
    /// 2. Assign the camera this script should raycast from
    /// 3. Configure raycast range and layers
    /// 4. BaseInteractable objects will be auto-detected by raycast
    /// 5. BaseInteractable objects are handled through raycast interaction.
    /// </summary>
    public class InteractionSystem : MonoBehaviour
    {
        private const string LocalPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;

        [Header("Raycast Interaction")]
        [SerializeField]
        private Camera _raycastCamera;

        [SerializeField]
        private float _raycastRange = 4f;

        [SerializeField]
        private LayerMask _raycastLayerMask = -1;

        [SerializeField]
        private bool _showDebugRay = true;

        [Header("Debug")]
        [SerializeField]
        private bool _enableInteractionLogs = true;

        private BaseInteractable _currentInteractable;
        private BaseInteractable _queuedInteractable;
        private InputManager _inputManager;
        private bool _isSubscribedToInteract;
        private bool _interactRequested;
        private int _interactRequestedFrame = -1;

        private void OnEnable()
        {
            PlayerContextRegistry.RegisterOrUpdate(this, LocalPlayerId);
            TryResolveRaycastCameraFromContext();

            if (_raycastCamera == null)
            {
                Debug.LogError(
                    "[InteractionSystem] Missing Raycast Camera reference. Assign InteractionSystem._raycastCamera explicitly.",
                    this);
                enabled = false;
                return;
            }

            TrySubscribeToInput();
        }

        private void OnDisable()
        {
            PlayerContextRegistry.Unregister(this, LocalPlayerId);
            UnsubscribeFromInput();
        }

        private void Update()
        {
            if (!IsLocallyOwnedInteractionSystem())
            {
                UnsubscribeFromInput();
                return;
            }

            TryResolveRaycastCameraFromContext();
            TrySubscribeToInput();
            UpdateRaycastInteraction();
            HandleQueuedInteractRequest();

            if (_showDebugRay)
            {
                DebugDrawRaycast();
            }
        }

        private void UpdateRaycastInteraction()
        {
            if (_raycastCamera == null)
            {
                return;
            }

            Ray ray = new Ray(_raycastCamera.transform.position, _raycastCamera.transform.forward);
            BaseInteractable newInteractable = null;

            if (Physics.Raycast(ray, out RaycastHit hit, _raycastRange, _raycastLayerMask))
            {
                BaseInteractable interactable = hit.collider.GetComponent<BaseInteractable>();
                if (interactable != null)
                {
                    newInteractable = interactable;
                }
            }

            if (newInteractable != _currentInteractable)
            {
                if (_currentInteractable != null)
                {
                    _currentInteractable.OnInteractableExit();
                }

                _currentInteractable = newInteractable;
                if (_currentInteractable != null)
                {
                    _currentInteractable.OnInteractableEnter();
                }
            }
        }

        private void HandleQueuedInteractRequest()
        {
            if (!_interactRequested)
            {
                return;
            }

            BaseInteractable queuedInteractable = _queuedInteractable;
            int queuedFrame = _interactRequestedFrame;

            _interactRequested = false;
            _interactRequestedFrame = -1;
            _queuedInteractable = null;

            if (IsInteractionExecutionBlocked(out string blockReason))
            {
                if (_enableInteractionLogs)
                {
                    Debug.Log(
                        $"[InteractionSystem] Interact blocked. queuedFrame={queuedFrame}, consumeFrame={Time.frameCount}, reason={blockReason}, queued={GetInteractableDebugName(queuedInteractable)}");
                }

                return;
            }

            if (_enableInteractionLogs)
            {
                Debug.Log(
                    $"[InteractionSystem] Consuming queued interact. queuedFrame={queuedFrame}, consumeFrame={Time.frameCount}, queued={GetInteractableDebugName(queuedInteractable)}, current={GetInteractableDebugName(_currentInteractable)}");
            }

            InteractWithCaptured(queuedInteractable);
        }

        private void InteractWithCaptured(BaseInteractable capturedInteractable)
        {
            if (!IsValidInteractableForExecution(capturedInteractable))
            {
                if (_enableInteractionLogs)
                {
                    Debug.Log(
                        $"[InteractionSystem] Interact skipped: captured target is invalid. captured={GetInteractableDebugName(capturedInteractable)}");
                }

                return;
            }

            if (TryRouteNetworkInteraction(capturedInteractable))
            {
                return;
            }

            if (_enableInteractionLogs)
            {
                Debug.Log($"[InteractionSystem] Calling Interact() on {GetInteractableDebugName(capturedInteractable)}.");
            }

            capturedInteractable.Interact();
        }

        private bool TryRouteNetworkInteraction(BaseInteractable capturedInteractable)
        {
            if (capturedInteractable == null)
            {
                return false;
            }

            NetworkInteractionAuthorityBridge authorityBridge =
                capturedInteractable.GetComponent<NetworkInteractionAuthorityBridge>();
            if (authorityBridge == null || !authorityBridge.ShouldRouteThroughNetworkAuthority())
            {
                return false;
            }

            if (_enableInteractionLogs)
            {
                Debug.Log($"[InteractionSystem] Routing interaction through network authority bridge for {GetInteractableDebugName(capturedInteractable)}.");
            }

            authorityBridge.RequestAuthoritativeInteraction();
            return true;
        }

        private void DebugDrawRaycast()
        {
            if (_raycastCamera == null)
            {
                return;
            }

            Ray ray = new Ray(_raycastCamera.transform.position, _raycastCamera.transform.forward);
            Color rayColor = _currentInteractable != null ? Color.green : Color.white;
            Debug.DrawRay(ray.origin, ray.direction * _raycastRange, rayColor);
        }

        public BaseInteractable GetCurrentInteractable()
        {
            return _currentInteractable;
        }

        public bool CanInteractWithCurrent()
        {
            return _currentInteractable != null && _currentInteractable.CanInteract;
        }

        public bool IsInteractableInRange(BaseInteractable interactable)
        {
            return interactable == _currentInteractable;
        }

        public void SetRaycastRange(float range)
        {
            _raycastRange = Mathf.Max(0.1f, range);
        }

        public float GetRaycastRange()
        {
            return _raycastRange;
        }

        public void ForceInteract(BaseInteractable interactable)
        {
            if (IsInteractionExecutionBlocked(out _))
            {
                return;
            }

            if (IsValidInteractableForExecution(interactable))
            {
                interactable.Interact();
            }
        }

        private void TrySubscribeToInput()
        {
            if (_isSubscribedToInteract)
            {
                return;
            }

            if (!IsLocallyOwnedInteractionSystem())
            {
                return;
            }

            _inputManager = InputManager.Instance;
            if (_inputManager == null)
            {
                return;
            }

            _inputManager.OnInteract += OnInteractPerformed;
            _isSubscribedToInteract = true;
        }

        private void UnsubscribeFromInput()
        {
            if (!_isSubscribedToInteract)
            {
                return;
            }

            if (_inputManager != null)
            {
                _inputManager.OnInteract -= OnInteractPerformed;
            }

            _isSubscribedToInteract = false;
            _inputManager = null;
            _interactRequested = false;
            _queuedInteractable = null;
            _interactRequestedFrame = -1;
        }

        private void OnInteractPerformed()
        {
            _queuedInteractable = IsValidInteractableForExecution(_currentInteractable)
                ? _currentInteractable
                : null;

            _interactRequested = _queuedInteractable != null;
            _interactRequestedFrame = Time.frameCount;

            if (_enableInteractionLogs)
            {
                Debug.Log(
                    $"[InteractionSystem] Interact event fired. eventFrame={Time.frameCount}, currentAtEvent={GetInteractableDebugName(_currentInteractable)}, queued={GetInteractableDebugName(_queuedInteractable)}");
            }
        }

        private bool IsInteractionExecutionBlocked(out string reason)
        {
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null && pauseManager.IsPaused)
            {
                reason = "paused";
                return true;
            }

            if (PlayerContextLocator.TryGetLocalPresentationMode(out LocalPlayerPresentationMode mode)
                && mode != LocalPlayerPresentationMode.Unknown
                && mode != LocalPlayerPresentationMode.FreePlay)
            {
                reason = $"presentation_mode:{mode}";
                return true;
            }

            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager != null && minigameManager.IsMinigameActiveForOwner(LocalPlayerId))
            {
                reason = "minigame_active";
                return true;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                if (gameManager.IsRunFailed())
                {
                    reason = "run_failed";
                    return true;
                }

                if (gameManager.CurrentState != GameState.FreePlay)
                {
                    reason = $"invalid_game_state:{gameManager.CurrentState}";
                    return true;
                }

                if (gameManager.GetCurrentRunPhase() == GameManager.RunPhase.GameOver)
                {
                    reason = "run_phase_game_over";
                    return true;
                }
            }

            reason = string.Empty;
            return false;
        }

        private static bool IsValidInteractableForExecution(BaseInteractable interactable)
        {
            return interactable != null
                   && interactable.isActiveAndEnabled
                   && interactable.gameObject.activeInHierarchy
                   && interactable.CanInteract;
        }

        private static string GetInteractableDebugName(BaseInteractable interactable)
        {
            if (interactable == null)
            {
                return "<null>";
            }

            return $"{interactable.name} ({interactable.GetType().Name})";
        }

        private void TryResolveRaycastCameraFromContext()
        {
            if (_raycastCamera != null)
            {
                return;
            }

            if (PlayerContextLocator.TryGetLocalFirstPersonCamera(out FirstPersonCamera firstPersonCamera)
                && firstPersonCamera != null)
            {
                _raycastCamera = firstPersonCamera.GetComponent<Camera>();
            }

            if (_raycastCamera == null
                && PlayerContextLocator.IsCompatibilityFallbackAllowed()
                && PlayerContextLocator.TryGetFirstPersonCamera(out firstPersonCamera)
                && firstPersonCamera != null)
            {
                _raycastCamera = firstPersonCamera.GetComponent<Camera>();
            }

            if (_raycastCamera == null)
            {
                _raycastCamera = GetComponentInParent<Camera>();
            }

            // TODO(MP-4): Remove compatibility fallback once per-player camera bootstrap/rebind is explicit in scene setup.
        }

        private bool IsLocallyOwnedInteractionSystem()
        {
            return PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext)
                   && localContext != null
                   && localContext.InteractionSystem == this;
        }
    }
}
