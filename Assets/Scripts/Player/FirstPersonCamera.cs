using UnityEngine;
using Game.Input;
using Game.Core;
using Game.Minigames;

namespace Game.Player
{
    /// <summary>
    /// FirstPersonCamera handles all camera rotation for the player.
    /// Separate from PlayerController for cleaner architecture.
    /// 
    /// Features:
    /// - Mouse look with smooth input handling
    /// - Horizontal rotation applied to parent (player body)
    /// - Vertical rotation applied to self (camera only)
    /// - Vertical rotation clamped to prevent over-rotation
    /// - Adjustable mouse sensitivity
    /// - Works independently via InputManager
    /// 
    /// Setup:
    /// 1. Place this script on the Camera child object of the player
    /// 2. The parent of this Camera should be the Player root (with PlayerController)
    /// 3. Configure sensitivity in Inspector
    /// 
    /// Example Hierarchy:
    /// Player (PlayerController)
    ///   └── Camera (this script here)
    /// </summary>
    public class FirstPersonCamera : MonoBehaviour
    {
        private const string LocalPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;
        private const string CursorAuthorityOwner = "freeplay_camera";

        [Header("Rotation Targets")]
        [SerializeField]
        [Tooltip("Transform that receives horizontal (yaw) rotation. If not assigned, it auto-resolves from hierarchy.")]
        private Transform _horizontalRotationTarget;

        [Header("Sensitivity")]
        [SerializeField]
        private float _mouseSensitivity = 0.46f;

        [Header("Vertical Look Limits")]
        [SerializeField]
        private float _verticalClampMin = -80f;

        [SerializeField]
        private float _verticalClampMax = 80f;

        [Header("Smoothing")]
        [SerializeField]
        private bool _useSmoothInput = true;

        [SerializeField]
        [Range(0.1f, 1f)]
        private float _smoothInputSpeed = 0.1f;

        [Header("Cursor")]
        [SerializeField]
        [Tooltip("When true, free gameplay keeps cursor locked and hidden for mouse-look.")]
        private bool _lockCursorDuringFreePlay = true;

        [SerializeField]
        [Tooltip("Number of look-input frames to suppress after startup/focus/lock transitions.")]
        [Range(0, 8)]
        private int _startupLookIgnoreFrames = 2;

        [Header("Local Body Visibility")]
        [SerializeField]
        [Tooltip("When true, this camera hides only its own local body renderers using layer-based culling.")]
        private bool _hideLocalBodyFromOwnCamera = true;

        [SerializeField]
        [Tooltip("Dedicated layer used for local player body visuals hidden from this camera.")]
        private string _localBodyHiddenLayerName = "LocalPlayerBody";

        [SerializeField]
        [Tooltip("Optional root for local body visuals. Defaults to camera parent (typically PlayerBody).")]
        private Transform _localBodyVisualRoot;

        [SerializeField]
        [Tooltip("Optional visible roots to keep renderers visible (for future first-person hands/items not parented to camera).")]
        private Transform[] _firstPersonVisibleRoots;

        private float _verticalRotation = 0f;
        private Vector2 _currentLookInput = Vector2.zero;
        private Vector2 _inputVelocity = Vector2.zero;
        private int _pendingLookIgnoreFrames;
        private bool _wasGameplayCursorLocked;
        private bool _wasPausedLastFrame;
        private int _localBodyHiddenLayer = -1;

        private void OnEnable()
        {
            RegisterPlayerContext();
        }

        private void OnDisable()
        {
            PlayerContextRegistry.Unregister(this, LocalPlayerId);
        }

        private void Start()
        {
            ResolveHorizontalRotationTarget();
            InitializeCameraRotationFromCurrentPose();
            ConfigureLocalBodyVisibility();
            ResetLookInputState(_startupLookIgnoreFrames);
            RegisterPlayerContext();
        }

        private void Update()
        {
            RegisterPlayerContext();
            bool isPaused = PauseManager.TryGetInstance(out PauseManager pauseManager) && pauseManager.IsPaused;

            UpdateGameplayCursorState(isPaused);

            if (isPaused)
            {
                _wasPausedLastFrame = true;
                return;
            }

            if (_wasPausedLastFrame)
            {
                ResetLookInputState(GetLookIgnoreFramesForContext());
                _wasPausedLastFrame = false;
            }

            if (CanProcessCameraInput())
            {
                UpdateCameraRotation();
                return;
            }

            _currentLookInput = Vector2.zero;
            _inputVelocity = Vector2.zero;
        }

        private void UpdateCameraRotation()
        {
            if (InputManager.Instance == null)
            {
                return;
            }

            Vector2 rawLookInput = InputManager.Instance.GetLookInput();

            if (_pendingLookIgnoreFrames > 0)
            {
                rawLookInput = Vector2.zero;
                _pendingLookIgnoreFrames--;
            }

            if (_useSmoothInput)
            {
                _currentLookInput = Vector2.SmoothDamp(
                    _currentLookInput,
                    rawLookInput,
                    ref _inputVelocity,
                    _smoothInputSpeed
                );
            }
            else
            {
                _currentLookInput = rawLookInput;
            }
            RotateHorizontal(_currentLookInput.x);
            RotateVertical(_currentLookInput.y);
        }

        private void RotateHorizontal(float horizontalInput)
        {
            if (!ResolveHorizontalRotationTarget())
            {
                return;
            }

            float yDelta = horizontalInput * _mouseSensitivity;
            _horizontalRotationTarget.Rotate(0, yDelta, 0, Space.Self);
        }

        private void RotateVertical(float verticalInput)
        {
            float xDelta = -verticalInput * _mouseSensitivity;
            _verticalRotation += xDelta;
            _verticalRotation = Mathf.Clamp(_verticalRotation, _verticalClampMin, _verticalClampMax);
            transform.localRotation = Quaternion.Euler(_verticalRotation, 0, 0);
        }

        public void SetHorizontalRotation(float yRotation)
        {
            if (ResolveHorizontalRotationTarget())
            {
                _horizontalRotationTarget.rotation = Quaternion.Euler(0, yRotation, 0);
            }
        }

        public void SetVerticalRotation(float xRotation)
        {
            _verticalRotation = Mathf.Clamp(xRotation, _verticalClampMin, _verticalClampMax);
            transform.localRotation = Quaternion.Euler(_verticalRotation, 0, 0);
        }

        public float GetVerticalRotation()
        {
            return _verticalRotation;
        }

        public float GetHorizontalRotation()
        {
            if (ResolveHorizontalRotationTarget())
            {
                return _horizontalRotationTarget.eulerAngles.y;
            }
            return 0f;
        }

        public void SetMouseSensitivity(float sensitivity)
        {
            _mouseSensitivity = Mathf.Max(0.1f, sensitivity);
        }

        public float GetMouseSensitivity()
        {
            return _mouseSensitivity;
        }

        public void SetSmoothInput(bool enabled)
        {
            _useSmoothInput = enabled;
        }

        public void ResetRotation()
        {
            _verticalRotation = 0f;
            transform.localRotation = Quaternion.identity;

            if (ResolveHorizontalRotationTarget())
            {
                _horizontalRotationTarget.rotation = Quaternion.Euler(0, 0, 0);
            }

            ResetLookInputState(GetLookIgnoreFramesForContext());
        }

        private void InitializeCameraRotationFromCurrentPose()
        {
            _verticalRotation = NormalizeSignedAngle(transform.localEulerAngles.x);
            _verticalRotation = Mathf.Clamp(_verticalRotation, _verticalClampMin, _verticalClampMax);
            transform.localRotation = Quaternion.Euler(_verticalRotation, 0f, 0f);
        }

        private static float NormalizeSignedAngle(float angle)
        {
            if (angle > 180f)
            {
                angle -= 360f;
            }

            return angle;
        }

        private void ResetLookInputState(int ignoreFrames)
        {
            _currentLookInput = Vector2.zero;
            _inputVelocity = Vector2.zero;
            _pendingLookIgnoreFrames = Mathf.Max(0, ignoreFrames);
        }

        private void UpdateGameplayCursorState(bool isPaused)
        {
            if (!_lockCursorDuringFreePlay)
            {
                return;
            }

            if (isPaused)
            {
                _wasGameplayCursorLocked = false;
                return;
            }

            bool shouldLock = CanProcessCameraInput();
            if (!shouldLock)
            {
                if (_wasGameplayCursorLocked)
                {
                    PlayerContextLocator.TryReleaseLocalCursorAuthority(CursorAuthorityOwner);
                }

                _wasGameplayCursorLocked = false;
                return;
            }

            if (PlayerContextLocator.TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
                && presentationState != null)
            {
                if (presentationState.Mode != LocalPlayerPresentationMode.FreePlay
                    || !presentationState.CanAcquireCursorAuthority(CursorAuthorityOwner))
                {
                    _wasGameplayCursorLocked = false;
                    return;
                }
            }

            bool cursorAlreadyLocked = Cursor.lockState == CursorLockMode.Locked && !Cursor.visible;
            if (!cursorAlreadyLocked)
            {
                if (!PlayerContextLocator.TryAcquireLocalCursorAuthority(CursorAuthorityOwner, CursorLockMode.Locked, false))
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }

                ResetLookInputState(GetLookIgnoreFramesForContext());
            }
            else if (!_wasGameplayCursorLocked)
            {
                PlayerContextLocator.TryAcquireLocalCursorAuthority(CursorAuthorityOwner, CursorLockMode.Locked, false);
                ResetLookInputState(GetLookIgnoreFramesForContext());
            }

            _wasGameplayCursorLocked = true;
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                _wasGameplayCursorLocked = false;
                return;
            }

            if (CanProcessCameraInput())
            {
                ResetLookInputState(GetLookIgnoreFramesForContext());
            }
        }

        private int GetLookIgnoreFramesForContext()
        {
            int minimumEditorFrames = Application.isEditor ? 4 : 0;
            return Mathf.Max(_startupLookIgnoreFrames, minimumEditorFrames);
        }

        private void ConfigureLocalBodyVisibility()
        {
            if (!_hideLocalBodyFromOwnCamera)
            {
                return;
            }

            _localBodyHiddenLayer = LayerMask.NameToLayer(_localBodyHiddenLayerName);
            if (_localBodyHiddenLayer < 0)
            {
                Debug.LogWarning($"[FirstPersonCamera] Local body layer '{_localBodyHiddenLayerName}' does not exist; body will remain visible.", this);
                return;
            }

            if (TryGetComponent(out Camera cameraComponent))
            {
                cameraComponent.cullingMask &= ~(1 << _localBodyHiddenLayer);
            }

            Transform visualRoot = ResolveLocalBodyVisualRoot();
            if (visualRoot == null)
            {
                return;
            }

            Renderer[] localRenderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < localRenderers.Length; i++)
            {
                Renderer renderer = localRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                Transform rendererTransform = renderer.transform;
                if (rendererTransform == transform || rendererTransform.IsChildOf(transform))
                {
                    continue;
                }

                if (IsExplicitlyVisibleInFirstPerson(rendererTransform))
                {
                    continue;
                }

                renderer.gameObject.layer = _localBodyHiddenLayer;
            }
        }

        private Transform ResolveLocalBodyVisualRoot()
        {
            if (_localBodyVisualRoot != null)
            {
                return _localBodyVisualRoot;
            }

            return transform.parent;
        }

        private bool IsExplicitlyVisibleInFirstPerson(Transform target)
        {
            if (_firstPersonVisibleRoots == null || target == null)
            {
                return false;
            }

            for (int i = 0; i < _firstPersonVisibleRoots.Length; i++)
            {
                Transform visibleRoot = _firstPersonVisibleRoots[i];
                if (visibleRoot == null)
                {
                    continue;
                }

                if (target == visibleRoot || target.IsChildOf(visibleRoot))
                {
                    return true;
                }
            }

            return false;
        }

        private bool ResolveHorizontalRotationTarget()
        {
            if (_horizontalRotationTarget != null)
            {
                return true;
            }

            Transform parent = transform.parent;
            if (parent == null)
            {
                return false;
            }

            // Preserve legacy behavior by preferring parent.parent when available.
            _horizontalRotationTarget = parent.parent != null ? parent.parent : parent;
            return _horizontalRotationTarget != null;
        }

        private void RegisterPlayerContext()
        {
            PlayerContextRegistry.RegisterOrUpdate(this, LocalPlayerId);
            // TODO(MP-3): Split local camera ownership assignment from hardcoded local player id.
        }

        private bool CanProcessCameraInput()
        {
            if (!IsLocallyOwnedCamera())
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

        private bool IsLocallyOwnedCamera()
        {
            return PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext)
                   && localContext != null
                   && localContext.FirstPersonCamera == this;
        }
    }
}

