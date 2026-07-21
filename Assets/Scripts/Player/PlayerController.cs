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

        [SerializeField]
        [Tooltip("Camera or camera-rig transform whose local Y follows the capsule stance. Auto-resolved from FirstPersonCamera when omitted.")]
        private Transform _cameraTransform;

        [SerializeField]
        private float _crouchingCameraY = 0.75f;

        [SerializeField]
        [Tooltip("Environment layers checked before expanding the capsule back to standing height.")]
        private LayerMask _standingClearanceMask = ~0;

        private Vector3 _velocity = Vector3.zero;
        private float _currentSpeed = 0f;
        private float _timeSinceLastGrounded = 0f;
        private bool _isCrouching = false;
        private bool _wantsToCrouch;
        private float _targetCharacterHeight;
        private bool _stanceGeometryInitialized;
        private Vector3 _standingCenter;
        private Vector3 _standingCameraLocalPosition;
        private float _capsuleBottomY;
        private bool _hasLoggedInvalidStanceConfiguration;
        private readonly Collider[] _standingClearanceOverlaps = new Collider[16];

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
            StabilizeCurrentStanceGeometry();

            if (_characterController != null)
            {
                _characterController.enabled = false;
            }
        }

        private void Start()
        {
            InitializeComponents();
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
            }

            if (_inputHandler == null)
            {
                _inputHandler = GetComponent<PlayerInputHandler>();
            }

            ResolveCameraTransform();
            InitializeStanceGeometry();
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

            if (_inputHandler.ConsumeJumpPress() && CanJump())
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
            _wantsToCrouch = !_wantsToCrouch;
            if (_wantsToCrouch)
            {
                _isCrouching = true;
                _targetCharacterHeight = _crouchHeight;
            }
        }

        private void UpdateCharacterHeight()
        {
            if (_characterController == null)
                return;

            InitializeStanceGeometry();
            if (!_stanceGeometryInitialized)
            {
                return;
            }

            if (_wantsToCrouch)
            {
                _targetCharacterHeight = _crouchHeight;
            }
            else if (CanStandUp())
            {
                _targetCharacterHeight = _normalHeight;
            }
            else
            {
                _targetCharacterHeight = _crouchHeight;
            }

            float newHeight = PlayerStanceGeometry.MoveHeight(
                _characterController.height,
                _targetCharacterHeight,
                _crouchHeight,
                _normalHeight,
                _crouchHeightTransitionSpeed,
                Time.deltaTime);
            ApplyStanceGeometry(newHeight);
            _isCrouching = _wantsToCrouch || !Mathf.Approximately(newHeight, _normalHeight);
        }

        public bool CanStandUp()
        {
            if (_characterController == null)
            {
                return false;
            }

            InitializeStanceGeometry();
            if (!_stanceGeometryInitialized
                || _characterController.height >= _normalHeight - 0.001f)
            {
                return true;
            }

            float radius = Mathf.Max(0.001f, _characterController.radius);
            Vector3 currentCenter = PlayerStanceGeometry.CenterPreservingBottom(
                _standingCenter,
                _normalHeight,
                _characterController.height);
            float currentTopSphereY = PlayerStanceGeometry.TopSphereCenterY(
                currentCenter,
                _characterController.height,
                radius);
            float standingTopSphereY = PlayerStanceGeometry.TopSphereCenterY(
                _standingCenter,
                _normalHeight,
                radius);

            Vector3 currentTopSphere = transform.TransformPoint(
                new Vector3(currentCenter.x, currentTopSphereY, currentCenter.z));
            Vector3 standingTopSphere = transform.TransformPoint(
                new Vector3(_standingCenter.x, standingTopSphereY, _standingCenter.z));

            Vector3 lossyScale = transform.lossyScale;
            float horizontalScale = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.z));
            float worldRadius = Mathf.Max(
                0.001f,
                (radius - Mathf.Min(radius * 0.5f, _characterController.skinWidth)) * horizontalScale);

            int overlapCount = Physics.OverlapCapsuleNonAlloc(
                currentTopSphere,
                standingTopSphere,
                worldRadius,
                _standingClearanceOverlaps,
                _standingClearanceMask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlapCount; i++)
            {
                Collider overlap = _standingClearanceOverlaps[i];
                if (overlap == null || IsOwnCollider(overlap))
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private void InitializeStanceGeometry()
        {
            if (_stanceGeometryInitialized || _characterController == null)
            {
                return;
            }

            float originalHeight = PlayerStanceGeometry.NormalizeHeight(
                _characterController.height,
                _characterController.radius,
                _normalHeight);
            Vector3 originalCenter = _characterController.center;
            _capsuleBottomY = PlayerStanceGeometry.BottomY(originalCenter, originalHeight);

            float normalizedStandingHeight = PlayerStanceGeometry.NormalizeHeight(
                _normalHeight,
                _characterController.radius,
                originalHeight);
            float normalizedCrouchingHeight = PlayerStanceGeometry.NormalizeHeight(
                _crouchHeight,
                _characterController.radius,
                normalizedStandingHeight);
            normalizedCrouchingHeight = Mathf.Min(normalizedCrouchingHeight, normalizedStandingHeight);

            bool invalidConfiguration = !Mathf.Approximately(_normalHeight, normalizedStandingHeight)
                                        || !Mathf.Approximately(_crouchHeight, normalizedCrouchingHeight)
                                        || !PlayerStanceGeometry.IsFinite(_crouchHeightTransitionSpeed)
                                        || _crouchHeightTransitionSpeed < 0f;

            _normalHeight = normalizedStandingHeight;
            _crouchHeight = normalizedCrouchingHeight;
            if (!PlayerStanceGeometry.IsFinite(_crouchHeightTransitionSpeed)
                || _crouchHeightTransitionSpeed < 0f)
            {
                _crouchHeightTransitionSpeed = 10f;
            }

            _standingCenter = originalCenter;
            _standingCenter.y = _capsuleBottomY + (_normalHeight * 0.5f);

            if (_cameraTransform != null)
            {
                _standingCameraLocalPosition = _cameraTransform.localPosition;
                if (!PlayerStanceGeometry.IsFinite(_crouchingCameraY))
                {
                    _crouchingCameraY = _standingCameraLocalPosition.y;
                    invalidConfiguration = true;
                }

                _crouchingCameraY = Mathf.Min(_crouchingCameraY, _standingCameraLocalPosition.y);
            }

            _targetCharacterHeight = _normalHeight;
            _wantsToCrouch = false;
            _isCrouching = false;
            _stanceGeometryInitialized = true;
            ApplyStanceGeometry(_normalHeight);

            if (invalidConfiguration && !_hasLoggedInvalidStanceConfiguration)
            {
                _hasLoggedInvalidStanceConfiguration = true;
                Debug.LogWarning(
                    $"[PlayerController] Invalid stance configuration was normalized. " +
                    $"standingHeight={_normalHeight:0.###}, crouchingHeight={_crouchHeight:0.###}, " +
                    $"radius={_characterController.radius:0.###}, transitionSpeed={_crouchHeightTransitionSpeed:0.###}.",
                    this);
            }
        }

        private void ApplyStanceGeometry(float height)
        {
            if (_characterController == null || !_stanceGeometryInitialized)
            {
                return;
            }

            float normalizedHeight = PlayerStanceGeometry.NormalizeHeight(
                height,
                _characterController.radius,
                _normalHeight);
            normalizedHeight = Mathf.Clamp(normalizedHeight, _crouchHeight, _normalHeight);
            _characterController.height = normalizedHeight;
            _characterController.center = PlayerStanceGeometry.CenterPreservingBottom(
                _standingCenter,
                _normalHeight,
                normalizedHeight);

            if (_cameraTransform == null)
            {
                return;
            }

            Vector3 cameraLocalPosition = _cameraTransform.localPosition;
            cameraLocalPosition.y = PlayerStanceGeometry.CameraYForHeight(
                normalizedHeight,
                _crouchHeight,
                _normalHeight,
                _crouchingCameraY,
                _standingCameraLocalPosition.y);
            _cameraTransform.localPosition = cameraLocalPosition;
        }

        private void StabilizeCurrentStanceGeometry()
        {
            if (!_stanceGeometryInitialized || _characterController == null)
            {
                return;
            }

            ApplyStanceGeometry(_characterController.height);
        }

        private void ResolveCameraTransform()
        {
            if (_cameraTransform != null)
            {
                return;
            }

            FirstPersonCamera firstPersonCamera = GetComponentInChildren<FirstPersonCamera>(true);
            if (firstPersonCamera != null)
            {
                _cameraTransform = firstPersonCamera.transform;
            }
        }

        private bool IsOwnCollider(Collider candidate)
        {
            if (candidate == null || candidate == _characterController)
            {
                return true;
            }

            Transform candidateTransform = candidate.transform;
            return candidateTransform == transform || candidateTransform.IsChildOf(transform);
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

