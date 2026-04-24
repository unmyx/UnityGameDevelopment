using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using Game.Minigames;
using Game.Networking;
using Game.Player;
using Unity.Netcode;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// Launches the Home computer browser minigame using the shared minigame flow.
    /// </summary>
    public class ComputerInteractable : MinigameInteractableBase
    {
        private const string MinigameId = "computer";
        private const string DisplayName = "Computer";
        private const string CameraPoseChildName = "MinigameCameraPose";
        private const string LookTargetChildName = "MinigameLookTarget";
        private const string FallbackCanvasName = "ComputerCanvas";

        [Header("Required References")]
        [SerializeField]
        private Canvas _computerCanvas;

        [SerializeField]
        [Tooltip("Child transform defining the final camera pose while using the computer.")]
        private Transform _stationCameraPose;

        [SerializeField]
        [Tooltip("Child transform used as look-at target while using the computer.")]
        private Transform _stationCameraLookTarget;

        [Header("Camera Transition")]
        [SerializeField]
        [Min(0f)]
        private float _cameraTransitionDuration = 0.35f;

        [SerializeField]
        [Min(0f)]
        private float _cameraReturnDuration = 0.25f;

        [SerializeField]
        private Vector3 _worldCameraLocalOffset = new Vector3(0f, 0.18f, -0.42f);

        [SerializeField]
        private Vector3 _worldLookTargetLocalOffset = Vector3.zero;

        [SerializeField]
        [Range(-35f, 45f)]
        private float _worldTopDownAngleBias;

        [SerializeField]
        [Range(0f, 120f)]
        private float _worldCameraFovOverride = 42f;

        [Header("Input")]
        [SerializeField]
        private bool _unlockCursorDuringMinigame = true;

        private bool _awaitingNetworkStartApproval;
        private string _pendingNetworkStartStationKey = string.Empty;
        private bool _hasActiveComputerSession;
        private int _activeComputerSessionToken;
        private string _activeComputerStationKey = string.Empty;
        private bool _hasSubmittedComputerSessionEnd;

        private void OnEnable()
        {
            NetworkSessionProgressAuthority.OnComputerSessionStartResponse += OnComputerSessionStartResponse;
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void OnDisable()
        {
            NetworkSessionProgressAuthority.OnComputerSessionStartResponse -= OnComputerSessionStartResponse;
            EventBus.Unsubscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Unsubscribe<MinigameCancelledEvent>(OnMinigameCancelled);
            _awaitingNetworkStartApproval = false;
            _pendingNetworkStartStationKey = string.Empty;
            TryReleaseActiveComputerSessionLock();
            ClearActiveComputerSession();
        }

        protected override MinigameData BuildMinigameData()
        {
            if (!TryResolveRequiredReferences(out string validationError))
            {
                Debug.LogError($"[ComputerInteractable] Blocking computer minigame start: {validationError}", this);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = MinigameId,
                displayName = DisplayName,
                timeLimit = 0f
            };

            data.SetParameter("canvas", _computerCanvas);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("unlock_cursor_during_minigame", _unlockCursorDuringMinigame);
            return data;
        }

        protected override void StartMinigame(MinigameData data)
        {
            string stationKey = GetComputerStationKey();
            if (IsNetworkSession())
            {
                if (_awaitingNetworkStartApproval)
                {
                    return;
                }

                if (!NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority authority))
                {
                    Debug.LogWarning("[ComputerInteractable] Computer start unavailable: missing local authority requester.", this);
                    EventBus.Publish(new PlayerFeedbackEvent("Computer unavailable."));
                    return;
                }

                _awaitingNetworkStartApproval = true;
                _pendingNetworkStartStationKey = stationKey;
                authority.RequestComputerSessionStart(stationKey);
                return;
            }

            StartLocalComputerMinigame(data);
        }

        private void OnComputerSessionStartResponse(NetworkSessionProgressAuthority.ComputerSessionStartResponse response)
        {
            if (!_awaitingNetworkStartApproval)
            {
                return;
            }

            if (!string.Equals(response.stationKey, _pendingNetworkStartStationKey, StringComparison.Ordinal))
            {
                return;
            }

            string requestedStationKey = _pendingNetworkStartStationKey;
            _awaitingNetworkStartApproval = false;
            _pendingNetworkStartStationKey = string.Empty;

            if (!response.approved)
            {
                if (!string.IsNullOrWhiteSpace(response.reason))
                {
                    Debug.Log($"[ComputerInteractable] Computer start rejected: {response.reason}", this);
                    EventBus.Publish(new PlayerFeedbackEvent(response.reason.Trim()));
                }
                else
                {
                    EventBus.Publish(new PlayerFeedbackEvent("Computer unavailable."));
                }
                ClearActiveComputerSession();
                return;
            }

            int sessionToken = response.computerSessionToken;
            string canonicalStationKey = string.IsNullOrWhiteSpace(response.canonicalStationKey)
                ? requestedStationKey
                : response.canonicalStationKey.Trim();
            if (sessionToken <= 0 || string.IsNullOrWhiteSpace(canonicalStationKey))
            {
                Debug.LogWarning("[ComputerInteractable] Computer start approval payload invalid.", this);
                EventBus.Publish(new PlayerFeedbackEvent("Computer unavailable."));
                ClearActiveComputerSession();
                return;
            }

            _hasActiveComputerSession = true;
            _activeComputerSessionToken = sessionToken;
            _activeComputerStationKey = canonicalStationKey;
            _hasSubmittedComputerSessionEnd = false;

            MinigameData approvedData = BuildMinigameData();
            if (approvedData == null || string.IsNullOrEmpty(approvedData.minigameId))
            {
                TryReleaseActiveComputerSessionLock();
                ClearActiveComputerSession();
                return;
            }

            StartLocalComputerMinigame(approvedData);
        }

        private void OnMinigameEnded(MinigameEndedEvent evt)
        {
            TrySubmitComputerSessionEnd(evt.MinigameId, evt.OwnerPlayerId);
        }

        private void OnMinigameCancelled(MinigameCancelledEvent evt)
        {
            TrySubmitComputerSessionEnd(evt.MinigameId, evt.OwnerPlayerId);
        }

        private void TrySubmitComputerSessionEnd(string minigameId, string ownerPlayerId)
        {
            if (!_hasActiveComputerSession || _hasSubmittedComputerSessionEnd || !IsNetworkSession())
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

            TryReleaseActiveComputerSessionLock();
            ClearActiveComputerSession();
        }

        private void TryReleaseActiveComputerSessionLock()
        {
            if (!_hasActiveComputerSession || _hasSubmittedComputerSessionEnd || !IsNetworkSession())
            {
                return;
            }

            if (!NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority authority))
            {
                return;
            }

            _hasSubmittedComputerSessionEnd = true;
            authority.RequestEndComputerSession(_activeComputerSessionToken, _activeComputerStationKey);
        }

        private void ClearActiveComputerSession()
        {
            _hasActiveComputerSession = false;
            _activeComputerSessionToken = 0;
            _activeComputerStationKey = string.Empty;
            _hasSubmittedComputerSessionEnd = false;
        }

        private void StartLocalComputerMinigame(MinigameData data)
        {
            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager == null)
            {
                Debug.LogError(
                    "[ComputerInteractable] Cannot start computer minigame: MinigameManager is unavailable in the active scene.",
                    this);
                return;
            }

            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            IMinigame startedMinigame = minigameManager.StartMinigame<ComputerMinigame>(data, ownerPlayerId);
            if (startedMinigame == null)
            {
                Debug.LogError(
                    "[ComputerInteractable] Computer minigame startup was rejected by MinigameManager.",
                    this);
                TryReleaseActiveComputerSessionLock();
                ClearActiveComputerSession();
            }
        }

        private string GetComputerStationKey()
        {
            Transform container = transform.parent;
            string containerPath = BuildHierarchyPath(container != null ? container : transform);
            if (container == null)
            {
                return $"computer:{containerPath}:0";
            }

            ComputerInteractable[] interactables = container.GetComponentsInChildren<ComputerInteractable>(true);
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

            return $"computer:{containerPath}:{index}";
        }

        private static int CompareByHierarchyPath(ComputerInteractable a, ComputerInteractable b)
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

        private static bool IsNetworkSession()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening;
        }

        private bool TryResolveRequiredReferences(out string validationError)
        {
            List<string> failures = new List<string>(4);

            if (_stationCameraPose == null)
            {
                Transform pose = transform.Find(CameraPoseChildName);
                if (pose != null)
                {
                    _stationCameraPose = pose;
                }
            }

            if (_stationCameraLookTarget == null)
            {
                Transform lookTarget = transform.Find(LookTargetChildName);
                if (lookTarget != null)
                {
                    _stationCameraLookTarget = lookTarget;
                }
            }

            if (_computerCanvas == null)
            {
                _computerCanvas = FindCanvasByName(FallbackCanvasName);
            }

            if (_computerCanvas == null)
            {
                failures.Add("missing ComputerCanvas reference");
            }

            if (_stationCameraPose == null)
            {
                failures.Add($"missing {CameraPoseChildName} child transform");
            }

            if (_stationCameraLookTarget == null)
            {
                failures.Add($"missing {LookTargetChildName} child transform");
            }

            if (failures.Count > 0)
            {
                validationError = string.Join("; ", failures);
                return false;
            }

            validationError = string.Empty;
            return true;
        }

        private static Canvas FindCanvasByName(string canvasName)
        {
            if (string.IsNullOrWhiteSpace(canvasName))
            {
                return null;
            }

            Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas candidate = canvases[i];
                if (candidate == null)
                {
                    continue;
                }

                if (string.Equals(candidate.name, canvasName, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
