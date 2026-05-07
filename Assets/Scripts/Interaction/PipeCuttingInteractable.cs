using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using Game.Minigames;
using Game.Networking;
using Game.Player;
using TMPro;
using Unity.Netcode;
using UnityEngine;

namespace Game.Interaction
{
    public sealed class PipeCuttingInteractable : MinigameInteractableBase
    {
        private const string DailyTaskType = "measure_cut";
        private const string MinigameId = "measure_cut";

        [SerializeField] private Canvas _measureCutCanvas;

        [Header("Measure/Cut HUD")]
        [SerializeField] private TMP_Text _timerText;
        [SerializeField] private TMP_Text _qualityText;
        [SerializeField] private TMP_Text _instructionText;
        [SerializeField] private TMP_Text _cutProgressText;

        [Header("World View Camera")]
        [SerializeField] private Transform _stationCameraPose;
        [SerializeField] private Transform _stationCameraLookTarget;
        [SerializeField] private Transform _cutTargetsRoot;
        [SerializeField] private Transform _cutterVisual;
        [SerializeField] private float _cameraTransitionDuration = 0.45f;
        [SerializeField] private float _cameraReturnDuration = 0.3f;
        [SerializeField] private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);
        [SerializeField] private Vector3 _worldLookTargetLocalOffset = Vector3.zero;
        [SerializeField] private float _worldTopDownAngleBias = 16f;
        [SerializeField] private float _worldCameraFovOverride = 50f;
        [SerializeField] private bool _freezePlayerMovementInWorldView = true;

        [Header("Guide Visuals")]
        [SerializeField] private Transform _cutGuideRoot;
        [SerializeField] private Renderer _guideLine01;
        [SerializeField] private Renderer _guideLine02;
        [SerializeField] private Renderer _guideLine03;
        [SerializeField] private Renderer _toleranceBand01;
        [SerializeField] private Renderer _toleranceBand02;
        [SerializeField] private Renderer _toleranceBand03;

        [Header("Pipe Split Visuals")]
        [SerializeField] private GameObject _fullPipeVisual;
        [SerializeField] private Transform _leftSegment;
        [SerializeField] private Transform _rightSegment;
        [SerializeField] private float _splitSeparationDistance = 0.08f;

        [Header("Optional FX Hooks")]
        [SerializeField] private ParticleSystem _cuttingLoopFx;
        [SerializeField] private ParticleSystem _cutCompleteFx;
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _cuttingLoopClip;
        [SerializeField] private AudioClip _cutCompleteClip;

        [Header("Gameplay Tuning")]
        [SerializeField] private float _baseTimeLimitSeconds = 30f;
        [SerializeField] private float _baseLineToleranceWorld = 0.08f;
        [SerializeField] private float _requiredQualityThreshold = 35f;

        private bool _awaitingNetworkStartApproval;
        private string _pendingNetworkStartTaskKey = string.Empty;
        private bool _hasActiveSession;
        private int _activeSessionToken;
        private string _activeTaskKey = string.Empty;
        private bool _hasSubmittedResult;

        private void OnEnable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse += OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnMeasureCutResultResolutionResponse += OnMeasureCutResultResolutionResponse;
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void OnDisable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse -= OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnMeasureCutResultResolutionResponse -= OnMeasureCutResultResolutionResponse;
            EventBus.Unsubscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Unsubscribe<MinigameCancelledEvent>(OnMinigameCancelled);
            _awaitingNetworkStartApproval = false;
            _pendingNetworkStartTaskKey = string.Empty;
            ClearActiveSession();
        }

        protected override MinigameData BuildMinigameData()
        {
            if (!TryResolveRequiredReferences(out string validationError))
            {
                Debug.LogError($"[PipeCuttingInteractable] Blocking measure_cut start: {validationError}", this);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = MinigameId,
                displayName = "Measure & Cut",
                timeLimit = 0f
            };

            float dayMultiplier = 1f;
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                dayMultiplier = Mathf.Max(0.01f, gameManager.GetMeasureCutDayDifficultyMultiplier());
            }

            data.SetParameter("canvas", _measureCutCanvas);
            data.SetParameter("timer_text", _timerText);
            data.SetParameter("quality_text", _qualityText);
            data.SetParameter("instruction_text", _instructionText);
            data.SetParameter("cut_progress_text", _cutProgressText);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_cut_targets_root", _cutTargetsRoot);
            data.SetParameter("world_cutter_visual", _cutterVisual);
            data.SetParameter("world_cut_guide_root", _cutGuideRoot);
            data.SetParameter("world_guide_line_01", _guideLine01);
            data.SetParameter("world_guide_line_02", _guideLine02);
            data.SetParameter("world_guide_line_03", _guideLine03);
            data.SetParameter("world_tolerance_band_01", _toleranceBand01);
            data.SetParameter("world_tolerance_band_02", _toleranceBand02);
            data.SetParameter("world_tolerance_band_03", _toleranceBand03);
            data.SetParameter("world_full_pipe_visual", _fullPipeVisual);
            data.SetParameter("world_left_segment", _leftSegment);
            data.SetParameter("world_right_segment", _rightSegment);
            data.SetParameter("world_split_separation_distance", Mathf.Max(0f, _splitSeparationDistance));
            data.SetParameter("world_cutting_loop_fx", _cuttingLoopFx);
            data.SetParameter("world_cut_complete_fx", _cutCompleteFx);
            data.SetParameter("world_audio_source", _audioSource);
            data.SetParameter("world_cutting_loop_clip", _cuttingLoopClip);
            data.SetParameter("world_cut_complete_clip", _cutCompleteClip);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("world_freeze_player", _freezePlayerMovementInWorldView);
            data.SetParameter("line_tolerance_world", _baseLineToleranceWorld * dayMultiplier);
            data.SetParameter("required_quality_threshold", _requiredQualityThreshold);
            data.SetParameter("time_limit", Mathf.Max(5f, _baseTimeLimitSeconds - (5f * (1f - dayMultiplier))));

            return data;
        }

        protected override void StartMinigame(MinigameData data)
        {
            string taskKey = GetDailyTaskLocationKey();

            if (IsNetworkSession())
            {
                if (_awaitingNetworkStartApproval)
                {
                    return;
                }

                if (!NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority authority))
                {
                    EventBus.Publish(new PlayerFeedbackEvent("Task is unavailable right now."));
                    return;
                }

                _awaitingNetworkStartApproval = true;
                _pendingNetworkStartTaskKey = taskKey;
                authority.RequestJobInteractableStart(DailyTaskType, taskKey, MinigameId);
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager != null
                && !gameManager.CanLaunchTaskAtLocation(DailyTaskType, taskKey, out string blockedReason))
            {
                if (!string.IsNullOrWhiteSpace(blockedReason))
                {
                    EventBus.Publish(new PlayerFeedbackEvent(blockedReason));
                }

                return;
            }

            gameManager?.RegisterDailyTaskLaunchContext(DailyTaskType, taskKey);
            StartLocalMeasureCutMinigame(data);
        }

        private void OnJobInteractableStartResponse(NetworkSessionProgressAuthority.JobInteractableStartResponse response)
        {
            if (!_awaitingNetworkStartApproval)
            {
                return;
            }

            if (!string.Equals(response.taskType, DailyTaskType, StringComparison.Ordinal)
                || !string.Equals(response.minigameId, MinigameId, StringComparison.Ordinal)
                || !string.Equals(response.taskKey, _pendingNetworkStartTaskKey, StringComparison.Ordinal))
            {
                return;
            }

            string requestedTaskKey = _pendingNetworkStartTaskKey;
            _awaitingNetworkStartApproval = false;
            _pendingNetworkStartTaskKey = string.Empty;

            if (!response.approved)
            {
                if (!string.IsNullOrWhiteSpace(response.reason))
                {
                    EventBus.Publish(new PlayerFeedbackEvent(response.reason));
                }

                ClearActiveSession();
                return;
            }

            int sessionToken = response.measureCutSessionToken;
            string canonicalTaskKey = string.IsNullOrWhiteSpace(response.canonicalTaskKey)
                ? requestedTaskKey
                : response.canonicalTaskKey.Trim();
            if (IsNetworkSession())
            {
                if (sessionToken <= 0 || string.IsNullOrWhiteSpace(canonicalTaskKey))
                {
                    EventBus.Publish(new PlayerFeedbackEvent("Task start approval was invalid."));
                    ClearActiveSession();
                    return;
                }

                _hasActiveSession = true;
                _activeSessionToken = sessionToken;
                _activeTaskKey = canonicalTaskKey;
                _hasSubmittedResult = false;
            }

            MinigameData approvedData = BuildMinigameData();
            if (approvedData == null || string.IsNullOrWhiteSpace(approvedData.minigameId))
            {
                TryAbortActiveSessionStartFailure();
                return;
            }

            StartLocalMeasureCutMinigame(approvedData);
        }

        private void StartLocalMeasureCutMinigame(MinigameData data)
        {
            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            MinigameManager.Instance?.StartMinigame<MeasureCutMinigame>(data, ownerPlayerId);
        }

        private void OnMeasureCutResultResolutionResponse(NetworkSessionProgressAuthority.MeasureCutResultResolutionResponse response)
        {
            if (!_hasActiveSession)
            {
                return;
            }

            if (response.sessionToken != _activeSessionToken
                || !string.Equals(response.taskKey ?? string.Empty, _activeTaskKey ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            if (!response.accepted && !string.IsNullOrWhiteSpace(response.reason))
            {
                EventBus.Publish(new PlayerFeedbackEvent(response.reason));
            }

            ClearActiveSession();
        }

        private void OnMinigameEnded(MinigameEndedEvent evt)
        {
            TrySubmitTerminalResult(evt.MinigameId, evt.OwnerPlayerId, evt.Result);
        }

        private void OnMinigameCancelled(MinigameCancelledEvent evt)
        {
            TrySubmitTerminalResult(evt.MinigameId, evt.OwnerPlayerId, evt.Result);
        }

        private void TrySubmitTerminalResult(string minigameId, string ownerPlayerId, MinigameResult result)
        {
            if (!_hasActiveSession || _hasSubmittedResult || !IsNetworkSession())
            {
                return;
            }

            if (!string.Equals(minigameId, MinigameId, StringComparison.Ordinal))
            {
                return;
            }

            string localOwnerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            if (!string.Equals(ownerPlayerId, localOwnerId, StringComparison.Ordinal))
            {
                return;
            }

            if (!NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority authority))
            {
                return;
            }

            _hasSubmittedResult = true;
            float qualityScore = ResolveLastMeasureCutQualityScore();
            authority.RequestResolveMeasureCutResult(_activeSessionToken, _activeTaskKey, result, qualityScore);
        }

        private void TryAbortActiveSessionStartFailure()
        {
            if (!_hasActiveSession || !IsNetworkSession())
            {
                ClearActiveSession();
                return;
            }

            if (!NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority authority))
            {
                Debug.LogWarning("[PipeCuttingInteractable] Could not resolve active measure/cut session after approved start failure.", this);
                ClearActiveSession();
                return;
            }

            _hasSubmittedResult = true;
            authority.RequestResolveMeasureCutResult(_activeSessionToken, _activeTaskKey, MinigameResult.Cancelled, 0f);
        }

        private static float ResolveLastMeasureCutQualityScore()
        {
            IMinigame activeMinigame = MinigameManager.Instance?.GetActiveMinigame();
            MinigameData data = activeMinigame?.GetMinigameData();
            if (data == null || data.parameters == null)
            {
                return 0f;
            }

            if (!data.parameters.TryGetValue("measure_cut_quality", out object qualityValue) || qualityValue == null)
            {
                return 0f;
            }

            if (qualityValue is float floatValue)
            {
                return Mathf.Clamp(floatValue, 0f, 100f);
            }

            if (qualityValue is int intValue)
            {
                return Mathf.Clamp(intValue, 0, 100);
            }

            return 0f;
        }

        private void ClearActiveSession()
        {
            _hasActiveSession = false;
            _activeSessionToken = 0;
            _activeTaskKey = string.Empty;
            _hasSubmittedResult = false;
        }

        private static bool IsNetworkSession()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening;
        }

        public string GetDailyTaskLocationKey()
        {
            Transform container = transform.parent;
            string containerPath = BuildHierarchyPath(container != null ? container : transform);

            if (container == null)
            {
                return $"{DailyTaskType}:{containerPath}:0";
            }

            PipeCuttingInteractable[] interactables = container.GetComponentsInChildren<PipeCuttingInteractable>(true);
            Array.Sort(interactables, CompareByHierarchyPath);

            int index = 0;
            for (int i = 0; i < interactables.Length; i++)
            {
                if (interactables[i] == this)
                {
                    index = i;
                    break;
                }
            }

            return $"{DailyTaskType}:{containerPath}:{index}";
        }

        private static int CompareByHierarchyPath(PipeCuttingInteractable a, PipeCuttingInteractable b)
        {
            string pathA = BuildHierarchyPath(a != null ? a.transform : null);
            string pathB = BuildHierarchyPath(b != null ? b.transform : null);
            return string.CompareOrdinal(pathA, pathB);
        }

        private static string BuildHierarchyPath(Transform transformNode)
        {
            if (transformNode == null)
            {
                return string.Empty;
            }

            List<string> segments = new List<string>(8);
            Transform current = transformNode;
            while (current != null)
            {
                segments.Add($"{current.name}[{current.GetSiblingIndex()}]");
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments);
        }

        private bool TryResolveRequiredReferences(out string validationError)
        {
            List<string> failures = new List<string>(8);
            if (_measureCutCanvas == null) failures.Add("missing MeasureCutCanvas");
            if (_timerText == null) failures.Add("missing TimerText");
            if (_qualityText == null) failures.Add("missing QualityText");
            if (_instructionText == null) failures.Add("missing InstructionText");
            if (_cutTargetsRoot == null) failures.Add("missing CutTargets root");
            if (_stationCameraPose == null) failures.Add("missing MinigameCameraPose");
            if (_stationCameraLookTarget == null) failures.Add("missing MinigameLookTarget");
            if (_cutGuideRoot == null) failures.Add("missing CutGuideRoot");
            if (_guideLine01 == null || _guideLine02 == null || _guideLine03 == null) failures.Add("missing GuideLine renderer refs");
            if (_toleranceBand01 == null || _toleranceBand02 == null || _toleranceBand03 == null) failures.Add("missing ToleranceBand renderer refs");
            if (_fullPipeVisual == null) failures.Add("missing FullPipe visual");
            if (_leftSegment == null || _rightSegment == null) failures.Add("missing LeftSegment/RightSegment refs");
            if (_cutTargetsRoot != null && CountActiveChildren(_cutTargetsRoot) < 3) failures.Add("CutTargets needs at least 3 active cut lines");

            if (failures.Count > 0)
            {
                validationError = string.Join("; ", failures);
                return false;
            }

            validationError = string.Empty;
            return true;
        }

        private static int CountActiveChildren(Transform root)
        {
            if (root == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child != null && child.gameObject.activeInHierarchy)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
