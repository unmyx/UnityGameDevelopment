using Game.Core;
using Game.Input;
using Game.Minigames;
using Game.Networking;
using Game.Player;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        private static readonly HashSet<string> LoggedFallbackValidationWarnings = new HashSet<string>();
        private const string LocalPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;
        private const float DefaultRaycastRange = 4f;

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

        private InteractionTarget _currentTarget;
        private InteractionTarget _queuedTarget;
        private InputManager _inputManager;
        private bool _isSubscribedToInteract;
        private bool _interactRequested;
        private int _interactRequestedFrame = -1;
        private int _lastInteractInputFrame = -1;
        private bool _hasLoggedParentCameraFallback;

        private void OnEnable()
        {
            _lastInteractInputFrame = -1;
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
            CancelQueuedInteraction();
            SetCurrentTarget(default);
        }

        private void Update()
        {
            if (!IsLocallyOwnedInteractionSystem())
            {
                UnsubscribeFromInput();
                CancelQueuedInteraction();
                SetCurrentTarget(default);
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
            if (IsInteractionExecutionBlocked(out _)
                || !TryFindInteractable(out InteractionTarget target))
            {
                SetCurrentTarget(default);
                return;
            }

            SetCurrentTarget(target);
        }

        private void HandleQueuedInteractRequest()
        {
            if (!_interactRequested)
            {
                return;
            }

            InteractionTarget queuedTarget = _queuedTarget;
            int queuedFrame = _interactRequestedFrame;
            CancelQueuedInteraction();

            if (IsInteractionExecutionBlocked(out string blockReason))
            {
                SetCurrentTarget(default);
                if (_enableInteractionLogs)
                {
                    Debug.Log(
                        $"[InteractionSystem] Interact blocked. queuedFrame={queuedFrame}, consumeFrame={Time.frameCount}, reason={blockReason}, queued={GetInteractableDebugName(queuedTarget.Interactable)}");
                }

                return;
            }

            if (!TryFindInteractable(out InteractionTarget finalTarget))
            {
                SetCurrentTarget(default);
                return;
            }

            SetCurrentTarget(finalTarget);
            if (finalTarget.Interactable != queuedTarget.Interactable)
            {
                if (_enableInteractionLogs)
                {
                    Debug.Log(
                        $"[InteractionSystem] Interact skipped: target changed after input. queued={GetInteractableDebugName(queuedTarget.Interactable)}, final={GetInteractableDebugName(finalTarget.Interactable)}");
                }

                return;
            }

            if (_enableInteractionLogs)
            {
                Debug.Log(
                    $"[InteractionSystem] Consuming queued interact. queuedFrame={queuedFrame}, consumeFrame={Time.frameCount}, target={GetInteractableDebugName(finalTarget.Interactable)}");
            }

            InteractWithResolvedTarget(finalTarget);
        }

        private void InteractWithResolvedTarget(InteractionTarget target)
        {
            if (!InteractionTargetResolver.IsValidTarget(target))
            {
                if (_enableInteractionLogs)
                {
                    Debug.Log(
                        $"[InteractionSystem] Interact skipped: resolved target is invalid. target={GetInteractableDebugName(target.Interactable)}");
                }

                return;
            }

            if (TryRouteNetworkInteraction(target.Interactable))
            {
                return;
            }

            if (_enableInteractionLogs)
            {
                Debug.Log($"[InteractionSystem] Calling Interact() on {GetInteractableDebugName(target.Interactable)}.");
            }

            target.Interactable.Interact();
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
            Color rayColor = _currentTarget.Interactable != null ? Color.green : Color.white;
            Debug.DrawRay(ray.origin, ray.direction * GetEffectiveRaycastRange(), rayColor);
        }

        public BaseInteractable GetCurrentInteractable()
        {
            return _currentTarget.Interactable;
        }

        public bool CanInteractWithCurrent()
        {
            return !IsInteractionExecutionBlocked(out _)
                   && InteractionTargetResolver.IsValidTarget(_currentTarget);
        }

        public bool IsInteractableInRange(BaseInteractable interactable)
        {
            return interactable != null
                   && interactable == _currentTarget.Interactable
                   && InteractionTargetResolver.IsValidTarget(_currentTarget);
        }

        public void SetRaycastRange(float range)
        {
            _raycastRange = IsFinitePositive(range) ? range : DefaultRaycastRange;
        }

        public float GetRaycastRange()
        {
            return GetEffectiveRaycastRange();
        }

        public void SetRaycastLayerMask(LayerMask layerMask)
        {
            _raycastLayerMask = layerMask;
        }

        public int GetEffectiveRaycastLayerMask()
        {
            return InteractionTargetResolver.GetEffectiveLayerMask(_raycastLayerMask);
        }

        public bool TryFindInteractable(out InteractionTarget target)
        {
            target = default;
            if (_raycastCamera == null || !_raycastCamera.isActiveAndEnabled)
            {
                return false;
            }

            Ray ray = new Ray(
                _raycastCamera.transform.position,
                _raycastCamera.transform.forward);
            return InteractionTargetResolver.TryFindInteractable(
                ray,
                GetEffectiveRaycastRange(),
                _raycastLayerMask,
                out target);
        }

        public void ForceInteract(BaseInteractable interactable)
        {
            if (IsInteractionExecutionBlocked(out _))
            {
                return;
            }

            if (TryFindInteractable(out InteractionTarget finalTarget)
                && finalTarget.Interactable == interactable)
            {
                SetCurrentTarget(finalTarget);
                InteractWithResolvedTarget(finalTarget);
                return;
            }

            SetCurrentTarget(default);
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
            CancelQueuedInteraction();
        }

        private void OnInteractPerformed()
        {
            if (!IsLocallyOwnedInteractionSystem())
            {
                CancelQueuedInteraction();
                SetCurrentTarget(default);
                return;
            }

            int inputFrame = Time.frameCount;
            if (_lastInteractInputFrame == inputFrame)
            {
                return;
            }

            _lastInteractInputFrame = inputFrame;
            if (IsInteractionExecutionBlocked(out _)
                || !TryFindInteractable(out InteractionTarget inputTarget))
            {
                CancelQueuedInteraction();
                SetCurrentTarget(default);
                return;
            }

            SetCurrentTarget(inputTarget);
            _queuedTarget = inputTarget;
            _interactRequested = true;
            _interactRequestedFrame = inputFrame;

            if (_enableInteractionLogs)
            {
                Debug.Log(
                    $"[InteractionSystem] Interact event fired. eventFrame={inputFrame}, queued={GetInteractableDebugName(_queuedTarget.Interactable)}");
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

        private void SetCurrentTarget(InteractionTarget target)
        {
            BaseInteractable previousInteractable = _currentTarget.Interactable;
            BaseInteractable nextInteractable = InteractionTargetResolver.IsValidTarget(target)
                ? target.Interactable
                : null;

            if (previousInteractable == nextInteractable)
            {
                _currentTarget = nextInteractable != null ? target : default;
                return;
            }

            if (previousInteractable != null)
            {
                previousInteractable.OnInteractableExit();
            }

            _currentTarget = nextInteractable != null ? target : default;
            if (nextInteractable != null)
            {
                nextInteractable.OnInteractableEnter();
            }
        }

        private void CancelQueuedInteraction()
        {
            _interactRequested = false;
            _interactRequestedFrame = -1;
            _queuedTarget = default;
        }

        private float GetEffectiveRaycastRange()
        {
            return IsFinitePositive(_raycastRange) ? _raycastRange : DefaultRaycastRange;
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
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
                && PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext)
                && localContext != null
                && localContext.FirstPersonCamera != null)
            {
                _raycastCamera = localContext.FirstPersonCamera.GetComponent<Camera>();
            }

            if (_raycastCamera == null
                && PlayerContextLocator.IsCompatibilityFallbackAllowed()
                && PlayerContextLocator.TryGetFirstPersonCamera(out firstPersonCamera)
                && firstPersonCamera != null)
            {
                _raycastCamera = firstPersonCamera.GetComponent<Camera>();
                ValidateCompatibilityCameraFallbackWindow("compatibility_camera");
            }

            if (_raycastCamera == null)
            {
                _raycastCamera = GetComponentInParent<Camera>();
                if (_raycastCamera != null && !_hasLoggedParentCameraFallback)
                {
                    _hasLoggedParentCameraFallback = true;
                    ValidateParentCameraAmbiguity();
                    Debug.LogWarning(
                        "[InteractionSystem] Using parent Camera fallback for raycast camera. " +
                        "Prefer deterministic local PlayerContext camera binding.",
                        this);
                }
            }

            // TODO(MP-4): Remove compatibility fallback once per-player camera bootstrap/rebind is explicit in scene setup.
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void ValidateCompatibilityCameraFallbackWindow(string source)
        {
            FallbackWindow window = PlayerContextLocator.IsCompatibilityFallbackAllowed()
                ? FallbackWindow.Bootstrap
                : FallbackWindow.Stable;
            if (window != FallbackWindow.Stable)
            {
                return;
            }

            string sceneName = SceneManager.GetActiveScene().name;
            string key = $"INT_CAMERA_WINDOW|{sceneName}|{source}";
            if (!LoggedFallbackValidationWarnings.Add(key))
            {
                return;
            }

            Debug.LogWarning(
                $"[FallbackValidation][INT_CAMERA_WINDOW] scene='{sceneName}' source='{source}' window='{FallbackGuardrails.ToToken(window)}' expected='bootstrap|recovery' risk='stable_camera_fallback'",
                this);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void ValidateParentCameraAmbiguity()
        {
            Camera[] activeCameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude);
            int count = activeCameras != null ? activeCameras.Length : 0;
            if (count <= 1)
            {
                return;
            }

            string sceneName = SceneManager.GetActiveScene().name;
            string key = $"INT_PARENT_CAMERA_AMBIGUITY|{sceneName}|{count}";
            if (!LoggedFallbackValidationWarnings.Add(key))
            {
                return;
            }

            Debug.LogWarning(
                $"[FallbackValidation][INT_PARENT_CAMERA_AMBIGUITY] scene='{sceneName}' activeCameras='{count}' chosenCamera='{_raycastCamera?.name ?? "null"}' risk='wrong_camera_bind'",
                this);
        }

        private bool IsLocallyOwnedInteractionSystem()
        {
            return PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext)
                   && localContext != null
                   && localContext.InteractionSystem == this;
        }
    }
}
