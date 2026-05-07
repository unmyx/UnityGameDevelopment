using UnityEngine;
using UnityEngine.InputSystem;

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
        private readonly InputAction[] _slotSelectActions = new InputAction[9];
        private readonly bool[] _slotSelectTriggeredThisFrame = new bool[9];

        private Vector2 _currentMoveInput;
        private Vector2 _currentLookInput;
        private bool _jumpPressed;
        private bool _interactPressed;
        private bool _interactTriggeredThisFrame;
        private bool _sprintPressed;
        private bool _crouchPressed;
        private bool _pausePressed;

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
                Destroy(gameObject);
                return;
            }

            _instance = this;

            if (_inputActionAsset == null)
            {
                _inputActionAsset = Resources.Load<InputActionAsset>("InputSystem_Actions");
                if (_inputActionAsset == null)
                {
                    _inputActionAsset = UnityEngine.Resources.Load<InputActionAsset>("InputSystem_Actions");
                }
            }

            InitializeInputActions();
        }

        private void OnEnable()
        {
            if (_inputActionAsset != null)
            {
                _inputActionAsset.Enable();
            }
        }

        private void OnDisable()
        {
            if (_inputActionAsset != null)
            {
                _inputActionAsset.Disable();
            }
        }

        private void Update()
        {
            _interactTriggeredThisFrame = false;
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

            if (_jumpAction != null)
            {
                _jumpPressed = _jumpAction.IsPressed();
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

        private void InitializeInputActions()
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
            if (_moveAction != null)
            {
                _moveAction.performed += ctx => OnMoveInput?.Invoke(ctx.ReadValue<Vector2>());
            }

            _lookAction = _playerActionMap.FindAction("Look");
            if (_lookAction != null)
            {
                _lookAction.performed += ctx => OnLookInput?.Invoke(ctx.ReadValue<Vector2>());
            }

            _jumpAction = _playerActionMap.FindAction("Jump");
            if (_jumpAction != null)
            {
                _jumpAction.performed += ctx => OnJump?.Invoke();
            }

            _interactAction = _playerActionMap.FindAction("Interact");
            if (_interactAction != null)
            {
                _interactAction.performed += ctx =>
                {
                    _interactTriggeredThisFrame = true;
                    OnInteract?.Invoke();
                };
            }

            _sprintAction = _playerActionMap.FindAction("Sprint");
            if (_sprintAction != null)
            {
                _sprintAction.performed += ctx => OnSprint?.Invoke();
            }

            _crouchAction = _playerActionMap.FindAction("Crouch");
            if (_crouchAction != null)
            {
                _crouchAction.performed += ctx => OnCrouch?.Invoke();
            }

            if (_uiActionMap != null)
            {
                _pauseAction = _uiActionMap.FindAction("Cancel");
                if (_pauseAction != null)
                {
                    _pauseAction.performed += ctx => OnPause?.Invoke();
                }
            }

            for (int i = 0; i < _slotSelectActions.Length; i++)
            {
                int slotIndex = i;
                string actionName = $"SelectSlot{slotIndex + 1}";
                _slotSelectActions[slotIndex] = _playerActionMap.FindAction(actionName);

                if (_slotSelectActions[slotIndex] != null)
                {
                    _slotSelectActions[slotIndex].performed += ctx =>
                    {
                        _slotSelectTriggeredThisFrame[slotIndex] = true;
                        OnSelectSlot?.Invoke(slotIndex);
                    };
                }
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
            return _jumpPressed;
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

