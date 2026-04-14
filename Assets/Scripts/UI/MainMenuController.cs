using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using Game.Core;
using Game.Inventory;
using TMPro;

namespace Game.UI
{
    /// <summary>
    /// MainMenuController manages the main menu UI and navigation.
    /// 
    /// Features:
    /// - Start Game button (transitions to Playing state)
    /// - Options button (placeholder for future settings UI)
    /// - Quit button (exits application)
    /// - Clean separation between UI and game logic
    /// 
    /// Setup:
    /// 1. Create Canvas object in scene
    /// 2. Add this script to the Canvas
    /// 3. Create buttons and assign in Inspector:
    ///    - Start Button
    ///    - Options Button
    ///    - Quit Button
    /// 4. Script auto-subscribes to button clicks
    /// 
    /// Integration:
    /// - Uses GameManager.Instance.ChangeState() to transition states
    /// - Listens to GameManager state changes if needed
    /// - Event-driven via button onClick events
    /// 
    /// Architecture:
    /// - Minimal logic (mostly delegation)
    /// - No direct gameplay logic
    /// - Reusable button references
    /// - Easy to extend with more menu options
    /// 
    /// Future Extensions:
    /// - Options/Settings submenu
    /// - Load Game functionality
    /// - Credits screen
    /// - Audio settings
    /// - Graphics settings
    /// - Key bindings
    /// </summary>
    public class MainMenuController : MonoBehaviour
    {
        private const string GameplaySceneName = "GameplayScene";

        [SerializeField]
        private Button _startButton;

        [SerializeField]
        private Button _optionsButton;

        [SerializeField]
        private Button _quitButton;

        [SerializeField]
        private GameObject _mainMenuPanel;

        [SerializeField]
        private GameObject _optionsPanelPlaceholder;

        [Header("Game Over Flow")]
        [SerializeField]
        private GameObject _gameOverPanel;

        [SerializeField]
        private TextMeshProUGUI _gameOverReasonText;

        [SerializeField]
        private Button _newRunButton;

        [SerializeField]
        private Button _gameOverContinueButton;

        [SerializeField]
        private Button _gameOverBackButton;

        private bool _hasLoggedMissingRequiredReferences;
        private bool _hasLoggedMissingEventSystem;

        private void OnEnable()
        {
            EnsureMenuRuntimeState();
            WarnIfRequiredReferencesMissing();
            InitializeMenuButtons();
            HideGameOverPanel();
        }

        private void Start()
        {
            EnsureMenuRuntimeState();
            HideGameOverPanel();
        }

        private void OnDisable()
        {
            UnsubscribeMenuButtons();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus || !isActiveAndEnabled)
            {
                return;
            }

            EnsureMenuRuntimeState();
        }

        private void EnsureMenuRuntimeState()
        {
            PauseManager pauseManager = PauseManager.HasInstance ? PauseManager.Instance : null;
            if (pauseManager != null && pauseManager.IsPaused)
            {
                pauseManager.Resume();
            }

            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (!EnsureEventSystemExists())
            {
                enabled = false;
            }
        }

        private bool EnsureEventSystemExists()
        {
            EventSystem eventSystem = EventSystem.current;
            bool exists = eventSystem != null || FindAnyObjectByType<EventSystem>() != null;
            if (!exists && !_hasLoggedMissingEventSystem)
            {
                _hasLoggedMissingEventSystem = true;
                Debug.LogError(
                    "[MainMenuController] Missing required EventSystem in menu scene. " +
                    "Add EventSystem + InputSystemUIInputModule to scene setup.",
                    this);
            }

            return exists;
        }

        private void InitializeMenuButtons()
        {
            if (_startButton != null)
            {
                _startButton.onClick.AddListener(OnStartButtonPressed);
            }

            if (_optionsButton != null)
            {
                _optionsButton.onClick.AddListener(OnOptionsButtonPressed);
            }

            if (_quitButton != null)
            {
                _quitButton.onClick.AddListener(OnQuitButtonPressed);
            }

            if (_newRunButton != null)
            {
                _newRunButton.onClick.AddListener(OnNewRunPressed);
            }

            if (_gameOverBackButton != null)
            {
                _gameOverBackButton.onClick.AddListener(OnGameOverBackPressed);
            }
        }

        private void WarnIfRequiredReferencesMissing()
        {
            if (_hasLoggedMissingRequiredReferences)
            {
                return;
            }

            if (_startButton == null || _quitButton == null || _mainMenuPanel == null)
            {
                _hasLoggedMissingRequiredReferences = true;
                Debug.LogWarning(
                    "[MainMenuController] Required references are missing. " +
                    "Assign Start Button, Quit Button, and Main Menu Panel in the inspector.",
                    this);
                return;
            }

            if (_gameOverPanel == null || _newRunButton == null || _gameOverReasonText == null)
            {
                _hasLoggedMissingRequiredReferences = true;
                Debug.LogWarning(
                    "[MainMenuController] Game over flow references are missing. " +
                    "Assign GameOverPanel, GameOverReasonText, and NewRunButton.",
                    this);
            }
        }
        private void UnsubscribeMenuButtons()
        {
            if (_startButton != null)
                _startButton.onClick.RemoveListener(OnStartButtonPressed);

            if (_optionsButton != null)
                _optionsButton.onClick.RemoveListener(OnOptionsButtonPressed);

            if (_quitButton != null)
                _quitButton.onClick.RemoveListener(OnQuitButtonPressed);

            if (_newRunButton != null)
                _newRunButton.onClick.RemoveListener(OnNewRunPressed);

            if (_gameOverBackButton != null)
                _gameOverBackButton.onClick.RemoveListener(OnGameOverBackPressed);
        }

        private void OnStartButtonPressed()
        {
            HideGameOverPanel();

            if (!SaveManager.SaveFileExists())
            {
                StartFreshRun(deleteSaveFile: false);
                return;
            }

            if (!SaveManager.TryReadRunFailureMeta(out bool runFailed, out string reason))
            {
                StartGameplayScene();
                return;
            }

            if (!runFailed)
            {
                StartGameplayScene();
                return;
            }

            ShowGameOverPanel(reason);
        }

        public void OnNewRunPressed()
        {
            StartFreshRun(deleteSaveFile: true);
        }

        public void OnGameOverBackPressed()
        {
            ShowMainMenu();
        }

        private void OnOptionsButtonPressed()
        {
            if (_optionsPanelPlaceholder != null)
            {
                _optionsPanelPlaceholder.SetActive(true);

                if (_mainMenuPanel != null)
                {
                    _mainMenuPanel.SetActive(false);
                }
            }
        }

        private void OnQuitButtonPressed()
        {

            #if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
            #else
            Application.Quit();
            #endif
        }

        public void ShowMainMenu()
        {
            if (_mainMenuPanel != null)
            {
                _mainMenuPanel.SetActive(true);
            }

            if (_optionsPanelPlaceholder != null)
            {
                _optionsPanelPlaceholder.SetActive(false);
            }

            HideGameOverPanel();
        }

        public void HideMainMenu()
        {
            if (_mainMenuPanel != null)
            {
                _mainMenuPanel.SetActive(false);
            }
        }

        public void BackFromOptions()
        {
            ShowMainMenu();
        }

        public void SetMenuEnabled(bool enabled)
        {
            if (_startButton != null)
                _startButton.interactable = enabled;
            if (_optionsButton != null)
                _optionsButton.interactable = enabled;
            if (_quitButton != null)
                _quitButton.interactable = enabled;
            if (_newRunButton != null)
                _newRunButton.interactable = enabled;
        }

        private void StartFreshRun(bool deleteSaveFile)
        {
            SaveManager.ClearRuntimeCaches();

            if (deleteSaveFile)
            {
                SaveManager.DeleteSave();
            }

            GameManager gameManager = ResolveActiveGameManager();
            if (gameManager != null)
            {
                gameManager.ResetForNewRun();
            }

            if (ObjectiveManager.TryGetInstance(out ObjectiveManager objectiveManager) && objectiveManager != null)
            {
                objectiveManager.ResetForNewRun();
            }

            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem != null)
            {
                inventorySystem.ClearInventory();
            }

            StartGameplayScene();
        }

        private void StartGameplayScene()
        {
            HideGameOverPanel();
            SceneManager.LoadScene(GameplaySceneName);
        }

        private void ShowGameOverPanel(string reason)
        {
            if (_gameOverPanel == null)
            {
                Debug.LogWarning("[MainMenuController] Cannot show game over panel because reference is missing.", this);
                return;
            }

            if (_mainMenuPanel != null)
            {
                _mainMenuPanel.SetActive(false);
            }

            if (_optionsPanelPlaceholder != null)
            {
                _optionsPanelPlaceholder.SetActive(false);
            }

            if (_gameOverReasonText != null)
            {
                _gameOverReasonText.text = string.IsNullOrWhiteSpace(reason)
                    ? "Your run has ended. Start a new run to continue."
                    : reason;
            }

            if (_gameOverContinueButton != null)
            {
                _gameOverContinueButton.interactable = false;
            }

            _gameOverPanel.SetActive(true);
        }

        private void HideGameOverPanel()
        {
            if (_gameOverPanel != null)
            {
                _gameOverPanel.SetActive(false);
            }
        }

        private static GameManager ResolveActiveGameManager()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                return gameManager;
            }

            gameManager = FindAnyObjectByType<GameManager>();
            if (gameManager == null)
            {
                Debug.LogError("[MainMenuController] Missing GameManager in Menu scene. New run reset cannot be applied before scene load.");
            }

            return gameManager;
        }
    }
}

