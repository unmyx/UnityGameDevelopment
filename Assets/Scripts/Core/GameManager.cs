using UnityEngine;
using UnityEngine.SceneManagement;
using Game.Inventory;
using Game.Interaction;
using Game.Minigames;
using Game.UI;
using Game.Player;
using Game.Core.Events;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Game.Core
{
    public enum GameState
    {
        Menu,
        FreePlay,
        Minigame
    }

    /// <summary>
    /// GameManager is the central manager for game state transitions.
    /// Persists across scenes with DontDestroyOnLoad. Auto-detects state from scene GameObjects.
    /// On scene load, re-detects and initializes the appropriate state.
    /// </summary>

    public class GameManager : MonoBehaviour
    {
        public enum RunPhase
        {
            Work,
            Home,
            GameOver
        }

        [Serializable]
        private class DailyTaskAssignment
        {
            public string taskType;
            public string taskKey;
            public bool isCompleted;

            public DailyTaskAssignment(string taskType, string taskKey)
            {
                this.taskType = taskType;
                this.taskKey = taskKey;
                isCompleted = false;
            }
        }

        [Serializable]
        private class StolenLootTrackerEntry
        {
            public string itemId;
            public int count;

            public StolenLootTrackerEntry(string itemId, int count)
            {
                this.itemId = itemId;
                this.count = Mathf.Max(0, count);
            }
        }

        private const string DailyTaskTypeCleaning = "cleaning";
        private const string DailyTaskTypeWelding = "welding";
        private const string MenuSceneName = "Menu";
        private const string GameplaySceneName = "GameplayScene";
        private const string HomeSceneName = "HomeScene";
        public const string UpgradeIdInventoryQuickSlots = "inventory_quick_slots";
        public const string UpgradeIdCleaningTool = "cleaning_tool";
        public const string UpgradeIdWeldingTool = "welding_tool";
        private const int DefaultCleaningAssignmentsPerDay = 2;
        private const int DefaultWeldingAssignmentsPerDay = 2;
        private const int DayDifficultyTierSpanDays = 2;
        private const int WeldingActiveTargetDayBonusSpanDays = 3;
        private const int MaxWeldingActiveTargetDayBonus = 1;
        private const float CleaningDayDifficultyStep = 0.06f;
        private const float WeldingDayDifficultyStep = 0.05f;
        private const float MinCleaningDayDifficultyMultiplier = 0.64f;
        private const float MinWeldingDayDifficultyMultiplier = 0.68f;
        private const int MaxConsecutiveFailedWorkdaysBeforeGameOver = 3;
        private const int FailedLieEscalationThresholdPerDay = 3;

        private static readonly Dictionary<string, int> StolenLootSellPriceByItemId =
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "wedding_ring_gold", 250 },
                { "wedding_ring_silver", 125 }
            };

        private static readonly string[] SupportedUpgradeIds =
        {
            UpgradeIdInventoryQuickSlots,
            UpgradeIdCleaningTool,
            UpgradeIdWeldingTool
        };

        private static readonly Dictionary<string, int[]> UpgradeTierCostsById =
            new Dictionary<string, int[]>(StringComparer.Ordinal)
            {
                { UpgradeIdInventoryQuickSlots, new[] { 250, 400 } },
                { UpgradeIdCleaningTool, new[] { 200, 350 } },
                { UpgradeIdWeldingTool, new[] { 300 } }
            };

        private GameStateResolver _stateResolver;
        private GameStateTransitionService _stateTransitionService;

        private static GameManager _instance;
        public static GameManager Instance
        {
            get
            {
                _instance = GameManagerInstanceLocator.Resolve(_instance);
                return _instance;
            }
        }

        [Header("State Wiring")]
        [SerializeField] private MenuState _menuState;
        [SerializeField] private FreePlayState _freePlayState;
        [SerializeField] private MinigameState _minigameState;

        [Header("Core Gameplay Dependencies")]
        [SerializeField] private PauseManager _pauseManager;
        [SerializeField] private ObjectiveManager _objectiveManager;
        [SerializeField] private InventorySystem _inventorySystem;

        [SerializeField]
        private GameState _currentState = GameState.FreePlay;

        [SerializeField]
        [Tooltip("Runtime general currency starting value before save restore.")]
        private int _startingCurrency = 0;

        private int _currency;

        [SerializeField]
        [Tooltip("Workday earnings that reset when a new day begins.")]
        private int _dayWorkEarnings;

        [Header("Run Progression")]
        [SerializeField]
        [Tooltip("Current day number for the active run.")]
        private int _currentDay = 1;

        [SerializeField]
        [Tooltip("Top-level day/run phase.")]
        private RunPhase _currentRunPhase = RunPhase.Work;

        [SerializeField]
        [Tooltip("True after the current workday has been completed.")]
        private bool _workdayCompleted;

        [SerializeField]
        [Tooltip("How many workdays failed in sequence.")]
        private int _consecutiveFailedWorkdays;

        [SerializeField]
        [Tooltip("True once the run enters game over.")]
        private bool _runFailed;

        [SerializeField]
        [Tooltip("Optional reason for run failure.")]
        private string _runFailedReason = string.Empty;

        [SerializeField]
        [Tooltip("Failed lie outcomes recorded during the current workday.")]
        private int _failedLieEscalationCountThisDay;

        [Header("Daily Task Assignments")]
        [SerializeField]
        [Tooltip("Current workday assignments (location-bound cleaning/welding tasks).")]
        private List<DailyTaskAssignment> _dailyTaskAssignments = new List<DailyTaskAssignment>();

        [SerializeField]
        [Tooltip("Day index for which assignments were generated.")]
        private int _dailyTaskAssignmentDay = -1;

        [Header("Stolen Loot Tracking")]
        [SerializeField]
        [Tooltip("Eligible world loot picked up during the current day, keyed by item ID.")]
        private List<StolenLootTrackerEntry> _stolenLootThisDay = new List<StolenLootTrackerEntry>();

        private readonly Dictionary<string, int> _ownedToolTiers = new Dictionary<string, int>(StringComparer.Ordinal);

        private string _lastLaunchedDailyTaskType = string.Empty;
        private string _lastLaunchedDailyTaskKey = string.Empty;

        private const int InitialLoadBindingRetryLimit = 10;

        public GameState CurrentState => _currentState;
        public int CurrentDay => _currentDay;
        public RunPhase CurrentRunPhase => _currentRunPhase;

        private IGameState _currentStateImplementation;
        private bool _isTransitioning;
        private bool _hasAttemptedInitialLoad;
        private int _initialLoadBindingRetryCount;
        private bool _hasCriticalSetupFailure;
        private bool _isRunPhaseSceneRouting;
        private bool _hasRoutedAfterFailure;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                _instance.ApplySceneBindingsFrom(this);
                _instance.EnsureServicesInitialized();
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _currency = _startingCurrency;
            _dayWorkEarnings = 0;
            _currentDay = Mathf.Max(1, _currentDay);
            _consecutiveFailedWorkdays = Mathf.Max(0, _consecutiveFailedWorkdays);
            _failedLieEscalationCountThisDay = Mathf.Max(0, _failedLieEscalationCountThisDay);

            if (_runFailed || _currentRunPhase == RunPhase.GameOver)
            {
                _runFailed = true;
                _currentRunPhase = RunPhase.GameOver;
            }

            if (!_runFailed)
            {
                _runFailedReason = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(_runFailedReason))
            {
                _runFailedReason = string.Empty;
            }

            _hasRoutedAfterFailure = false;

            DetachFromParentIfNeeded();
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureServicesInitialized();
        }

        private void DetachFromParentIfNeeded()
        {
            if (transform.parent != null)
            {
                transform.SetParent(null, true);
            }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnEnable()
        {
            EventBus.Subscribe<MinigameStartedEvent>(OnMinigameStarted);
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<MinigameStartedEvent>(OnMinigameStarted);
            EventBus.Unsubscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Unsubscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void Start()
        {
            EnsureServicesInitialized();
            RefreshRuntimeBindings();
            if (!DetermineAndInitializeStartingState())
            {
                return;
            }

            StartCoroutine(DeferredInitialLoad());
        }

        private void Update()
        {
            _currentStateImplementation?.OnStateUpdate();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _isRunPhaseSceneRouting = false;
            RefreshRuntimeBindings();
            if (!DetermineAndInitializeStartingState())
            {
                return;
            }

            if (_currentState == GameState.Menu)
            {
                _hasAttemptedInitialLoad = false;
                _initialLoadBindingRetryCount = 0;
            }

            if (_hasAttemptedInitialLoad)
            {
                EnsureDailyAssignmentsForCurrentWorkday();
            }

            StartCoroutine(DeferredInitialLoad());
        }

        private void OnMinigameStarted(MinigameStartedEvent eventData)
        {
            if (_currentState == GameState.Minigame)
            {
                return;
            }

            ChangeStateInternal(GameState.Minigame);
        }

        private void OnMinigameEnded(MinigameEndedEvent eventData)
        {
            if (_currentState == GameState.Minigame)
            {
                ChangeStateInternal(GameState.FreePlay);
            }

            if (eventData.Result != MinigameResult.Pass)
            {
                ClearPendingDailyTaskLaunchContext();
            }
        }

        private void OnMinigameCancelled(MinigameCancelledEvent eventData)
        {
            if (_currentState == GameState.Minigame)
            {
                ChangeStateInternal(GameState.FreePlay);
            }

            ClearPendingDailyTaskLaunchContext();
        }

        private void EnsureServicesInitialized()
        {
            _stateResolver = new GameStateResolver(_menuState, _freePlayState, _minigameState);

            if (_stateTransitionService == null)
            {
                _stateTransitionService = new GameStateTransitionService(_stateResolver);
                return;
            }

            _stateTransitionService.SetResolver(_stateResolver);
        }

        private bool DetermineAndInitializeStartingState()
        {
            EnsureServicesInitialized();
            if (!ValidateManagerSetupForScene())
            {
                return false;
            }

            if (!_stateTransitionService.RefreshStateFromScene(ref _currentState, ref _currentStateImplementation, out string transitionFailure))
            {
                FailCriticalSetup($"Failed to enter configured state '{_currentState}': {transitionFailure}");
                return false;
            }

            if (_currentState != GameState.FreePlay)
            {
                return true;
            }

            if (_objectiveManager == null)
            {
                FailCriticalSetup("Missing ObjectiveManager reference required for FreePlay objective synchronization.");
                return false;
            }

            _objectiveManager.SyncAfterLoad();
            return true;
        }

        private IEnumerator DeferredInitialLoad()
        {
            if (_hasAttemptedInitialLoad)
            {
                yield break;
            }

            // Wait one frame so scene Start() methods finish before restore calls.
            yield return null;

            while (!_hasAttemptedInitialLoad && _currentState == GameState.FreePlay)
            {
                RefreshRuntimeBindings();

                if (TryBuildSaveContext(out SaveManager.SaveContext saveContext, out string failureReason))
                {
                    _hasAttemptedInitialLoad = true;
                    _initialLoadBindingRetryCount = 0;
                    SaveManager.Load(saveContext);
                    Scene activeSceneBeforeReconcile = SceneManager.GetActiveScene();
                    string activeSceneNameBeforeReconcile = activeSceneBeforeReconcile.name;
                    ReconcileSceneWithRunPhaseAfterRestore();
                    Scene activeSceneAfterReconcile = SceneManager.GetActiveScene();
                    if (!string.Equals(activeSceneNameBeforeReconcile, activeSceneAfterReconcile.name, StringComparison.Ordinal))
                    {
                        yield break;
                    }

                    EnsureDailyAssignmentsForCurrentWorkday();
                    RefreshInventoryGridUiBindings(forceFullRefresh: true);
                    yield break;
                }

                if (_initialLoadBindingRetryCount >= InitialLoadBindingRetryLimit)
                {
                    FailCriticalSetup($"Initial load blocked due to missing gameplay save dependencies: {failureReason}");
                    yield break;
                }

                _initialLoadBindingRetryCount++;
                yield return null;
            }
        }

        private bool ValidateManagerSetupForScene()
        {
            if (TryValidateSceneSetupForState(_currentState, out string failureReason))
            {
                return true;
            }

            FailCriticalSetup($"Missing required wiring for state '{_currentState}': {failureReason}");
            return false;
        }

        private void ChangeStateInternal(GameState newState)
        {
            RefreshRuntimeBindings();
            if (!TryValidateSceneSetupForState(newState, out string setupFailure))
            {
                FailCriticalSetup($"Cannot transition to '{newState}': {setupFailure}");
                return;
            }

            EnsureServicesInitialized();
            if (!_stateTransitionService.ChangeState(newState, ref _currentState, ref _currentStateImplementation, ref _isTransitioning, out string transitionFailure))
            {
                FailCriticalSetup($"Transition to '{newState}' failed: {transitionFailure}");
            }
        }

        public void RequestEnterMinigame()
        {
            ChangeStateInternal(GameState.Minigame);
        }

        public void RequestReturnToFreePlay()
        {
            ChangeStateInternal(GameState.FreePlay);
        }

        public void RequestReturnToMenu()
        {
            ChangeStateInternal(GameState.Menu);
        }

        public int GetCurrency()
        {
            return _currency;
        }

        public int GetDayWorkEarnings()
        {
            return _dayWorkEarnings;
        }

        public int GetCurrentDay()
        {
            return _currentDay;
        }

        public RunPhase GetCurrentRunPhase()
        {
            return _currentRunPhase;
        }

        public int GetCurrentDayDifficultyTier()
        {
            int normalizedDay = Mathf.Max(1, _currentDay);
            return Mathf.Max(0, (normalizedDay - 1) / DayDifficultyTierSpanDays);
        }

        public int GetScaledAssignmentTargetCountForCurrentDay(int baseAssignmentsPerDay)
        {
            int safeBaseAssignments = Mathf.Max(0, baseAssignmentsPerDay);
            return Mathf.Max(0, safeBaseAssignments + GetCurrentDayDifficultyTier());
        }

        public float GetCleaningDayDifficultyMultiplier()
        {
            float scaledMultiplier = 1f - (CleaningDayDifficultyStep * GetCurrentDayDifficultyTier());
            return Mathf.Clamp(scaledMultiplier, MinCleaningDayDifficultyMultiplier, 1f);
        }

        public float GetWeldingDayDifficultyMultiplier()
        {
            float scaledMultiplier = 1f - (WeldingDayDifficultyStep * GetCurrentDayDifficultyTier());
            return Mathf.Clamp(scaledMultiplier, MinWeldingDayDifficultyMultiplier, 1f);
        }

        public int GetWeldingActiveTargetCountBonusForCurrentDay()
        {
            int normalizedDay = Mathf.Max(1, _currentDay);
            int bonus = (normalizedDay - 1) / WeldingActiveTargetDayBonusSpanDays;
            return Mathf.Clamp(bonus, 0, MaxWeldingActiveTargetDayBonus);
        }

        public bool IsWorkdayCompleted()
        {
            return _workdayCompleted;
        }

        public int GetConsecutiveFailedWorkdays()
        {
            return _consecutiveFailedWorkdays;
        }

        public bool IsRunFailed()
        {
            return _runFailed;
        }

        public string GetRunFailedReason()
        {
            return _runFailedReason;
        }

        public int GetUnlockedQuickSlots()
        {
            int tier = GetOwnedUpgradeTier(UpgradeIdInventoryQuickSlots);
            switch (tier)
            {
                case 0:
                    return 3;

                case 1:
                    return 6;

                default:
                    return 9;
            }
        }

        public float GetCleaningEffectivenessMultiplier()
        {
            int tier = GetOwnedUpgradeTier(UpgradeIdCleaningTool);
            switch (tier)
            {
                case 0:
                    return 1f;

                case 1:
                    return 1.2f;

                default:
                    return 1.4f;
            }
        }

        public float GetWeldingEffectivenessMultiplier()
        {
            int tier = GetOwnedUpgradeTier(UpgradeIdWeldingTool);
            return tier >= 1 ? 1.25f : 1f;
        }

        public bool TryPurchaseUpgradeInHome(string upgradeId, out int spentCurrency, out int resultingTier)
        {
            spentCurrency = 0;

            string normalizedUpgradeId = NormalizeUpgradeId(upgradeId);
            resultingTier = GetOwnedUpgradeTier(normalizedUpgradeId);

            if (_runFailed || _currentRunPhase != RunPhase.Home)
            {
                return false;
            }

            int maxTier = GetMaxUpgradeTier(normalizedUpgradeId);
            if (maxTier <= 0)
            {
                return false;
            }

            int currentTier = GetOwnedUpgradeTier(normalizedUpgradeId);
            resultingTier = currentTier;
            if (currentTier >= maxTier)
            {
                return false;
            }

            int[] tierCosts = UpgradeTierCostsById[normalizedUpgradeId];
            int cost = tierCosts[currentTier];
            if (!TrySpendCurrency(cost))
            {
                return false;
            }

            int nextTier = currentTier + 1;
            SetOwnedUpgradeTier(normalizedUpgradeId, nextTier);
            spentCurrency = cost;
            resultingTier = nextTier;
            return true;
        }

        public List<ToolDataEntry> GetOwnedToolUpgradesForSave()
        {
            List<ToolDataEntry> saved = new List<ToolDataEntry>();
            for (int i = 0; i < SupportedUpgradeIds.Length; i++)
            {
                string upgradeId = SupportedUpgradeIds[i];
                int tier = GetOwnedUpgradeTier(upgradeId);
                if (tier <= 0)
                {
                    continue;
                }

                saved.Add(new ToolDataEntry(upgradeId, tier));
            }

            return saved;
        }

        public void RestoreOwnedToolUpgradesFromSave(List<ToolDataEntry> savedEntries)
        {
            _ownedToolTiers.Clear();

            if (savedEntries == null)
            {
                return;
            }

            for (int i = 0; i < savedEntries.Count; i++)
            {
                ToolDataEntry entry = savedEntries[i];
                if (entry == null)
                {
                    continue;
                }

                string normalizedUpgradeId = NormalizeUpgradeId(entry.toolId);
                int maxTier = GetMaxUpgradeTier(normalizedUpgradeId);
                if (maxTier <= 0)
                {
                    continue;
                }

                int clampedTier = Mathf.Clamp(entry.tier, 0, maxTier);
                if (clampedTier <= 0)
                {
                    continue;
                }

                _ownedToolTiers[normalizedUpgradeId] = clampedTier;
            }
        }

        public int GetFailedLieEscalationCountThisDay()
        {
            return Mathf.Max(0, _failedLieEscalationCountThisDay);
        }

        public int GetFailedLieEscalationThresholdPerDay()
        {
            return FailedLieEscalationThresholdPerDay;
        }

        public bool RegisterFailedLieEscalation()
        {
            if (_runFailed || _currentRunPhase == RunPhase.GameOver)
            {
                return true;
            }

            if (_currentRunPhase != RunPhase.Work || _workdayCompleted)
            {
                return false;
            }

            int previousCount = Mathf.Max(0, _failedLieEscalationCountThisDay);
            _failedLieEscalationCountThisDay = previousCount + 1;

            bool crossedThreshold = previousCount < FailedLieEscalationThresholdPerDay
                && _failedLieEscalationCountThisDay >= FailedLieEscalationThresholdPerDay;
            if (!crossedThreshold)
            {
                return false;
            }

            bool triggered = TriggerGameOver(
                $"Arrested after {_failedLieEscalationCountThisDay} failed lie outcomes in one workday.");
            return triggered || _runFailed || _currentRunPhase == RunPhase.GameOver;
        }

        public bool HasDailyTaskAssignmentsForCurrentWorkday()
        {
            return _dailyTaskAssignments != null
                   && _dailyTaskAssignments.Count > 0
                   && _dailyTaskAssignmentDay == _currentDay;
        }

        public void EnsureDailyAssignmentsForCurrentWorkday()
        {
            if (_currentState != GameState.FreePlay || _currentRunPhase != RunPhase.Work)
            {
                return;
            }

            if (HasDailyTaskAssignmentsForCurrentWorkday())
            {
                return;
            }

            if (_dailyTaskAssignmentDay != _currentDay)
            {
                _dailyTaskAssignments.Clear();
            }

            GenerateDailyTaskAssignmentsForCurrentWorkday();
        }

        public bool IsAssignedDailyTask(string taskType, string taskKey)
        {
            if (!HasDailyTaskAssignmentsForCurrentWorkday())
            {
                return false;
            }

            string normalizedType = NormalizeTaskType(taskType);
            string normalizedKey = NormalizeTaskKey(taskKey);
            if (string.IsNullOrEmpty(normalizedType) || string.IsNullOrEmpty(normalizedKey))
            {
                return false;
            }

            for (int i = 0; i < _dailyTaskAssignments.Count; i++)
            {
                DailyTaskAssignment assignment = _dailyTaskAssignments[i];
                if (assignment == null)
                {
                    continue;
                }

                if (string.Equals(assignment.taskType, normalizedType, StringComparison.Ordinal)
                    && string.Equals(assignment.taskKey, normalizedKey, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryCompleteAssignedDailyTask(string taskType, string taskKey)
        {
            if (!HasDailyTaskAssignmentsForCurrentWorkday())
            {
                return false;
            }

            string normalizedType = NormalizeTaskType(taskType);
            string normalizedKey = NormalizeTaskKey(taskKey);
            if (string.IsNullOrEmpty(normalizedType) || string.IsNullOrEmpty(normalizedKey))
            {
                return false;
            }

            for (int i = 0; i < _dailyTaskAssignments.Count; i++)
            {
                DailyTaskAssignment assignment = _dailyTaskAssignments[i];
                if (assignment == null)
                {
                    continue;
                }

                if (!string.Equals(assignment.taskType, normalizedType, StringComparison.Ordinal)
                    || !string.Equals(assignment.taskKey, normalizedKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (assignment.isCompleted)
                {
                    return false;
                }

                assignment.isCompleted = true;
                return true;
            }

            return false;
        }

        public bool TryConsumeDailyTaskRewardEligibility(string minigameId)
        {
            string normalizedType = NormalizeTaskType(minigameId);
            if (!IsRewardGatedDailyTaskType(normalizedType))
            {
                return true;
            }

            if (_currentRunPhase != RunPhase.Work || _workdayCompleted)
            {
                ClearPendingDailyTaskLaunchContext();
                return false;
            }

            bool hasLaunchContext = !string.IsNullOrEmpty(_lastLaunchedDailyTaskType)
                && !string.IsNullOrEmpty(_lastLaunchedDailyTaskKey);
            if (!hasLaunchContext)
            {
                ClearPendingDailyTaskLaunchContext();
                return false;
            }

            if (!string.Equals(_lastLaunchedDailyTaskType, normalizedType, StringComparison.Ordinal))
            {
                ClearPendingDailyTaskLaunchContext();
                return false;
            }

            bool isEligible = TryCompleteAssignedDailyTask(_lastLaunchedDailyTaskType, _lastLaunchedDailyTaskKey);
            ClearPendingDailyTaskLaunchContext();
            return isEligible;
        }

        public void RegisterDailyTaskLaunchContext(string taskType, string taskKey)
        {
            _lastLaunchedDailyTaskType = NormalizeTaskType(taskType);
            _lastLaunchedDailyTaskKey = NormalizeTaskKey(taskKey);
        }

        public List<DailyTaskAssignmentData> GetDailyTaskAssignmentsForSave()
        {
            List<DailyTaskAssignmentData> savedAssignments = new List<DailyTaskAssignmentData>();
            if (_dailyTaskAssignments == null || _dailyTaskAssignments.Count == 0)
            {
                return savedAssignments;
            }

            for (int i = 0; i < _dailyTaskAssignments.Count; i++)
            {
                DailyTaskAssignment assignment = _dailyTaskAssignments[i];
                if (assignment == null)
                {
                    continue;
                }

                string normalizedType = NormalizeTaskType(assignment.taskType);
                string normalizedKey = NormalizeTaskKey(assignment.taskKey);
                if (string.IsNullOrEmpty(normalizedType) || string.IsNullOrEmpty(normalizedKey))
                {
                    continue;
                }

                savedAssignments.Add(new DailyTaskAssignmentData
                {
                    taskType = normalizedType,
                    taskKey = normalizedKey,
                    isCompleted = assignment.isCompleted
                });
            }

            return savedAssignments;
        }

        public void RestoreDailyTaskAssignmentsFromSave(List<DailyTaskAssignmentData> savedAssignments)
        {
            ClearDailyTaskAssignments();

            if (savedAssignments != null)
            {
                for (int i = 0; i < savedAssignments.Count; i++)
                {
                    DailyTaskAssignmentData saved = savedAssignments[i];
                    if (saved == null)
                    {
                        continue;
                    }

                    string normalizedType = NormalizeTaskType(saved.taskType);
                    string normalizedKey = NormalizeTaskKey(saved.taskKey);
                    if (string.IsNullOrEmpty(normalizedType) || string.IsNullOrEmpty(normalizedKey))
                    {
                        continue;
                    }

                    bool alreadyPresent = false;
                    for (int j = 0; j < _dailyTaskAssignments.Count; j++)
                    {
                        DailyTaskAssignment existing = _dailyTaskAssignments[j];
                        if (existing == null)
                        {
                            continue;
                        }

                        if (string.Equals(existing.taskType, normalizedType, StringComparison.Ordinal)
                            && string.Equals(existing.taskKey, normalizedKey, StringComparison.Ordinal))
                        {
                            alreadyPresent = true;
                            if (saved.isCompleted)
                            {
                                existing.isCompleted = true;
                            }

                            break;
                        }
                    }

                    if (alreadyPresent)
                    {
                        continue;
                    }

                    DailyTaskAssignment restored = new DailyTaskAssignment(normalizedType, normalizedKey)
                    {
                        isCompleted = saved.isCompleted
                    };

                    _dailyTaskAssignments.Add(restored);
                }
            }

            _dailyTaskAssignmentDay = _dailyTaskAssignments.Count > 0 ? _currentDay : -1;
            ClearPendingDailyTaskLaunchContext();

            if (_currentRunPhase == RunPhase.Work)
            {
                EnsureDailyAssignmentsForCurrentWorkday();
            }
        }

        public bool TryRegisterStolenLootPickup(string itemId)
        {
            string normalizedItemId = NormalizeStolenLootItemId(itemId);
            if (string.IsNullOrEmpty(normalizedItemId))
            {
                return false;
            }

            if (_currentRunPhase != RunPhase.Work || _workdayCompleted || _runFailed)
            {
                return false;
            }

            for (int i = 0; i < _stolenLootThisDay.Count; i++)
            {
                StolenLootTrackerEntry entry = _stolenLootThisDay[i];
                if (entry == null)
                {
                    continue;
                }

                if (!string.Equals(entry.itemId, normalizedItemId, StringComparison.Ordinal))
                {
                    continue;
                }

                entry.count = Mathf.Max(0, entry.count) + 1;
                return true;
            }

            _stolenLootThisDay.Add(new StolenLootTrackerEntry(normalizedItemId, 1));
            return true;
        }

        public int GetTotalStolenLootCountThisDay()
        {
            int totalCount = 0;
            for (int i = 0; i < _stolenLootThisDay.Count; i++)
            {
                StolenLootTrackerEntry entry = _stolenLootThisDay[i];
                if (entry == null || entry.count <= 0)
                {
                    continue;
                }

                totalCount += entry.count;
            }

            return totalCount;
        }

        public int GetStolenLootCountForItem(string itemId)
        {
            string normalizedItemId = NormalizeStolenLootItemId(itemId);
            if (string.IsNullOrEmpty(normalizedItemId))
            {
                return 0;
            }

            for (int i = 0; i < _stolenLootThisDay.Count; i++)
            {
                StolenLootTrackerEntry entry = _stolenLootThisDay[i];
                if (entry == null)
                {
                    continue;
                }

                if (string.Equals(entry.itemId, normalizedItemId, StringComparison.Ordinal))
                {
                    return Mathf.Max(0, entry.count);
                }
            }

            return 0;
        }

        public List<StolenLootEntryData> GetStolenLootThisDaySnapshot()
        {
            List<StolenLootEntryData> snapshot = new List<StolenLootEntryData>();
            for (int i = 0; i < _stolenLootThisDay.Count; i++)
            {
                StolenLootTrackerEntry entry = _stolenLootThisDay[i];
                if (entry == null)
                {
                    continue;
                }

                string normalizedItemId = NormalizeStolenLootItemId(entry.itemId);
                int count = Mathf.Max(0, entry.count);
                if (string.IsNullOrEmpty(normalizedItemId) || count <= 0)
                {
                    continue;
                }

                snapshot.Add(new StolenLootEntryData
                {
                    itemId = normalizedItemId,
                    count = count
                });
            }

            return snapshot;
        }

        public List<StolenLootEntryData> ConsumeDayStolenLoot()
        {
            List<StolenLootEntryData> consumedSnapshot = GetStolenLootThisDaySnapshot();
            ClearStolenLootThisDay();
            return consumedSnapshot;
        }

        public bool TrySellTrackedStolenLootInHome(out int soldItemCount, out int payoutAmount)
        {
            soldItemCount = 0;
            payoutAmount = 0;

            if (_runFailed || _currentRunPhase != RunPhase.Home)
            {
                return false;
            }

            if (_inventorySystem == null)
            {
                RefreshRuntimeBindings();
            }

            if (_inventorySystem == null)
            {
                Debug.LogWarning("[GameManager] Cannot sell tracked stolen loot because InventorySystem is unavailable.", this);
                return false;
            }

            // Consume tracker up front so repeated interactions cannot duplicate sale payout.
            List<StolenLootEntryData> trackedLootSnapshot = ConsumeDayStolenLoot();
            if (trackedLootSnapshot == null || trackedLootSnapshot.Count == 0)
            {
                return true;
            }

            int totalRemoved = 0;
            int totalPayout = 0;

            for (int i = 0; i < trackedLootSnapshot.Count; i++)
            {
                StolenLootEntryData trackedEntry = trackedLootSnapshot[i];
                if (trackedEntry == null)
                {
                    continue;
                }

                string normalizedItemId = NormalizeStolenLootItemId(trackedEntry.itemId);
                int requestedCount = Mathf.Max(0, trackedEntry.count);
                if (string.IsNullOrEmpty(normalizedItemId) || requestedCount <= 0)
                {
                    continue;
                }

                int removedCount = _inventorySystem.RemoveItemsByItemId(normalizedItemId, requestedCount);
                if (removedCount <= 0)
                {
                    continue;
                }

                totalRemoved += removedCount;
                int unitPrice = GetStolenLootSellPrice(normalizedItemId);
                if (unitPrice > 0)
                {
                    totalPayout += removedCount * unitPrice;
                }
            }

            if (totalPayout > 0)
            {
                ModifyCurrency(totalPayout);
            }

            soldItemCount = totalRemoved;
            payoutAmount = totalPayout;
            return true;
        }

        public void RestoreStolenLootThisDayFromSave(List<StolenLootEntryData> savedEntries)
        {
            ClearStolenLootThisDay();

            if (savedEntries == null)
            {
                return;
            }

            for (int i = 0; i < savedEntries.Count; i++)
            {
                StolenLootEntryData savedEntry = savedEntries[i];
                if (savedEntry == null)
                {
                    continue;
                }

                string normalizedItemId = NormalizeStolenLootItemId(savedEntry.itemId);
                int count = Mathf.Max(0, savedEntry.count);
                if (string.IsNullOrEmpty(normalizedItemId) || count <= 0)
                {
                    continue;
                }

                bool alreadyPresent = false;
                for (int j = 0; j < _stolenLootThisDay.Count; j++)
                {
                    StolenLootTrackerEntry existingEntry = _stolenLootThisDay[j];
                    if (existingEntry == null)
                    {
                        continue;
                    }

                    if (!string.Equals(existingEntry.itemId, normalizedItemId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    existingEntry.count = Mathf.Max(0, existingEntry.count) + count;
                    alreadyPresent = true;
                    break;
                }

                if (!alreadyPresent)
                {
                    _stolenLootThisDay.Add(new StolenLootTrackerEntry(normalizedItemId, count));
                }
            }
        }

        public bool CompleteWorkdayAndGoHome()
        {
            if (_currentRunPhase != RunPhase.Work)
            {
                Debug.LogWarning($"[GameManager] Cannot complete workday while in run phase '{_currentRunPhase}'.", this);
                return false;
            }

            if (_runFailed)
            {
                Debug.LogWarning("[GameManager] Cannot complete workday after run failure.", this);
                return false;
            }

            if (_workdayCompleted)
            {
                Debug.LogWarning("[GameManager] Cannot complete workday because it is already marked completed.", this);
                return false;
            }

            if (IsRunPhaseTransitionBlocked(out string blockReason))
            {
                Debug.LogWarning($"[GameManager] CompleteWorkdayAndGoHome blocked: {blockReason}", this);
                return false;
            }

            bool completedSuccessfully = IsCurrentWorkdayCompletionRuleSatisfied();
            if (completedSuccessfully)
            {
                FinalizeSuccessfulWorkday();
            }
            else
            {
                FinalizeFailedWorkday();
            }

            if (_runFailed || _currentRunPhase == RunPhase.GameOver)
            {
                return true;
            }

            _currentRunPhase = RunPhase.Home;
            return true;
        }

        public bool TryCompleteWorkdayAndRouteToHomeScene()
        {
            bool completed = CompleteWorkdayAndGoHome();
            if (!completed)
            {
                return false;
            }

            if (_runFailed || _currentRunPhase != RunPhase.Home)
            {
                return true;
            }

            TryRouteToRunPhaseScene(_currentRunPhase);
            return true;
        }

        public bool StartNextDay()
        {
            if (_currentRunPhase != RunPhase.Home)
            {
                Debug.LogWarning($"[GameManager] Cannot start next day while in run phase '{_currentRunPhase}'.", this);
                return false;
            }

            if (IsRunPhaseTransitionBlocked(out string blockReason))
            {
                Debug.LogWarning($"[GameManager] StartNextDay blocked: {blockReason}", this);
                return false;
            }

            _currentDay = Mathf.Max(1, _currentDay) + 1;
            _workdayCompleted = false;
            ClearDayWorkEarnings();
            ClearStolenLootThisDay();
            ClearFailedLieEscalationCountThisDay();
            _currentRunPhase = RunPhase.Work;
            ClearDailyTaskAssignments();
            EnsureDailyAssignmentsForCurrentWorkday();
            return true;
        }

        public bool TryStartNextDayAndRouteToGameplayScene()
        {
            bool started = StartNextDay();
            if (!started)
            {
                return false;
            }

            if (_runFailed || _currentRunPhase != RunPhase.Work)
            {
                return true;
            }

            TryRouteToRunPhaseScene(_currentRunPhase);
            return true;
        }

        public bool TriggerGameOver(string reason = null)
        {
            if (_currentRunPhase != RunPhase.GameOver && IsRunPhaseTransitionBlocked(out string blockReason))
            {
                Debug.LogWarning(
                    $"[GameManager] TriggerGameOver requested while transition was blocked: {blockReason}. " +
                    "Proceeding with fail-state hardening.",
                    this);
            }

            if (_runFailed)
            {
                return true;
            }

            _runFailed = true;
            _currentRunPhase = RunPhase.GameOver;
            _runFailedReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

            _hasRoutedAfterFailure = false;
            TryRouteToFailureTerminalScene();
            return true;
        }

        public void AddDayWorkEarnings(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            if (_currentRunPhase != RunPhase.Work || _workdayCompleted)
            {
                Debug.LogWarning(
                    $"[GameManager] Ignored day work earnings (+{amount}) outside active workday. " +
                    $"Phase={_currentRunPhase}, workdayCompleted={_workdayCompleted}.",
                    this);
                return;
            }

            _dayWorkEarnings += amount;
            EventBus.Publish(new DayWorkEarningsChangedEvent(_dayWorkEarnings));
        }

        public void ClearDayWorkEarnings()
        {
            if (_dayWorkEarnings == 0)
            {
                return;
            }

            _dayWorkEarnings = 0;
            EventBus.Publish(new DayWorkEarningsChangedEvent(_dayWorkEarnings));
        }

        public void ModifyCurrency(int amount)
        {
            if (amount == 0)
            {
                return;
            }

            _currency += amount;
            EventBus.Publish(new CurrencyChangedEvent(_currency));
        }

        public bool TrySpendCurrency(int amount)
        {
            if (amount <= 0 || _currency < amount)
            {
                return false;
            }

            _currency -= amount;
            EventBus.Publish(new CurrencyChangedEvent(_currency));
            return true;
        }

        public void RestoreCurrencyFromSave(int savedCurrency)
        {
            _currency = savedCurrency;
            EventBus.Publish(new CurrencyChangedEvent(_currency));
        }

        public void RestoreDayWorkEarningsFromSave(int savedDayWorkEarnings)
        {
            _dayWorkEarnings = Mathf.Max(0, savedDayWorkEarnings);
            EventBus.Publish(new DayWorkEarningsChangedEvent(_dayWorkEarnings));
        }

        public void RestoreRunProgressFromSave(
            int savedCurrentDay,
            int savedRunPhase,
            bool savedWorkdayCompleted,
            int savedConsecutiveFailedWorkdays,
            bool savedRunFailed,
            string savedRunFailedReason)
        {
            _currentDay = Mathf.Max(1, savedCurrentDay);
            _currentRunPhase = IsValidRunPhase(savedRunPhase) ? (RunPhase)savedRunPhase : RunPhase.Work;
            _workdayCompleted = savedWorkdayCompleted;
            _consecutiveFailedWorkdays = Mathf.Max(0, savedConsecutiveFailedWorkdays);
            _runFailedReason = string.IsNullOrWhiteSpace(savedRunFailedReason)
                ? string.Empty
                : savedRunFailedReason.Trim();

            _runFailed = savedRunFailed || _currentRunPhase == RunPhase.GameOver;
            if (_runFailed && _currentRunPhase != RunPhase.GameOver)
            {
                _currentRunPhase = RunPhase.GameOver;
            }

            if (!_runFailed)
            {
                _runFailedReason = string.Empty;
            }

            _hasRoutedAfterFailure = false;

            NormalizeRestoredRunProgressInvariants();
        }

        public void RestoreFailedLieEscalationCountThisDayFromSave(int savedCount)
        {
            _failedLieEscalationCountThisDay = Mathf.Max(0, savedCount);
        }

        public void ResetForNewRun()
        {
            _currency = _startingCurrency;
            EventBus.Publish(new CurrencyChangedEvent(_currency));

            _currentDay = 1;
            _currentRunPhase = RunPhase.Work;
            _workdayCompleted = false;
            _consecutiveFailedWorkdays = 0;
            _runFailed = false;
            _runFailedReason = string.Empty;
            _failedLieEscalationCountThisDay = 0;

            ClearDayWorkEarnings();
            ClearStolenLootThisDay();
            ClearDailyTaskAssignments();
            _ownedToolTiers.Clear();

            _hasRoutedAfterFailure = false;
            _isRunPhaseSceneRouting = false;
            _hasAttemptedInitialLoad = false;
            _initialLoadBindingRetryCount = 0;
        }

        private void NormalizeRestoredRunProgressInvariants()
        {
            List<string> normalizedIssues = new List<string>();

            if (_currentRunPhase != RunPhase.Work && _dayWorkEarnings > 0)
            {
                ClearDayWorkEarnings();
                normalizedIssues.Add("Cleared dayWorkEarnings outside Work phase");
            }

            if (_currentRunPhase == RunPhase.Work && _workdayCompleted)
            {
                _workdayCompleted = false;
                normalizedIssues.Add("Reset workdayCompleted while in Work phase");
            }

            if (_currentRunPhase == RunPhase.GameOver && !_runFailed)
            {
                _runFailed = true;
                normalizedIssues.Add("Set runFailed=true for GameOver phase");
            }
            else if (_runFailed && _currentRunPhase != RunPhase.GameOver)
            {
                _currentRunPhase = RunPhase.GameOver;
                normalizedIssues.Add("Forced GameOver phase because runFailed was true");
            }

            if (!_runFailed && !string.IsNullOrEmpty(_runFailedReason))
            {
                _runFailedReason = string.Empty;
                normalizedIssues.Add("Cleared runFailedReason while runFailed was false");
            }

            if (!_runFailed)
            {
                _hasRoutedAfterFailure = false;
            }

            if (_failedLieEscalationCountThisDay < 0)
            {
                _failedLieEscalationCountThisDay = 0;
                normalizedIssues.Add("Clamped failedLieEscalationCountThisDay to non-negative value");
            }

            if (normalizedIssues.Count > 0)
            {
                Debug.LogWarning(
                    $"[GameManager] Normalized restored run progress: {string.Join("; ", normalizedIssues)}.",
                    this);
            }
        }

        public bool TryGetAuthoritativePlayerTransform(out Transform playerTransform)
        {
            if (_freePlayState != null && _freePlayState.TryGetAuthoritativePlayerTransform(out playerTransform) && playerTransform != null)
            {
                return true;
            }

            playerTransform = null;
            return false;
        }

        public void RestorePlayerTransformFromSave(Vector3 worldPosition, Quaternion worldRotation)
        {
            if (!TryGetAuthoritativePlayerTransform(out Transform playerTransform) || playerTransform == null)
            {
                return;
            }

            CharacterController characterController = playerTransform.GetComponent<CharacterController>();
            if (characterController != null)
            {
                bool wasEnabled = characterController.enabled;
                if (wasEnabled)
                {
                    characterController.enabled = false;
                }

                playerTransform.SetPositionAndRotation(worldPosition, worldRotation);

                if (wasEnabled)
                {
                    characterController.enabled = true;
                }

                return;
            }

            playerTransform.SetPositionAndRotation(worldPosition, worldRotation);
        }

        public int GetSelectedInventorySlotIndexForSave()
        {
            InventoryGridUI inventoryGridUI = UnityEngine.Object.FindAnyObjectByType<InventoryGridUI>();
            if (inventoryGridUI == null)
            {
                return 0;
            }

            return inventoryGridUI.GetSelectedSlotIndex();
        }

        public void RestoreSelectedInventorySlotFromSave(int selectedSlotIndex)
        {
            InventoryGridUI inventoryGridUI = UnityEngine.Object.FindAnyObjectByType<InventoryGridUI>();
            if (inventoryGridUI == null)
            {
                return;
            }

            inventoryGridUI.RestoreSelectedSlotFromSave(selectedSlotIndex);
        }

        public bool IsInState(GameState state)
        {
            return _currentState == state;
        }

        private void GenerateDailyTaskAssignmentsForCurrentWorkday()
        {
            if (_currentState != GameState.FreePlay || _currentRunPhase != RunPhase.Work)
            {
                return;
            }

            List<string> cleaningKeys = CollectCleaningTaskKeysFromScene();
            List<string> weldingKeys = CollectWeldingTaskKeysFromScene();

            int cleaningAssignmentTarget = GetScaledAssignmentTargetCountForCurrentDay(DefaultCleaningAssignmentsPerDay);
            int weldingAssignmentTarget = GetScaledAssignmentTargetCountForCurrentDay(DefaultWeldingAssignmentsPerDay);

            AppendRandomAssignments(DailyTaskTypeCleaning, cleaningKeys, cleaningAssignmentTarget);
            AppendRandomAssignments(DailyTaskTypeWelding, weldingKeys, weldingAssignmentTarget);

            _dailyTaskAssignmentDay = _currentDay;
        }

        private bool IsCurrentWorkdayCompletionRuleSatisfied()
        {
            if (_currentRunPhase != RunPhase.Work)
            {
                return false;
            }

            if (!HasDailyTaskAssignmentsForCurrentWorkday())
            {
                return false;
            }

            for (int i = 0; i < _dailyTaskAssignments.Count; i++)
            {
                DailyTaskAssignment assignment = _dailyTaskAssignments[i];
                if (assignment == null || !assignment.isCompleted)
                {
                    return false;
                }
            }

            return _dailyTaskAssignments.Count > 0;
        }

        private void FinalizeSuccessfulWorkday()
        {
            _workdayCompleted = true;
            _consecutiveFailedWorkdays = 0;

            int payoutAmount = Mathf.Max(0, _dayWorkEarnings);
            if (payoutAmount > 0)
            {
                ModifyCurrency(payoutAmount);
            }

            ClearDayWorkEarnings();
        }

        private void FinalizeFailedWorkday()
        {
            _workdayCompleted = false;
            _consecutiveFailedWorkdays = Mathf.Max(0, _consecutiveFailedWorkdays) + 1;
            ClearDayWorkEarnings();

            if (!ShouldTriggerRunFailureFromFailedWorkdays())
            {
                return;
            }

            TriggerGameOver($"Reached {MaxConsecutiveFailedWorkdaysBeforeGameOver} failed workdays in a row.");
        }

        private bool ShouldTriggerRunFailureFromFailedWorkdays()
        {
            return _consecutiveFailedWorkdays >= MaxConsecutiveFailedWorkdaysBeforeGameOver;
        }

        private static List<string> CollectCleaningTaskKeysFromScene()
        {
            PipeInteractable[] interactables = UnityEngine.Object.FindObjectsByType<PipeInteractable>(FindObjectsInactive.Exclude);
            List<string> keys = new List<string>(interactables.Length);
            for (int i = 0; i < interactables.Length; i++)
            {
                PipeInteractable interactable = interactables[i];
                if (interactable == null)
                {
                    continue;
                }

                TryAddTaskKey(keys, interactable.GetDailyTaskLocationKey());
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static List<string> CollectWeldingTaskKeysFromScene()
        {
            WeldingInteractable[] interactables = UnityEngine.Object.FindObjectsByType<WeldingInteractable>(FindObjectsInactive.Exclude);
            List<string> keys = new List<string>(interactables.Length);
            for (int i = 0; i < interactables.Length; i++)
            {
                WeldingInteractable interactable = interactables[i];
                if (interactable == null)
                {
                    continue;
                }

                TryAddTaskKey(keys, interactable.GetDailyTaskLocationKey());
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private void AppendRandomAssignments(string taskType, List<string> taskKeys, int maxAssignments)
        {
            if (maxAssignments <= 0 || taskKeys == null || taskKeys.Count == 0)
            {
                return;
            }

            List<string> remaining = new List<string>(taskKeys);
            int assignCount = Mathf.Min(maxAssignments, remaining.Count);

            for (int i = 0; i < assignCount; i++)
            {
                int randomIndex = UnityEngine.Random.Range(0, remaining.Count);
                string selectedKey = remaining[randomIndex];
                remaining.RemoveAt(randomIndex);

                _dailyTaskAssignments.Add(new DailyTaskAssignment(taskType, selectedKey));
            }
        }

        private static void TryAddTaskKey(List<string> keys, string taskKey)
        {
            if (keys == null)
            {
                return;
            }

            string normalizedKey = NormalizeTaskKey(taskKey);
            if (string.IsNullOrEmpty(normalizedKey) || keys.Contains(normalizedKey))
            {
                return;
            }

            keys.Add(normalizedKey);
        }

        private static string NormalizeTaskType(string taskType)
        {
            return string.IsNullOrWhiteSpace(taskType)
                ? string.Empty
                : taskType.Trim().ToLowerInvariant();
        }

        private static bool IsRewardGatedDailyTaskType(string taskType)
        {
            return string.Equals(taskType, DailyTaskTypeCleaning, StringComparison.Ordinal)
                || string.Equals(taskType, DailyTaskTypeWelding, StringComparison.Ordinal);
        }

        private static string NormalizeTaskKey(string taskKey)
        {
            return string.IsNullOrWhiteSpace(taskKey)
                ? string.Empty
                : taskKey.Trim();
        }

        private static string NormalizeStolenLootItemId(string itemId)
        {
            return string.IsNullOrWhiteSpace(itemId)
                ? string.Empty
                : itemId.Trim();
        }

        private static string NormalizeUpgradeId(string upgradeId)
        {
            return string.IsNullOrWhiteSpace(upgradeId)
                ? string.Empty
                : upgradeId.Trim().ToLowerInvariant();
        }

        private static int GetMaxUpgradeTier(string upgradeId)
        {
            if (string.IsNullOrEmpty(upgradeId))
            {
                return 0;
            }

            return UpgradeTierCostsById.TryGetValue(upgradeId, out int[] tierCosts)
                ? Mathf.Max(0, tierCosts.Length)
                : 0;
        }

        private int GetOwnedUpgradeTier(string upgradeId)
        {
            if (string.IsNullOrEmpty(upgradeId))
            {
                return 0;
            }

            int maxTier = GetMaxUpgradeTier(upgradeId);
            if (maxTier <= 0)
            {
                return 0;
            }

            if (!_ownedToolTiers.TryGetValue(upgradeId, out int tier))
            {
                return 0;
            }

            return Mathf.Clamp(tier, 0, maxTier);
        }

        private void SetOwnedUpgradeTier(string upgradeId, int tier)
        {
            if (string.IsNullOrEmpty(upgradeId))
            {
                return;
            }

            int maxTier = GetMaxUpgradeTier(upgradeId);
            if (maxTier <= 0)
            {
                return;
            }

            int clampedTier = Mathf.Clamp(tier, 0, maxTier);
            if (clampedTier <= 0)
            {
                _ownedToolTiers.Remove(upgradeId);
                return;
            }

            _ownedToolTiers[upgradeId] = clampedTier;
        }

        private static int GetStolenLootSellPrice(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return 0;
            }

            return StolenLootSellPriceByItemId.TryGetValue(itemId.Trim(), out int price)
                ? Mathf.Max(0, price)
                : 0;
        }

        private static string ResolveRunPhaseTargetScene(RunPhase runPhase)
        {
            switch (runPhase)
            {
                case RunPhase.Work:
                    return GameplaySceneName;

                case RunPhase.Home:
                    return HomeSceneName;

                default:
                    return string.Empty;
            }
        }

        private bool TryRouteToRunPhaseScene(RunPhase runPhase)
        {
            string targetSceneName = ResolveRunPhaseTargetScene(runPhase);
            if (string.IsNullOrEmpty(targetSceneName))
            {
                return false;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid() && string.Equals(activeScene.name, targetSceneName, StringComparison.Ordinal))
            {
                return true;
            }

            if (_isRunPhaseSceneRouting)
            {
                return false;
            }

            _isRunPhaseSceneRouting = true;
            SceneManager.LoadScene(targetSceneName);
            return true;
        }

        private bool TryRouteToFailureTerminalScene()
        {
            if (!_runFailed && _currentRunPhase != RunPhase.GameOver)
            {
                return false;
            }

            if (_hasRoutedAfterFailure)
            {
                return true;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid() && string.Equals(activeScene.name, MenuSceneName, StringComparison.Ordinal))
            {
                _hasRoutedAfterFailure = true;
                return true;
            }

            if (_isRunPhaseSceneRouting)
            {
                return false;
            }

            _hasRoutedAfterFailure = true;
            _isRunPhaseSceneRouting = true;
            SceneManager.LoadScene(MenuSceneName);
            return true;
        }

        private void ReconcileSceneWithRunPhaseAfterRestore()
        {
            if (_runFailed || _currentRunPhase == RunPhase.GameOver)
            {
                _runFailed = true;
                _currentRunPhase = RunPhase.GameOver;
                TryRouteToFailureTerminalScene();
                return;
            }

            TryRouteToRunPhaseScene(_currentRunPhase);
        }

        private void ClearStolenLootThisDay()
        {
            _stolenLootThisDay.Clear();
        }

        private void ClearFailedLieEscalationCountThisDay()
        {
            _failedLieEscalationCountThisDay = 0;
        }

        private void ClearPendingDailyTaskLaunchContext()
        {
            _lastLaunchedDailyTaskType = string.Empty;
            _lastLaunchedDailyTaskKey = string.Empty;
        }

        private void ClearDailyTaskAssignments()
        {
            _dailyTaskAssignments.Clear();
            _dailyTaskAssignmentDay = -1;
            ClearPendingDailyTaskLaunchContext();
        }

        private bool IsRunPhaseTransitionBlocked(out string failureReason)
        {
            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager != null && minigameManager.IsMinigameActive())
            {
                failureReason = "A minigame is currently active.";
                return true;
            }

            failureReason = string.Empty;
            return false;
        }

        private static bool IsValidRunPhase(int runPhaseValue)
        {
            return runPhaseValue >= (int)RunPhase.Work && runPhaseValue <= (int)RunPhase.GameOver;
        }

        private void ApplySceneBindingsFrom(GameManager source)
        {
            if (source == null)
            {
                return;
            }

            if (source._menuState != null)
            {
                _menuState = source._menuState;
            }

            if (source._freePlayState != null)
            {
                _freePlayState = source._freePlayState;
            }

            if (source._minigameState != null)
            {
                _minigameState = source._minigameState;
            }

            if (source._pauseManager != null)
            {
                _pauseManager = source._pauseManager;
            }

            if (source._objectiveManager != null)
            {
                _objectiveManager = source._objectiveManager;
            }

            if (source._inventorySystem != null)
            {
                _inventorySystem = source._inventorySystem;
            }

            _currentState = source._currentState;
            RefreshRuntimeBindings();
        }

        private void RefreshRuntimeBindings()
        {
            if (PauseManager.TryGetInstance(out PauseManager pauseManager))
            {
                _pauseManager = pauseManager;
            }

            if (ObjectiveManager.TryGetInstance(out ObjectiveManager objectiveManager))
            {
                _objectiveManager = objectiveManager;
            }

            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem != null)
            {
                _inventorySystem = inventorySystem;
            }

            if (_freePlayState != null && _objectiveManager != null)
            {
                _freePlayState.RebindObjectiveManager(_objectiveManager);
            }

            RefreshInventoryGridUiBindings();
        }

        private static void RefreshInventoryGridUiBindings(bool forceFullRefresh = false)
        {
            InventoryGridUI inventoryGridUI = UnityEngine.Object.FindAnyObjectByType<InventoryGridUI>();
            if (inventoryGridUI == null)
            {
                return;
            }

            inventoryGridUI.TryRefreshHeldItemAnchorBinding();

            if (forceFullRefresh)
            {
                inventoryGridUI.RefreshAllSlots();
            }
        }

        private bool TryValidateSceneSetupForState(GameState state, out string failureReason)
        {
            List<string> missing = new List<string>();
            switch (state)
            {
                case GameState.Menu:
                    if (_menuState == null)
                    {
                        missing.Add("MenuState reference");
                    }
                    break;

                case GameState.FreePlay:
                    if (_freePlayState == null)
                    {
                        missing.Add("FreePlayState reference");
                    }

                    if (_minigameState == null)
                    {
                        missing.Add("MinigameState reference");
                    }

                    if (_pauseManager == null)
                    {
                        missing.Add("PauseManager reference");
                    }

                    if (_objectiveManager == null)
                    {
                        missing.Add("ObjectiveManager reference");
                    }

                    if (_inventorySystem == null)
                    {
                        missing.Add("InventorySystem reference");
                    }

                    if (_freePlayState != null && !_freePlayState.ValidateConfiguration(out string freePlayFailure))
                    {
                        missing.Add($"FreePlayState configuration ({freePlayFailure})");
                    }
                    break;

                case GameState.Minigame:
                    if (_minigameState == null)
                    {
                        missing.Add("MinigameState reference");
                    }

                    if (_freePlayState == null)
                    {
                        missing.Add("FreePlayState reference required for return flow");
                    }
                    break;

                default:
                    missing.Add($"Unsupported state value '{state}'");
                    break;
            }

            if (missing.Count == 0)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason = string.Join(", ", missing);
            return false;
        }

        private bool TryBuildSaveContext(out SaveManager.SaveContext context, out string failureReason)
        {
            if (_objectiveManager == null)
            {
                context = default;
                failureReason = "ObjectiveManager reference is not assigned.";
                return false;
            }

            if (_inventorySystem == null)
            {
                context = default;
                failureReason = "InventorySystem reference is not assigned.";
                return false;
            }

            context = new SaveManager.SaveContext(this, _objectiveManager, _inventorySystem);
            failureReason = string.Empty;
            return true;
        }

        private void FailCriticalSetup(string failureReason)
        {
            if (_hasCriticalSetupFailure)
            {
                return;
            }

            _hasCriticalSetupFailure = true;
            Debug.LogError($"[GameManager] Critical setup failure. {failureReason}", this);
            _currentStateImplementation = null;
            enabled = false;
        }
    }
}
