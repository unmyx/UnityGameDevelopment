using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Game.Core;
using Game.Networking;

namespace Game.UI
{
    /// <summary>
    /// PauseMenuController manages pause menu UI and canvas setup.
    /// Ensures pause menu always renders on top, handles button interactions.
    /// </summary>
    public class PauseMenuController : MonoBehaviour
    {
        private Canvas _canvas;
        private bool _hasLoggedMissingRequiredReferences;
        private bool _hasLoggedMissingEventSystem;

        [SerializeField]
        private GameObject _pauseMenuPanel;

        [SerializeField]
        private Button _continueButton;

        [SerializeField]
        private Button _settingsButton;

        [SerializeField]
        private Button _quitButton;

        [SerializeField]
        private GameObject _settingsPanelPlaceholder;

        private void Awake()
        {
            if (!EnsureEventSystemExists())
            {
                enabled = false;
                return;
            }

            _canvas = GetComponent<Canvas>();
            if (_canvas != null)
            {
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.overrideSorting = true;
                _canvas.sortingOrder = 1000;
            }

            WarnIfRequiredReferencesMissing();

            HidePauseMenu();
        }

        private void OnEnable()
        {
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null)
            {
                pauseManager.OnPaused += ShowPauseMenu;
                pauseManager.OnResumed += HidePauseMenu;
            }

            SubscribeButtons();
            ReconcilePauseMenuVisibility();
        }

        private void OnDisable()
        {
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null)
            {
                pauseManager.OnPaused -= ShowPauseMenu;
                pauseManager.OnResumed -= HidePauseMenu;
            }

            UnsubscribeButtons();
        }

        private void SubscribeButtons()
        {
            if (_continueButton != null)
                _continueButton.onClick.AddListener(OnContinuePressed);

            if (_settingsButton != null)
                _settingsButton.onClick.AddListener(OnSettingsPressed);

            if (_quitButton != null)
                _quitButton.onClick.AddListener(OnQuitPressed);
        }

        private void WarnIfRequiredReferencesMissing()
        {
            if (_hasLoggedMissingRequiredReferences)
            {
                return;
            }

            if (_pauseMenuPanel == null || _continueButton == null || _quitButton == null)
            {
                _hasLoggedMissingRequiredReferences = true;
                Debug.LogWarning(
                    "[PauseMenuController] Required references are missing. " +
                    "Assign Pause Menu Panel, Continue Button, and Quit Button in the inspector.",
                    this);
            }
        }

        private void UnsubscribeButtons()
        {
            if (_continueButton != null)
                _continueButton.onClick.RemoveListener(OnContinuePressed);

            if (_settingsButton != null)
                _settingsButton.onClick.RemoveListener(OnSettingsPressed);

            if (_quitButton != null)
                _quitButton.onClick.RemoveListener(OnQuitPressed);
        }

        private void ShowPauseMenu()
        {
            if (_pauseMenuPanel != null)
                _pauseMenuPanel.SetActive(true);

            if (_canvas != null)
            {
                _canvas.enabled = true;
            }

            SelectDefaultButton();
        }

        private void HidePauseMenu()
        {
            if (_pauseMenuPanel != null)
                _pauseMenuPanel.SetActive(false);

            if (_settingsPanelPlaceholder != null)
                _settingsPanelPlaceholder.SetActive(false);

            if (_canvas != null)
            {
                _canvas.enabled = false;
            }
        }

        private void ReconcilePauseMenuVisibility()
        {
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null && pauseManager.IsPaused)
            {
                ShowPauseMenu();
                return;
            }

            HidePauseMenu();
        }

        private void OnContinuePressed()
        {
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null)
                pauseManager.Resume();
        }

        private void OnSettingsPressed()
        {
            if (_pauseMenuPanel != null)
                _pauseMenuPanel.SetActive(false);

            if (_settingsPanelPlaceholder != null)
                _settingsPanelPlaceholder.SetActive(true);
        }

        private void OnQuitPressed()
        {
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null)
                pauseManager.Resume();
            NetworkSafeSceneRouter.TryRouteToMenu(this, allowClientLocalLoad: true);
        }

        public void BackFromSettings()
        {
            if (_pauseMenuPanel != null)
                _pauseMenuPanel.SetActive(true);

            if (_settingsPanelPlaceholder != null)
                _settingsPanelPlaceholder.SetActive(false);

            SelectDefaultButton();
        }

        private bool EnsureEventSystemExists()
        {
            EventSystem eventSystem = EventSystem.current;
            bool exists = eventSystem != null || FindAnyObjectByType<EventSystem>() != null;
            if (!exists && !_hasLoggedMissingEventSystem)
            {
                _hasLoggedMissingEventSystem = true;
                Debug.LogError(
                    "[PauseMenuController] Missing required EventSystem in gameplay scene. " +
                    "Add EventSystem + InputSystemUIInputModule to scene setup.",
                    this);
            }

            return exists;
        }

        private void SelectDefaultButton()
        {
            if (_continueButton == null || !_continueButton.gameObject.activeInHierarchy || !_continueButton.interactable)
            {
                return;
            }

            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                eventSystem = FindAnyObjectByType<EventSystem>();
            }

            if (eventSystem == null)
            {
                EnsureEventSystemExists();
                return;
            }

            eventSystem.SetSelectedGameObject(null);
            eventSystem.SetSelectedGameObject(_continueButton.gameObject);
        }
    }
}
