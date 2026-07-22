using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using Game.Inventory;
using Game.Minigames;
using Game.Networking;
using Game.Player;
using TMPro;
using Unity.Netcode;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// Starts the world-surface cleaning minigame from world interaction.
    /// </summary>
    public class PipeInteractable : MinigameInteractableBase
    {
        private const string DailyTaskType = "cleaning";
        private const string MinigameId = "cleaning";

        [SerializeField]
        private Canvas _cleaningCanvas;

        [Header("Cleaning HUD")]
        [SerializeField]
        private TMP_Text _timerText;

        [SerializeField]
        private TMP_Text _progressText;
        
        [SerializeField]
        private TMP_Text _toolText;

        [Header("World View Camera")]
        [SerializeField]
        [Tooltip("Station child transform defining the final cleaning camera pose.")]
        private Transform _stationCameraPose;

        [SerializeField]
        [Tooltip("Station child transform the cleaning camera looks at.")]
        private Transform _stationCameraLookTarget;

        [SerializeField]
        private Transform _worldCleaningSurfaceRoot;

        [SerializeField]
        [Min(0f)]
        private float _cameraTransitionDuration = 0.45f;

        [SerializeField]
        [Min(0f)]
        private float _cameraReturnDuration = 0.3f;

        [SerializeField]
        private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);

        [SerializeField]
        private Vector3 _worldLookTargetLocalOffset = Vector3.zero;

        [SerializeField]
        [Range(-35f, 45f)]
        private float _worldTopDownAngleBias = 16f;

        [SerializeField]
        [Range(0f, 120f)]
        private float _worldCameraFovOverride = 52f;

        [SerializeField]
        private bool _freezePlayerMovementInWorldView = true;

        [Header("World Cleaning Interaction")]
        [SerializeField]
        [Min(0.01f)]
        private float _worldStainMarkerScale = 0.05f;

        [SerializeField]
        [Min(0.01f)]
        private float _worldStainMinSpacing = 0.14f;

        private bool _awaitingNetworkStartApproval;
        private string _pendingNetworkStartTaskKey = string.Empty;
        private bool _hasActiveCleaningSession;
        private int _activeCleaningSessionToken;
        private string _activeCleaningTaskKey = string.Empty;
        private bool _hasSubmittedCleaningResult;
        private ToolType _interactionToolSnapshot = ToolType.None;
        private ToolType _pendingNetworkToolSnapshot = ToolType.None;
        private readonly WarningOnceGate _sceneBindingWarningGate = new WarningOnceGate();

        public override void Interact()
        {
            if (!CanInteract)
            {
                return;
            }

            if (!PlayerContextLocator.TryGetLocalSelectedTool(out ToolType selectedTool)
                || !CleaningMinigame.SupportsTool(selectedTool))
            {
                EventBus.Publish(new PlayerFeedbackEvent(
                    "Select a cleaning tool: Water, Gasoline, or Chemical."));
                return;
            }

            _interactionToolSnapshot = selectedTool;
            try
            {
                base.Interact();
            }
            finally
            {
                _interactionToolSnapshot = ToolType.None;
            }
        }

        private void OnEnable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse += OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnCleaningResultResolutionResponse += OnCleaningResultResolutionResponse;
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void OnDisable()
        {
            NetworkSessionProgressAuthority.OnJobInteractableStartResponse -= OnJobInteractableStartResponse;
            NetworkSessionProgressAuthority.OnCleaningResultResolutionResponse -= OnCleaningResultResolutionResponse;
            EventBus.Unsubscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Unsubscribe<MinigameCancelledEvent>(OnMinigameCancelled);
            _awaitingNetworkStartApproval = false;
            _pendingNetworkStartTaskKey = string.Empty;
            _interactionToolSnapshot = ToolType.None;
            _pendingNetworkToolSnapshot = ToolType.None;
            _sceneBindingWarningGate.Clear();
            ClearActiveCleaningSession();
        }

        protected override MinigameData BuildMinigameData()
        {
            if (!CleaningMinigame.SupportsTool(_interactionToolSnapshot))
            {
                return null;
            }

            if (!ValidateSceneBindings(out string validationError))
            {
                ReportSceneBindingFailure(validationError);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = MinigameId,
                displayName = "Clean the Pipes",
                timeLimit = 0f
            };

            float cleaningEffectivenessMultiplier = 1f;
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                cleaningEffectivenessMultiplier = Mathf.Max(0.01f, gameManager.GetCleaningEffectivenessMultiplier());
            }

            data.SetParameter("canvas", _cleaningCanvas);
            data.SetParameter("timer_text", _timerText);
            data.SetParameter("progress_text", _progressText);
            data.SetParameter("tool_text", _toolText);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_cleaning_surface_root", _worldCleaningSurfaceRoot);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("world_freeze_player", _freezePlayerMovementInWorldView);
            data.SetParameter("world_stain_marker_scale", _worldStainMarkerScale);
            data.SetParameter("world_stain_min_spacing", _worldStainMinSpacing);
            data.SetParameter("world_spawn_seed", UnityEngine.Random.Range(int.MinValue, int.MaxValue));
            data.SetParameter("cleaning_tool_effectiveness_multiplier", cleaningEffectivenessMultiplier);
            MinigameToolSnapshot.Set(data, _interactionToolSnapshot);

            return data;
        }

        protected override void StartMinigame(MinigameData data)
        {
            ToolType toolSnapshot = MinigameToolSnapshot.Get(data);
            if (!CleaningMinigame.SupportsTool(toolSnapshot))
            {
                EventBus.Publish(new PlayerFeedbackEvent(
                    "Select a cleaning tool: Water, Gasoline, or Chemical."));
                return;
            }

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
                _pendingNetworkToolSnapshot = toolSnapshot;
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

            StartLocalCleaningMinigame(data);
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
            ToolType requestedTool = _pendingNetworkToolSnapshot;
            _awaitingNetworkStartApproval = false;
            _pendingNetworkStartTaskKey = string.Empty;
            _pendingNetworkToolSnapshot = ToolType.None;

            if (!response.approved)
            {
                if (!string.IsNullOrWhiteSpace(response.reason))
                {
                    EventBus.Publish(new PlayerFeedbackEvent(response.reason));
                }

                ClearActiveCleaningSession();
                return;
            }

            int sessionToken = response.cleaningSessionToken;
            string canonicalTaskKey = string.IsNullOrWhiteSpace(response.canonicalTaskKey)
                ? requestedTaskKey
                : response.canonicalTaskKey.Trim();
            if (IsNetworkSession())
            {
                if (sessionToken <= 0 || string.IsNullOrWhiteSpace(canonicalTaskKey))
                {
                    EventBus.Publish(new PlayerFeedbackEvent("Task start approval was invalid."));
                    ClearActiveCleaningSession();
                    return;
                }

                _hasActiveCleaningSession = true;
                _activeCleaningSessionToken = sessionToken;
                _activeCleaningTaskKey = canonicalTaskKey;
                _hasSubmittedCleaningResult = false;
            }

            _interactionToolSnapshot = requestedTool;
            MinigameData approvedData;
            try
            {
                approvedData = BuildMinigameData();
            }
            finally
            {
                _interactionToolSnapshot = ToolType.None;
            }
            if (approvedData == null || string.IsNullOrWhiteSpace(approvedData.minigameId))
            {
                ClearActiveCleaningSession();
                return;
            }

            StartLocalCleaningMinigame(approvedData);
        }

        private void StartLocalCleaningMinigame(MinigameData data)
        {
            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            MinigameManager.Instance?.StartMinigame<CleaningMinigame>(data, ownerPlayerId);
        }

        private void OnCleaningResultResolutionResponse(NetworkSessionProgressAuthority.CleaningResultResolutionResponse response)
        {
            if (!_hasActiveCleaningSession)
            {
                return;
            }

            if (response.sessionToken != _activeCleaningSessionToken
                || !string.Equals(response.taskKey ?? string.Empty, _activeCleaningTaskKey ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            if (!response.accepted && !string.IsNullOrWhiteSpace(response.reason))
            {
                EventBus.Publish(new PlayerFeedbackEvent(response.reason));
            }

            ClearActiveCleaningSession();
        }

        private void OnMinigameEnded(MinigameEndedEvent evt)
        {
            TrySubmitCleaningTerminalResult(evt.MinigameId, evt.OwnerPlayerId, evt.Result);
        }

        private void OnMinigameCancelled(MinigameCancelledEvent evt)
        {
            TrySubmitCleaningTerminalResult(evt.MinigameId, evt.OwnerPlayerId, evt.Result);
        }

        private void TrySubmitCleaningTerminalResult(string minigameId, string ownerPlayerId, MinigameResult result)
        {
            if (!_hasActiveCleaningSession || _hasSubmittedCleaningResult || !IsNetworkSession())
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

            _hasSubmittedCleaningResult = true;
            authority.RequestResolveCleaningResult(_activeCleaningSessionToken, _activeCleaningTaskKey, result);
        }

        private void ClearActiveCleaningSession()
        {
            _hasActiveCleaningSession = false;
            _activeCleaningSessionToken = 0;
            _activeCleaningTaskKey = string.Empty;
            _hasSubmittedCleaningResult = false;
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

            PipeInteractable[] interactables = container.GetComponentsInChildren<PipeInteractable>(true);
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

        private static int CompareByHierarchyPath(PipeInteractable a, PipeInteractable b)
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

        public bool ValidateSceneBindings(out string failureReason)
        {
            SceneBindingValidation validation = new SceneBindingValidation();
            validation.Require(
                _cleaningCanvas != null,
                $"Missing required field '_cleaningCanvas' on '{name}' (CleaningCanvas)");
            validation.Require(
                IsUiTextOnCanvas(_timerText, _cleaningCanvas),
                $"Missing or invalid required field '_timerText' on '{name}'; assign a TextMeshProUGUI child of CleaningCanvas");
            validation.Require(
                IsUiTextOnCanvas(_progressText, _cleaningCanvas),
                $"Missing or invalid required field '_progressText' on '{name}'; assign a TextMeshProUGUI child of CleaningCanvas");
            validation.Require(
                _worldCleaningSurfaceRoot != null,
                $"Missing required field '_worldCleaningSurfaceRoot' on '{name}' (CleaningSurfaces root)");
            validation.Require(
                _stationCameraPose != null,
                $"Missing required field '_stationCameraPose' on '{name}' (MinigameCameraPose)");
            validation.Require(
                _stationCameraLookTarget != null,
                $"Missing required field '_stationCameraLookTarget' on '{name}' (MinigameLookTarget)");

            if (_worldCleaningSurfaceRoot != null)
            {
                int activeSurfaceCount = CountActiveCleaningSurfaces(_worldCleaningSurfaceRoot);
                validation.Require(
                    CleaningSpawnRules.HasRequiredSurfaceCount(activeSurfaceCount),
                    $"Field '_worldCleaningSurfaceRoot' on '{name}' requires exactly " +
                    $"{CleaningSpawnRules.RequiredSurfaceCount} active collidable CleaningSurface children, found {activeSurfaceCount}");
            }

            bool isValid = validation.Complete(out failureReason);
            if (isValid)
            {
                _sceneBindingWarningGate.Clear();
            }

            return isValid;
        }

        private void ReportSceneBindingFailure(string failureReason)
        {
            if (_sceneBindingWarningGate.ShouldReport(failureReason))
            {
                Debug.LogWarning(
                    $"[PipeInteractable] Blocking cleaning minigame start on '{name}'. " +
                    $"Context: taskType='{DailyTaskType}', minigameId='{MinigameId}'. {failureReason}",
                    this);
            }

            EventBus.Publish(new PlayerFeedbackEvent("Cleaning station is unavailable because its scene setup is incomplete."));
        }

        private static bool IsUiTextOnCanvas(TMP_Text text, Canvas canvas)
        {
            return text is TextMeshProUGUI
                && canvas != null
                && text.transform != null
                && text.transform.IsChildOf(canvas.transform);
        }

        private static int CountActiveCleaningSurfaces(Transform surfacesRoot)
        {
            if (surfacesRoot == null || !surfacesRoot.gameObject.activeInHierarchy)
            {
                return 0;
            }

            int count = 0;
            Transform[] transforms = surfacesRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate == surfacesRoot)
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

                if (HasEnabledCollider(candidate))
                {
                    count++;
                }
            }

            if (count > 0)
            {
                return count;
            }

            return HasEnabledCollider(surfacesRoot) ? 1 : 0;
        }

        private static bool HasEnabledCollider(Transform root)
        {
            if (root == null)
            {
                return false;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

