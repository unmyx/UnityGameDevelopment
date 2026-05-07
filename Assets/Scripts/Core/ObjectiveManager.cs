using UnityEngine;
using System.Collections.Generic;
using Game.Minigames;
using Game.Inventory;
using Game.Core.Events;
using Game.Player;
using Unity.Netcode;
using UnityEngine.SceneManagement;

namespace Game.Core
{
    /// <summary>
    /// ObjectiveManager tracks the player's active objectives and their progress.
    /// 
    /// Responsibilities:
    /// - Maintain list of active objectives
    /// - Listen to minigame completion events
    /// - Track objective progress and completion
    /// - Fire notifications when objectives complete or all objectives are done
    /// 
    /// Design:
    /// - Singleton pattern (one manager per game)
    /// - Subscribes to "MinigameRewardGranted" event from MinigameRewardSystem
    /// - Objectives assigned at runtime or in OnStart()
    /// - Fires "ObjectiveProgress" and "ObjectiveCompleted" events
    /// 
    /// Usage:
    /// 1. Add ObjectiveManager to scene (must be present in scene setup)
    /// 2. Assign objectives via ObjectiveManager.Instance.SetObjectives(List<Objective>)
    /// 3. Subscribe to "ObjectiveCompleted" event for UI notifications
    /// 
    /// Example:
    /// List<Objective> objectiveList = new() { objective1, objective2 };
    /// ObjectiveManager.Instance.SetObjectives(objectiveList);
    /// </summary>
    public class ObjectiveManager : MonoBehaviour
    {
        private const string ObjectiveResourcesPath = "Objectives";
        private const string MissingInstanceMessage =
            "[ObjectiveManager] Instance requested but no ObjectiveManager exists in the active scene. " +
            "Add ObjectiveManager to your bootstrap/gameplay scene instead of relying on runtime auto-creation.";

        private static ObjectiveManager _instance;
        private static bool _hasLoggedMissingInstance;
        private static bool _hasLoggedFallbackInstanceLookup;
        private static string _instanceBindingSource = "unbound";
        private static readonly Queue<RewardGrantedData> PendingRewardEvents = new Queue<RewardGrantedData>();
        private static bool _isReadyForRewardEvents;
        public static ObjectiveManager Instance
        {
            get
            {
                if (TryGetInstance(out ObjectiveManager instance))
                {
                    return instance;
                }

                if (!_hasLoggedMissingInstance)
                {
                    Debug.LogWarning(MissingInstanceMessage);
                    _hasLoggedMissingInstance = true;
                }

                return null;
            }
        }

        public static bool TryGetInstance(out ObjectiveManager instance)
        {
            if (_instance != null)
            {
                instance = _instance;
                return true;
            }

            _instance = FindAnyObjectByType<ObjectiveManager>();
            if (_instance != null)
            {
                LogFallbackInstanceLookup(_instance, "TryGetInstance");
            }
            instance = _instance;
            return instance != null;
        }

        public static bool ValidateSceneSetup(bool logWarning = true)
        {
            bool found = TryGetInstance(out _);
            if (!found && logWarning)
            {
                Debug.LogWarning(MissingInstanceMessage);
            }

            return found;
        }

        public static void ClearRuntimeCaches()
        {
            PendingRewardEvents.Clear();
        }
        
        public static bool IsReadyForRewardEvents => _instance != null && _isReadyForRewardEvents;

        [SerializeField]
        [Tooltip("Objectives to track. Can be set in inspector or at runtime.")]
        private List<Objective> _activeObjectives = new List<Objective>();

        private List<Objective> _completedObjectives = new List<Objective>();
        private readonly Dictionary<string, Objective> _objectiveCacheById = new Dictionary<string, Objective>();
        private readonly HashSet<string> _rewardGrantedObjectiveIds = new HashSet<string>();
        private bool _isMissionComplete = false;
        private bool _hasPublishedMissionCompleteEvent;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _instanceBindingSource = "Awake";
            _hasLoggedMissingInstance = false;
            DetachFromParentIfNeeded();
            DontDestroyOnLoad(gameObject);
            SubscribeToMinigameEvents();
            FlushQueuedRewardEvents();
        }

        private void DetachFromParentIfNeeded()
        {
            if (transform.parent != null)
            {
                transform.SetParent(null, true);
            }
        }

        private void OnEnable()
        {
            SubscribeToMinigameEvents();
            FlushQueuedRewardEvents();
        }

        private void OnDisable()
        {
            _isReadyForRewardEvents = false;
            EventBus.Unsubscribe<MinigameRewardGrantedEvent>(OnMinigameRewardGranted);
            EventBus.Unsubscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Unsubscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        /// <summary>
        /// Set the active objectives to track.
        /// Resets any previous objective state.
        /// </summary>
        public void SetObjectives(List<Objective> objectives)
        {
            if (objectives == null)
            {
                objectives = new List<Objective>();
            }

            _activeObjectives = new List<Objective>(objectives);
            _completedObjectives.Clear();
            _rewardGrantedObjectiveIds.Clear();
            _isMissionComplete = false;
            _hasPublishedMissionCompleteEvent = false;

            // Reset all objectives to start fresh
            foreach (var objective in _activeObjectives)
            {
                objective.Reset();
            }
        }

        /// <summary>
        /// Add a single objective to the active list.
        /// </summary>
        public void AddObjective(Objective objective)
        {
            if (objective != null && !_activeObjectives.Contains(objective))
            {
                _activeObjectives.Add(objective);
                objective.Reset();
                _isMissionComplete = false;
                _hasPublishedMissionCompleteEvent = false;
            }
        }

        /// <summary>
        /// Called when minigame completes and rewards are granted.
        /// Tracks progress toward objectives.
        /// </summary>
        private void OnMinigameRewardGranted(MinigameRewardGrantedEvent rewardEvent)
        {
            RewardGrantedData rewardData = rewardEvent.Data;
            HandleRewardGrantedData(rewardData);
        }

        /// <summary>
        /// Get currently active (incomplete) objectives.
        /// </summary>
        public List<Objective> GetActiveObjectives()
        {
            return new List<Objective>(_activeObjectives);
        }

        /// <summary>
        /// Get completed objectives.
        /// </summary>
        public List<Objective> GetCompletedObjectives()
        {
            return new List<Objective>(_completedObjectives);
        }

        /// <summary>
        /// Check if all objectives have been completed.
        /// </summary>
        public bool IsMissionComplete()
        {
            return _isMissionComplete;
        }

        /// <summary>
        /// Get objective by ID.
        /// </summary>
        public Objective GetObjectiveById(string objectiveId)
        {
            foreach (var objective in _activeObjectives)
            {
                if (objective.ObjectiveId == objectiveId)
                    return objective;
            }

            foreach (var objective in _completedObjectives)
            {
                if (objective.ObjectiveId == objectiveId)
                    return objective;
            }

            return null;
        }

        /// <summary>
        /// Get overall mission progress as percentage (0-1).
        /// </summary>
        public float GetMissionProgress()
        {
            int totalObjectives = _activeObjectives.Count + _completedObjectives.Count;
            if (totalObjectives == 0)
                return 0f;

            return (float)_completedObjectives.Count / totalObjectives;
        }

        /// <summary>
        /// Get save state of all objectives.
        /// Used by SaveManager.Save() to serialize objective progress.
        /// </summary>
        public ObjectiveStates GetObjectiveStates()
        {
            ObjectiveStates states = new ObjectiveStates();

            // Track completed objectives by ID
            foreach (var completed in _completedObjectives)
            {
                states.completedObjectiveIds.Add(completed.ObjectiveId);
            }

            // Track active objectives with their progress
            foreach (var active in _activeObjectives)
            {
                states.activeObjectives.Add(new ObjectiveProgressForSave
                {
                    objectiveId = active.ObjectiveId,
                    currentProgress = active.CompletionCount
                });
            }

            states.isMissionComplete = _isMissionComplete;

            return states;
        }

        /// <summary>
        /// True when there is any tracked objective data already present.
        /// Used to avoid overriding loaded save state or manually assigned objectives.
        /// </summary>
        public bool HasTrackedObjectives()
        {
            return _activeObjectives.Count > 0 || _completedObjectives.Count > 0 || _isMissionComplete;
        }

        /// <summary>
        /// Initialize default objectives from Resources/Objectives if state is empty.
        /// Returns true when objectives were initialized.
        /// </summary>
        public bool InitializeDefaultObjectivesIfEmpty()
        {
            if (HasTrackedObjectives())
            {
                return false;
            }

            EnsureObjectiveCacheBuilt();

            List<Objective> defaults = new List<Objective>();
            Objective firstAvailable = null;

            foreach (Objective objective in _objectiveCacheById.Values)
            {
                if (objective == null)
                {
                    continue;
                }

                if (firstAvailable == null)
                {
                    firstAvailable = objective;
                }

                if (objective.IsActive)
                {
                    defaults.Add(objective);
                }
            }

            // Guarantee at least one active objective when any objective assets exist.
            if (defaults.Count == 0 && firstAvailable != null)
            {
                defaults.Add(firstAvailable);
            }

            if (defaults.Count == 0)
            {
                return false;
            }

            SetObjectives(defaults);
            return true;
        }

        /// <summary>
        /// Re-synchronize objective runtime state immediately after load.
        /// - Ensures reward event subscription is active
        /// - Cleans inconsistent loaded objective lists
        /// - Forces HUD/event listeners to refresh objective display
        /// </summary>
        public void SyncAfterLoad()
        {
            SubscribeToMinigameEvents();
            SanitizeLoadedState();
            FlushQueuedRewardEvents();
            PublishObjectiveRefreshEvents();
        }

        public void ResetForNewRun()
        {
            _activeObjectives.Clear();
            _completedObjectives.Clear();
            _rewardGrantedObjectiveIds.Clear();
            _isMissionComplete = false;
            _hasPublishedMissionCompleteEvent = false;

            ClearRuntimeCaches();
            InitializeDefaultObjectivesIfEmpty();
            SyncAfterLoad();
        }

        /// <summary>
        /// Re-bind minigame completion subscriptions.
        /// Safe to call repeatedly across scene transitions.
        /// </summary>
        public void SubscribeToMinigameEvents()
        {
            EventBus.Unsubscribe<MinigameRewardGrantedEvent>(OnMinigameRewardGranted);
            EventBus.Unsubscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Unsubscribe<MinigameCancelledEvent>(OnMinigameCancelled);

            EventBus.Subscribe<MinigameRewardGrantedEvent>(OnMinigameRewardGranted);
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
            _isReadyForRewardEvents = true;
        }

        public static void EnqueuePendingRewardEvent(RewardGrantedData rewardData)
        {
            if (rewardData == null)
            {
                return;
            }

            PendingRewardEvents.Enqueue(rewardData);
        }

        /// <summary>
        /// Restore objective state from save data.
        /// Used by SaveManager.Load() to reconstruct objective progress.
        /// Note: Assumes objective assets exist and are accessible.
        /// </summary>
        public void RestoreFromSave(ObjectivesSaveData savedData)
        {
            // Clear current state
            _activeObjectives.Clear();
            _completedObjectives.Clear();
            _rewardGrantedObjectiveIds.Clear();
            _isMissionComplete = false;
            _hasPublishedMissionCompleteEvent = false;

            if (savedData == null)
            {
                Debug.Log("ObjectiveManager restored: no save data");
                return;
            }

            int restoredCount = 0;
            int failedCount = 0;

            // Restore completed objectives
            if (savedData.completedObjectiveIds != null)
            {
                foreach (var objectiveId in savedData.completedObjectiveIds)
                {
                    Objective objective = LoadObjectiveAsset(objectiveId);
                    if (objective != null)
                    {
                        objective.Reset();
                        // Mark as complete by incrementing to required count
                        for (int i = 0; i < objective.RequiredCompletions; i++)
                        {
                            objective.IncrementProgress();
                        }
                        _completedObjectives.Add(objective);
                        _rewardGrantedObjectiveIds.Add(objective.ObjectiveId);
                        restoredCount++;
                    }
                    else
                    {
                        Debug.LogWarning($"Failed to load completed objective: {objectiveId}");
                        failedCount++;
                    }
                }
            }

            // Restore active objectives with progress
            if (savedData.activeObjectives != null)
            {
                foreach (var progressData in savedData.activeObjectives)
                {
                    Objective objective = LoadObjectiveAsset(progressData.objectiveId);
                    if (objective != null)
                    {
                        objective.Reset();
                        // Restore progress by incrementing the required number of times
                        for (int i = 0; i < progressData.currentProgress; i++)
                        {
                            objective.IncrementProgress();
                        }
                        _activeObjectives.Add(objective);
                        restoredCount++;
                    }
                    else
                    {
                        Debug.LogWarning($"Failed to load active objective: {progressData.objectiveId}");
                        failedCount++;
                    }
                }
            }

            _isMissionComplete = savedData.isMissionComplete;
            _hasPublishedMissionCompleteEvent = false;

            Debug.Log($"ObjectiveManager restored: {restoredCount} objectives loaded, {failedCount} failed");
        }

        /// <summary>
        /// Load an Objective asset by ID from Resources.
        /// </summary>
        private Objective LoadObjectiveAsset(string objectiveId)
        {
            if (string.IsNullOrEmpty(objectiveId))
                return null;

            EnsureObjectiveCacheBuilt();
            if (_objectiveCacheById.TryGetValue(objectiveId, out Objective cachedObjective))
            {
                return cachedObjective;
            }

            return null;
        }

        private void EnsureObjectiveCacheBuilt()
        {
            if (_objectiveCacheById.Count > 0)
            {
                return;
            }

            Objective[] allObjectives = Resources.LoadAll<Objective>(ObjectiveResourcesPath);
            foreach (Objective objective in allObjectives)
            {
                if (objective == null || string.IsNullOrEmpty(objective.ObjectiveId))
                {
                    continue;
                }

                _objectiveCacheById[objective.ObjectiveId] = objective;
            }
        }

        private void OnMinigameEnded(MinigameEndedEvent _)
        {
            // Keep internal lists coherent even after scene/state transitions.
            SanitizeLoadedState();
        }

        private void OnMinigameCancelled(MinigameCancelledEvent _)
        {
            SanitizeLoadedState();
        }

        private void SanitizeLoadedState()
        {
            _completedObjectives.RemoveAll(objective => objective == null);
            _activeObjectives.RemoveAll(objective => objective == null);

            HashSet<string> completedIds = new HashSet<string>();
            for (int i = _completedObjectives.Count - 1; i >= 0; i--)
            {
                Objective completed = _completedObjectives[i];
                string id = completed.ObjectiveId;
                if (string.IsNullOrEmpty(id) || !completedIds.Add(id))
                {
                    _completedObjectives.RemoveAt(i);
                }
            }

            _rewardGrantedObjectiveIds.Clear();
            foreach (Objective completed in _completedObjectives)
            {
                if (completed != null && !string.IsNullOrEmpty(completed.ObjectiveId))
                {
                    _rewardGrantedObjectiveIds.Add(completed.ObjectiveId);
                }
            }

            for (int i = _activeObjectives.Count - 1; i >= 0; i--)
            {
                Objective active = _activeObjectives[i];
                string id = active.ObjectiveId;
                if (string.IsNullOrEmpty(id) || completedIds.Contains(id))
                {
                    _activeObjectives.RemoveAt(i);
                    continue;
                }

                if (active.IsCompleted || active.CompletionCount >= active.RequiredCompletions)
                {
                    _activeObjectives.RemoveAt(i);
                    _completedObjectives.Add(active);
                    completedIds.Add(id);
                }
            }

            _isMissionComplete = _activeObjectives.Count == 0 && _completedObjectives.Count > 0;
            if (!_isMissionComplete)
            {
                _hasPublishedMissionCompleteEvent = false;
            }
        }

        private void PublishObjectiveRefreshEvents()
        {
            for (int i = 0; i < _activeObjectives.Count; i++)
            {
                EventBus.Publish(new ObjectiveProgressEvent(_activeObjectives[i]));
            }

            PublishMissionCompleteEventIfNeeded();
        }

        private void PublishMissionCompleteEventIfNeeded()
        {
            if (!_isMissionComplete || _activeObjectives.Count > 0)
            {
                return;
            }

            if (_hasPublishedMissionCompleteEvent)
            {
                return;
            }

            _hasPublishedMissionCompleteEvent = true;
            EventBus.Publish(new AllObjectivesCompletedEvent());
        }

        private void FlushQueuedRewardEvents()
        {
            if (!_isReadyForRewardEvents)
            {
                return;
            }

            while (PendingRewardEvents.Count > 0)
            {
                RewardGrantedData queuedEvent = PendingRewardEvents.Dequeue();
                HandleRewardGrantedData(queuedEvent);
            }
        }

        private void HandleRewardGrantedData(RewardGrantedData rewardData)
        {
            if (rewardData == null)
            {
                return;
            }

            // Only count pass results for objective progress
            if (rewardData.result != MinigameResult.Pass)
            {
                return;
            }

            // Check each active objective
            for (int i = _activeObjectives.Count - 1; i >= 0; i--)
            {
                Objective objective = _activeObjectives[i];

                if (objective.ShouldCountMinigame(rewardData.minigameId, rewardData.result))
                {
                    bool justCompleted = objective.IncrementProgress();

                    // Fire progress event
                    EventBus.Publish(new ObjectiveProgressEvent(objective));

                    if (justCompleted)
                    {
                        // Move to completed list
                        _activeObjectives.RemoveAt(i);
                        _completedObjectives.Add(objective);
                        GrantObjectiveRewardsOnce(objective);

                        // Fire completion event
                        EventBus.Publish(new ObjectiveCompletedEvent(objective));

                        // Check if all objectives are done
                        if (_activeObjectives.Count == 0)
                        {
                            _isMissionComplete = true;
                            PublishMissionCompleteEventIfNeeded();
                        }
                    }
                }
            }
        }

        private void GrantObjectiveRewardsOnce(Objective objective)
        {
            if (objective == null || string.IsNullOrEmpty(objective.ObjectiveId))
            {
                return;
            }

            if (!_rewardGrantedObjectiveIds.Add(objective.ObjectiveId))
            {
                return;
            }

            if (IsNonAuthoritativeNetworkClient())
            {
                return;
            }

            if (objective.RewardCurrency > 0)
            {
                GameManager gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    bool isDailyTaskModeActive = gameManager.GetCurrentRunPhase() == GameManager.RunPhase.Work
                        && gameManager.HasDailyTaskAssignmentsForCurrentWorkday();
                    if (!isDailyTaskModeActive)
                    {
                        gameManager.AddDayWorkEarnings(objective.RewardCurrency);
                    }
                }
            }

            if (objective.RewardItems == null || objective.RewardItems.Count == 0)
            {
                return;
            }

            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null)
            {
                return;
            }

            foreach (InventoryItem rewardItem in objective.RewardItems)
            {
                if (rewardItem == null)
                {
                    continue;
                }

                string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
                bool added = inventorySystem.AddItem(rewardItem, ownerPlayerId);
                if (added)
                {
                    EventBus.Publish(new ItemPickedUpEvent(rewardItem.ItemName));
                }
                else
                {
                    EventBus.Publish(new ItemPickupFailedEvent(rewardItem.ItemName));
                }
            }
        }

        private static bool IsNonAuthoritativeNetworkClient()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening && manager.IsClient && !manager.IsServer;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void LogFallbackInstanceLookup(ObjectiveManager resolvedInstance, string callsite)
        {
            if (_hasLoggedFallbackInstanceLookup || resolvedInstance == null)
            {
                return;
            }

            _hasLoggedFallbackInstanceLookup = true;
            NetworkManager networkManager = NetworkManager.Singleton;
            string netMode = "offline";
            if (networkManager != null && networkManager.IsListening)
            {
                netMode = networkManager.IsServer
                    ? (networkManager.IsClient ? "host" : "server")
                    : "client";
            }

            Debug.LogWarning(
                $"[ObjectiveManager] Fallback instance scan used at '{callsite}' in scene '{SceneManager.GetActiveScene().name}' ({netMode}). " +
                $"Resolved '{resolvedInstance.name}' via FindAnyObjectByType. BindingSource={_instanceBindingSource}. " +
                "Behavior remains permissive for bootstrap/recovery compatibility.");
        }
    }

    /// <summary>
    /// Data class for save/load operation containing objective states.
    /// </summary>
    public class ObjectiveStates
    {
        public List<string> completedObjectiveIds = new List<string>();
        public List<ObjectiveProgressForSave> activeObjectives = new List<ObjectiveProgressForSave>();
        public bool isMissionComplete;
    }

    /// <summary>
    /// Data class for tracking objective progress during save/load.
    /// </summary>
    public class ObjectiveProgressForSave
    {
        public string objectiveId;
        public int currentProgress;
    }
}
