using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.Player;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Minigames
{
    public sealed class DrillScrewMinigame : BaseMinigame
    {
        private enum DrillScrewPhase
        {
            Drill = 0,
            Screw = 1
        }

        private sealed class HoleState
        {
            public Transform transform;
            public Renderer renderer;
            public float drillProgress;
            public float drillOverrun;
            public float drillDistanceAccum;
            public int drillDistanceSamples;
            public float earlyReleasePenalty;
            public float screwTightness;
            public float screwOvertight;
            public bool drillStarted;
            public bool drilledComplete;
            public bool screwAccepted;
        }

        private const string CursorAuthorityOwner = "minigame_drill_screw";
        private const float DefaultTimeLimitSeconds = 30f;

        [SerializeField] private Canvas _minigameCanvas;
        [SerializeField] private TMP_Text _timerText;
        [SerializeField] private TMP_Text _qualityText;
        [SerializeField] private TMP_Text _phaseText;
        [SerializeField] private TMP_Text _progressText;

        [SerializeField] private Transform _worldViewPose;
        [SerializeField] private Transform _worldViewLookTarget;
        [SerializeField] private Transform _targetsRoot;
        [SerializeField] private Transform _toolVisual;
        [SerializeField] private float _cameraTransitionDuration = 0.45f;
        [SerializeField] private float _cameraReturnDuration = 0.3f;
        [SerializeField] private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);
        [SerializeField] private Vector3 _worldLookTargetLocalOffset = Vector3.zero;
        [SerializeField] private float _worldTopDownAngleBias = 16f;
        [SerializeField] private float _worldCameraFovOverride = 50f;
        [SerializeField] private bool _freezePlayerMovementInWorldView = true;

        [SerializeField] private float _timeLimitSeconds = DefaultTimeLimitSeconds;
        [SerializeField] private int _requiredHoleCount = 4;
        [SerializeField] private float _drillRadiusWorld = 0.08f;
        [SerializeField] private float _drillProgressPerSecond = 0.65f;
        [SerializeField] private float _drillOverrunPenaltyPerSecond = 0.3f;
        [SerializeField] private float _screwAcceptThreshold01 = 0.65f;
        [SerializeField] private float _screwTargetTightness01 = 0.85f;
        [SerializeField] private float _screwOvertightThreshold01 = 1.05f;
        [SerializeField] private float _screwRotationDegreesForFullTightness = 540f;
        [SerializeField] private Color _neutralColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        [SerializeField] private Color _activeColor = new Color(1f, 0.92f, 0.2f, 1f);
        [SerializeField] private Color _completeColor = new Color(0.2f, 0.85f, 0.35f, 1f);

        private readonly List<HoleState> _holes = new List<HoleState>(4);

        private DrillScrewPhase _phase;
        private int _activeHoleIndex;
        private float _remainingTimeSeconds;
        private float _lastQualityScore;
        private bool _setupValid;

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
        private InputAction _primaryAction;
        private PlayerController _playerController;
        private bool _playerControllerWasEnabled;
        private Vector2 _previousMousePosition;
        private bool _hasPreviousMousePosition;

        protected override void OnInitialize()
        {
            LoadParameters();
            _phase = DrillScrewPhase.Drill;
            _activeHoleIndex = 0;
            _remainingTimeSeconds = _timeLimitSeconds;
            _lastQualityScore = 100f;
            _setupValid = ValidateContract();
            CacheInputAction();
            UpdateAllUi();
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
            if (!InitializeTargets())
            {
                SetResult(MinigameResult.Fail);
                return;
            }

            StartWorldViewPresentation();
            SetPlayerMovementFrozen(true);
            UpdateHoleVisualStates();
            UpdateAllUi();
        }

        protected override void OnUpdate()
        {
            if (_isFinishing)
            {
                UpdateFinishFlow();
                return;
            }

            _remainingTimeSeconds = Mathf.Max(0f, _remainingTimeSeconds - Time.deltaTime);
            if (_remainingTimeSeconds <= 0f)
            {
                FinishAndScore();
                return;
            }

            if (_phase == DrillScrewPhase.Drill)
            {
                UpdateDrillPhase();
            }
            else
            {
                UpdateScrewPhase();
            }

            _lastQualityScore = ComputeCurrentQualityScore();
            UpdateAllUi();
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
            _holes.Clear();
            _isFinishing = false;
            _isReturningToGameplayView = false;
            _pendingResult = MinigameResult.None;
            _hasPreviousMousePosition = false;
        }

        private void LoadParameters()
        {
            _minigameCanvas = GetParameter<Canvas>("canvas") ?? _minigameCanvas;
            _timerText = GetParameter<TMP_Text>("timer_text") ?? _timerText;
            _qualityText = GetParameter<TMP_Text>("quality_text") ?? _qualityText;
            _phaseText = GetParameter<TMP_Text>("phase_text") ?? _phaseText;
            _progressText = GetParameter<TMP_Text>("progress_text") ?? _progressText;

            _worldViewPose = GetParameter<Transform>("world_camera_pose") ?? _worldViewPose;
            _worldViewLookTarget = GetParameter<Transform>("world_look_target") ?? _worldViewLookTarget;
            _targetsRoot = GetParameter<Transform>("world_drill_targets_root") ?? _targetsRoot;
            _toolVisual = GetParameter<Transform>("world_tool_visual") ?? _toolVisual;
            _cameraTransitionDuration = GetFloat("world_camera_transition", _cameraTransitionDuration);
            _cameraReturnDuration = GetFloat("world_camera_return", _cameraReturnDuration);
            _worldCameraLocalOffset = GetVector3("world_camera_local_offset", _worldCameraLocalOffset);
            _worldLookTargetLocalOffset = GetVector3("world_look_target_local_offset", _worldLookTargetLocalOffset);
            _worldTopDownAngleBias = GetFloat("world_top_down_angle_bias", _worldTopDownAngleBias);
            _worldCameraFovOverride = GetFloat("world_camera_fov", _worldCameraFovOverride);
            _freezePlayerMovementInWorldView = GetBool("world_freeze_player", _freezePlayerMovementInWorldView);
            _drillRadiusWorld = Mathf.Max(0.02f, GetFloat("drill_radius_world", _drillRadiusWorld));
            _drillProgressPerSecond = Mathf.Max(0.1f, GetFloat("drill_progress_per_second", _drillProgressPerSecond));
            _drillOverrunPenaltyPerSecond = Mathf.Max(0f, GetFloat("drill_overrun_penalty_per_second", _drillOverrunPenaltyPerSecond));
            _screwAcceptThreshold01 = Mathf.Clamp01(GetFloat("screw_accept_threshold", _screwAcceptThreshold01));
            _screwTargetTightness01 = Mathf.Clamp01(GetFloat("screw_target_tightness", _screwTargetTightness01));
            _screwOvertightThreshold01 = Mathf.Max(_screwTargetTightness01, GetFloat("screw_overtight_threshold", _screwOvertightThreshold01));
            _screwRotationDegreesForFullTightness = Mathf.Max(90f, GetFloat("screw_rotation_degrees_full", _screwRotationDegreesForFullTightness));

            float configuredTime = GetFloat("time_limit", _timeLimitSeconds);
            configuredTime = GetFloat("timeLimit", configuredTime);
            _timeLimitSeconds = configuredTime > 0f ? configuredTime : DefaultTimeLimitSeconds;
            _minigameData.timeLimit = 0f;
        }

        private bool ValidateContract()
        {
            if (_minigameCanvas == null || _timerText == null || _qualityText == null || _phaseText == null)
            {
                Debug.LogError("[DrillScrewMinigame] Missing canvas/timer/quality/phase references.", this);
                return false;
            }

            if (_worldViewPose == null || _worldViewLookTarget == null || _targetsRoot == null)
            {
                Debug.LogError("[DrillScrewMinigame] Missing world camera pose/look target/drill target root.", this);
                return false;
            }

            if (_targetsRoot.childCount < _requiredHoleCount)
            {
                Debug.LogError("[DrillScrewMinigame] DrillTargetsRoot needs at least 4 active hole targets.", this);
                return false;
            }

            return true;
        }

        private bool InitializeTargets()
        {
            _holes.Clear();
            for (int i = 0; i < _targetsRoot.childCount; i++)
            {
                Transform holeTransform = _targetsRoot.GetChild(i);
                if (holeTransform == null || !holeTransform.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Renderer holeRenderer = holeTransform.GetComponent<Renderer>();
                if (holeRenderer == null)
                {
                    holeRenderer = holeTransform.GetComponentInChildren<Renderer>();
                }

                _holes.Add(new HoleState
                {
                    transform = holeTransform,
                    renderer = holeRenderer
                });

                if (_holes.Count >= _requiredHoleCount)
                {
                    break;
                }
            }

            if (_holes.Count < _requiredHoleCount)
            {
                Debug.LogError("[DrillScrewMinigame] Could not resolve 4 active drill hole targets.", this);
                return false;
            }

            return true;
        }

        private void UpdateDrillPhase()
        {
            HoleState activeHole = GetActiveHole();
            if (activeHole == null || activeHole.transform == null)
            {
                return;
            }

            if (!TryGetMouseWorldPoint(out Vector3 mousePoint, activeHole.transform))
            {
                return;
            }

            UpdateToolVisual(mousePoint);

            bool held = IsPrimaryHeld();
            float distance = Vector3.Distance(mousePoint, activeHole.transform.position);
            float normalizedDistance = distance / Mathf.Max(0.001f, _drillRadiusWorld);
            bool aligned = distance <= _drillRadiusWorld;

            if (held)
            {
                activeHole.drillStarted = true;
                activeHole.drillDistanceAccum += Mathf.Clamp01(normalizedDistance);
                activeHole.drillDistanceSamples++;

                if (aligned)
                {
                    if (!activeHole.drilledComplete)
                    {
                        activeHole.drillProgress += Time.deltaTime * _drillProgressPerSecond * (1f - Mathf.Clamp01(normalizedDistance));
                        if (activeHole.drillProgress >= 1f)
                        {
                            activeHole.drilledComplete = true;
                            activeHole.drillProgress = 1f;
                        }
                    }
                    else
                    {
                        activeHole.drillOverrun += Time.deltaTime * _drillOverrunPenaltyPerSecond;
                    }
                }
            }
            else if (activeHole.drillStarted && !activeHole.drilledComplete)
            {
                activeHole.earlyReleasePenalty += Time.deltaTime * 0.5f;
            }

            if (activeHole.drilledComplete && _activeHoleIndex < _holes.Count - 1)
            {
                _activeHoleIndex++;
            }

            if (AllDrillComplete())
            {
                _phase = DrillScrewPhase.Screw;
                _activeHoleIndex = 0;
                _hasPreviousMousePosition = false;
            }

            UpdateHoleVisualStates();
        }

        private void UpdateScrewPhase()
        {
            HoleState activeHole = GetActiveHole();
            if (activeHole == null || activeHole.transform == null)
            {
                return;
            }

            if (!TryGetMouseWorldPoint(out Vector3 mousePoint, activeHole.transform))
            {
                return;
            }

            UpdateToolVisual(mousePoint);

            Vector2 mousePosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            Camera activeCamera = _worldViewCamera != null ? _worldViewCamera : ResolveGameplayViewCamera();
            if (activeCamera == null)
            {
                return;
            }

            Vector2 targetScreen = activeCamera.WorldToScreenPoint(activeHole.transform.position);
            if (!_hasPreviousMousePosition)
            {
                _previousMousePosition = mousePosition;
                _hasPreviousMousePosition = true;
            }

            if (IsPrimaryHeld())
            {
                Vector2 prevVec = _previousMousePosition - targetScreen;
                Vector2 currentVec = mousePosition - targetScreen;
                if (prevVec.sqrMagnitude > 16f && currentVec.sqrMagnitude > 16f)
                {
                    float deltaAngle = Mathf.Abs(Vector2.SignedAngle(prevVec, currentVec));
                    float deltaTightness = deltaAngle / Mathf.Max(90f, _screwRotationDegreesForFullTightness);
                    activeHole.screwTightness += deltaTightness;
                    if (activeHole.screwTightness > _screwOvertightThreshold01)
                    {
                        activeHole.screwOvertight += deltaTightness;
                    }
                }
            }

            _previousMousePosition = mousePosition;

            if (activeHole.screwTightness >= _screwAcceptThreshold01)
            {
                activeHole.screwAccepted = true;
                if (_activeHoleIndex < _holes.Count - 1)
                {
                    _activeHoleIndex++;
                    _hasPreviousMousePosition = false;
                }
            }

            if (AllScrewAccepted())
            {
                FinishAndScore();
                return;
            }

            UpdateHoleVisualStates();
        }

        private bool IsPrimaryHeld()
        {
            if (_primaryAction != null)
            {
                return _primaryAction.IsPressed();
            }

            return Mouse.current != null && Mouse.current.leftButton.isPressed;
        }

        private HoleState GetActiveHole()
        {
            if (_activeHoleIndex < 0 || _activeHoleIndex >= _holes.Count)
            {
                return null;
            }

            return _holes[_activeHoleIndex];
        }

        private bool AllDrillComplete()
        {
            for (int i = 0; i < _holes.Count; i++)
            {
                if (!_holes[i].drilledComplete)
                {
                    return false;
                }
            }

            return true;
        }

        private bool AllScrewAccepted()
        {
            for (int i = 0; i < _holes.Count; i++)
            {
                if (!_holes[i].screwAccepted)
                {
                    return false;
                }
            }

            return true;
        }

        private float ComputeCurrentQualityScore()
        {
            float drillSum = 0f;
            float screwSum = 0f;
            for (int i = 0; i < _holes.Count; i++)
            {
                HoleState hole = _holes[i];
                float averageDistance = hole.drillDistanceSamples > 0 ? hole.drillDistanceAccum / hole.drillDistanceSamples : 1f;
                float drillScore = DrillScrewScoring.ComputeDrillHoleQuality(
                    hole.drillProgress,
                    averageDistance,
                    hole.drillOverrun,
                    hole.earlyReleasePenalty);
                float overTight01 = Mathf.Max(0f, hole.screwTightness - _screwOvertightThreshold01) + hole.screwOvertight;
                float screwScore = DrillScrewScoring.ComputeScrewHoleQuality(hole.screwTightness, _screwTargetTightness01, overTight01);

                drillSum += drillScore;
                screwSum += screwScore;
            }

            float drillAverage = _holes.Count > 0 ? drillSum / _holes.Count : 0f;
            float screwAverage = _holes.Count > 0 ? screwSum / _holes.Count : 0f;
            if (_phase == DrillScrewPhase.Drill)
            {
                screwAverage = drillAverage;
            }

            return DrillScrewScoring.ComputeAggregateQuality(drillAverage, screwAverage);
        }

        private void FinishAndScore()
        {
            float qualityScore = ComputeCurrentQualityScore();
            _minigameData.SetParameter("drill_screw_quality", qualityScore);
            RequestFinish(MinigameResult.Pass);
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

        private void UpdateHoleVisualStates()
        {
            for (int i = 0; i < _holes.Count; i++)
            {
                HoleState hole = _holes[i];
                if (hole?.renderer == null || hole.renderer.material == null)
                {
                    continue;
                }

                Color color = _neutralColor;
                if (i == _activeHoleIndex)
                {
                    color = _activeColor;
                }

                if (_phase == DrillScrewPhase.Drill)
                {
                    if (hole.drilledComplete)
                    {
                        color = _completeColor;
                    }
                }
                else if (hole.screwAccepted)
                {
                    color = _completeColor;
                }

                hole.renderer.material.color = color;
            }
        }

        private void UpdateToolVisual(Vector3 mousePoint)
        {
            if (_toolVisual != null)
            {
                _toolVisual.position = mousePoint;
            }
        }

        private void CacheInputAction()
        {
            _primaryAction = null;
            if (Game.Input.InputManager.Instance == null)
            {
                return;
            }

            InputActionAsset inputAsset = Game.Input.InputManager.Instance.GetInputActionAsset();
            InputActionMap playerMap = inputAsset != null ? inputAsset.FindActionMap("Player") : null;
            _primaryAction = playerMap != null ? playerMap.FindAction("Attack") : null;
        }

        private bool TryGetMouseWorldPoint(out Vector3 worldPoint, Transform activeHoleTransform = null)
        {
            worldPoint = Vector3.zero;
            Camera activeCamera = _worldViewCamera != null && _worldViewCamera.enabled ? _worldViewCamera : ResolveGameplayViewCamera();
            if (activeCamera == null)
            {
                return false;
            }

            Ray mouseRay = activeCamera.ScreenPointToRay(UnityEngine.Input.mousePosition);
            // Use an active-hole plane so alignment distance is measured in the same spatial layer as the hole.
            // Projecting to DrillTargetsRoot.position can offset points when holes are vertically displaced from the root.
            Vector3 planeNormal = activeHoleTransform != null ? activeHoleTransform.up : Vector3.zero;
            if (planeNormal.sqrMagnitude < 0.0001f)
            {
                planeNormal = _targetsRoot != null ? _targetsRoot.up : Vector3.up;
            }

            Vector3 planePoint = activeHoleTransform != null
                ? activeHoleTransform.position
                : (_targetsRoot != null ? _targetsRoot.position : Vector3.zero);

            Plane plane = new Plane(planeNormal, planePoint);
            if (!plane.Raycast(mouseRay, out float enter))
            {
                return false;
            }

            worldPoint = mouseRay.GetPoint(enter);
            return true;
        }

        private void UpdateAllUi()
        {
            UpdateTimerUi();
            UpdateQualityUi();
            UpdatePhaseUi();
            UpdateProgressUi();
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

            _qualityText.text = $"Quality: {_lastQualityScore:0}%";
        }

        private void UpdatePhaseUi()
        {
            if (_phaseText == null)
            {
                return;
            }

            _phaseText.text = _phase == DrillScrewPhase.Drill
                ? "Phase: Drill (hold LMB on target)"
                : "Phase: Screw (rotate mouse around bolt)";
        }

        private void UpdateProgressUi()
        {
            if (_progressText == null)
            {
                return;
            }

            int drillDone = 0;
            int screwDone = 0;
            for (int i = 0; i < _holes.Count; i++)
            {
                if (_holes[i].drilledComplete)
                {
                    drillDone++;
                }

                if (_holes[i].screwAccepted)
                {
                    screwDone++;
                }
            }

            _progressText.text = $"Drill {drillDone}/{_holes.Count} | Screw {screwDone}/{_holes.Count}";
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

        private IEnumerator SmoothTransitionToWorldView()
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
                GameObject cameraObject = new GameObject("[DrillScrewWorldViewCamera]", typeof(Camera));
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
