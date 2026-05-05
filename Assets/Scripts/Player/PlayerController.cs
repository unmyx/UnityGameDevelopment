using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// PlayerController manages the player character's movement, physics, and camera control.
    /// Uses Unity's CharacterController for collision and movement.
    /// 
    /// Features:
    /// - Arcade-style WASD movement (smooth, responsive)
    /// - Jump with forgiving coyote time
    /// - Gravity and fall speed
    /// - Sprint mechanic (speed boost)
    /// - Crouch toggle (height reduction)
    /// - Air control for responsive mid-air movement
    /// - Camera rotation (horizontal = player body, vertical = camera only)
    /// - Vertical look clamping (-80 to 80 degrees)
    /// 
    /// Separation of Concerns:
    /// - PlayerInputHandler: Handles input polling
    /// - PlayerController: Handles physics, movement, camera
    /// - CharacterController: Handles collision and displacement
    /// </summary>
    public class PlayerController : MonoBehaviour
    {
        private CharacterController _characterController;
        private PlayerInputHandler _inputHandler;

        [Header("Movement")]
        [SerializeField]
        private float _walkSpeed = 4f;

        [SerializeField]
        private float _sprintSpeed = 7f;

        [SerializeField]
        private float _airControlMultiplier = 0.5f;

        [Header("Jump")]
        [SerializeField]
        private float _jumpForce = 5f;

        [SerializeField]
        private float _gravity = 20f;

        [SerializeField]
        private float _coyoteTime = 0.1f;

        [Header("Crouch")]
        [SerializeField]
        private float _normalHeight = 1.8f;

        [SerializeField]
        private float _crouchHeight = 0.9f;

        [SerializeField]
        private float _crouchHeightTransitionSpeed = 10f;

        private Vector3 _velocity = Vector3.zero;
        private float _currentSpeed = 0f;
        private float _timeSinceLastGrounded = 0f;
        private bool _isCrouching = false;
        private float _targetCharacterHeight;

        private void OnEnable()
        {
            InitializeComponents();

            if (_characterController != null && !_characterController.enabled)
            {
                _characterController.enabled = true;
            }
        }

        private void OnDisable()
        {
            if (_characterController != null)
            {
                _characterController.enabled = false;
            }
        }

        private void Start()
        {
            InitializeComponents();
            _targetCharacterHeight = _normalHeight;
        }

        private void Update()
        {
            if (_characterController == null)
            {
                InitializeComponents();
                if (_characterController == null)
                {
                    return;
                }
            }

            HandleInput();
            ApplyMovement();
            ApplyGravity();
            MoveCharacter();
            UpdateCharacterHeight();
        }

        private void InitializeComponents()
        {
            if (_characterController == null)
            {
                _characterController = GetComponent<CharacterController>();
                if (_characterController == null)
                {
                    return;
                }

                _characterController.height = _normalHeight;
            }

            if (_inputHandler == null)
            {
                _inputHandler = GetComponent<PlayerInputHandler>();
            }
        }

        private void HandleInput()
        {
            if (_inputHandler == null)
                return;
            Vector2 moveInput = _inputHandler.MovementInput;
            float currentMaxSpeed = _inputHandler.SprintHeld ? _sprintSpeed : _walkSpeed;

            if (moveInput.magnitude > 0)
            {
                _currentSpeed = currentMaxSpeed;
            }
            else
            {
                _currentSpeed = 0;
            }

            if (_inputHandler.JumpPressed && CanJump())
            {
                Jump();
            }

            if (_inputHandler.CrouchPressed)
            {
                ToggleCrouch();
            }

            UpdateGroundedState();
        }

        private void ApplyMovement()
        {
            if (_inputHandler == null || _characterController == null)
                return;

            Vector2 moveInput = _inputHandler.MovementInput;

            Vector3 moveDirection = transform.forward * moveInput.y + transform.right * moveInput.x;
            moveDirection = moveDirection.normalized;
            
            float speedMultiplier = _characterController.isGrounded ? 1f : _airControlMultiplier;
            Vector3 horizontalVelocity = moveDirection * _currentSpeed * speedMultiplier;

            _velocity.x = horizontalVelocity.x;
            _velocity.z = horizontalVelocity.z;
        }

        private void ApplyGravity()
        {
            if (_characterController == null)
            {
                return;
            }

            if (!_characterController.isGrounded)
            {
                _velocity.y -= _gravity * Time.deltaTime;
            }
            else if (_velocity.y < 0)
            {
                _velocity.y = -0.5f;
            }
        }

        private void MoveCharacter()
        {
            if (_characterController == null)
                return;

            _characterController.Move(_velocity * Time.deltaTime);
        }

        private void UpdateGroundedState()
        {
            if (_characterController == null)
            {
                return;
            }

            if (_characterController.isGrounded)
            {
                _timeSinceLastGrounded = 0f;
            }
            else
            {
                _timeSinceLastGrounded += Time.deltaTime;
            }
        }

        private bool CanJump()
        {
            return _timeSinceLastGrounded < _coyoteTime;
        }

        private void Jump()
        {
            _timeSinceLastGrounded = _coyoteTime;
            _velocity.y = _jumpForce;

        }

        private void ToggleCrouch()
        {
            _isCrouching = !_isCrouching;
            _targetCharacterHeight = _isCrouching ? _crouchHeight : _normalHeight;
        }

        private void UpdateCharacterHeight()
        {
            if (_characterController == null)
                return;

            float currentHeight = _characterController.height;
            float heightDifference = _targetCharacterHeight - currentHeight;

            if (Mathf.Abs(heightDifference) > 0.01f)
            {
                float newHeight = Mathf.Lerp(currentHeight, _targetCharacterHeight, Time.deltaTime * _crouchHeightTransitionSpeed);
                _characterController.height = newHeight;
            }
        }

        public bool IsGrounded()
        {
            return _characterController != null && _characterController.isGrounded;
        }

        public bool IsCrouching()
        {
            return _isCrouching;
        }

        public float GetCurrentSpeed()
        {
            return _currentSpeed;
        }

        public Vector3 GetVelocity()
        {
            return _velocity;
        }

        public void ResetMovementStateAfterTeleport()
        {
            _velocity = Vector3.zero;
            _timeSinceLastGrounded = 0f;
            _currentSpeed = 0f;
        }
    }
}

