using System.Collections.Generic;
using Game.Core;
using Game.Player;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Minigames
{
    public sealed class PipePaintMinigame : BaseMinigame
    {
        private sealed class PaintSegmentState
        {
            public Transform transform;
            public Collider collider;
            public Renderer renderer;
            public int pipeIndex;
            public bool painted;
        }

        private sealed class PaintPipeState
        {
            public Transform transform;
            public readonly List<PaintSegmentState> segments = new List<PaintSegmentState>(24);
            public int paintedCount;
        }

        private const string CursorAuthorityOwner = "minigame_pipe_paint";
        private const float DefaultTimeLimitSeconds = 30f;
        private const float DefaultPassCoverageThreshold = 50f;
        private static readonly Color DefaultUnpaintedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        private static readonly Color DefaultPaintedColor = new Color(0.2f, 0.7f, 0.95f, 1f);

        [SerializeField] private Canvas _minigameCanvas;
        [SerializeField] private TMP_Text _timerText;
        [SerializeField] private TMP_Text _qualityText;
        [SerializeField] private TMP_Text _instructionText;
        [SerializeField] private TMP_Text _progressText;

        [SerializeField] private Transform _worldViewPose;
        [SerializeField] private Transform _worldViewLookTarget;
        [SerializeField] private Transform _paintTargetsRoot;
        [SerializeField] private Transform _brushCursorVisual;
        [SerializeField] private float _cameraTransitionDuration = 0.45f;
        [SerializeField] private float _cameraReturnDuration = 0.3f;
        [SerializeField] private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);
        [SerializeField] private Vector3 _worldLookTargetLocalOffset = Vector3.zero;
        [SerializeField] private float _worldTopDownAngleBias = 16f;
        [SerializeField] private float _worldCameraFovOverride = 50f;
        [SerializeField] private bool _freezePlayerMovementInWorldView = true;

        [SerializeField] private float _timeLimitSeconds = DefaultTimeLimitSeconds;
        [SerializeField] private float _requiredPassCoveragePercent = DefaultPassCoverageThreshold;
        [SerializeField] private float _brushRadiusWorld = 0.12f;
        [SerializeField] private Color _unpaintedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        [SerializeField] private Color _paintedColor = new Color(0.2f, 0.7f, 0.95f, 1f);

        private readonly List<PaintPipeState> _pipes = new List<PaintPipeState>(3);
        private readonly Dictionary<Collider, PaintSegmentState> _segmentsByCollider = new Dictionary<Collider, PaintSegmentState>();

        private Camera _gameplayViewCamera;
        private Camera _worldViewCamera;
        private Coroutine _cameraTransitionRoutine;
        private bool _hasGameplayCameraRenderOverride;
        private bool _gameplayCameraWasEnabled;
        private bool _isFinishing;
        private bool _isReturningToGameplayView;
        private float _returnTransitionElapsed;
        private Vector3 _returnTransitionStartPosition;
        private Quaternion _returnTransitionStartRotation;
        private MinigameResult _pendingResult = MinigameResult.None;

        private bool _usesPresentationCursorAuthority;
        private CursorLockMode _previousCursorLockMode;
        private bool _previousCursorVisible;
        private bool _setupValid;
        private float _remainingTimeSeconds;
        private float _lastCoveragePercent;
        private InputAction _paintAction;

        private PlayerController _playerController;
        private bool _playerControllerWasEnabled;
        private bool _hasLoggedPlayerControllerFallbackWarning;

        protected override void OnInitialize()
        {
            LoadParameters();
            _remainingTimeSeconds = _timeLimitSeconds;
            _lastCoveragePercent = 0f;
            _setupValid = ValidateContract();
            CachePaintAction();
            UpdateTimerUi();
            UpdateQualityUi(_lastCoveragePercent);
            UpdateInstructionUi();
            UpdateProgressUi();
        }

        protected override void OnStart()
        {
            if (!_setupValid)
            {
                SetResult(MinigameResult.Fail);
                return;
            }

            _minigameCanvas.enabled = true;
            AcquireCursorAuthority();
            if (!InitializePaintTargets())
            {
                SetResult(MinigameResult.Fail);
                return;
            }

            StartWorldViewPresentation();
            SetPlayerMovementFrozen(true);
            UpdateProgressUi();
            UpdateQualityUi(ComputeCoveragePercent());
        }

        protected override void OnUpdate()
        {
            if (_isFinishing)
            {
                UpdateFinishFlow();
                return;
            }

            UpdateTimer();
            if (_remainingTimeSeconds <= 0f)
            {
                FinishAndScore();
                return;
            }

            UpdateBrushAndPaint();
            float currentCoverage = ComputeCoveragePercent();
            _lastCoveragePercent = currentCoverage;
            UpdateQualityUi(currentCoverage);
            UpdateProgressUi();

            if (currentCoverage >= 100f - 0.001f)
            {
                FinishAndScore();
            }
        }

        protected override void OnEnd()
        {
            if (_cameraTransitionRoutine != null)
            {
                StopCoroutine(_cameraTransitionRoutine);
                _cameraTransitionRoutine = null;
            }

            if (_minigameCanvas != null)
            {
                _minigameCanvas.enabled = false;
            }

            ReleaseCursorAuthority();
            TearDownWorldViewPresentation();
            SetPlayerMovementFrozen(false);

            _pipes.Clear();
            _segmentsByCollider.Clear();
            _isFinishing = false;
            _isReturningToGameplayView = false;
            _pendingResult = MinigameResult.None;
        }

        private void LoadParameters()
        {
            _minigameCanvas = GetParameter<Canvas>("canvas") ?? _minigameCanvas;
            _timerText = GetParameter<TMP_Text>("timer_text") ?? _timerText;
            _qualityText = GetParameter<TMP_Text>("quality_text") ?? _qualityText;
            _instructionText = GetParameter<TMP_Text>("instruction_text") ?? _instructionText;
            _progressText = GetParameter<TMP_Text>("progress_text") ?? _progressText;

            _worldViewPose = GetParameter<Transform>("world_camera_pose") ?? _worldViewPose;
            _worldViewLookTarget = GetParameter<Transform>("world_look_target") ?? _worldViewLookTarget;
            _paintTargetsRoot = GetParameter<Transform>("world_paint_targets_root") ?? _paintTargetsRoot;
            _brushCursorVisual = GetParameter<Transform>("world_brush_cursor_visual") ?? _brushCursorVisual;
            _cameraTransitionDuration = GetFloat("world_camera_transition", _cameraTransitionDuration);
            _cameraReturnDuration = GetFloat("world_camera_return", _cameraReturnDuration);
            _worldCameraLocalOffset = GetVector3("world_camera_local_offset", _worldCameraLocalOffset);
            _worldLookTargetLocalOffset = GetVector3("world_look_target_local_offset", _worldLookTargetLocalOffset);
            _worldTopDownAngleBias = GetFloat("world_top_down_angle_bias", _worldTopDownAngleBias);
            _worldCameraFovOverride = GetFloat("world_camera_fov", _worldCameraFovOverride);
            _freezePlayerMovementInWorldView = GetBool("world_freeze_player", _freezePlayerMovementInWorldView);

            _requiredPassCoveragePercent = Mathf.Clamp(GetFloat("required_pass_coverage_percent", _requiredPassCoveragePercent), 0f, 100f);
            _brushRadiusWorld = Mathf.Max(0.01f, GetFloat("brush_radius_world", _brushRadiusWorld));
            _unpaintedColor = GetColor("unpainted_color", _unpaintedColor);
            _paintedColor = GetColor("painted_color", _paintedColor);

            float configuredTime = GetFloat("time_limit", _timeLimitSeconds);
            configuredTime = GetFloat("timeLimit", configuredTime);
            _timeLimitSeconds = configuredTime > 0f ? configuredTime : DefaultTimeLimitSeconds;
            _minigameData.timeLimit = 0f;
        }

        private bool ValidateContract()
        {
            if (_minigameCanvas == null || _timerText == null || _qualityText == null || _instructionText == null)
            {
                Debug.LogError("[PipePaintMinigame] Missing canvas/timer/quality/instruction references.", this);
                return false;
            }

            if (_worldViewPose == null || _worldViewLookTarget == null || _paintTargetsRoot == null)
            {
                Debug.LogError("[PipePaintMinigame] Missing world camera pose/look target/paint target root.", this);
                return false;
            }

            if (_paintTargetsRoot.childCount < 3)
            {
                Debug.LogError("[PipePaintMinigame] PaintTargetsRoot must contain at least 3 pipe targets.", this);
                return false;
            }

            return true;
        }

        private bool InitializePaintTargets()
        {
            _pipes.Clear();
            _segmentsByCollider.Clear();

            int activePipeCount = 0;
            for (int i = 0; i < _paintTargetsRoot.childCount; i++)
            {
                Transform pipeRoot = _paintTargetsRoot.GetChild(i);
                if (pipeRoot == null || !pipeRoot.gameObject.activeInHierarchy)
                {
                    continue;
                }

                PaintPipeState pipeState = new PaintPipeState { transform = pipeRoot };
                int segmentAddedCount = 0;
                for (int j = 0; j < pipeRoot.childCount; j++)
                {
                    Transform segmentTransform = pipeRoot.GetChild(j);
                    if (segmentTransform == null || !segmentTransform.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    Collider segmentCollider = segmentTransform.GetComponent<Collider>();
                    Renderer segmentRenderer = segmentTransform.GetComponent<Renderer>();
                    if (segmentCollider == null || segmentRenderer == null)
                    {
                        continue;
                    }

                    PaintSegmentState segmentState = new PaintSegmentState
                    {
                        transform = segmentTransform,
                        collider = segmentCollider,
                        renderer = segmentRenderer,
                        pipeIndex = activePipeCount,
                        painted = false
                    };

                    ApplySegmentColor(segmentState, _unpaintedColor);
                    pipeState.segments.Add(segmentState);
                    _segmentsByCollider[segmentCollider] = segmentState;
                    segmentAddedCount++;
                }

                if (segmentAddedCount <= 0)
                {
                    Debug.LogError($"[PipePaintMinigame] Pipe target '{pipeRoot.name}' has no paintable segment children.", this);
                    return false;
                }

                _pipes.Add(pipeState);
                activePipeCount++;
                if (activePipeCount >= 3)
                {
                    break;
                }
            }

            if (_pipes.Count < 3)
            {
                Debug.LogError("[PipePaintMinigame] Need at least 3 active pipe targets with paintable segments.", this);
                return false;
            }

            return true;
        }

        private void UpdateTimer()
        {
            _remainingTimeSeconds = Mathf.Max(0f, _remainingTimeSeconds - Time.deltaTime);
            UpdateTimerUi();
        }

        private void CachePaintAction()
        {
            _paintAction = null;
            if (Game.Input.InputManager.Instance == null)
            {
                return;
            }

            InputActionAsset inputAsset = Game.Input.InputManager.Instance.GetInputActionAsset();
            InputActionMap playerMap = inputAsset != null ? inputAsset.FindActionMap("Player") : null;
            _paintAction = playerMap != null ? playerMap.FindAction("Attack") : null;
        }

        private void UpdateBrushAndPaint()
        {
            if (!TryGetMouseWorldPoint(out Vector3 mousePoint, out PaintSegmentState hoveredSegment))
            {
                return;
            }

            if (_brushCursorVisual != null)
            {
                _brushCursorVisual.position = mousePoint;
            }

            bool paintHeld = _paintAction != null && _paintAction.IsPressed();
            if (!paintHeld)
            {
                return;
            }

            if (hoveredSegment != null)
            {
                PaintSegment(hoveredSegment);
                PaintNearbySegments(hoveredSegment.pipeIndex, mousePoint);
            }
        }

        private void PaintNearbySegments(int pipeIndex, Vector3 brushPoint)
        {
            if (pipeIndex < 0 || pipeIndex >= _pipes.Count)
            {
                return;
            }

            List<PaintSegmentState> segments = _pipes[pipeIndex].segments;
            float radiusSq = _brushRadiusWorld * _brushRadiusWorld;
            for (int i = 0; i < segments.Count; i++)
            {
                PaintSegmentState segment = segments[i];
                if (segment == null || segment.painted || segment.transform == null)
                {
                    continue;
                }

                if ((segment.transform.position - brushPoint).sqrMagnitude <= radiusSq)
                {
                    PaintSegment(segment);
                }
            }
        }

        private void PaintSegment(PaintSegmentState segment)
        {
            if (segment == null || segment.painted)
            {
                return;
            }

            segment.painted = true;
            ApplySegmentColor(segment, _paintedColor);
            if (segment.pipeIndex >= 0 && segment.pipeIndex < _pipes.Count)
            {
                _pipes[segment.pipeIndex].paintedCount++;
            }
        }

        private static void ApplySegmentColor(PaintSegmentState segment, Color color)
        {
            if (segment?.renderer == null || segment.renderer.material == null)
            {
                return;
            }

            segment.renderer.material.color = color;
        }

        private float ComputeCoveragePercent()
        {
            float sumCoverage01 = 0f;
            for (int i = 0; i < _pipes.Count; i++)
            {
                PipePaintScoring.CoverageResult result =
                    PipePaintScoring.BuildCoverageResult(_pipes[i].paintedCount, _pipes[i].segments.Count);
                sumCoverage01 += result.coverage01;
            }

            float aggregate01 = PipePaintScoring.ComputeAggregateCoverage01(sumCoverage01, _pipes.Count);
            return aggregate01 * 100f;
        }

        private void FinishAndScore()
        {
            float coveragePercent = ComputeCoveragePercent();
            _minigameData.SetParameter("pipe_paint_coverage", coveragePercent);
            MinigameResult result = coveragePercent >= _requiredPassCoveragePercent ? MinigameResult.Pass : MinigameResult.Fail;
            RequestFinish(result);
        }

        private void RequestFinish(MinigameResult result)
        {
            if (_isFinishing)
            {
                return;
            }

            _isFinishing = true;
            _pendingResult = result;
            _minigameCanvas.enabled = false;

            if (CanSmoothReturnToGameplayView())
            {
                BeginReturnToGameplayView();
                return;
            }

            SetResult(_pendingResult);
        }

        private void UpdateFinishFlow()
        {
            if (!_isReturningToGameplayView)
            {
                SetResult(_pendingResult);
                return;
            }

            float duration = Mathf.Max(0.01f, _cameraReturnDuration);
            _returnTransitionElapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_returnTransitionElapsed / duration);

            _worldViewCamera.transform.SetPositionAndRotation(
                Vector3.Lerp(_returnTransitionStartPosition, _gameplayViewCamera.transform.position, t),
                Quaternion.Slerp(_returnTransitionStartRotation, _gameplayViewCamera.transform.rotation, t));

            if (t >= 1f)
            {
                _isReturningToGameplayView = false;
                SetResult(_pendingResult);
            }
        }

        private void BeginReturnToGameplayView()
        {
            _isReturningToGameplayView = true;
            _returnTransitionElapsed = 0f;
            _returnTransitionStartPosition = _worldViewCamera.transform.position;
            _returnTransitionStartRotation = _worldViewCamera.transform.rotation;
        }

        private bool CanSmoothReturnToGameplayView()
        {
            return _worldViewCamera != null && _gameplayViewCamera != null && _cameraReturnDuration > 0f;
        }

        private void StartWorldViewPresentation()
        {
            ResolveGameplayViewCamera();
            if (!EnsureWorldViewCamera())
            {
                return;
            }

            if (_cameraTransitionRoutine != null)
            {
                StopCoroutine(_cameraTransitionRoutine);
            }

            if (_cameraTransitionDuration <= 0f)
            {
                _worldViewCamera.transform.SetPositionAndRotation(GetWorldViewTargetPosition(), GetWorldViewTargetRotation());
                _worldViewCamera.enabled = true;
                SetGameplayCameraRenderingSuppressed(true);
                return;
            }

            _cameraTransitionRoutine = StartCoroutine(SmoothTransitionToWorldView());
        }

        private System.Collections.IEnumerator SmoothTransitionToWorldView()
        {
            Vector3 endPosition = GetWorldViewTargetPosition();
            Quaternion endRotation = GetWorldViewTargetRotation();
            Vector3 startPosition = _gameplayViewCamera != null ? _gameplayViewCamera.transform.position : endPosition;
            Quaternion startRotation = _gameplayViewCamera != null ? _gameplayViewCamera.transform.rotation : endRotation;

            _worldViewCamera.transform.SetPositionAndRotation(startPosition, startRotation);
            _worldViewCamera.enabled = true;
            SetGameplayCameraRenderingSuppressed(true);

            float duration = Mathf.Max(0.01f, _cameraTransitionDuration);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                _worldViewCamera.transform.SetPositionAndRotation(
                    Vector3.Lerp(startPosition, endPosition, t),
                    Quaternion.Slerp(startRotation, endRotation, t));
                yield return null;
            }

            _worldViewCamera.transform.SetPositionAndRotation(endPosition, endRotation);
            _cameraTransitionRoutine = null;
        }

        private Camera ResolveGameplayViewCamera()
        {
            if (_gameplayViewCamera != null)
            {
                return _gameplayViewCamera;
            }

            if (PlayerContextLocator.TryGetLocalFirstPersonCamera(out FirstPersonCamera localFirstPersonCamera)
                && localFirstPersonCamera != null)
            {
                _gameplayViewCamera = localFirstPersonCamera.GetComponent<Camera>();
            }

            if (_gameplayViewCamera == null && PlayerContextLocator.IsCompatibilityFallbackAllowed())
            {
                _gameplayViewCamera = Camera.main;
            }

            return _gameplayViewCamera;
        }

        private bool EnsureWorldViewCamera()
        {
            if (_worldViewPose == null || _worldViewLookTarget == null)
            {
                return false;
            }

            if (_worldViewCamera == null)
            {
                GameObject cameraObject = new GameObject("[PipePaintWorldViewCamera]", typeof(Camera));
                cameraObject.transform.SetParent(transform, false);
                _worldViewCamera = cameraObject.GetComponent<Camera>();
            }

            Camera gameplayCamera = ResolveGameplayViewCamera();
            if (gameplayCamera != null)
            {
                _worldViewCamera.CopyFrom(gameplayCamera);
                _worldViewCamera.depth = gameplayCamera.depth + 1f;
            }

            if (_worldCameraFovOverride > 0f)
            {
                _worldViewCamera.fieldOfView = Mathf.Clamp(_worldCameraFovOverride, 20f, 100f);
            }

            _worldViewCamera.enabled = false;
            return true;
        }

        private void SetGameplayCameraRenderingSuppressed(bool suppressed)
        {
            Camera gameplayCamera = ResolveGameplayViewCamera();
            if (gameplayCamera == null || gameplayCamera == _worldViewCamera)
            {
                return;
            }

            if (PlayerContextLocator.TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
                && presentationState != null)
            {
                presentationState.SetGameplayCameraSuppressed(suppressed);
            }

            if (suppressed)
            {
                if (_hasGameplayCameraRenderOverride)
                {
                    return;
                }

                _gameplayCameraWasEnabled = gameplayCamera.enabled;
                gameplayCamera.enabled = false;
                _hasGameplayCameraRenderOverride = true;
                return;
            }

            if (!_hasGameplayCameraRenderOverride)
            {
                return;
            }

            gameplayCamera.enabled = _gameplayCameraWasEnabled;
            _hasGameplayCameraRenderOverride = false;
        }

        private Vector3 GetWorldViewTargetPosition()
        {
            return _worldViewPose.TransformPoint(_worldCameraLocalOffset);
        }

        private Quaternion GetWorldViewTargetRotation()
        {
            Vector3 cameraPosition = GetWorldViewTargetPosition();
            Vector3 lookPoint = _worldViewLookTarget.TransformPoint(_worldLookTargetLocalOffset);
            Vector3 direction = lookPoint - cameraPosition;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return _worldViewPose.rotation;
            }

            Quaternion lookRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            Vector3 euler = lookRotation.eulerAngles;
            float signedPitch = NormalizeSignedAngle(euler.x);
            signedPitch = Mathf.Clamp(signedPitch + _worldTopDownAngleBias, -89f, 89f);
            return Quaternion.Euler(signedPitch, euler.y, 0f);
        }

        private static float NormalizeSignedAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f)
            {
                angle -= 360f;
            }

            return angle;
        }

        private void TearDownWorldViewPresentation()
        {
            SetGameplayCameraRenderingSuppressed(false);
            if (_worldViewCamera != null)
            {
                _worldViewCamera.enabled = false;
                Destroy(_worldViewCamera.gameObject);
                _worldViewCamera = null;
            }
        }

        private void SetPlayerMovementFrozen(bool frozen)
        {
            if (!_freezePlayerMovementInWorldView)
            {
                return;
            }

            if (frozen)
            {
                if (_playerController == null)
                {
                    if (PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext)
                        && localContext != null
                        && localContext.PlayerController != null)
                    {
                        _playerController = localContext.PlayerController;
                    }
                }

                if (_playerController == null)
                {
                    _playerController = FindAnyObjectByType<PlayerController>();
                    if (_playerController != null && !_hasLoggedPlayerControllerFallbackWarning)
                    {
                        _hasLoggedPlayerControllerFallbackWarning = true;
                        Debug.LogWarning("[PipePaintMinigame] Using scene-wide PlayerController fallback for movement freeze.", this);
                    }
                }

                if (_playerController != null)
                {
                    _playerControllerWasEnabled = _playerController.enabled;
                    _playerController.enabled = false;
                }

                return;
            }

            if (_playerController != null)
            {
                _playerController.enabled = _playerControllerWasEnabled;
            }
        }

        private bool TryGetMouseWorldPoint(out Vector3 worldPoint, out PaintSegmentState segment)
        {
            worldPoint = Vector3.zero;
            segment = null;
            Camera activeCamera = _worldViewCamera != null && _worldViewCamera.enabled ? _worldViewCamera : ResolveGameplayViewCamera();
            if (activeCamera == null)
            {
                return false;
            }

            Ray mouseRay = activeCamera.ScreenPointToRay(UnityEngine.Input.mousePosition);
            if (!Physics.Raycast(mouseRay, out RaycastHit hit, 50f))
            {
                return false;
            }

            worldPoint = hit.point;
            if (hit.collider != null)
            {
                _segmentsByCollider.TryGetValue(hit.collider, out segment);
            }

            return true;
        }

        private void UpdateTimerUi()
        {
            if (_timerText == null)
            {
                return;
            }

            _timerText.text = $"Time: {Mathf.CeilToInt(_remainingTimeSeconds)}s";
            _timerText.color = _remainingTimeSeconds <= 5f ? Color.red : Color.white;
        }

        private void UpdateQualityUi(float coveragePercent)
        {
            if (_qualityText == null)
            {
                return;
            }

            _qualityText.text = $"Coverage: {coveragePercent:0}%";
        }

        private void UpdateInstructionUi()
        {
            if (_instructionText == null)
            {
                return;
            }

            _instructionText.text = "Hold LMB and drag the brush to paint all pipe segments.";
        }

        private void UpdateProgressUi()
        {
            if (_progressText == null)
            {
                return;
            }

            int paintedTotal = 0;
            int segmentTotal = 0;
            for (int i = 0; i < _pipes.Count; i++)
            {
                paintedTotal += _pipes[i].paintedCount;
                segmentTotal += _pipes[i].segments.Count;
            }

            _progressText.text = $"Segments: {paintedTotal}/{Mathf.Max(1, segmentTotal)}";
        }

        private void AcquireCursorAuthority()
        {
            if (PlayerContextLocator.TryAcquireLocalCursorAuthority(CursorAuthorityOwner, CursorLockMode.None, true))
            {
                _usesPresentationCursorAuthority = true;
                return;
            }

            _usesPresentationCursorAuthority = false;
            _previousCursorLockMode = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void ReleaseCursorAuthority()
        {
            if (_usesPresentationCursorAuthority)
            {
                PlayerContextLocator.TryReleaseLocalCursorAuthority(CursorAuthorityOwner);
                _usesPresentationCursorAuthority = false;
                return;
            }

            Cursor.lockState = _previousCursorLockMode;
            Cursor.visible = _previousCursorVisible;
        }

        private float GetFloat(string key, float defaultValue)
        {
            object value = GetParameter(key);
            if (value is float floatValue) return floatValue;
            if (value is int intValue) return intValue;
            if (value is string stringValue && float.TryParse(stringValue, out float parsed)) return parsed;
            return defaultValue;
        }

        private bool GetBool(string key, bool defaultValue)
        {
            object value = GetParameter(key);
            if (value is bool boolValue) return boolValue;
            if (value is int intValue) return intValue != 0;
            if (value is float floatValue) return !Mathf.Approximately(floatValue, 0f);
            return defaultValue;
        }

        private Vector3 GetVector3(string key, Vector3 defaultValue)
        {
            object value = GetParameter(key);
            return value is Vector3 vectorValue ? vectorValue : defaultValue;
        }

        private Color GetColor(string key, Color defaultValue)
        {
            object value = GetParameter(key);
            return value is Color colorValue ? colorValue : defaultValue;
        }
    }
}
