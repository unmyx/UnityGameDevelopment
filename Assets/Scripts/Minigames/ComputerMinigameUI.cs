using System;
using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Minigames
{
    /// <summary>
    /// Browser-style UI view layer for the computer minigame.
    /// </summary>
    public class ComputerMinigameUI : MonoBehaviour
    {
        public event Action CloseRequested;
        public event Action SellRequested;
        public event Action<string> UpgradeRequested;

        private enum BrowserTab
        {
            Sell,
            Upgrade
        }

        private const string UpgradeInventoryId = GameManager.UpgradeIdInventoryQuickSlots;
        private const string UpgradeCleaningId = GameManager.UpgradeIdCleaningTool;
        private const string UpgradeWeldingId = GameManager.UpgradeIdWeldingTool;

        private Canvas _canvas;
        private bool _initialized;

        private RectTransform _rootPanel;
        private RectTransform _sellPanel;
        private RectTransform _upgradePanel;

        private Button _sellTabButton;
        private Button _upgradeTabButton;
        private Button _closeButton;

        private Button _sellActionButton;
        private Button _upgradeInventoryButton;
        private Button _upgradeCleaningButton;
        private Button _upgradeWeldingButton;

        private TextMeshProUGUI _sellSummaryText;
        private TextMeshProUGUI _walletText;
        private TextMeshProUGUI _statusText;

        private Color _activeTabColor = new Color(0.21f, 0.42f, 0.66f, 1f);
        private Color _inactiveTabColor = new Color(0.15f, 0.17f, 0.21f, 1f);

        public void Initialize(Canvas canvas)
        {
            _canvas = canvas;
            EnsureUI();
            WireButtonEvents();
            SetActiveTab(BrowserTab.Sell);
            SetRuntimeSummary(0, 0);
            SetStatusMessage(string.Empty);
        }

        public void Show()
        {
            if (_canvas == null)
            {
                return;
            }

            EnsureUI();
            SetActiveTab(BrowserTab.Sell);
            _rootPanel.gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (_rootPanel != null)
            {
                _rootPanel.gameObject.SetActive(false);
            }
        }

        public void SetRuntimeSummary(int walletAmount, int trackedLootCount)
        {
            if (_walletText != null)
            {
                _walletText.text = $"Wallet: ${Mathf.Max(0, walletAmount)}";
            }

            if (_sellSummaryText != null)
            {
                _sellSummaryText.text = $"Tracked stolen items: {Mathf.Max(0, trackedLootCount)}";
            }
        }

        public void SetStatusMessage(string message)
        {
            if (_statusText == null)
            {
                return;
            }

            _statusText.text = string.IsNullOrWhiteSpace(message)
                ? "Ready."
                : message.Trim();
        }

        private void EnsureUI()
        {
            if (_initialized)
            {
                return;
            }

            if (_canvas == null)
            {
                _canvas = GetComponent<Canvas>();
            }

            if (_canvas == null)
            {
                Debug.LogError("[ComputerMinigameUI] Missing canvas reference.", this);
                return;
            }

            RectTransform canvasRect = _canvas.GetComponent<RectTransform>();
            if (canvasRect == null)
            {
                Debug.LogError("[ComputerMinigameUI] Canvas is missing RectTransform.", this);
                return;
            }

            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = Mathf.Max(_canvas.sortingOrder, 550);

            _rootPanel = CreatePanel("ComputerBrowserRoot", canvasRect, new Vector2(1020f, 620f), new Color(0.08f, 0.09f, 0.11f, 0.96f));
            _rootPanel.anchorMin = new Vector2(0.5f, 0.5f);
            _rootPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _rootPanel.pivot = new Vector2(0.5f, 0.5f);
            _rootPanel.anchoredPosition = Vector2.zero;

            RectTransform topBar = CreatePanel("TopBar", _rootPanel, new Vector2(980f, 64f), new Color(0.11f, 0.12f, 0.16f, 1f));
            topBar.anchorMin = new Vector2(0.5f, 1f);
            topBar.anchorMax = new Vector2(0.5f, 1f);
            topBar.pivot = new Vector2(0.5f, 1f);
            topBar.anchoredPosition = new Vector2(0f, -16f);

            _sellTabButton = CreateButton(topBar, "SellTabButton", new Vector2(150f, 40f), new Vector2(-300f, -12f), "Sell");
            _upgradeTabButton = CreateButton(topBar, "UpgradeTabButton", new Vector2(150f, 40f), new Vector2(-130f, -12f), "Upgrade");
            _closeButton = CreateButton(topBar, "CloseButton", new Vector2(56f, 40f), new Vector2(450f, -12f), "X");

            _sellPanel = CreatePanel("SellPanel", _rootPanel, new Vector2(980f, 500f), new Color(0.12f, 0.13f, 0.17f, 0.95f));
            _sellPanel.anchorMin = new Vector2(0.5f, 0.5f);
            _sellPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _sellPanel.pivot = new Vector2(0.5f, 0.5f);
            _sellPanel.anchoredPosition = new Vector2(0f, -38f);

            CreateLabel(_sellPanel, "SellTitle", new Vector2(520f, 52f), new Vector2(-210f, 188f), "Sell Tab", 34f, FontStyles.Bold, TextAlignmentOptions.Left);
            _sellSummaryText = CreateLabel(_sellPanel, "SellSummary", new Vector2(700f, 46f), new Vector2(-120f, 115f), string.Empty, 26f, FontStyles.Normal, TextAlignmentOptions.Left);
            _sellActionButton = CreateButton(_sellPanel, "SellActionButton", new Vector2(360f, 64f), new Vector2(-280f, 20f), "Sell Tracked Loot");

            _upgradePanel = CreatePanel("UpgradePanel", _rootPanel, new Vector2(980f, 500f), new Color(0.12f, 0.13f, 0.17f, 0.95f));
            _upgradePanel.anchorMin = new Vector2(0.5f, 0.5f);
            _upgradePanel.anchorMax = new Vector2(0.5f, 0.5f);
            _upgradePanel.pivot = new Vector2(0.5f, 0.5f);
            _upgradePanel.anchoredPosition = new Vector2(0f, -38f);

            CreateLabel(_upgradePanel, "UpgradeTitle", new Vector2(640f, 52f), new Vector2(-150f, 188f), "Upgrade Tab", 34f, FontStyles.Bold, TextAlignmentOptions.Left);
            _walletText = CreateLabel(_upgradePanel, "WalletText", new Vector2(420f, 46f), new Vector2(280f, 188f), string.Empty, 24f, FontStyles.Normal, TextAlignmentOptions.Right);

            _upgradeInventoryButton = CreateButton(_upgradePanel, "UpgradeInventoryButton", new Vector2(420f, 58f), new Vector2(-240f, 110f), "Upgrade Inventory Slots");
            _upgradeCleaningButton = CreateButton(_upgradePanel, "UpgradeCleaningButton", new Vector2(420f, 58f), new Vector2(-240f, 34f), "Upgrade Cleaning Tool");
            _upgradeWeldingButton = CreateButton(_upgradePanel, "UpgradeWeldingButton", new Vector2(420f, 58f), new Vector2(-240f, -42f), "Upgrade Welding Tool");

            _statusText = CreateLabel(_rootPanel, "StatusText", new Vector2(940f, 56f), new Vector2(0f, -278f), string.Empty, 22f, FontStyles.Normal, TextAlignmentOptions.Left);

            _initialized = true;
        }

        private void WireButtonEvents()
        {
            if (!_initialized)
            {
                return;
            }

            WireButton(_sellTabButton, () => SetActiveTab(BrowserTab.Sell));
            WireButton(_upgradeTabButton, () => SetActiveTab(BrowserTab.Upgrade));
            WireButton(_closeButton, () => CloseRequested?.Invoke());

            WireButton(_sellActionButton, () => SellRequested?.Invoke());
            WireButton(_upgradeInventoryButton, () => UpgradeRequested?.Invoke(UpgradeInventoryId));
            WireButton(_upgradeCleaningButton, () => UpgradeRequested?.Invoke(UpgradeCleaningId));
            WireButton(_upgradeWeldingButton, () => UpgradeRequested?.Invoke(UpgradeWeldingId));
        }

        private static void WireButton(Button button, Action callback)
        {
            if (button == null)
            {
                return;
            }

            button.onClick.RemoveAllListeners();
            if (callback != null)
            {
                button.onClick.AddListener(() => callback.Invoke());
            }
        }

        private void SetActiveTab(BrowserTab tab)
        {
            if (!_initialized)
            {
                return;
            }

            bool sellActive = tab == BrowserTab.Sell;
            bool upgradeActive = tab == BrowserTab.Upgrade;

            if (_sellPanel != null)
            {
                _sellPanel.gameObject.SetActive(sellActive);
            }

            if (_upgradePanel != null)
            {
                _upgradePanel.gameObject.SetActive(upgradeActive);
            }

            SetTabColor(_sellTabButton, sellActive ? _activeTabColor : _inactiveTabColor);
            SetTabColor(_upgradeTabButton, upgradeActive ? _activeTabColor : _inactiveTabColor);
        }

        private static RectTransform CreatePanel(string name, Transform parent, Vector2 size, Color background)
        {
            GameObject panelObject = new GameObject(name, typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(parent, false);

            RectTransform rect = panelObject.GetComponent<RectTransform>();
            rect.sizeDelta = size;

            Image image = panelObject.GetComponent<Image>();
            image.color = background;
            image.raycastTarget = true;

            return rect;
        }

        private static TextMeshProUGUI CreateLabel(
            RectTransform parent,
            string name,
            Vector2 size,
            Vector2 anchoredPosition,
            string text,
            float fontSize,
            FontStyles fontStyle,
            TextAlignmentOptions alignment)
        {
            GameObject labelObject = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(parent, false);

            RectTransform rect = labelObject.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;

            TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.fontStyle = fontStyle;
            label.color = Color.white;
            label.alignment = alignment;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.Normal;
            return label;
        }

        private static Button CreateButton(RectTransform parent, string name, Vector2 size, Vector2 anchoredPosition, string text)
        {
            GameObject buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.sizeDelta = size;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;

            Image image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.2f, 0.23f, 0.29f, 1f);

            Button button = buttonObject.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = image.color;
            colors.highlightedColor = new Color(0.28f, 0.34f, 0.45f, 1f);
            colors.pressedColor = new Color(0.16f, 0.2f, 0.28f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;

            CreateLabel(rect, "Label", new Vector2(size.x - 12f, size.y - 8f), Vector2.zero, text, 22f, FontStyles.Bold, TextAlignmentOptions.Center);
            return button;
        }

        private static void SetTabColor(Button button, Color color)
        {
            if (button == null)
            {
                return;
            }

            Image image = button.GetComponent<Image>();
            if (image == null)
            {
                return;
            }

            image.color = color;
            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.12f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.2f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;
        }
    }
}
