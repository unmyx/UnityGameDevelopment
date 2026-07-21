using System;
using System.Collections.Generic;
using Game.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Minigames
{
    /// <summary>
    /// MVP UI for the Home computer station.
    /// </summary>
    public class ComputerMinigameUI : MonoBehaviour
    {
        public event Action CloseRequested;
        public event Action<string> SellRequested;
        public event Action<string> UpgradeRequested;

        private enum BrowserTab
        {
            Sell,
            Upgrade
        }

        private sealed class SellGridCell
        {
            public Button Button;
            public Image Background;
            public Image Icon;
            public TextMeshProUGUI NameText;
            public TextMeshProUGUI CountText;
            public string ItemId;
        }

        private sealed class UpgradeRow
        {
            public string UpgradeId;
            public TextMeshProUGUI TitleText;
            public TextMeshProUGUI DetailText;
            public Button ActionButton;
            public TextMeshProUGUI ActionLabel;
        }

        private const int SellGridColumns = 5;
        private const int SellGridRows = 2;
        private const int SellGridCellCount = SellGridColumns * SellGridRows;

        private const string UpgradeInventoryId = GameManager.UpgradeIdInventoryQuickSlots;
        private const string UpgradeCleaningId = GameManager.UpgradeIdCleaningTool;
        private const string UpgradeWeldingId = GameManager.UpgradeIdWeldingTool;

        private readonly List<SellGridCell> _sellGridCells = new List<SellGridCell>(SellGridCellCount);
        private readonly List<GameManager.SellableStolenLootEntryData> _sellEntries = new List<GameManager.SellableStolenLootEntryData>(SellGridCellCount);
        private readonly Dictionary<string, UpgradeRow> _upgradeRowsById = new Dictionary<string, UpgradeRow>(StringComparer.Ordinal);

        private Canvas _canvas;
        private bool _initialized;

        private RectTransform _rootPanel;
        private RectTransform _sellPanel;
        private RectTransform _upgradePanel;
        private RectTransform _sellGridRoot;

        private Button _sellTabButton;
        private Button _upgradeTabButton;
        private Button _closeButton;
        private Button _sellActionButton;

        private TextMeshProUGUI _sellSummaryText;
        private TextMeshProUGUI _walletText;
        private TextMeshProUGUI _statusText;
        private TextMeshProUGUI _selectedPriceText;
        private TextMeshProUGUI _selectedDescriptionText;

        private string _selectedSellItemId = string.Empty;

        private readonly Color _activeTabColor = new Color(0.23f, 0.45f, 0.72f, 1f);
        private readonly Color _inactiveTabColor = new Color(0.15f, 0.17f, 0.21f, 1f);
        private readonly Color _slotEmptyColor = new Color(0.16f, 0.18f, 0.22f, 0.95f);
        private readonly Color _slotFilledColor = new Color(0.2f, 0.24f, 0.31f, 1f);
        private readonly Color _slotSelectedColor = new Color(0.37f, 0.47f, 0.16f, 1f);
        private readonly Color _slotSelectedOutlineColor = new Color(0.95f, 0.84f, 0.35f, 1f);

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
            if (_rootPanel != null)
            {
                _rootPanel.gameObject.SetActive(true);
            }
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
                _sellSummaryText.text = $"Sellable stolen items: {Mathf.Max(0, trackedLootCount)}";
            }
        }

        public void SetSellEntries(List<GameManager.SellableStolenLootEntryData> entries)
        {
            int previousSelectedIndex = -1;
            if (!string.IsNullOrEmpty(_selectedSellItemId))
            {
                for (int i = 0; i < _sellEntries.Count; i++)
                {
                    GameManager.SellableStolenLootEntryData existingEntry = _sellEntries[i];
                    if (existingEntry == null)
                    {
                        continue;
                    }

                    if (string.Equals(existingEntry.itemId, _selectedSellItemId, StringComparison.Ordinal))
                    {
                        previousSelectedIndex = i;
                        break;
                    }
                }
            }

            _sellEntries.Clear();
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    GameManager.SellableStolenLootEntryData entry = entries[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.itemId) || entry.sellableCount <= 0)
                    {
                        continue;
                    }

                    _sellEntries.Add(entry);
                }
            }

            if (_sellEntries.Count <= 0)
            {
                _selectedSellItemId = string.Empty;
            }
            else if (string.IsNullOrEmpty(_selectedSellItemId) || FindSellEntryByItemId(_selectedSellItemId) == null)
            {
                if (previousSelectedIndex >= 0)
                {
                    int nextIndex = Mathf.Clamp(previousSelectedIndex, 0, _sellEntries.Count - 1);
                    _selectedSellItemId = _sellEntries[nextIndex].itemId;
                }
                else
                {
                    _selectedSellItemId = _sellEntries[0].itemId;
                }
            }

            RefreshSellGridCells();
            RefreshSelectedSellDetails();
        }

        public void SetUpgradeEntries(List<GameManager.HomeUpgradeStatusData> statuses)
        {
            if (statuses == null)
            {
                return;
            }

            for (int i = 0; i < statuses.Count; i++)
            {
                GameManager.HomeUpgradeStatusData status = statuses[i];
                if (status == null || string.IsNullOrWhiteSpace(status.upgradeId))
                {
                    continue;
                }

                if (!_upgradeRowsById.TryGetValue(status.upgradeId, out UpgradeRow row) || row == null)
                {
                    continue;
                }

                if (row.TitleText != null)
                {
                    row.TitleText.text = status.displayName;
                }

                if (row.DetailText != null)
                {
                    if (!string.IsNullOrWhiteSpace(status.detailTextOverride))
                    {
                        row.DetailText.text = status.detailTextOverride.Trim();
                    }
                    else
                    {
                        string costText = status.currentTier >= status.maxTier
                            ? "Cost: MAX"
                            : $"Cost: ${Mathf.Max(0, status.nextTierCost)}";
                        string reasonText = string.IsNullOrWhiteSpace(status.unavailableReason)
                            ? string.Empty
                            : $"  |  {status.unavailableReason}";
                        row.DetailText.text = $"Tier: {status.currentTier}/{status.maxTier}  |  {costText}{reasonText}";
                    }
                }

                if (row.ActionButton != null)
                {
                    row.ActionButton.interactable = status.canPurchase;
                }

                if (row.ActionLabel != null)
                {
                    if (!string.IsNullOrWhiteSpace(status.actionLabelOverride))
                    {
                        row.ActionLabel.text = status.actionLabelOverride.Trim();
                    }
                    else if (status.currentTier >= status.maxTier)
                    {
                        row.ActionLabel.text = "Maxed";
                    }
                    else if (status.canPurchase)
                    {
                        row.ActionLabel.text = "Buy";
                    }
                    else
                    {
                        row.ActionLabel.text = "Unavailable";
                    }
                }
            }
        }

        public void SetStatusMessage(string message)
        {
            if (_statusText == null)
            {
                return;
            }

            _statusText.text = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
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

            _rootPanel = CreatePanel("ComputerRoot", canvasRect, new Vector2(1060f, 670f), new Color(0.07f, 0.08f, 0.1f, 0.97f));
            _rootPanel.anchorMin = new Vector2(0.5f, 0.5f);
            _rootPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _rootPanel.pivot = new Vector2(0.5f, 0.5f);
            _rootPanel.anchoredPosition = Vector2.zero;

            RectTransform topBar = CreatePanel("TopBar", _rootPanel, new Vector2(1012f, 62f), new Color(0.11f, 0.12f, 0.16f, 1f));
            topBar.anchorMin = new Vector2(0.5f, 1f);
            topBar.anchorMax = new Vector2(0.5f, 1f);
            topBar.pivot = new Vector2(0.5f, 1f);
            topBar.anchoredPosition = new Vector2(0f, -14f);

            _sellTabButton = CreateButton(topBar, "SellTabButton", new Vector2(160f, 40f), new Vector2(-316f, -10f), "Sell");
            _upgradeTabButton = CreateButton(topBar, "UpgradeTabButton", new Vector2(160f, 40f), new Vector2(-136f, -10f), "Upgrades");
            _walletText = CreateLabel(topBar, "WalletText", new Vector2(280f, 40f), new Vector2(230f, -10f), string.Empty, 23f, FontStyles.Bold, TextAlignmentOptions.Right);
            _closeButton = CreateButton(topBar, "CloseButton", new Vector2(56f, 40f), new Vector2(470f, -10f), "X");

            _sellPanel = CreatePanel("SellPanel", _rootPanel, new Vector2(1012f, 540f), new Color(0.11f, 0.13f, 0.18f, 0.98f));
            _sellPanel.anchorMin = new Vector2(0.5f, 0.5f);
            _sellPanel.anchorMax = new Vector2(0.5f, 0.5f);
            _sellPanel.pivot = new Vector2(0.5f, 0.5f);
            _sellPanel.anchoredPosition = new Vector2(0f, -20f);

            CreateLabel(_sellPanel, "SellTitle", new Vector2(260f, 44f), new Vector2(-372f, 230f), "Sell", 31f, FontStyles.Bold, TextAlignmentOptions.Left);
            _sellSummaryText = CreateLabel(_sellPanel, "SellSummary", new Vector2(520f, 34f), new Vector2(-208f, 195f), string.Empty, 22f, FontStyles.Normal, TextAlignmentOptions.Left);

            _sellGridRoot = CreatePanel("SellGridRoot", _sellPanel, new Vector2(840f, 270f), new Color(0.09f, 0.1f, 0.13f, 0.9f));
            _sellGridRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _sellGridRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _sellGridRoot.pivot = new Vector2(0.5f, 0.5f);
            _sellGridRoot.anchoredPosition = new Vector2(0f, 64f);
            GridLayoutGroup gridLayout = _sellGridRoot.gameObject.AddComponent<GridLayoutGroup>();
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = SellGridColumns;
            gridLayout.cellSize = new Vector2(156f, 112f);
            gridLayout.spacing = new Vector2(12f, 12f);
            gridLayout.padding = new RectOffset(14, 14, 14, 14);
            gridLayout.childAlignment = TextAnchor.MiddleCenter;

            for (int i = 0; i < SellGridCellCount; i++)
            {
                _sellGridCells.Add(CreateSellGridCell(_sellGridRoot, i));
            }

            RectTransform detailsRoot = CreatePanel("SellDetailsRoot", _sellPanel, new Vector2(840f, 176f), new Color(0.1f, 0.11f, 0.15f, 0.95f));
            detailsRoot.anchorMin = new Vector2(0.5f, 0.5f);
            detailsRoot.anchorMax = new Vector2(0.5f, 0.5f);
            detailsRoot.pivot = new Vector2(0.5f, 0.5f);
            detailsRoot.anchoredPosition = new Vector2(0f, -178f);

            _selectedPriceText = CreateLabel(detailsRoot, "SelectedPriceText", new Vector2(420f, 34f), new Vector2(-196f, 52f), "Price: --", 24f, FontStyles.Bold, TextAlignmentOptions.Left);
            _selectedDescriptionText = CreateLabel(detailsRoot, "SelectedDescriptionText", new Vector2(780f, 70f), new Vector2(0f, 8f), "Select an item to sell.", 21f, FontStyles.Normal, TextAlignmentOptions.Left);
            _sellActionButton = CreateButton(detailsRoot, "SellActionButton", new Vector2(260f, 54f), new Vector2(272f, 52f), "Sell 1");

            _upgradePanel = CreatePanel("UpgradePanel", _rootPanel, new Vector2(1012f, 540f), new Color(0.11f, 0.13f, 0.18f, 0.98f));
            _upgradePanel.anchorMin = new Vector2(0.5f, 0.5f);
            _upgradePanel.anchorMax = new Vector2(0.5f, 0.5f);
            _upgradePanel.pivot = new Vector2(0.5f, 0.5f);
            _upgradePanel.anchoredPosition = new Vector2(0f, -20f);

            CreateLabel(_upgradePanel, "UpgradeTitle", new Vector2(420f, 44f), new Vector2(-294f, 230f), "Upgrades", 31f, FontStyles.Bold, TextAlignmentOptions.Left);

            CreateUpgradeRow(_upgradePanel, UpgradeInventoryId, "Inventory Slots", new Vector2(0f, 110f));
            CreateUpgradeRow(_upgradePanel, UpgradeCleaningId, "Cleaning Tool", new Vector2(0f, 16f));
            CreateUpgradeRow(_upgradePanel, UpgradeWeldingId, "Welding Tool", new Vector2(0f, -78f));

            _statusText = CreateLabel(_rootPanel, "StatusText", new Vector2(980f, 34f), new Vector2(0f, -314f), string.Empty, 20f, FontStyles.Normal, TextAlignmentOptions.Left);
            _statusText.color = new Color(0.88f, 0.9f, 0.95f, 0.95f);

            _initialized = true;
            _rootPanel.gameObject.SetActive(false);
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
            WireButton(_sellActionButton, HandleSellButtonPressed);

            foreach (KeyValuePair<string, UpgradeRow> pair in _upgradeRowsById)
            {
                UpgradeRow row = pair.Value;
                if (row == null)
                {
                    continue;
                }

                string upgradeId = row.UpgradeId;
                WireButton(row.ActionButton, () => UpgradeRequested?.Invoke(upgradeId));
            }
        }

        private void HandleSellButtonPressed()
        {
            if (string.IsNullOrEmpty(_selectedSellItemId))
            {
                return;
            }

            SellRequested?.Invoke(_selectedSellItemId);
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

        private UpgradeRow CreateUpgradeRow(RectTransform parent, string upgradeId, string fallbackTitle, Vector2 anchoredPosition)
        {
            RectTransform rowRoot = CreatePanel($"UpgradeRow_{upgradeId}", parent, new Vector2(900f, 84f), new Color(0.1f, 0.11f, 0.15f, 0.95f));
            rowRoot.anchorMin = new Vector2(0.5f, 0.5f);
            rowRoot.anchorMax = new Vector2(0.5f, 0.5f);
            rowRoot.pivot = new Vector2(0.5f, 0.5f);
            rowRoot.anchoredPosition = anchoredPosition;

            UpgradeRow row = new UpgradeRow
            {
                UpgradeId = upgradeId,
                TitleText = CreateLabel(rowRoot, "Title", new Vector2(360f, 32f), new Vector2(-230f, 18f), fallbackTitle, 24f, FontStyles.Bold, TextAlignmentOptions.Left),
                DetailText = CreateLabel(rowRoot, "Detail", new Vector2(620f, 30f), new Vector2(-98f, -16f), string.Empty, 19f, FontStyles.Normal, TextAlignmentOptions.Left),
                ActionButton = CreateButton(rowRoot, "ActionButton", new Vector2(152f, 48f), new Vector2(348f, 0f), "Buy")
            };

            row.ActionLabel = row.ActionButton.GetComponentInChildren<TextMeshProUGUI>(true);
            _upgradeRowsById[upgradeId] = row;
            return row;
        }

        private SellGridCell CreateSellGridCell(RectTransform parent, int index)
        {
            GameObject buttonObject = new GameObject($"SellGridCell_{index}", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            buttonObject.transform.SetParent(parent, false);

            RectTransform cellRect = buttonObject.GetComponent<RectTransform>();
            cellRect.localScale = Vector3.one;

            Image background = buttonObject.GetComponent<Image>();
            background.color = _slotEmptyColor;

            Outline outline = buttonObject.GetComponent<Outline>();
            outline.effectDistance = new Vector2(2f, -2f);
            outline.effectColor = new Color(0f, 0f, 0f, 0.45f);
            outline.useGraphicAlpha = true;

            Button cellButton = buttonObject.GetComponent<Button>();
            ColorBlock colors = cellButton.colors;
            colors.normalColor = background.color;
            colors.highlightedColor = Color.Lerp(background.color, Color.white, 0.08f);
            colors.pressedColor = Color.Lerp(background.color, Color.black, 0.2f);
            colors.selectedColor = colors.highlightedColor;
            cellButton.colors = colors;

            RectTransform iconRect = new GameObject("Icon", typeof(RectTransform), typeof(Image)).GetComponent<RectTransform>();
            iconRect.SetParent(cellRect, false);
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(10f, 0f);
            iconRect.sizeDelta = new Vector2(58f, 58f);
            Image iconImage = iconRect.GetComponent<Image>();
            iconImage.preserveAspect = true;
            iconImage.color = new Color(1f, 1f, 1f, 0f);
            iconImage.raycastTarget = false;

            TextMeshProUGUI nameText = CreateLabel(cellRect, "Name", new Vector2(78f, 42f), new Vector2(30f, 12f), string.Empty, 16f, FontStyles.Bold, TextAlignmentOptions.TopLeft);
            nameText.overflowMode = TextOverflowModes.Ellipsis;

            TextMeshProUGUI countText = CreateLabel(cellRect, "Count", new Vector2(78f, 28f), new Vector2(30f, -32f), string.Empty, 16f, FontStyles.Normal, TextAlignmentOptions.TopLeft);
            countText.color = new Color(0.88f, 0.9f, 0.95f, 0.95f);

            SellGridCell cell = new SellGridCell
            {
                Button = cellButton,
                Background = background,
                Icon = iconImage,
                NameText = nameText,
                CountText = countText,
                ItemId = string.Empty
            };

            WireButton(cellButton, () => HandleSellCellSelected(cell));
            return cell;
        }

        private void HandleSellCellSelected(SellGridCell cell)
        {
            if (cell == null || string.IsNullOrEmpty(cell.ItemId))
            {
                return;
            }

            _selectedSellItemId = cell.ItemId;
            RefreshSellGridCells();
            RefreshSelectedSellDetails();
        }

        private void RefreshSellGridCells()
        {
            for (int i = 0; i < _sellGridCells.Count; i++)
            {
                SellGridCell cell = _sellGridCells[i];
                if (cell == null)
                {
                    continue;
                }

                GameManager.SellableStolenLootEntryData entry = i < _sellEntries.Count ? _sellEntries[i] : null;
                bool hasEntry = entry != null && !string.IsNullOrWhiteSpace(entry.itemId) && entry.sellableCount > 0;
                cell.ItemId = hasEntry ? entry.itemId : string.Empty;

                if (cell.Button != null)
                {
                    cell.Button.interactable = hasEntry;
                }

                if (cell.Icon != null)
                {
                    cell.Icon.sprite = hasEntry ? entry.itemIcon : null;
                    cell.Icon.color = hasEntry && entry.itemIcon != null
                        ? Color.white
                        : new Color(1f, 1f, 1f, 0f);
                }

                if (cell.NameText != null)
                {
                    cell.NameText.text = hasEntry ? entry.itemName : string.Empty;
                }

                if (cell.CountText != null)
                {
                    cell.CountText.text = hasEntry ? $"x{entry.sellableCount}" : string.Empty;
                }

                bool isSelected = hasEntry && string.Equals(cell.ItemId, _selectedSellItemId, StringComparison.Ordinal);
                if (cell.Background != null)
                {
                    cell.Background.color = isSelected
                        ? _slotSelectedColor
                        : hasEntry
                            ? _slotFilledColor
                            : _slotEmptyColor;
                }

                Outline outline = cell.Button != null ? cell.Button.GetComponent<Outline>() : null;
                if (outline != null)
                {
                    outline.effectColor = isSelected
                        ? _slotSelectedOutlineColor
                        : new Color(0f, 0f, 0f, 0.45f);
                }
            }
        }

        private void RefreshSelectedSellDetails()
        {
            GameManager.SellableStolenLootEntryData selectedEntry = FindSellEntryByItemId(_selectedSellItemId);
            if (selectedEntry == null)
            {
                _selectedSellItemId = string.Empty;
                if (_selectedPriceText != null)
                {
                    _selectedPriceText.text = "Price: --";
                }

                if (_selectedDescriptionText != null)
                {
                    _selectedDescriptionText.text = "No sellable stolen items.";
                }

                if (_sellActionButton != null)
                {
                    _sellActionButton.interactable = false;
                }

                return;
            }

            if (_selectedPriceText != null)
            {
                _selectedPriceText.text = $"Price: ${Mathf.Max(0, selectedEntry.unitPrice)}";
            }

            if (_selectedDescriptionText != null)
            {
                string description = string.IsNullOrWhiteSpace(selectedEntry.itemDescription)
                    ? "No description."
                    : selectedEntry.itemDescription.Trim();
                _selectedDescriptionText.text = $"{description}  (Available: {selectedEntry.sellableCount})";
            }

            if (_sellActionButton != null)
            {
                _sellActionButton.interactable = selectedEntry.sellableCount > 0;
            }
        }

        private GameManager.SellableStolenLootEntryData FindSellEntryByItemId(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return null;
            }

            for (int i = 0; i < _sellEntries.Count; i++)
            {
                GameManager.SellableStolenLootEntryData entry = _sellEntries[i];
                if (entry == null)
                {
                    continue;
                }

                if (string.Equals(entry.itemId, itemId, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
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

            CreateLabel(rect, "Label", new Vector2(size.x - 12f, size.y - 8f), Vector2.zero, text, 20f, FontStyles.Bold, TextAlignmentOptions.Center);
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
