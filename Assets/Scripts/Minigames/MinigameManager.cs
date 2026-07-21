using UnityEngine;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using Game.Player;
using UnityEngine.SceneManagement;

namespace Game.Minigames
{
    /// <summary>
    /// MinigameManager handles the lifecycle of active minigames.
    /// </summary>
    public class MinigameManager : MonoBehaviour
    {
        private static readonly HashSet<string> LoggedFallbackValidationWarnings = new HashSet<string>();
        private const string HomeSceneName = "HomeScene";
        private const string GameplaySceneName = "GameplayScene";

        [Header("Minigame Canvases")]
        [SerializeField] private Canvas _cleaningCanvas;
        [SerializeField] private Canvas _weldingCanvas;
        [SerializeField] private Canvas _measureCutCanvas;
        [SerializeField] private Canvas _pipePaintCanvas;
        [SerializeField] private Canvas _drillScrewCanvas;

        private const string CleaningMinigameId = "cleaning";
        private const string WeldingMinigameId = "welding";
        private const string MeasureCutMinigameId = "measure_cut";
        private const string PipePaintMinigameId = "pipe_paint";
        private const string DrillScrewMinigameId = "drill_screw";
        private const string FreeplayCursorAuthorityOwner = "freeplay_camera";
        private const string MissingCleaningCanvasErrorMessage = "missing CleaningCanvas reference";
        private const string MissingWeldingCanvasErrorMessage = "missing WeldingCanvas reference";
        private const string MissingMeasureCutCanvasErrorMessage = "missing MeasureCutCanvas reference";
        private const string MissingPipePaintCanvasErrorMessage = "missing PipePaintCanvas reference";
        private const string MissingDrillScrewCanvasErrorMessage = "missing DrillScrewCanvas reference";

        private static MinigameManager _instance;
        private static int _managerLifetimeSequence;
        public static MinigameManager Instance => _instance;

        private IMinigame _activeMinigame;
        private GameObject _minigameGameObject;
        private System.Type _minigameType;
        private int _sessionSequence;
        private int _activeSessionToken;
        private string _activeOwnerPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;
        private int _managerLifetimeScope;
        private int _lastTerminalFlowFrame = -1;
        private string _lastTerminalFlowType;
        private bool _isCompletingTerminalFlow;
        private bool _hasLoggedHomeSceneOptionalCanvasInfo;
        private bool _hasLoggedGameplaySceneCanvasWarning;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _managerLifetimeScope = ++_managerLifetimeSequence;

            EnsureMinigameCanvasesHidden();
            LogSceneCanvasBindingExpectations();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _instance = null;
            }
        }

        private void Update()
        {
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null && pauseManager.IsPaused)
            {
                return;
            }

            if (_activeMinigame == null)
            {
                return;
            }

            if (_activeMinigame.IsActive())
            {
                _activeMinigame.OnMinigameUpdate();
            }

            if (_activeMinigame != null && _activeMinigame.GetResult() != MinigameResult.None)
            {
                EndActiveMinigame();
            }
        }

        public IMinigame StartMinigame<T>(MinigameData data) where T : MonoBehaviour, IMinigame
        {
            string ownerPlayerId = ResolveOwnerPlayerId(
                data != null ? data.ownerPlayerId : null,
                "StartMinigame<T>(data)",
                data != null ? data.minigameId : typeof(T).Name,
                allowNetworkDefaultOwnerFallback: false);
            return StartMinigame<T>(data, ownerPlayerId);
        }

        public IMinigame StartMinigame<T>(MinigameData data, string ownerPlayerId) where T : MonoBehaviour, IMinigame
        {
            if (_activeMinigame != null && _activeMinigame.IsActive())
            {
                return null;
            }

            if (data == null)
            {
                return null;
            }

            ownerPlayerId = ResolveOwnerPlayerId(ownerPlayerId, "StartMinigame<T>(data, owner)", data.minigameId, allowNetworkDefaultOwnerFallback: false);
            if (string.IsNullOrWhiteSpace(ownerPlayerId))
            {
                return null;
            }

            data.ownerPlayerId = ownerPlayerId;

            if (!ValidateCanvasRequirements(typeof(T), data, ownerPlayerId))
            {
                return null;
            }

            EnsureMinigameCanvasesHidden();

            GameObject gameObject = new GameObject($"[Minigame] {data.minigameId}");
            T minigameComponent = gameObject.AddComponent<T>();

            if (minigameComponent == null || !(minigameComponent is IMinigame))
            {
                Destroy(gameObject);
                return null;
            }

            _activeMinigame = minigameComponent;
            _minigameGameObject = gameObject;
            _minigameType = typeof(T);
            _activeSessionToken = ++_sessionSequence;
            _activeOwnerPlayerId = ownerPlayerId;

            _activeMinigame.Initialize(data);
            BeginLocalPresentationForOwner(data.minigameId, _activeOwnerPlayerId);
            _activeMinigame.OnMinigameStart();

            MinigameResult startupResult = _activeMinigame.GetResult();
            if (startupResult != MinigameResult.None)
            {
                EndActiveMinigame();
                return null;
            }

            EventBus.Publish(new MinigameStartedEvent(data.minigameId, _activeOwnerPlayerId));

            return _activeMinigame;
        }

        public IMinigame StartMinigameWithObject(IMinigame minigame, MinigameData data)
        {
            string ownerPlayerId = ResolveOwnerPlayerId(
                data != null ? data.ownerPlayerId : null,
                "StartMinigameWithObject(minigame, data)",
                data != null ? data.minigameId : (minigame != null ? minigame.GetType().Name : "<null>"),
                allowNetworkDefaultOwnerFallback: false);
            return StartMinigameWithObject(minigame, data, ownerPlayerId);
        }

        public IMinigame StartMinigameWithObject(IMinigame minigame, MinigameData data, string ownerPlayerId)
        {
            if (minigame == null)
            {
                return null;
            }

            if (data == null)
            {
                return null;
            }

            if (_activeMinigame != null && _activeMinigame.IsActive())
            {
                return null;
            }

            ownerPlayerId = ResolveOwnerPlayerId(ownerPlayerId, "StartMinigameWithObject(minigame, data, owner)", data.minigameId, allowNetworkDefaultOwnerFallback: false);
            if (string.IsNullOrWhiteSpace(ownerPlayerId))
            {
                return null;
            }

            data.ownerPlayerId = ownerPlayerId;

            if (!ValidateCanvasRequirements(minigame.GetType(), data, ownerPlayerId))
            {
                return null;
            }

            EnsureMinigameCanvasesHidden();

            _activeMinigame = minigame;
            _minigameGameObject = (minigame as MonoBehaviour)?.gameObject;
            _activeSessionToken = ++_sessionSequence;
            _activeOwnerPlayerId = ownerPlayerId;

            _activeMinigame.Initialize(data);
            BeginLocalPresentationForOwner(data.minigameId, _activeOwnerPlayerId);
            _activeMinigame.OnMinigameStart();

            MinigameResult startupResult = _activeMinigame.GetResult();
            if (startupResult != MinigameResult.None)
            {
                EndActiveMinigame();
                return null;
            }

            EventBus.Publish(new MinigameStartedEvent(data.minigameId, _activeOwnerPlayerId));

            return _activeMinigame;
        }

        public void EndActiveMinigame()
        {
            if (_activeMinigame == null)
            {
                WarnIfDuplicateTerminalFlow("end", MinigameResult.None);
                return;
            }

            if (_isCompletingTerminalFlow)
            {
                WarnIfDuplicateTerminalFlow("end", _activeMinigame.GetResult());
                return;
            }

            _isCompletingTerminalFlow = true;
            try
            {
                MinigameResult result = _activeMinigame.GetResult();
                string minigameId = _activeMinigame.GetMinigameId();
                string ownerPlayerId = _activeOwnerPlayerId;
                int sessionToken = _activeSessionToken;
                MinigameData minigameData = _activeMinigame.GetMinigameData();

                _activeMinigame.OnMinigameEnd();

                if (_minigameGameObject != null)
                {
                    Destroy(_minigameGameObject);
                }

                ClearActiveSession("end");
                EndLocalPresentationForOwner(ownerPlayerId);
                EnsureMinigameCanvasesHidden();

                if (ObjectiveManager.TryGetInstance(out ObjectiveManager objectiveManager))
                {
                    objectiveManager.SyncAfterLoad();
                }
                else
                {
                    Debug.LogWarning("[MinigameManager] ObjectiveManager is missing; objective sync skipped after minigame end.");
                }
                MinigameRewardSystem.DistributeRewards(
                    result,
                    minigameId,
                    ownerPlayerId,
                    sessionToken,
                    _managerLifetimeScope,
                    minigameData);

                EventBus.Publish(new MinigameEndedEvent(result, minigameId, ownerPlayerId));
            }
            finally
            {
                _isCompletingTerminalFlow = false;
            }
        }

        public void CancelActiveMinigame()
        {
            if (_activeMinigame == null)
            {
                WarnIfDuplicateTerminalFlow("cancel", MinigameResult.Cancelled);
                return;
            }

            if (_isCompletingTerminalFlow)
            {
                WarnIfDuplicateTerminalFlow("cancel", MinigameResult.Cancelled);
                return;
            }

            _isCompletingTerminalFlow = true;
            try
            {
                _activeMinigame.OnMinigameEnd();
                string minigameId = _activeMinigame.GetMinigameId();
                string ownerPlayerId = _activeOwnerPlayerId;

                if (_minigameGameObject != null)
                {
                    Destroy(_minigameGameObject);
                }

                ClearActiveSession("cancel");
                EndLocalPresentationForOwner(ownerPlayerId);
                EnsureMinigameCanvasesHidden();
                EventBus.Publish(new MinigameCancelledEvent(MinigameResult.Cancelled, minigameId, ownerPlayerId));
            }
            finally
            {
                _isCompletingTerminalFlow = false;
            }
        }

        private void ClearActiveSession(string terminalFlowType)
        {
            _activeMinigame = null;
            _minigameGameObject = null;
            _minigameType = null;
            _activeSessionToken = 0;
            _activeOwnerPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;
            _lastTerminalFlowFrame = Time.frameCount;
            _lastTerminalFlowType = terminalFlowType;
        }

        public IMinigame GetActiveMinigame()
        {
            return _activeMinigame;
        }

        public bool IsMinigameActive()
        {
            return _activeMinigame != null && _activeMinigame.IsActive();
        }

        public bool ForceCancelActiveMinigameForLocalOwner()
        {
            if (_activeMinigame == null)
            {
                return false;
            }

            if (!PlayerInventoryAuthority.IsLocalOwner(_activeOwnerPlayerId))
            {
                return false;
            }

            CancelActiveMinigame();
            return true;
        }

        public bool IsMinigameActiveForOwner(string ownerPlayerId)
        {
            if (!IsMinigameActive())
            {
                return false;
            }

            string normalizedOwner = ResolveOwnerPlayerId(ownerPlayerId, "TryCancelMinigameForOwner", _activeMinigame?.GetMinigameId());
            return string.Equals(normalizedOwner, _activeOwnerPlayerId, System.StringComparison.Ordinal);
        }

        public bool TryGetActiveOwnerPlayerId(out string ownerPlayerId)
        {
            if (IsMinigameActive())
            {
                ownerPlayerId = _activeOwnerPlayerId;
                return true;
            }

            ownerPlayerId = string.Empty;
            return false;
        }

        public MinigameResult GetLastResult()
        {
            if (_activeMinigame == null)
            {
                return MinigameResult.None;
            }

            return _activeMinigame.GetResult();
        }

        public string GetActiveMinigameId()
        {
            return _activeMinigame?.GetMinigameId() ?? "";
        }

        public Canvas GetCleaningCanvas()
        {
            return _cleaningCanvas;
        }

        public Canvas GetWeldingCanvas()
        {
            return _weldingCanvas;
        }

        public Canvas GetMeasureCutCanvas()
        {
            return _measureCutCanvas;
        }

        public Canvas GetPipePaintCanvas()
        {
            return _pipePaintCanvas;
        }

        public Canvas GetDrillScrewCanvas()
        {
            return _drillScrewCanvas;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureMinigameCanvasesHidden();
            LogSceneCanvasBindingExpectations();
        }

        private void EnsureMinigameCanvasesHidden()
        {
            if (_cleaningCanvas != null)
            {
                _cleaningCanvas.enabled = false;
            }

            if (_weldingCanvas != null)
            {
                _weldingCanvas.enabled = false;
            }

            if (_measureCutCanvas != null)
            {
                _measureCutCanvas.enabled = false;
            }

            if (_pipePaintCanvas != null)
            {
                _pipePaintCanvas.enabled = false;
            }

            if (_drillScrewCanvas != null)
            {
                _drillScrewCanvas.enabled = false;
            }
        }

        private bool ValidateCanvasRequirements(System.Type minigameType, MinigameData data, string ownerPlayerId)
        {
            bool requiresCleaningCanvas = RequiresCleaningCanvas(minigameType, data);
            bool requiresWeldingCanvas = RequiresWeldingCanvas(minigameType, data);
            bool requiresMeasureCutCanvas = RequiresMeasureCutCanvas(minigameType, data);
            bool requiresPipePaintCanvas = RequiresPipePaintCanvas(minigameType, data);
            bool requiresDrillScrewCanvas = RequiresDrillScrewCanvas(minigameType, data);
            string minigameId = data != null ? data.minigameId : string.Empty;
            string sceneName = SceneManager.GetActiveScene().name;
            string minigameTypeName = minigameType != null ? minigameType.Name : "UnknownMinigameType";

            if (requiresCleaningCanvas && _cleaningCanvas == null)
            {
                LogCanvasRequirementFailure(
                    MissingCleaningCanvasErrorMessage,
                    sceneName,
                    minigameTypeName,
                    minigameId,
                    ownerPlayerId);
                return false;
            }

            if (requiresWeldingCanvas && _weldingCanvas == null)
            {
                LogCanvasRequirementFailure(
                    MissingWeldingCanvasErrorMessage,
                    sceneName,
                    minigameTypeName,
                    minigameId,
                    ownerPlayerId);
                return false;
            }

            if (requiresMeasureCutCanvas && _measureCutCanvas == null)
            {
                LogCanvasRequirementFailure(
                    MissingMeasureCutCanvasErrorMessage,
                    sceneName,
                    minigameTypeName,
                    minigameId,
                    ownerPlayerId);
                return false;
            }

            if (requiresPipePaintCanvas && _pipePaintCanvas == null)
            {
                LogCanvasRequirementFailure(
                    MissingPipePaintCanvasErrorMessage,
                    sceneName,
                    minigameTypeName,
                    minigameId,
                    ownerPlayerId);
                return false;
            }

            if (requiresDrillScrewCanvas && _drillScrewCanvas == null)
            {
                LogCanvasRequirementFailure(
                    MissingDrillScrewCanvasErrorMessage,
                    sceneName,
                    minigameTypeName,
                    minigameId,
                    ownerPlayerId);
                return false;
            }

            return true;
        }

        private void LogCanvasRequirementFailure(
            string missingRequirement,
            string sceneName,
            string minigameTypeName,
            string minigameId,
            string ownerPlayerId)
        {
            string normalizedOwner = string.IsNullOrWhiteSpace(ownerPlayerId)
                ? PlayerContextRegistry.DefaultLocalPlayerId
                : ownerPlayerId.Trim();
            string normalizedMinigameId = string.IsNullOrWhiteSpace(minigameId) ? "(unset)" : minigameId.Trim();
            string normalizedScene = string.IsNullOrWhiteSpace(sceneName) ? "(unknown)" : sceneName.Trim();

            Debug.LogError(
                $"[MinigameManager] Cannot start minigame because {missingRequirement}. " +
                $"scene='{normalizedScene}', minigameType='{minigameTypeName}', minigameId='{normalizedMinigameId}', ownerPlayerId='{normalizedOwner}', managerObject='{name}'. " +
                "This likely means scene wiring or prefab instance wiring is incomplete.",
                this);
        }

        private static bool RequiresCleaningCanvas(System.Type minigameType, MinigameData data)
        {
            if (minigameType == typeof(CleaningMinigame))
            {
                return true;
            }

            return string.Equals(data?.minigameId, CleaningMinigameId, System.StringComparison.Ordinal);
        }

        private static bool RequiresWeldingCanvas(System.Type minigameType, MinigameData data)
        {
            if (minigameType == typeof(WeldingFillMinigame))
            {
                return true;
            }

            return string.Equals(data?.minigameId, WeldingMinigameId, System.StringComparison.Ordinal);
        }

        private static bool RequiresMeasureCutCanvas(System.Type minigameType, MinigameData data)
        {
            if (minigameType == typeof(MeasureCutMinigame))
            {
                return true;
            }

            return string.Equals(data?.minigameId, MeasureCutMinigameId, System.StringComparison.Ordinal);
        }

        private static bool RequiresPipePaintCanvas(System.Type minigameType, MinigameData data)
        {
            if (minigameType == typeof(PipePaintMinigame))
            {
                return true;
            }

            return string.Equals(data?.minigameId, PipePaintMinigameId, System.StringComparison.Ordinal);
        }

        private static bool RequiresDrillScrewCanvas(System.Type minigameType, MinigameData data)
        {
            if (minigameType == typeof(DrillScrewMinigame))
            {
                return true;
            }

            return string.Equals(data?.minigameId, DrillScrewMinigameId, System.StringComparison.Ordinal);
        }

        private void LogSceneCanvasBindingExpectations()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            bool hasAnyGameplayCanvas = _cleaningCanvas != null
                || _weldingCanvas != null
                || _measureCutCanvas != null
                || _pipePaintCanvas != null
                || _drillScrewCanvas != null;

            if (string.Equals(sceneName, HomeSceneName, System.StringComparison.Ordinal))
            {
                if (_hasLoggedHomeSceneOptionalCanvasInfo || hasAnyGameplayCanvas)
                {
                    return;
                }

                _hasLoggedHomeSceneOptionalCanvasInfo = true;
                Debug.Log(
                    "[MinigameManager] HomeScene has no gameplay minigame canvases assigned. " +
                    "This is expected for home/staging flow.",
                    this);
                return;
            }

            if (!string.Equals(sceneName, GameplaySceneName, System.StringComparison.Ordinal))
            {
                return;
            }

            if (_hasLoggedGameplaySceneCanvasWarning)
            {
                return;
            }

            List<string> missing = new List<string>();
            if (_cleaningCanvas == null) missing.Add("CleaningCanvas");
            if (_weldingCanvas == null) missing.Add("WeldingCanvas");
            if (_measureCutCanvas == null) missing.Add("MeasureCutCanvas");
            if (_pipePaintCanvas == null) missing.Add("PipePaintCanvas");
            if (_drillScrewCanvas == null) missing.Add("DrillScrewCanvas");

            if (missing.Count == 0)
            {
                return;
            }

            _hasLoggedGameplaySceneCanvasWarning = true;
            Debug.LogWarning(
                $"[MinigameManager] GameplayScene is missing minigame canvas bindings: {string.Join(", ", missing)}. " +
                "Minigames requiring these canvases may fail to start.",
                this);
        }

        private void WarnIfDuplicateTerminalFlow(string incomingFlowType, MinigameResult result)
        {
            if (_lastTerminalFlowFrame == Time.frameCount)
            {
                Debug.LogWarning(
                    $"[MinigameManager] Duplicate terminal flow detected in frame {Time.frameCount}. " +
                    $"Previous={_lastTerminalFlowType}, Incoming={incomingFlowType}, Result={result}.",
                    this);
            }
        }

        private static string ResolveOwnerPlayerId(
            string ownerPlayerId,
            string callsite,
            string minigameId,
            bool allowNetworkDefaultOwnerFallback = true)
        {
            if (string.IsNullOrWhiteSpace(ownerPlayerId))
            {
                ValidateOwnerDefaultFallback(callsite, minigameId);
                if (!allowNetworkDefaultOwnerFallback && IsActiveNetworkSession())
                {
                    return string.Empty;
                }

                return PlayerContextRegistry.DefaultLocalPlayerId;
            }

            return ownerPlayerId.Trim();
        }

        private static bool IsActiveNetworkSession()
        {
            Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
            return manager != null && manager.IsListening;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void ValidateOwnerDefaultFallback(string callsite, string minigameId)
        {
            Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
            {
                return;
            }

            string sceneName = SceneManager.GetActiveScene().name;
            string resolvedMinigameId = string.IsNullOrWhiteSpace(minigameId) ? "<unknown>" : minigameId.Trim();
            string key = $"MM_OWNER_DEFAULT|{sceneName}|{callsite}|{resolvedMinigameId}";
            if (!LoggedFallbackValidationWarnings.Add(key))
            {
                return;
            }

            Debug.LogWarning(
                $"[FallbackValidation][MM_OWNER_DEFAULT] scene='{sceneName}' callsite='{callsite}' minigameId='{resolvedMinigameId}' netMode='{(manager.IsServer ? (manager.IsClient ? "host" : "server") : "client")}' risk='owner_drift_default_local'");
        }

        private static void BeginLocalPresentationForOwner(string minigameId, string ownerPlayerId)
        {
            if (!PlayerInventoryAuthority.IsLocalOwner(ownerPlayerId))
            {
                return;
            }

            PlayerContextLocator.BeginLocalMinigamePresentation(minigameId, ownerPlayerId);
            PlayerContextLocator.TryReleaseLocalCursorAuthority(FreeplayCursorAuthorityOwner);
        }

        private static void EndLocalPresentationForOwner(string ownerPlayerId)
        {
            if (!PlayerInventoryAuthority.IsLocalOwner(ownerPlayerId))
            {
                return;
            }

            PlayerContextLocator.EndLocalMinigamePresentation(ownerPlayerId);
            GameManager gameManager = GameManager.Instance;
            PlayerContextLocator.TrySetLocalPresentationMode(
                gameManager != null && gameManager.IsSceneRoutePending
                    ? LocalPlayerPresentationMode.Menu
                    : LocalPlayerPresentationMode.FreePlay);
        }
    }
}
