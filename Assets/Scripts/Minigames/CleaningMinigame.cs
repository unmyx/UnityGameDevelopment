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
            public OilStainView StainView;
            public int RequiredSwipes;
            public float SwipeProgress;
            public bool IsCleaned;
        }

        private const int FixedSwipesPerStain = 6;
        private const int DefaultStainsPerSurface = 2;
        private const int DefaultSpawnAttemptsPerStain = 14;
        private const float MinTopSurfaceDot = 0.65f;
        private const float StainSurfaceOffset = 0.006f;
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
        [SerializeField] private float _worldStainMarkerScale = 0.14f;
        [SerializeField] private Color _worldStainDirtyColor = new Color(0.46f, 0.27f, 0.12f, 0.97f);
        [SerializeField] private Color _worldStainHoverColor = new Color(0.94f, 0.67f, 0.29f, 1f);
        [SerializeField] private Color _worldStainCleanColor = new Color(0.51f, 0.30f, 0.16f, 0.1f);
        [SerializeField] [Min(1)] private int _worldStainsPerSurface = DefaultStainsPerSurface;
        [SerializeField] [Min(0.01f)] private float _worldStainMinSpacing = 0.14f;
        [SerializeField] [Range(0.1f, 0.95f)] private float _worldStainMaxSurfaceCoverage = 0.78f;
        [SerializeField] [Range(0.05f, 0.9f)] private float _worldStainMinSurfaceCoverage = 0.30f;
        [SerializeField] [Min(0f)] private float _worldStainEdgePadding = 0.01f;

        private float _timeLimitSeconds = DefaultTimeLimitSeconds;
        private float _remainingTimeSeconds = DefaultTimeLimitSeconds;
        private int _timeoutCurrencyPenalty = DefaultTimeoutCurrencyPenalty;
        private int _fixedSwipesPerStain = FixedSwipesPerStain;
        private int _requiredStainsMin = -1;
        private int _requiredStainsMax = -1;
        private int? _worldSpawnSeed;
        private string _cleaningToolLabel = "Water";
        private float _cleaningToolEffectivenessMultiplier = 1f;

        private Canvas _minigameCanvas;
        private readonly List<WorldStainState> _worldStains = new List<WorldStainState>(24);
        private readonly List<Transform> _worldSpawnSurfaces = new List<Transform>(8);
        private readonly List<Bounds> _worldSpawnSurfaceBounds = new List<Bounds>(8);
        private readonly List<Collider[]> _worldSpawnSurfaceColliders = new List<Collider[]>(8);
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
        private TextMeshProUGUI _toolText;
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
            _worldSpawnSurfaceColliders.Clear();
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
            ConfigureAssignedToolUI();
            UpdateTimerUI();
            UpdateProgressUI();
            UpdateToolUI();
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
            
            if (_toolText != null)
            {
                _toolText.text = string.Empty;
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
            
            TextMeshProUGUI toolTextParameter = GetParameter<TextMeshProUGUI>("tool_text");
            if (toolTextParameter != null)
            {
                _toolText = toolTextParameter;
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
            _worldStainMinSpacing = Mathf.Max(0.01f, GetParameterFloat("world_stain_min_spacing") ?? _worldStainMinSpacing);
            _worldStainMaxSurfaceCoverage = Mathf.Clamp(
                GetParameterFloat("world_stain_max_surface_coverage") ?? _worldStainMaxSurfaceCoverage,
                0.1f,
                0.95f);
            _worldStainMinSurfaceCoverage = Mathf.Clamp(
                GetParameterFloat("world_stain_min_surface_coverage") ?? _worldStainMinSurfaceCoverage,
                0.05f,
                _worldStainMaxSurfaceCoverage);
            _worldStainEdgePadding = Mathf.Max(0f, GetParameterFloat("world_stain_edge_padding") ?? _worldStainEdgePadding);
            _worldStainsPerSurface = Mathf.Max(1, GetParameterInt("world_stains_per_surface") ?? _worldStainsPerSurface);
            _freezePlayerMovementInWorldView = GetParameterBool("world_freeze_player") ?? _freezePlayerMovementInWorldView;
            _fixedSwipesPerStain = Mathf.Max(1, GetParameterInt("world_fixed_swipes_per_stain") ?? _fixedSwipesPerStain);
            _requiredStainsMin = GetParameterInt("required_stains_min") ?? _requiredStainsMin;
            _requiredStainsMax = GetParameterInt("required_stains_max") ?? _requiredStainsMax;
            _worldSpawnSeed = GetParameterInt("world_spawn_seed");
            _cleaningToolLabel = GetParameter<string>("cleaning_tool_label") ?? _cleaningToolLabel;
            _cleaningToolEffectivenessMultiplier =
                Mathf.Max(0.01f, GetParameterFloat("cleaning_tool_effectiveness_multiplier") ?? _cleaningToolEffectivenessMultiplier);

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
                $"FixedSwipesPerStain={_fixedSwipesPerStain}, " +
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
            _worldSpawnSurfaceColliders.Clear();

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

                if (!TryGetWorldSurfaceGeometry(candidate, out Bounds candidateBounds, out Collider[] candidateColliders))
                {
                    continue;
                }

                _worldSpawnSurfaces.Add(candidate);
                _worldSpawnSurfaceBounds.Add(candidateBounds);
                _worldSpawnSurfaceColliders.Add(candidateColliders);
            }

            if (_worldSpawnSurfaces.Count > 0)
            {
                return;
            }

            if (_worldCleaningSurfaceRoot.gameObject.activeInHierarchy &&
                TryGetWorldSurfaceGeometry(_worldCleaningSurfaceRoot, out Bounds rootBounds, out Collider[] rootColliders))
            {
                _worldSpawnSurfaces.Add(_worldCleaningSurfaceRoot);
                _worldSpawnSurfaceBounds.Add(rootBounds);
                _worldSpawnSurfaceColliders.Add(rootColliders);
            }
        }

        private static bool TryGetWorldSurfaceGeometry(Transform root, out Bounds bounds, out Collider[] activeColliders)
        {
            bounds = default;
            activeColliders = null;
            if (root == null)
            {
                return false;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            List<Collider> filteredColliders = new List<Collider>(colliders.Length);
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

                filteredColliders.Add(collider);
            }

            if (!hasBounds || filteredColliders.Count <= 0)
            {
                return false;
            }

            activeColliders = filteredColliders.ToArray();
            return hasBounds;
        }

        private bool InitializeWorldStains()
        {
            if (_worldSpawnSurfaces.Count <= 0 || _worldSpawnSurfaceColliders.Count <= 0)
            {
                return false;
            }

            int targetStainCount = ResolveTargetStainCount();
            int spawnSeed = _worldSpawnSeed ?? Environment.TickCount;
            System.Random rng = new System.Random(spawnSeed);
            Transform stainParent = ResolveRuntimeStainParent();
            List<Vector3> placedPositions = new List<Vector3>(Mathf.Max(4, targetStainCount));
            float minSpacing = Mathf.Max(_worldStainMarkerScale * 1.2f, _worldStainMinSpacing);
            int maxAttempts = Mathf.Max(targetStainCount * DefaultSpawnAttemptsPerStain, DefaultSpawnAttemptsPerStain);
            int attempts = 0;

            while (_worldStains.Count < targetStainCount && attempts < maxAttempts)
            {
                attempts++;
                int surfaceIndex = rng.Next(0, _worldSpawnSurfaces.Count);
                Transform spawnSurface = _worldSpawnSurfaces[surfaceIndex];
                Bounds spawnBounds = _worldSpawnSurfaceBounds[surfaceIndex];
                Collider[] surfaceColliders = _worldSpawnSurfaceColliders[surfaceIndex];

                if (!TryGetRandomPipeSurfacePosition(
                        spawnBounds,
                        surfaceColliders,
                        rng,
                        placedPositions,
                        minSpacing,
                        out Vector3 worldPosition,
                        out Vector3 surfaceNormal))
                {
                    continue;
                }

                Vector3 spawnPosition = worldPosition + (surfaceNormal * StainSurfaceOffset);
                placedPositions.Add(spawnPosition);

                GameObject stainObject = new GameObject($"WorldStain_{surfaceIndex}_{_worldStains.Count}");
                stainObject.transform.SetParent(stainParent, true);
                stainObject.transform.position = spawnPosition;
                stainObject.transform.rotation = GetSurfaceAlignedRotation(surfaceNormal, (float)rng.NextDouble() * 360f);

                float footprintScaleBoost = Mathf.Lerp(1.65f, 1.95f, (float)rng.NextDouble());
                float baseScale = _worldStainMarkerScale * footprintScaleBoost * Mathf.Lerp(0.9f, 1.15f, (float)rng.NextDouble());
                float majorScale = baseScale * Mathf.Lerp(0.95f, 1.15f, (float)rng.NextDouble());
                float minorScale = baseScale * Mathf.Lerp(0.8f, 0.98f, (float)rng.NextDouble());
                float secondaryScale = baseScale * Mathf.Lerp(0.55f, 0.8f, (float)rng.NextDouble());
                float secondaryMinorScale = secondaryScale * Mathf.Lerp(0.9f, 1.1f, (float)rng.NextDouble());

                float surfacePlanarMinAxis = GetPlanarMinAxisByNormal(surfaceNormal, spawnBounds.size);
                float usableAxis = Mathf.Max(0.01f, surfacePlanarMinAxis - (_worldStainEdgePadding * 2f));
                float maxCoverage = Mathf.Clamp(_worldStainMaxSurfaceCoverage, 0.1f, 0.95f);
                float minCoverage = Mathf.Clamp(_worldStainMinSurfaceCoverage, 0.05f, maxCoverage);
                float maxFootprint = usableAxis * maxCoverage;
                float minFootprint = usableAxis * minCoverage;

                majorScale = Mathf.Clamp(majorScale, minFootprint * 0.5f, maxFootprint * 0.5f);
                minorScale = Mathf.Clamp(minorScale, minFootprint * 0.45f, maxFootprint * 0.5f);
                float secondaryMajor = Mathf.Clamp(secondaryScale, minFootprint * 0.3f, majorScale * 0.85f);
                float secondaryMinor = Mathf.Clamp(secondaryMinorScale, minFootprint * 0.3f, minorScale * 0.85f);

                float stainDiameter = Mathf.Clamp(
                    Mathf.Max(majorScale, secondaryMajor, secondaryMinor) * 2f,
                    minFootprint * 0.65f,
                    maxFootprint * 0.9f);

                GameObject sphereObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                sphereObject.name = "StainVisibleCylinder";
                sphereObject.transform.SetParent(stainObject.transform, false);
                sphereObject.transform.localPosition = Vector3.zero;
                // Cylinder height is local Y; rotate so Y aligns with parent forward (surface normal).
                sphereObject.transform.localRotation = Quaternion.FromToRotation(Vector3.up, Vector3.forward);
                float stainThickness = Mathf.Max(0.01f, stainDiameter * 0.06f);
                sphereObject.transform.localScale = new Vector3(stainDiameter, stainThickness, stainDiameter);

                Collider sphereCollider = sphereObject.GetComponent<Collider>();
                if (sphereCollider != null)
                {
                    Destroy(sphereCollider);
                }

                Renderer primaryRenderer = sphereObject.GetComponent<Renderer>();
                if (primaryRenderer != null)
                {
                    Material stainMaterial = primaryRenderer.material;
                    if (stainMaterial != null)
                    {
                        Color visibleOilColor = _worldStainDirtyColor;
                        visibleOilColor.a = 1f;
                        stainMaterial.color = visibleOilColor;
                    }
                }

                _worldStains.Add(new WorldStainState
                {
                    MarkerTransform = stainObject.transform,
                    MarkerRenderer = primaryRenderer,
                    StainView = null,
                    RequiredSwipes = Mathf.Max(1, _fixedSwipesPerStain),
                    SwipeProgress = 0f,
                    IsCleaned = false
                });
            }

            return _worldStains.Count > 0;
        }

        private static float GetPlanarMinAxisByNormal(Vector3 surfaceNormal, Vector3 boundsSize)
        {
            Vector3 n = new Vector3(Mathf.Abs(surfaceNormal.x), Mathf.Abs(surfaceNormal.y), Mathf.Abs(surfaceNormal.z));
            float x = Mathf.Max(0.0001f, boundsSize.x);
            float y = Mathf.Max(0.0001f, boundsSize.y);
            float z = Mathf.Max(0.0001f, boundsSize.z);

            // Ignore the dominant normal axis (surface thickness direction), use only planar axes.
            if (n.x >= n.y && n.x >= n.z)
            {
                return Mathf.Min(y, z);
            }

            if (n.y >= n.z)
            {
                return Mathf.Min(x, z);
            }

            return Mathf.Min(x, y);
        }

        private Transform ResolveRuntimeStainParent()
        {
            if (IsNeutralScaleTransform(_worldCleaningSurfaceRoot))
            {
                return _worldCleaningSurfaceRoot;
            }

            if (IsNeutralScaleTransform(transform))
            {
                return transform;
            }

            return null;
        }

        private static bool IsNeutralScaleTransform(Transform candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            Vector3 s = candidate.lossyScale;
            return Mathf.Abs(s.x - 1f) <= 0.01f
                && Mathf.Abs(s.y - 1f) <= 0.01f
                && Mathf.Abs(s.z - 1f) <= 0.01f;
        }

        private int ResolveTargetStainCount()
        {
            int fallbackCount = Mathf.Max(1, _worldSpawnSurfaces.Count * Mathf.Max(1, _worldStainsPerSurface));

            int minCount = _requiredStainsMin > 0 ? _requiredStainsMin : fallbackCount;
            int maxCount = _requiredStainsMax > 0 ? _requiredStainsMax : minCount;
            if (maxCount < minCount)
            {
                int swap = minCount;
                minCount = maxCount;
                maxCount = swap;
            }

            minCount = Mathf.Max(1, minCount);
            maxCount = Mathf.Max(minCount, maxCount);
            if (minCount == maxCount)
            {
                return minCount;
            }

            return UnityEngine.Random.Range(minCount, maxCount + 1);
        }

        private static bool TryGetRandomPipeSurfacePosition(
            Bounds bounds,
            Collider[] surfaceColliders,
            System.Random rng,
            List<Vector3> existingPositions,
            float minDistance,
            out Vector3 position,
            out Vector3 normal)
        {
            position = bounds.center;
            normal = Vector3.up;
            if (surfaceColliders == null || surfaceColliders.Length <= 0 || rng == null)
            {
                return false;
            }

            Vector3 rayDirection = Vector3.down;
            float rayStartMargin = Mathf.Max(0.25f, bounds.size.y + 0.25f);
            float rayMaxDistance = Mathf.Max(1f, bounds.size.y + 1.5f);
            int retries = 8;
            for (int attempt = 0; attempt < retries; attempt++)
            {
                Collider selectedCollider = surfaceColliders[rng.Next(0, surfaceColliders.Length)];
                if (selectedCollider == null)
                {
                    continue;
                }

                Vector3 randomPoint = new Vector3(
                    Mathf.Lerp(bounds.min.x, bounds.max.x, (float)rng.NextDouble()),
                    Mathf.Lerp(bounds.min.y, bounds.max.y, (float)rng.NextDouble()),
                    Mathf.Lerp(bounds.min.z, bounds.max.z, (float)rng.NextDouble()));

                Vector3 rayOrigin = new Vector3(
                    randomPoint.x,
                    bounds.max.y + rayStartMargin,
                    randomPoint.z);

                Ray ray = new Ray(rayOrigin, rayDirection);
                if (!selectedCollider.Raycast(ray, out RaycastHit hit, rayMaxDistance))
                {
                    continue;
                }

                Vector3 candidate = hit.point;
                if (HasNearbyPlacement(existingPositions, candidate, minDistance))
                {
                    continue;
                }

                Vector3 candidateNormal = hit.normal.sqrMagnitude > 0.0001f
                    ? hit.normal.normalized
                    : EstimateSurfaceNormal(selectedCollider, candidate);
                if (Vector3.Dot(candidateNormal, Vector3.up) < MinTopSurfaceDot)
                {
                    continue;
                }

                position = candidate;
                normal = candidateNormal;
                return true;
            }

            return false;
        }

        private static Vector3 EstimateSurfaceNormal(Collider collider, Vector3 point)
        {
            if (collider == null)
            {
                return Vector3.up;
            }

            if (collider is BoxCollider boxCollider)
            {
                Vector3 localPoint = boxCollider.transform.InverseTransformPoint(point) - boxCollider.center;
                Vector3 extents = boxCollider.size * 0.5f;
                if (extents.x > 0f && extents.y > 0f && extents.z > 0f)
                {
                    float nx = Mathf.Abs(localPoint.x) / extents.x;
                    float ny = Mathf.Abs(localPoint.y) / extents.y;
                    float nz = Mathf.Abs(localPoint.z) / extents.z;

                    Vector3 localNormal;
                    if (ny >= nx && ny >= nz)
                    {
                        localNormal = new Vector3(0f, Mathf.Sign(localPoint.y), 0f);
                    }
                    else if (nx >= nz)
                    {
                        localNormal = new Vector3(Mathf.Sign(localPoint.x), 0f, 0f);
                    }
                    else
                    {
                        localNormal = new Vector3(0f, 0f, Mathf.Sign(localPoint.z));
                    }

                    return boxCollider.transform.TransformDirection(localNormal).normalized;
                }
            }

            Vector3 fallback = (point - collider.bounds.center).normalized;
            return fallback.sqrMagnitude > 0.0001f ? fallback : Vector3.up;
        }

        private static Quaternion GetSurfaceAlignedRotation(Vector3 surfaceNormal, float yawDegrees)
        {
            Vector3 normalizedNormal = surfaceNormal.sqrMagnitude > 0.0001f ? surfaceNormal.normalized : Vector3.up;
            Quaternion align = Quaternion.FromToRotation(Vector3.forward, normalizedNormal);
            return align * Quaternion.AngleAxis(yawDegrees, Vector3.forward);
        }

        private static Renderer CreatePuddleLayer(
            Transform parent,
            string layerName,
            float width,
            float height,
            float localYaw,
            float normalOffset)
        {
            GameObject layer = GameObject.CreatePrimitive(PrimitiveType.Quad);
            layer.name = layerName;
            layer.transform.SetParent(parent, false);
            layer.transform.localPosition = new Vector3(0f, 0f, normalOffset);
            layer.transform.localRotation = Quaternion.Euler(0f, 0f, localYaw);
            layer.transform.localScale = new Vector3(Mathf.Max(0.01f, width), Mathf.Max(0.01f, height), 1f);

            Collider layerCollider = layer.GetComponent<Collider>();
            if (layerCollider != null)
            {
                Destroy(layerCollider);
            }

            return layer.GetComponent<Renderer>();
        }

        private static bool HasNearbyPlacement(List<Vector3> existingPositions, Vector3 candidate, float minDistance)
        {
            if (existingPositions == null || existingPositions.Count <= 0)
            {
                return false;
            }

            float minDistanceSqr = Mathf.Max(0.0001f, minDistance * minDistance);
            for (int i = 0; i < existingPositions.Count; i++)
            {
                if ((existingPositions[i] - candidate).sqrMagnitude < minDistanceSqr)
                {
                    return true;
                }
            }

            return false;
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
                    _cleanedStains++;
                    if (nearestStain.StainView != null)
                    {
                        nearestStain.StainView.PlayCompleteAndHide();
                    }
                    else if (nearestStain.MarkerTransform != null)
                    {
                        nearestStain.MarkerTransform.gameObject.SetActive(false);
                    }
                }
            }

            for (int i = 0; i < _worldStains.Count; i++)
            {
                WorldStainState stain = _worldStains[i];
                if (stain == null)
                {
                    continue;
                }

                UpdateWorldStainVisual(stain, stain == nearestStain);
            }
        }

        private void UpdateWorldStainVisual(WorldStainState stain, bool isHovered)
        {
            if (stain?.StainView != null)
            {
                float progress01 = stain.RequiredSwipes <= 0
                    ? 1f
                    : Mathf.Clamp01(stain.SwipeProgress / stain.RequiredSwipes);
                stain.StainView.SetProgress01(progress01, isHovered && !stain.IsCleaned);
                return;
            }

            if (stain == null || stain.MarkerRenderer == null || stain.IsCleaned)
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
            _worldSpawnSurfaceColliders.Clear();
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

            timerRect.anchorMin = new Vector2(1f, 1f);
            timerRect.anchorMax = new Vector2(1f, 1f);
            timerRect.pivot = new Vector2(1f, 1f);
            timerRect.anchoredPosition = new Vector2(-24f, -24f);
            timerRect.sizeDelta = new Vector2(240f, 52f);
            timerRect.localScale = Vector3.one;

            _timerText.alignment = TextAlignmentOptions.Right;
            _timerText.fontSize = 34f;
            _timerText.textWrappingMode = TextWrappingModes.NoWrap;
            _timerText.raycastTarget = false;
            _timerText.color = Color.white;
            _timerText.gameObject.SetActive(true);
            timerRect.SetAsLastSibling();
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

            progressRect.anchorMin = new Vector2(1f, 1f);
            progressRect.anchorMax = new Vector2(1f, 1f);
            progressRect.pivot = new Vector2(1f, 1f);
            progressRect.anchoredPosition = new Vector2(-24f, -78f);
            progressRect.sizeDelta = new Vector2(280f, 44f);
            progressRect.localScale = Vector3.one;

            _progressText.alignment = TextAlignmentOptions.Right;
            _progressText.fontSize = 28f;
            _progressText.textWrappingMode = TextWrappingModes.NoWrap;
            _progressText.raycastTarget = false;
            _progressText.color = Color.white;
            _progressText.gameObject.SetActive(true);
            progressRect.SetAsLastSibling();
        }

        private void ConfigureAssignedToolUI()
        {
            if (_toolText == null || _minigameCanvas == null)
            {
                return;
            }

            RectTransform toolRect = _toolText.rectTransform;
            if (toolRect == null)
            {
                return;
            }

            toolRect.SetParent(_minigameCanvas.transform, false);
            toolRect.anchorMin = new Vector2(1f, 1f);
            toolRect.anchorMax = new Vector2(1f, 1f);
            toolRect.pivot = new Vector2(1f, 1f);
            toolRect.anchoredPosition = new Vector2(-24f, -120f);
            toolRect.sizeDelta = new Vector2(320f, 36f);
            toolRect.localScale = Vector3.one;

            _toolText.alignment = TextAlignmentOptions.Right;
            _toolText.fontSize = 22f;
            _toolText.textWrappingMode = TextWrappingModes.NoWrap;
            _toolText.raycastTarget = false;
            _toolText.color = new Color(0.95f, 0.8f, 0.55f, 1f);
            _toolText.gameObject.SetActive(true);
            toolRect.SetAsLastSibling();
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
            _progressText.color = Color.Lerp(Color.white, new Color(1f, 0.85f, 0.55f, 1f), progress01);
        }

        private void UpdateToolUI()
        {
            if (_toolText == null)
            {
                return;
            }

            _toolText.text = $"Tool: {_cleaningToolLabel} (x{_cleaningToolEffectivenessMultiplier:0.00})";
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
