using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using Game.Core;
using Game.Inventory;
using Game.Networking;
using TMPro;
using Unity.Netcode;

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
        private const string NetworkSandboxSceneName = "NetworkSandbox";
        private const string DefaultNetworkAddress = "127.0.0.1";
        private const ushort DefaultNetworkPort = 7777;
        private const string DefaultRelayJoinCode = "";
        private const string GameplaySceneName = "GameplayScene";
        private const string HomeSceneName = "HomeScene";

        [SerializeField]
        private Button _startButton;

        [SerializeField]
        private Button _optionsButton;

        [SerializeField]
        private Button _multiplayerButton;

        [SerializeField]
        private Button _quitButton;

        [SerializeField]
        private GameObject _mainMenuPanel;

        [SerializeField]
        private GameObject _optionsPanelPlaceholder;

        [SerializeField]
        private GameObject _multiplayerPanel;

        [SerializeField]
        private Button _hostGameButton;

        [SerializeField]
        private Button _joinGameButton;

        [SerializeField]
        private Button _hostRelayButton;

        [SerializeField]
        private Button _joinRelayButton;

        [SerializeField]
        private Button _multiplayerBackButton;

        [SerializeField]
        private TMP_InputField _addressInput;

        [SerializeField]
        private TMP_InputField _portInput;

        [SerializeField]
        private TMP_InputField _relayJoinCodeInput;

        [SerializeField]
        private TextMeshProUGUI _relayJoinCodeDisplayText;

        [SerializeField]
        private TextMeshProUGUI _multiplayerStatusText;

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
        private bool _multiplayerUiInitialized;
        private Button _resolvedMultiplayerEntryButton;
        private bool _usingOptionsAsMultiplayerEntry;

        private void OnEnable()
        {
            EnsureMenuRuntimeState();
            CleanupNetworkRuntimeForMenuEntry();
            EnsureMultiplayerUiSetup();
            WarnIfRequiredReferencesMissing();
            InitializeMenuButtons();
            HideGameOverPanel();
            ShowMainMenu();
        }

        private void Start()
        {
            EnsureMenuRuntimeState();
            EnsureMultiplayerUiSetup();
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

            if (_resolvedMultiplayerEntryButton != null)
            {
                _resolvedMultiplayerEntryButton.onClick.AddListener(OnMultiplayerButtonPressed);
            }

            if (_optionsButton != null && !_usingOptionsAsMultiplayerEntry)
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

            if (_hostGameButton != null)
            {
                _hostGameButton.onClick.AddListener(OnHostGamePressed);
            }

            if (_joinGameButton != null)
            {
                _joinGameButton.onClick.AddListener(OnJoinGamePressed);
            }

            if (_hostRelayButton != null)
            {
                _hostRelayButton.onClick.AddListener(OnHostRelayPressed);
            }

            if (_joinRelayButton != null)
            {
                _joinRelayButton.onClick.AddListener(OnJoinRelayPressed);
            }

            if (_multiplayerBackButton != null)
            {
                _multiplayerBackButton.onClick.AddListener(OnMultiplayerBackPressed);
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

            if (_resolvedMultiplayerEntryButton != null)
                _resolvedMultiplayerEntryButton.onClick.RemoveListener(OnMultiplayerButtonPressed);

            if (_optionsButton != null && !_usingOptionsAsMultiplayerEntry)
                _optionsButton.onClick.RemoveListener(OnOptionsButtonPressed);

            if (_quitButton != null)
                _quitButton.onClick.RemoveListener(OnQuitButtonPressed);

            if (_newRunButton != null)
                _newRunButton.onClick.RemoveListener(OnNewRunPressed);

            if (_gameOverBackButton != null)
                _gameOverBackButton.onClick.RemoveListener(OnGameOverBackPressed);

            if (_hostGameButton != null)
                _hostGameButton.onClick.RemoveListener(OnHostGamePressed);

            if (_joinGameButton != null)
                _joinGameButton.onClick.RemoveListener(OnJoinGamePressed);

            if (_hostRelayButton != null)
                _hostRelayButton.onClick.RemoveListener(OnHostRelayPressed);

            if (_joinRelayButton != null)
                _joinRelayButton.onClick.RemoveListener(OnJoinRelayPressed);

            if (_multiplayerBackButton != null)
                _multiplayerBackButton.onClick.RemoveListener(OnMultiplayerBackPressed);
        }

        private void OnStartButtonPressed()
        {
            HideGameOverPanel();

            if (!SaveManager.SaveFileExists())
            {
                StartFreshRun(deleteSaveFile: false);
                return;
            }

            if (!SaveManager.TryReadRunResumeMeta(out int savedRunPhase, out int _, out bool runFailed))
            {
                StartHomeScene();
                return;
            }

            if (!runFailed)
            {
                RouteToSceneForSavedRunPhase(savedRunPhase);
                return;
            }

            if (!SaveManager.TryReadRunFailureMeta(out _, out string reason))
            {
                reason = string.Empty;
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

        private void OnMultiplayerButtonPressed()
        {
            if (_multiplayerPanel != null)
            {
                _multiplayerPanel.SetActive(true);
            }

            if (_mainMenuPanel != null)
            {
                _mainMenuPanel.SetActive(false);
            }

            SetMultiplayerStatus(string.Empty);
            InitializeMultiplayerInputsIfNeeded();
        }

        private void OnHostGamePressed()
        {
            LaunchMultiplayer(NetworkStartupMode.Host);
        }

        private void OnJoinGamePressed()
        {
            LaunchMultiplayer(NetworkStartupMode.Client);
        }

        private void OnHostRelayPressed()
        {
            LaunchRelayHost();
        }

        private void OnJoinRelayPressed()
        {
            LaunchRelayClient();
        }

        private void OnMultiplayerBackPressed()
        {
            ShowMainMenu();
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

            if (_multiplayerPanel != null)
            {
                _multiplayerPanel.SetActive(false);
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
            if (_resolvedMultiplayerEntryButton != null)
                _resolvedMultiplayerEntryButton.interactable = enabled;
            if (_quitButton != null)
                _quitButton.interactable = enabled;
            if (_newRunButton != null)
                _newRunButton.interactable = enabled;
            if (_hostGameButton != null)
                _hostGameButton.interactable = enabled;
            if (_joinGameButton != null)
                _joinGameButton.interactable = enabled;
            if (_hostRelayButton != null)
                _hostRelayButton.interactable = enabled;
            if (_joinRelayButton != null)
                _joinRelayButton.interactable = enabled;
            if (_multiplayerBackButton != null)
                _multiplayerBackButton.interactable = enabled;
        }

        private void EnsureMultiplayerUiSetup()
        {
            if (_multiplayerUiInitialized)
            {
                return;
            }

            _resolvedMultiplayerEntryButton = _multiplayerButton != null ? _multiplayerButton : _optionsButton;
            _usingOptionsAsMultiplayerEntry = _multiplayerButton == null && _resolvedMultiplayerEntryButton == _optionsButton;
            if (_usingOptionsAsMultiplayerEntry)
            {
                SetButtonText(_resolvedMultiplayerEntryButton, "Multiplayer");
            }

            if (_multiplayerPanel == null)
            {
                _multiplayerPanel = _optionsPanelPlaceholder;
            }

            if (_multiplayerPanel != null)
            {
                BuildMultiplayerFallbackControls(_multiplayerPanel.transform);
                _multiplayerPanel.SetActive(false);
            }

            InitializeMultiplayerInputsIfNeeded();
            _multiplayerUiInitialized = true;
        }

        private void BuildMultiplayerFallbackControls(Transform panelTransform)
        {
            if (_hostGameButton != null && _joinGameButton != null && _multiplayerBackButton != null
                && _addressInput != null && _portInput != null
                && _hostRelayButton != null && _joinRelayButton != null && _relayJoinCodeInput != null)
            {
                return;
            }

            Transform root = panelTransform.Find("MultiplayerRuntimeRoot");
            if (root == null)
            {
                GameObject rootObject = new GameObject("MultiplayerRuntimeRoot", typeof(RectTransform));
                root = rootObject.transform;
                root.SetParent(panelTransform, false);
                RectTransform rootRect = (RectTransform)root;
                rootRect.anchorMin = new Vector2(0.5f, 0.5f);
                rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                rootRect.pivot = new Vector2(0.5f, 0.5f);
                rootRect.sizeDelta = new Vector2(560f, 820f);
            }

            if (_multiplayerStatusText == null)
            {
                _multiplayerStatusText = EnsureLabel(root, "StatusText", new Vector2(0f, 220f), new Vector2(520f, 44f), string.Empty);
                _multiplayerStatusText.alignment = TextAlignmentOptions.Center;
            }

            EnsureLabel(root, "AddressLabel", new Vector2(-180f, 150f), new Vector2(140f, 40f), "Address");
            if (_addressInput == null)
            {
                _addressInput = EnsureInput(root, "AddressInput", new Vector2(60f, 150f), new Vector2(320f, 46f), DefaultNetworkAddress);
            }

            EnsureLabel(root, "PortLabel", new Vector2(-180f, 90f), new Vector2(140f, 40f), "Port");
            if (_portInput == null)
            {
                _portInput = EnsureInput(root, "PortInput", new Vector2(60f, 90f), new Vector2(320f, 46f), DefaultNetworkPort.ToString());
            }

            if (_hostGameButton == null)
            {
                _hostGameButton = EnsureButton(root, "HostGameButton", new Vector2(0f, 20f), new Vector2(320f, 56f), "Host Game");
            }

            if (_joinGameButton == null)
            {
                _joinGameButton = EnsureButton(root, "JoinGameButton", new Vector2(0f, -50f), new Vector2(320f, 56f), "Join Game");
            }

            EnsureLabel(root, "RelayCodeLabel", new Vector2(-180f, -120f), new Vector2(140f, 40f), "Relay Code");
            if (_relayJoinCodeInput == null)
            {
                _relayJoinCodeInput = EnsureInput(root, "RelayJoinCodeInput", new Vector2(60f, -120f), new Vector2(320f, 46f), "ABC123");
            }

            if (_hostRelayButton == null)
            {
                _hostRelayButton = EnsureButton(root, "HostRelayButton", new Vector2(0f, -190f), new Vector2(320f, 56f), "Host via Relay");
            }

            if (_joinRelayButton == null)
            {
                _joinRelayButton = EnsureButton(root, "JoinRelayButton", new Vector2(0f, -260f), new Vector2(320f, 56f), "Join via Relay");
            }

            if (_relayJoinCodeDisplayText == null)
            {
                _relayJoinCodeDisplayText = EnsureLabel(root, "RelayCodeDisplay", new Vector2(0f, -320f), new Vector2(520f, 44f), string.Empty);
                _relayJoinCodeDisplayText.alignment = TextAlignmentOptions.Center;
            }

            if (_multiplayerBackButton == null)
            {
                _multiplayerBackButton = EnsureButton(root, "MultiplayerBackButton", new Vector2(0f, -380f), new Vector2(320f, 56f), "Back");
            }
        }

        private void LaunchMultiplayer(NetworkStartupMode mode)
        {
            if (!TryReadMultiplayerConnectionSettings(out string address, out ushort port, out string validationError))
            {
                SetMultiplayerStatus(validationError);
                return;
            }

            CleanupNetworkRuntimeForMenuEntry();
            NetworkModeRuntime.Address = address;
            NetworkModeRuntime.Port = port;
            NetworkModeRuntime.StartupMode = mode;
            NetworkModeRuntime.RelayJoinCode = string.Empty;
            NetworkModeRuntime.LastStartupMessage = string.Empty;

            SceneManager.LoadScene(NetworkSandboxSceneName);
        }

        private void LaunchRelayHost()
        {
            CleanupNetworkRuntimeForMenuEntry();
            NetworkModeRuntime.StartupMode = NetworkStartupMode.RelayHost;
            NetworkModeRuntime.RelayJoinCode = string.Empty;
            NetworkModeRuntime.LastStartupMessage = string.Empty;
            SceneManager.LoadScene(NetworkSandboxSceneName);
        }

        private void LaunchRelayClient()
        {
            string joinCode = _relayJoinCodeInput != null ? _relayJoinCodeInput.text : string.Empty;
            joinCode = string.IsNullOrWhiteSpace(joinCode) ? DefaultRelayJoinCode : joinCode.Trim();
            if (string.IsNullOrWhiteSpace(joinCode))
            {
                SetMultiplayerStatus("Enter a Relay join code.");
                return;
            }

            CleanupNetworkRuntimeForMenuEntry();
            NetworkModeRuntime.StartupMode = NetworkStartupMode.RelayClient;
            NetworkModeRuntime.RelayJoinCode = joinCode;
            NetworkModeRuntime.LastStartupMessage = string.Empty;
            SceneManager.LoadScene(NetworkSandboxSceneName);
        }

        private bool TryReadMultiplayerConnectionSettings(out string address, out ushort port, out string validationError)
        {
            string normalizedAddress = _addressInput != null
                ? _addressInput.text
                : DefaultNetworkAddress;
            normalizedAddress = string.IsNullOrWhiteSpace(normalizedAddress)
                ? DefaultNetworkAddress
                : normalizedAddress.Trim();

            string normalizedPort = _portInput != null
                ? _portInput.text
                : DefaultNetworkPort.ToString();
            normalizedPort = string.IsNullOrWhiteSpace(normalizedPort)
                ? DefaultNetworkPort.ToString()
                : normalizedPort.Trim();

            if (!ushort.TryParse(normalizedPort, out port) || port == 0)
            {
                address = normalizedAddress;
                validationError = "Enter a valid port (1-65535).";
                return false;
            }

            address = normalizedAddress;
            validationError = string.Empty;
            return true;
        }

        private void InitializeMultiplayerInputsIfNeeded()
        {
            if (_addressInput != null && string.IsNullOrWhiteSpace(_addressInput.text))
            {
                _addressInput.text = string.IsNullOrWhiteSpace(NetworkModeRuntime.Address)
                    ? DefaultNetworkAddress
                    : NetworkModeRuntime.Address.Trim();
            }

            if (_portInput != null && string.IsNullOrWhiteSpace(_portInput.text))
            {
                ushort runtimePort = NetworkModeRuntime.Port > 0 ? NetworkModeRuntime.Port : DefaultNetworkPort;
                _portInput.text = runtimePort.ToString();
            }

            if (_relayJoinCodeInput != null && string.IsNullOrWhiteSpace(_relayJoinCodeInput.text))
            {
                _relayJoinCodeInput.text = string.IsNullOrWhiteSpace(NetworkModeRuntime.RelayJoinCode)
                    ? DefaultRelayJoinCode
                    : NetworkModeRuntime.RelayJoinCode.Trim();
            }

            if (_relayJoinCodeDisplayText != null)
            {
                _relayJoinCodeDisplayText.text = string.IsNullOrWhiteSpace(NetworkModeRuntime.RelayJoinCode)
                    ? string.Empty
                    : $"Last Relay Join Code: {NetworkModeRuntime.RelayJoinCode}";
            }

            if (!string.IsNullOrWhiteSpace(NetworkModeRuntime.LastStartupMessage))
            {
                SetMultiplayerStatus(NetworkModeRuntime.LastStartupMessage);
            }
        }

        private void SetMultiplayerStatus(string message)
        {
            if (_multiplayerStatusText == null)
            {
                return;
            }

            _multiplayerStatusText.text = message ?? string.Empty;
        }

        private static void CleanupNetworkRuntimeForMenuEntry()
        {
            NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
            NetworkModeRuntime.RelayJoinCode = string.Empty;
            NetworkModeRuntime.LastStartupMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(NetworkModeRuntime.Address))
            {
                NetworkModeRuntime.Address = DefaultNetworkAddress;
            }

            if (NetworkModeRuntime.Port == 0)
            {
                NetworkModeRuntime.Port = DefaultNetworkPort;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null)
            {
                return;
            }

            if (manager.IsListening)
            {
                NetworkSessionLifecycleCoordinator.MarkLocalShutdownIntent();
                manager.Shutdown();
            }

            if (Application.isPlaying)
            {
                Object.DestroyImmediate(manager.gameObject);
            }
            else
            {
                Object.Destroy(manager.gameObject);
            }
        }

        private static Button EnsureButton(Transform parent, string name, Vector2 anchoredPosition, Vector2 size, string text)
        {
            Transform existing = parent.Find(name);
            GameObject buttonObject;
            if (existing == null)
            {
                buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
                buttonObject.transform.SetParent(parent, false);
            }
            else
            {
                buttonObject = existing.gameObject;
            }

            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            Image image = buttonObject.GetComponent<Image>();
            image.color = Color.white;

            Button button = buttonObject.GetComponent<Button>();
            SetButtonText(button, text);
            return button;
        }

        private static TMP_InputField EnsureInput(Transform parent, string name, Vector2 anchoredPosition, Vector2 size, string placeholderText)
        {
            Transform existing = parent.Find(name);
            GameObject inputObject;
            if (existing == null)
            {
                inputObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
                inputObject.transform.SetParent(parent, false);
            }
            else
            {
                inputObject = existing.gameObject;
            }

            RectTransform rect = inputObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            Image background = inputObject.GetComponent<Image>();
            background.color = new Color(1f, 1f, 1f, 0.95f);

            TMP_InputField inputField = inputObject.GetComponent<TMP_InputField>();
            inputField.textViewport = EnsureTextViewport(inputObject.transform);
            inputField.textComponent = EnsureInputText(inputObject.transform, inputField.textViewport.transform);
            inputField.placeholder = EnsurePlaceholderText(inputObject.transform, inputField.textViewport.transform, placeholderText);
            inputField.text = string.Empty;
            return inputField;
        }

        private static RectTransform EnsureTextViewport(Transform inputTransform)
        {
            Transform existing = inputTransform.Find("Text Area");
            GameObject viewportObject;
            if (existing == null)
            {
                viewportObject = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
                viewportObject.transform.SetParent(inputTransform, false);
            }
            else
            {
                viewportObject = existing.gameObject;
            }

            RectTransform viewport = viewportObject.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(10f, 6f);
            viewport.offsetMax = new Vector2(-10f, -6f);
            return viewport;
        }

        private static TextMeshProUGUI EnsureInputText(Transform inputTransform, Transform viewport)
        {
            Transform existing = inputTransform.Find("Text Area/Text");
            GameObject textObject;
            if (existing == null)
            {
                textObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                textObject.transform.SetParent(viewport, false);
            }
            else
            {
                textObject = existing.gameObject;
            }

            RectTransform rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
            text.text = string.Empty;
            text.fontSize = 28f;
            text.color = Color.black;
            text.alignment = TextAlignmentOptions.Left;
            return text;
        }

        private static TextMeshProUGUI EnsurePlaceholderText(Transform inputTransform, Transform viewport, string content)
        {
            Transform existing = inputTransform.Find("Text Area/Placeholder");
            GameObject placeholderObject;
            if (existing == null)
            {
                placeholderObject = new GameObject("Placeholder", typeof(RectTransform), typeof(TextMeshProUGUI));
                placeholderObject.transform.SetParent(viewport, false);
            }
            else
            {
                placeholderObject = existing.gameObject;
            }

            RectTransform rect = placeholderObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            TextMeshProUGUI placeholder = placeholderObject.GetComponent<TextMeshProUGUI>();
            placeholder.text = content;
            placeholder.fontSize = 28f;
            placeholder.color = new Color(0f, 0f, 0f, 0.5f);
            placeholder.alignment = TextAlignmentOptions.Left;
            return placeholder;
        }

        private static TextMeshProUGUI EnsureLabel(Transform parent, string name, Vector2 anchoredPosition, Vector2 size, string content)
        {
            Transform existing = parent.Find(name);
            GameObject labelObject;
            if (existing == null)
            {
                labelObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
                labelObject.transform.SetParent(parent, false);
            }
            else
            {
                labelObject = existing.gameObject;
            }

            RectTransform rect = labelObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
            label.text = content;
            label.fontSize = 28f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            return label;
        }

        private static void SetButtonText(Button button, string text)
        {
            if (button == null)
            {
                return;
            }

            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null)
            {
                GameObject textObject = new GameObject("Text (TMP)", typeof(RectTransform), typeof(TextMeshProUGUI));
                textObject.transform.SetParent(button.transform, false);
                RectTransform textRect = textObject.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
                label = textObject.GetComponent<TextMeshProUGUI>();
                label.alignment = TextAlignmentOptions.Center;
                label.color = Color.black;
                label.fontSize = 30f;
            }

            label.text = text;
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

            StartHomeScene();
        }

        private void StartGameplayScene()
        {
            HideGameOverPanel();
            SceneManager.LoadScene(GameplaySceneName);
        }

        private void StartHomeScene()
        {
            HideGameOverPanel();
            SceneManager.LoadScene(HomeSceneName);
        }

        private void RouteToSceneForSavedRunPhase(int savedRunPhase)
        {
            if (savedRunPhase == (int)GameManager.RunPhase.Work)
            {
                StartGameplayScene();
                return;
            }

            StartHomeScene();
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

