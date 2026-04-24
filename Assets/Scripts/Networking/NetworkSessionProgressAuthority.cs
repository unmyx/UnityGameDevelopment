using System;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using Game.Minigames;
using Game.Player;
using Game.Systems;
using Unity.Netcode;
using UnityEngine;

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
        public static event Action<NpcCatchTriggeredResponse> OnNpcCatchTriggeredResponse;
        public static event Action<NpcLieResolutionResponse> OnNpcLieResolutionResponse;
        public static event Action<CleaningResultResolutionResponse> OnCleaningResultResolutionResponse;
        public static event Action<WeldingResultResolutionResponse> OnWeldingResultResolutionResponse;
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

        private static NetworkSessionProgressAuthority _localRequester;
        private static NetworkSessionProgressAuthority _authoritativePublisher;

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
        private readonly Dictionary<string, ComputerSessionRuntime> _activeComputerSessionsByStationKey =
            new Dictionary<string, ComputerSessionRuntime>(StringComparer.Ordinal);
        private readonly Dictionary<int, ComputerSessionRuntime> _computerSessionsByToken =
            new Dictionary<int, ComputerSessionRuntime>();
        private static readonly HashSet<string> ConsumedNpcCatchKeys = new HashSet<string>();
        private int _cleaningSessionSequence;
        private int _weldingSessionSequence;
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
                    return true;
                }
            }

            NetworkSessionProgressAuthority[] authorities = FindObjectsByType<NetworkSessionProgressAuthority>(FindObjectsInactive.Exclude);
            for (int i = 0; i < authorities.Length; i++)
            {
                NetworkSessionProgressAuthority candidate = authorities[i];
                if (candidate != null && candidate.IsOwner && candidate.IsSpawned)
                {
                    _localRequester = candidate;
                    authority = candidate;
                    return true;
                }
            }

            authority = null;
            return false;
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
                ExecuteResolveWeldingResult(OwnerClientId, sessionToken, normalizedTaskKey, (int)result);
                return;
            }

            RequestResolveWeldingResultServerRpc(sessionToken, normalizedTaskKey, (int)result);
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
        private void RequestCompleteWorkdayAndGoHomeServerRpc()
        {
            ExecuteCompleteWorkdayAndGoHome();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestStartNextDayServerRpc()
        {
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
        private void RequestComputerSessionStartServerRpc(
            string stationKey,
            ServerRpcParams serverRpcParams = default)
        {
            ExecuteComputerSessionStart(serverRpcParams.Receive.SenderClientId, stationKey);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestEndComputerSessionServerRpc(
            int sessionToken,
            string stationKey,
            ServerRpcParams serverRpcParams = default)
        {
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
            ExecuteResolveWeldingResult(
                serverRpcParams.Receive.SenderClientId,
                sessionToken,
                taskKey,
                resultValue);
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
                    string.Empty,
                    BuildTargetClientRpcParams(senderClientId));
                return;
            }

            if (!gameManager.CanLaunchTaskAtLocation(taskType, taskKey, out string blockedReason))
            {
                if (_enableLogs)
                {
                    Debug.Log($"[NetworkSessionProgressAuthority] Job start rejected from client {senderClientId}: {blockedReason}");
                }

                SendJobInteractableStartResponseClientRpc(
                    taskType ?? string.Empty,
                    taskKey ?? string.Empty,
                    minigameId ?? string.Empty,
                    false,
                    string.IsNullOrWhiteSpace(blockedReason) ? "Task is unavailable." : blockedReason,
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
                        normalizedTaskKey,
                        BuildTargetClientRpcParams(senderClientId));
                    return;
                }

                weldingSessionToken = weldingSession.sessionToken;
                canonicalTaskKey = weldingSession.taskKey;
            }

            gameManager.RegisterDailyTaskLaunchContext(normalizedTaskType, normalizedTaskKey);
            SyncFromGameManager(forceTaskSnapshot: true);

            if (_enableLogs)
            {
                Debug.Log($"[NetworkSessionProgressAuthority] Job start approved for client {senderClientId}: {taskType}:{taskKey} ({minigameId}).");
            }

            SendJobInteractableStartResponseClientRpc(
                taskType ?? string.Empty,
                taskKey ?? string.Empty,
                minigameId ?? string.Empty,
                true,
                string.Empty,
                cleaningSessionToken,
                weldingSessionToken,
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
                    Debug.Log($"[NetworkSessionProgressAuthority] Ignored duplicate minigame reward claim: {claimDedupeKey}");
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
                    Debug.Log($"[NetworkSessionProgressAuthority] Ignored duplicate NPC catch registration: {dedupeKey}");
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
            string dedupeKey = BuildNpcCatchDedupeKey(ownerKey, catchToken);
            if (string.IsNullOrWhiteSpace(dedupeKey) || !ConsumedNpcCatchKeys.Add(dedupeKey))
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

            EventBus.Publish(new NpcCatchTriggeredEvent(
                response.ownerKey,
                response.targetClientId,
                response.catchToken,
                response.npcNetworkObjectId,
                response.serverTime,
                response.pendingLie));

            TryBeginLocalLieMinigameFromCatch(response);
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
