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
    public sealed class PipePaintInteractable : MinigameInteractableBase
    {
        private const string DailyTaskType = "pipe_paint";
        private const string MinigameId = "pipe_paint";

        [SerializeField] private Canvas _pipePaintCanvas;

        [Header("Paint HUD")]
        [SerializeField] private TMP_Text _timerText;
        [SerializeField] private TMP_Text _qualityText;
        [SerializeField] private TMP_Text _instructionText;
        [SerializeField] private TMP_Text _progressText;

        [Header("World View Camera")]
        [SerializeField] private Transform _stationCameraPose;
        [SerializeField] private Transform _stationCameraLookTarget;
        [SerializeField] private Transform _paintTargetsRoot;
        [SerializeField] private Transform _brushCursorVisual;
        [SerializeField] private float _cameraTransitionDuration = 0.45f;
        [SerializeField] private float _cameraReturnDuration = 0.3f;
        [SerializeField] private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);
        [SerializeField] private Vector3 _worldLookTargetLocalOffset = Vector3.zero;
        [SerializeField] private float _worldTopDownAngleBias = 16f;
        [SerializeField] private float _worldCameraFovOverride = 50f;
        [SerializeField] private bool _freezePlayerMovementInWorldView = true;

        [Header("Gameplay Tuning")]
        [SerializeField] private float _baseTimeLimitSeconds = 30f;
        [SerializeField] private float _requiredPassCoveragePercent = 50f;
        [SerializeField] private float _brushRadiusWorld = 0.12f;
        [SerializeField] private Color _unpaintedColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        [SerializeField] private Color _paintedColor = new Color(0.2f, 0.7f, 0.95f, 1f);

        private bool _awaitingNetworkStartApproval;
        private string _pendingNetworkStartTaskKey = string.Empty;
        private bool _hasActiveSession;
        private int _activeSessionToken;
        private string _activeTaskKey = string.Empty;
        private bool _hasSubmittedResult;

        private void OnEnable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse += OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnPipePaintResultResolutionResponse += OnPipePaintResultResolutionResponse;
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void OnDisable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse -= OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnPipePaintResultResolutionResponse -= OnPipePaintResultResolutionResponse;
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
                Debug.LogError($"[PipePaintInteractable] Blocking pipe_paint start: {validationError}", this);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = MinigameId,
                displayName = "Paint Pipes",
                timeLimit = 0f
            };

            float dayMultiplier = 1f;
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                dayMultiplier = Mathf.Max(0.01f, gameManager.GetPipePaintDayDifficultyMultiplier());
            }

            data.SetParameter("canvas", _pipePaintCanvas);
            data.SetParameter("timer_text", _timerText);
            data.SetParameter("quality_text", _qualityText);
            data.SetParameter("instruction_text", _instructionText);
            data.SetParameter("progress_text", _progressText);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_paint_targets_root", _paintTargetsRoot);
            data.SetParameter("world_brush_cursor_visual", _brushCursorVisual);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("world_freeze_player", _freezePlayerMovementInWorldView);
            data.SetParameter("time_limit", Mathf.Max(5f, _baseTimeLimitSeconds - (5f * (1f - dayMultiplier))));
            data.SetParameter("required_pass_coverage_percent", _requiredPassCoveragePercent);
            data.SetParameter("brush_radius_world", _brushRadiusWorld);
            data.SetParameter("unpainted_color", _unpaintedColor);
            data.SetParameter("painted_color", _paintedColor);

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
            StartLocalPipePaintMinigame(data);
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

            int sessionToken = response.pipePaintSessionToken;
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

            StartLocalPipePaintMinigame(approvedData);
        }

        private void StartLocalPipePaintMinigame(MinigameData data)
        {
            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            MinigameManager.Instance?.StartMinigame<PipePaintMinigame>(data, ownerPlayerId);
        }

        private void OnPipePaintResultResolutionResponse(NetworkSessionProgressAuthority.PipePaintResultResolutionResponse response)
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
            float coverageScore = ResolveLastPipePaintCoverageScore();
            authority.RequestResolvePipePaintResult(_activeSessionToken, _activeTaskKey, result, coverageScore);
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
                Debug.LogWarning("[PipePaintInteractable] Could not resolve active pipe_paint session after approved start failure.", this);
                ClearActiveSession();
                return;
            }

            _hasSubmittedResult = true;
            authority.RequestResolvePipePaintResult(_activeSessionToken, _activeTaskKey, MinigameResult.Cancelled, 0f);
        }

        private static float ResolveLastPipePaintCoverageScore()
        {
            IMinigame activeMinigame = MinigameManager.Instance?.GetActiveMinigame();
            MinigameData data = activeMinigame?.GetMinigameData();
            if (data == null || data.parameters == null)
            {
                return 0f;
            }

            if (!data.parameters.TryGetValue("pipe_paint_coverage", out object coverageValue) || coverageValue == null)
            {
                return 0f;
            }

            if (coverageValue is float floatValue)
            {
                return Mathf.Clamp(floatValue, 0f, 100f);
            }

            if (coverageValue is int intValue)
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

            PipePaintInteractable[] interactables = container.GetComponentsInChildren<PipePaintInteractable>(true);
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

        private static int CompareByHierarchyPath(PipePaintInteractable a, PipePaintInteractable b)
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
            if (_pipePaintCanvas == null) failures.Add("missing PipePaintCanvas");
            if (_timerText == null) failures.Add("missing TimerText");
            if (_qualityText == null) failures.Add("missing QualityText");
            if (_instructionText == null) failures.Add("missing InstructionText");
            if (_progressText == null) failures.Add("missing ProgressText");
            if (_paintTargetsRoot == null) failures.Add("missing PaintTargets root");
            if (_stationCameraPose == null) failures.Add("missing MinigameCameraPose");
            if (_stationCameraLookTarget == null) failures.Add("missing MinigameLookTarget");
            if (_paintTargetsRoot != null && CountActiveChildren(_paintTargetsRoot) < 3) failures.Add("PaintTargets needs at least 3 active pipes");

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
