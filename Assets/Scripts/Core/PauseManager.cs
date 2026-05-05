using UnityEngine;
using Game.Input;
using Game.Inventory;
using Game.Minigames;
using Game.Player;
using Unity.Netcode;

namespace Game.Core
{
    /// <summary>
    /// PauseManager handles pause/resume logic globally.
    /// Detects ESC key, manages Time.timeScale, fires pause/resume events.
    /// </summary>
    public class PauseManager : MonoBehaviour
    {
        private const string PauseCursorAuthorityOwner = "pause_menu";
        private const string MissingInstanceMessage =
            "[PauseManager] Instance requested but no PauseManager exists in the active scene. " +
            "Add PauseManager to your bootstrap/gameplay scene instead of relying on runtime auto-creation.";

        private static PauseManager _instance;
        private static bool _hasLoggedMissingInstance;
        private static bool _isShuttingDown;
        public static PauseManager Instance
        {
            get
            {
                if (_isShuttingDown)
                {
                    return null;
                }

                if (_instance == null)
                {
                    if (TryGetInstance(out PauseManager instance))
                    {
                        return instance;
                    }

                    if (!_hasLoggedMissingInstance)
                    {
                        Debug.LogWarning(MissingInstanceMessage);
                        _hasLoggedMissingInstance = true;
                    }
                }
                return _instance;
            }
        }

        public static bool HasInstance => !_isShuttingDown && TryGetInstance(out _);

        public static bool TryGetInstance(out PauseManager instance)
        {
            if (_instance != null)
            {
                instance = _instance;
                return true;
            }

            _instance = FindAnyObjectByType<PauseManager>();
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

        private bool _isPaused = false;
        public bool IsPaused => _isPaused;

        private CursorLockMode _cursorLockModeBeforePause = CursorLockMode.Locked;
        private bool _cursorVisibleBeforePause;
        private bool _hasCursorStateBeforePause;

        public delegate void PauseDelegate();
        public event PauseDelegate OnPaused;
        public event PauseDelegate OnResumed;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _hasLoggedMissingInstance = false;
            DetachFromParentIfNeeded();
            DontDestroyOnLoad(gameObject);
        }

        private void DetachFromParentIfNeeded()
        {
            if (transform.parent != null)
            {
                transform.SetParent(null, true);
            }
        }

        private void OnApplicationQuit()
        {
            TryAutoSave();
            _isShuttingDown = true;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _isShuttingDown = true;
                _instance = null;
            }
        }

        private void Update()
        {
            InputManager inputManager = InputManager.Instance;
            if (inputManager == null || !inputManager.IsPausePressed())
            {
                return;
            }

            if (_isPaused)
            {
                Resume();
                return;
            }

            if (!CanEnterPause())
            {
                return;
            }

            Pause();
        }

        public void Pause()
        {
            if (_isPaused || !CanEnterPause())
                return;

            CaptureCursorStateForPause();
            if (!PlayerContextLocator.TryAcquireLocalCursorAuthority(PauseCursorAuthorityOwner, CursorLockMode.None, true))
            {
                SetCursorForPauseMenu();
            }
            else
            {
                _hasCursorStateBeforePause = false;
            }

            PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.Paused);
            _isPaused = true;
            Time.timeScale = 0f;
            OnPaused?.Invoke();
            TryAutoSave();
        }

        public void Resume()
        {
            if (!_isPaused)
                return;

            _isPaused = false;
            Time.timeScale = 1f;
            if (!PlayerContextLocator.TryReleaseLocalCursorAuthority(PauseCursorAuthorityOwner))
            {
                RestoreCursorStateAfterPause();
            }
            else
            {
                _hasCursorStateBeforePause = false;
            }

            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager != null && minigameManager.IsMinigameActiveForOwner(PlayerContextRegistry.DefaultLocalPlayerId))
            {
                PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.Minigame);
            }
            else
            {
                PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.FreePlay);
            }

            OnResumed?.Invoke();
        }

        public bool CanProcessInput()
        {
            return !_isPaused;
        }

        private static bool CanEnterPause()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return false;
            }

            if (gameManager.CurrentState != GameState.FreePlay)
            {
                return false;
            }

            if (gameManager.IsRunFailed() || gameManager.GetCurrentRunPhase() == GameManager.RunPhase.GameOver)
            {
                return false;
            }

            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager != null && minigameManager.IsMinigameActiveForOwner(PlayerContextRegistry.DefaultLocalPlayerId))
            {
                return false;
            }

            if (PlayerContextLocator.TryGetLocalPresentationMode(out LocalPlayerPresentationMode mode)
                && mode == LocalPlayerPresentationMode.Minigame)
            {
                return false;
            }

            return true;
        }

        private static bool CanAutoSaveNow()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.CurrentState != GameState.FreePlay)
            {
                return false;
            }

            return Object.FindAnyObjectByType<InventorySystem>() != null
                   && ObjectiveManager.TryGetInstance(out _);
        }

        private static void TryAutoSave()
        {
            if (!CanAutoSaveNow())
            {
                return;
            }

            if (IsNonAuthoritativeNetworkClient())
            {
                return;
            }

            SaveManager.Save();
        }

        private static bool IsNonAuthoritativeNetworkClient()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening && manager.IsClient && !manager.IsServer;
        }

        private void CaptureCursorStateForPause()
        {
            _cursorLockModeBeforePause = Cursor.lockState;
            _cursorVisibleBeforePause = Cursor.visible;
            _hasCursorStateBeforePause = true;
        }

        private static void SetCursorForPauseMenu()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void RestoreCursorStateAfterPause()
        {
            if (_hasCursorStateBeforePause)
            {
                Cursor.lockState = _cursorLockModeBeforePause;
                Cursor.visible = _cursorVisibleBeforePause;
                _hasCursorStateBeforePause = false;
            }
        }
    }
}
