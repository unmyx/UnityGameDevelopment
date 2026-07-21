using UnityEngine;
using UnityEngine.SceneManagement;
using Game.Inventory;
using Game.Interaction;
using Game.Minigames;
using Game.Networking;
using Game.UI;
using Game.Player;
using Game.Core.Events;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;

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
        private class GeneratedTaskWave
        {
            public float unlockHour;
            public List<string> taskKeys = new List<string>();
        }

        [Serializable]
        private class StolenLootTrackerEntry
        {
            public string ownerKey;
            public string itemId;
            public int count;

            public StolenLootTrackerEntry(string ownerKey, string itemId, int count)
            {
                this.ownerKey = string.IsNullOrWhiteSpace(ownerKey)
                    ? PlayerContextRegistry.DefaultLocalPlayerId
                    : ownerKey.Trim();
                this.itemId = itemId;
                this.count = Mathf.Max(0, count);
            }
        }

        [Serializable]
        public class SellableStolenLootEntryData
        {
            public string itemId;
            public string itemName;
            public string itemDescription;
            public Sprite itemIcon;
            public int sellableCount;
            public int unitPrice;
        }

        [Serializable]
        public class HomeUpgradeStatusData
        {
            public string upgradeId;
            public string displayName;
            public int currentTier;
            public int maxTier;
            public int nextTierCost;
            public bool canPurchase;
            public string unavailableReason;
        }

        private const string DailyTaskTypeCleaning = "cleaning";
        private const string DailyTaskTypeWelding = "welding";
        private const string DailyTaskTypeMeasureCut = "measure_cut";
        private const string DailyTaskTypePipePaint = "pipe_paint";
        private const string DailyTaskTypeDrillScrew = "drill_screw";
        private const string MenuSceneName = SceneIds.Menu;
        private const string NetworkSandboxSceneName = SceneIds.NetworkSandbox;
        private const string GameplaySceneName = SceneIds.Gameplay;
        private const string HomeSceneName = SceneIds.Home;
        public const string UpgradeIdInventoryQuickSlots = "inventory_quick_slots";
        public const string UpgradeIdCleaningTool = "cleaning_tool";
        public const string UpgradeIdWeldingTool = "welding_tool";
        private const int DefaultCleaningAssignmentsPerDay = 2;
        private const int DefaultWeldingAssignmentsPerDay = 2;
        private const int DefaultMeasureCutAssignmentsPerDay = 1;
        private const int DefaultPipePaintAssignmentsPerDay = 1;
        private const int DefaultDrillScrewAssignmentsPerDay = 1;
        private const int DayDifficultyTierSpanDays = 2;
        private const int WeldingActiveTargetDayBonusSpanDays = 3;
        private const int MaxWeldingActiveTargetDayBonus = 1;
        private const float CleaningDayDifficultyStep = 0.06f;
        private const float WeldingDayDifficultyStep = 0.05f;
        private const float MeasureCutDayDifficultyStep = 0.05f;
        private const float PipePaintDayDifficultyStep = 0.05f;
        private const float DrillScrewDayDifficultyStep = 0.05f;
        private const float MinCleaningDayDifficultyMultiplier = 0.64f;
        private const float MinWeldingDayDifficultyMultiplier = 0.68f;
        private const float MinMeasureCutDayDifficultyMultiplier = 0.68f;
        private const float MinPipePaintDayDifficultyMultiplier = 0.68f;
        private const float MinDrillScrewDayDifficultyMultiplier = 0.68f;
        private const int MaxConsecutiveFailedWorkdaysBeforeGameOver = 3;
        private const int FailedLieEscalationThresholdPerDay = 3;
        private const float DefaultWorkdayStartHour = 7f;
        private const float DefaultWorkdayEndHour = 17f;

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

        [NonSerialized]
        private bool _hasBootstrapStateOverride;

        [NonSerialized]
        private GameState _bootstrapStateOverride = GameState.FreePlay;

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

        [Header("Workday Runtime")]
        [SerializeField]
        [Tooltip("Real-time minutes for a full 07:00-17:00 work shift.")]
        [Min(1f)]
        private float _workdayDurationMinutes = 12f;

        [SerializeField]
        [Tooltip("Minimum real-time delay in minutes between randomized task waves after the initial wave.")]
        [Min(0.5f)]
        private float _waveDelayMinMinutes = 2f;

        [SerializeField]
        [Tooltip("Maximum real-time delay in minutes between randomized task waves after the initial wave.")]
        [Min(0.5f)]
        private float _waveDelayMaxMinutes = 4f;

        [SerializeField]
        [Tooltip("No new task waves are scheduled or unlocked after this in-shift hour.")]
        [Range(DefaultWorkdayStartHour, DefaultWorkdayEndHour)]
        private float _lateWaveCutoffHour = 15.5f;

        [SerializeField]
        [Tooltip("Minimum in-shift minutes before 17:00 where new waves are still allowed to unlock.")]
        [Min(1f)]
        private float _lateWaveSafetyBufferMinutes = 60f;

        [SerializeField]
        [Tooltip("Current in-shift work clock hour.")]
        private float _currentWorkHour = DefaultWorkdayStartHour;

        [SerializeField]
        [Tooltip("Index of the next generated wave to unlock.")]
        private int _nextTaskWaveIndex;

        [SerializeField]
        [Tooltip("Generated unlock schedule for assignment waves in the current workday.")]
        private List<GeneratedTaskWave> _generatedTaskWaves = new List<GeneratedTaskWave>();

        [SerializeField]
        [Tooltip("Task keys that are currently unlocked and launchable.")]
        private List<string> _unlockedTaskKeys = new List<string>();

        private bool _workdayRuntimeInitialized;

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

        public void SetBootstrapStateOverride(GameState state)
        {
            _bootstrapStateOverride = state;
            _hasBootstrapStateOverride = true;
        }

        public void ClearBootstrapStateOverride()
        {
            _hasBootstrapStateOverride = false;
            _bootstrapStateOverride = GameState.FreePlay;
        }

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
            if (ShouldBypassStateBootstrapForActiveScene())
            {
                SuppressStateBootstrapForCurrentScene();
                return;
            }

            if (!DetermineAndInitializeStartingState())
            {
                return;
            }

            StartCoroutine(DeferredInitialLoad());
        }

        private void Update()
        {
            _currentStateImplementation?.OnStateUpdate();
            if (!IsNonAuthoritativeNetworkClient())
            {
                TickWorkdayRuntime();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _isRunPhaseSceneRouting = false;
            RefreshRuntimeBindings();
            if (ShouldBypassStateBootstrapForActiveScene())
            {
                SuppressStateBootstrapForCurrentScene();
                return;
            }

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

        private bool ShouldBypassStateBootstrapForActiveScene()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                return false;
            }

            return string.Equals(activeScene.name, NetworkSandboxSceneName, StringComparison.Ordinal);
        }

        private void SuppressStateBootstrapForCurrentScene()
        {
            if (_currentStateImplementation != null)
            {
                if (_currentStateImplementation is UnityEngine.Object stateObject && stateObject == null)
                {
                    _currentStateImplementation = null;
                }
                else
                {
                    try
                    {
                        _currentStateImplementation.OnStateExit();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning($"[GameManager] Ignored state exit during bootstrap-only scene bypass: {exception.Message}", this);
                    }
                }
            }

            _currentStateImplementation = null;
            _isTransitioning = false;

            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid())
            {
                Debug.Log(
                    $"[GameManager] Scene '{activeScene.name}' is bootstrap-only. State initialization is intentionally bypassed.",
                    this);
            }
        }

        private void OnMinigameStarted(MinigameStartedEvent eventData)
        {
            if (!IsLocalMinigameOwner(eventData.OwnerPlayerId))
            {
                return;
            }

            if (_currentState == GameState.Minigame)
            {
                return;
            }

            ChangeStateInternal(GameState.Minigame);
        }

        private void OnMinigameEnded(MinigameEndedEvent eventData)
        {
            if (!IsLocalMinigameOwner(eventData.OwnerPlayerId))
            {
                return;
            }

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
            if (!IsLocalMinigameOwner(eventData.OwnerPlayerId))
            {
                return;
            }

            if (_currentState == GameState.Minigame)
            {
                ChangeStateInternal(GameState.FreePlay);
            }

            ClearPendingDailyTaskLaunchContext();
        }

        private static bool IsLocalMinigameOwner(string ownerPlayerId)
        {
            if (string.IsNullOrWhiteSpace(ownerPlayerId))
            {
                return true;
            }

            return PlayerInventoryAuthority.IsLocalOwner(ownerPlayerId);
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
            if (!TryDetermineBootstrapState(out GameState bootstrapState, out string bootstrapFailure))
            {
                FailCriticalSetup($"Unable to determine startup state: {bootstrapFailure}");
                return false;
            }

            if (!_stateTransitionService.RefreshStateFromScene(bootstrapState, ref _currentState, ref _currentStateImplementation, out string transitionFailure))
            {
                FailCriticalSetup($"Failed to enter startup state '{bootstrapState}': {transitionFailure}");
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

        private bool TryDetermineBootstrapState(out GameState bootstrapState, out string failureReason)
        {
            Scene activeScene = SceneManager.GetActiveScene();
            string activeSceneName = activeScene.IsValid() ? activeScene.name : string.Empty;
            GameState configuredState = _currentState;

            if (_stateResolver == null)
            {
                bootstrapState = default;
                failureReason = "State resolver is not initialized.";
                return false;
            }

            if (_stateResolver.TryResolveStateFromSceneName(activeSceneName, out GameState sceneMappedState))
            {
                if (TryValidateSceneSetupForState(sceneMappedState, out string sceneMappingValidationFailure))
                {
                    bootstrapState = sceneMappedState;
                    failureReason = string.Empty;
                    return true;
                }

                Debug.LogWarning(
                    $"[GameManager] Scene '{activeSceneName}' maps to startup state '{sceneMappedState}', " +
                    $"but required wiring is incomplete ({sceneMappingValidationFailure}). " +
                    "Falling back to validated configured startup state.",
                    this);
            }

            if (_hasBootstrapStateOverride)
            {
                GameState sanitizedOverride = _stateResolver.SanitizeBootstrapState(_bootstrapStateOverride, out bool overrideRemappedFromMinigame);
                if (TryValidateSceneSetupForState(sanitizedOverride, out string overrideValidationFailure))
                {
                    if (overrideRemappedFromMinigame)
                    {
                        Debug.LogWarning(
                            $"[GameManager] Bootstrap override requested Minigame, remapped to '{sanitizedOverride}' for startup safety.",
                            this);
                    }

                    bootstrapState = sanitizedOverride;
                    failureReason = string.Empty;
                    return true;
                }

                Debug.LogWarning(
                    $"[GameManager] Bootstrap override '{_bootstrapStateOverride}' is invalid in scene '{activeSceneName}' ({overrideValidationFailure}). " +
                    "Falling back to validated configured startup state.",
                    this);
            }

            GameState sanitizedConfigured = _stateResolver.SanitizeBootstrapState(configuredState, out bool configuredRemappedFromMinigame);
            if (TryValidateSceneSetupForState(sanitizedConfigured, out string configuredValidationFailure))
            {
                if (configuredRemappedFromMinigame)
                {
                    Debug.LogWarning(
                        $"[GameManager] Serialized startup state '{configuredState}' is not valid for bootstrap; remapped to '{sanitizedConfigured}'.",
                        this);
                }

                if (!string.IsNullOrWhiteSpace(activeSceneName))
                {
                    Debug.LogWarning(
                        $"[GameManager] Startup state for scene '{activeSceneName}' fell back to validated serialized state '{sanitizedConfigured}'.",
                        this);
                }

                bootstrapState = sanitizedConfigured;
                failureReason = string.Empty;
                return true;
            }

            if (TryValidateSceneSetupForState(GameState.Menu, out string menuValidationFailure))
            {
                Debug.LogWarning(
                    $"[GameManager] Serialized startup state '{configuredState}' is invalid ({configuredValidationFailure}). " +
                    "Falling back to Menu state wiring.",
                    this);
                bootstrapState = GameState.Menu;
                failureReason = string.Empty;
                return true;
            }

            if (TryValidateSceneSetupForState(GameState.FreePlay, out string freePlayValidationFailure))
            {
                Debug.LogWarning(
                    $"[GameManager] Serialized startup state '{configuredState}' is invalid ({configuredValidationFailure}). " +
                    "Falling back to FreePlay state wiring.",
                    this);
                bootstrapState = GameState.FreePlay;
                failureReason = string.Empty;
                return true;
            }

            bootstrapState = default;
            failureReason =
                $"No valid startup state for scene '{activeSceneName}'. " +
                $"Configured '{configuredState}' failed ({configuredValidationFailure}); " +
                $"Menu failed ({menuValidationFailure}); FreePlay failed ({freePlayValidationFailure}).";
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

        public float GetCurrentWorkHour()
        {
            return Mathf.Clamp(_currentWorkHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour);
        }

        public float GetCurrentWorkHourForSave()
        {
            return GetCurrentWorkHour();
        }

        public int GetNextTaskWaveIndexForSave()
        {
            return Mathf.Max(0, _nextTaskWaveIndex);
        }

        public List<GeneratedTaskWaveData> GetGeneratedTaskWavesForSave()
        {
            List<GeneratedTaskWaveData> snapshot = new List<GeneratedTaskWaveData>(_generatedTaskWaves.Count);
            for (int i = 0; i < _generatedTaskWaves.Count; i++)
            {
                GeneratedTaskWave wave = _generatedTaskWaves[i];
                if (wave == null)
                {
                    continue;
                }

                GeneratedTaskWaveData data = new GeneratedTaskWaveData
                {
                    unlockHour = Mathf.Clamp(wave.unlockHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour),
                    taskKeys = new List<string>()
                };

                if (wave.taskKeys != null)
                {
                    for (int k = 0; k < wave.taskKeys.Count; k++)
                    {
                        string normalized = NormalizeTaskKey(wave.taskKeys[k]);
                        if (!string.IsNullOrEmpty(normalized) && !data.taskKeys.Contains(normalized))
                        {
                            data.taskKeys.Add(normalized);
                        }
                    }
                }

                snapshot.Add(data);
            }

            return snapshot;
        }

        public List<string> GetUnlockedTaskKeysForSave()
        {
            List<string> snapshot = new List<string>(_unlockedTaskKeys.Count);
            for (int i = 0; i < _unlockedTaskKeys.Count; i++)
            {
                string normalized = NormalizeTaskKey(_unlockedTaskKeys[i]);
                if (!string.IsNullOrEmpty(normalized) && !snapshot.Contains(normalized))
                {
                    snapshot.Add(normalized);
                }
            }

            return snapshot;
        }

        public bool TryGetNextTaskWaveEtaSeconds(out float etaSeconds)
        {
            etaSeconds = 0f;

            if (_nextTaskWaveIndex < 0 || _nextTaskWaveIndex >= _generatedTaskWaves.Count)
            {
                return false;
            }

            GeneratedTaskWave nextWave = _generatedTaskWaves[_nextTaskWaveIndex];
            if (nextWave == null)
            {
                return false;
            }

            float deltaHours = Mathf.Max(0f, nextWave.unlockHour - GetCurrentWorkHour());
            etaSeconds = deltaHours * 3600f;
            return true;
        }

        public void GetDailyTaskProgress(out int cleaningCompleted, out int cleaningTotal, out int weldingCompleted, out int weldingTotal)
        {
            cleaningCompleted = 0;
            cleaningTotal = 0;
            weldingCompleted = 0;
            weldingTotal = 0;

            for (int i = 0; i < _dailyTaskAssignments.Count; i++)
            {
                DailyTaskAssignment assignment = _dailyTaskAssignments[i];
                if (assignment == null)
                {
                    continue;
                }

                if (string.Equals(assignment.taskType, DailyTaskTypeCleaning, StringComparison.Ordinal))
                {
                    cleaningTotal++;
                    if (assignment.isCompleted)
                    {
                        cleaningCompleted++;
                    }

                    continue;
                }

                if (string.Equals(assignment.taskType, DailyTaskTypeWelding, StringComparison.Ordinal))
                {
                    weldingTotal++;
                    if (assignment.isCompleted)
                    {
                        weldingCompleted++;
                    }
                }
            }
        }

        public void GetActiveWaveTaskProgress(
            out int cleaningCompleted,
            out int cleaningTotal,
            out int weldingCompleted,
            out int weldingTotal,
            out int measureCutCompleted,
            out int measureCutTotal,
            out int pipePaintCompleted,
            out int pipePaintTotal,
            out int drillScrewCompleted,
            out int drillScrewTotal,
            out bool hasPendingWave,
            out float nextWaveEtaSeconds)
        {
            cleaningCompleted = 0;
            cleaningTotal = 0;
            weldingCompleted = 0;
            weldingTotal = 0;
            measureCutCompleted = 0;
            measureCutTotal = 0;
            pipePaintCompleted = 0;
            pipePaintTotal = 0;
            drillScrewCompleted = 0;
            drillScrewTotal = 0;

            EnsureDailyAssignmentsForCurrentWorkday();
            EnsureWorkdayRuntimeInitialized();

            for (int i = 0; i < _dailyTaskAssignments.Count; i++)
            {
                DailyTaskAssignment assignment = _dailyTaskAssignments[i];
                if (assignment == null)
                {
                    continue;
                }

                if (!IsTaskKeyUnlocked(assignment.taskKey))
                {
                    continue;
                }

                if (string.Equals(assignment.taskType, DailyTaskTypeCleaning, StringComparison.Ordinal))
                {
                    cleaningTotal++;
                    if (assignment.isCompleted)
                    {
                        cleaningCompleted++;
                    }

                    continue;
                }

                if (string.Equals(assignment.taskType, DailyTaskTypeWelding, StringComparison.Ordinal))
                {
                    weldingTotal++;
                    if (assignment.isCompleted)
                    {
                        weldingCompleted++;
                    }

                    continue;
                }

                if (string.Equals(assignment.taskType, DailyTaskTypeMeasureCut, StringComparison.Ordinal))
                {
                    measureCutTotal++;
                    if (assignment.isCompleted)
                    {
                        measureCutCompleted++;
                    }
                    continue;
                }

                if (string.Equals(assignment.taskType, DailyTaskTypePipePaint, StringComparison.Ordinal))
                {
                    pipePaintTotal++;
                    if (assignment.isCompleted)
                    {
                        pipePaintCompleted++;
                    }

                    continue;
                }

                if (string.Equals(assignment.taskType, DailyTaskTypeDrillScrew, StringComparison.Ordinal))
                {
                    drillScrewTotal++;
                    if (assignment.isCompleted)
                    {
                        drillScrewCompleted++;
                    }
                }
            }

            hasPendingWave = TryGetNextTaskWaveEtaSeconds(out float etaSeconds);
            nextWaveEtaSeconds = hasPendingWave ? etaSeconds : 0f;
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

        public float GetMeasureCutDayDifficultyMultiplier()
        {
            float scaledMultiplier = 1f - (MeasureCutDayDifficultyStep * GetCurrentDayDifficultyTier());
            return Mathf.Clamp(scaledMultiplier, MinMeasureCutDayDifficultyMultiplier, 1f);
        }

        public float GetPipePaintDayDifficultyMultiplier()
        {
            float scaledMultiplier = 1f - (PipePaintDayDifficultyStep * GetCurrentDayDifficultyTier());
            return Mathf.Clamp(scaledMultiplier, MinPipePaintDayDifficultyMultiplier, 1f);
        }

        public float GetDrillScrewDayDifficultyMultiplier()
        {
            float scaledMultiplier = 1f - (DrillScrewDayDifficultyStep * GetCurrentDayDifficultyTier());
            return Mathf.Clamp(scaledMultiplier, MinDrillScrewDayDifficultyMultiplier, 1f);
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
            return InventoryQuickSlotRules.MaxQuickSlots;
        }

        public bool IsTrackedStolenLootItem(string itemId)
        {
            string normalizedItemId = NormalizeStolenLootItemId(itemId);
            if (string.IsNullOrEmpty(normalizedItemId))
            {
                return false;
            }

            return GetStolenLootSellPrice(normalizedItemId) > 0;
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
            if (IsNonAuthoritativeNetworkClient())
            {
                return;
            }

            if (_currentState != GameState.FreePlay || _currentRunPhase != RunPhase.Work)
            {
                return;
            }

            if (HasDailyTaskAssignmentsForCurrentWorkday())
            {
                EnsureWorkdayRuntimeInitialized();
                return;
            }

            if (_dailyTaskAssignmentDay != _currentDay)
            {
                _dailyTaskAssignments.Clear();
            }

            GenerateDailyTaskAssignmentsForCurrentWorkday();
            EnsureWorkdayRuntimeInitialized();
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
            if (IsNonAuthoritativeNetworkClient())
            {
                return false;
            }

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
            if (IsNonAuthoritativeNetworkClient())
            {
                return false;
            }

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
            if (IsNonAuthoritativeNetworkClient())
            {
                if (NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority authority))
                {
                    authority.RequestRegisterDailyTaskLaunchContext(taskType, taskKey);
                }
                return;
            }

            string normalizedType = NormalizeTaskType(taskType);
            string normalizedKey = NormalizeTaskKey(taskKey);
            if (string.IsNullOrEmpty(normalizedType) || string.IsNullOrEmpty(normalizedKey))
            {
                ClearPendingDailyTaskLaunchContext();
                return;
            }

            if (!CanLaunchTaskAtLocation(normalizedType, normalizedKey, out _))
            {
                ClearPendingDailyTaskLaunchContext();
                return;
            }

            _lastLaunchedDailyTaskType = normalizedType;
            _lastLaunchedDailyTaskKey = normalizedKey;
        }

        public bool CanLaunchTaskAtLocation(string taskType, string taskKey, out string reason)
        {
            reason = string.Empty;

            if (_runFailed || _currentRunPhase != RunPhase.Work || _workdayCompleted)
            {
                reason = "Work tasks are unavailable right now.";
                return false;
            }

            EnsureDailyAssignmentsForCurrentWorkday();
            EnsureWorkdayRuntimeInitialized();

            if (GetCurrentWorkHour() >= DefaultWorkdayEndHour)
            {
                reason = "Shift has ended for today.";
                return false;
            }

            string normalizedType = NormalizeTaskType(taskType);
            string normalizedKey = NormalizeTaskKey(taskKey);
            if (string.IsNullOrEmpty(normalizedType) || string.IsNullOrEmpty(normalizedKey))
            {
                reason = "Task is unavailable.";
                return false;
            }

            if (!IsAssignedDailyTask(normalizedType, normalizedKey))
            {
                reason = "This station is not assigned right now.";
                return false;
            }

            if (!IsTaskKeyUnlocked(normalizedKey))
            {
                reason = "This task wave is not unlocked yet.";
                return false;
            }

            if (IsAssignedDailyTaskCompleted(normalizedType, normalizedKey))
            {
                reason = "This task is already completed.";
                return false;
            }

            return true;
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

            bool addedCompatibilityAssignment = TryBackfillDevTestingAssignmentsForLegacyWorkday();
            _dailyTaskAssignmentDay = _dailyTaskAssignments.Count > 0 ? _currentDay : -1;
            ClearPendingDailyTaskLaunchContext();

            if (_currentRunPhase == RunPhase.Work)
            {
                EnsureDailyAssignmentsForCurrentWorkday();
                if (addedCompatibilityAssignment)
                {
                    RebuildTaskWaveScheduleForCurrentWorkday();
                }
                else
                {
                    EnsureWorkdayRuntimeInitialized();
                }
            }
        }

        public void RestoreWorkdayRuntimeFromSave(
            float savedCurrentWorkHour,
            int savedNextTaskWaveIndex,
            List<GeneratedTaskWaveData> savedGeneratedTaskWaves,
            List<string> savedUnlockedTaskKeys)
        {
            _currentWorkHour = Mathf.Clamp(savedCurrentWorkHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour);
            _nextTaskWaveIndex = Mathf.Max(0, savedNextTaskWaveIndex);
            _generatedTaskWaves.Clear();
            _unlockedTaskKeys.Clear();

            if (savedGeneratedTaskWaves != null)
            {
                for (int i = 0; i < savedGeneratedTaskWaves.Count; i++)
                {
                    GeneratedTaskWaveData savedWave = savedGeneratedTaskWaves[i];
                    if (savedWave == null)
                    {
                        continue;
                    }

                    GeneratedTaskWave runtimeWave = new GeneratedTaskWave
                    {
                        unlockHour = Mathf.Clamp(savedWave.unlockHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour),
                        taskKeys = new List<string>()
                    };

                    if (savedWave.taskKeys != null)
                    {
                        for (int k = 0; k < savedWave.taskKeys.Count; k++)
                        {
                            string normalized = NormalizeTaskKey(savedWave.taskKeys[k]);
                            if (!string.IsNullOrEmpty(normalized) && !runtimeWave.taskKeys.Contains(normalized))
                            {
                                runtimeWave.taskKeys.Add(normalized);
                            }
                        }
                    }

                    _generatedTaskWaves.Add(runtimeWave);
                }
            }

            if (_nextTaskWaveIndex > _generatedTaskWaves.Count)
            {
                _nextTaskWaveIndex = _generatedTaskWaves.Count;
            }

            if (savedUnlockedTaskKeys != null)
            {
                for (int i = 0; i < savedUnlockedTaskKeys.Count; i++)
                {
                    AddUnlockedTaskKey(savedUnlockedTaskKeys[i]);
                }
            }

            _workdayRuntimeInitialized = _generatedTaskWaves.Count > 0 || _unlockedTaskKeys.Count > 0;

            if (_currentRunPhase == RunPhase.Work)
            {
                EnsureDailyAssignmentsForCurrentWorkday();
                EnsureWorkdayRuntimeInitialized();
                CatchUpDueTaskWaves();
            }
        }

        public void ApplyNetworkProgressScalarState(
            int currentDay,
            int runPhase,
            bool workdayCompleted,
            float currentWorkHour,
            int nextTaskWaveIndex,
            bool runFailed)
        {
            _currentDay = Mathf.Max(1, currentDay);
            _currentRunPhase = IsValidRunPhase(runPhase) ? (RunPhase)runPhase : RunPhase.Work;
            _workdayCompleted = workdayCompleted;
            _currentWorkHour = Mathf.Clamp(currentWorkHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour);
            _nextTaskWaveIndex = Mathf.Max(0, nextTaskWaveIndex);
            _runFailed = runFailed || _currentRunPhase == RunPhase.GameOver;
            if (_runFailed && _currentRunPhase != RunPhase.GameOver)
            {
                _currentRunPhase = RunPhase.GameOver;
            }
        }

        public void ApplyNetworkEconomyScalarState(int currency, int dayWorkEarnings)
        {
            int normalizedCurrency = currency;
            int normalizedDayWorkEarnings = Mathf.Max(0, dayWorkEarnings);

            if (_currency != normalizedCurrency)
            {
                _currency = normalizedCurrency;
                EventBus.Publish(new CurrencyChangedEvent(_currency));
            }

            if (_dayWorkEarnings != normalizedDayWorkEarnings)
            {
                _dayWorkEarnings = normalizedDayWorkEarnings;
                EventBus.Publish(new DayWorkEarningsChangedEvent(_dayWorkEarnings));
            }
        }

        public void ApplyNetworkProgressComplexState(
            List<DailyTaskAssignmentData> dailyTaskAssignments,
            List<GeneratedTaskWaveData> generatedTaskWaves,
            List<string> unlockedTaskKeys,
            List<ToolDataEntry> ownedTools)
        {
            _dailyTaskAssignments.Clear();
            if (dailyTaskAssignments != null)
            {
                for (int i = 0; i < dailyTaskAssignments.Count; i++)
                {
                    DailyTaskAssignmentData saved = dailyTaskAssignments[i];
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

                    DailyTaskAssignment restored = new DailyTaskAssignment(normalizedType, normalizedKey)
                    {
                        isCompleted = saved.isCompleted
                    };
                    _dailyTaskAssignments.Add(restored);
                }
            }

            bool addedCompatibilityAssignment = TryBackfillDevTestingAssignmentsForLegacyWorkday();
            _dailyTaskAssignmentDay = _dailyTaskAssignments.Count > 0 ? _currentDay : -1;
            _generatedTaskWaves.Clear();
            _unlockedTaskKeys.Clear();

            if (generatedTaskWaves != null)
            {
                for (int i = 0; i < generatedTaskWaves.Count; i++)
                {
                    GeneratedTaskWaveData savedWave = generatedTaskWaves[i];
                    if (savedWave == null)
                    {
                        continue;
                    }

                    GeneratedTaskWave runtimeWave = new GeneratedTaskWave
                    {
                        unlockHour = Mathf.Clamp(savedWave.unlockHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour),
                        taskKeys = new List<string>()
                    };

                    if (savedWave.taskKeys != null)
                    {
                        for (int k = 0; k < savedWave.taskKeys.Count; k++)
                        {
                            string normalizedKey = NormalizeTaskKey(savedWave.taskKeys[k]);
                            if (!string.IsNullOrEmpty(normalizedKey) && !runtimeWave.taskKeys.Contains(normalizedKey))
                            {
                                runtimeWave.taskKeys.Add(normalizedKey);
                            }
                        }
                    }

                    _generatedTaskWaves.Add(runtimeWave);
                }
            }

            if (unlockedTaskKeys != null)
            {
                for (int i = 0; i < unlockedTaskKeys.Count; i++)
                {
                    AddUnlockedTaskKey(unlockedTaskKeys[i]);
                }
            }

            if (_nextTaskWaveIndex > _generatedTaskWaves.Count)
            {
                _nextTaskWaveIndex = _generatedTaskWaves.Count;
            }

            _workdayRuntimeInitialized = _generatedTaskWaves.Count > 0 || _unlockedTaskKeys.Count > 0;
            if (addedCompatibilityAssignment)
            {
                RebuildTaskWaveScheduleForCurrentWorkday();
            }
            RestoreOwnedToolUpgradesFromSave(ownedTools);
            ClearPendingDailyTaskLaunchContext();
        }

        // DEVELOPMENT/TEMPORARY TEST SUPPORT:
        // Backward-compatibility backfill for saves/workdays created before newer work minigames existed.
        // This preserves existing assignments and only adds at most one assignment per supported type when missing.
        private bool TryBackfillDevTestingAssignmentsForLegacyWorkday()
        {
            if (_currentRunPhase != RunPhase.Work || _dailyTaskAssignments == null || _dailyTaskAssignments.Count <= 0)
            {
                return false;
            }

            bool changed = false;
            changed |= TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypeCleaning, CollectCleaningTaskKeysFromScene());
            changed |= TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypeWelding, CollectWeldingTaskKeysFromScene());
            changed |= TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypeMeasureCut, CollectMeasureCutTaskKeysFromScene());
            changed |= TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypePipePaint, CollectPipePaintTaskKeysFromScene());
            changed |= TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypeDrillScrew, CollectDrillScrewTaskKeysFromScene());
            return changed;
        }

        // DEVELOPMENT/TEMPORARY TEST SUPPORT:
        // Guarantees that a task type can be tested when a valid station exists in-scene.
        private bool TryEnsureAtLeastOneAssignmentForTaskType(string taskType, List<string> taskKeys)
        {
            if (string.IsNullOrEmpty(taskType) || taskKeys == null || taskKeys.Count <= 0)
            {
                return false;
            }

            string normalizedType = NormalizeTaskType(taskType);
            if (string.IsNullOrEmpty(normalizedType))
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

                if (string.Equals(NormalizeTaskType(assignment.taskType), normalizedType, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            for (int i = 0; i < taskKeys.Count; i++)
            {
                string normalizedKey = NormalizeTaskKey(taskKeys[i]);
                if (string.IsNullOrEmpty(normalizedKey))
                {
                    continue;
                }

                bool alreadyAssigned = false;
                for (int j = 0; j < _dailyTaskAssignments.Count; j++)
                {
                    DailyTaskAssignment existing = _dailyTaskAssignments[j];
                    if (existing == null)
                    {
                        continue;
                    }

                    if (string.Equals(NormalizeTaskType(existing.taskType), normalizedType, StringComparison.Ordinal)
                        && string.Equals(NormalizeTaskKey(existing.taskKey), normalizedKey, StringComparison.Ordinal))
                    {
                        alreadyAssigned = true;
                        break;
                    }
                }

                if (alreadyAssigned)
                {
                    continue;
                }

                _dailyTaskAssignments.Add(new DailyTaskAssignment(normalizedType, normalizedKey));
                return true;
            }

            return false;
        }

        public bool TryRegisterStolenLootPickup(string itemId)
        {
            return TryRegisterStolenLootPickup(itemId, PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public bool TryRegisterStolenLootPickup(string itemId, string ownerPlayerId)
        {
            ValidateLocalOwnerStoragePath(ownerPlayerId, nameof(TryRegisterStolenLootPickup));
            string normalizedOwnerKey = NormalizeOwnerKey(ownerPlayerId);
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

                if (!string.Equals(entry.ownerKey, normalizedOwnerKey, StringComparison.Ordinal))
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

            _stolenLootThisDay.Add(new StolenLootTrackerEntry(normalizedOwnerKey, normalizedItemId, 1));
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
            return GetStolenLootCountForItem(itemId, PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public int GetStolenLootCountForItem(string itemId, string ownerPlayerId)
        {
            ValidateLocalOwnerStoragePath(ownerPlayerId, nameof(GetStolenLootCountForItem));
            string normalizedOwnerKey = NormalizeOwnerKey(ownerPlayerId);
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

                if (!string.Equals(entry.ownerKey, normalizedOwnerKey, StringComparison.Ordinal))
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

        public bool TryUnregisterStolenLootForDrop(string itemId, int amount = 1)
        {
            return TryUnregisterStolenLootForDrop(itemId, PlayerInventoryAuthority.GetLocalOwnerPlayerId(), amount);
        }

        public bool TryUnregisterStolenLootForDrop(string itemId, string ownerPlayerId, int amount = 1)
        {
            ValidateLocalOwnerStoragePath(ownerPlayerId, nameof(TryUnregisterStolenLootForDrop));
            string normalizedOwnerKey = NormalizeOwnerKey(ownerPlayerId);
            string normalizedItemId = NormalizeStolenLootItemId(itemId);
            if (string.IsNullOrEmpty(normalizedItemId) || amount <= 0)
            {
                return false;
            }

            int remainingToRemove = amount;
            for (int i = _stolenLootThisDay.Count - 1; i >= 0; i--)
            {
                StolenLootTrackerEntry entry = _stolenLootThisDay[i];
                if (entry == null
                    || !string.Equals(entry.ownerKey, normalizedOwnerKey, StringComparison.Ordinal)
                    || !string.Equals(entry.itemId, normalizedItemId, StringComparison.Ordinal))
                {
                    continue;
                }

                int currentCount = Mathf.Max(0, entry.count);
                int removed = Mathf.Min(currentCount, remainingToRemove);
                if (removed <= 0)
                {
                    continue;
                }

                entry.count = currentCount - removed;
                remainingToRemove -= removed;

                if (entry.count <= 0)
                {
                    _stolenLootThisDay.RemoveAt(i);
                }

                if (remainingToRemove <= 0)
                {
                    break;
                }
            }

            return remainingToRemove < amount;
        }

        public List<StolenLootEntryData> GetStolenLootThisDaySnapshot()
        {
            return GetStolenLootThisDaySnapshot(PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public List<StolenLootEntryData> GetStolenLootThisDaySnapshot(string ownerPlayerId)
        {
            ValidateLocalOwnerStoragePath(ownerPlayerId, nameof(GetStolenLootThisDaySnapshot));
            string normalizedOwnerKey = NormalizeOwnerKey(ownerPlayerId);
            List<StolenLootEntryData> snapshot = new List<StolenLootEntryData>();
            for (int i = 0; i < _stolenLootThisDay.Count; i++)
            {
                StolenLootTrackerEntry entry = _stolenLootThisDay[i];
                if (entry == null)
                {
                    continue;
                }

                if (!string.Equals(entry.ownerKey, normalizedOwnerKey, StringComparison.Ordinal))
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
                    ownerKey = normalizedOwnerKey,
                    itemId = normalizedItemId,
                    count = count
                });
            }

            return snapshot;
        }

        public List<StolenLootEntryData> GetStolenLootThisDaySnapshotForAllOwners()
        {
            List<StolenLootEntryData> snapshot = new List<StolenLootEntryData>();
            for (int i = 0; i < _stolenLootThisDay.Count; i++)
            {
                StolenLootTrackerEntry entry = _stolenLootThisDay[i];
                if (entry == null)
                {
                    continue;
                }

                string normalizedOwnerKey = NormalizeOwnerKey(entry.ownerKey);
                string normalizedItemId = NormalizeStolenLootItemId(entry.itemId);
                int count = Mathf.Max(0, entry.count);
                if (string.IsNullOrEmpty(normalizedItemId) || count <= 0)
                {
                    continue;
                }

                snapshot.Add(new StolenLootEntryData
                {
                    ownerKey = normalizedOwnerKey,
                    itemId = normalizedItemId,
                    count = count
                });
            }

            return snapshot;
        }

        public List<StolenLootEntryData> ConsumeDayStolenLoot()
        {
            return ConsumeDayStolenLoot(PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public List<StolenLootEntryData> ConsumeDayStolenLoot(string ownerPlayerId)
        {
            string normalizedOwnerKey = NormalizeOwnerKey(ownerPlayerId);
            List<StolenLootEntryData> consumedSnapshot = GetStolenLootThisDaySnapshot(ownerPlayerId);
            ClearStolenLootThisDay(normalizedOwnerKey);
            return consumedSnapshot;
        }

        public bool TrySellTrackedStolenLootInHome(out int soldItemCount, out int payoutAmount)
        {
            return TrySellTrackedStolenLootInHome(
                PlayerInventoryAuthority.GetLocalOwnerPlayerId(),
                out soldItemCount,
                out payoutAmount);
        }

        public bool TrySellTrackedStolenLootInHome(string ownerPlayerId, out int soldItemCount, out int payoutAmount)
        {
            ValidateLocalOwnerStoragePath(ownerPlayerId, nameof(TrySellTrackedStolenLootInHome));
            bool isRemoteNetworkOwner = IsRemoteNetworkOwnerOnAuthoritativeServer(ownerPlayerId);
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

            List<StolenLootEntryData> trackedLootSnapshot = GetStolenLootThisDaySnapshot(ownerPlayerId);
            if (trackedLootSnapshot == null || trackedLootSnapshot.Count == 0)
            {
                return true;
            }

            int totalRemoved = 0;
            int totalPayout = 0;
            List<StolenLootEntryData> remainingTrackedLoot = new List<StolenLootEntryData>(trackedLootSnapshot.Count);

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

                int removedCount = isRemoteNetworkOwner
                    ? requestedCount
                    : _inventorySystem.RemoveItemsByItemId(normalizedItemId, requestedCount, ownerPlayerId);
                if (removedCount > 0)
                {
                    totalRemoved += removedCount;
                    int unitPrice = GetStolenLootSellPrice(normalizedItemId);
                    if (unitPrice > 0)
                    {
                        totalPayout += removedCount * unitPrice;
                    }
                }

                int remainingCount = Mathf.Max(0, requestedCount - removedCount);
                if (remainingCount > 0)
                {
                    remainingTrackedLoot.Add(new StolenLootEntryData
                    {
                        itemId = normalizedItemId,
                        count = remainingCount
                    });
                }
            }

            RestoreStolenLootThisDayFromSave(remainingTrackedLoot, ownerPlayerId);

            if (totalPayout > 0)
            {
                ModifyCurrency(totalPayout);
            }

            soldItemCount = totalRemoved;
            payoutAmount = totalPayout;
            return true;
        }

        public bool TrySellTrackedStolenLootItemUnitInHome(
            string itemId,
            out int payoutAmount,
            out int remainingTrackedCount)
        {
            return TrySellTrackedStolenLootItemUnitInHome(
                itemId,
                PlayerInventoryAuthority.GetLocalOwnerPlayerId(),
                out payoutAmount,
                out remainingTrackedCount);
        }

        public bool TrySellTrackedStolenLootItemUnitInHome(
            string itemId,
            string ownerPlayerId,
            out int payoutAmount,
            out int remainingTrackedCount)
        {
            ValidateLocalOwnerStoragePath(ownerPlayerId, nameof(TrySellTrackedStolenLootItemUnitInHome));
            bool isRemoteNetworkOwner = IsRemoteNetworkOwnerOnAuthoritativeServer(ownerPlayerId);
            payoutAmount = 0;
            remainingTrackedCount = 0;

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
                return false;
            }

            string normalizedItemId = NormalizeStolenLootItemId(itemId);
            if (string.IsNullOrEmpty(normalizedItemId))
            {
                return false;
            }

            int trackedCount = GetStolenLootCountForItem(normalizedItemId, ownerPlayerId);
            if (trackedCount <= 0)
            {
                return false;
            }

            int unitPrice = GetStolenLootSellPrice(normalizedItemId);
            if (unitPrice <= 0)
            {
                return false;
            }

            int removedCount = isRemoteNetworkOwner
                ? 1
                : _inventorySystem.RemoveItemsByItemId(normalizedItemId, 1, ownerPlayerId);
            if (removedCount <= 0)
            {
                return false;
            }

            payoutAmount = unitPrice * removedCount;
            if (payoutAmount > 0)
            {
                ModifyCurrency(payoutAmount);
            }

            List<StolenLootEntryData> trackedLootSnapshot = GetStolenLootThisDaySnapshot(ownerPlayerId);
            List<StolenLootEntryData> updatedTrackedLoot = new List<StolenLootEntryData>(trackedLootSnapshot.Count);

            for (int i = 0; i < trackedLootSnapshot.Count; i++)
            {
                StolenLootEntryData trackedEntry = trackedLootSnapshot[i];
                if (trackedEntry == null)
                {
                    continue;
                }

                string normalizedTrackedId = NormalizeStolenLootItemId(trackedEntry.itemId);
                int count = Mathf.Max(0, trackedEntry.count);
                if (string.IsNullOrEmpty(normalizedTrackedId) || count <= 0)
                {
                    continue;
                }

                if (string.Equals(normalizedTrackedId, normalizedItemId, StringComparison.Ordinal))
                {
                    count = Mathf.Max(0, count - removedCount);
                }

                if (count <= 0)
                {
                    continue;
                }

                updatedTrackedLoot.Add(new StolenLootEntryData
                {
                    itemId = normalizedTrackedId,
                    count = count
                });
            }

            RestoreStolenLootThisDayFromSave(updatedTrackedLoot, ownerPlayerId);
            remainingTrackedCount = GetStolenLootCountForItem(normalizedItemId, ownerPlayerId);
            return true;
        }

        public List<SellableStolenLootEntryData> GetSellableStolenLootEntriesInHome()
        {
            return GetSellableStolenLootEntriesInHome(PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public List<SellableStolenLootEntryData> GetSellableStolenLootEntriesInHome(string ownerPlayerId)
        {
            ValidateLocalOwnerStoragePath(ownerPlayerId, nameof(GetSellableStolenLootEntriesInHome));
            bool isRemoteNetworkOwner = IsRemoteNetworkOwnerOnAuthoritativeServer(ownerPlayerId);
            List<SellableStolenLootEntryData> entries = new List<SellableStolenLootEntryData>();
            List<StolenLootEntryData> trackedLootSnapshot = GetStolenLootThisDaySnapshot(ownerPlayerId);
            if (trackedLootSnapshot == null || trackedLootSnapshot.Count <= 0)
            {
                return entries;
            }

            if (!isRemoteNetworkOwner && _inventorySystem == null)
            {
                RefreshRuntimeBindings();
            }

            if (!isRemoteNetworkOwner && _inventorySystem == null)
            {
                return entries;
            }

            Dictionary<string, int> inventoryCountsByItemId = new Dictionary<string, int>(StringComparer.Ordinal);
            if (!isRemoteNetworkOwner)
            {
                List<InventoryItem> inventoryItems = _inventorySystem.GetAllItems();
                for (int i = 0; i < inventoryItems.Count; i++)
                {
                    InventoryItem inventoryItem = inventoryItems[i];
                    if (inventoryItem == null || !inventoryItem.IsValid())
                    {
                        continue;
                    }

                    string normalizedItemId = NormalizeStolenLootItemId(inventoryItem.ItemId);
                    if (string.IsNullOrEmpty(normalizedItemId))
                    {
                        continue;
                    }

                    if (!inventoryCountsByItemId.TryGetValue(normalizedItemId, out int currentCount))
                    {
                        currentCount = 0;
                    }

                    inventoryCountsByItemId[normalizedItemId] = currentCount + 1;
                }
            }

            for (int i = 0; i < trackedLootSnapshot.Count; i++)
            {
                StolenLootEntryData trackedEntry = trackedLootSnapshot[i];
                if (trackedEntry == null)
                {
                    continue;
                }

                string normalizedItemId = NormalizeStolenLootItemId(trackedEntry.itemId);
                int trackedCount = Mathf.Max(0, trackedEntry.count);
                if (string.IsNullOrEmpty(normalizedItemId) || trackedCount <= 0)
                {
                    continue;
                }

                int inventoryCount = trackedCount;
                if (!isRemoteNetworkOwner
                    && (!inventoryCountsByItemId.TryGetValue(normalizedItemId, out inventoryCount) || inventoryCount <= 0))
                {
                    continue;
                }

                int unitPrice = GetStolenLootSellPrice(normalizedItemId);
                if (unitPrice <= 0)
                {
                    continue;
                }

                int sellableCount = Mathf.Min(trackedCount, inventoryCount);
                if (sellableCount <= 0)
                {
                    continue;
                }

                InventoryItem itemAsset = _inventorySystem.LoadItemById(normalizedItemId);
                entries.Add(new SellableStolenLootEntryData
                {
                    itemId = normalizedItemId,
                    itemName = itemAsset != null && !string.IsNullOrWhiteSpace(itemAsset.ItemName)
                        ? itemAsset.ItemName
                        : normalizedItemId,
                    itemDescription = itemAsset != null ? itemAsset.ItemDescription : string.Empty,
                    itemIcon = itemAsset != null ? itemAsset.ItemIcon : null,
                    sellableCount = sellableCount,
                    unitPrice = unitPrice
                });
            }

            return entries;
        }

        public List<HomeUpgradeStatusData> GetHomeUpgradeStatusEntries()
        {
            List<HomeUpgradeStatusData> statuses = new List<HomeUpgradeStatusData>(SupportedUpgradeIds.Length);
            for (int i = 0; i < SupportedUpgradeIds.Length; i++)
            {
                string upgradeId = SupportedUpgradeIds[i];
                HomeUpgradeStatusData status = BuildHomeUpgradeStatus(upgradeId);
                if (status != null)
                {
                    statuses.Add(status);
                }
            }

            return statuses;
        }

        public void RestoreStolenLootThisDayFromSave(List<StolenLootEntryData> savedEntries)
        {
            RestoreStolenLootThisDayFromSave(savedEntries, PlayerContextRegistry.DefaultLocalPlayerId);
        }

        public void RestoreStolenLootThisDayFromSave(List<StolenLootEntryData> savedEntries, string ownerPlayerId)
        {
            string normalizedOwnerKey = NormalizeOwnerKey(ownerPlayerId);
            ClearStolenLootThisDay(normalizedOwnerKey);

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

                string entryOwnerKey = string.IsNullOrWhiteSpace(savedEntry.ownerKey)
                    ? normalizedOwnerKey
                    : NormalizeOwnerKey(savedEntry.ownerKey);
                if (!string.Equals(entryOwnerKey, normalizedOwnerKey, StringComparison.Ordinal))
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

                    if (!string.Equals(existingEntry.ownerKey, normalizedOwnerKey, StringComparison.Ordinal))
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
                    _stolenLootThisDay.Add(new StolenLootTrackerEntry(normalizedOwnerKey, normalizedItemId, count));
                }
            }
        }

        public void RestoreStolenLootThisDayFromSaveForAllOwners(List<StolenLootEntryData> savedEntries)
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

                string normalizedOwnerKey = NormalizeOwnerKey(savedEntry.ownerKey);
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

                    if (!string.Equals(existingEntry.ownerKey, normalizedOwnerKey, StringComparison.Ordinal)
                        || !string.Equals(existingEntry.itemId, normalizedItemId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    existingEntry.count = Mathf.Max(0, existingEntry.count) + count;
                    alreadyPresent = true;
                    break;
                }

                if (!alreadyPresent)
                {
                    _stolenLootThisDay.Add(new StolenLootTrackerEntry(normalizedOwnerKey, normalizedItemId, count));
                }
            }
        }

        public bool CompleteWorkdayAndGoHome()
        {
            if (IsNonAuthoritativeNetworkClient())
            {
                return false;
            }

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
            ResetWorkdayRuntimeState();
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
            if (IsNonAuthoritativeNetworkClient())
            {
                return false;
            }

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

            if (ShouldBeginCurrentDayFromHomeStaging())
            {
                BeginCurrentDayFromHomeStaging();
                return true;
            }

            _currentDay = Mathf.Max(1, _currentDay) + 1;
            _workdayCompleted = false;
            ClearDayWorkEarnings();
            ClearStolenLootThisDay();
            ClearFailedLieEscalationCountThisDay();
            _currentRunPhase = RunPhase.Work;
            ClearDailyTaskAssignments();
            EnsureDailyAssignmentsForCurrentWorkday();
            ResetWorkdayRuntimeState();
            EnsureWorkdayRuntimeInitialized();
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

        public bool TryBeginWorkdayFromHomeAndRouteToGameplayScene()
        {
            if (_currentRunPhase != RunPhase.Home)
            {
                return false;
            }

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
            if (IsNonAuthoritativeNetworkClient())
            {
                return false;
            }

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
            ResetWorkdayRuntimeState();

            if (TryBuildSaveContext(out SaveManager.SaveContext saveContext, out string saveContextFailureReason))
            {
                if (!SaveManager.Save(saveContext))
                {
                    Debug.LogWarning(
                        "[GameManager] Failed to persist fail-state snapshot before routing to Menu.",
                        this);
                }
            }
            else
            {
                Debug.LogWarning(
                    $"[GameManager] Could not persist fail-state before routing to Menu: {saveContextFailureReason}",
                    this);
            }

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

            if (IsNonAuthoritativeNetworkClient())
            {
                Debug.LogWarning(
                    $"[GameManager] Ignored client-side day work earnings mutation (+{amount}) in network session.",
                    this);
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

            if (IsNonAuthoritativeNetworkClient())
            {
                Debug.LogWarning(
                    $"[GameManager] Ignored client-side currency mutation ({amount}) in network session.",
                    this);
                return;
            }

            _currency += amount;
            EventBus.Publish(new CurrencyChangedEvent(_currency));
        }

        public bool TrySpendCurrency(int amount)
        {
            if (IsNonAuthoritativeNetworkClient())
            {
                Debug.LogWarning(
                    $"[GameManager] Ignored client-side spend request ({amount}) in network session.",
                    this);
                return false;
            }

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
            _currentRunPhase = RunPhase.Home;
            _workdayCompleted = false;
            _consecutiveFailedWorkdays = 0;
            _runFailed = false;
            _runFailedReason = string.Empty;
            _failedLieEscalationCountThisDay = 0;

            ClearDayWorkEarnings();
            ClearStolenLootThisDay();
            ClearDailyTaskAssignments();
            ResetWorkdayRuntimeState();
            _ownedToolTiers.Clear();

            _hasRoutedAfterFailure = false;
            _isRunPhaseSceneRouting = false;
            _hasAttemptedInitialLoad = false;
            _initialLoadBindingRetryCount = 0;
        }

        private void NormalizeRestoredRunProgressInvariants()
        {
            List<string> normalizedIssues = new List<string>();

            _currentWorkHour = Mathf.Clamp(_currentWorkHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour);

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

            if (_currentRunPhase != RunPhase.Work)
            {
                ResetWorkdayRuntimeState();
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
            if (PlayerContextLocator.TryGetLocalPlayerTransform(out playerTransform) && playerTransform != null)
            {
                return true;
            }

            if (PlayerContextLocator.IsCompatibilityFallbackAllowed()
                && _freePlayState != null
                && _freePlayState.TryGetAuthoritativePlayerTransform(out playerTransform)
                && playerTransform != null)
            {
                return true;
            }

            if (PlayerContextLocator.IsCompatibilityFallbackAllowed()
                && PlayerContextLocator.TryGetAuthoritativePlayerTransform(out playerTransform)
                && playerTransform != null)
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
            if (PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext) && localContext != null)
            {
                return InventoryQuickSlotRules.ClampIndex(localContext.SelectedQuickSlotIndex);
            }

            PlayerContextLocator.TryGetLocalInventoryGridUI(out InventoryGridUI inventoryGridUI);
            if (inventoryGridUI == null && PlayerContextLocator.IsCompatibilityFallbackAllowed())
            {
                PlayerContextLocator.TryGetInventoryGridUI(out inventoryGridUI);
            }

            if (inventoryGridUI == null)
            {
                return 0;
            }

            return inventoryGridUI.GetSelectedSlotIndex();
        }

        public void RestoreSelectedInventorySlotFromSave(int selectedSlotIndex)
        {
            int normalizedSelectedSlotIndex = InventoryQuickSlotRules.ClampIndex(selectedSlotIndex);
            if (PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext) && localContext != null)
            {
                localContext.SetSelectedQuickSlotIndex(normalizedSelectedSlotIndex);
            }

            PlayerContextLocator.TryGetLocalInventoryGridUI(out InventoryGridUI inventoryGridUI);
            if (inventoryGridUI == null && PlayerContextLocator.IsCompatibilityFallbackAllowed())
            {
                PlayerContextLocator.TryGetInventoryGridUI(out inventoryGridUI);
            }

            if (inventoryGridUI == null)
            {
                return;
            }

            inventoryGridUI.RestoreSelectedSlotFromSave(normalizedSelectedSlotIndex);
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
            List<string> measureCutKeys = CollectMeasureCutTaskKeysFromScene();
            List<string> pipePaintKeys = CollectPipePaintTaskKeysFromScene();
            List<string> drillScrewKeys = CollectDrillScrewTaskKeysFromScene();

            int cleaningAssignmentTarget = GetScaledAssignmentTargetCountForCurrentDay(DefaultCleaningAssignmentsPerDay);
            int weldingAssignmentTarget = GetScaledAssignmentTargetCountForCurrentDay(DefaultWeldingAssignmentsPerDay);
            int measureCutAssignmentTarget = GetScaledAssignmentTargetCountForCurrentDay(DefaultMeasureCutAssignmentsPerDay);
            int pipePaintAssignmentTarget = GetScaledAssignmentTargetCountForCurrentDay(DefaultPipePaintAssignmentsPerDay);
            int drillScrewAssignmentTarget = GetScaledAssignmentTargetCountForCurrentDay(DefaultDrillScrewAssignmentsPerDay);

            AppendRandomAssignments(DailyTaskTypeCleaning, cleaningKeys, cleaningAssignmentTarget);
            AppendRandomAssignments(DailyTaskTypeWelding, weldingKeys, weldingAssignmentTarget);
            AppendRandomAssignments(DailyTaskTypeMeasureCut, measureCutKeys, measureCutAssignmentTarget);
            AppendRandomAssignments(DailyTaskTypePipePaint, pipePaintKeys, pipePaintAssignmentTarget);
            AppendRandomAssignments(DailyTaskTypeDrillScrew, drillScrewKeys, drillScrewAssignmentTarget);
            // DEVELOPMENT/TEMPORARY TEST SUPPORT:
            // Keep core work minigames reliably testable when valid stations exist in the scene.
            TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypeCleaning, cleaningKeys);
            TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypeWelding, weldingKeys);
            TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypeMeasureCut, measureCutKeys);
            TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypePipePaint, pipePaintKeys);
            TryEnsureAtLeastOneAssignmentForTaskType(DailyTaskTypeDrillScrew, drillScrewKeys);

            _dailyTaskAssignmentDay = _currentDay;
            RebuildTaskWaveScheduleForCurrentWorkday();
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

        private void TickWorkdayRuntime()
        {
            if (_currentRunPhase != RunPhase.Work || _workdayCompleted || _runFailed)
            {
                return;
            }

            if (_currentState != GameState.FreePlay)
            {
                return;
            }

            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null && pauseManager.IsPaused)
            {
                return;
            }

            EnsureDailyAssignmentsForCurrentWorkday();
            EnsureWorkdayRuntimeInitialized();

            if (_generatedTaskWaves.Count == 0)
            {
                return;
            }

            float shiftHours = DefaultWorkdayEndHour - DefaultWorkdayStartHour;
            float durationSeconds = Mathf.Max(1f, _workdayDurationMinutes * 60f);
            float hoursPerSecond = shiftHours / durationSeconds;
            _currentWorkHour = Mathf.Clamp(_currentWorkHour + (Time.deltaTime * hoursPerSecond), DefaultWorkdayStartHour, DefaultWorkdayEndHour);

            CatchUpDueTaskWaves();
        }

        private float ConvertRealMinutesToWorkHourDelta(float realMinutes)
        {
            float clampedRealMinutes = Mathf.Max(0f, realMinutes);
            float durationMinutes = Mathf.Max(0.01f, _workdayDurationMinutes);
            float shiftHours = DefaultWorkdayEndHour - DefaultWorkdayStartHour;
            return (clampedRealMinutes / durationMinutes) * shiftHours;
        }

        private float GetEffectiveLateWaveCutoffHour()
        {
            float cutoffByConfig = Mathf.Clamp(_lateWaveCutoffHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour);
            float safetyBufferHours = Mathf.Max(1f, _lateWaveSafetyBufferMinutes) / 60f;
            float cutoffByBuffer = DefaultWorkdayEndHour - safetyBufferHours;
            return Mathf.Clamp(Mathf.Min(cutoffByConfig, cutoffByBuffer), DefaultWorkdayStartHour, DefaultWorkdayEndHour);
        }

        private void EnsureWorkdayRuntimeInitialized()
        {
            if (IsNonAuthoritativeNetworkClient())
            {
                return;
            }

            if (_currentRunPhase != RunPhase.Work)
            {
                return;
            }

            if (_workdayRuntimeInitialized)
            {
                return;
            }

            RebuildTaskWaveScheduleForCurrentWorkday();
        }

        private void RebuildTaskWaveScheduleForCurrentWorkday()
        {
            if (IsNonAuthoritativeNetworkClient())
            {
                return;
            }

            _generatedTaskWaves.Clear();
            _unlockedTaskKeys.Clear();
            _nextTaskWaveIndex = 0;
            _currentWorkHour = Mathf.Clamp(_currentWorkHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour);

            List<string> pendingKeys = new List<string>();
            for (int i = 0; i < _dailyTaskAssignments.Count; i++)
            {
                DailyTaskAssignment assignment = _dailyTaskAssignments[i];
                if (assignment == null || assignment.isCompleted)
                {
                    continue;
                }

                string normalizedKey = NormalizeTaskKey(assignment.taskKey);
                if (!string.IsNullOrEmpty(normalizedKey) && !pendingKeys.Contains(normalizedKey))
                {
                    pendingKeys.Add(normalizedKey);
                }
            }

            if (pendingKeys.Count <= 0)
            {
                _workdayRuntimeInitialized = true;
                return;
            }

            GeneratedTaskWave firstWave = new GeneratedTaskWave
            {
                unlockHour = DefaultWorkdayStartHour,
                taskKeys = new List<string>()
            };

            int firstWaveCount;
            if (pendingKeys.Count <= 1)
            {
                firstWaveCount = 1;
            }
            else
            {
                int maxFirstWaveCount = Mathf.Min(2, pendingKeys.Count - 1);
                firstWaveCount = Mathf.Clamp(maxFirstWaveCount, 1, pendingKeys.Count);
            }
            for (int i = 0; i < firstWaveCount; i++)
            {
                firstWave.taskKeys.Add(pendingKeys[i]);
            }

            _generatedTaskWaves.Add(firstWave);

            // Ensure wave 1 includes at least one pending key per assigned task type
            // without replacing existing wave entries.
            EnsureAssignedTaskTypesAppearInFirstWave(firstWave, pendingKeys);

            int nextKeyIndex = firstWaveCount;
            float nextWaveHour = DefaultWorkdayStartHour;
            float minDelayMinutes = Mathf.Max(0.5f, Mathf.Min(_waveDelayMinMinutes, _waveDelayMaxMinutes));
            float maxDelayMinutes = Mathf.Max(minDelayMinutes, _waveDelayMaxMinutes);
            float effectiveCutoffHour = GetEffectiveLateWaveCutoffHour();

            while (nextKeyIndex < pendingKeys.Count)
            {
                float delayMinutesRealTime = UnityEngine.Random.Range(minDelayMinutes, maxDelayMinutes);
                nextWaveHour += ConvertRealMinutesToWorkHourDelta(delayMinutesRealTime);
                if (nextWaveHour > effectiveCutoffHour)
                {
                    break;
                }

                GeneratedTaskWave wave = new GeneratedTaskWave
                {
                    unlockHour = Mathf.Clamp(nextWaveHour, DefaultWorkdayStartHour, DefaultWorkdayEndHour),
                    taskKeys = new List<string>()
                };

                int tasksInWave = Mathf.Min(2, pendingKeys.Count - nextKeyIndex);
                for (int i = 0; i < tasksInWave; i++)
                {
                    wave.taskKeys.Add(pendingKeys[nextKeyIndex + i]);
                }

                nextKeyIndex += tasksInWave;
                _generatedTaskWaves.Add(wave);
            }

            // Ensure all assignments are eventually unlockable even if cutoff is early.
            if (nextKeyIndex < pendingKeys.Count)
            {
                GeneratedTaskWave fallbackWave = new GeneratedTaskWave
                {
                    unlockHour = effectiveCutoffHour,
                    taskKeys = new List<string>()
                };

                while (nextKeyIndex < pendingKeys.Count)
                {
                    fallbackWave.taskKeys.Add(pendingKeys[nextKeyIndex]);
                    nextKeyIndex++;
                }

                _generatedTaskWaves.Add(fallbackWave);
            }

            _workdayRuntimeInitialized = true;
            CatchUpDueTaskWaves();
        }

        private void EnsureAssignedTaskTypesAppearInFirstWave(GeneratedTaskWave firstWave, List<string> pendingKeys)
        {
            if (_currentRunPhase != RunPhase.Work
                || firstWave == null
                || firstWave.taskKeys == null
                || pendingKeys == null)
            {
                return;
            }

            Dictionary<string, string> firstPendingKeyByType = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < _dailyTaskAssignments.Count; i++)
            {
                DailyTaskAssignment assignment = _dailyTaskAssignments[i];
                if (assignment == null || assignment.isCompleted)
                {
                    continue;
                }

                string normalizedTaskType = NormalizeTaskType(assignment.taskType);
                if (string.IsNullOrEmpty(normalizedTaskType))
                {
                    continue;
                }

                string normalizedKey = NormalizeTaskKey(assignment.taskKey);
                if (string.IsNullOrEmpty(normalizedKey))
                {
                    continue;
                }

                if (!pendingKeys.Contains(normalizedKey))
                {
                    continue;
                }

                if (!firstPendingKeyByType.ContainsKey(normalizedTaskType))
                {
                    firstPendingKeyByType[normalizedTaskType] = normalizedKey;
                }
            }

            if (firstPendingKeyByType.Count <= 0)
            {
                return;
            }

            foreach (KeyValuePair<string, string> pair in firstPendingKeyByType)
            {
                string taskKeyForType = pair.Value;
                if (string.IsNullOrEmpty(taskKeyForType) || firstWave.taskKeys.Contains(taskKeyForType))
                {
                    continue;
                }

                firstWave.taskKeys.Add(taskKeyForType);
            }
        }

        private void CatchUpDueTaskWaves()
        {
            if (_generatedTaskWaves.Count <= 0)
            {
                return;
            }

            if (_nextTaskWaveIndex < 0)
            {
                _nextTaskWaveIndex = 0;
            }

            float nowHour = GetCurrentWorkHour();
            while (_nextTaskWaveIndex < _generatedTaskWaves.Count)
            {
                GeneratedTaskWave wave = _generatedTaskWaves[_nextTaskWaveIndex];
                if (wave == null)
                {
                    _nextTaskWaveIndex++;
                    continue;
                }

                if (wave.unlockHour > nowHour + 0.0001f)
                {
                    break;
                }

                if (wave.taskKeys != null)
                {
                    for (int k = 0; k < wave.taskKeys.Count; k++)
                    {
                        AddUnlockedTaskKey(wave.taskKeys[k]);
                    }
                }

                _nextTaskWaveIndex++;
            }
        }

        private void AddUnlockedTaskKey(string taskKey)
        {
            string normalizedKey = NormalizeTaskKey(taskKey);
            if (string.IsNullOrEmpty(normalizedKey) || _unlockedTaskKeys.Contains(normalizedKey))
            {
                return;
            }

            _unlockedTaskKeys.Add(normalizedKey);
        }

        private bool IsTaskKeyUnlocked(string taskKey)
        {
            string normalizedKey = NormalizeTaskKey(taskKey);
            return !string.IsNullOrEmpty(normalizedKey) && _unlockedTaskKeys.Contains(normalizedKey);
        }

        private bool IsAssignedDailyTaskCompleted(string taskType, string taskKey)
        {
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
                    return assignment.isCompleted;
                }
            }

            return false;
        }

        private bool ShouldBeginCurrentDayFromHomeStaging()
        {
            return _currentDay <= 1
                && !_workdayCompleted
                && _currentRunPhase == RunPhase.Home
                && !HasDailyTaskAssignmentsForCurrentWorkday();
        }

        private void BeginCurrentDayFromHomeStaging()
        {
            _workdayCompleted = false;
            ClearDayWorkEarnings();
            ClearStolenLootThisDay();
            ClearFailedLieEscalationCountThisDay();
            _currentRunPhase = RunPhase.Work;
            ClearDailyTaskAssignments();
            ResetWorkdayRuntimeState();
            EnsureDailyAssignmentsForCurrentWorkday();
            EnsureWorkdayRuntimeInitialized();
        }

        private void ResetWorkdayRuntimeState()
        {
            _currentWorkHour = DefaultWorkdayStartHour;
            _nextTaskWaveIndex = 0;
            _generatedTaskWaves.Clear();
            _unlockedTaskKeys.Clear();
            _workdayRuntimeInitialized = false;
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

        private static List<string> CollectMeasureCutTaskKeysFromScene()
        {
            PipeCuttingInteractable[] interactables = UnityEngine.Object.FindObjectsByType<PipeCuttingInteractable>(FindObjectsInactive.Exclude);
            List<string> keys = new List<string>(interactables.Length);
            for (int i = 0; i < interactables.Length; i++)
            {
                PipeCuttingInteractable interactable = interactables[i];
                if (interactable == null)
                {
                    continue;
                }

                TryAddTaskKey(keys, interactable.GetDailyTaskLocationKey());
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static List<string> CollectPipePaintTaskKeysFromScene()
        {
            PipePaintInteractable[] interactables = UnityEngine.Object.FindObjectsByType<PipePaintInteractable>(FindObjectsInactive.Exclude);
            List<string> keys = new List<string>(interactables.Length);
            for (int i = 0; i < interactables.Length; i++)
            {
                PipePaintInteractable interactable = interactables[i];
                if (interactable == null)
                {
                    continue;
                }

                TryAddTaskKey(keys, interactable.GetDailyTaskLocationKey());
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static List<string> CollectDrillScrewTaskKeysFromScene()
        {
            DrillScrewInteractable[] interactables = UnityEngine.Object.FindObjectsByType<DrillScrewInteractable>(FindObjectsInactive.Exclude);
            List<string> keys = new List<string>(interactables.Length);
            for (int i = 0; i < interactables.Length; i++)
            {
                DrillScrewInteractable interactable = interactables[i];
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
                || string.Equals(taskType, DailyTaskTypeWelding, StringComparison.Ordinal)
                || string.Equals(taskType, DailyTaskTypeMeasureCut, StringComparison.Ordinal)
                || string.Equals(taskType, DailyTaskTypePipePaint, StringComparison.Ordinal)
                || string.Equals(taskType, DailyTaskTypeDrillScrew, StringComparison.Ordinal);
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

        private static string NormalizeOwnerKey(string ownerKey)
        {
            return string.IsNullOrWhiteSpace(ownerKey)
                ? PlayerContextRegistry.DefaultLocalPlayerId
                : ownerKey.Trim();
        }

        private static bool IsRemoteNetworkOwnerOnAuthoritativeServer(string ownerKey)
        {
            if (!NetworkOwnerKeyUtility.IsNetworkOwnerKey(ownerKey))
            {
                return false;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening || !manager.IsServer)
            {
                return false;
            }

            string localOwnerKey = NetworkOwnerKeyUtility.GetOwnerKeyForSender(manager.LocalClientId);
            return !string.Equals(NormalizeOwnerKey(ownerKey), localOwnerKey, StringComparison.Ordinal);
        }

        private static string NormalizeUpgradeId(string upgradeId)
        {
            return string.IsNullOrWhiteSpace(upgradeId)
                ? string.Empty
                : upgradeId.Trim().ToLowerInvariant();
        }

        private void ValidateLocalOwnerStoragePath(string ownerPlayerId, string callsite)
        {
            if (NetworkOwnerKeyUtility.IsNetworkOwnerKey(ownerPlayerId))
            {
                return;
            }

            PlayerInventoryAuthority.LogNonLocalOwnerUsage($"GameManager.{callsite}", ownerPlayerId, this);
        }

        private HomeUpgradeStatusData BuildHomeUpgradeStatus(string upgradeId)
        {
            string normalizedUpgradeId = NormalizeUpgradeId(upgradeId);
            if (string.IsNullOrEmpty(normalizedUpgradeId))
            {
                return null;
            }

            int maxTier = GetMaxUpgradeTier(normalizedUpgradeId);
            int currentTier = GetOwnedUpgradeTier(normalizedUpgradeId);

            int nextTierCost = -1;
            if (currentTier < maxTier
                && UpgradeTierCostsById.TryGetValue(normalizedUpgradeId, out int[] tierCosts)
                && tierCosts != null
                && currentTier >= 0
                && currentTier < tierCosts.Length)
            {
                nextTierCost = Mathf.Max(0, tierCosts[currentTier]);
            }

            bool canPurchase = false;
            string unavailableReason = string.Empty;
            if (_runFailed)
            {
                unavailableReason = "Run failed.";
            }
            else if (_currentRunPhase != RunPhase.Home)
            {
                unavailableReason = "Home only.";
            }
            else if (maxTier <= 0)
            {
                unavailableReason = "Unavailable.";
            }
            else if (currentTier >= maxTier)
            {
                unavailableReason = "Max tier reached.";
            }
            else if (_currency < nextTierCost)
            {
                unavailableReason = $"Need ${nextTierCost}.";
            }
            else
            {
                canPurchase = true;
            }

            return new HomeUpgradeStatusData
            {
                upgradeId = normalizedUpgradeId,
                displayName = GetUpgradeDisplayNameForUi(normalizedUpgradeId),
                currentTier = currentTier,
                maxTier = maxTier,
                nextTierCost = nextTierCost,
                canPurchase = canPurchase,
                unavailableReason = unavailableReason
            };
        }

        private static string GetUpgradeDisplayNameForUi(string upgradeId)
        {
            if (string.Equals(upgradeId, UpgradeIdInventoryQuickSlots, StringComparison.Ordinal))
            {
                return "Inventory Slots";
            }

            if (string.Equals(upgradeId, UpgradeIdCleaningTool, StringComparison.Ordinal))
            {
                return "Cleaning Tool";
            }

            if (string.Equals(upgradeId, UpgradeIdWeldingTool, StringComparison.Ordinal))
            {
                return "Welding Tool";
            }

            return "Upgrade";
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

            if (IsNonAuthoritativeNetworkClient())
            {
                return true;
            }

            _isRunPhaseSceneRouting = true;
            return TryLoadSceneAuthoritatively(targetSceneName);
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

            if (IsNonAuthoritativeNetworkClient())
            {
                _hasRoutedAfterFailure = true;
                return true;
            }

            _hasRoutedAfterFailure = true;
            _isRunPhaseSceneRouting = true;
            return TryLoadSceneAuthoritatively(MenuSceneName);
        }

        private bool TryLoadSceneAuthoritatively(string targetSceneName)
        {
            if (string.IsNullOrWhiteSpace(targetSceneName))
            {
                return false;
            }
            return NetworkSafeSceneRouter.TryRoute(targetSceneName, this, allowClientLocalLoad: false);
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

        private void ClearStolenLootThisDay(string ownerKey)
        {
            string normalizedOwnerKey = NormalizeOwnerKey(ownerKey);
            for (int i = _stolenLootThisDay.Count - 1; i >= 0; i--)
            {
                StolenLootTrackerEntry entry = _stolenLootThisDay[i];
                if (entry == null)
                {
                    _stolenLootThisDay.RemoveAt(i);
                    continue;
                }

                if (string.Equals(entry.ownerKey, normalizedOwnerKey, StringComparison.Ordinal))
                {
                    _stolenLootThisDay.RemoveAt(i);
                }
            }
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
            ResetWorkdayRuntimeState();
        }

        private bool IsRunPhaseTransitionBlocked(out string failureReason)
        {
            if (PlayerContextLocator.TryGetLocalPresentationMode(out LocalPlayerPresentationMode mode)
                && mode == LocalPlayerPresentationMode.Minigame)
            {
                failureReason = "A local minigame presentation is active.";
                return true;
            }

            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager != null && minigameManager.IsMinigameActiveForOwner(PlayerContextRegistry.DefaultLocalPlayerId))
            {
                failureReason = "A minigame is currently active.";
                return true;
            }

            failureReason = string.Empty;
            return false;
        }

        private static bool IsNonAuthoritativeNetworkClient()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening && manager.IsClient && !manager.IsServer;
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
            PlayerContextLocator.TryGetLocalInventoryGridUI(out InventoryGridUI inventoryGridUI);
            if (inventoryGridUI == null && PlayerContextLocator.IsCompatibilityFallbackAllowed())
            {
                PlayerContextLocator.TryGetInventoryGridUI(out inventoryGridUI);
            }

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

