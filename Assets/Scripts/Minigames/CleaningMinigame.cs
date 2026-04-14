using System;
using System.Collections.Generic;
using Game.Core;
using Game.Player;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.Minigames
{
    /// <summary>
    /// Cleaning minigame that always runs in world mode against explicit cleaning surfaces.
    /// </summary>
    public class CleaningMinigame : BaseMinigame
    {
        private sealed class WorldStainState
        {
            public Transform MarkerTransform;
            public Renderer MarkerRenderer;
            public int RequiredSwipes;
            public float SwipeProgress;
            public bool IsCleaned;
        }

        private const int FixedSwipesPerStain = 6;
        private const int DefaultStainsPerSurface = 2;
        private const float DefaultTimeLimitSeconds = 30f;
        private const int DefaultTimeoutCurrencyPenalty = 10;

        [SerializeField]
        private bool _lockCursorDuringMinigame = true;

        [Header("World View")]
        [SerializeField] private Transform _worldViewPose;
        [SerializeField] private Transform _worldViewLookTarget;
        [SerializeField] private Transform _worldCleaningSurfaceRoot;
        [SerializeField] private float _cameraTransitionDuration = 0.45f;
        [SerializeField] private float _cameraReturnDuration = 0.3f;
        [SerializeField] private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);
        [SerializeField] private Vector3 _worldLookTargetLocalOffset = Vector3.zero;
        [SerializeField] private float _worldTopDownAngleBias = 16f;
        [SerializeField] private float _worldCameraFovOverride = 52f;
        [SerializeField] private bool _freezePlayerMovementInWorldView = true;

        [Header("World Stains")]
        [SerializeField] private float _worldStainScreenRadiusPixels = 52f;
        [SerializeField] private float _minMouseMovePixelsForCleaning = 1.5f;
        [SerializeField] private float _worldSwipeGainPerPixel = 0.02f;
        [SerializeField] private float _worldStainMarkerScale = 0.05f;
        [SerializeField] private Color _worldStainDirtyColor = new Color(0.1f, 0.1f, 0.1f, 0.95f);
        [SerializeField] private Color _worldStainHoverColor = new Color(1f, 0.88f, 0.4f, 1f);
        [SerializeField] private Color _worldStainCleanColor = new Color(0.25f, 1f, 0.4f, 0.25f);
        [SerializeField] [Min(1)] private int _worldStainsPerSurface = DefaultStainsPerSurface;

        private float _timeLimitSeconds = DefaultTimeLimitSeconds;
        private float _remainingTimeSeconds = DefaultTimeLimitSeconds;
        private int _timeoutCurrencyPenalty = DefaultTimeoutCurrencyPenalty;

        private Canvas _minigameCanvas;
        private readonly List<WorldStainState> _worldStains = new List<WorldStainState>(24);
        private readonly List<Transform> _worldSpawnSurfaces = new List<Transform>(8);
        private readonly List<Bounds> _worldSpawnSurfaceBounds = new List<Bounds>(8);
        private int _totalStains;
        private int _cleanedStains;
        private bool _isInitialized;
        private bool _isUsingWorldPresentation;

        private Camera _gameplayViewCamera;
        private Camera _worldViewCamera;
        private Coroutine _cameraTransitionRoutine;
        private bool _gameplayCameraWasEnabled;
        private bool _hasGameplayCameraRenderOverride;
        private bool _isFinishing;
        private bool _isReturningToGameplayView;
        private float _returnTransitionElapsed;
        private Vector3 _returnTransitionStartPosition;
        private Quaternion _returnTransitionStartRotation;
        private MinigameResult _pendingResult = MinigameResult.None;
        private bool _hasMousePosition;
        private Vector2 _previousMousePosition;

        private PlayerController _playerController;
        private bool _playerControllerWasEnabled;

        private TextMeshProUGUI _timerText;
        private TextMeshProUGUI _progressText;
        private bool _hasProcessedTimeoutFailure;

        private CursorLockMode _previousCursorLockMode;
        private bool _previousCursorVisible;

        protected override void OnInitialize()
        {
            LoadParameters();

            if (_minigameCanvas != null)
            {
                _minigameCanvas.enabled = false;
            }

            _worldStains.Clear();
            _worldSpawnSurfaces.Clear();
            _worldSpawnSurfaceBounds.Clear();
            _totalStains = 0;
            _cleanedStains = 0;
            _isUsingWorldPresentation = false;
            _hasProcessedTimeoutFailure = false;
            _remainingTimeSeconds = _timeLimitSeconds;
            _isFinishing = false;
            _isReturningToGameplayView = false;
            _pendingResult = MinigameResult.None;
            _hasMousePosition = false;

            ConfigureAssignedTimerUI();
            ConfigureAssignedProgressUI();
            UpdateTimerUI();
            UpdateProgressUI();
            ResolveGameplayViewCamera();

            _isInitialized = ValidateWorldContract(logErrors: true);
        }

        protected override void OnStart()
        {
            if (!_isInitialized)
            {
                SetResult(MinigameResult.Fail);
                return;
            }

            if (_minigameCanvas != null)
            {
                _minigameCanvas.enabled = true;
            }

            if (_lockCursorDuringMinigame)
            {
                LockCursor();
            }

            _hasProcessedTimeoutFailure = false;
            _remainingTimeSeconds = _timeLimitSeconds;
            UpdateTimerUI();

            if (!TryStartWorldPresentation())
            {
                SetResult(MinigameResult.Fail);
                return;
            }

            UpdateProgressUI();
        }

        protected override void OnUpdate()
        {
            if (_isFinishing)
            {
                UpdateFinishFlow();
                return;
            }

            UpdateTimer();
            if (_hasProcessedTimeoutFailure)
            {
                return;
            }

            UpdateWorldCleaningInteraction();
            UpdateProgressUI();

            if (_cleanedStains >= _totalStains && _totalStains > 0)
            {
                RequestFinish(MinigameResult.Pass);
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

            if (_lockCursorDuringMinigame)
            {
                UnlockCursor();
            }

            CleanupWorldStains();
            TearDownWorldViewPresentation();
            SetPlayerMovementFrozen(false);

            if (_timerText != null)
            {
                _timerText.text = string.Empty;
            }

            if (_progressText != null)
            {
                _progressText.text = string.Empty;
            }

            _isUsingWorldPresentation = false;
            _isFinishing = false;
            _isReturningToGameplayView = false;
            _pendingResult = MinigameResult.None;
            _hasMousePosition = false;
        }

        private void LoadParameters()
        {
            if (_minigameData == null)
            {
                return;
            }

            Canvas canvasParameter = GetParameter<Canvas>("canvas");
            if (canvasParameter != null)
            {
                _minigameCanvas = canvasParameter;
            }

            TextMeshProUGUI timerTextParameter = GetParameter<TextMeshProUGUI>("timer_text");
            if (timerTextParameter != null)
            {
                _timerText = timerTextParameter;
            }

            TextMeshProUGUI progressTextParameter = GetParameter<TextMeshProUGUI>("progress_text");
            if (progressTextParameter != null)
            {
                _progressText = progressTextParameter;
            }

            _worldViewPose = GetParameter<Transform>("world_camera_pose") ?? _worldViewPose;
            _worldViewLookTarget = GetParameter<Transform>("world_look_target") ?? _worldViewLookTarget;
            _worldCleaningSurfaceRoot = GetParameter<Transform>("world_cleaning_surface_root") ?? _worldCleaningSurfaceRoot;
            _cameraTransitionDuration = GetParameterFloat("world_camera_transition") ?? _cameraTransitionDuration;
            _cameraReturnDuration = GetParameterFloat("world_camera_return") ?? _cameraReturnDuration;
            _worldCameraLocalOffset = GetParameterVector3("world_camera_local_offset") ?? _worldCameraLocalOffset;
            _worldLookTargetLocalOffset = GetParameterVector3("world_look_target_local_offset") ?? _worldLookTargetLocalOffset;
            _worldTopDownAngleBias = GetParameterFloat("world_top_down_angle_bias") ?? _worldTopDownAngleBias;
            _worldCameraFovOverride = GetParameterFloat("world_camera_fov") ?? _worldCameraFovOverride;

            _worldStainScreenRadiusPixels = Mathf.Max(8f, GetParameterFloat("world_stain_screen_radius") ?? _worldStainScreenRadiusPixels);
            _minMouseMovePixelsForCleaning = Mathf.Max(0.1f, GetParameterFloat("world_min_mouse_move_pixels") ?? _minMouseMovePixelsForCleaning);
            _worldSwipeGainPerPixel = Mathf.Max(0.001f, GetParameterFloat("world_swipe_gain_per_pixel") ?? _worldSwipeGainPerPixel);
            _worldStainMarkerScale = Mathf.Max(0.01f, GetParameterFloat("world_stain_marker_scale") ?? _worldStainMarkerScale);
            _worldStainsPerSurface = Mathf.Max(1, GetParameterInt("world_stains_per_surface") ?? _worldStainsPerSurface);
            _freezePlayerMovementInWorldView = GetParameterBool("world_freeze_player") ?? _freezePlayerMovementInWorldView;

            float? parameterTimeLimit = GetParameterFloat("timeLimit");
            if (!parameterTimeLimit.HasValue)
            {
                parameterTimeLimit = GetParameterFloat("time_limit");
            }

            if (parameterTimeLimit.HasValue && parameterTimeLimit.Value > 0f)
            {
                _timeLimitSeconds = parameterTimeLimit.Value;
            }
            else if (_minigameData.timeLimit > 0f)
            {
                _timeLimitSeconds = _minigameData.timeLimit;
            }
            else
            {
                _timeLimitSeconds = DefaultTimeLimitSeconds;
            }

            int? timeoutPenalty = GetParameterInt("timeout_currency_penalty");
            if (timeoutPenalty.HasValue)
            {
                _timeoutCurrencyPenalty = Mathf.Max(0, timeoutPenalty.Value);
            }

            // This minigame owns timeout behavior to enforce fail+penalty semantics.
            _minigameData.timeLimit = 0f;

            Debug.Log(
                $"[CleaningMinigame] Parameters loaded: TimeLimit={_timeLimitSeconds:0.0}s, " +
                $"TimeoutPenalty={_timeoutCurrencyPenalty}, " +
                $"FixedSwipesPerStain={FixedSwipesPerStain}, " +
                $"StainsPerSurface={_worldStainsPerSurface}");
        }

        private bool ValidateWorldContract(bool logErrors)
        {
            List<string> failures = new List<string>(7);
            if (_minigameCanvas == null)
            {
                failures.Add("missing CleaningCanvas reference");
            }

            if (!IsTextOnCanvas(_timerText, _minigameCanvas))
            {
                failures.Add("missing TimerText reference on CleaningCanvas");
            }

            if (!IsTextOnCanvas(_progressText, _minigameCanvas))
            {
                failures.Add("missing ProgressText reference on CleaningCanvas");
            }

            if (_worldCleaningSurfaceRoot == null)
            {
                failures.Add("missing CleaningSurfaces root");
            }

            if (_worldViewPose == null)
            {
                failures.Add("missing MinigameCameraPose");
            }

            if (_worldViewLookTarget == null)
            {
                failures.Add("missing MinigameLookTarget");
            }

            if (_worldCleaningSurfaceRoot != null)
            {
                CacheWorldSpawnSurfaces();
                if (_worldSpawnSurfaces.Count <= 0)
                {
                    failures.Add("CleaningSurfaces has zero active collidable surfaces");
                }
            }

            if (failures.Count == 0)
            {
                return true;
            }

            if (logErrors)
            {
                Debug.LogError($"[CleaningMinigame] Invalid world cleaning setup: {string.Join("; ", failures)}", this);
            }

            return false;
        }

        private static bool IsTextOnCanvas(TextMeshProUGUI text, Canvas canvas)
        {
            return text != null
                && canvas != null
                && text.transform != null
                && text.transform.IsChildOf(canvas.transform);
        }

        private int? GetParameterInt(string key)
        {
            object value = GetParameter(key);
            if (value is int intValue)
            {
                return intValue;
            }

            if (value is float floatValue)
            {
                return (int)floatValue;
            }

            return null;
        }

        private float? GetParameterFloat(string key)
        {
            object value = GetParameter(key);
            if (value is float floatValue)
            {
                return floatValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            return null;
        }

        private bool? GetParameterBool(string key)
        {
            object value = GetParameter(key);
            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is int intValue)
            {
                return intValue != 0;
            }

            if (value is float floatValue)
            {
                return !Mathf.Approximately(floatValue, 0f);
            }

            return null;
        }

        private Vector3? GetParameterVector3(string key)
        {
            object value = GetParameter(key);
            if (value is Vector3 vector3Value)
            {
                return vector3Value;
            }

            return null;
        }

        private bool TryStartWorldPresentation()
        {
            CleanupWorldStains();

            if (!PrepareWorldCleaningSurface())
            {
                Debug.LogError("[CleaningMinigame] Missing world cleaning setup. Hard-failing start.", this);
                return false;
            }

            if (!InitializeWorldStains())
            {
                Debug.LogError("[CleaningMinigame] Failed to initialize world stains. Hard-failing start.", this);
                CleanupWorldStains();
                return false;
            }

            _hasMousePosition = false;
            _cleanedStains = 0;
            _totalStains = _worldStains.Count;
            _isUsingWorldPresentation = true;

            StartWorldViewPresentation();
            SetPlayerMovementFrozen(true);
            return true;
        }

        private bool PrepareWorldCleaningSurface()
        {
            if (_worldCleaningSurfaceRoot == null || _worldViewPose == null || _worldViewLookTarget == null)
            {
                return false;
            }

            CacheWorldSpawnSurfaces();
            return _worldSpawnSurfaces.Count > 0;
        }

        private void CacheWorldSpawnSurfaces()
        {
            _worldSpawnSurfaces.Clear();
            _worldSpawnSurfaceBounds.Clear();

            if (_worldCleaningSurfaceRoot == null)
            {
                return;
            }

            Transform[] transforms = _worldCleaningSurfaceRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate == _worldCleaningSurfaceRoot)
                {
                    continue;
                }

                if (!candidate.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!candidate.name.StartsWith("CleaningSurface", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryGetWorldSurfaceBounds(candidate, out Bounds candidateBounds))
                {
                    continue;
                }

                _worldSpawnSurfaces.Add(candidate);
                _worldSpawnSurfaceBounds.Add(candidateBounds);
            }

            if (_worldSpawnSurfaces.Count > 0)
            {
                return;
            }

            if (_worldCleaningSurfaceRoot.gameObject.activeInHierarchy &&
                TryGetWorldSurfaceBounds(_worldCleaningSurfaceRoot, out Bounds rootBounds))
            {
                _worldSpawnSurfaces.Add(_worldCleaningSurfaceRoot);
                _worldSpawnSurfaceBounds.Add(rootBounds);
            }
        }

        private static bool TryGetWorldSurfaceBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
            {
                return false;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            bool hasBounds = false;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            return hasBounds;
        }

        private bool InitializeWorldStains()
        {
            if (_worldSpawnSurfaces.Count <= 0)
            {
                return false;
            }

            int stainsPerSurface = Mathf.Max(1, _worldStainsPerSurface);
            for (int surfaceIndex = 0; surfaceIndex < _worldSpawnSurfaces.Count; surfaceIndex++)
            {
                Transform spawnSurface = _worldSpawnSurfaces[surfaceIndex];
                Bounds spawnBounds = _worldSpawnSurfaceBounds[surfaceIndex];

                for (int stainIndex = 0; stainIndex < stainsPerSurface; stainIndex++)
                {
                    if (!TryGetDeterministicPipeSurfacePosition(
                            spawnSurface,
                            spawnBounds,
                            surfaceIndex,
                            stainIndex,
                            stainsPerSurface,
                            out Vector3 worldPosition))
                    {
                        continue;
                    }

                    GameObject stainObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    stainObject.name = $"WorldStain_{surfaceIndex}_{stainIndex}";
                    stainObject.transform.SetParent(spawnSurface, true);
                    stainObject.transform.position = worldPosition;
                    stainObject.transform.localScale = Vector3.one * _worldStainMarkerScale;

                    Collider stainCollider = stainObject.GetComponent<Collider>();
                    if (stainCollider != null)
                    {
                        Destroy(stainCollider);
                    }

                    Renderer stainRenderer = stainObject.GetComponent<Renderer>();
                    if (stainRenderer != null)
                    {
                        Material stainMaterial = stainRenderer.material;
                        if (stainMaterial != null)
                        {
                            stainMaterial.color = _worldStainDirtyColor;
                        }
                    }

                    _worldStains.Add(new WorldStainState
                    {
                        MarkerTransform = stainObject.transform,
                        MarkerRenderer = stainRenderer,
                        RequiredSwipes = FixedSwipesPerStain,
                        SwipeProgress = 0f,
                        IsCleaned = false
                    });
                }
            }

            return _worldStains.Count > 0;
        }

        private static bool TryGetDeterministicPipeSurfacePosition(
            Transform surfaceRoot,
            Bounds bounds,
            int surfaceIndex,
            int stainIndex,
            int stainsPerSurface,
            out Vector3 position)
        {
            position = bounds.center;
            if (surfaceRoot == null)
            {
                return false;
            }

            bool hasExplicitCleaningSurface = surfaceRoot.name.StartsWith("CleaningSurface", StringComparison.OrdinalIgnoreCase);
            float lane01 = stainsPerSurface <= 1 ? 0.5f : stainIndex / (float)(stainsPerSurface - 1);
            float laneSigned = Mathf.Lerp(-0.7f, 0.7f, lane01);
            float rowSigned = ((surfaceIndex % 3) - 1) * 0.28f;

            if (hasExplicitCleaningSurface)
            {
                Vector3 tangentA = surfaceRoot.right.normalized;
                Vector3 tangentB = surfaceRoot.forward.normalized;
                Vector3 normal = surfaceRoot.up.normalized;

                float halfA =
                    Mathf.Abs(tangentA.x) * bounds.extents.x +
                    Mathf.Abs(tangentA.y) * bounds.extents.y +
                    Mathf.Abs(tangentA.z) * bounds.extents.z;

                float halfB =
                    Mathf.Abs(tangentB.x) * bounds.extents.x +
                    Mathf.Abs(tangentB.y) * bounds.extents.y +
                    Mathf.Abs(tangentB.z) * bounds.extents.z;

                float normalExtent =
                    Mathf.Abs(normal.x) * bounds.extents.x +
                    Mathf.Abs(normal.y) * bounds.extents.y +
                    Mathf.Abs(normal.z) * bounds.extents.z;

                float outwardOffset = Mathf.Max(0.002f, normalExtent * 1.05f);
                position = bounds.center
                           + (tangentA * (halfA * laneSigned))
                           + (tangentB * (halfB * rowSigned))
                           + (normal * outwardOffset);
                return true;
            }

            Vector3 axis = surfaceRoot.right.normalized;
            Vector3 radialA = surfaceRoot.up.normalized;
            Vector3 radialB = surfaceRoot.forward.normalized;

            float halfLength =
                Mathf.Abs(axis.x) * bounds.extents.x +
                Mathf.Abs(axis.y) * bounds.extents.y +
                Mathf.Abs(axis.z) * bounds.extents.z;

            float radiusA =
                Mathf.Abs(radialA.x) * bounds.extents.x +
                Mathf.Abs(radialA.y) * bounds.extents.y +
                Mathf.Abs(radialA.z) * bounds.extents.z;

            float radiusB =
                Mathf.Abs(radialB.x) * bounds.extents.x +
                Mathf.Abs(radialB.y) * bounds.extents.y +
                Mathf.Abs(radialB.z) * bounds.extents.z;

            float radialRadius = Mathf.Max(0.02f, Mathf.Min(radiusA, radiusB));
            float along = halfLength * laneSigned;
            float angle = ((surfaceIndex * 97) + (stainIndex * 37)) % 360 * Mathf.Deg2Rad;
            Vector3 radialDirection = (radialA * Mathf.Cos(angle) + radialB * Mathf.Sin(angle)).normalized;

            position = bounds.center + (axis * along) + (radialDirection * (radialRadius * 1.02f));
            return true;
        }

        private void UpdateWorldCleaningInteraction()
        {
            Camera targetCamera = GetWorldTargetingCamera();
            if (targetCamera == null || _worldStains.Count == 0)
            {
                return;
            }

            Vector2 mousePosition = UnityEngine.Input.mousePosition;
            if (!_hasMousePosition)
            {
                _hasMousePosition = true;
                _previousMousePosition = mousePosition;
            }

            float movementPixels = Vector2.Distance(mousePosition, _previousMousePosition);
            _previousMousePosition = mousePosition;

            WorldStainState nearestStain = null;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < _worldStains.Count; i++)
            {
                WorldStainState stain = _worldStains[i];
                if (stain == null || stain.IsCleaned || stain.MarkerTransform == null)
                {
                    continue;
                }

                Vector3 screenPoint3 = targetCamera.WorldToScreenPoint(stain.MarkerTransform.position);
                if (screenPoint3.z <= 0f)
                {
                    continue;
                }

                float distance = Vector2.Distance(mousePosition, new Vector2(screenPoint3.x, screenPoint3.y));
                if (distance <= _worldStainScreenRadiusPixels && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestStain = stain;
                }
            }

            if (nearestStain != null && movementPixels >= _minMouseMovePixelsForCleaning)
            {
                float proximityFactor = 1f - Mathf.Clamp01(nearestDistance / Mathf.Max(1f, _worldStainScreenRadiusPixels));
                float swipeGain = movementPixels * _worldSwipeGainPerPixel * Mathf.Lerp(0.45f, 1.1f, proximityFactor);
                nearestStain.SwipeProgress += swipeGain;
                if (nearestStain.SwipeProgress >= nearestStain.RequiredSwipes)
                {
                    nearestStain.IsCleaned = true;
                    if (nearestStain.MarkerTransform != null)
                    {
                        nearestStain.MarkerTransform.gameObject.SetActive(false);
                    }
                }
            }

            _cleanedStains = 0;
            for (int i = 0; i < _worldStains.Count; i++)
            {
                WorldStainState stain = _worldStains[i];
                if (stain == null)
                {
                    continue;
                }

                if (stain.IsCleaned)
                {
                    _cleanedStains++;
                }

                UpdateWorldStainVisual(stain, stain == nearestStain);
            }
        }

        private void UpdateWorldStainVisual(WorldStainState stain, bool isHovered)
        {
            if (stain == null || stain.MarkerRenderer == null)
            {
                return;
            }

            Material material = stain.MarkerRenderer.material;
            if (material == null)
            {
                return;
            }

            if (stain.IsCleaned)
            {
                material.color = _worldStainCleanColor;
                return;
            }

            float cleanedRatio = stain.RequiredSwipes <= 0
                ? 0f
                : Mathf.Clamp01(stain.SwipeProgress / stain.RequiredSwipes);

            Color baseColor = Color.Lerp(_worldStainDirtyColor, _worldStainCleanColor, cleanedRatio * 0.45f);
            material.color = isHovered ? _worldStainHoverColor : baseColor;
        }

        private Camera GetWorldTargetingCamera()
        {
            if (_worldViewCamera != null && _worldViewCamera.enabled)
            {
                return _worldViewCamera;
            }

            return ResolveGameplayViewCamera();
        }

        private void CleanupWorldStains()
        {
            for (int i = 0; i < _worldStains.Count; i++)
            {
                WorldStainState stain = _worldStains[i];
                if (stain?.MarkerTransform != null)
                {
                    Destroy(stain.MarkerTransform.gameObject);
                }
            }

            _worldStains.Clear();
            _worldSpawnSurfaces.Clear();
            _worldSpawnSurfaceBounds.Clear();
            _totalStains = 0;
            _cleanedStains = 0;
        }

        private void RequestFinish(MinigameResult result)
        {
            if (result == MinigameResult.None || _isFinishing)
            {
                return;
            }

            _isFinishing = true;
            _pendingResult = result;

            if (_minigameCanvas != null)
            {
                _minigameCanvas.enabled = false;
            }

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

            if (_worldViewCamera == null || ResolveGameplayViewCamera() == null)
            {
                _isReturningToGameplayView = false;
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
            if (_worldViewCamera == null || ResolveGameplayViewCamera() == null)
            {
                _isReturningToGameplayView = false;
                SetResult(_pendingResult);
                return;
            }

            _isReturningToGameplayView = true;
            _returnTransitionElapsed = 0f;
            _returnTransitionStartPosition = _worldViewCamera.transform.position;
            _returnTransitionStartRotation = _worldViewCamera.transform.rotation;
        }

        private bool CanSmoothReturnToGameplayView()
        {
            return _isUsingWorldPresentation
                && _worldViewCamera != null
                && _worldViewCamera.enabled
                && ResolveGameplayViewCamera() != null
                && _cameraReturnDuration > 0f;
        }

        private void StartWorldViewPresentation()
        {
            if (_worldViewPose == null)
            {
                return;
            }

            ResolveGameplayViewCamera();
            if (!EnsureWorldViewCamera())
            {
                return;
            }

            if (_cameraTransitionRoutine != null)
            {
                StopCoroutine(_cameraTransitionRoutine);
                _cameraTransitionRoutine = null;
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
            if (_worldViewCamera == null || _worldViewPose == null)
            {
                yield break;
            }

            Transform gameplayTransform = ResolveGameplayViewCamera() != null
                ? _gameplayViewCamera.transform
                : null;

            Vector3 endPosition = GetWorldViewTargetPosition();
            Quaternion endRotation = GetWorldViewTargetRotation();
            Vector3 startPosition = gameplayTransform != null ? gameplayTransform.position : endPosition;
            Quaternion startRotation = gameplayTransform != null ? gameplayTransform.rotation : endRotation;

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

            FirstPersonCamera firstPersonCamera = FindAnyObjectByType<FirstPersonCamera>();
            if (firstPersonCamera != null)
            {
                _gameplayViewCamera = firstPersonCamera.GetComponent<Camera>();
                if (_gameplayViewCamera != null)
                {
                    return _gameplayViewCamera;
                }
            }

            _gameplayViewCamera = Camera.main;
            return _gameplayViewCamera;
        }

        private bool EnsureWorldViewCamera()
        {
            if (_worldViewPose == null)
            {
                return false;
            }

            if (_worldViewCamera == null)
            {
                GameObject cameraObject = new GameObject("[CleaningWorldViewCamera]", typeof(Camera));
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

            UniversalAdditionalCameraData worldViewCameraData = _worldViewCamera.GetComponent<UniversalAdditionalCameraData>();
            if (worldViewCameraData == null)
            {
                worldViewCameraData = _worldViewCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            }

            worldViewCameraData.renderType = CameraRenderType.Base;
            worldViewCameraData.cameraStack.Clear();

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
            if (_worldViewPose == null)
            {
                return Vector3.zero;
            }

            return _worldViewPose.TransformPoint(_worldCameraLocalOffset);
        }

        private Vector3 GetWorldViewLookPoint(Vector3 cameraPosition)
        {
            Transform lookRoot = _worldViewLookTarget != null ? _worldViewLookTarget : _worldViewPose;
            if (lookRoot == null)
            {
                return cameraPosition + Vector3.down;
            }

            return lookRoot.TransformPoint(_worldLookTargetLocalOffset);
        }

        private Quaternion GetWorldViewTargetRotation()
        {
            if (_worldViewPose == null)
            {
                return Quaternion.identity;
            }

            Vector3 cameraPosition = GetWorldViewTargetPosition();
            Vector3 lookPoint = GetWorldViewLookPoint(cameraPosition);
            Vector3 toTarget = lookPoint - cameraPosition;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                return _worldViewPose.rotation;
            }

            Quaternion lookRotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
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

            _isReturningToGameplayView = false;
            _returnTransitionElapsed = 0f;
            _isUsingWorldPresentation = false;
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

        private void UpdateTimer()
        {
            _remainingTimeSeconds = Mathf.Max(0f, _remainingTimeSeconds - Time.deltaTime);
            UpdateTimerUI();

            if (_remainingTimeSeconds > 0f || _hasProcessedTimeoutFailure)
            {
                return;
            }

            _hasProcessedTimeoutFailure = true;
            ApplyTimeoutCurrencyPenalty();
            RequestFinish(MinigameResult.Fail);
        }

        private void ApplyTimeoutCurrencyPenalty()
        {
            if (_timeoutCurrencyPenalty <= 0)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                gameManager.ModifyCurrency(-_timeoutCurrencyPenalty);
            }
            else
            {
                Debug.LogWarning("[CleaningMinigame] Timeout penalty skipped because GameManager is unavailable.");
            }
        }

        private void ConfigureAssignedTimerUI()
        {
            if (_timerText == null || _minigameCanvas == null)
            {
                return;
            }

            RectTransform timerRect = _timerText.rectTransform;
            if (timerRect == null)
            {
                return;
            }

            timerRect.SetParent(_minigameCanvas.transform, false);

            timerRect.anchorMin = new Vector2(0.5f, 1f);
            timerRect.anchorMax = new Vector2(0.5f, 1f);
            timerRect.pivot = new Vector2(0.5f, 1f);
            timerRect.anchoredPosition = new Vector2(0f, -16f);
            timerRect.sizeDelta = new Vector2(300f, 52f);

            _timerText.alignment = TextAlignmentOptions.Center;
            _timerText.fontSize = 34f;
            _timerText.textWrappingMode = TextWrappingModes.NoWrap;
            _timerText.raycastTarget = false;
            _timerText.color = Color.white;
            _timerText.gameObject.SetActive(true);
        }

        private void ConfigureAssignedProgressUI()
        {
            if (_progressText == null || _minigameCanvas == null)
            {
                return;
            }

            RectTransform progressRect = _progressText.rectTransform;
            if (progressRect == null)
            {
                return;
            }

            progressRect.SetParent(_minigameCanvas.transform, false);

            progressRect.anchorMin = new Vector2(0.5f, 1f);
            progressRect.anchorMax = new Vector2(0.5f, 1f);
            progressRect.pivot = new Vector2(0.5f, 1f);
            progressRect.anchoredPosition = new Vector2(0f, -72f);
            progressRect.sizeDelta = new Vector2(360f, 44f);

            _progressText.alignment = TextAlignmentOptions.Center;
            _progressText.fontSize = 28f;
            _progressText.textWrappingMode = TextWrappingModes.NoWrap;
            _progressText.raycastTarget = false;
            _progressText.color = Color.white;
            _progressText.gameObject.SetActive(true);
        }

        private void UpdateTimerUI()
        {
            if (_timerText == null)
            {
                return;
            }

            int secondsLeft = Mathf.CeilToInt(_remainingTimeSeconds);
            _timerText.text = $"Time: {secondsLeft}s";
            _timerText.color = _remainingTimeSeconds <= 5f ? Color.red : Color.white;
        }

        private void UpdateProgressUI()
        {
            if (_progressText == null)
            {
                return;
            }

            if (_totalStains <= 0)
            {
                _progressText.text = "Cleaned: 0/0";
                _progressText.color = Color.white;
                return;
            }

            _progressText.text = $"Cleaned: {_cleanedStains}/{_totalStains}";
            float progress01 = Mathf.Clamp01(_cleanedStains / (float)_totalStains);
            _progressText.color = Color.Lerp(Color.white, new Color(0.25f, 1f, 0.45f, 1f), progress01);
        }

        private void LockCursor()
        {
            _previousCursorLockMode = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void UnlockCursor()
        {
            Cursor.lockState = _previousCursorLockMode;
            Cursor.visible = _previousCursorVisible;
        }
    }
}
