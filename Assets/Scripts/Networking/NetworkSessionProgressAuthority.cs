using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using Game.Interaction;
using Game.Minigames;
using Game.Player;
using Game.Systems;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Networking
{
    /// <summary>
    /// MP-12 host-authoritative session progression seam.
    /// Server owns progression writes; clients consume mirrored progression state.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkSessionProgressAuthority : NetworkBehaviour
    {
        public struct JobInteractableStartResponse
        {
            public string taskType;
            public string taskKey;
            public string minigameId;
            public bool approved;
            public string reason;
            public int cleaningSessionToken;
            public int weldingSessionToken;
            public int measureCutSessionToken;
            public int pipePaintSessionToken;
            public int drillScrewSessionToken;
            public string canonicalTaskKey;
        }

        public struct StolenLootSellItemResponse
        {
            public string itemId;
            public bool success;
            public string reason;
            public int payoutAmount;
            public int remainingTrackedCount;
        }

        public struct UpgradePurchaseResponse
        {
            public string upgradeId;
            public bool success;
            public string reason;
            public int spentCurrency;
            public int resultingTier;
        }

        public struct TrackedLootDropResponse
        {
            public ulong requestId;
            public string itemId;
            public bool success;
            public string reason;
        }

        public struct NpcCatchTriggeredResponse
        {
            public string ownerKey;
            public ulong targetClientId;
            public ulong catchToken;
            public ulong npcNetworkObjectId;
            public float serverTime;
            public bool pendingLie;
        }

        public struct NpcLieResolutionResponse
        {
            public ulong catchToken;
            public bool accepted;
            public string reason;
            public MinigameResult result;
            public bool appliedConsequence;
        }

        public struct CleaningResultResolutionResponse
        {
            public int sessionToken;
            public string taskKey;
            public bool accepted;
            public string reason;
            public MinigameResult result;
        }

        public struct WeldingResultResolutionResponse
        {
            public int sessionToken;
            public string taskKey;
            public bool accepted;
            public string reason;
            public MinigameResult result;
        }

        public struct MeasureCutResultResolutionResponse
        {
            public int sessionToken;
            public string taskKey;
            public bool accepted;
            public string reason;
            public MinigameResult result;
            public float qualityScore;
        }

        public struct PipePaintResultResolutionResponse
        {
            public int sessionToken;
            public string taskKey;
            public bool accepted;
            public string reason;
            public MinigameResult result;
            public float coverageScore;
        }

        public struct DrillScrewResultResolutionResponse
        {
            public int sessionToken;
            public string taskKey;
            public bool accepted;
            public string reason;
            public MinigameResult result;
            public float qualityScore;
        }

        public struct ComputerSessionStartResponse
        {
            public string stationKey;
            public bool approved;
            public string reason;
            public int computerSessionToken;
            public string canonicalStationKey;
        }

        public static event Action<JobInteractableStartResponse> OnJobInteractableStartResponse;
        public static event Action<StolenLootSellItemResponse> OnStolenLootSellItemResponse;
        public static event Action<UpgradePurchaseResponse> OnUpgradePurchaseResponse;
        public static event Action<TrackedLootDropResponse> OnTrackedLootDropResponse;
        public static event Action<NpcCatchTriggeredResponse> OnNpcCatchTriggeredResponse;
        public static event Action<NpcLieResolutionResponse> OnNpcLieResolutionResponse;
        public static event Action<CleaningResultResolutionResponse> OnCleaningResultResolutionResponse;
        public static event Action<WeldingResultResolutionResponse> OnWeldingResultResolutionResponse;
        public static event Action<MeasureCutResultResolutionResponse> OnMeasureCutResultResolutionResponse;
        public static event Action<PipePaintResultResolutionResponse> OnPipePaintResultResolutionResponse;
        public static event Action<DrillScrewResultResolutionResponse> OnDrillScrewResultResolutionResponse;
        public static event Action<ComputerSessionStartResponse> OnComputerSessionStartResponse;
        public static event Action<string> OnOwnerStolenLootSnapshotApplied;

        [Serializable]
        private class TaskProgressSnapshot
        {
            public List<DailyTaskAssignmentData> dailyTaskAssignments = new List<DailyTaskAssignmentData>();
            public List<GeneratedTaskWaveData> generatedTaskWaves = new List<GeneratedTaskWaveData>();
            public List<string> unlockedTaskKeys = new List<string>();
            public List<ToolDataEntry> ownedTools = new List<ToolDataEntry>();
        }

        [Serializable]
        private class StolenLootSnapshotPayload
        {
            public List<StolenLootEntryData> entries = new List<StolenLootEntryData>();
        }

        [SerializeField] private float _syncIntervalSeconds = 0.25f;
        [SerializeField] private bool _enableLogs;
        [SerializeField] private float _cleaningSessionTimeoutSeconds = 45f;
        private const string GoldRingItemId = "wedding_ring_gold";
        private const string SilverRingItemId = "wedding_ring_silver";
        private const string GoldRingDropPrefabResourcesPath = ResourcePaths.NetworkGoldRingDropPrefab;
        private const string SilverRingDropPrefabResourcesPath = ResourcePaths.NetworkSilverRingDropPrefab;

        private static NetworkSessionProgressAuthority _localRequester;
        private static NetworkSessionProgressAuthority _authoritativePublisher;
        private static GameObject _cachedGoldRingDropNetworkPrefab;
        private static GameObject _cachedSilverRingDropNetworkPrefab;
        private static bool _hasValidatedCriticalResourcePaths;
        private static readonly HashSet<string> LoggedRequesterFallbackTelemetry = new HashSet<string>();
        private static readonly HashSet<string> LoggedRequesterValidationWarnings = new HashSet<string>();

        private readonly NetworkVariable<int> _currentDay = new NetworkVariable<int>(
            1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _runPhase = new NetworkVariable<int>(
            (int)GameManager.RunPhase.Work, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<bool> _workdayCompleted = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<float> _currentWorkHour = new NetworkVariable<float>(
            7f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _nextTaskWaveIndex = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<bool> _runFailed = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _dayWorkEarnings = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int> _currency = new NetworkVariable<int>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private float _nextSyncTime;
        private string _lastTaskSnapshotJson = string.Empty;
        private readonly HashSet<string> _processedRewardClaimKeys = new HashSet<string>();
        private readonly HashSet<string> _processedNpcCatchKeys = new HashSet<string>();
        private readonly HashSet<string> _processedNpcLieResolutionKeys = new HashSet<string>();
        private readonly Dictionary<string, CleaningSessionRuntime> _activeCleaningSessionsByTaskKey =
            new Dictionary<string, CleaningSessionRuntime>(StringComparer.Ordinal);
        private readonly Dictionary<int, CleaningSessionRuntime> _cleaningSessionsByToken =
            new Dictionary<int, CleaningSessionRuntime>();
        private readonly Dictionary<string, WeldingSessionRuntime> _activeWeldingSessionsByTaskKey =
            new Dictionary<string, WeldingSessionRuntime>(StringComparer.Ordinal);
        private readonly Dictionary<int, WeldingSessionRuntime> _weldingSessionsByToken =
            new Dictionary<int, WeldingSessionRuntime>();
        private readonly Dictionary<string, MeasureCutSessionRuntime> _activeMeasureCutSessionsByTaskKey =
            new Dictionary<string, MeasureCutSessionRuntime>(StringComparer.Ordinal);
        private readonly Dictionary<int, MeasureCutSessionRuntime> _measureCutSessionsByToken =
            new Dictionary<int, MeasureCutSessionRuntime>();
        private readonly Dictionary<string, PipePaintSessionRuntime> _activePipePaintSessionsByTaskKey =
            new Dictionary<string, PipePaintSessionRuntime>(StringComparer.Ordinal);
        private readonly Dictionary<int, PipePaintSessionRuntime> _pipePaintSessionsByToken =
            new Dictionary<int, PipePaintSessionRuntime>();
        private readonly Dictionary<string, DrillScrewSessionRuntime> _activeDrillScrewSessionsByTaskKey =
            new Dictionary<string, DrillScrewSessionRuntime>(StringComparer.Ordinal);
        private readonly Dictionary<int, DrillScrewSessionRuntime> _drillScrewSessionsByToken =
            new Dictionary<int, DrillScrewSessionRuntime>();
        private readonly Dictionary<string, ComputerSessionRuntime> _activeComputerSessionsByStationKey =
            new Dictionary<string, ComputerSessionRuntime>(StringComparer.Ordinal);
        private readonly Dictionary<int, ComputerSessionRuntime> _computerSessionsByToken =
            new Dictionary<int, ComputerSessionRuntime>();
        private static readonly HashSet<string> ConsumedNpcCatchKeys = new HashSet<string>();
        private int _cleaningSessionSequence;
        private int _weldingSessionSequence;
        private int _measureCutSessionSequence;
        private int _pipePaintSessionSequence;
        private int _drillScrewSessionSequence;
        private int _computerSessionSequence;

        private bool _hasActiveLocalLieCatch;
        private string _activeLocalLieOwnerKey = string.Empty;
        private ulong _activeLocalLieCatchToken;
        private ulong _activeLocalLieNpcNetworkObjectId;
        private bool _awaitingLocalLieResolutionAck;

        private enum CleaningSessionStatus
        {
            Pending = 0,
            Resolved = 1,
            Aborted = 2
        }

        private sealed class CleaningSessionRuntime
        {
            public int sessionToken;
            public string taskKey;
            public string ownerKey;
            public ulong ownerClientId;
            public float startedAt;
            public CleaningSessionStatus status;
            public MinigameResult resolvedResult;
            public float resolvedAt;
        }

        private enum WeldingSessionStatus
        {
            Pending = 0,
            Resolved = 1,
            Aborted = 2
        }

        private sealed class WeldingSessionRuntime
        {
            public int sessionToken;
            public string taskKey;
            public string ownerKey;
            public ulong ownerClientId;
            public float startedAt;
            public WeldingSessionStatus status;
            public MinigameResult resolvedResult;
            public float resolvedAt;
        }

        private enum MeasureCutSessionStatus
        {
            Pending = 0,
            Resolved = 1,
            Aborted = 2
        }

        private sealed class MeasureCutSessionRuntime
        {
            public int sessionToken;
            public string taskKey;
            public string ownerKey;
            public ulong ownerClientId;
            public float startedAt;
            public MeasureCutSessionStatus status;
            public MinigameResult resolvedResult;
            public float resolvedAt;
            public float resolvedQualityScore;
        }

        private enum PipePaintSessionStatus
        {
            Pending = 0,
            Resolved = 1,
            Aborted = 2
        }

        private sealed class PipePaintSessionRuntime
        {
            public int sessionToken;
            public string taskKey;
            public string ownerKey;
            public ulong ownerClientId;
            public float startedAt;
            public PipePaintSessionStatus status;
            public MinigameResult resolvedResult;
            public float resolvedAt;
            public float resolvedCoverageScore;
        }

        private enum DrillScrewSessionStatus
        {
            Pending = 0,
            Resolved = 1,
            Aborted = 2
        }

        private sealed class DrillScrewSessionRuntime
        {
            public int sessionToken;
            public string taskKey;
            public string ownerKey;
            public ulong ownerClientId;
            public float startedAt;
            public DrillScrewSessionStatus status;
            public MinigameResult resolvedResult;
            public float resolvedAt;
            public float resolvedQualityScore;
        }

        private enum ComputerSessionStatus
        {
            Pending = 0,
            Resolved = 1,
            Aborted = 2
        }

        private sealed class ComputerSessionRuntime
        {
            public int sessionToken;
            public string stationKey;
            public string ownerKey;
            public ulong ownerClientId;
            public float startedAt;
            public ComputerSessionStatus status;
            public float resolvedAt;
        }

        private const string CleaningTaskType = "cleaning";
        private const string CleaningMinigameId = "cleaning";
        private const int CleaningSessionRewardDedupeScope = 21021;
        private const string WeldingTaskType = "welding";
        private const string WeldingMinigameId = "welding";
        private const int WeldingSessionRewardDedupeScope = 22022;
        private const string MeasureCutTaskType = "measure_cut";
        private const string MeasureCutMinigameId = "measure_cut";
        private const int MeasureCutSessionRewardDedupeScope = 23023;
        private const string PipePaintTaskType = "pipe_paint";
        private const string PipePaintMinigameId = "pipe_paint";
        private const int PipePaintSessionRewardDedupeScope = 24024;
        private const string DrillScrewTaskType = "drill_screw";
        private const string DrillScrewMinigameId = "drill_screw";
        private const int DrillScrewSessionRewardDedupeScope = 25025;
        private const string ComputerMinigameId = "computer";

        public static bool TryGetLocalRequester(out NetworkSessionProgressAuthority authority)
        {
            if (_localRequester != null && _localRequester.IsSpawned)
            {
                authority = _localRequester;
                return true;
            }

            if (PlayerContextLocator.TryGetLocalPlayerTransform(out Transform localPlayerTransform)
                && localPlayerTransform != null)
            {
                authority = localPlayerTransform.GetComponent<NetworkSessionProgressAuthority>();
                if (authority != null)
                {
                    _localRequester = authority;
                    LogLocalRequesterFallbackTelemetry("local_player_transform", 1, authority);
                    ValidateRequesterFallbackWindow("local_player_transform");
                    ValidateRequesterOwnerMatch(authority, "local_player_transform");
                    return true;
                }
            }

            NetworkSessionProgressAuthority[] authorities = FindObjectsByType<NetworkSessionProgressAuthority>(FindObjectsInactive.Exclude);
            int ownerSpawnedCount = 0;
            for (int i = 0; i < authorities.Length; i++)
            {
                NetworkSessionProgressAuthority candidate = authorities[i];
                if (candidate != null && candidate.IsOwner && candidate.IsSpawned)
                {
                    ownerSpawnedCount++;
                }
            }

            ValidateRequesterSceneScanAmbiguity(ownerSpawnedCount, authorities);
            if (ownerSpawnedCount != 1)
            {
                authority = null;
                return false;
            }

            for (int i = 0; i < authorities.Length; i++)
            {
                NetworkSessionProgressAuthority candidate = authorities[i];
                if (candidate != null && candidate.IsOwner && candidate.IsSpawned)
                {
                    _localRequester = candidate;
                    authority = candidate;
                    LogLocalRequesterFallbackTelemetry("scene_scan", authorities.Length, candidate);
                    ValidateRequesterFallbackWindow("scene_scan");
                    ValidateRequesterOwnerMatch(candidate, "scene_scan");
                    return true;
                }
            }

            authority = null;
            return false;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void ValidateRequesterFallbackWindow(string source)
        {
            NetworkManager manager = NetworkManager.Singleton;
            FallbackWindow window = FallbackWindow.Stable;
            if (manager == null || !manager.IsListening)
            {
                window = FallbackWindow.Bootstrap;
            }
            else if (_localRequester == null || !_localRequester.IsSpawned)
            {
                window = FallbackWindow.OwnershipRebind;
                if (!manager.IsConnectedClient || !manager.ConnectedClients.ContainsKey(manager.LocalClientId))
                {
                    window = FallbackWindow.LateNetworkSpawnSync;
                }
            }

            if (window == FallbackWindow.OwnershipRebind || window == FallbackWindow.LateNetworkSpawnSync || window == FallbackWindow.Bootstrap)
            {
                return;
            }

            string sceneName = SceneManager.GetActiveScene().name;
            string key = $"NSA_WINDOW|{sceneName}|{source}|{FallbackGuardrails.ToToken(window)}";
            if (!LoggedRequesterValidationWarnings.Add(key))
            {
                return;
            }

            Debug.LogWarning(
                $"[FallbackValidation][NSA_REQUESTER_WINDOW] scene='{sceneName}' source='{source}' window='{FallbackGuardrails.ToToken(window)}' expected='ownership_rebind|late_network_spawn_sync' risk='stable_session_fallback'");
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void ValidateRequesterOwnerMatch(NetworkSessionProgressAuthority resolvedAuthority, string source)
        {
            if (resolvedAuthority == null)
            {
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
            {
                return;
            }

            bool ownerMismatch = !resolvedAuthority.IsOwner;
            bool spawnedMismatch = !resolvedAuthority.IsSpawned;
            bool modeMismatch = manager.IsServer && !manager.IsClient;
            if (!ownerMismatch && !spawnedMismatch && !modeMismatch)
            {
                return;
            }

            string sceneName = SceneManager.GetActiveScene().name;
            string key = $"NSA_OWNER_MISMATCH|{sceneName}|{source}|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(resolvedAuthority)}|{ownerMismatch}|{spawnedMismatch}|{modeMismatch}";
            if (!LoggedRequesterValidationWarnings.Add(key))
            {
                return;
            }

            Debug.LogWarning(
                $"[FallbackValidation][NSA_REQUESTER_OWNER_MISMATCH] scene='{sceneName}' source='{source}' ownerMismatch='{ownerMismatch}' spawnedMismatch='{spawnedMismatch}' modeMismatch='{modeMismatch}' risk='ownership_misbind'");
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void ValidateRequesterSceneScanAmbiguity(int ownerSpawnedCount, NetworkSessionProgressAuthority[] allCandidates)
        {
            string sceneName = SceneManager.GetActiveScene().name;
            string key = $"NSA_SCENE_SCAN_COUNT|{sceneName}|{ownerSpawnedCount}";
            if (!LoggedRequesterValidationWarnings.Add(key))
            {
                return;
            }

            if (ownerSpawnedCount == 1)
            {
                return;
            }

            Debug.LogWarning(
                $"[FallbackValidation][NSA_SCENE_SCAN_AMBIGUITY] scene='{sceneName}' ownerSpawnedCandidates='{ownerSpawnedCount}' totalCandidates='{(allCandidates != null ? allCandidates.Length : 0)}' risk='nondeterministic_owner_selection'");
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void LogLocalRequesterFallbackTelemetry(
            string source,
            int candidates,
            NetworkSessionProgressAuthority chosenAuthority)
        {
            string sceneName = SceneManager.GetActiveScene().name;
            NetworkManager manager = NetworkManager.Singleton;
            string netMode = "offline";
            ulong localClientId = 0UL;
            if (manager != null)
            {
                localClientId = manager.LocalClientId;
                if (manager.IsListening)
                {
                    netMode = manager.IsServer
                        ? (manager.IsClient ? "host" : "server")
                        : "client";
                }
            }

            string chosenAuthorityInstanceId = chosenAuthority != null
                ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(chosenAuthority).ToString()
                : "none";
            string key =
                $"NSA_LOCAL_REQUESTER_FALLBACK|{sceneName}|{netMode}|{localClientId}|{source}|{chosenAuthorityInstanceId}";
            if (!LoggedRequesterFallbackTelemetry.Add(key))
            {
                return;
            }

            string chosenNetworkObjectId = chosenAuthority != null && chosenAuthority.IsSpawned
                ? chosenAuthority.NetworkObjectId.ToString()
                : "none";
            Debug.LogWarning(
                $"[FallbackTelemetry][NSA_LOCAL_REQUESTER_FALLBACK] scene='{sceneName}' netMode='{netMode}' localClientId='{localClientId}' " +
                $"source='{source}' candidates='{Mathf.Max(0, candidates)}' chosenNetworkObjectId='{chosenNetworkObjectId}' risk='ownership_misbind'");
        }

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                _localRequester = this;
                ConsumedNpcCatchKeys.Clear();
            }

            if (IsServer && IsOwner)
            {
                _authoritativePublisher = this;
            }

            _currentDay.OnValueChanged += OnScalarValueChanged;
            _runPhase.OnValueChanged += OnScalarValueChanged;
            _workdayCompleted.OnValueChanged += OnScalarValueChanged;
            _currentWorkHour.OnValueChanged += OnScalarValueChanged;
            _nextTaskWaveIndex.OnValueChanged += OnScalarValueChanged;
            _runFailed.OnValueChanged += OnScalarValueChanged;
            _dayWorkEarnings.OnValueChanged += OnScalarValueChanged;
            _currency.OnValueChanged += OnScalarValueChanged;

            if (IsServer)
            {
                ValidateCriticalResourcePathsOnce();
                SyncFromGameManager(forceTaskSnapshot: true);

                if (IsAuthoritativePublisher() && NetworkManager != null)
                {
                    NetworkManager.OnClientConnectedCallback += OnServerClientConnected;
                    NetworkManager.OnClientDisconnectCallback += OnServerClientDisconnected;
                }
            }
            else if (ShouldConsumeAsClientMirror())
            {
                ApplyScalarStateToLocalGameManager();
            }

            if (ShouldHandleTargetedCatchFlow())
            {
                EventBus.Subscribe<MinigameEndedEvent>(OnLocalMinigameEnded);
                EventBus.Subscribe<MinigameCancelledEvent>(OnLocalMinigameCancelled);
            }
        }

        public override void OnNetworkDespawn()
        {
            _currentDay.OnValueChanged -= OnScalarValueChanged;
            _runPhase.OnValueChanged -= OnScalarValueChanged;
            _workdayCompleted.OnValueChanged -= OnScalarValueChanged;
            _currentWorkHour.OnValueChanged -= OnScalarValueChanged;
            _nextTaskWaveIndex.OnValueChanged -= OnScalarValueChanged;
            _runFailed.OnValueChanged -= OnScalarValueChanged;
            _dayWorkEarnings.OnValueChanged -= OnScalarValueChanged;
            _currency.OnValueChanged -= OnScalarValueChanged;
            _processedRewardClaimKeys.Clear();
            _processedNpcCatchKeys.Clear();
            _processedNpcLieResolutionKeys.Clear();
            _activeCleaningSessionsByTaskKey.Clear();
            _cleaningSessionsByToken.Clear();
            _activeWeldingSessionsByTaskKey.Clear();
            _weldingSessionsByToken.Clear();
            _activeMeasureCutSessionsByTaskKey.Clear();
            _measureCutSessionsByToken.Clear();
            _activePipePaintSessionsByTaskKey.Clear();
            _pipePaintSessionsByToken.Clear();
            _activeDrillScrewSessionsByTaskKey.Clear();
            _drillScrewSessionsByToken.Clear();
            _activeComputerSessionsByStationKey.Clear();
            _computerSessionsByToken.Clear();
            ClearLocalLieCatchSession();

            if (IsServer && NetworkManager != null)
            {
                NetworkManager.OnClientConnectedCallback -= OnServerClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnServerClientDisconnected;
            }

            if (ReferenceEquals(_localRequester, this))
            {
                _localRequester = null;
                ConsumedNpcCatchKeys.Clear();
            }

            if (ReferenceEquals(_authoritativePublisher, this))
            {
                _authoritativePublisher = null;
            }

            if (ShouldHandleTargetedCatchFlow())
            {
                EventBus.Unsubscribe<MinigameEndedEvent>(OnLocalMinigameEnded);
                EventBus.Unsubscribe<MinigameCancelledEvent>(OnLocalMinigameCancelled);
            }
        }

        private void Update()
        {
            if (!IsAuthoritativePublisher())
            {
                return;
            }

            CleanupStaleCleaningSessions();
            CleanupStaleWeldingSessions();
            CleanupStaleMeasureCutSessions();
            CleanupStalePipePaintSessions();
            CleanupStaleDrillScrewSessions();
            CleanupStaleComputerSessions();

            if (Time.unscaledTime < _nextSyncTime)
            {
                return;
            }

            _nextSyncTime = Time.unscaledTime + Mathf.Max(0.1f, _syncIntervalSeconds);
            SyncFromGameManager(forceTaskSnapshot: false);
        }

        private void OnServerClientConnected(ulong clientId)
        {
            if (!IsAuthoritativePublisher())
            {
                return;
            }

            // MP-26: force a full authoritative snapshot on join/rejoin so complex
            // progression state is never missed due to unchanged snapshot hashes.
            SyncFromGameManager(forceTaskSnapshot: true);

            // MP-27: proactively hydrate owner-scoped loot snapshot on join/rejoin.
            string ownerKey = ResolveOwnerPlayerIdFromSender(clientId);
            PushOwnerStolenLootSnapshotToClient(clientId, ownerKey);
        }

        private void OnServerClientDisconnected(ulong clientId)
        {
            if (!IsAuthoritativePublisher() || clientId == NetworkManager.ServerClientId)
            {
                return;
            }

            HandleServerClientDisconnected(clientId);
        }

        public void RequestCompleteWorkdayAndGoHome()
        {
            if (!IsSpawned)
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteCompleteWorkdayAndGoHome();
                    return;
                }

                ExecuteCompleteWorkdayAndGoHome();
                return;
            }

            RequestCompleteWorkdayAndGoHomeServerRpc();
        }

        public void RequestStartNextDay()
        {
            if (!IsSpawned)
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteStartNextDay();
                    return;
                }

                ExecuteStartNextDay();
                return;
            }

            RequestStartNextDayServerRpc();
        }

        public void RequestSellTrackedStolenLoot()
        {
            if (!IsSpawned)
            {
                return;
            }

            if (IsServer)
            {
                ExecuteSellTrackedStolenLoot(OwnerClientId);
                return;
            }

            RequestSellTrackedStolenLootServerRpc();
        }

        public void RequestSellTrackedStolenLootItemUnit(string itemId)
        {
            if (!IsSpawned)
            {
                return;
            }

            string normalizedItemId = string.IsNullOrWhiteSpace(itemId)
                ? string.Empty
                : itemId.Trim();
            if (string.IsNullOrEmpty(normalizedItemId))
            {
                return;
            }

            if (IsServer)
            {
                ExecuteSellTrackedStolenLootItemUnit(OwnerClientId, normalizedItemId);
                return;
            }

            RequestSellTrackedStolenLootItemUnitServerRpc(normalizedItemId);
        }

        public void RequestRefreshStolenLootSnapshot()
        {
            if (!IsSpawned)
            {
                return;
            }

            if (IsServer)
            {
                string ownerKey = ResolveOwnerPlayerIdFromSender(OwnerClientId);
                PushOwnerStolenLootSnapshotToClient(OwnerClientId, ownerKey);
                return;
            }

            RequestRefreshStolenLootSnapshotServerRpc();
        }

        public static bool TrySyncStolenLootSnapshotToClient(ulong targetClientId, string ownerKey)
        {
            if (_authoritativePublisher == null || !_authoritativePublisher.IsServer || !_authoritativePublisher.IsSpawned)
            {
                return false;
            }

            _authoritativePublisher.PushOwnerStolenLootSnapshotToClient(targetClientId, ownerKey);
            return true;
        }

        public static bool TryHandleClientDisconnected(ulong clientId)
        {
            if (_authoritativePublisher == null || !_authoritativePublisher.IsServer || !_authoritativePublisher.IsSpawned)
            {
                return false;
            }

            _authoritativePublisher.HandleServerClientDisconnected(clientId);
            return true;
        }

        public static bool TryRegisterNpcCatch(
            ulong targetClientId,
            string ownerKey,
            ulong catchToken,
            ulong npcNetworkObjectId,
            float serverTime,
            bool pendingLie)
        {
            if (_authoritativePublisher == null || !_authoritativePublisher.IsServer || !_authoritativePublisher.IsSpawned)
            {
                return false;
            }

            return _authoritativePublisher.ServerRegisterNpcCatch(
                targetClientId,
                ownerKey,
                catchToken,
                npcNetworkObjectId,
                serverTime,
                pendingLie);
        }

        public void RequestPurchaseUpgrade(string upgradeId)
        {
            if (!IsSpawned)
            {
                return;
            }

            string normalizedUpgradeId = string.IsNullOrWhiteSpace(upgradeId)
                ? string.Empty
                : upgradeId.Trim();

            if (IsServer)
            {
                ExecutePurchaseUpgrade(normalizedUpgradeId, OwnerClientId);
                return;
            }

            RequestPurchaseUpgradeServerRpc(normalizedUpgradeId);
        }

        public void RequestTrackedLootDrop(string itemId, Vector3 worldPosition, Quaternion worldRotation, ulong requestId)
        {
            if (!IsSpawned)
            {
                return;
            }

            string normalizedItemId = string.IsNullOrWhiteSpace(itemId)
                ? string.Empty
                : itemId.Trim();
            if (string.IsNullOrEmpty(normalizedItemId))
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteTrackedLootDrop(OwnerClientId, normalizedItemId, worldPosition, worldRotation, requestId);
                    return;
                }

                ExecuteTrackedLootDrop(OwnerClientId, normalizedItemId, worldPosition, worldRotation, requestId);
                return;
            }

            RequestTrackedLootDropServerRpc(normalizedItemId, worldPosition, worldRotation, requestId);
        }

        public void RequestComputerSessionStart(string stationKey)
        {
            if (!IsSpawned)
            {
                return;
            }

            string normalizedStationKey = NormalizeTaskKeyForSession(stationKey);
            if (string.IsNullOrEmpty(normalizedStationKey))
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteComputerSessionStart(OwnerClientId, normalizedStationKey);
                    return;
                }

                ExecuteComputerSessionStart(OwnerClientId, normalizedStationKey);
                return;
            }

            RequestComputerSessionStartServerRpc(normalizedStationKey);
        }

        public void RequestEndComputerSession(int sessionToken, string stationKey)
        {
            if (!IsSpawned || sessionToken <= 0)
            {
                return;
            }

            string normalizedStationKey = NormalizeTaskKeyForSession(stationKey);
            if (string.IsNullOrEmpty(normalizedStationKey))
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteEndComputerSession(OwnerClientId, sessionToken, normalizedStationKey);
                    return;
                }

                ExecuteEndComputerSession(OwnerClientId, sessionToken, normalizedStationKey);
                return;
            }

            RequestEndComputerSessionServerRpc(sessionToken, normalizedStationKey);
        }

        public void RequestResolveNpcLieResult(ulong catchToken, ulong npcNetworkObjectId, MinigameResult result)
        {
            if (!IsSpawned || catchToken == 0UL)
            {
                return;
            }

            if (IsServer)
            {
                ExecuteResolveNpcLieResult(OwnerClientId, catchToken, npcNetworkObjectId, (int)result);
                return;
            }

            RequestResolveNpcLieResultServerRpc(catchToken, npcNetworkObjectId, (int)result);
        }

        public void RequestResolveCleaningResult(int sessionToken, string taskKey, MinigameResult result)
        {
            if (!IsSpawned || sessionToken <= 0)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteResolveCleaningResult(OwnerClientId, sessionToken, normalizedTaskKey, (int)result);
                    return;
                }

                ExecuteResolveCleaningResult(OwnerClientId, sessionToken, normalizedTaskKey, (int)result);
                return;
            }

            RequestResolveCleaningResultServerRpc(sessionToken, normalizedTaskKey, (int)result);
        }

        public void RequestResolveWeldingResult(int sessionToken, string taskKey, MinigameResult result)
        {
            if (!IsSpawned || sessionToken <= 0)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteResolveWeldingResult(OwnerClientId, sessionToken, normalizedTaskKey, (int)result);
                    return;
                }

                ExecuteResolveWeldingResult(OwnerClientId, sessionToken, normalizedTaskKey, (int)result);
                return;
            }

            RequestResolveWeldingResultServerRpc(sessionToken, normalizedTaskKey, (int)result);
        }

        public void RequestResolveMeasureCutResult(int sessionToken, string taskKey, MinigameResult result, float qualityScore)
        {
            if (!IsSpawned || sessionToken <= 0)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteResolveMeasureCutResult(
                        OwnerClientId,
                        sessionToken,
                        normalizedTaskKey,
                        (int)result,
                        qualityScore);
                    return;
                }

                ExecuteResolveMeasureCutResult(OwnerClientId, sessionToken, normalizedTaskKey, (int)result, qualityScore);
                return;
            }

            RequestResolveMeasureCutResultServerRpc(sessionToken, normalizedTaskKey, (int)result, qualityScore);
        }

        public void RequestResolvePipePaintResult(int sessionToken, string taskKey, MinigameResult result, float coverageScore)
        {
            if (!IsSpawned || sessionToken <= 0)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteResolvePipePaintResult(
                        OwnerClientId,
                        sessionToken,
                        normalizedTaskKey,
                        (int)result,
                        coverageScore);
                    return;
                }

                ExecuteResolvePipePaintResult(OwnerClientId, sessionToken, normalizedTaskKey, (int)result, coverageScore);
                return;
            }

            RequestResolvePipePaintResultServerRpc(sessionToken, normalizedTaskKey, (int)result, coverageScore);
        }

        public void RequestResolveDrillScrewResult(int sessionToken, string taskKey, MinigameResult result, float qualityScore)
        {
            if (!IsSpawned || sessionToken <= 0)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                return;
            }

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteResolveDrillScrewResult(
                        OwnerClientId,
                        sessionToken,
                        normalizedTaskKey,
                        (int)result,
                        qualityScore);
                    return;
                }

                ExecuteResolveDrillScrewResult(OwnerClientId, sessionToken, normalizedTaskKey, (int)result, qualityScore);
                return;
            }

            RequestResolveDrillScrewResultServerRpc(sessionToken, normalizedTaskKey, (int)result, qualityScore);
        }

        public void RequestRegisterDailyTaskLaunchContext(string taskType, string taskKey)
        {
            if (!IsSpawned)
            {
                return;
            }

            if (IsServer)
            {
                ExecuteRegisterDailyTaskLaunchContext(taskType, taskKey);
                return;
            }

            RequestRegisterDailyTaskLaunchContextServerRpc(taskType ?? string.Empty, taskKey ?? string.Empty);
        }

        public void RequestMinigameRewardClaim(
            MinigameResult result,
            string minigameId,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope)
        {
            if (!IsSpawned)
            {
                return;
            }

            if (IsServer)
            {
                ExecuteMinigameRewardClaim(result, minigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope, OwnerClientId);
                return;
            }

            RequestMinigameRewardClaimServerRpc(
                (int)result,
                minigameId ?? string.Empty,
                ownerPlayerId ?? string.Empty,
                sessionToken,
                dedupeLifecycleScope);
        }

        public void RequestJobInteractableStart(string taskType, string taskKey, string minigameId)
        {
            if (!IsSpawned)
            {
                return;
            }

            string normalizedTaskType = taskType ?? string.Empty;
            string normalizedTaskKey = taskKey ?? string.Empty;
            string normalizedMinigameId = minigameId ?? string.Empty;

            if (IsServer)
            {
                if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                    && !ReferenceEquals(authoritativePublisher, this))
                {
                    authoritativePublisher.ExecuteJobInteractableStart(
                        OwnerClientId,
                        normalizedTaskType,
                        normalizedTaskKey,
                        normalizedMinigameId);
                    return;
                }

                ExecuteJobInteractableStart(
                    OwnerClientId,
                    normalizedTaskType,
                    normalizedTaskKey,
                    normalizedMinigameId);
                return;
            }

            RequestJobInteractableStartServerRpc(
                normalizedTaskType,
                normalizedTaskKey,
                normalizedMinigameId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestCompleteWorkdayAndGoHomeServerRpc(ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteCompleteWorkdayAndGoHome();
                return;
            }

            ExecuteCompleteWorkdayAndGoHome();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestStartNextDayServerRpc(ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteStartNextDay();
                return;
            }

            ExecuteStartNextDay();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestSellTrackedStolenLootServerRpc(ServerRpcParams serverRpcParams = default)
        {
            ExecuteSellTrackedStolenLoot(serverRpcParams.Receive.SenderClientId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestSellTrackedStolenLootItemUnitServerRpc(
            string itemId,
            ServerRpcParams serverRpcParams = default)
        {
            ExecuteSellTrackedStolenLootItemUnit(serverRpcParams.Receive.SenderClientId, itemId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestRefreshStolenLootSnapshotServerRpc(ServerRpcParams serverRpcParams = default)
        {
            string ownerKey = ResolveOwnerPlayerIdFromSender(serverRpcParams.Receive.SenderClientId);
            PushOwnerStolenLootSnapshotToClient(serverRpcParams.Receive.SenderClientId, ownerKey);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestPurchaseUpgradeServerRpc(
            string upgradeId,
            ServerRpcParams serverRpcParams = default)
        {
            ExecutePurchaseUpgrade(upgradeId, serverRpcParams.Receive.SenderClientId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestTrackedLootDropServerRpc(
            string itemId,
            Vector3 worldPosition,
            Quaternion worldRotation,
            ulong requestId,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteTrackedLootDrop(
                    serverRpcParams.Receive.SenderClientId,
                    itemId,
                    worldPosition,
                    worldRotation,
                    requestId);
                return;
            }

            ExecuteTrackedLootDrop(serverRpcParams.Receive.SenderClientId, itemId, worldPosition, worldRotation, requestId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestComputerSessionStartServerRpc(
            string stationKey,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteComputerSessionStart(serverRpcParams.Receive.SenderClientId, stationKey);
                return;
            }

            ExecuteComputerSessionStart(serverRpcParams.Receive.SenderClientId, stationKey);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestEndComputerSessionServerRpc(
            int sessionToken,
            string stationKey,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteEndComputerSession(serverRpcParams.Receive.SenderClientId, sessionToken, stationKey);
                return;
            }

            ExecuteEndComputerSession(serverRpcParams.Receive.SenderClientId, sessionToken, stationKey);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestRegisterDailyTaskLaunchContextServerRpc(string taskType, string taskKey)
        {
            ExecuteRegisterDailyTaskLaunchContext(taskType, taskKey);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestMinigameRewardClaimServerRpc(
            int resultValue,
            string minigameId,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope,
            ServerRpcParams serverRpcParams = default)
        {
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;
            ExecuteMinigameRewardClaim(
                parsedResult,
                minigameId,
                ownerPlayerId,
                sessionToken,
                dedupeLifecycleScope,
                serverRpcParams.Receive.SenderClientId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestJobInteractableStartServerRpc(
            string taskType,
            string taskKey,
            string minigameId,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteJobInteractableStart(
                    serverRpcParams.Receive.SenderClientId,
                    taskType,
                    taskKey,
                    minigameId);
                return;
            }

            ExecuteJobInteractableStart(
                serverRpcParams.Receive.SenderClientId,
                taskType,
                taskKey,
                minigameId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestResolveNpcLieResultServerRpc(
            ulong catchToken,
            ulong npcNetworkObjectId,
            int resultValue,
            ServerRpcParams serverRpcParams = default)
        {
            ExecuteResolveNpcLieResult(
                serverRpcParams.Receive.SenderClientId,
                catchToken,
                npcNetworkObjectId,
                resultValue);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestResolveCleaningResultServerRpc(
            int sessionToken,
            string taskKey,
            int resultValue,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteResolveCleaningResult(
                    serverRpcParams.Receive.SenderClientId,
                    sessionToken,
                    taskKey,
                    resultValue);
                return;
            }

            ExecuteResolveCleaningResult(
                serverRpcParams.Receive.SenderClientId,
                sessionToken,
                taskKey,
                resultValue);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestResolveWeldingResultServerRpc(
            int sessionToken,
            string taskKey,
            int resultValue,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteResolveWeldingResult(
                    serverRpcParams.Receive.SenderClientId,
                    sessionToken,
                    taskKey,
                    resultValue);
                return;
            }

            ExecuteResolveWeldingResult(
                serverRpcParams.Receive.SenderClientId,
                sessionToken,
                taskKey,
                resultValue);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestResolveMeasureCutResultServerRpc(
            int sessionToken,
            string taskKey,
            int resultValue,
            float qualityScore,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteResolveMeasureCutResult(
                    serverRpcParams.Receive.SenderClientId,
                    sessionToken,
                    taskKey,
                    resultValue,
                    qualityScore);
                return;
            }

            ExecuteResolveMeasureCutResult(
                serverRpcParams.Receive.SenderClientId,
                sessionToken,
                taskKey,
                resultValue,
                qualityScore);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestResolvePipePaintResultServerRpc(
            int sessionToken,
            string taskKey,
            int resultValue,
            float coverageScore,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteResolvePipePaintResult(
                    serverRpcParams.Receive.SenderClientId,
                    sessionToken,
                    taskKey,
                    resultValue,
                    coverageScore);
                return;
            }

            ExecuteResolvePipePaintResult(
                serverRpcParams.Receive.SenderClientId,
                sessionToken,
                taskKey,
                resultValue,
                coverageScore);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestResolveDrillScrewResultServerRpc(
            int sessionToken,
            string taskKey,
            int resultValue,
            float qualityScore,
            ServerRpcParams serverRpcParams = default)
        {
            if (TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authoritativePublisher)
                && !ReferenceEquals(authoritativePublisher, this))
            {
                authoritativePublisher.ExecuteResolveDrillScrewResult(
                    serverRpcParams.Receive.SenderClientId,
                    sessionToken,
                    taskKey,
                    resultValue,
                    qualityScore);
                return;
            }

            ExecuteResolveDrillScrewResult(
                serverRpcParams.Receive.SenderClientId,
                sessionToken,
                taskKey,
                resultValue,
                qualityScore);
        }

        private static bool TryGetAuthoritativePublisher(out NetworkSessionProgressAuthority authority)
        {
            authority = _authoritativePublisher;
            return authority != null && authority.IsServer && authority.IsSpawned;
        }

        [ClientRpc]
        private void ReceiveTaskSnapshotClientRpc(string snapshotJson)
        {
            if (IsServer || !ShouldConsumeAsClientMirror())
            {
                return;
            }

            ApplyScalarStateToLocalGameManager();

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || string.IsNullOrWhiteSpace(snapshotJson))
            {
                return;
            }

            TaskProgressSnapshot snapshot = JsonUtility.FromJson<TaskProgressSnapshot>(snapshotJson);
            if (snapshot == null)
            {
                return;
            }

            gameManager.ApplyNetworkProgressComplexState(
                snapshot.dailyTaskAssignments,
                snapshot.generatedTaskWaves,
                snapshot.unlockedTaskKeys,
                snapshot.ownedTools);
        }

        private void SyncFromGameManager(bool forceTaskSnapshot)
        {
            if (!IsServer)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            _currentDay.Value = gameManager.GetCurrentDay();
            _runPhase.Value = (int)gameManager.GetCurrentRunPhase();
            _workdayCompleted.Value = gameManager.IsWorkdayCompleted();
            _currentWorkHour.Value = gameManager.GetCurrentWorkHour();
            _nextTaskWaveIndex.Value = gameManager.GetNextTaskWaveIndexForSave();
            _runFailed.Value = gameManager.IsRunFailed();
            _dayWorkEarnings.Value = gameManager.GetDayWorkEarnings();
            _currency.Value = gameManager.GetCurrency();

            TaskProgressSnapshot snapshot = new TaskProgressSnapshot
            {
                dailyTaskAssignments = gameManager.GetDailyTaskAssignmentsForSave(),
                generatedTaskWaves = gameManager.GetGeneratedTaskWavesForSave(),
                unlockedTaskKeys = gameManager.GetUnlockedTaskKeysForSave(),
                ownedTools = gameManager.GetOwnedToolUpgradesForSave()
            };

            string snapshotJson = JsonUtility.ToJson(snapshot);
            if (!forceTaskSnapshot && string.Equals(_lastTaskSnapshotJson, snapshotJson, StringComparison.Ordinal))
            {
                return;
            }

            _lastTaskSnapshotJson = snapshotJson;
            ReceiveTaskSnapshotClientRpc(snapshotJson);
        }

        private void OnScalarValueChanged<T>(T previousValue, T newValue)
        {
            if (IsServer || !ShouldConsumeAsClientMirror())
            {
                return;
            }

            ApplyScalarStateToLocalGameManager();
        }

        private void ApplyScalarStateToLocalGameManager()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            gameManager.ApplyNetworkProgressScalarState(
                _currentDay.Value,
                _runPhase.Value,
                _workdayCompleted.Value,
                _currentWorkHour.Value,
                _nextTaskWaveIndex.Value,
                _runFailed.Value);
            gameManager.ApplyNetworkEconomyScalarState(_currency.Value, _dayWorkEarnings.Value);
        }

        private void ExecuteCompleteWorkdayAndGoHome()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            gameManager.TryCompleteWorkdayAndRouteToHomeScene();
            SyncFromGameManager(forceTaskSnapshot: true);
        }

        private void ExecuteStartNextDay()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            gameManager.TryStartNextDayAndRouteToGameplayScene();
            SyncFromGameManager(forceTaskSnapshot: true);
        }

        private void ExecuteSellTrackedStolenLoot(ulong senderClientId)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            string ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            gameManager.TrySellTrackedStolenLootInHome(ownerKey, out _, out _);
            PushOwnerStolenLootSnapshotToClient(senderClientId, ownerKey);
            SyncFromGameManager(forceTaskSnapshot: true);
        }

        private void ExecuteSellTrackedStolenLootItemUnit(ulong senderClientId, string itemId)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                SendStolenLootSellItemResponseClientRpc(
                    itemId ?? string.Empty,
                    false,
                    "Progression is unavailable.",
                    0,
                    0,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string normalizedItemId = string.IsNullOrWhiteSpace(itemId)
                ? string.Empty
                : itemId.Trim();
            if (string.IsNullOrEmpty(normalizedItemId))
            {
                SendStolenLootSellItemResponseClientRpc(
                    string.Empty,
                    false,
                    "Invalid item.",
                    0,
                    0,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            if (gameManager.IsRunFailed() || gameManager.GetCurrentRunPhase() != GameManager.RunPhase.Home)
            {
                PushOwnerStolenLootSnapshotToClient(senderClientId, ownerKey);
                SendStolenLootSellItemResponseClientRpc(
                    normalizedItemId,
                    false,
                    "Sell unavailable.",
                    0,
                    0,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!gameManager.IsTrackedStolenLootItem(normalizedItemId))
            {
                PushOwnerStolenLootSnapshotToClient(senderClientId, ownerKey);
                SendStolenLootSellItemResponseClientRpc(
                    normalizedItemId,
                    false,
                    "Item is not sellable.",
                    0,
                    0,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (gameManager.GetStolenLootCountForItem(normalizedItemId, ownerKey) <= 0)
            {
                PushOwnerStolenLootSnapshotToClient(senderClientId, ownerKey);
                SendStolenLootSellItemResponseClientRpc(
                    normalizedItemId,
                    false,
                    "Item unavailable.",
                    0,
                    0,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            bool success = gameManager.TrySellTrackedStolenLootItemUnitInHome(
                normalizedItemId,
                ownerKey,
                out int payoutAmount,
                out int remainingTrackedCount);

            PushOwnerStolenLootSnapshotToClient(senderClientId, ownerKey);
            SyncFromGameManager(forceTaskSnapshot: true);

            SendStolenLootSellItemResponseClientRpc(
                normalizedItemId,
                success,
                success ? string.Empty : "Sell unavailable.",
                payoutAmount,
                remainingTrackedCount,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecutePurchaseUpgrade(string upgradeId, ulong senderClientId)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                SendUpgradePurchaseResponseClientRpc(
                    upgradeId ?? string.Empty,
                    false,
                    "Upgrade unavailable.",
                    0,
                    0,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string normalizedUpgradeId = string.IsNullOrWhiteSpace(upgradeId)
                ? string.Empty
                : upgradeId.Trim();
            if (string.IsNullOrEmpty(normalizedUpgradeId))
            {
                SendUpgradePurchaseResponseClientRpc(
                    string.Empty,
                    false,
                    "Upgrade unavailable.",
                    0,
                    0,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            bool success = gameManager.TryPurchaseUpgradeInHome(normalizedUpgradeId, out int spentCurrency, out int resultingTier);
            string reason = string.Empty;
            if (!success)
            {
                List<GameManager.HomeUpgradeStatusData> statuses = gameManager.GetHomeUpgradeStatusEntries();
                for (int i = 0; i < statuses.Count; i++)
                {
                    GameManager.HomeUpgradeStatusData status = statuses[i];
                    if (status == null)
                    {
                        continue;
                    }

                    if (!string.Equals(status.upgradeId, normalizedUpgradeId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    reason = string.IsNullOrWhiteSpace(status.unavailableReason)
                        ? "Upgrade unavailable."
                        : status.unavailableReason.Trim();
                    resultingTier = status.currentTier;
                    break;
                }

                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = "Upgrade unavailable.";
                }
            }

            SyncFromGameManager(forceTaskSnapshot: true);
            SendUpgradePurchaseResponseClientRpc(
                normalizedUpgradeId,
                success,
                success ? string.Empty : reason,
                success ? spentCurrency : 0,
                resultingTier,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteTrackedLootDrop(
            ulong senderClientId,
            string itemId,
            Vector3 worldPosition,
            Quaternion worldRotation,
            ulong requestId)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            string normalizedItemId = string.IsNullOrWhiteSpace(itemId)
                ? string.Empty
                : itemId.Trim();

            if (string.IsNullOrEmpty(normalizedItemId))
            {
                SendTrackedLootDropResponseClientRpc(requestId, string.Empty, false, "Invalid item id.", BuildTargetClientRpcParams(senderClientId));
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.IsRunFailed() || gameManager.GetCurrentRunPhase() != GameManager.RunPhase.Work)
            {
                SendTrackedLootDropResponseClientRpc(requestId, normalizedItemId, false, "Drop unavailable.", BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!gameManager.IsTrackedStolenLootItem(normalizedItemId))
            {
                SendTrackedLootDropResponseClientRpc(requestId, normalizedItemId, false, "Item is not droppable.", BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            if (gameManager.GetStolenLootCountForItem(normalizedItemId, ownerKey) <= 0)
            {
                SendTrackedLootDropResponseClientRpc(requestId, normalizedItemId, false, "Item unavailable.", BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!TryResolveTrackedLootNetworkDropPrefab(normalizedItemId, out GameObject dropPrefab) || dropPrefab == null)
            {
                SendTrackedLootDropResponseClientRpc(requestId, normalizedItemId, false, "Drop prefab missing.", BuildTargetClientRpcParams(senderClientId));
                return;
            }

            GameObject instance = Instantiate(dropPrefab, worldPosition, worldRotation);
            if (instance == null)
            {
                SendTrackedLootDropResponseClientRpc(requestId, normalizedItemId, false, "Failed to spawn drop.", BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!instance.TryGetComponent(out NetworkObject networkObject))
            {
                Destroy(instance);
                SendTrackedLootDropResponseClientRpc(requestId, normalizedItemId, false, "Drop prefab missing NetworkObject.", BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!instance.TryGetComponent(out InteractableItem interactableItem))
            {
                Destroy(instance);
                SendTrackedLootDropResponseClientRpc(requestId, normalizedItemId, false, "Drop prefab missing InteractableItem.", BuildTargetClientRpcParams(senderClientId));
                return;
            }

            interactableItem.DisablePersistenceForRuntimeDrop();
            networkObject.Spawn();
            gameManager.TryUnregisterStolenLootForDrop(normalizedItemId, ownerKey, 1);
            PushOwnerStolenLootSnapshotToClient(senderClientId, ownerKey);
            SyncFromGameManager(forceTaskSnapshot: true);

            if (_enableLogs)
            {
                Game.Core.DevelopmentDiagnostics.Log(
                    $"[NetworkSessionProgressAuthority] Spawned network drop '{normalizedItemId}' for client {senderClientId} at {worldPosition}.",
                    this);
            }

            SendTrackedLootDropResponseClientRpc(requestId, normalizedItemId, true, string.Empty, BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteComputerSessionStart(ulong senderClientId, string stationKey)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            string normalizedStationKey = NormalizeTaskKeyForSession(stationKey);
            if (string.IsNullOrEmpty(normalizedStationKey))
            {
                SendComputerSessionStartResponseClientRpc(
                    stationKey ?? string.Empty,
                    false,
                    "Station unavailable.",
                    0,
                    string.Empty,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.IsRunFailed() || gameManager.GetCurrentRunPhase() != GameManager.RunPhase.Home)
            {
                SendComputerSessionStartResponseClientRpc(
                    normalizedStationKey,
                    false,
                    "Computer unavailable.",
                    0,
                    normalizedStationKey,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null
                || !manager.IsListening
                || !manager.ConnectedClients.TryGetValue(senderClientId, out NetworkClient senderClient)
                || senderClient == null
                || senderClient.PlayerObject == null
                || !senderClient.PlayerObject.IsSpawned)
            {
                SendComputerSessionStartResponseClientRpc(
                    normalizedStationKey,
                    false,
                    "Requester unavailable.",
                    0,
                    normalizedStationKey,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (_activeComputerSessionsByStationKey.TryGetValue(normalizedStationKey, out ComputerSessionRuntime active)
                && active != null
                && active.status == ComputerSessionStatus.Pending)
            {
                SendComputerSessionStartResponseClientRpc(
                    normalizedStationKey,
                    false,
                    "Computer station is currently occupied.",
                    0,
                    normalizedStationKey,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            int token = ++_computerSessionSequence;
            if (token <= 0)
            {
                _computerSessionSequence = 1;
                token = _computerSessionSequence;
            }

            ComputerSessionRuntime session = new ComputerSessionRuntime
            {
                sessionToken = token,
                stationKey = normalizedStationKey,
                ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId),
                ownerClientId = senderClientId,
                startedAt = Time.unscaledTime,
                status = ComputerSessionStatus.Pending,
                resolvedAt = 0f
            };
            _activeComputerSessionsByStationKey[normalizedStationKey] = session;
            _computerSessionsByToken[token] = session;

            SendComputerSessionStartResponseClientRpc(
                normalizedStationKey,
                true,
                string.Empty,
                token,
                normalizedStationKey,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteEndComputerSession(ulong senderClientId, int sessionToken, string stationKey)
        {
            if (!IsServer || !IsSpawned || sessionToken <= 0)
            {
                return;
            }

            string normalizedStationKey = NormalizeTaskKeyForSession(stationKey);
            if (string.IsNullOrEmpty(normalizedStationKey))
            {
                return;
            }

            if (!_computerSessionsByToken.TryGetValue(sessionToken, out ComputerSessionRuntime session)
                || session == null
                || session.status != ComputerSessionStatus.Pending)
            {
                return;
            }

            string authoritativeOwnerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            if (session.ownerClientId != senderClientId
                || !string.Equals(session.ownerKey, authoritativeOwnerKey, StringComparison.Ordinal)
                || !string.Equals(session.stationKey, normalizedStationKey, StringComparison.Ordinal))
            {
                return;
            }

            session.status = ComputerSessionStatus.Resolved;
            session.resolvedAt = Time.unscaledTime;
            _activeComputerSessionsByStationKey.Remove(session.stationKey);
            _computerSessionsByToken.Remove(sessionToken);
        }

        private void ExecuteRegisterDailyTaskLaunchContext(string taskType, string taskKey)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            gameManager.RegisterDailyTaskLaunchContext(taskType, taskKey);
            SyncFromGameManager(forceTaskSnapshot: true);
        }

        private void ExecuteJobInteractableStart(
            ulong senderClientId,
            string taskType,
            string taskKey,
            string minigameId)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                if (_enableLogs)
                {
                    Debug.LogWarning("[NetworkSessionProgressAuthority] Job start rejected: missing GameManager.");
                }

                SendJobInteractableStartResponseClientRpc(
                    taskType ?? string.Empty,
                    taskKey ?? string.Empty,
                    minigameId ?? string.Empty,
                    false,
                    "Progression is unavailable.",
                    0,
                    0,
                    0,
                    0,
                    0,
                    string.Empty,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null
                || !manager.IsListening
                || !manager.ConnectedClients.TryGetValue(senderClientId, out NetworkClient senderClient)
                || senderClient == null
                || senderClient.PlayerObject == null
                || !senderClient.PlayerObject.IsSpawned)
            {
                if (_enableLogs)
                {
                    Debug.LogWarning($"[NetworkSessionProgressAuthority] Job start rejected: sender {senderClientId} unavailable.");
                }

                SendJobInteractableStartResponseClientRpc(
                    taskType ?? string.Empty,
                    taskKey ?? string.Empty,
                    minigameId ?? string.Empty,
                    false,
                    "Requester is unavailable.",
                    0,
                    0,
                    0,
                    0,
                    0,
                    string.Empty,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!gameManager.CanLaunchTaskAtLocation(taskType, taskKey, out string blockedReason))
            {
                if (_enableLogs)
                {
                    Game.Core.DevelopmentDiagnostics.Log($"[NetworkSessionProgressAuthority] Job start rejected from client {senderClientId}: {blockedReason}");
                }

                SendJobInteractableStartResponseClientRpc(
                    taskType ?? string.Empty,
                    taskKey ?? string.Empty,
                    minigameId ?? string.Empty,
                    false,
                    string.IsNullOrWhiteSpace(blockedReason) ? "Task is unavailable." : blockedReason,
                    0,
                    0,
                    0,
                    0,
                    0,
                    string.Empty,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string normalizedTaskType = NormalizeTaskTypeForSession(taskType);
            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            int cleaningSessionToken = 0;
            int weldingSessionToken = 0;
            int measureCutSessionToken = 0;
            int pipePaintSessionToken = 0;
            int drillScrewSessionToken = 0;
            string canonicalTaskKey = normalizedTaskKey;
            if (string.Equals(normalizedTaskType, CleaningTaskType, StringComparison.Ordinal))
            {
                if (!TryStartCleaningSession(senderClientId, normalizedTaskKey, out CleaningSessionRuntime cleaningSession, out string lockRejectReason))
                {
                    SendJobInteractableStartResponseClientRpc(
                        taskType ?? string.Empty,
                        taskKey ?? string.Empty,
                        minigameId ?? string.Empty,
                        false,
                        string.IsNullOrWhiteSpace(lockRejectReason) ? "Station is currently occupied." : lockRejectReason,
                        0,
                        0,
                        0,
                        0,
                        0,
                        normalizedTaskKey,
                        BuildTargetClientRpcParams(senderClientId));
                    return;
                }

                cleaningSessionToken = cleaningSession.sessionToken;
                canonicalTaskKey = cleaningSession.taskKey;
            }
            else if (string.Equals(normalizedTaskType, WeldingTaskType, StringComparison.Ordinal))
            {
                if (!TryStartWeldingSession(senderClientId, normalizedTaskKey, out WeldingSessionRuntime weldingSession, out string lockRejectReason))
                {
                    SendJobInteractableStartResponseClientRpc(
                        taskType ?? string.Empty,
                        taskKey ?? string.Empty,
                        minigameId ?? string.Empty,
                        false,
                        string.IsNullOrWhiteSpace(lockRejectReason) ? "Station is currently occupied." : lockRejectReason,
                        0,
                        0,
                        0,
                        0,
                        0,
                        normalizedTaskKey,
                        BuildTargetClientRpcParams(senderClientId));
                    return;
                }

                weldingSessionToken = weldingSession.sessionToken;
                canonicalTaskKey = weldingSession.taskKey;
            }
            else if (string.Equals(normalizedTaskType, MeasureCutTaskType, StringComparison.Ordinal))
            {
                if (!TryStartMeasureCutSession(senderClientId, normalizedTaskKey, out MeasureCutSessionRuntime measureCutSession, out string lockRejectReason))
                {
                    SendJobInteractableStartResponseClientRpc(
                        taskType ?? string.Empty,
                        taskKey ?? string.Empty,
                        minigameId ?? string.Empty,
                        false,
                        string.IsNullOrWhiteSpace(lockRejectReason) ? "Station is currently occupied." : lockRejectReason,
                        0,
                        0,
                        0,
                        0,
                        0,
                        normalizedTaskKey,
                        BuildTargetClientRpcParams(senderClientId));
                    return;
                }

                measureCutSessionToken = measureCutSession.sessionToken;
                canonicalTaskKey = measureCutSession.taskKey;
            }
            else if (string.Equals(normalizedTaskType, PipePaintTaskType, StringComparison.Ordinal))
            {
                if (!TryStartPipePaintSession(senderClientId, normalizedTaskKey, out PipePaintSessionRuntime pipePaintSession, out string lockRejectReason))
                {
                    SendJobInteractableStartResponseClientRpc(
                        taskType ?? string.Empty,
                        taskKey ?? string.Empty,
                        minigameId ?? string.Empty,
                        false,
                        string.IsNullOrWhiteSpace(lockRejectReason) ? "Station is currently occupied." : lockRejectReason,
                        0,
                        0,
                        0,
                        0,
                        0,
                        normalizedTaskKey,
                        BuildTargetClientRpcParams(senderClientId));
                    return;
                }

                pipePaintSessionToken = pipePaintSession.sessionToken;
                canonicalTaskKey = pipePaintSession.taskKey;
            }
            else if (string.Equals(normalizedTaskType, DrillScrewTaskType, StringComparison.Ordinal))
            {
                if (!TryStartDrillScrewSession(senderClientId, normalizedTaskKey, out DrillScrewSessionRuntime drillScrewSession, out string lockRejectReason))
                {
                    SendJobInteractableStartResponseClientRpc(
                        taskType ?? string.Empty,
                        taskKey ?? string.Empty,
                        minigameId ?? string.Empty,
                        false,
                        string.IsNullOrWhiteSpace(lockRejectReason) ? "Station is currently occupied." : lockRejectReason,
                        0,
                        0,
                        0,
                        0,
                        0,
                        normalizedTaskKey,
                        BuildTargetClientRpcParams(senderClientId));
                    return;
                }

                drillScrewSessionToken = drillScrewSession.sessionToken;
                canonicalTaskKey = drillScrewSession.taskKey;
            }

            gameManager.RegisterDailyTaskLaunchContext(normalizedTaskType, normalizedTaskKey);
            SyncFromGameManager(forceTaskSnapshot: true);

            if (_enableLogs)
            {
                Game.Core.DevelopmentDiagnostics.Log($"[NetworkSessionProgressAuthority] Job start approved for client {senderClientId}: {taskType}:{taskKey} ({minigameId}).");
            }

            SendJobInteractableStartResponseClientRpc(
                taskType ?? string.Empty,
                taskKey ?? string.Empty,
                minigameId ?? string.Empty,
                true,
                string.Empty,
                cleaningSessionToken,
                weldingSessionToken,
                measureCutSessionToken,
                pipePaintSessionToken,
                drillScrewSessionToken,
                canonicalTaskKey,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteResolveCleaningResult(
            ulong senderClientId,
            int sessionToken,
            string taskKey,
            int resultValue)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;
            if (sessionToken <= 0 || string.IsNullOrEmpty(normalizedTaskKey) || parsedResult == MinigameResult.None)
            {
                SendCleaningResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Invalid cleaning result payload.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!_cleaningSessionsByToken.TryGetValue(sessionToken, out CleaningSessionRuntime session) || session == null)
            {
                SendCleaningResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Cleaning session not found.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!string.Equals(session.taskKey, normalizedTaskKey, StringComparison.Ordinal))
            {
                SendCleaningResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Cleaning task key mismatch.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (session.status != CleaningSessionStatus.Pending)
            {
                SendCleaningResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Cleaning session already resolved.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string authoritativeOwnerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            if (!string.Equals(session.ownerKey, authoritativeOwnerKey, StringComparison.Ordinal)
                || session.ownerClientId != senderClientId)
            {
                SendCleaningResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Sender does not own this cleaning session.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            session.status = CleaningSessionStatus.Resolved;
            session.resolvedResult = parsedResult;
            session.resolvedAt = Time.unscaledTime;
            _activeCleaningSessionsByTaskKey.Remove(session.taskKey);

            if (parsedResult == MinigameResult.Pass)
            {
                GameManager gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.RegisterDailyTaskLaunchContext(CleaningTaskType, session.taskKey);
                }

                MinigameRewardSystem.DistributeNetworkCleaningSessionReward(
                    parsedResult,
                    authoritativeOwnerKey,
                    sessionToken,
                    CleaningSessionRewardDedupeScope);
                SyncFromGameManager(forceTaskSnapshot: true);
            }

            SendCleaningResultResolutionResponseClientRpc(
                sessionToken,
                session.taskKey,
                true,
                string.Empty,
                (int)parsedResult,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteResolveWeldingResult(
            ulong senderClientId,
            int sessionToken,
            string taskKey,
            int resultValue)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;
            if (sessionToken <= 0 || string.IsNullOrEmpty(normalizedTaskKey) || parsedResult == MinigameResult.None)
            {
                SendWeldingResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Invalid welding result payload.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!_weldingSessionsByToken.TryGetValue(sessionToken, out WeldingSessionRuntime session) || session == null)
            {
                SendWeldingResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Welding session not found.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!string.Equals(session.taskKey, normalizedTaskKey, StringComparison.Ordinal))
            {
                SendWeldingResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Welding task key mismatch.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (session.status != WeldingSessionStatus.Pending)
            {
                SendWeldingResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Welding session already resolved.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string authoritativeOwnerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            if (!string.Equals(session.ownerKey, authoritativeOwnerKey, StringComparison.Ordinal)
                || session.ownerClientId != senderClientId)
            {
                SendWeldingResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Sender does not own this welding session.",
                    (int)parsedResult,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            session.status = WeldingSessionStatus.Resolved;
            session.resolvedResult = parsedResult;
            session.resolvedAt = Time.unscaledTime;
            _activeWeldingSessionsByTaskKey.Remove(session.taskKey);

            if (parsedResult == MinigameResult.Pass)
            {
                GameManager gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.RegisterDailyTaskLaunchContext(WeldingTaskType, session.taskKey);
                }

                MinigameRewardSystem.DistributeNetworkWeldingSessionReward(
                    parsedResult,
                    authoritativeOwnerKey,
                    sessionToken,
                    WeldingSessionRewardDedupeScope);
                SyncFromGameManager(forceTaskSnapshot: true);
            }

            SendWeldingResultResolutionResponseClientRpc(
                sessionToken,
                session.taskKey,
                true,
                string.Empty,
                (int)parsedResult,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteResolveMeasureCutResult(
            ulong senderClientId,
            int sessionToken,
            string taskKey,
            int resultValue,
            float qualityScore)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;
            float sanitizedQualityScore = Mathf.Clamp(qualityScore, 0f, 100f);
            if (sessionToken <= 0 || string.IsNullOrEmpty(normalizedTaskKey) || parsedResult == MinigameResult.None)
            {
                SendMeasureCutResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Invalid measure/cut result payload.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!_measureCutSessionsByToken.TryGetValue(sessionToken, out MeasureCutSessionRuntime session) || session == null)
            {
                SendMeasureCutResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Measure/cut session not found.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!string.Equals(session.taskKey, normalizedTaskKey, StringComparison.Ordinal))
            {
                SendMeasureCutResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Measure/cut task key mismatch.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (session.status != MeasureCutSessionStatus.Pending)
            {
                SendMeasureCutResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Measure/cut session already resolved.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string authoritativeOwnerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            if (!string.Equals(session.ownerKey, authoritativeOwnerKey, StringComparison.Ordinal)
                || session.ownerClientId != senderClientId)
            {
                SendMeasureCutResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Sender does not own this measure/cut session.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            session.status = MeasureCutSessionStatus.Resolved;
            session.resolvedResult = parsedResult;
            session.resolvedAt = Time.unscaledTime;
            session.resolvedQualityScore = sanitizedQualityScore;
            _activeMeasureCutSessionsByTaskKey.Remove(session.taskKey);

            if (parsedResult == MinigameResult.Pass)
            {
                GameManager gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.RegisterDailyTaskLaunchContext(MeasureCutTaskType, session.taskKey);
                }

                MinigameRewardSystem.DistributeNetworkMeasureCutSessionReward(
                    parsedResult,
                    authoritativeOwnerKey,
                    sessionToken,
                    MeasureCutSessionRewardDedupeScope,
                    sanitizedQualityScore);
                SyncFromGameManager(forceTaskSnapshot: true);
            }

            SendMeasureCutResultResolutionResponseClientRpc(
                sessionToken,
                session.taskKey,
                true,
                string.Empty,
                (int)parsedResult,
                sanitizedQualityScore,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteResolvePipePaintResult(
            ulong senderClientId,
            int sessionToken,
            string taskKey,
            int resultValue,
            float coverageScore)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;
            float sanitizedCoverageScore = Mathf.Clamp(coverageScore, 0f, 100f);
            if (sessionToken <= 0 || string.IsNullOrEmpty(normalizedTaskKey) || parsedResult == MinigameResult.None)
            {
                SendPipePaintResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Invalid pipe paint result payload.",
                    (int)parsedResult,
                    sanitizedCoverageScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!_pipePaintSessionsByToken.TryGetValue(sessionToken, out PipePaintSessionRuntime session) || session == null)
            {
                SendPipePaintResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Pipe paint session not found.",
                    (int)parsedResult,
                    sanitizedCoverageScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!string.Equals(session.taskKey, normalizedTaskKey, StringComparison.Ordinal))
            {
                SendPipePaintResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Pipe paint task key mismatch.",
                    (int)parsedResult,
                    sanitizedCoverageScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (session.status != PipePaintSessionStatus.Pending)
            {
                SendPipePaintResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Pipe paint session already resolved.",
                    (int)parsedResult,
                    sanitizedCoverageScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string authoritativeOwnerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            if (!string.Equals(session.ownerKey, authoritativeOwnerKey, StringComparison.Ordinal)
                || session.ownerClientId != senderClientId)
            {
                SendPipePaintResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Sender does not own this pipe paint session.",
                    (int)parsedResult,
                    sanitizedCoverageScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            session.status = PipePaintSessionStatus.Resolved;
            session.resolvedResult = parsedResult;
            session.resolvedAt = Time.unscaledTime;
            session.resolvedCoverageScore = sanitizedCoverageScore;
            _activePipePaintSessionsByTaskKey.Remove(session.taskKey);

            if (parsedResult == MinigameResult.Pass)
            {
                GameManager gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.RegisterDailyTaskLaunchContext(PipePaintTaskType, session.taskKey);
                }

                MinigameRewardSystem.DistributeNetworkPipePaintSessionReward(
                    parsedResult,
                    authoritativeOwnerKey,
                    sessionToken,
                    PipePaintSessionRewardDedupeScope,
                    sanitizedCoverageScore);
                SyncFromGameManager(forceTaskSnapshot: true);
            }

            SendPipePaintResultResolutionResponseClientRpc(
                sessionToken,
                session.taskKey,
                true,
                string.Empty,
                (int)parsedResult,
                sanitizedCoverageScore,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteResolveDrillScrewResult(
            ulong senderClientId,
            int sessionToken,
            string taskKey,
            int resultValue,
            float qualityScore)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            string normalizedTaskKey = NormalizeTaskKeyForSession(taskKey);
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;
            float sanitizedQualityScore = Mathf.Clamp(qualityScore, 0f, 100f);
            if (sessionToken <= 0 || string.IsNullOrEmpty(normalizedTaskKey) || parsedResult == MinigameResult.None)
            {
                SendDrillScrewResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Invalid drill/screw result payload.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!_drillScrewSessionsByToken.TryGetValue(sessionToken, out DrillScrewSessionRuntime session) || session == null)
            {
                SendDrillScrewResultResolutionResponseClientRpc(
                    sessionToken,
                    normalizedTaskKey,
                    false,
                    "Drill/screw session not found.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string authoritativeOwnerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            if (session.ownerClientId != senderClientId
                || !string.Equals(session.ownerKey, authoritativeOwnerKey, StringComparison.Ordinal)
                || !string.Equals(session.taskKey, normalizedTaskKey, StringComparison.Ordinal))
            {
                SendDrillScrewResultResolutionResponseClientRpc(
                    sessionToken,
                    session.taskKey,
                    false,
                    "Drill/screw session ownership mismatch.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (session.status != DrillScrewSessionStatus.Pending)
            {
                SendDrillScrewResultResolutionResponseClientRpc(
                    sessionToken,
                    session.taskKey,
                    false,
                    "Drill/screw session already resolved.",
                    (int)parsedResult,
                    sanitizedQualityScore,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            session.status = parsedResult == MinigameResult.Cancelled ? DrillScrewSessionStatus.Aborted : DrillScrewSessionStatus.Resolved;
            session.resolvedResult = parsedResult;
            session.resolvedAt = Time.unscaledTime;
            session.resolvedQualityScore = sanitizedQualityScore;
            _drillScrewSessionsByToken.Remove(sessionToken);
            _activeDrillScrewSessionsByTaskKey.Remove(session.taskKey);

            if (parsedResult == MinigameResult.Pass)
            {
                GameManager gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.RegisterDailyTaskLaunchContext(DrillScrewTaskType, session.taskKey);
                }

                MinigameRewardSystem.DistributeNetworkDrillScrewSessionReward(
                    parsedResult,
                    authoritativeOwnerKey,
                    sessionToken,
                    DrillScrewSessionRewardDedupeScope,
                    sanitizedQualityScore);
                SyncFromGameManager(forceTaskSnapshot: true);
            }

            SendDrillScrewResultResolutionResponseClientRpc(
                sessionToken,
                session.taskKey,
                true,
                string.Empty,
                (int)parsedResult,
                sanitizedQualityScore,
                BuildTargetClientRpcParams(senderClientId));
        }

        private void ExecuteMinigameRewardClaim(
            MinigameResult result,
            string minigameId,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope,
            ulong senderClientId)
        {
            string normalizedMinigameId = NormalizeMinigameId(minigameId);
            string claimDedupeKey = BuildRewardClaimDedupeKey(senderClientId, dedupeLifecycleScope, sessionToken, normalizedMinigameId);
            if (string.IsNullOrEmpty(claimDedupeKey) || !_processedRewardClaimKeys.Add(claimDedupeKey))
            {
                if (_enableLogs)
                {
                    Game.Core.DevelopmentDiagnostics.Log($"[NetworkSessionProgressAuthority] Ignored duplicate minigame reward claim: {claimDedupeKey}");
                }
                return;
            }

            string authoritativeOwnerPlayerId = ResolveOwnerPlayerIdFromSender(senderClientId);
            MinigameRewardSystem.DistributeRewards(result, normalizedMinigameId, authoritativeOwnerPlayerId, sessionToken, dedupeLifecycleScope);
            SyncFromGameManager(forceTaskSnapshot: true);
        }

        private bool ServerRegisterNpcCatch(
            ulong targetClientId,
            string ownerKey,
            ulong catchToken,
            ulong npcNetworkObjectId,
            float serverTime,
            bool pendingLie)
        {
            if (!IsServer || !IsSpawned)
            {
                return false;
            }

            string normalizedOwner = NetworkOwnerKeyUtility.NormalizeOwnerKey(ownerKey);
            if (targetClientId == NetworkNpcAuthorityBridge.NoTargetClientId || catchToken == 0UL || string.IsNullOrEmpty(normalizedOwner))
            {
                return false;
            }

            string dedupeKey = BuildNpcCatchDedupeKey(normalizedOwner, catchToken);
            if (!_processedNpcCatchKeys.Add(dedupeKey))
            {
                if (_enableLogs)
                {
                    Game.Core.DevelopmentDiagnostics.Log($"[NetworkSessionProgressAuthority] Ignored duplicate NPC catch registration: {dedupeKey}");
                }
                return false;
            }

            SendNpcCatchTriggeredClientRpc(
                normalizedOwner,
                targetClientId,
                catchToken,
                npcNetworkObjectId,
                Mathf.Max(0f, serverTime),
                pendingLie,
                BuildTargetClientRpcParams(targetClientId));
            return true;
        }

        private void ExecuteResolveNpcLieResult(
            ulong senderClientId,
            ulong catchToken,
            ulong npcNetworkObjectId,
            int resultValue)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;
            if (parsedResult == MinigameResult.None)
            {
                SendNpcLieResolutionResponseClientRpc(
                    catchToken,
                    false,
                    "Invalid lie result.",
                    (int)MinigameResult.None,
                    false,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            string ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId);
            string resolutionKey = BuildNpcCatchDedupeKey(ownerKey, catchToken);
            if (string.IsNullOrWhiteSpace(resolutionKey) || !_processedNpcLieResolutionKeys.Add(resolutionKey))
            {
                SendNpcLieResolutionResponseClientRpc(
                    catchToken,
                    false,
                    "Catch result already resolved.",
                    (int)parsedResult,
                    false,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null
                || !manager.IsListening
                || !manager.SpawnManager.SpawnedObjects.TryGetValue(npcNetworkObjectId, out NetworkObject npcNetworkObject)
                || npcNetworkObject == null
                || !npcNetworkObject.IsSpawned)
            {
                SendNpcLieResolutionResponseClientRpc(
                    catchToken,
                    false,
                    "NPC authority is unavailable.",
                    (int)parsedResult,
                    false,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            NPCController npcController = npcNetworkObject.GetComponent<NPCController>();
            if (npcController == null)
            {
                SendNpcLieResolutionResponseClientRpc(
                    catchToken,
                    false,
                    "NPC authority is unavailable.",
                    (int)parsedResult,
                    false,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            bool accepted = npcController.TryResolveAuthoritativeLieResult(
                senderClientId,
                catchToken,
                parsedResult,
                out bool appliedConsequence,
                out string rejectionReason);

            if (accepted)
            {
                SyncFromGameManager(forceTaskSnapshot: true);
            }

            SendNpcLieResolutionResponseClientRpc(
                catchToken,
                accepted,
                accepted ? string.Empty : (string.IsNullOrWhiteSpace(rejectionReason) ? "Catch result rejected." : rejectionReason),
                (int)parsedResult,
                accepted && appliedConsequence,
                BuildTargetClientRpcParams(senderClientId));
        }

        [ClientRpc]
        private void SendJobInteractableStartResponseClientRpc(
            string taskType,
            string taskKey,
            string minigameId,
            bool approved,
            string reason,
            int cleaningSessionToken,
            int weldingSessionToken,
            int measureCutSessionToken,
            int pipePaintSessionToken,
            int drillScrewSessionToken,
            string canonicalTaskKey,
            ClientRpcParams clientRpcParams = default)
        {
            JobInteractableStartResponse response = new JobInteractableStartResponse
            {
                taskType = taskType,
                taskKey = taskKey,
                minigameId = minigameId,
                approved = approved,
                reason = reason,
                cleaningSessionToken = cleaningSessionToken,
                weldingSessionToken = weldingSessionToken,
                measureCutSessionToken = measureCutSessionToken,
                pipePaintSessionToken = pipePaintSessionToken,
                drillScrewSessionToken = drillScrewSessionToken,
                canonicalTaskKey = canonicalTaskKey
            };

            OnJobInteractableStartResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendCleaningResultResolutionResponseClientRpc(
            int sessionToken,
            string taskKey,
            bool accepted,
            string reason,
            int resultValue,
            ClientRpcParams clientRpcParams = default)
        {
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;

            CleaningResultResolutionResponse response = new CleaningResultResolutionResponse
            {
                sessionToken = sessionToken,
                taskKey = taskKey,
                accepted = accepted,
                reason = reason,
                result = parsedResult
            };

            OnCleaningResultResolutionResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendWeldingResultResolutionResponseClientRpc(
            int sessionToken,
            string taskKey,
            bool accepted,
            string reason,
            int resultValue,
            ClientRpcParams clientRpcParams = default)
        {
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;

            WeldingResultResolutionResponse response = new WeldingResultResolutionResponse
            {
                sessionToken = sessionToken,
                taskKey = taskKey,
                accepted = accepted,
                reason = reason,
                result = parsedResult
            };

            OnWeldingResultResolutionResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendMeasureCutResultResolutionResponseClientRpc(
            int sessionToken,
            string taskKey,
            bool accepted,
            string reason,
            int resultValue,
            float qualityScore,
            ClientRpcParams clientRpcParams = default)
        {
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;

            MeasureCutResultResolutionResponse response = new MeasureCutResultResolutionResponse
            {
                sessionToken = sessionToken,
                taskKey = taskKey,
                accepted = accepted,
                reason = reason,
                result = parsedResult,
                qualityScore = Mathf.Clamp(qualityScore, 0f, 100f)
            };

            OnMeasureCutResultResolutionResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendPipePaintResultResolutionResponseClientRpc(
            int sessionToken,
            string taskKey,
            bool accepted,
            string reason,
            int resultValue,
            float coverageScore,
            ClientRpcParams clientRpcParams = default)
        {
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;

            PipePaintResultResolutionResponse response = new PipePaintResultResolutionResponse
            {
                sessionToken = sessionToken,
                taskKey = taskKey,
                accepted = accepted,
                reason = reason,
                result = parsedResult,
                coverageScore = Mathf.Clamp(coverageScore, 0f, 100f)
            };

            OnPipePaintResultResolutionResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendDrillScrewResultResolutionResponseClientRpc(
            int sessionToken,
            string taskKey,
            bool accepted,
            string reason,
            int resultValue,
            float qualityScore,
            ClientRpcParams clientRpcParams = default)
        {
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;

            DrillScrewResultResolutionResponse response = new DrillScrewResultResolutionResponse
            {
                sessionToken = sessionToken,
                taskKey = taskKey,
                accepted = accepted,
                reason = reason,
                result = parsedResult,
                qualityScore = Mathf.Clamp(qualityScore, 0f, 100f)
            };

            OnDrillScrewResultResolutionResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendStolenLootSellItemResponseClientRpc(
            string itemId,
            bool success,
            string reason,
            int payoutAmount,
            int remainingTrackedCount,
            ClientRpcParams clientRpcParams = default)
        {
            StolenLootSellItemResponse response = new StolenLootSellItemResponse
            {
                itemId = itemId,
                success = success,
                reason = reason,
                payoutAmount = payoutAmount,
                remainingTrackedCount = remainingTrackedCount
            };

            OnStolenLootSellItemResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendUpgradePurchaseResponseClientRpc(
            string upgradeId,
            bool success,
            string reason,
            int spentCurrency,
            int resultingTier,
            ClientRpcParams clientRpcParams = default)
        {
            UpgradePurchaseResponse response = new UpgradePurchaseResponse
            {
                upgradeId = upgradeId,
                success = success,
                reason = reason,
                spentCurrency = spentCurrency,
                resultingTier = resultingTier
            };

            OnUpgradePurchaseResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendTrackedLootDropResponseClientRpc(
            ulong requestId,
            string itemId,
            bool success,
            string reason,
            ClientRpcParams clientRpcParams = default)
        {
            TrackedLootDropResponse response = new TrackedLootDropResponse
            {
                requestId = requestId,
                itemId = itemId,
                success = success,
                reason = reason
            };

            OnTrackedLootDropResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendComputerSessionStartResponseClientRpc(
            string stationKey,
            bool approved,
            string reason,
            int computerSessionToken,
            string canonicalStationKey,
            ClientRpcParams clientRpcParams = default)
        {
            ComputerSessionStartResponse response = new ComputerSessionStartResponse
            {
                stationKey = stationKey,
                approved = approved,
                reason = reason,
                computerSessionToken = computerSessionToken,
                canonicalStationKey = canonicalStationKey
            };

            OnComputerSessionStartResponse?.Invoke(response);
        }

        [ClientRpc]
        private void SendNpcCatchTriggeredClientRpc(
            string ownerKey,
            ulong targetClientId,
            ulong catchToken,
            ulong npcNetworkObjectId,
            float serverTime,
            bool pendingLie,
            ClientRpcParams clientRpcParams = default)
        {
            if (!TryConsumeNpcCatch(ownerKey, catchToken))
            {
                return;
            }

            NpcCatchTriggeredResponse response = new NpcCatchTriggeredResponse
            {
                ownerKey = ownerKey,
                targetClientId = targetClientId,
                catchToken = catchToken,
                npcNetworkObjectId = npcNetworkObjectId,
                serverTime = serverTime,
                pendingLie = pendingLie
            };

            OnNpcCatchTriggeredResponse?.Invoke(response);
            TryBeginLocalLieMinigameFromCatch(response);
        }

        private static bool TryConsumeNpcCatch(string ownerKey, ulong catchToken)
        {
            string dedupeKey = BuildNpcCatchDedupeKey(ownerKey, catchToken);
            return !string.IsNullOrWhiteSpace(dedupeKey) && ConsumedNpcCatchKeys.Add(dedupeKey);
        }

        [ClientRpc]
        private void SendNpcLieResolutionResponseClientRpc(
            ulong catchToken,
            bool accepted,
            string reason,
            int resultValue,
            bool appliedConsequence,
            ClientRpcParams clientRpcParams = default)
        {
            MinigameResult parsedResult = Enum.IsDefined(typeof(MinigameResult), resultValue)
                ? (MinigameResult)resultValue
                : MinigameResult.None;
            NpcLieResolutionResponse response = new NpcLieResolutionResponse
            {
                catchToken = catchToken,
                accepted = accepted,
                reason = reason,
                result = parsedResult,
                appliedConsequence = appliedConsequence
            };

            OnNpcLieResolutionResponse?.Invoke(response);

            if (_hasActiveLocalLieCatch && _activeLocalLieCatchToken == catchToken)
            {
                ClearLocalLieCatchSession();
            }
        }

        [ClientRpc]
        private void ReceiveOwnerStolenLootSnapshotClientRpc(
            string ownerKey,
            string snapshotJson,
            ClientRpcParams clientRpcParams = default)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || string.IsNullOrWhiteSpace(snapshotJson))
            {
                return;
            }

            StolenLootSnapshotPayload snapshot = JsonUtility.FromJson<StolenLootSnapshotPayload>(snapshotJson);
            if (snapshot == null)
            {
                return;
            }

            gameManager.RestoreStolenLootThisDayFromSave(snapshot.entries, ownerKey);
            OnOwnerStolenLootSnapshotApplied?.Invoke(ownerKey);
        }

        private void PushOwnerStolenLootSnapshotToClient(ulong targetClientId, string ownerKey)
        {
            if (!IsServer)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            List<StolenLootEntryData> ownerEntries = gameManager.GetStolenLootThisDaySnapshot(ownerKey);
            StolenLootSnapshotPayload payload = new StolenLootSnapshotPayload
            {
                entries = ownerEntries ?? new List<StolenLootEntryData>()
            };

            string payloadJson = JsonUtility.ToJson(payload);
            ReceiveOwnerStolenLootSnapshotClientRpc(
                ownerKey,
                payloadJson,
                BuildTargetClientRpcParams(targetClientId));
        }

        private void HandleServerClientDisconnected(ulong disconnectedClientId)
        {
            if (!IsServer || disconnectedClientId == NetworkManager.ServerClientId)
            {
                return;
            }

            CleanupPendingSessionsForDisconnectedClient(disconnectedClientId);
            NotifyNpcAuthoritiesClientDisconnected(disconnectedClientId);
        }

        private void CleanupPendingSessionsForDisconnectedClient(ulong disconnectedClientId)
        {
            List<string> staleCleaningTaskKeys = null;
            List<int> staleCleaningTokens = null;
            foreach (KeyValuePair<string, CleaningSessionRuntime> entry in _activeCleaningSessionsByTaskKey)
            {
                CleaningSessionRuntime session = entry.Value;
                if (session == null
                    || session.status != CleaningSessionStatus.Pending
                    || session.ownerClientId != disconnectedClientId)
                {
                    continue;
                }

                session.status = CleaningSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = Time.unscaledTime;
                staleCleaningTaskKeys ??= new List<string>();
                staleCleaningTokens ??= new List<int>();
                staleCleaningTaskKeys.Add(entry.Key);
                staleCleaningTokens.Add(session.sessionToken);
            }

            if (staleCleaningTaskKeys != null)
            {
                for (int i = 0; i < staleCleaningTaskKeys.Count; i++)
                {
                    _activeCleaningSessionsByTaskKey.Remove(staleCleaningTaskKeys[i]);
                }
            }

            if (staleCleaningTokens != null)
            {
                for (int i = 0; i < staleCleaningTokens.Count; i++)
                {
                    _cleaningSessionsByToken.Remove(staleCleaningTokens[i]);
                }
            }

            List<string> staleWeldingTaskKeys = null;
            List<int> staleWeldingTokens = null;
            foreach (KeyValuePair<string, WeldingSessionRuntime> entry in _activeWeldingSessionsByTaskKey)
            {
                WeldingSessionRuntime session = entry.Value;
                if (session == null
                    || session.status != WeldingSessionStatus.Pending
                    || session.ownerClientId != disconnectedClientId)
                {
                    continue;
                }

                session.status = WeldingSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = Time.unscaledTime;
                staleWeldingTaskKeys ??= new List<string>();
                staleWeldingTokens ??= new List<int>();
                staleWeldingTaskKeys.Add(entry.Key);
                staleWeldingTokens.Add(session.sessionToken);
            }

            if (staleWeldingTaskKeys != null)
            {
                for (int i = 0; i < staleWeldingTaskKeys.Count; i++)
                {
                    _activeWeldingSessionsByTaskKey.Remove(staleWeldingTaskKeys[i]);
                }
            }

            if (staleWeldingTokens != null)
            {
                for (int i = 0; i < staleWeldingTokens.Count; i++)
                {
                    _weldingSessionsByToken.Remove(staleWeldingTokens[i]);
                }
            }

            List<string> staleMeasureCutTaskKeys = null;
            List<int> staleMeasureCutTokens = null;
            foreach (KeyValuePair<string, MeasureCutSessionRuntime> entry in _activeMeasureCutSessionsByTaskKey)
            {
                MeasureCutSessionRuntime session = entry.Value;
                if (session == null
                    || session.status != MeasureCutSessionStatus.Pending
                    || session.ownerClientId != disconnectedClientId)
                {
                    continue;
                }

                session.status = MeasureCutSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = Time.unscaledTime;
                staleMeasureCutTaskKeys ??= new List<string>();
                staleMeasureCutTokens ??= new List<int>();
                staleMeasureCutTaskKeys.Add(entry.Key);
                staleMeasureCutTokens.Add(session.sessionToken);
            }

            if (staleMeasureCutTaskKeys != null)
            {
                for (int i = 0; i < staleMeasureCutTaskKeys.Count; i++)
                {
                    _activeMeasureCutSessionsByTaskKey.Remove(staleMeasureCutTaskKeys[i]);
                }
            }

            if (staleMeasureCutTokens != null)
            {
                for (int i = 0; i < staleMeasureCutTokens.Count; i++)
                {
                    _measureCutSessionsByToken.Remove(staleMeasureCutTokens[i]);
                }
            }

            List<string> stalePipePaintTaskKeys = null;
            List<int> stalePipePaintTokens = null;
            foreach (KeyValuePair<string, PipePaintSessionRuntime> entry in _activePipePaintSessionsByTaskKey)
            {
                PipePaintSessionRuntime session = entry.Value;
                if (session == null
                    || session.status != PipePaintSessionStatus.Pending
                    || session.ownerClientId != disconnectedClientId)
                {
                    continue;
                }

                session.status = PipePaintSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = Time.unscaledTime;
                stalePipePaintTaskKeys ??= new List<string>();
                stalePipePaintTokens ??= new List<int>();
                stalePipePaintTaskKeys.Add(entry.Key);
                stalePipePaintTokens.Add(session.sessionToken);
            }

            if (stalePipePaintTaskKeys != null)
            {
                for (int i = 0; i < stalePipePaintTaskKeys.Count; i++)
                {
                    _activePipePaintSessionsByTaskKey.Remove(stalePipePaintTaskKeys[i]);
                }
            }

            if (stalePipePaintTokens != null)
            {
                for (int i = 0; i < stalePipePaintTokens.Count; i++)
                {
                    _pipePaintSessionsByToken.Remove(stalePipePaintTokens[i]);
                }
            }

            List<string> staleDrillScrewTaskKeys = null;
            List<int> staleDrillScrewTokens = null;
            foreach (KeyValuePair<string, DrillScrewSessionRuntime> entry in _activeDrillScrewSessionsByTaskKey)
            {
                DrillScrewSessionRuntime session = entry.Value;
                if (session == null
                    || session.status != DrillScrewSessionStatus.Pending
                    || session.ownerClientId != disconnectedClientId)
                {
                    continue;
                }

                session.status = DrillScrewSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = Time.unscaledTime;
                staleDrillScrewTaskKeys ??= new List<string>();
                staleDrillScrewTokens ??= new List<int>();
                staleDrillScrewTaskKeys.Add(entry.Key);
                staleDrillScrewTokens.Add(session.sessionToken);
            }

            if (staleDrillScrewTaskKeys != null)
            {
                for (int i = 0; i < staleDrillScrewTaskKeys.Count; i++)
                {
                    _activeDrillScrewSessionsByTaskKey.Remove(staleDrillScrewTaskKeys[i]);
                }
            }

            if (staleDrillScrewTokens != null)
            {
                for (int i = 0; i < staleDrillScrewTokens.Count; i++)
                {
                    _drillScrewSessionsByToken.Remove(staleDrillScrewTokens[i]);
                }
            }

            List<string> staleComputerStationKeys = null;
            List<int> staleComputerTokens = null;
            foreach (KeyValuePair<string, ComputerSessionRuntime> entry in _activeComputerSessionsByStationKey)
            {
                ComputerSessionRuntime session = entry.Value;
                if (session == null
                    || session.status != ComputerSessionStatus.Pending
                    || session.ownerClientId != disconnectedClientId)
                {
                    continue;
                }

                session.status = ComputerSessionStatus.Aborted;
                session.resolvedAt = Time.unscaledTime;
                staleComputerStationKeys ??= new List<string>();
                staleComputerTokens ??= new List<int>();
                staleComputerStationKeys.Add(entry.Key);
                staleComputerTokens.Add(session.sessionToken);
            }

            if (staleComputerStationKeys != null)
            {
                for (int i = 0; i < staleComputerStationKeys.Count; i++)
                {
                    _activeComputerSessionsByStationKey.Remove(staleComputerStationKeys[i]);
                }
            }

            if (staleComputerTokens != null)
            {
                for (int i = 0; i < staleComputerTokens.Count; i++)
                {
                    _computerSessionsByToken.Remove(staleComputerTokens[i]);
                }
            }
        }

        private static void NotifyNpcAuthoritiesClientDisconnected(ulong disconnectedClientId)
        {
            NPCController[] npcs = FindObjectsByType<NPCController>(FindObjectsInactive.Exclude);
            for (int i = 0; i < npcs.Length; i++)
            {
                NPCController npc = npcs[i];
                if (npc == null)
                {
                    continue;
                }

                npc.HandleAuthoritativeClientDisconnect(disconnectedClientId);
            }
        }

        private static ClientRpcParams BuildTargetClientRpcParams(ulong targetClientId)
        {
            return new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { targetClientId }
                }
            };
        }

        private bool IsAuthoritativePublisher()
        {
            return IsServer && IsOwner && ReferenceEquals(_authoritativePublisher, this);
        }

        private bool ShouldConsumeAsClientMirror()
        {
            if (IsServer)
            {
                return false;
            }

            NetworkManager manager = NetworkManager;
            if (manager == null || !manager.IsListening)
            {
                return false;
            }

            return OwnerClientId == NetworkManager.ServerClientId;
        }

        private static string NormalizeMinigameId(string minigameId)
        {
            return string.IsNullOrWhiteSpace(minigameId)
                ? string.Empty
                : minigameId.Trim().ToLowerInvariant();
        }

        private static string BuildRewardClaimDedupeKey(
            ulong senderClientId,
            int dedupeLifecycleScope,
            int sessionToken,
            string normalizedMinigameId)
        {
            if (sessionToken <= 0 || string.IsNullOrEmpty(normalizedMinigameId))
            {
                return string.Empty;
            }

            return $"{senderClientId}:{dedupeLifecycleScope}:{sessionToken}:{normalizedMinigameId}";
        }

        private static string BuildNpcCatchDedupeKey(string ownerKey, ulong catchToken)
        {
            if (string.IsNullOrWhiteSpace(ownerKey) || catchToken == 0UL)
            {
                return string.Empty;
            }

            return $"{ownerKey.Trim()}:{catchToken}";
        }

        private static string ResolveOwnerPlayerIdFromSender(ulong senderClientId)
        {
            return NetworkOwnerKeyUtility.GetOwnerKeyForSender(senderClientId);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void ValidateCriticalResourcePathsOnce()
        {
            if (_hasValidatedCriticalResourcePaths)
            {
                return;
            }

            _hasValidatedCriticalResourcePaths = true;
            if (Resources.Load<GameObject>(GoldRingDropPrefabResourcesPath) == null)
            {
                Debug.LogWarning(
                    $"[NetworkSessionProgressAuthority] Missing resource at '{GoldRingDropPrefabResourcesPath}'. " +
                    "Tracked network drop for gold ring will fail.");
            }

            if (Resources.Load<GameObject>(SilverRingDropPrefabResourcesPath) == null)
            {
                Debug.LogWarning(
                    $"[NetworkSessionProgressAuthority] Missing resource at '{SilverRingDropPrefabResourcesPath}'. " +
                    "Tracked network drop for silver ring will fail.");
            }
        }

        private static bool TryResolveTrackedLootNetworkDropPrefab(string itemId, out GameObject prefab)
        {
            prefab = null;
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            string normalizedItemId = itemId.Trim();
            if (string.Equals(normalizedItemId, GoldRingItemId, StringComparison.Ordinal))
            {
                _cachedGoldRingDropNetworkPrefab ??= Resources.Load<GameObject>(GoldRingDropPrefabResourcesPath);
                prefab = _cachedGoldRingDropNetworkPrefab;
                return prefab != null;
            }

            if (string.Equals(normalizedItemId, SilverRingItemId, StringComparison.Ordinal))
            {
                _cachedSilverRingDropNetworkPrefab ??= Resources.Load<GameObject>(SilverRingDropPrefabResourcesPath);
                prefab = _cachedSilverRingDropNetworkPrefab;
                return prefab != null;
            }

            return false;
        }

        private bool TryStartCleaningSession(
            ulong senderClientId,
            string normalizedTaskKey,
            out CleaningSessionRuntime session,
            out string reason)
        {
            session = null;
            reason = string.Empty;
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                reason = "Cleaning station unavailable.";
                return false;
            }

            if (_activeCleaningSessionsByTaskKey.TryGetValue(normalizedTaskKey, out CleaningSessionRuntime active)
                && active != null
                && active.status == CleaningSessionStatus.Pending)
            {
                reason = "This cleaning station is currently occupied.";
                return false;
            }

            int token = ++_cleaningSessionSequence;
            if (token <= 0)
            {
                _cleaningSessionSequence = 1;
                token = _cleaningSessionSequence;
            }

            session = new CleaningSessionRuntime
            {
                sessionToken = token,
                taskKey = normalizedTaskKey,
                ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId),
                ownerClientId = senderClientId,
                startedAt = Time.unscaledTime,
                status = CleaningSessionStatus.Pending,
                resolvedResult = MinigameResult.None,
                resolvedAt = 0f
            };

            _activeCleaningSessionsByTaskKey[normalizedTaskKey] = session;
            _cleaningSessionsByToken[token] = session;
            return true;
        }

        private bool TryStartWeldingSession(
            ulong senderClientId,
            string normalizedTaskKey,
            out WeldingSessionRuntime session,
            out string reason)
        {
            session = null;
            reason = string.Empty;
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                reason = "Welding station unavailable.";
                return false;
            }

            if (_activeWeldingSessionsByTaskKey.TryGetValue(normalizedTaskKey, out WeldingSessionRuntime active)
                && active != null
                && active.status == WeldingSessionStatus.Pending)
            {
                reason = "This welding station is currently occupied.";
                return false;
            }

            int token = ++_weldingSessionSequence;
            if (token <= 0)
            {
                _weldingSessionSequence = 1;
                token = _weldingSessionSequence;
            }

            session = new WeldingSessionRuntime
            {
                sessionToken = token,
                taskKey = normalizedTaskKey,
                ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId),
                ownerClientId = senderClientId,
                startedAt = Time.unscaledTime,
                status = WeldingSessionStatus.Pending,
                resolvedResult = MinigameResult.None,
                resolvedAt = 0f
            };

            _activeWeldingSessionsByTaskKey[normalizedTaskKey] = session;
            _weldingSessionsByToken[token] = session;
            return true;
        }

        private bool TryStartMeasureCutSession(
            ulong senderClientId,
            string normalizedTaskKey,
            out MeasureCutSessionRuntime session,
            out string reason)
        {
            session = null;
            reason = string.Empty;
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                reason = "Measure/cut station unavailable.";
                return false;
            }

            if (_activeMeasureCutSessionsByTaskKey.TryGetValue(normalizedTaskKey, out MeasureCutSessionRuntime active)
                && active != null
                && active.status == MeasureCutSessionStatus.Pending)
            {
                reason = "This measure/cut station is currently occupied.";
                return false;
            }

            int token = ++_measureCutSessionSequence;
            if (token <= 0)
            {
                _measureCutSessionSequence = 1;
                token = _measureCutSessionSequence;
            }

            session = new MeasureCutSessionRuntime
            {
                sessionToken = token,
                taskKey = normalizedTaskKey,
                ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId),
                ownerClientId = senderClientId,
                startedAt = Time.unscaledTime,
                status = MeasureCutSessionStatus.Pending,
                resolvedResult = MinigameResult.None,
                resolvedAt = 0f
            };

            _activeMeasureCutSessionsByTaskKey[normalizedTaskKey] = session;
            _measureCutSessionsByToken[token] = session;
            return true;
        }

        private bool TryStartPipePaintSession(
            ulong senderClientId,
            string normalizedTaskKey,
            out PipePaintSessionRuntime session,
            out string reason)
        {
            session = null;
            reason = string.Empty;
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                reason = "Pipe paint station unavailable.";
                return false;
            }

            if (_activePipePaintSessionsByTaskKey.TryGetValue(normalizedTaskKey, out PipePaintSessionRuntime active)
                && active != null
                && active.status == PipePaintSessionStatus.Pending)
            {
                reason = "This pipe paint station is currently occupied.";
                return false;
            }

            int token = ++_pipePaintSessionSequence;
            if (token <= 0)
            {
                _pipePaintSessionSequence = 1;
                token = _pipePaintSessionSequence;
            }

            session = new PipePaintSessionRuntime
            {
                sessionToken = token,
                taskKey = normalizedTaskKey,
                ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId),
                ownerClientId = senderClientId,
                startedAt = Time.unscaledTime,
                status = PipePaintSessionStatus.Pending,
                resolvedResult = MinigameResult.None,
                resolvedAt = 0f
            };

            _activePipePaintSessionsByTaskKey[normalizedTaskKey] = session;
            _pipePaintSessionsByToken[token] = session;
            return true;
        }

        private bool TryStartDrillScrewSession(
            ulong senderClientId,
            string normalizedTaskKey,
            out DrillScrewSessionRuntime session,
            out string reason)
        {
            session = null;
            reason = string.Empty;
            if (string.IsNullOrEmpty(normalizedTaskKey))
            {
                reason = "Drill/screw station unavailable.";
                return false;
            }

            if (_activeDrillScrewSessionsByTaskKey.TryGetValue(normalizedTaskKey, out DrillScrewSessionRuntime active)
                && active != null
                && active.status == DrillScrewSessionStatus.Pending)
            {
                reason = "This drill/screw station is currently occupied.";
                return false;
            }

            int token = ++_drillScrewSessionSequence;
            if (token <= 0)
            {
                _drillScrewSessionSequence = 1;
                token = _drillScrewSessionSequence;
            }

            session = new DrillScrewSessionRuntime
            {
                sessionToken = token,
                taskKey = normalizedTaskKey,
                ownerKey = ResolveOwnerPlayerIdFromSender(senderClientId),
                ownerClientId = senderClientId,
                startedAt = Time.unscaledTime,
                status = DrillScrewSessionStatus.Pending,
                resolvedResult = MinigameResult.None,
                resolvedAt = 0f
            };

            _activeDrillScrewSessionsByTaskKey[normalizedTaskKey] = session;
            _drillScrewSessionsByToken[token] = session;
            return true;
        }

        private void CleanupStaleCleaningSessions()
        {
            if (!IsServer || _activeCleaningSessionsByTaskKey.Count == 0)
            {
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            float now = Time.unscaledTime;
            float timeout = Mathf.Max(5f, _cleaningSessionTimeoutSeconds);
            List<string> staleTaskKeys = null;
            List<int> staleTokens = null;

            foreach (KeyValuePair<string, CleaningSessionRuntime> entry in _activeCleaningSessionsByTaskKey)
            {
                CleaningSessionRuntime session = entry.Value;
                if (session == null || session.status != CleaningSessionStatus.Pending)
                {
                    continue;
                }

                bool ownerDisconnected = manager == null
                    || !manager.IsListening
                    || !manager.ConnectedClients.ContainsKey(session.ownerClientId);
                bool timedOut = now - session.startedAt >= timeout;
                if (!ownerDisconnected && !timedOut)
                {
                    continue;
                }

                session.status = CleaningSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = now;

                staleTaskKeys ??= new List<string>();
                staleTokens ??= new List<int>();
                staleTaskKeys.Add(entry.Key);
                staleTokens.Add(session.sessionToken);
            }

            if (staleTaskKeys == null)
            {
                return;
            }

            for (int i = 0; i < staleTaskKeys.Count; i++)
            {
                _activeCleaningSessionsByTaskKey.Remove(staleTaskKeys[i]);
            }

            for (int i = 0; i < staleTokens.Count; i++)
            {
                _cleaningSessionsByToken.Remove(staleTokens[i]);
            }
        }

        private void CleanupStaleWeldingSessions()
        {
            if (!IsServer || _activeWeldingSessionsByTaskKey.Count == 0)
            {
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            float now = Time.unscaledTime;
            float timeout = Mathf.Max(5f, _cleaningSessionTimeoutSeconds);
            List<string> staleTaskKeys = null;
            List<int> staleTokens = null;

            foreach (KeyValuePair<string, WeldingSessionRuntime> entry in _activeWeldingSessionsByTaskKey)
            {
                WeldingSessionRuntime session = entry.Value;
                if (session == null || session.status != WeldingSessionStatus.Pending)
                {
                    continue;
                }

                bool ownerDisconnected = manager == null
                    || !manager.IsListening
                    || !manager.ConnectedClients.ContainsKey(session.ownerClientId);
                bool timedOut = now - session.startedAt >= timeout;
                if (!ownerDisconnected && !timedOut)
                {
                    continue;
                }

                session.status = WeldingSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = now;

                staleTaskKeys ??= new List<string>();
                staleTokens ??= new List<int>();
                staleTaskKeys.Add(entry.Key);
                staleTokens.Add(session.sessionToken);
            }

            if (staleTaskKeys == null)
            {
                return;
            }

            for (int i = 0; i < staleTaskKeys.Count; i++)
            {
                _activeWeldingSessionsByTaskKey.Remove(staleTaskKeys[i]);
            }

            for (int i = 0; i < staleTokens.Count; i++)
            {
                _weldingSessionsByToken.Remove(staleTokens[i]);
            }
        }

        private void CleanupStaleMeasureCutSessions()
        {
            if (!IsServer || _activeMeasureCutSessionsByTaskKey.Count == 0)
            {
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            float now = Time.unscaledTime;
            float timeout = Mathf.Max(5f, _cleaningSessionTimeoutSeconds);
            List<string> staleTaskKeys = null;
            List<int> staleTokens = null;

            foreach (KeyValuePair<string, MeasureCutSessionRuntime> entry in _activeMeasureCutSessionsByTaskKey)
            {
                MeasureCutSessionRuntime session = entry.Value;
                if (session == null || session.status != MeasureCutSessionStatus.Pending)
                {
                    continue;
                }

                bool ownerDisconnected = manager == null
                    || !manager.IsListening
                    || !manager.ConnectedClients.ContainsKey(session.ownerClientId);
                bool timedOut = now - session.startedAt >= timeout;
                if (!ownerDisconnected && !timedOut)
                {
                    continue;
                }

                session.status = MeasureCutSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = now;

                staleTaskKeys ??= new List<string>();
                staleTokens ??= new List<int>();
                staleTaskKeys.Add(entry.Key);
                staleTokens.Add(session.sessionToken);
            }

            if (staleTaskKeys == null)
            {
                return;
            }

            for (int i = 0; i < staleTaskKeys.Count; i++)
            {
                _activeMeasureCutSessionsByTaskKey.Remove(staleTaskKeys[i]);
            }

            for (int i = 0; i < staleTokens.Count; i++)
            {
                _measureCutSessionsByToken.Remove(staleTokens[i]);
            }
        }

        private void CleanupStalePipePaintSessions()
        {
            if (!IsServer || _activePipePaintSessionsByTaskKey.Count == 0)
            {
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            float now = Time.unscaledTime;
            float timeout = Mathf.Max(5f, _cleaningSessionTimeoutSeconds);
            List<string> staleTaskKeys = null;
            List<int> staleTokens = null;

            foreach (KeyValuePair<string, PipePaintSessionRuntime> entry in _activePipePaintSessionsByTaskKey)
            {
                PipePaintSessionRuntime session = entry.Value;
                if (session == null || session.status != PipePaintSessionStatus.Pending)
                {
                    continue;
                }

                bool ownerDisconnected = manager == null
                    || !manager.IsListening
                    || !manager.ConnectedClients.ContainsKey(session.ownerClientId);
                bool timedOut = now - session.startedAt >= timeout;
                if (!ownerDisconnected && !timedOut)
                {
                    continue;
                }

                session.status = PipePaintSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = now;

                staleTaskKeys ??= new List<string>();
                staleTokens ??= new List<int>();
                staleTaskKeys.Add(entry.Key);
                staleTokens.Add(session.sessionToken);
            }

            if (staleTaskKeys == null)
            {
                return;
            }

            for (int i = 0; i < staleTaskKeys.Count; i++)
            {
                _activePipePaintSessionsByTaskKey.Remove(staleTaskKeys[i]);
            }

            for (int i = 0; i < staleTokens.Count; i++)
            {
                _pipePaintSessionsByToken.Remove(staleTokens[i]);
            }
        }

        private void CleanupStaleDrillScrewSessions()
        {
            if (!IsServer || _activeDrillScrewSessionsByTaskKey.Count == 0)
            {
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            float now = Time.unscaledTime;
            float timeout = Mathf.Max(5f, _cleaningSessionTimeoutSeconds);
            List<string> staleTaskKeys = null;
            List<int> staleTokens = null;

            foreach (KeyValuePair<string, DrillScrewSessionRuntime> entry in _activeDrillScrewSessionsByTaskKey)
            {
                DrillScrewSessionRuntime session = entry.Value;
                if (session == null || session.status != DrillScrewSessionStatus.Pending)
                {
                    continue;
                }

                bool ownerDisconnected = manager == null
                    || !manager.IsListening
                    || !manager.ConnectedClients.ContainsKey(session.ownerClientId);
                bool timedOut = now - session.startedAt >= timeout;
                if (!ownerDisconnected && !timedOut)
                {
                    continue;
                }

                session.status = DrillScrewSessionStatus.Aborted;
                session.resolvedResult = MinigameResult.Cancelled;
                session.resolvedAt = now;
                staleTaskKeys ??= new List<string>();
                staleTokens ??= new List<int>();
                staleTaskKeys.Add(entry.Key);
                staleTokens.Add(session.sessionToken);
            }

            if (staleTaskKeys == null)
            {
                return;
            }

            for (int i = 0; i < staleTaskKeys.Count; i++)
            {
                _activeDrillScrewSessionsByTaskKey.Remove(staleTaskKeys[i]);
            }

            for (int i = 0; i < staleTokens.Count; i++)
            {
                _drillScrewSessionsByToken.Remove(staleTokens[i]);
            }
        }

        private void CleanupStaleComputerSessions()
        {
            if (!IsServer || _activeComputerSessionsByStationKey.Count == 0)
            {
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            float now = Time.unscaledTime;
            float timeout = Mathf.Max(5f, _cleaningSessionTimeoutSeconds);
            List<string> staleStationKeys = null;
            List<int> staleTokens = null;

            foreach (KeyValuePair<string, ComputerSessionRuntime> entry in _activeComputerSessionsByStationKey)
            {
                ComputerSessionRuntime session = entry.Value;
                if (session == null || session.status != ComputerSessionStatus.Pending)
                {
                    continue;
                }

                bool ownerDisconnected = manager == null
                    || !manager.IsListening
                    || !manager.ConnectedClients.ContainsKey(session.ownerClientId);
                bool timedOut = now - session.startedAt >= timeout;
                if (!ownerDisconnected && !timedOut)
                {
                    continue;
                }

                session.status = ComputerSessionStatus.Aborted;
                session.resolvedAt = now;
                staleStationKeys ??= new List<string>();
                staleTokens ??= new List<int>();
                staleStationKeys.Add(entry.Key);
                staleTokens.Add(session.sessionToken);
            }

            if (staleStationKeys == null)
            {
                return;
            }

            for (int i = 0; i < staleStationKeys.Count; i++)
            {
                _activeComputerSessionsByStationKey.Remove(staleStationKeys[i]);
            }

            for (int i = 0; i < staleTokens.Count; i++)
            {
                _computerSessionsByToken.Remove(staleTokens[i]);
            }
        }

        private static string NormalizeTaskTypeForSession(string taskType)
        {
            return string.IsNullOrWhiteSpace(taskType)
                ? string.Empty
                : taskType.Trim().ToLowerInvariant();
        }

        private static string NormalizeTaskKeyForSession(string taskKey)
        {
            return string.IsNullOrWhiteSpace(taskKey)
                ? string.Empty
                : taskKey.Trim();
        }

        private bool ShouldHandleTargetedCatchFlow()
        {
            return IsAuthoritativePublisher() || ShouldConsumeAsClientMirror();
        }

        private void TryBeginLocalLieMinigameFromCatch(NpcCatchTriggeredResponse response)
        {
            if (!ShouldHandleTargetedCatchFlow())
            {
                return;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening || manager.LocalClientId != response.targetClientId)
            {
                return;
            }

            string localOwnerKey = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            if (!string.Equals(response.ownerKey, localOwnerKey, StringComparison.Ordinal))
            {
                return;
            }

            if (_hasActiveLocalLieCatch)
            {
                return;
            }

            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager == null)
            {
                return;
            }

            MinigameData lieData = BuildLieMinigameDataForCatch(response.npcNetworkObjectId);
            lieData.ownerPlayerId = localOwnerKey;

            IMinigame minigame = minigameManager.StartMinigame<LieMinigame>(lieData, localOwnerKey);
            if (minigame == null)
            {
                RequestResolveNpcLieResult(response.catchToken, response.npcNetworkObjectId, MinigameResult.Cancelled);
                return;
            }

            _hasActiveLocalLieCatch = true;
            _activeLocalLieOwnerKey = localOwnerKey;
            _activeLocalLieCatchToken = response.catchToken;
            _activeLocalLieNpcNetworkObjectId = response.npcNetworkObjectId;
            _awaitingLocalLieResolutionAck = false;
        }

        private static MinigameData BuildLieMinigameDataForCatch(ulong npcNetworkObjectId)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null
                && manager.IsListening
                && manager.SpawnManager.SpawnedObjects.TryGetValue(npcNetworkObjectId, out NetworkObject npcNetworkObject)
                && npcNetworkObject != null)
            {
                NPCController npcController = npcNetworkObject.GetComponent<NPCController>();
                if (npcController != null)
                {
                    MinigameData sourcedData = npcController.BuildNetworkLieMinigameData();
                    if (sourcedData != null && !string.IsNullOrWhiteSpace(sourcedData.minigameId))
                    {
                        return sourcedData;
                    }
                }
            }

            return new MinigameData
            {
                minigameId = "lie_detection",
                displayName = "Detect the Lie",
                difficulty = 5
            };
        }

        private void OnLocalMinigameEnded(MinigameEndedEvent eventData)
        {
            TrySubmitActiveLieResult(eventData.MinigameId, eventData.OwnerPlayerId, eventData.Result);
        }

        private void OnLocalMinigameCancelled(MinigameCancelledEvent eventData)
        {
            TrySubmitActiveLieResult(eventData.MinigameId, eventData.OwnerPlayerId, MinigameResult.Cancelled);
        }

        private void TrySubmitActiveLieResult(string minigameId, string ownerPlayerId, MinigameResult result)
        {
            if (!_hasActiveLocalLieCatch
                || _awaitingLocalLieResolutionAck
                || !string.Equals(minigameId, "lie_detection", StringComparison.Ordinal))
            {
                return;
            }

            string localOwnerKey = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            if (!string.Equals(ownerPlayerId, localOwnerKey, StringComparison.Ordinal))
            {
                return;
            }

            _awaitingLocalLieResolutionAck = true;

            if (NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority requester) && requester != null)
            {
                requester.RequestResolveNpcLieResult(_activeLocalLieCatchToken, _activeLocalLieNpcNetworkObjectId, result);
                return;
            }

            RequestResolveNpcLieResult(_activeLocalLieCatchToken, _activeLocalLieNpcNetworkObjectId, result);
        }

        private void ClearLocalLieCatchSession()
        {
            _hasActiveLocalLieCatch = false;
            _activeLocalLieOwnerKey = string.Empty;
            _activeLocalLieCatchToken = 0UL;
            _activeLocalLieNpcNetworkObjectId = 0UL;
            _awaitingLocalLieResolutionAck = false;
        }
    }
}
