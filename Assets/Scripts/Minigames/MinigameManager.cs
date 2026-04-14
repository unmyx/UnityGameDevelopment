using UnityEngine;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using UnityEngine.SceneManagement;

namespace Game.Minigames
{
    /// <summary>
    /// MinigameManager handles the lifecycle of active minigames.
    /// </summary>
    public class MinigameManager : MonoBehaviour
    {
        [Header("Minigame Canvases")]
        [SerializeField] private Canvas _cleaningCanvas;
        [SerializeField] private Canvas _weldingCanvas;

        private const string MissingCanvasErrorMessage =
            "[MinigameManager] Missing required minigame canvas references. " +
            "Assign CleaningCanvas and WeldingCanvas explicitly in the scene setup.";

        private static MinigameManager _instance;
        private static int _managerLifetimeSequence;
        public static MinigameManager Instance => _instance;

        private IMinigame _activeMinigame;
        private GameObject _minigameGameObject;
        private System.Type _minigameType;
        private int _sessionSequence;
        private int _activeSessionToken;
        private int _managerLifetimeScope;
        private int _lastTerminalFlowFrame = -1;
        private string _lastTerminalFlowType;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _managerLifetimeScope = ++_managerLifetimeSequence;

            if (!ValidateRequiredCanvases())
            {
                enabled = false;
                return;
            }

            EnsureMinigameCanvasesHidden();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
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
            if (_activeMinigame != null && _activeMinigame.IsActive())
            {
                return null;
            }

            if (data == null)
            {
                return null;
            }

            if (!ValidateRequiredCanvases())
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

            _activeMinigame.Initialize(data);
            _activeMinigame.OnMinigameStart();

            MinigameResult startupResult = _activeMinigame.GetResult();
            if (startupResult != MinigameResult.None)
            {
                EndActiveMinigame();
                return null;
            }

            EventBus.Publish(new MinigameStartedEvent(data.minigameId));

            return _activeMinigame;
        }

        public IMinigame StartMinigameWithObject(IMinigame minigame, MinigameData data)
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

            if (!ValidateRequiredCanvases())
            {
                return null;
            }

            EnsureMinigameCanvasesHidden();

            _activeMinigame = minigame;
            _minigameGameObject = (minigame as MonoBehaviour)?.gameObject;
            _activeSessionToken = ++_sessionSequence;

            _activeMinigame.Initialize(data);
            _activeMinigame.OnMinigameStart();

            MinigameResult startupResult = _activeMinigame.GetResult();
            if (startupResult != MinigameResult.None)
            {
                EndActiveMinigame();
                return null;
            }

            EventBus.Publish(new MinigameStartedEvent(data.minigameId));

            return _activeMinigame;
        }

        public void EndActiveMinigame()
        {
            if (_activeMinigame == null)
            {
                WarnIfDuplicateTerminalFlow("end", MinigameResult.None);
                return;
            }

            MinigameResult result = _activeMinigame.GetResult();
            string minigameId = _activeMinigame.GetMinigameId();

            _activeMinigame.OnMinigameEnd();

            if (_minigameGameObject != null)
            {
                Destroy(_minigameGameObject);
            }

            EventBus.Publish(new MinigameEndedEvent(result));

            if (ObjectiveManager.TryGetInstance(out ObjectiveManager objectiveManager))
            {
                objectiveManager.SyncAfterLoad();
            }
            else
            {
                Debug.LogWarning("[MinigameManager] ObjectiveManager is missing; objective sync skipped after minigame end.");
            }
            MinigameRewardSystem.DistributeRewards(result, minigameId, _activeSessionToken, _managerLifetimeScope);

            _activeMinigame = null;
            _minigameGameObject = null;
            _minigameType = null;
            _activeSessionToken = 0;
            _lastTerminalFlowFrame = Time.frameCount;
            _lastTerminalFlowType = "end";

            EnsureMinigameCanvasesHidden();
        }

        public void CancelActiveMinigame()
        {
            if (_activeMinigame == null)
            {
                WarnIfDuplicateTerminalFlow("cancel", MinigameResult.Cancelled);
                return;
            }

            _activeMinigame.OnMinigameEnd();

            if (_minigameGameObject != null)
            {
                Destroy(_minigameGameObject);
            }

            EventBus.Publish(new MinigameCancelledEvent(MinigameResult.Cancelled));

            _activeMinigame = null;
            _minigameGameObject = null;
            _minigameType = null;
            _activeSessionToken = 0;
            _lastTerminalFlowFrame = Time.frameCount;
            _lastTerminalFlowType = "cancel";

            EnsureMinigameCanvasesHidden();
        }

        public IMinigame GetActiveMinigame()
        {
            return _activeMinigame;
        }

        public bool IsMinigameActive()
        {
            return _activeMinigame != null && _activeMinigame.IsActive();
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

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ValidateRequiredCanvases())
            {
                enabled = false;
                return;
            }

            EnsureMinigameCanvasesHidden();
        }

        private void EnsureMinigameCanvasesHidden()
        {
            if (!ValidateRequiredCanvases())
            {
                return;
            }

            _cleaningCanvas.enabled = false;
            _weldingCanvas.enabled = false;
        }

        private bool ValidateRequiredCanvases()
        {
            if (_cleaningCanvas != null && _weldingCanvas != null)
            {
                return true;
            }

            Debug.LogError(MissingCanvasErrorMessage, this);
            return false;
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
    }
}
