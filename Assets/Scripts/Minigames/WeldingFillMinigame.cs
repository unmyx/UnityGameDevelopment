using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.Player;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Minigames
{
    public class WeldingFillMinigame : BaseMinigame
    {
        private const float RequiredCoverage01 = 1f;
        private const float DefaultTimeLimitSeconds = 30f;
        private const int DefaultTimeoutCurrencyPenalty = 10;

        private sealed class WeldAnchorState
        {
            public Transform Anchor;
            public float Coverage01;
            public bool IsComplete;
            public bool[] CoveredSeamSamples;
            public float LastPaintSeamT;
            public float StationaryDuration;
            public Transform MarkerVisualRoot;
            public Renderer MarkerSeamRenderer;
            public Renderer[] MarkerWeldedSampleRenderers;
            public Renderer MarkerHotspotRenderer;
            public Transform MarkerHotspotTransform;
        }

        private readonly WeldInputHandler _inputHandler = new WeldInputHandler();
        private readonly List<WeldAnchorState> _activeWorldAnchors = new List<WeldAnchorState>(8);
        private readonly List<Transform> _candidateWorldAnchors = new List<Transform>(16);

        [Header("Canvas References")]
        [SerializeField] private Canvas _minigameCanvas;
        [SerializeField] private TMP_Text _coverageText;
        [Tooltip("Assign the Fill image (child of Overall bar Background), not the background image.")]
        [SerializeField] private Image _coverageFillImage;
        [SerializeField] private TMP_Text _timerText;

        [Header("Cursor")]
        [SerializeField] private bool _unlockCursorDuringMinigame = true;

        [Header("World Station View")]
        [SerializeField] private bool _useWorldStationView;
        [SerializeField] private Transform _worldViewPose;
        [SerializeField] private Transform _worldViewLookTarget;
        [SerializeField] private float _cameraTransitionDuration = 0.45f;
        [SerializeField] private float _cameraReturnDuration = 0.3f;
        [SerializeField] private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.35f, -0.8f);
        [SerializeField] private Vector3 _worldLookTargetLocalOffset = Vector3.zero;
        [SerializeField] private float _worldTopDownAngleBias = 18f;
        [SerializeField] private float _worldCameraFovOverride = 50f;

        [Header("World Weld Targets")]
        [SerializeField] private Transform _worldWeldAnchorsRoot;
        [SerializeField] private int _worldActiveWeldCount = 3;
        [SerializeField] private float _worldAnchorWeldRate = 1f;
        [SerializeField] private float _worldAnchorScreenRadiusPixels = 60f;
        [SerializeField] private bool _showWorldAnchorMarkers = true;
        [SerializeField] private Color _worldAnchorPendingColor = new Color(1f, 0.55f, 0.1f, 0.95f);
        [SerializeField] private Color _worldAnchorActiveColor = new Color(1f, 0.9f, 0.45f, 0.98f);
        [SerializeField] private Color _worldAnchorCompleteColor = new Color(0.25f, 1f, 0.45f, 0.95f);
        [SerializeField] private float _worldSeamLength = 0.28f;
        [SerializeField] private float _worldSeamVisualWidth = 0.045f;
        [SerializeField] private int _worldSeamSampleCount = 14;

        [Header("World Trace Behavior")]
        [Tooltip("Minimum seam travel speed (normalized seam units per second) for efficient welding.")]
        [SerializeField] private float _worldMinEfficientTravelSpeed = 0.03f;
        [Tooltip("Travel speed beyond which welding starts losing efficiency and leaving gaps.")]
        [SerializeField] private float _worldMaxEfficientTravelSpeed = 0.4f;
        [Tooltip("At and above this speed, gap behavior reaches full strength.")]
        [SerializeField] private float _worldGapTravelSpeed = 0.8f;
        [Tooltip("Below this speed, holding in place is treated as stalling.")]
        [SerializeField] private float _worldStallTravelSpeed = 0.0035f;
        [SerializeField] private float _worldStallGraceSeconds = 0.28f;
        [SerializeField, Range(0f, 1f)] private float _worldStallEfficiency = 0f;
        [SerializeField] private float _worldFastGapSpacingMultiplier = 1.6f;
        [SerializeField, Range(0.012f, 0.2f)] private float _worldTraceRadius01 = 0.051f;

        private Camera _gameplayViewCamera;
        private Camera _worldViewCamera;
        private FirstPersonCamera _firstPersonCamera;
        private CursorLockMode _previousCursorLockMode;
        private bool _previousCursorVisible;
        private float _lastOverallCoverage = -1f;
        private float _timeLimitSeconds = DefaultTimeLimitSeconds;
        private float _remainingTimeSeconds = DefaultTimeLimitSeconds;
        private int _timeoutCurrencyPenalty = DefaultTimeoutCurrencyPenalty;
        private bool _hasProcessedTimeoutFailure;
        private bool _isSetupValid = true;
        private string _setupFailureReason = string.Empty;

        private bool _isFinishing;
        private bool _isReturningToGameplayView;
        private float _returnTransitionElapsed;
        private Vector3 _returnTransitionStartPosition;
        private Quaternion _returnTransitionStartRotation;
        private MinigameResult _pendingResult = MinigameResult.None;
        private Coroutine _cameraTransitionRoutine;

        protected override void OnInitialize()
        {
            LoadParameters();
            _isSetupValid = true;
            _setupFailureReason = string.Empty;

            if (_minigameCanvas == null)
            {
                FailSetup("Missing required WeldingCanvas reference.");
                return;
            }

            _minigameCanvas.enabled = false;
            if (!ValidateExplicitUiReferences())
            {
                return;
            }

            NormalizeAndStyleTimerText(_timerText, _minigameCanvas.transform);
            NormalizeCoverageTextRect(_coverageText, _minigameCanvas.transform);
            NormalizeCoverageBarRect(_coverageFillImage, _minigameCanvas.transform);
            _remainingTimeSeconds = _timeLimitSeconds;
            _hasProcessedTimeoutFailure = false;
            _lastOverallCoverage = -1f;
            ResolveGameplayViewCamera();

            if (_worldWeldAnchorsRoot == null)
            {
                FailSetup("Missing required WeldAnchors root.");
                return;
            }

            if (CountActiveAnchors(_worldWeldAnchorsRoot) == 0)
            {
                FailSetup("WeldAnchors root has zero active children.");
                return;
            }

            if (_useWorldStationView)
            {
                if (_worldViewPose == null)
                {
                    FailSetup("Missing MinigameCameraPose while world view is enabled.");
                    return;
                }

                if (_worldViewLookTarget == null)
                {
                    FailSetup("Missing MinigameLookTarget while world view is enabled.");
                    return;
                }
            }
        }

        protected override void OnStart()
        {
            if (!_isSetupValid)
            {
                Debug.LogError($"[WeldingFillMinigame] Minigame start blocked by invalid setup: {_setupFailureReason}", this);
                RequestFinish(MinigameResult.Fail);
                return;
            }

            if (_minigameCanvas != null)
            {
                _minigameCanvas.enabled = true;
            }

            if (_coverageText != null)
            {
                _coverageText.gameObject.SetActive(true);
            }

            if (_coverageFillImage != null)
            {
                _coverageFillImage.gameObject.SetActive(true);
            }

            if (_timerText != null)
            {
                _timerText.gameObject.SetActive(true);
            }

            NormalizeCoverageTextRect(_coverageText, _minigameCanvas != null ? _minigameCanvas.transform : null);
            NormalizeCoverageBarRect(_coverageFillImage, _minigameCanvas != null ? _minigameCanvas.transform : null);

            if (_unlockCursorDuringMinigame)
            {
                _previousCursorLockMode = Cursor.lockState;
                _previousCursorVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            _inputHandler.CachePaintAction();
            _remainingTimeSeconds = _timeLimitSeconds;
            _hasProcessedTimeoutFailure = false;
            _lastOverallCoverage = -1f;

            if (!InitializeWorldAnchorTargets())
            {
                RequestFinish(MinigameResult.Fail);
                return;
            }

            UpdateWorldAnchorCoverageUi(force: true);
            UpdateTimerUi();
            StartWorldViewPresentation();
        }

        protected override void OnUpdate()
        {
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null && !pauseManager.CanProcessInput())
            {
                return;
            }

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

            UpdateWorldAnchorTargets();
            if (AreAllWorldAnchorsComplete())
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

            _inputHandler.Clear();
            ClearWorldAnchorTargets();
            TearDownWorldViewPresentation();

            if (_minigameCanvas != null)
            {
                _minigameCanvas.enabled = false;
            }

            if (_unlockCursorDuringMinigame)
            {
                Cursor.lockState = _previousCursorLockMode;
                Cursor.visible = _previousCursorVisible;
            }

            if (_timerText != null)
            {
                _timerText.text = string.Empty;
            }

            _isFinishing = false;
            _isReturningToGameplayView = false;
            _pendingResult = MinigameResult.None;
            _setupFailureReason = string.Empty;
        }

        private void FailSetup(string reason)
        {
            if (!_isSetupValid)
            {
                return;
            }

            _isSetupValid = false;
            _setupFailureReason = reason;
            Debug.LogError($"[WeldingFillMinigame] {reason}", this);
        }

        private static int CountActiveAnchors(Transform root)
        {
            if (root == null)
            {
                return 0;
            }

            int activeCount = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child != null && child.gameObject.activeInHierarchy)
                {
                    activeCount++;
                }
            }

            return activeCount;
        }

        private bool InitializeWorldAnchorTargets()
        {
            ClearWorldAnchorTargets();
            if (_worldWeldAnchorsRoot == null)
            {
                Debug.LogError("[WeldingFillMinigame] World-anchor mode requires a WeldAnchors root.", this);
                return false;
            }

            CollectCandidateAnchors(_worldWeldAnchorsRoot, _candidateWorldAnchors);
            if (_candidateWorldAnchors.Count == 0)
            {
                Debug.LogError("[WeldingFillMinigame] World-anchor mode requires at least one active WeldAnchors child.", this);
                return false;
            }

            Shuffle(_candidateWorldAnchors);
            int activeCount = Mathf.Clamp(_worldActiveWeldCount, 1, _candidateWorldAnchors.Count);
            for (int i = 0; i < activeCount; i++)
            {
                Transform anchor = _candidateWorldAnchors[i];
                WeldAnchorState state = new WeldAnchorState
                {
                    Anchor = anchor,
                    Coverage01 = 0f,
                    IsComplete = false,
                    CoveredSeamSamples = new bool[Mathf.Max(24, _worldSeamSampleCount)],
                    LastPaintSeamT = -1f,
                    StationaryDuration = 0f,
                    MarkerVisualRoot = null,
                    MarkerSeamRenderer = null,
                    MarkerWeldedSampleRenderers = null,
                    MarkerHotspotRenderer = null,
                    MarkerHotspotTransform = null
                };

                if (_showWorldAnchorMarkers)
                {
                    CreateWorldAnchorMarker(state);
                    SetWorldAnchorMarkerColor(state, _worldAnchorPendingColor, _worldAnchorPendingColor);
                }

                _activeWorldAnchors.Add(state);
            }

            return _activeWorldAnchors.Count > 0;
        }

        private static void CollectCandidateAnchors(Transform root, List<Transform> destination)
        {
            destination.Clear();
            if (root == null)
            {
                return;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                destination.Add(child);
            }
        }

        private static void Shuffle<T>(List<T> list)
        {
            if (list == null)
            {
                return;
            }

            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private void CreateWorldAnchorMarker(WeldAnchorState state)
        {
            if (state == null || state.Anchor == null)
            {
                return;
            }

            GameObject visualRoot = new GameObject("ActiveWeldTargetVisual");
            visualRoot.transform.SetParent(state.Anchor, false);
            visualRoot.transform.localPosition = Vector3.zero;
            visualRoot.transform.localRotation = Quaternion.identity;
            visualRoot.transform.localScale = Vector3.one;

            GameObject seamBase = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seamBase.name = "SeamBase";
            seamBase.transform.SetParent(visualRoot.transform, false);
            seamBase.transform.localPosition = Vector3.zero;
            seamBase.transform.localRotation = Quaternion.identity;
            seamBase.transform.localScale = new Vector3(Mathf.Max(0.08f, _worldSeamLength), 0.006f, Mathf.Max(0.012f, _worldSeamVisualWidth));

            int seamSamples = Mathf.Max(24, _worldSeamSampleCount);
            Renderer[] weldedSampleRenderers = new Renderer[seamSamples];
            float halfLength = Mathf.Max(0.08f, _worldSeamLength) * 0.5f;
            for (int i = 0; i < seamSamples; i++)
            {
                float t = seamSamples > 1 ? i / (float)(seamSamples - 1) : 0f;
                float sampleOffset = Mathf.Lerp(-halfLength, halfLength, t);

                GameObject weldSample = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                weldSample.name = $"WeldSample_{i}";
                weldSample.transform.SetParent(visualRoot.transform, false);
                weldSample.transform.localRotation = Quaternion.identity;
                weldSample.transform.localPosition = new Vector3(sampleOffset, 0.006f, 0f);
                weldSample.transform.localScale = new Vector3(0.008f, 0.008f, 0.008f);
                weldSample.SetActive(false);

                Collider sampleCollider = weldSample.GetComponent<Collider>();
                if (sampleCollider != null)
                {
                    Destroy(sampleCollider);
                }

                weldedSampleRenderers[i] = weldSample.GetComponent<Renderer>();
            }

            GameObject seamHotspot = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seamHotspot.name = "SeamHotspot";
            seamHotspot.transform.SetParent(visualRoot.transform, false);
            seamHotspot.transform.localRotation = Quaternion.identity;
            seamHotspot.transform.localPosition = new Vector3(-halfLength, 0.009f, 0f);
            seamHotspot.transform.localScale = new Vector3(0.02f, 0.009f, Mathf.Max(0.012f, _worldSeamVisualWidth * 0.85f));

            Collider baseCollider = seamBase.GetComponent<Collider>();
            if (baseCollider != null)
            {
                Destroy(baseCollider);
            }

            Collider hotspotCollider = seamHotspot.GetComponent<Collider>();
            if (hotspotCollider != null)
            {
                Destroy(hotspotCollider);
            }

            state.MarkerVisualRoot = visualRoot.transform;
            state.MarkerSeamRenderer = seamBase.GetComponent<Renderer>();
            state.MarkerWeldedSampleRenderers = weldedSampleRenderers;
            state.MarkerHotspotRenderer = seamHotspot.GetComponent<Renderer>();
            state.MarkerHotspotTransform = seamHotspot.transform;
        }

        private void SetWorldAnchorMarkerColor(WeldAnchorState state, Color seamColor, Color sampleColor)
        {
            if (state == null)
            {
                return;
            }

            if (state.MarkerSeamRenderer != null)
            {
                Material seamMat = state.MarkerSeamRenderer.material;
                if (seamMat != null)
                {
                    seamMat.color = seamColor;
                }
            }

            Renderer[] sampleRenderers = state.MarkerWeldedSampleRenderers;
            if (sampleRenderers == null)
            {
                return;
            }

            for (int i = 0; i < sampleRenderers.Length; i++)
            {
                if (sampleRenderers[i] == null)
                {
                    continue;
                }

                Material fillMat = sampleRenderers[i].material;
                if (fillMat != null)
                {
                    fillMat.color = sampleColor;
                }
            }
        }

        private void UpdateWorldAnchorTargets()
        {
            WeldAnchorState targetedAnchor = null;
            float targetedSeamT = 0f;
            bool paintHeld = _inputHandler.IsPaintHeld();
            bool hasTarget = TryGetTargetedWorldAnchor(out targetedAnchor, out targetedSeamT);
            if (paintHeld && hasTarget)
            {
                PaintWorldAnchorSeam(targetedAnchor, targetedSeamT);
                if (!targetedAnchor.IsComplete && targetedAnchor.Coverage01 >= RequiredCoverage01)
                {
                    targetedAnchor.IsComplete = true;
                }
            }
            else
            {
                for (int i = 0; i < _activeWorldAnchors.Count; i++)
                {
                    WeldAnchorState state = _activeWorldAnchors[i];
                    if (state != null)
                    {
                        state.LastPaintSeamT = -1f;
                        state.StationaryDuration = 0f;
                    }
                }
            }

            UpdateWorldAnchorMarkerVisuals(hasTarget ? targetedAnchor : null, targetedSeamT, paintHeld);
            UpdateWorldAnchorCoverageUi(force: false);
        }

        private void PaintWorldAnchorSeam(WeldAnchorState anchor, float seamT)
        {
            if (anchor == null || anchor.CoveredSeamSamples == null || anchor.CoveredSeamSamples.Length == 0)
            {
                return;
            }

            float clampedT = Mathf.Clamp01(seamT);
            float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);
            float previousT = anchor.LastPaintSeamT >= 0f ? anchor.LastPaintSeamT : clampedT;
            float travelSpeed = Mathf.Abs(clampedT - previousT) / deltaTime;

            float stallSpeed = Mathf.Max(0.0001f, _worldStallTravelSpeed);
            float minEfficientSpeed = Mathf.Max(stallSpeed + 0.0001f, _worldMinEfficientTravelSpeed);
            float maxEfficientSpeed = Mathf.Max(minEfficientSpeed + 0.0001f, _worldMaxEfficientTravelSpeed);
            float gapSpeed = Mathf.Max(maxEfficientSpeed + 0.0001f, _worldGapTravelSpeed);

            if (travelSpeed <= stallSpeed)
            {
                anchor.StationaryDuration += deltaTime;
            }
            else
            {
                anchor.StationaryDuration = 0f;
            }

            float stallMultiplier = anchor.StationaryDuration > _worldStallGraceSeconds
                ? Mathf.Clamp01(_worldStallEfficiency)
                : 1f;

            float lowSpeedEfficiency = Mathf.InverseLerp(stallSpeed, minEfficientSpeed, travelSpeed);
            if (travelSpeed > stallSpeed)
            {
                lowSpeedEfficiency = Mathf.Max(0.28f, lowSpeedEfficiency);
            }

            float highSpeedEfficiency = Mathf.Lerp(1f, 0.42f, Mathf.InverseLerp(maxEfficientSpeed, gapSpeed, travelSpeed));
            float travelEfficiency = Mathf.Clamp01(Mathf.Min(lowSpeedEfficiency, highSpeedEfficiency));
            float effectiveEfficiency = Mathf.Clamp01(travelEfficiency * stallMultiplier);

            float baseRadiusT = GetWorldTraceRadius01();
            float effectiveRadiusT = Mathf.Lerp(baseRadiusT * 0.35f, baseRadiusT, effectiveEfficiency);

            if (effectiveEfficiency > 0.001f && effectiveRadiusT > 0.0005f)
            {
                float fastFactor = Mathf.InverseLerp(maxEfficientSpeed, gapSpeed, travelSpeed);
                float spacingMultiplier = Mathf.Lerp(0.6f, Mathf.Max(0.7f, _worldFastGapSpacingMultiplier), fastFactor);
                float stampSpacingT = Mathf.Max(0.001f, effectiveRadiusT * spacingMultiplier);
                StampSeamCoverage(anchor.CoveredSeamSamples, previousT, clampedT, effectiveRadiusT, stampSpacingT);
            }

            anchor.LastPaintSeamT = clampedT;
            UpdateAnchorCoverageFromSamples(anchor);
        }

        private void UpdateAnchorCoverageFromSamples(WeldAnchorState anchor)
        {
            if (anchor == null || anchor.CoveredSeamSamples == null || anchor.CoveredSeamSamples.Length == 0)
            {
                return;
            }

            int sampleCount = anchor.CoveredSeamSamples.Length;
            int coveredCount = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                if (anchor.CoveredSeamSamples[i])
                {
                    coveredCount++;
                }
            }

            float targetCoverage = coveredCount / (float)sampleCount;
            anchor.Coverage01 = Mathf.MoveTowards(anchor.Coverage01, targetCoverage, _worldAnchorWeldRate * Time.deltaTime);
        }

        private static void StampSeamCoverage(bool[] coveredSamples, float startT, float endT, float radiusT, float spacingT)
        {
            if (coveredSamples == null || coveredSamples.Length == 0)
            {
                return;
            }

            float clampedStart = Mathf.Clamp01(startT);
            float clampedEnd = Mathf.Clamp01(endT);
            float travelDistance = Mathf.Abs(clampedEnd - clampedStart);
            int stampCount = Mathf.Max(1, Mathf.CeilToInt(travelDistance / Mathf.Max(0.0001f, spacingT)));

            for (int stampIndex = 0; stampIndex <= stampCount; stampIndex++)
            {
                float u = stampCount > 0 ? stampIndex / (float)stampCount : 0f;
                float stampCenterT = Mathf.Lerp(clampedStart, clampedEnd, u);
                MarkSamplesWithinRadius(coveredSamples, stampCenterT, radiusT);
            }
        }

        private static void MarkSamplesWithinRadius(bool[] coveredSamples, float centerT, float radiusT)
        {
            if (coveredSamples == null || coveredSamples.Length == 0)
            {
                return;
            }

            int sampleCount = coveredSamples.Length;
            for (int i = 0; i < sampleCount; i++)
            {
                float sampleT = sampleCount > 1 ? i / (float)(sampleCount - 1) : 0f;
                if (Mathf.Abs(sampleT - centerT) <= radiusT)
                {
                    coveredSamples[i] = true;
                }
            }
        }

        private float GetWorldTraceRadius01()
        {
            return Mathf.Clamp(_worldTraceRadius01, 0.012f, 0.2f);
        }

        private void UpdateWorldAnchorMarkerVisuals(WeldAnchorState targetedAnchor, float targetedSeamT, bool paintHeld)
        {
            for (int i = 0; i < _activeWorldAnchors.Count; i++)
            {
                WeldAnchorState anchor = _activeWorldAnchors[i];
                if (anchor == null || anchor.MarkerVisualRoot == null)
                {
                    continue;
                }

                anchor.MarkerVisualRoot.localScale = Vector3.one;

                bool isTargeted = anchor == targetedAnchor;
                Color seamColor = new Color(0.2f, 0.2f, 0.2f, 0.9f);
                if (anchor.IsComplete)
                {
                    seamColor = Color.Lerp(_worldAnchorCompleteColor, Color.white, 0.15f);
                }

                SetWorldAnchorMarkerColor(anchor, seamColor, _worldAnchorCompleteColor);

                Renderer[] sampleRenderers = anchor.MarkerWeldedSampleRenderers;
                bool[] coveredSamples = anchor.CoveredSeamSamples;
                if (sampleRenderers != null)
                {
                    for (int sampleIndex = 0; sampleIndex < sampleRenderers.Length; sampleIndex++)
                    {
                        Renderer sampleRenderer = sampleRenderers[sampleIndex];
                        if (sampleRenderer == null)
                        {
                            continue;
                        }

                        bool covered = coveredSamples != null && sampleIndex < coveredSamples.Length && coveredSamples[sampleIndex];
                        sampleRenderer.gameObject.SetActive(covered);
                        if (!covered)
                        {
                            continue;
                        }

                        Material sampleMaterial = sampleRenderer.material;
                        if (sampleMaterial != null)
                        {
                            sampleMaterial.color = anchor.IsComplete
                                ? Color.Lerp(_worldAnchorCompleteColor, Color.white, 0.3f)
                                : Color.Lerp(_worldAnchorCompleteColor, Color.white, 0.12f);
                        }
                    }
                }

                if (anchor.MarkerHotspotTransform != null)
                {
                    bool showContact = isTargeted && !anchor.IsComplete;
                    anchor.MarkerHotspotTransform.gameObject.SetActive(showContact);
                    if (showContact)
                    {
                        float halfLength = Mathf.Max(0.08f, _worldSeamLength) * 0.5f;
                        float traceRadius = GetWorldTraceRadius01();
                        anchor.MarkerHotspotTransform.localPosition = new Vector3(Mathf.Lerp(-halfLength, halfLength, Mathf.Clamp01(targetedSeamT)), 0.009f, 0f);
                        anchor.MarkerHotspotTransform.localScale = new Vector3(
                            Mathf.Clamp(_worldSeamLength * traceRadius * 2f, 0.012f, _worldSeamLength * 0.6f),
                            0.009f,
                            Mathf.Max(0.012f, _worldSeamVisualWidth * 0.85f));
                    }
                }

                if (anchor.IsComplete)
                {
                    if (anchor.MarkerHotspotTransform != null)
                    {
                        anchor.MarkerHotspotTransform.gameObject.SetActive(false);
                    }

                    continue;
                }

                if (anchor.MarkerHotspotRenderer != null && anchor.MarkerHotspotTransform != null && anchor.MarkerHotspotTransform.gameObject.activeSelf)
                {
                    Material hotspotMaterial = anchor.MarkerHotspotRenderer.material;
                    if (hotspotMaterial != null)
                    {
                        hotspotMaterial.color = paintHeld
                            ? Color.Lerp(_worldAnchorActiveColor, Color.white, 0.35f)
                            : Color.Lerp(_worldAnchorActiveColor, Color.white, 0.1f);
                    }
                }
            }
        }

        private bool TryGetTargetedWorldAnchor(out WeldAnchorState targetedAnchor, out float seamT)
        {
            targetedAnchor = null;
            seamT = 0f;

            Camera targetingCamera = ResolveTargetingCamera();
            if (targetingCamera == null)
            {
                return false;
            }

            Vector2 mousePosition = UnityEngine.Input.mousePosition;
            float bestDistance = float.MaxValue;
            float traceRadiusT = GetWorldTraceRadius01();
            for (int i = 0; i < _activeWorldAnchors.Count; i++)
            {
                WeldAnchorState candidate = _activeWorldAnchors[i];
                if (candidate == null || candidate.IsComplete || candidate.Anchor == null)
                {
                    continue;
                }

                if (!TryGetAnchorScreenSeam(candidate, targetingCamera, out Vector2 seamStart, out Vector2 seamEnd, out float seamHalfWidthPixels))
                {
                    continue;
                }

                float distance = DistanceToSegment(mousePosition, seamStart, seamEnd, out float candidateT);
                float seamLengthPixels = Vector2.Distance(seamStart, seamEnd);
                float traceRadiusPixels = Mathf.Max(2f, seamLengthPixels * traceRadiusT);
                float hitRadiusPixels = Mathf.Clamp(
                    (seamHalfWidthPixels * 0.75f) + traceRadiusPixels,
                    8f,
                    Mathf.Max(12f, _worldAnchorScreenRadiusPixels));

                if (distance > hitRadiusPixels || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                seamT = candidateT;
                targetedAnchor = candidate;
            }

            return targetedAnchor != null;
        }

        private bool TryGetAnchorScreenSeam(WeldAnchorState anchor, Camera targetingCamera, out Vector2 seamStart, out Vector2 seamEnd, out float seamHalfWidthPixels)
        {
            seamStart = Vector2.zero;
            seamEnd = Vector2.zero;
            seamHalfWidthPixels = 0f;

            if (anchor == null || anchor.Anchor == null || targetingCamera == null)
            {
                return false;
            }

            Vector3 seamDirection = anchor.Anchor.right;
            if (seamDirection.sqrMagnitude <= 0.0001f)
            {
                seamDirection = anchor.Anchor.forward;
            }

            float halfLength = Mathf.Max(0.08f, _worldSeamLength) * 0.5f;
            Vector3 worldStart = anchor.Anchor.position - (seamDirection * halfLength);
            Vector3 worldEnd = anchor.Anchor.position + (seamDirection * halfLength);

            Vector3 startScreen3 = targetingCamera.WorldToScreenPoint(worldStart);
            Vector3 endScreen3 = targetingCamera.WorldToScreenPoint(worldEnd);
            if (startScreen3.z <= 0f || endScreen3.z <= 0f)
            {
                return false;
            }

            seamStart = new Vector2(startScreen3.x, startScreen3.y);
            seamEnd = new Vector2(endScreen3.x, endScreen3.y);

            float halfVisualWidth = Mathf.Max(0.006f, _worldSeamVisualWidth * 0.5f);
            Vector3 seamWidthWorld = anchor.Anchor.forward * halfVisualWidth;
            Vector3 centerScreen3 = targetingCamera.WorldToScreenPoint(anchor.Anchor.position);
            Vector3 widthScreen3 = targetingCamera.WorldToScreenPoint(anchor.Anchor.position + seamWidthWorld);
            if (centerScreen3.z > 0f && widthScreen3.z > 0f)
            {
                seamHalfWidthPixels = Vector2.Distance(
                    new Vector2(centerScreen3.x, centerScreen3.y),
                    new Vector2(widthScreen3.x, widthScreen3.y));
            }

            seamHalfWidthPixels = Mathf.Max(4f, seamHalfWidthPixels);
            return true;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 segmentStart, Vector2 segmentEnd, out float t)
        {
            Vector2 segment = segmentEnd - segmentStart;
            float segmentLengthSquared = segment.sqrMagnitude;
            if (segmentLengthSquared <= 0.0001f)
            {
                t = 0f;
                return Vector2.Distance(point, segmentStart);
            }

            t = Mathf.Clamp01(Vector2.Dot(point - segmentStart, segment) / segmentLengthSquared);
            Vector2 closestPoint = segmentStart + (segment * t);
            return Vector2.Distance(point, closestPoint);
        }

        private Camera ResolveTargetingCamera()
        {
            if (_worldViewCamera != null && _worldViewCamera.enabled)
            {
                return _worldViewCamera;
            }

            return ResolveGameplayViewCamera();
        }

        private void UpdateWorldAnchorCoverageUi(bool force)
        {
            if (_activeWorldAnchors.Count == 0)
            {
                return;
            }

            float overallCoverage = 0f;
            int completeCount = 0;
            for (int i = 0; i < _activeWorldAnchors.Count; i++)
            {
                WeldAnchorState anchor = _activeWorldAnchors[i];
                if (anchor == null)
                {
                    continue;
                }

                overallCoverage += anchor.Coverage01;
                if (anchor.IsComplete)
                {
                    completeCount++;
                }
            }

            overallCoverage /= _activeWorldAnchors.Count;
            if (!force && Mathf.Abs(overallCoverage - _lastOverallCoverage) < 0.0001f)
            {
                return;
            }

            _lastOverallCoverage = overallCoverage;
            if (_coverageText != null)
            {
                _coverageText.text = $"Welds: {completeCount}/{_activeWorldAnchors.Count}";
            }

            if (_coverageFillImage != null)
            {
                _coverageFillImage.fillAmount = Mathf.Clamp01(overallCoverage);
                _coverageFillImage.color = Color.Lerp(
                    new Color(1f, 0.15f, 0.1f, 1f),
                    new Color(0.2f, 1f, 0.35f, 1f),
                    Mathf.Clamp01(overallCoverage));
            }
        }

        private bool AreAllWorldAnchorsComplete()
        {
            if (_activeWorldAnchors.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < _activeWorldAnchors.Count; i++)
            {
                WeldAnchorState anchor = _activeWorldAnchors[i];
                if (anchor == null || !anchor.IsComplete)
                {
                    return false;
                }
            }

            return true;
        }

        private void ClearWorldAnchorTargets()
        {
            for (int i = 0; i < _activeWorldAnchors.Count; i++)
            {
                WeldAnchorState anchor = _activeWorldAnchors[i];
                if (anchor?.MarkerVisualRoot != null)
                {
                    Destroy(anchor.MarkerVisualRoot.gameObject);
                }
            }

            _activeWorldAnchors.Clear();
            _candidateWorldAnchors.Clear();
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
            return _useWorldStationView
                && _worldViewCamera != null
                && _worldViewCamera.enabled
                && ResolveGameplayViewCamera() != null
                && _cameraReturnDuration > 0f;
        }

        private void StartWorldViewPresentation()
        {
            if (!_useWorldStationView || _worldViewPose == null || _worldViewLookTarget == null)
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
                return;
            }

            _cameraTransitionRoutine = StartCoroutine(SmoothTransitionToWorldView());
        }

        private IEnumerator SmoothTransitionToWorldView()
        {
            if (_worldViewCamera == null || _worldViewPose == null || _worldViewLookTarget == null)
            {
                yield break;
            }

            Transform gameplayTransform = ResolveGameplayViewCamera() != null
                ? _gameplayViewCamera.transform
                : null;

            Vector3 endPosition = GetWorldViewTargetPosition();
            Vector3 startPosition = gameplayTransform != null ? gameplayTransform.position : endPosition;
            Quaternion startRotation = gameplayTransform != null ? gameplayTransform.rotation : GetWorldViewTargetRotation();
            Quaternion endRotation = GetWorldViewTargetRotation();

            _worldViewCamera.transform.SetPositionAndRotation(startPosition, startRotation);
            _worldViewCamera.enabled = true;

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

            if (_firstPersonCamera == null)
            {
                _firstPersonCamera = FindAnyObjectByType<FirstPersonCamera>();
            }

            if (_firstPersonCamera != null)
            {
                _gameplayViewCamera = _firstPersonCamera.GetComponent<Camera>();
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
            if (_worldViewPose == null || _worldViewLookTarget == null)
            {
                return false;
            }

            if (_worldViewCamera == null)
            {
                GameObject cameraObject = new GameObject("[WeldingWorldViewCamera]", typeof(Camera));
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
            if (_worldViewLookTarget == null)
            {
                return cameraPosition + Vector3.down;
            }

            return _worldViewLookTarget.TransformPoint(_worldLookTargetLocalOffset);
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
            if (Mathf.Abs(_worldTopDownAngleBias) <= 0.001f)
            {
                return lookRotation;
            }

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
            if (_worldViewCamera != null)
            {
                _worldViewCamera.enabled = false;
                Destroy(_worldViewCamera.gameObject);
                _worldViewCamera = null;
            }

            _isReturningToGameplayView = false;
            _returnTransitionElapsed = 0f;
        }

        private void LoadParameters()
        {
            if (_minigameData == null)
            {
                return;
            }

            Canvas canvas = GetParameter<Canvas>("canvas");
            if (canvas != null)
            {
                _minigameCanvas = canvas;
            }

            TMP_Text coverageText = GetParameter<TMP_Text>("coverage_text");
            if (coverageText != null)
            {
                _coverageText = coverageText;
            }

            Image coverageFillImage = GetParameter<Image>("coverage_fill_image");
            if (coverageFillImage != null)
            {
                _coverageFillImage = coverageFillImage;
            }

            TMP_Text timerText = GetParameter<TMP_Text>("timer_text");
            if (timerText != null)
            {
                _timerText = timerText;
            }

            float configuredTimeLimit = GetFloatParameter(
                "timeLimit",
                GetFloatParameter(
                    "time_limit",
                    _minigameData.timeLimit > 0f ? _minigameData.timeLimit : DefaultTimeLimitSeconds));
            _timeLimitSeconds = configuredTimeLimit > 0f ? configuredTimeLimit : DefaultTimeLimitSeconds;
            _timeoutCurrencyPenalty = Mathf.Max(0, GetIntParameter("timeout_currency_penalty", DefaultTimeoutCurrencyPenalty));

            _useWorldStationView = GetBoolParameter("world_view_enabled", _useWorldStationView);
            _worldViewPose = GetParameter<Transform>("world_camera_pose") ?? _worldViewPose;
            _worldViewLookTarget = GetParameter<Transform>("world_look_target") ?? _worldViewLookTarget;
            _cameraTransitionDuration = Mathf.Max(0f, GetFloatParameter("world_camera_transition", _cameraTransitionDuration));
            _cameraReturnDuration = Mathf.Max(0f, GetFloatParameter("world_camera_return", _cameraReturnDuration));
            _worldCameraLocalOffset = GetVector3Parameter("world_camera_local_offset", _worldCameraLocalOffset);
            _worldLookTargetLocalOffset = GetVector3Parameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            _worldTopDownAngleBias = Mathf.Clamp(GetFloatParameter("world_top_down_angle_bias", _worldTopDownAngleBias), -45f, 60f);
            _worldCameraFovOverride = Mathf.Clamp(GetFloatParameter("world_camera_fov", _worldCameraFovOverride), 0f, 120f);

            _worldWeldAnchorsRoot = GetParameter<Transform>("world_weld_anchors_root") ?? _worldWeldAnchorsRoot;
            _worldActiveWeldCount = Mathf.Clamp(GetIntParameter("world_active_weld_count", _worldActiveWeldCount), 1, 12);
            _worldAnchorWeldRate = Mathf.Max(0.1f, GetFloatParameter("world_anchor_weld_rate", _worldAnchorWeldRate));
            _worldAnchorScreenRadiusPixels = Mathf.Max(12f, GetFloatParameter("world_anchor_screen_radius", _worldAnchorScreenRadiusPixels));
            _showWorldAnchorMarkers = GetBoolParameter("world_anchor_markers", _showWorldAnchorMarkers);
            _worldTraceRadius01 = Mathf.Clamp(GetFloatParameter("world_trace_radius", _worldTraceRadius01), 0.012f, 0.2f);

            _minigameData.timeLimit = 0f;
        }

        private bool ValidateExplicitUiReferences()
        {
            if (_minigameCanvas == null)
            {
                FailSetup("Missing required WeldingCanvas reference.");
                return false;
            }

            Transform canvasTransform = _minigameCanvas.transform;
            if (_coverageFillImage == null || !(_coverageFillImage.transform != null && _coverageFillImage.transform.IsChildOf(canvasTransform)))
            {
                FailSetup("Missing required welding coverage Fill image reference on WeldingCanvas.");
                return false;
            }

            if (_coverageFillImage.type != Image.Type.Filled || _coverageFillImage.fillMethod != Image.FillMethod.Horizontal)
            {
                FailSetup("Coverage Fill image must use Image.Type=Filled and FillMethod=Horizontal.");
                return false;
            }

            if (!IsValidTimerTextOnCanvas(_timerText, canvasTransform))
            {
                FailSetup("Missing required welding TimerText reference on WeldingCanvas.");
                return false;
            }

            return true;
        }

        private bool GetBoolParameter(string key, bool defaultValue)
        {
            if (!TryGetParameterValue(key, out object value))
            {
                return defaultValue;
            }

            if (value is bool boolValue) return boolValue;
            if (value is int intValue) return intValue != 0;
            if (value is float floatValue) return !Mathf.Approximately(floatValue, 0f);
            if (value is string stringValue)
            {
                if (bool.TryParse(stringValue, out bool parsedBool)) return parsedBool;
                if (int.TryParse(stringValue, out int parsedInt)) return parsedInt != 0;
            }

            return defaultValue;
        }

        private Vector3 GetVector3Parameter(string key, Vector3 defaultValue)
        {
            if (!TryGetParameterValue(key, out object value))
            {
                return defaultValue;
            }

            if (value is Vector3 vectorValue)
            {
                return vectorValue;
            }

            return defaultValue;
        }

        private int GetIntParameter(string key, int defaultValue)
        {
            if (!TryGetParameterValue(key, out object value))
            {
                return defaultValue;
            }

            if (value is int intValue) return intValue;
            if (value is float floatValue) return Mathf.RoundToInt(floatValue);
            if (value is long longValue) return (int)longValue;
            if (value is double doubleValue) return Mathf.RoundToInt((float)doubleValue);
            if (value is string stringValue && int.TryParse(stringValue, out int parsedInt)) return parsedInt;
            return defaultValue;
        }

        private float GetFloatParameter(string key, float defaultValue)
        {
            if (!TryGetParameterValue(key, out object value))
            {
                return defaultValue;
            }

            if (value is float floatValue) return floatValue;
            if (value is int intValue) return intValue;
            if (value is long longValue) return longValue;
            if (value is double doubleValue) return (float)doubleValue;
            if (value is string stringValue && float.TryParse(stringValue, out float parsedFloat)) return parsedFloat;
            return defaultValue;
        }

        private bool TryGetParameterValue(string key, out object value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(key) || _minigameData == null || _minigameData.parameters == null)
            {
                return false;
            }

            return _minigameData.parameters.TryGetValue(key, out value);
        }

        private static bool IsValidTimerTextOnCanvas(TMP_Text timerText, Transform canvasTransform)
        {
            if (timerText == null || canvasTransform == null)
            {
                return false;
            }

            if (timerText is not TextMeshProUGUI)
            {
                return false;
            }

            return timerText.transform.IsChildOf(canvasTransform);
        }

        private void NormalizeAndStyleTimerText(TMP_Text timerText, Transform canvasTransform)
        {
            if (timerText == null || canvasTransform == null)
            {
                return;
            }

            if (timerText is TextMeshProUGUI timerUiText)
            {
                if (timerUiText.font == null && TMP_Settings.instance != null && TMP_Settings.defaultFontAsset != null)
                {
                    timerUiText.font = TMP_Settings.defaultFontAsset;
                }

                timerUiText.alignment = TextAlignmentOptions.Right;
                timerUiText.fontSize = Mathf.Max(30f, timerUiText.fontSize);
                timerUiText.textWrappingMode = TextWrappingModes.NoWrap;
                timerUiText.color = Color.white;
            }

            timerText.raycastTarget = false;

            RectTransform timerRect = timerText.rectTransform;
            if (timerRect != null)
            {
                timerRect.SetParent(canvasTransform, false);
            }

            NormalizeTimerRect(timerRect);
        }

        private void NormalizeTimerRect(RectTransform timerRect)
        {
            if (timerRect == null)
            {
                return;
            }

            timerRect.anchorMin = new Vector2(1f, 1f);
            timerRect.anchorMax = new Vector2(1f, 1f);
            timerRect.pivot = new Vector2(1f, 1f);
            timerRect.anchoredPosition = new Vector2(-24f, -24f);
            timerRect.sizeDelta = new Vector2(240f, 52f);
            timerRect.localScale = Vector3.one;
            timerRect.gameObject.SetActive(true);
            timerRect.SetAsLastSibling();
        }

        private void NormalizeCoverageTextRect(TMP_Text coverageText, Transform canvasTransform)
        {
            if (coverageText == null || canvasTransform == null)
            {
                return;
            }

            if (coverageText is TextMeshProUGUI coverageUiText)
            {
                if (coverageUiText.font == null && TMP_Settings.instance != null && TMP_Settings.defaultFontAsset != null)
                {
                    coverageUiText.font = TMP_Settings.defaultFontAsset;
                }

                coverageUiText.alignment = TextAlignmentOptions.Right;
                coverageUiText.fontSize = Mathf.Max(26f, coverageUiText.fontSize);
                coverageUiText.textWrappingMode = TextWrappingModes.NoWrap;
                coverageUiText.color = Color.white;
            }

            coverageText.raycastTarget = false;

            RectTransform coverageRect = coverageText.rectTransform;
            if (coverageRect == null)
            {
                return;
            }

            coverageRect.SetParent(canvasTransform, false);
            coverageRect.anchorMin = new Vector2(1f, 1f);
            coverageRect.anchorMax = new Vector2(1f, 1f);
            coverageRect.pivot = new Vector2(1f, 1f);
            coverageRect.anchoredPosition = new Vector2(-24f, -82f);
            coverageRect.sizeDelta = new Vector2(240f, 40f);
            coverageRect.localScale = Vector3.one;
            coverageRect.gameObject.SetActive(true);
            coverageRect.SetAsLastSibling();
        }

        private void NormalizeCoverageBarRect(Image coverageFillImage, Transform canvasTransform)
        {
            if (coverageFillImage == null || canvasTransform == null || coverageFillImage.rectTransform == null)
            {
                return;
            }

            RectTransform fillRect = coverageFillImage.rectTransform;
            RectTransform backgroundRect = fillRect.parent as RectTransform;
            RectTransform progressRect = backgroundRect != null ? backgroundRect.parent as RectTransform : null;

            RectTransform targetRect = progressRect != null ? progressRect : backgroundRect;
            if (targetRect == null)
            {
                return;
            }

            targetRect.SetParent(canvasTransform, false);
            targetRect.anchorMin = new Vector2(0.5f, 1f);
            targetRect.anchorMax = new Vector2(0.5f, 1f);
            targetRect.pivot = new Vector2(0.5f, 1f);
            targetRect.anchoredPosition = new Vector2(0f, -24f);
            targetRect.sizeDelta = new Vector2(520f, 24f);
            targetRect.localScale = Vector3.one;
        }

        private void UpdateTimer()
        {
            _remainingTimeSeconds = Mathf.Max(0f, _remainingTimeSeconds - Time.deltaTime);
            UpdateTimerUi();

            if (_remainingTimeSeconds > 0f || _hasProcessedTimeoutFailure)
            {
                return;
            }

            _hasProcessedTimeoutFailure = true;
            ApplyTimeoutCurrencyPenalty();
            RequestFinish(MinigameResult.Fail);
        }

        private void UpdateTimerUi()
        {
            if (_timerText == null)
            {
                return;
            }

            _timerText.gameObject.SetActive(true);

            int secondsLeft = Mathf.CeilToInt(_remainingTimeSeconds);
            _timerText.text = $"Time: {secondsLeft}s";
            _timerText.color = _remainingTimeSeconds <= 5f ? Color.red : Color.white;
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
                Debug.LogWarning("[WeldingFillMinigame] Timeout penalty skipped because GameManager is unavailable.");
            }
        }
    }
}
