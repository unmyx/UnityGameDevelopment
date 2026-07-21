using UnityEngine;
using UnityEngine.InputSystem;

using Game.Core;
using Game.Inventory;

namespace Game.Input
{
    /// <summary>
    /// InputManager is a centralized wrapper for Unity's New Input System.
    /// It provides both event-based and polling-based access to input.
    /// 
    /// Features:
    /// - Singleton pattern for easy global access
    /// - Event callbacks for reactive input handling
    /// - Polling methods for imperative input checking
    /// - Automatic InputActionAsset loading and management
    /// 
    /// Usage:
    /// Events: Subscribe to InputManager.Instance.OnMoveInput += HandleMove;
    /// Polling: Vector2 move = InputManager.Instance.GetMovementInput();
    /// </summary>
    public class InputManager : MonoBehaviour
    {
        private static InputManager _instance;
        private static bool _hasLoggedFallbackInstanceLookup;
        private static bool _hasValidatedResourcePaths;
        public static InputManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindAnyObjectByType<InputManager>();
                    if (_instance != null && !_hasLoggedFallbackInstanceLookup)
                    {
                        _hasLoggedFallbackInstanceLookup = true;
                        Debug.LogWarning(
                            $"[InputManager] Fallback instance scan used in scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'. " +
                            "Resolved via FindAnyObjectByType for bootstrap/recovery compatibility.");
                    }
                }
                return _instance;
            }
        }

        [SerializeField]
        private InputActionAsset _inputActionAsset;

        private InputActionMap _playerActionMap;
        private InputActionMap _uiActionMap;
        private InputAction _moveAction;
        private InputAction _lookAction;
        private InputAction _jumpAction;
        private InputAction _interactAction;
        private InputAction _sprintAction;
        private InputAction _crouchAction;
        private InputAction _pauseAction;
        private readonly InputAction[] _slotSelectActions = new InputAction[InventoryQuickSlotRules.MaxQuickSlots];
        private readonly bool[] _slotSelectTriggeredThisFrame = new bool[InventoryQuickSlotRules.MaxQuickSlots];
        private bool _callbacksSubscribed;
        private bool _isDuplicateInstance;

        private Vector2 _currentMoveInput;
        private Vector2 _currentLookInput;
        private bool _interactPressed;
        private bool _interactTriggeredThisFrame;
        private bool _sprintPressed;
        private bool _crouchPressed;
        private bool _pausePressed;
        private readonly JumpPressLatch _jumpPressLatch = new JumpPressLatch();

        public delegate void OnMovementInputDelegate(Vector2 input);
        public delegate void OnLookInputDelegate(Vector2 input);
        public delegate void OnJumpDelegate();
        public delegate void OnInteractDelegate();
        public delegate void OnSprintDelegate();
        public delegate void OnCrouchDelegate();
        public delegate void OnPauseDelegate();
        public delegate void OnSelectSlotDelegate(int slotIndex);

        public event OnMovementInputDelegate OnMoveInput;
        public event OnLookInputDelegate OnLookInput;
        public event OnJumpDelegate OnJump;
        public event OnInteractDelegate OnInteract;
        public event OnSprintDelegate OnSprint;
        public event OnCrouchDelegate OnCrouch;
        public event OnPauseDelegate OnPause;
        public event OnSelectSlotDelegate OnSelectSlot;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                _isDuplicateInstance = true;
                enabled = false;
                Destroy(gameObject);
                return;
            }

            _instance = this;

            if (_inputActionAsset == null)
            {
                _inputActionAsset = Resources.Load<InputActionAsset>(ResourcePaths.InputActions);
                if (_inputActionAsset == null)
                {
                    _inputActionAsset = UnityEngine.Resources.Load<InputActionAsset>(ResourcePaths.InputActions);
                }
            }

            ValidateConfiguredResourcesOnce();
            ResolveInputActions();
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void ValidateConfiguredResourcesOnce()
        {
            if (_hasValidatedResourcePaths)
            {
                return;
            }

            _hasValidatedResourcePaths = true;
            if (Resources.Load<InputActionAsset>(ResourcePaths.InputActions) == null)
            {
                Debug.LogWarning(
                    $"[InputManager] Missing input action asset at Resources path '{ResourcePaths.InputActions}'.");
            }
        }

        private void OnEnable()
        {
            if (_isDuplicateInstance || _inputActionAsset == null)
            {
                return;
            }

            SubscribeInputCallbacks();
            _inputActionAsset.Enable();
        }

        private void OnDisable()
        {
            if (_isDuplicateInstance)
            {
                return;
            }

            UnsubscribeInputCallbacks();

            if (_inputActionAsset != null)
            {
                _inputActionAsset.Disable();
            }

            ResetCachedInputState();
        }

        private void OnDestroy()
        {
            UnsubscribeInputCallbacks();

            if (_instance == this)
            {
                _instance = null;
            }
        }

        private void Update()
        {
            _interactTriggeredThisFrame = false;
            _jumpPressLatch.ExpireBefore(Time.frameCount);
            for (int i = 0; i < _slotSelectTriggeredThisFrame.Length; i++)
            {
                _slotSelectTriggeredThisFrame[i] = false;
            }

            if (_moveAction != null)
            {
                _currentMoveInput = _moveAction.ReadValue<Vector2>();
            }

            if (_lookAction != null)
            {
                _currentLookInput = _lookAction.ReadValue<Vector2>();
            }

            if (_interactAction != null)
            {
                _interactPressed = _interactAction.IsPressed();
                if (_interactAction.WasPerformedThisFrame())
                {
                    _interactTriggeredThisFrame = true;
                }
            }

            if (_sprintAction != null)
            {
                _sprintPressed = _sprintAction.IsPressed();
            }

            if (_crouchAction != null)
            {
                _crouchPressed = _crouchAction.WasPressedThisFrame();
            }

            if (_pauseAction != null)
            {
                _pausePressed = _pauseAction.WasPressedThisFrame();
            }

            for (int i = 0; i < _slotSelectActions.Length; i++)
            {
                InputAction slotAction = _slotSelectActions[i];
                if (slotAction != null && slotAction.WasPerformedThisFrame())
                {
                    _slotSelectTriggeredThisFrame[i] = true;
                }
            }
        }

        private void ResolveInputActions()
        {
            if (_inputActionAsset == null)
            {
                return;
            }

            _playerActionMap = _inputActionAsset.FindActionMap("Player");
            _uiActionMap = _inputActionAsset.FindActionMap("UI");

            if (_playerActionMap == null)
            {
                return;
            }
            _moveAction = _playerActionMap.FindAction("Move");
            _lookAction = _playerActionMap.FindAction("Look");
            _jumpAction = _playerActionMap.FindAction("Jump");
            _interactAction = _playerActionMap.FindAction("Interact");
            _sprintAction = _playerActionMap.FindAction("Sprint");
            _crouchAction = _playerActionMap.FindAction("Crouch");

            if (_uiActionMap != null)
            {
                _pauseAction = _uiActionMap.FindAction("Cancel");
            }

            for (int i = 0; i < _slotSelectActions.Length; i++)
            {
                string actionName = $"SelectSlot{i + 1}";
                _slotSelectActions[i] = _playerActionMap.FindAction(actionName);
            }
        }

        private void SubscribeInputCallbacks()
        {
            if (_callbacksSubscribed)
            {
                return;
            }

            SubscribePerformed(_moveAction, HandleMovePerformed);
            SubscribePerformed(_lookAction, HandleLookPerformed);
            SubscribePerformed(_jumpAction, HandleJumpPerformed);
            SubscribePerformed(_interactAction, HandleInteractPerformed);
            SubscribePerformed(_sprintAction, HandleSprintPerformed);
            SubscribePerformed(_crouchAction, HandleCrouchPerformed);
            SubscribePerformed(_pauseAction, HandlePausePerformed);

            for (int i = 0; i < _slotSelectActions.Length; i++)
            {
                SubscribePerformed(_slotSelectActions[i], HandleSlotSelectPerformed);
            }

            _callbacksSubscribed = true;
        }

        private void UnsubscribeInputCallbacks()
        {
            if (!_callbacksSubscribed)
            {
                return;
            }

            UnsubscribePerformed(_moveAction, HandleMovePerformed);
            UnsubscribePerformed(_lookAction, HandleLookPerformed);
            UnsubscribePerformed(_jumpAction, HandleJumpPerformed);
            UnsubscribePerformed(_interactAction, HandleInteractPerformed);
            UnsubscribePerformed(_sprintAction, HandleSprintPerformed);
            UnsubscribePerformed(_crouchAction, HandleCrouchPerformed);
            UnsubscribePerformed(_pauseAction, HandlePausePerformed);

            for (int i = 0; i < _slotSelectActions.Length; i++)
            {
                UnsubscribePerformed(_slotSelectActions[i], HandleSlotSelectPerformed);
            }

            _callbacksSubscribed = false;
        }

        private static void SubscribePerformed(InputAction action, System.Action<InputAction.CallbackContext> callback)
        {
            if (action != null)
            {
                action.performed += callback;
            }
        }

        private static void UnsubscribePerformed(InputAction action, System.Action<InputAction.CallbackContext> callback)
        {
            if (action != null)
            {
                action.performed -= callback;
            }
        }

        private void HandleMovePerformed(InputAction.CallbackContext context)
        {
            OnMoveInput?.Invoke(context.ReadValue<Vector2>());
        }

        private void HandleLookPerformed(InputAction.CallbackContext context)
        {
            OnLookInput?.Invoke(context.ReadValue<Vector2>());
        }

        private void HandleJumpPerformed(InputAction.CallbackContext context)
        {
            if (!_jumpPressLatch.RegisterPress(Time.frameCount))
            {
                return;
            }

            OnJump?.Invoke();
        }

        private void HandleInteractPerformed(InputAction.CallbackContext context)
        {
            _interactTriggeredThisFrame = true;
            OnInteract?.Invoke();
        }

        private void HandleSprintPerformed(InputAction.CallbackContext context)
        {
            OnSprint?.Invoke();
        }

        private void HandleCrouchPerformed(InputAction.CallbackContext context)
        {
            OnCrouch?.Invoke();
        }

        private void HandlePausePerformed(InputAction.CallbackContext context)
        {
            OnPause?.Invoke();
        }

        private void HandleSlotSelectPerformed(InputAction.CallbackContext context)
        {
            for (int i = 0; i < _slotSelectActions.Length; i++)
            {
                if (_slotSelectActions[i] != context.action)
                {
                    continue;
                }

                _slotSelectTriggeredThisFrame[i] = true;
                OnSelectSlot?.Invoke(i);
                return;
            }
        }

        private void ResetCachedInputState()
        {
            _currentMoveInput = Vector2.zero;
            _currentLookInput = Vector2.zero;
            _jumpPressLatch.Clear();
            _interactPressed = false;
            _interactTriggeredThisFrame = false;
            _sprintPressed = false;
            _crouchPressed = false;
            _pausePressed = false;

            for (int i = 0; i < _slotSelectTriggeredThisFrame.Length; i++)
            {
                _slotSelectTriggeredThisFrame[i] = false;
            }
        }

        public Vector2 GetMovementInput()
        {
            return _currentMoveInput;
        }
        public Vector2 GetLookInput()
        {
            return _currentLookInput;
        }

        public bool IsJumpPressed()
        {
            return _jumpPressLatch.HasPendingPress(Time.frameCount);
        }

        public bool TryConsumeJumpPress()
        {
            return _jumpPressLatch.TryConsume(Time.frameCount);
        }

        public void DiscardPendingJumpPress()
        {
            _jumpPressLatch.Clear();
        }

        public bool IsInteractPressed()
        {
            return _interactTriggeredThisFrame;
        }

        public bool IsInteractHeld()
        {
            return _interactPressed;
        }

        public bool IsSprintPressed()
        {
            return _sprintPressed;
        }

        public bool IsCrouchPressed()
        {
            return _crouchPressed;
        }

        public bool IsPausePressed()
        {
            return _pausePressed;
        }

        public bool WasSelectSlotPressed(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= _slotSelectTriggeredThisFrame.Length)
            {
                return false;
            }

            return _slotSelectTriggeredThisFrame[slotIndex];
        }

        public InputActionAsset GetInputActionAsset()
        {
            return _inputActionAsset;
        }

        public void EnableInput()
        {
            if (_inputActionAsset != null)
            {
                _inputActionAsset.Enable();
            }
        }

        public void DisableInput()
        {
            if (_inputActionAsset != null)
            {
                _inputActionAsset.Disable();
            }
        }
    }
}

