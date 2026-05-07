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
    public sealed class DrillScrewInteractable : MinigameInteractableBase
    {
        private const string DailyTaskType = "drill_screw";
        private const string MinigameId = "drill_screw";

        [SerializeField] private Canvas _drillScrewCanvas;

        [Header("Drill/Screw HUD")]
        [SerializeField] private TMP_Text _timerText;
        [SerializeField] private TMP_Text _qualityText;
        [SerializeField] private TMP_Text _phaseText;
        [SerializeField] private TMP_Text _progressText;

        [Header("World View Camera")]
        [SerializeField] private Transform _stationCameraPose;
        [SerializeField] private Transform _stationCameraLookTarget;
        [SerializeField] private Transform _drillTargetsRoot;
        [SerializeField] private Transform _toolVisual;
        [SerializeField] private float _cameraTransitionDuration = 0.45f;
        [SerializeField] private float _cameraReturnDuration = 0.3f;
        [SerializeField] private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);
        [SerializeField] private Vector3 _worldLookTargetLocalOffset = Vector3.zero;
        [SerializeField] private float _worldTopDownAngleBias = 16f;
        [SerializeField] private float _worldCameraFovOverride = 50f;
        [SerializeField] private bool _freezePlayerMovementInWorldView = true;

        [Header("Gameplay Tuning")]
        [SerializeField] private float _baseTimeLimitSeconds = 30f;
        [SerializeField] private float _drillRadiusWorld = 0.08f;
        [SerializeField] private float _drillProgressPerSecond = 0.65f;
        [SerializeField] private float _drillOverrunPenaltyPerSecond = 0.3f;
        [SerializeField] private float _screwAcceptThreshold01 = 0.65f;
        [SerializeField] private float _screwTargetTightness01 = 0.85f;
        [SerializeField] private float _screwOvertightThreshold01 = 1.05f;
        [SerializeField] private float _screwRotationDegreesForFullTightness = 540f;

        private bool _awaitingNetworkStartApproval;
        private string _pendingNetworkStartTaskKey = string.Empty;
        private bool _hasActiveSession;
        private int _activeSessionToken;
        private string _activeTaskKey = string.Empty;
        private bool _hasSubmittedResult;

        private void OnEnable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse += OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnDrillScrewResultResolutionResponse += OnDrillScrewResultResolutionResponse;
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void OnDisable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse -= OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnDrillScrewResultResolutionResponse -= OnDrillScrewResultResolutionResponse;
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
                Debug.LogError($"[DrillScrewInteractable] Blocking drill_screw start: {validationError}", this);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = MinigameId,
                displayName = "Drill/Screw Roof Panel",
                timeLimit = 0f
            };

            data.SetParameter("canvas", _drillScrewCanvas);
            data.SetParameter("timer_text", _timerText);
            data.SetParameter("quality_text", _qualityText);
            data.SetParameter("phase_text", _phaseText);
            data.SetParameter("progress_text", _progressText);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_drill_targets_root", _drillTargetsRoot);
            data.SetParameter("world_tool_visual", _toolVisual);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("world_freeze_player", _freezePlayerMovementInWorldView);
            data.SetParameter("time_limit", Mathf.Max(5f, _baseTimeLimitSeconds));
            data.SetParameter("drill_radius_world", _drillRadiusWorld);
            data.SetParameter("drill_progress_per_second", _drillProgressPerSecond);
            data.SetParameter("drill_overrun_penalty_per_second", _drillOverrunPenaltyPerSecond);
            data.SetParameter("screw_accept_threshold", _screwAcceptThreshold01);
            data.SetParameter("screw_target_tightness", _screwTargetTightness01);
            data.SetParameter("screw_overtight_threshold", _screwOvertightThreshold01);
            data.SetParameter("screw_rotation_degrees_full", _screwRotationDegreesForFullTightness);
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
            StartLocalDrillScrewMinigame(data);
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

            int sessionToken = response.drillScrewSessionToken;
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

            StartLocalDrillScrewMinigame(approvedData);
        }

        private void StartLocalDrillScrewMinigame(MinigameData data)
        {
            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            MinigameManager.Instance?.StartMinigame<DrillScrewMinigame>(data, ownerPlayerId);
        }

        private void OnDrillScrewResultResolutionResponse(NetworkSessionProgressAuthority.DrillScrewResultResolutionResponse response)
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
            float qualityScore = ResolveLastDrillScrewQualityScore();
            authority.RequestResolveDrillScrewResult(_activeSessionToken, _activeTaskKey, result, qualityScore);
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
                Debug.LogWarning("[DrillScrewInteractable] Could not resolve active drill_screw session after approved start failure.", this);
                ClearActiveSession();
                return;
            }

            _hasSubmittedResult = true;
            authority.RequestResolveDrillScrewResult(_activeSessionToken, _activeTaskKey, MinigameResult.Cancelled, 0f);
        }

        private static float ResolveLastDrillScrewQualityScore()
        {
            IMinigame activeMinigame = MinigameManager.Instance?.GetActiveMinigame();
            MinigameData data = activeMinigame?.GetMinigameData();
            if (data == null || data.parameters == null)
            {
                return 0f;
            }

            if (!data.parameters.TryGetValue("drill_screw_quality", out object qualityValue) || qualityValue == null)
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

            DrillScrewInteractable[] interactables = container.GetComponentsInChildren<DrillScrewInteractable>(true);
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

        private static int CompareByHierarchyPath(DrillScrewInteractable a, DrillScrewInteractable b)
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
            if (_drillScrewCanvas == null) failures.Add("missing DrillScrewCanvas");
            if (_timerText == null) failures.Add("missing TimerText");
            if (_qualityText == null) failures.Add("missing QualityText");
            if (_phaseText == null) failures.Add("missing PhaseText");
            if (_progressText == null) failures.Add("missing ProgressText");
            if (_drillTargetsRoot == null) failures.Add("missing DrillTargets root");
            if (_stationCameraPose == null) failures.Add("missing MinigameCameraPose");
            if (_stationCameraLookTarget == null) failures.Add("missing MinigameLookTarget");
            if (_drillTargetsRoot != null && CountActiveChildren(_drillTargetsRoot) < 4) failures.Add("DrillTargets needs at least 4 active targets");

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
