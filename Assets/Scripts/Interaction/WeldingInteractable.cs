using System.Collections.Generic;
using System;
using Game.Core;
using Game.Core.Events;
using Game.Minigames;
using Game.Networking;
using Game.Player;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Interaction
{
    /// <summary>
    /// Starts the world-anchor welding minigame from world interaction.
    /// </summary>
    public class WeldingInteractable : MinigameInteractableBase
    {
        private const string DailyTaskType = "welding";
        private const string MinigameId = "welding";

        [SerializeField] private Canvas _minigameCanvas;

        [Header("World Weld Progress UI")]
        [SerializeField] private TMP_Text _coverageText;
        [SerializeField] private Image _coverageFillImage;
        [SerializeField] private TMP_Text _timerText;

        [Header("World View Camera")]
        [SerializeField] private bool _useWorldStationView = true;

        [SerializeField]
        [Tooltip("Station child transform defining the final minigame camera pose.")]
        private Transform _stationCameraPose;

        [SerializeField]
        [Tooltip("Station child transform the minigame camera looks at during transition.")]
        private Transform _stationCameraLookTarget;

        [SerializeField]
        [Min(0f)]
        private float _cameraTransitionDuration = 0.45f;

        [SerializeField]
        [Min(0f)]
        private float _cameraReturnDuration = 0.3f;

        [Header("World View Framing Tuning")]
        [SerializeField]
        [Tooltip("Runtime camera local-space offset from MinigameCameraPose. Increase Y for higher bird-view framing.")]
        private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.35f, -0.8f);

        [SerializeField]
        [Tooltip("Look target local-space offset from MinigameLookTarget.")]
        private Vector3 _worldLookTargetLocalOffset = Vector3.zero;

        [SerializeField]
        [Tooltip("Additional pitch bias in degrees applied after look-at rotation. Positive values tilt more top-down.")]
        [Range(-35f, 45f)]
        private float _worldTopDownAngleBias = 18f;

        [SerializeField]
        [Tooltip("Optional FOV override for welding world camera. Set <= 0 to reuse gameplay camera FOV.")]
        [Range(0f, 120f)]
        private float _worldCameraFovOverride = 50f;

        [Header("World Weld Targets")]
        [SerializeField]
        [Tooltip("Container with manual world-space weld point anchors.")]
        private Transform _weldAnchorsRoot;

        [SerializeField]
        [Range(1, 12)]
        private int _activeWorldWeldCount = 3;

        [SerializeField]
        [Min(0.1f)]
        private float _worldAnchorWeldRate = 1f;

        [SerializeField]
        [Min(12f)]
        private float _worldAnchorScreenRadiusPixels = 60f;

        [SerializeField]
        private bool _showWorldAnchorMarkers = true;

        private bool _awaitingNetworkStartApproval;
        private string _pendingNetworkStartTaskKey = string.Empty;
        private bool _hasActiveWeldingSession;
        private int _activeWeldingSessionToken;
        private string _activeWeldingTaskKey = string.Empty;
        private bool _hasSubmittedWeldingResult;

        private void OnEnable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse += OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnWeldingResultResolutionResponse += OnWeldingResultResolutionResponse;
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void OnDisable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse -= OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnWeldingResultResolutionResponse -= OnWeldingResultResolutionResponse;
            EventBus.Unsubscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Unsubscribe<MinigameCancelledEvent>(OnMinigameCancelled);
            _awaitingNetworkStartApproval = false;
            _pendingNetworkStartTaskKey = string.Empty;
            ClearActiveWeldingSession();
        }

        protected override MinigameData BuildMinigameData()
        {
            if (!TryResolveRequiredReferences(out string validationError))
            {
                Debug.LogError($"[WeldingInteractable] Blocking welding minigame start: {validationError}", this);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = MinigameId,
                displayName = "Weld the Metal",
                timeLimit = 0f
            };

            float weldingEffectivenessMultiplier = 1f;
            float weldingDayDifficultyMultiplier = 1f;
            int activeWorldWeldCount = _activeWorldWeldCount;
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                weldingEffectivenessMultiplier = Mathf.Max(0.01f, gameManager.GetWeldingEffectivenessMultiplier());
                weldingDayDifficultyMultiplier = Mathf.Max(0.01f, gameManager.GetWeldingDayDifficultyMultiplier());
                activeWorldWeldCount = Mathf.Max(1, _activeWorldWeldCount + gameManager.GetWeldingActiveTargetCountBonusForCurrentDay());
            }

            data.SetParameter("canvas", _minigameCanvas);
            data.SetParameter("coverage_text", _coverageText);
            data.SetParameter("coverage_fill_image", _coverageFillImage);
            data.SetParameter("timer_text", _timerText);
            data.SetParameter("world_view_enabled", _useWorldStationView);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("world_weld_anchors_root", _weldAnchorsRoot);
            data.SetParameter("world_active_weld_count", activeWorldWeldCount);
            data.SetParameter("world_anchor_weld_rate", _worldAnchorWeldRate * weldingEffectivenessMultiplier * weldingDayDifficultyMultiplier);
            data.SetParameter("world_anchor_screen_radius", _worldAnchorScreenRadiusPixels);
            data.SetParameter("world_anchor_markers", _showWorldAnchorMarkers);

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

            if (gameManager != null)
            {
                gameManager.RegisterDailyTaskLaunchContext(DailyTaskType, taskKey);
            }

            StartLocalWeldingMinigame(data);
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
                ClearActiveWeldingSession();
                return;
            }

            int sessionToken = response.weldingSessionToken;
            string canonicalTaskKey = string.IsNullOrWhiteSpace(response.canonicalTaskKey)
                ? requestedTaskKey
                : response.canonicalTaskKey.Trim();
            if (IsNetworkSession())
            {
                if (sessionToken <= 0 || string.IsNullOrWhiteSpace(canonicalTaskKey))
                {
                    EventBus.Publish(new PlayerFeedbackEvent("Task start approval was invalid."));
                    ClearActiveWeldingSession();
                    return;
                }

                _hasActiveWeldingSession = true;
                _activeWeldingSessionToken = sessionToken;
                _activeWeldingTaskKey = canonicalTaskKey;
                _hasSubmittedWeldingResult = false;
            }

            MinigameData approvedData = BuildMinigameData();
            if (approvedData == null || string.IsNullOrWhiteSpace(approvedData.minigameId))
            {
                ClearActiveWeldingSession();
                return;
            }

            StartLocalWeldingMinigame(approvedData);
        }

        private void StartLocalWeldingMinigame(MinigameData data)
        {
            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            MinigameManager.Instance?.StartMinigame<WeldingFillMinigame>(data, ownerPlayerId);
        }

        private void OnWeldingResultResolutionResponse(NetworkSessionProgressAuthority.WeldingResultResolutionResponse response)
        {
            if (!_hasActiveWeldingSession)
            {
                return;
            }

            if (response.sessionToken != _activeWeldingSessionToken
                || !string.Equals(response.taskKey ?? string.Empty, _activeWeldingTaskKey ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            if (!response.accepted && !string.IsNullOrWhiteSpace(response.reason))
            {
                EventBus.Publish(new PlayerFeedbackEvent(response.reason));
            }

            ClearActiveWeldingSession();
        }

        private void OnMinigameEnded(MinigameEndedEvent evt)
        {
            TrySubmitWeldingTerminalResult(evt.MinigameId, evt.OwnerPlayerId, evt.Result);
        }

        private void OnMinigameCancelled(MinigameCancelledEvent evt)
        {
            TrySubmitWeldingTerminalResult(evt.MinigameId, evt.OwnerPlayerId, evt.Result);
        }

        private void TrySubmitWeldingTerminalResult(string minigameId, string ownerPlayerId, MinigameResult result)
        {
            if (!_hasActiveWeldingSession || _hasSubmittedWeldingResult || !IsNetworkSession())
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

            _hasSubmittedWeldingResult = true;
            authority.RequestResolveWeldingResult(_activeWeldingSessionToken, _activeWeldingTaskKey, result);
        }

        private void ClearActiveWeldingSession()
        {
            _hasActiveWeldingSession = false;
            _activeWeldingSessionToken = 0;
            _activeWeldingTaskKey = string.Empty;
            _hasSubmittedWeldingResult = false;
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

            WeldingInteractable[] interactables = container.GetComponentsInChildren<WeldingInteractable>(true);
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

        private static int CompareByHierarchyPath(WeldingInteractable a, WeldingInteractable b)
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
            List<string> failures = new List<string>(7);
            if (_minigameCanvas == null)
            {
                failures.Add("missing WeldingCanvas");
            }

            if (_coverageFillImage == null)
            {
                failures.Add("missing coverage Fill image / progress UI");
            }
            else if (_coverageFillImage.type != Image.Type.Filled || _coverageFillImage.fillMethod != Image.FillMethod.Horizontal)
            {
                failures.Add("coverage Fill image is not configured as Filled + Horizontal");
            }

            if (_timerText == null)
            {
                failures.Add("missing welding TimerText reference");
            }

            if (_weldAnchorsRoot == null)
            {
                failures.Add("missing WeldAnchors root");
            }
            else if (CountActiveChildren(_weldAnchorsRoot) == 0)
            {
                failures.Add("WeldAnchors has zero active children");
            }

            if (_useWorldStationView)
            {
                if (_stationCameraPose == null)
                {
                    failures.Add("missing MinigameCameraPose while world view is enabled");
                }

                if (_stationCameraLookTarget == null)
                {
                    failures.Add("missing MinigameLookTarget while world view is enabled");
                }
            }

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
