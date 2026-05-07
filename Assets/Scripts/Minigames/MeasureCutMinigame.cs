using System.Collections.Generic;
using Game.Core;
using Game.Player;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Minigames
{
    public sealed class MeasureCutMinigame : BaseMinigame
    {
        private sealed class CutLineState
        {
            public Transform lineTransform;
            public Vector3 lineStartWorld;
            public Vector3 lineEndWorld;
            public bool completed;
            public float quality01;
            public readonly List<Vector3> sampledPoints = new List<Vector3>(96);
        }

        private const float DefaultTimeLimitSeconds = 30f;
        private const float DefaultToleranceWorld = 0.08f;
        private const float DefaultFailThreshold = 35f;
        private const string CursorAuthorityOwner = "minigame_measure_cut";

        [SerializeField] private Canvas _minigameCanvas;
        [SerializeField] private TMP_Text _timerText;
        [SerializeField] private TMP_Text _qualityText;
        [SerializeField] private TMP_Text _instructionText;
        [SerializeField] private TMP_Text _cutProgressText;

        [SerializeField] private Transform _worldViewPose;
        [SerializeField] private Transform _worldViewLookTarget;
        [SerializeField] private Transform _cutTargetsRoot;
        [SerializeField] private Transform _cutterVisual;
        [SerializeField] private float _cameraTransitionDuration = 0.45f;
        [SerializeField] private float _cameraReturnDuration = 0.3f;
        [SerializeField] private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);
        [SerializeField] private Vector3 _worldLookTargetLocalOffset = Vector3.zero;
        [SerializeField] private float _worldTopDownAngleBias = 16f;
        [SerializeField] private float _worldCameraFovOverride = 50f;
        [SerializeField] private bool _freezePlayerMovementInWorldView = true;

        [SerializeField] private float _lineToleranceWorld = DefaultToleranceWorld;
        [SerializeField] private float _requiredQualityThreshold = DefaultFailThreshold;
        [SerializeField] private float _timeLimitSeconds = DefaultTimeLimitSeconds;

        [Header("Guide Visual Feedback")]
        [SerializeField] private Transform _cutGuideRoot;
        [SerializeField] private Renderer _guideLine01;
        [SerializeField] private Renderer _guideLine02;
        [SerializeField] private Renderer _guideLine03;
        [SerializeField] private Renderer _toleranceBand01;
        [SerializeField] private Renderer _toleranceBand02;
        [SerializeField] private Renderer _toleranceBand03;
        [SerializeField] private Color _guideNeutralColor = new Color(0.85f, 0.85f, 0.85f, 0.95f);
        [SerializeField] private Color _toleranceNeutralColor = new Color(0.25f, 0.45f, 0.95f, 0.28f);
        [SerializeField] private Color _completedColor = new Color(0.22f, 0.9f, 0.35f, 0.92f);
        [SerializeField] private Color _poorCutColor = new Color(0.95f, 0.22f, 0.22f, 0.95f);
        [SerializeField] private Color _mediumCutColor = new Color(0.95f, 0.82f, 0.22f, 0.95f);
        [SerializeField] private Color _goodCutColor = new Color(0.22f, 0.9f, 0.35f, 0.95f);

        [Header("Pipe Split Visuals")]
        [SerializeField] private GameObject _fullPipeVisual;
        [SerializeField] private Transform _leftSegment;
        [SerializeField] private Transform _rightSegment;
        [SerializeField] private float _splitSeparationDistance = 0.08f;

        [Header("Optional Feedback Hooks")]
        [SerializeField] private ParticleSystem _cuttingLoopFx;
        [SerializeField] private ParticleSystem _cutCompleteFx;
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _cuttingLoopClip;
        [SerializeField] private AudioClip _cutCompleteClip;

        private readonly List<CutLineState> _cutLines = new List<CutLineState>(6);
        private readonly List<Renderer> _guideLines = new List<Renderer>(6);
        private readonly List<Renderer> _toleranceBands = new List<Renderer>(6);
        private Plane _cuttingPlane = new Plane(Vector3.up, Vector3.zero);
        private bool _hasCuttingPlane;

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
        private bool _isCutHeld;
        private InputAction _cutAction;
        private int _activeCutIndex = -1;
        private float _remainingTimeSeconds;
        private float _aggregateQualityScore;
        private bool _setupValid;
        private float _activeLineQuality01;
        private float _activeLineProgress01;
        private bool _splitVisualApplied;
        private Vector3 _leftSegmentStartLocalPosition;
        private Vector3 _rightSegmentStartLocalPosition;

        private PlayerController _playerController;
        private bool _playerControllerWasEnabled;

        protected override void OnInitialize()
        {
            LoadParameters();
            _remainingTimeSeconds = _timeLimitSeconds;
            _aggregateQualityScore = 0f;
            _activeCutIndex = -1;
            _isCutHeld = false;
            _hasCuttingPlane = false;
            _activeLineQuality01 = 0f;
            _activeLineProgress01 = 0f;
            _splitVisualApplied = false;
            _setupValid = ValidateContract();
            CacheCutAction();
            UpdateTimerUi();
            UpdateQualityUi();
            UpdateInstructionUi();
            UpdateCutProgressUi();
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
            if (!InitializeCutLines())
            {
                SetResult(MinigameResult.Fail);
                return;
            }

            InitializeVisualFeedback();
            StartWorldViewPresentation();
            SetPlayerMovementFrozen(true);
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

            UpdateCutterVisualAndInput();
            if (AllCutsCompleted())
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

            _cutLines.Clear();
            _guideLines.Clear();
            _toleranceBands.Clear();
            _isCutHeld = false;
            _activeCutIndex = -1;
            _isFinishing = false;
            _isReturningToGameplayView = false;
            _pendingResult = MinigameResult.None;
            StopCuttingLoopFeedback();
        }

        private void LoadParameters()
        {
            _minigameCanvas = GetParameter<Canvas>("canvas") ?? _minigameCanvas;
            _timerText = GetParameter<TMP_Text>("timer_text") ?? _timerText;
            _qualityText = GetParameter<TMP_Text>("quality_text") ?? _qualityText;
            _instructionText = GetParameter<TMP_Text>("instruction_text") ?? _instructionText;
            _cutProgressText = GetParameter<TMP_Text>("cut_progress_text") ?? _cutProgressText;

            _worldViewPose = GetParameter<Transform>("world_camera_pose") ?? _worldViewPose;
            _worldViewLookTarget = GetParameter<Transform>("world_look_target") ?? _worldViewLookTarget;
            _cutTargetsRoot = GetParameter<Transform>("world_cut_targets_root") ?? _cutTargetsRoot;
            _cutterVisual = GetParameter<Transform>("world_cutter_visual") ?? _cutterVisual;
            _cutGuideRoot = GetParameter<Transform>("world_cut_guide_root") ?? _cutGuideRoot;
            _guideLine01 = GetParameter<Renderer>("world_guide_line_01") ?? _guideLine01;
            _guideLine02 = GetParameter<Renderer>("world_guide_line_02") ?? _guideLine02;
            _guideLine03 = GetParameter<Renderer>("world_guide_line_03") ?? _guideLine03;
            _toleranceBand01 = GetParameter<Renderer>("world_tolerance_band_01") ?? _toleranceBand01;
            _toleranceBand02 = GetParameter<Renderer>("world_tolerance_band_02") ?? _toleranceBand02;
            _toleranceBand03 = GetParameter<Renderer>("world_tolerance_band_03") ?? _toleranceBand03;
            _fullPipeVisual = GetParameter<GameObject>("world_full_pipe_visual") ?? _fullPipeVisual;
            _leftSegment = GetParameter<Transform>("world_left_segment") ?? _leftSegment;
            _rightSegment = GetParameter<Transform>("world_right_segment") ?? _rightSegment;
            _splitSeparationDistance = Mathf.Max(0f, GetFloat("world_split_separation_distance", _splitSeparationDistance));
            _cuttingLoopFx = GetParameter<ParticleSystem>("world_cutting_loop_fx") ?? _cuttingLoopFx;
            _cutCompleteFx = GetParameter<ParticleSystem>("world_cut_complete_fx") ?? _cutCompleteFx;
            _audioSource = GetParameter<AudioSource>("world_audio_source") ?? _audioSource;
            _cuttingLoopClip = GetParameter<AudioClip>("world_cutting_loop_clip") ?? _cuttingLoopClip;
            _cutCompleteClip = GetParameter<AudioClip>("world_cut_complete_clip") ?? _cutCompleteClip;

            _cameraTransitionDuration = GetFloat("world_camera_transition", _cameraTransitionDuration);
            _cameraReturnDuration = GetFloat("world_camera_return", _cameraReturnDuration);
            _worldCameraLocalOffset = GetVector3("world_camera_local_offset", _worldCameraLocalOffset);
            _worldLookTargetLocalOffset = GetVector3("world_look_target_local_offset", _worldLookTargetLocalOffset);
            _worldTopDownAngleBias = GetFloat("world_top_down_angle_bias", _worldTopDownAngleBias);
            _worldCameraFovOverride = GetFloat("world_camera_fov", _worldCameraFovOverride);
            _freezePlayerMovementInWorldView = GetBool("world_freeze_player", _freezePlayerMovementInWorldView);
            _lineToleranceWorld = Mathf.Max(0.01f, GetFloat("line_tolerance_world", _lineToleranceWorld));
            _requiredQualityThreshold = Mathf.Clamp(GetFloat("required_quality_threshold", _requiredQualityThreshold), 0f, 100f);

            float configuredTime = GetFloat("time_limit", _timeLimitSeconds);
            configuredTime = GetFloat("timeLimit", configuredTime);
            _timeLimitSeconds = configuredTime > 0f ? configuredTime : DefaultTimeLimitSeconds;
            _minigameData.timeLimit = 0f;
        }

        private bool ValidateContract()
        {
            if (_minigameCanvas == null || _timerText == null || _qualityText == null || _instructionText == null)
            {
                Debug.LogError("[MeasureCutMinigame] Missing canvas/timer/quality/instruction references.", this);
                return false;
            }

            if (_worldViewPose == null || _worldViewLookTarget == null || _cutTargetsRoot == null)
            {
                Debug.LogError("[MeasureCutMinigame] Missing world camera pose/look target/cut target root.", this);
                return false;
            }

            if (_cutTargetsRoot.childCount < 1)
            {
                Debug.LogError("[MeasureCutMinigame] CutTargets root has no active cut lines.", this);
                return false;
            }

            if (_guideLine01 == null || _guideLine02 == null || _guideLine03 == null)
            {
                Debug.LogError("[MeasureCutMinigame] Missing GuideLine renderer references.", this);
                return false;
            }

            if (_toleranceBand01 == null || _toleranceBand02 == null || _toleranceBand03 == null)
            {
                Debug.LogError("[MeasureCutMinigame] Missing ToleranceBand renderer references.", this);
                return false;
            }

            return true;
        }

        private bool InitializeCutLines()
        {
            _cutLines.Clear();
            _guideLines.Clear();
            _toleranceBands.Clear();
            _guideLines.Add(_guideLine01);
            _guideLines.Add(_guideLine02);
            _guideLines.Add(_guideLine03);
            _toleranceBands.Add(_toleranceBand01);
            _toleranceBands.Add(_toleranceBand02);
            _toleranceBands.Add(_toleranceBand03);

            for (int i = 0; i < _cutTargetsRoot.childCount; i++)
            {
                Transform cutLine = _cutTargetsRoot.GetChild(i);
                if (cutLine == null || !cutLine.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 center = cutLine.position;
                Vector3 direction = cutLine.right.sqrMagnitude > 0.001f ? cutLine.right.normalized : Vector3.right;
                float halfLength = Mathf.Max(0.1f, cutLine.lossyScale.x) * 0.5f;
                CutLineState lineState = new CutLineState
                {
                    lineTransform = cutLine,
                    lineStartWorld = center - (direction * halfLength),
                    lineEndWorld = center + (direction * halfLength),
                    completed = false,
                    quality01 = 0f
                };

                _cutLines.Add(lineState);
            }

            RefreshCuttingPlane();
            UpdateGuideVisualStates();
            return _cutLines.Count > 0;
        }

        private void RefreshCuttingPlane()
        {
            if (_cutLines.Count > 0 && _cutLines[0].lineTransform != null)
            {
                Transform reference = _cutLines[0].lineTransform;
                Vector3 normal = reference.up.sqrMagnitude > 0.0001f ? reference.up.normalized : Vector3.up;
                _cuttingPlane = new Plane(normal, reference.position);
                _hasCuttingPlane = true;
                return;
            }

            if (_cutTargetsRoot != null)
            {
                Vector3 normal = _cutTargetsRoot.up.sqrMagnitude > 0.0001f ? _cutTargetsRoot.up.normalized : Vector3.up;
                _cuttingPlane = new Plane(normal, _cutTargetsRoot.position);
                _hasCuttingPlane = true;
                return;
            }

            _cuttingPlane = new Plane(Vector3.up, Vector3.zero);
            _hasCuttingPlane = true;
        }

        private void UpdateTimer()
        {
            _remainingTimeSeconds = Mathf.Max(0f, _remainingTimeSeconds - Time.deltaTime);
            UpdateTimerUi();
        }

        private void CacheCutAction()
        {
            _cutAction = null;
            if (Game.Input.InputManager.Instance == null)
            {
                return;
            }

            InputActionAsset inputAsset = Game.Input.InputManager.Instance.GetInputActionAsset();
            InputActionMap playerMap = inputAsset != null ? inputAsset.FindActionMap("Player") : null;
            _cutAction = playerMap != null ? playerMap.FindAction("Attack") : null;
        }

        private void UpdateCutterVisualAndInput()
        {
            if (!TryGetMouseWorldPosition(out Vector3 worldMousePosition))
            {
                return;
            }

            if (_cutterVisual != null)
            {
                _cutterVisual.position = worldMousePosition;
            }

            bool cutHeld = _cutAction != null && _cutAction.IsPressed();
            if (cutHeld)
            {
                int targetIndex = FindClosestIncompleteCut(worldMousePosition);
                if (targetIndex >= 0)
                {
                    _activeCutIndex = targetIndex;
                    _isCutHeld = true;
                    _cutLines[targetIndex].sampledPoints.Add(worldMousePosition);
                    bool wasCompleted = _cutLines[targetIndex].completed;

                    MeasureCutScoring.CutScoreResult preview = MeasureCutScoring.ScoreHorizontalCut(
                        _cutLines[targetIndex].sampledPoints,
                        _cutLines[targetIndex].lineStartWorld,
                        _cutLines[targetIndex].lineEndWorld,
                        _lineToleranceWorld);
                    _cutLines[targetIndex].quality01 = preview.quality01;
                    _activeLineQuality01 = preview.quality01;
                    _activeLineProgress01 = preview.completion01;

                    if (preview.completion01 >= 0.95f)
                    {
                        _cutLines[targetIndex].completed = true;
                        if (!wasCompleted)
                        {
                            EmitCutCompleteFeedback(_cutLines[targetIndex].lineTransform != null
                                ? _cutLines[targetIndex].lineTransform.position
                                : worldMousePosition);
                        }
                        _activeCutIndex = -1;
                    }
                }
                else
                {
                    _activeLineQuality01 = 0f;
                    _activeLineProgress01 = 0f;
                }
            }
            else if (_isCutHeld && _activeCutIndex >= 0 && _activeCutIndex < _cutLines.Count)
            {
                bool wasCompleted = _cutLines[_activeCutIndex].completed;
                MeasureCutScoring.CutScoreResult result = MeasureCutScoring.ScoreHorizontalCut(
                    _cutLines[_activeCutIndex].sampledPoints,
                    _cutLines[_activeCutIndex].lineStartWorld,
                    _cutLines[_activeCutIndex].lineEndWorld,
                    _lineToleranceWorld);

                _cutLines[_activeCutIndex].quality01 = result.quality01;
                _cutLines[_activeCutIndex].completed = result.completion01 >= 0.95f;
                if (_cutLines[_activeCutIndex].completed && !wasCompleted)
                {
                    EmitCutCompleteFeedback(_cutLines[_activeCutIndex].lineTransform != null
                        ? _cutLines[_activeCutIndex].lineTransform.position
                        : worldMousePosition);
                }
                _activeCutIndex = -1;
                _activeLineQuality01 = 0f;
                _activeLineProgress01 = 0f;
            }

            _isCutHeld = cutHeld;
            UpdateCuttingLoopFeedback(cutHeld && _activeCutIndex >= 0);
            UpdateGuideVisualStates();
            UpdateQualityUi();
            UpdateCutProgressUi();
        }

        private int FindClosestIncompleteCut(Vector3 worldMousePosition)
        {
            int closestIndex = -1;
            float closestDistance = float.MaxValue;
            for (int i = 0; i < _cutLines.Count; i++)
            {
                if (_cutLines[i].completed)
                {
                    continue;
                }

                Vector3 nearest = NearestPointOnLineSegment(_cutLines[i].lineStartWorld, _cutLines[i].lineEndWorld, worldMousePosition);
                float distance = Vector3.Distance(nearest, worldMousePosition);
                if (distance < closestDistance && distance <= (_lineToleranceWorld * 3f))
                {
                    closestDistance = distance;
                    closestIndex = i;
                }
            }

            return closestIndex;
        }

        private bool AllCutsCompleted()
        {
            if (_cutLines.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < _cutLines.Count; i++)
            {
                if (!_cutLines[i].completed)
                {
                    return false;
                }
            }

            return true;
        }

        private void FinishAndScore()
        {
            float qualitySum = 0f;
            bool allCompleted = true;
            for (int i = 0; i < _cutLines.Count; i++)
            {
                MeasureCutScoring.CutScoreResult result = MeasureCutScoring.ScoreHorizontalCut(
                    _cutLines[i].sampledPoints,
                    _cutLines[i].lineStartWorld,
                    _cutLines[i].lineEndWorld,
                    _lineToleranceWorld);
                _cutLines[i].quality01 = result.quality01;
                _cutLines[i].completed = result.completion01 >= 0.95f;
                qualitySum += _cutLines[i].quality01;
                allCompleted &= _cutLines[i].completed;
            }

            _aggregateQualityScore = _cutLines.Count > 0 ? (qualitySum / _cutLines.Count) * 100f : 0f;
            _aggregateQualityScore = Mathf.Clamp(_aggregateQualityScore, 0f, 100f);
            _minigameData.SetParameter("measure_cut_quality", _aggregateQualityScore);
            UpdateGuideVisualStates();

            MinigameResult resultState = allCompleted && _aggregateQualityScore >= _requiredQualityThreshold
                ? MinigameResult.Pass
                : MinigameResult.Fail;
            RequestFinish(resultState);
        }

        private void RequestFinish(MinigameResult result)
        {
            if (result == MinigameResult.None || _isFinishing)
            {
                return;
            }

            _isFinishing = true;
            _pendingResult = result;
            _minigameCanvas.enabled = false;
            ApplySplitVisualsForFinish();
            StopCuttingLoopFeedback();

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
                GameObject cameraObject = new GameObject("[MeasureCutWorldViewCamera]", typeof(Camera));
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
                    _playerController = FindAnyObjectByType<PlayerController>();
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

        private bool TryGetMouseWorldPosition(out Vector3 worldMousePosition)
        {
            worldMousePosition = Vector3.zero;
            Camera activeCamera = _worldViewCamera != null && _worldViewCamera.enabled ? _worldViewCamera : ResolveGameplayViewCamera();
            if (activeCamera == null)
            {
                return false;
            }

            if (!_hasCuttingPlane)
            {
                RefreshCuttingPlane();
            }

            Ray mouseRay = activeCamera.ScreenPointToRay(UnityEngine.Input.mousePosition);
            if (!_cuttingPlane.Raycast(mouseRay, out float enter))
            {
                return false;
            }

            worldMousePosition = mouseRay.GetPoint(enter);
            return true;
        }

        private static Vector3 NearestPointOnLineSegment(Vector3 start, Vector3 end, Vector3 point)
        {
            Vector3 line = end - start;
            float lineLengthSq = line.sqrMagnitude;
            if (lineLengthSq <= 0.0001f)
            {
                return start;
            }

            float t = Mathf.Clamp01(Vector3.Dot(point - start, line) / lineLengthSq);
            return start + (line * t);
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

        private void UpdateQualityUi()
        {
            if (_qualityText == null)
            {
                return;
            }

            float aggregate = 0f;
            for (int i = 0; i < _cutLines.Count; i++)
            {
                aggregate += _cutLines[i].quality01;
            }

            float quality = _cutLines.Count > 0 ? (aggregate / _cutLines.Count) * 100f : 0f;
            if (_activeCutIndex >= 0 && _activeCutIndex < _cutLines.Count && _isCutHeld)
            {
                _qualityText.text = $"Quality: {quality:0} | Current: {_activeLineQuality01 * 100f:0}%";
                return;
            }

            _qualityText.text = $"Quality: {quality:0}";
        }

        private void UpdateInstructionUi()
        {
            if (_instructionText == null)
            {
                return;
            }

            _instructionText.text = "Move mouse to guide cutter. Hold LMB to cut on marked lines.";
        }

        private void UpdateCutProgressUi()
        {
            if (_cutProgressText == null)
            {
                return;
            }

            int completedCount = 0;
            for (int i = 0; i < _cutLines.Count; i++)
            {
                if (_cutLines[i].completed)
                {
                    completedCount++;
                }
            }

            if (_activeCutIndex >= 0 && _activeCutIndex < _cutLines.Count && _isCutHeld)
            {
                _cutProgressText.text = $"Cuts: {completedCount}/{_cutLines.Count} | Line: {_activeLineProgress01 * 100f:0}%";
                return;
            }

            _cutProgressText.text = $"Cuts: {completedCount}/{_cutLines.Count}";
        }

        private void InitializeVisualFeedback()
        {
            if (_fullPipeVisual != null)
            {
                _fullPipeVisual.SetActive(true);
            }

            if (_leftSegment != null)
            {
                _leftSegmentStartLocalPosition = _leftSegment.localPosition;
                _leftSegment.gameObject.SetActive(false);
            }

            if (_rightSegment != null)
            {
                _rightSegmentStartLocalPosition = _rightSegment.localPosition;
                _rightSegment.gameObject.SetActive(false);
            }

            _splitVisualApplied = false;
            UpdateGuideVisualStates();
        }

        private void ApplySplitVisualsForFinish()
        {
            if (_splitVisualApplied)
            {
                return;
            }

            _splitVisualApplied = true;
            if (_fullPipeVisual != null)
            {
                _fullPipeVisual.SetActive(false);
            }

            if (_leftSegment != null)
            {
                _leftSegment.gameObject.SetActive(true);
                _leftSegment.localPosition = _leftSegmentStartLocalPosition + (Vector3.left * _splitSeparationDistance);
            }

            if (_rightSegment != null)
            {
                _rightSegment.gameObject.SetActive(true);
                _rightSegment.localPosition = _rightSegmentStartLocalPosition + (Vector3.right * _splitSeparationDistance);
            }
        }

        private void UpdateGuideVisualStates()
        {
            int count = Mathf.Min(_cutLines.Count, Mathf.Min(_guideLines.Count, _toleranceBands.Count));
            for (int i = 0; i < count; i++)
            {
                Renderer lineRenderer = _guideLines[i];
                Renderer toleranceRenderer = _toleranceBands[i];
                if (lineRenderer == null || toleranceRenderer == null)
                {
                    continue;
                }

                if (_cutLines[i].completed)
                {
                    ApplyRendererColor(lineRenderer, _completedColor);
                    ApplyRendererColor(toleranceRenderer, new Color(_completedColor.r, _completedColor.g, _completedColor.b, _toleranceNeutralColor.a));
                    continue;
                }

                if (_activeCutIndex == i && _isCutHeld)
                {
                    Color liveColor = EvaluateLiveQualityColor(_activeLineQuality01);
                    ApplyRendererColor(lineRenderer, liveColor);
                    ApplyRendererColor(toleranceRenderer, new Color(liveColor.r, liveColor.g, liveColor.b, _toleranceNeutralColor.a));
                    continue;
                }

                ApplyRendererColor(lineRenderer, _guideNeutralColor);
                ApplyRendererColor(toleranceRenderer, _toleranceNeutralColor);
            }
        }

        private Color EvaluateLiveQualityColor(float quality01)
        {
            if (quality01 < 0.4f)
            {
                return _poorCutColor;
            }

            if (quality01 < 0.75f)
            {
                return _mediumCutColor;
            }

            return _goodCutColor;
        }

        private static void ApplyRendererColor(Renderer renderer, Color color)
        {
            if (renderer == null || renderer.material == null)
            {
                return;
            }

            renderer.material.color = color;
        }

        private void UpdateCuttingLoopFeedback(bool shouldPlay)
        {
            if (_cuttingLoopFx != null)
            {
                if (shouldPlay)
                {
                    if (!_cuttingLoopFx.isPlaying)
                    {
                        _cuttingLoopFx.Play();
                    }
                }
                else if (_cuttingLoopFx.isPlaying)
                {
                    _cuttingLoopFx.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            if (_audioSource != null && _cuttingLoopClip != null)
            {
                if (shouldPlay)
                {
                    if (_audioSource.clip != _cuttingLoopClip)
                    {
                        _audioSource.clip = _cuttingLoopClip;
                        _audioSource.loop = true;
                    }

                    if (!_audioSource.isPlaying)
                    {
                        _audioSource.Play();
                    }
                }
                else if (_audioSource.isPlaying && _audioSource.clip == _cuttingLoopClip)
                {
                    _audioSource.Stop();
                }
            }
        }

        private void StopCuttingLoopFeedback()
        {
            UpdateCuttingLoopFeedback(false);
        }

        private void EmitCutCompleteFeedback(Vector3 worldPosition)
        {
            if (_cutCompleteFx != null)
            {
                _cutCompleteFx.transform.position = worldPosition;
                _cutCompleteFx.Play();
            }

            if (_audioSource != null && _cutCompleteClip != null)
            {
                _audioSource.PlayOneShot(_cutCompleteClip);
            }
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
    }
}
