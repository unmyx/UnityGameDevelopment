using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Inventory;
using Game.Minigames;
using Game.Player;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class InventoryQuickSlotTests
{
    private static readonly string[] ItemResourcePaths =
    {
        "Items/ToolWater",
        "Items/ToolGasoline",
        "Items/ToolChemical",
        "Items/ToolElectric",
        "Items/ToolCO2",
        "Items/WeddingRingGold",
        "Items/WeddingRingSilver"
    };

    private GameObject _inventoryObject;
    private InventorySystem _inventory;
    private readonly List<GameObject> _uiObjects = new List<GameObject>();

    [SetUp]
    public void SetUp()
    {
        ResetInventorySingleton();
        _inventoryObject = new GameObject("InventoryQuickSlotTests.Inventory");
        _inventory = _inventoryObject.AddComponent<InventorySystem>();
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = 0; i < _uiObjects.Count; i++)
        {
            if (_uiObjects[i] != null)
            {
                Object.DestroyImmediate(_uiObjects[i]);
            }
        }

        _uiObjects.Clear();
        if (_inventoryObject != null)
        {
            Object.DestroyImmediate(_inventoryObject);
        }

        ResetInventorySingleton();
    }

    [Test]
    public void MaximumQuickSlotCount_IsNine()
    {
        Assert.That(InventoryQuickSlotRules.MaxQuickSlots, Is.EqualTo(9));
    }

    [Test]
    public void NewInventory_HasNineAvailableQuickSlots()
    {
        Assert.That(_inventory.GetAvailableSlots(), Is.EqualTo(9));
        Assert.That(_inventory.IsInventoryFull(), Is.False);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    public void QuickSlotIndicesZeroThroughEight_AreValid(int quickSlotIndex)
    {
        Assert.That(InventoryQuickSlotRules.IsValidIndex(quickSlotIndex), Is.True);
        Assert.That(TryGetGridPosition(quickSlotIndex, out _, out _), Is.True);
    }

    [TestCase(-1, 0)]
    [TestCase(9, 8)]
    [TestCase(int.MaxValue, 8)]
    public void InvalidSelectedIndex_IsSafelyClamped(int requestedIndex, int expectedIndex)
    {
        PlayerContext context = new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);

        context.SetSelectedQuickSlotIndex(requestedIndex);

        Assert.That(context.SelectedQuickSlotIndex, Is.EqualTo(expectedIndex));
    }

    [Test]
    public void SelectingEmptySlotNine_IsSafeAndReturnsNoTool()
    {
        PlayerContext context = new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);
        context.SetSelectedQuickSlotIndex(8);

        Assert.DoesNotThrow(() => context.TryGetSelectedQuickSlotItem(out _));
        Assert.That(context.TryGetSelectedQuickSlotItem(out InventoryItem selectedItem), Is.False);
        Assert.That(selectedItem, Is.Null);
        Assert.That(context.TryGetSelectedTool(out ToolType toolType), Is.False);
        Assert.That(toolType, Is.EqualTo(ToolType.None));
    }

    [Test]
    public void UserSlotNumbersOneThroughNine_MapToMatchingZeroBasedIndex()
    {
        for (int userSlotNumber = 1; userSlotNumber <= InventoryQuickSlotRules.MaxQuickSlots; userSlotNumber++)
        {
            Assert.That(
                InventoryQuickSlotRules.TryGetIndexForUserSlotNumber(userSlotNumber, out int quickSlotIndex),
                Is.True);
            Assert.That(quickSlotIndex, Is.EqualTo(userSlotNumber - 1));
        }
    }

    [Test]
    public void InputActionAsset_ContainsKeyboardBindingsForSlotsOneThroughNine()
    {
        InputActionAsset inputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(
            "Assets/InputSystem_Actions.inputactions");
        Assert.That(inputActions, Is.Not.Null);
        InputActionMap playerMap = inputActions.FindActionMap("Player");
        Assert.That(playerMap, Is.Not.Null);

        for (int userSlotNumber = 1; userSlotNumber <= InventoryQuickSlotRules.MaxQuickSlots; userSlotNumber++)
        {
            InputAction action = playerMap.FindAction($"SelectSlot{userSlotNumber}");
            Assert.That(action, Is.Not.Null);
            Assert.That(action.bindings, Has.Some.Matches<InputBinding>(
                binding => binding.path == $"<Keyboard>/{userSlotNumber}"));
        }
    }

    [TestCase(5)]
    [TestCase(8)]
    public void ItemCanBeAddedToHighQuickSlot(int quickSlotIndex)
    {
        InventoryItem item = LoadItem(quickSlotIndex);
        Assert.That(TryGetGridPosition(quickSlotIndex, out int gridX, out int gridY), Is.True);

        Assert.That(_inventory.AddItemAt(item, gridX, gridY), Is.True);
        Assert.That(_inventory.TryGetQuickSlotItem(quickSlotIndex, out InventoryItem storedItem), Is.True);
        Assert.That(storedItem, Is.SameAs(item));
    }

    [TestCase(5)]
    [TestCase(8)]
    public void ItemInHighQuickSlot_CanBeSelected(int quickSlotIndex)
    {
        InventoryItem item = LoadItem(quickSlotIndex);
        AddAtQuickSlot(item, quickSlotIndex);
        PlayerContext context = new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);

        context.SetSelectedQuickSlotIndex(quickSlotIndex);

        Assert.That(context.TryGetSelectedQuickSlotItem(out InventoryItem selectedItem), Is.True);
        Assert.That(selectedItem, Is.SameAs(item));
    }

    [TestCase(5)]
    [TestCase(8)]
    public void ItemInHighQuickSlot_CanBeRemoved(int quickSlotIndex)
    {
        InventoryItem item = LoadItem(quickSlotIndex);
        AddAtQuickSlot(item, quickSlotIndex);
        TryGetGridPosition(quickSlotIndex, out int gridX, out int gridY);

        InventoryItem removed = _inventory.RemoveItemAt(gridX, gridY);

        Assert.That(removed, Is.SameAs(item));
        Assert.That(_inventory.TryGetQuickSlotItem(quickSlotIndex, out _), Is.False);
    }

    [Test]
    public void NineFilledQuickSlots_ReportNoRemainingSpace()
    {
        FillAllQuickSlots();

        Assert.That(_inventory.GetItemCount(), Is.EqualTo(9));
        Assert.That(_inventory.GetAvailableSlots(), Is.Zero);
        Assert.That(_inventory.HasSpace(), Is.False);
        Assert.That(_inventory.IsInventoryFull(), Is.True);
        Assert.That(_inventory.AddItem(LoadItem(0)), Is.False);
    }

    [Test]
    public void PickupUsesFirstActuallyEmptySlotAcrossAllNine()
    {
        AddAtQuickSlot(LoadItem(0), 0);
        AddAtQuickSlot(LoadItem(2), 2);

        InventoryItem addedItem = LoadItem(1);
        Assert.That(_inventory.AddItem(addedItem), Is.True);

        Assert.That(_inventory.TryGetQuickSlotItem(1, out InventoryItem storedItem), Is.True);
        Assert.That(storedItem, Is.SameAs(addedItem));
    }

    [TestCase(3)]
    [TestCase(5)]
    public void LegacySparseSave_RestoresExistingItemsAndLeavesNineSlotCapacity(int itemCount)
    {
        List<InventorySlotData> saveData = CreateSaveEntries(itemCount);

        _inventory.RestoreFromSave(saveData);

        Assert.That(_inventory.GetItemCount(), Is.EqualTo(itemCount));
        Assert.That(_inventory.GetAvailableSlots(), Is.EqualTo(9 - itemCount));
        for (int i = 0; i < itemCount; i++)
        {
            Assert.That(_inventory.TryGetQuickSlotItem(i, out InventoryItem item), Is.True);
            Assert.That(item.ItemId, Is.EqualTo(saveData[i].itemId));
        }

        for (int i = itemCount; i < InventoryQuickSlotRules.MaxQuickSlots; i++)
        {
            Assert.That(_inventory.TryGetQuickSlotItem(i, out _), Is.False);
        }
    }

    [Test]
    public void NineSlotSave_RestoresWithoutMovingOrDuplicatingItems()
    {
        List<InventorySlotData> saveData = CreateSaveEntries(9);

        _inventory.RestoreFromSave(saveData);
        InventorySlotData[] snapshot = _inventory.GetInventorySnapshot();

        Assert.That(snapshot.Length, Is.EqualTo(9));
        for (int i = 0; i < snapshot.Length; i++)
        {
            Assert.That(snapshot[i].gridX, Is.EqualTo(saveData[i].gridX));
            Assert.That(snapshot[i].gridY, Is.EqualTo(saveData[i].gridY));
            Assert.That(snapshot[i].itemId, Is.EqualTo(saveData[i].itemId));
        }
    }

    [Test]
    public void SaveNormalization_IsIdempotent()
    {
        List<InventorySlotData> saveData = CreateSaveEntries(5);
        _inventory.RestoreFromSave(saveData);
        InventorySlotData[] firstSnapshot = _inventory.GetInventorySnapshot();

        _inventory.RestoreFromSave(saveData);
        InventorySlotData[] secondSnapshot = _inventory.GetInventorySnapshot();

        AssertSnapshotsEqual(firstSnapshot, secondSnapshot);
    }

    [Test]
    public void OversizedSaveList_IsSafelyLimitedToNineQuickSlots()
    {
        List<InventorySlotData> saveData = CreateSaveEntries(9);
        saveData.Add(new InventorySlotData(4, 1, LoadItem(0).ItemId));

        Assert.DoesNotThrow(() => _inventory.RestoreFromSave(saveData));
        Assert.That(_inventory.GetItemCount(), Is.EqualTo(9));
        Assert.That(_inventory.GetInventorySnapshot().Length, Is.EqualTo(9));
    }

    [Test]
    public void RestoreDoesNotDuplicateSingleSavedItem()
    {
        List<InventorySlotData> saveData = CreateSaveEntries(1);

        _inventory.RestoreFromSave(saveData);

        Assert.That(_inventory.GetItemCount(), Is.EqualTo(1));
        Assert.That(_inventory.GetInventorySnapshot().Length, Is.EqualTo(1));
    }

    [Test]
    public void InventoryUi_ExposesAndSelectsAllNineSlotsWithMatchingHighlight()
    {
        GameObject uiRoot = new GameObject("InventoryQuickSlotTests.UI");
        uiRoot.SetActive(false);
        _uiObjects.Add(uiRoot);
        InventoryGridUI ui = uiRoot.AddComponent<InventoryGridUI>();
        Image[] icons = new Image[InventoryQuickSlotRules.MaxQuickSlots];
        Image[] backgrounds = new Image[InventoryQuickSlotRules.MaxQuickSlots];
        for (int i = 0; i < InventoryQuickSlotRules.MaxQuickSlots; i++)
        {
            GameObject slot = new GameObject($"Slot_{i + 1}", typeof(RectTransform), typeof(Image));
            slot.transform.SetParent(uiRoot.transform, false);
            backgrounds[i] = slot.GetComponent<Image>();

            GameObject icon = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            icon.transform.SetParent(slot.transform, false);
            icons[i] = icon.GetComponent<Image>();
        }

        SetPrivateField(ui, "_slotIcons", icons);
        SetPrivateField(ui, "_slotBackgrounds", backgrounds);
        Color selectedColor = GetPrivateField<Color>(ui, "_selectedSlotColor");
        Color unselectedColor = GetPrivateField<Color>(ui, "_unselectedSlotColor");

        InvokePrivate(ui, "ApplySlotVisibility", InventoryQuickSlotRules.MaxQuickSlots);
        for (int selectedIndex = 0; selectedIndex < InventoryQuickSlotRules.MaxQuickSlots; selectedIndex++)
        {
            InvokePrivate(ui, "SelectSlot", selectedIndex, true);
            Assert.That(ui.GetSelectedSlotIndex(), Is.EqualTo(selectedIndex));
            for (int i = 0; i < backgrounds.Length; i++)
            {
                Assert.That(backgrounds[i].gameObject.activeSelf, Is.True);
                Assert.That(backgrounds[i].color, Is.EqualTo(i == selectedIndex ? selectedColor : unselectedColor));
            }
        }
    }

    [TestCase(0, "Items/ToolWater", ToolType.Water)]
    [TestCase(4, "Items/ToolChemical", ToolType.Chemical)]
    [TestCase(5, "Items/ToolElectric", ToolType.Electric)]
    [TestCase(8, "Items/ToolCO2", ToolType.CO2)]
    public void Aud007SelectedTool_WorksAtRepresentativeSlots(
        int quickSlotIndex,
        string resourcePath,
        ToolType expectedTool)
    {
        InventoryItem item = Resources.Load<InventoryItem>(resourcePath);
        AddAtQuickSlot(item, quickSlotIndex);
        PlayerContext context = new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);
        context.SetSelectedQuickSlotIndex(quickSlotIndex);

        Assert.That(context.TryGetSelectedQuickSlotItem(out InventoryItem selectedItem), Is.True);
        Assert.That(selectedItem, Is.SameAs(item));
        Assert.That(context.TryGetSelectedTool(out ToolType selectedTool), Is.True);
        Assert.That(selectedTool, Is.EqualTo(expectedTool));
    }

    [Test]
    public void CleaningToolInSlotNine_CreatesStableSnapshot()
    {
        InventoryItem chemical = Resources.Load<InventoryItem>("Items/ToolChemical");
        AddAtQuickSlot(chemical, 8);
        PlayerContext context = new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);
        context.SetSelectedQuickSlotIndex(8);
        Assert.That(context.TryGetSelectedTool(out ToolType selectedTool), Is.True);
        MinigameToolSession session = new MinigameToolSession();

        Assert.That(session.TryCapture(selectedTool, CleaningMinigame.SupportsTool), Is.True);
        context.SetSelectedQuickSlotIndex(0);

        Assert.That(session.ActiveTool, Is.EqualTo(ToolType.Chemical));
    }

    [Test]
    public void WeldingToolInSlotSix_CreatesStableSnapshot()
    {
        InventoryItem electric = Resources.Load<InventoryItem>("Items/ToolElectric");
        AddAtQuickSlot(electric, 5);
        PlayerContext context = new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);
        context.SetSelectedQuickSlotIndex(5);
        Assert.That(context.TryGetSelectedTool(out ToolType selectedTool), Is.True);
        MinigameToolSession session = new MinigameToolSession();

        Assert.That(session.TryCapture(selectedTool, WeldingFillMinigame.SupportsTool), Is.True);
        context.SetSelectedQuickSlotIndex(0);

        Assert.That(session.ActiveTool, Is.EqualTo(ToolType.Electric));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void LegacyInventoryUpgradeTier_DoesNotLimitNineAvailableSlots(int legacyTier)
    {
        GameManager gameManager = CreateInactiveGameManager(out GameObject gameManagerObject);
        try
        {
            if (legacyTier > 0)
            {
                gameManager.RestoreOwnedToolUpgradesFromSave(new List<ToolDataEntry>
                {
                    new ToolDataEntry(GameManager.UpgradeIdInventoryQuickSlots, legacyTier)
                });
            }

            Assert.That(gameManager.GetUnlockedQuickSlots(), Is.EqualTo(InventoryQuickSlotRules.MaxQuickSlots));
        }
        finally
        {
            Object.DestroyImmediate(gameManagerObject);
        }
    }

    [Test]
    public void LegacyInventoryUpgradeTier_IsPreservedForSaveCompatibility()
    {
        GameManager gameManager = CreateInactiveGameManager(out GameObject gameManagerObject);
        try
        {
            gameManager.RestoreOwnedToolUpgradesFromSave(new List<ToolDataEntry>
            {
                new ToolDataEntry(GameManager.UpgradeIdInventoryQuickSlots, 2)
            });

            List<ToolDataEntry> savedUpgrades = gameManager.GetOwnedToolUpgradesForSave();
            ToolDataEntry inventoryEntry = savedUpgrades.Find(
                entry => entry.toolId == GameManager.UpgradeIdInventoryQuickSlots);
            Assert.That(inventoryEntry, Is.Not.Null);
            Assert.That(inventoryEntry.tier, Is.EqualTo(2));
        }
        finally
        {
            Object.DestroyImmediate(gameManagerObject);
        }
    }

    [Test]
    public void ObsoleteInventoryCapacityUpgrade_CannotBePurchasedOrSpendCurrency()
    {
        GameManager gameManager = CreateInactiveGameManager(out GameObject gameManagerObject);
        try
        {
            gameManager.RestoreCurrencyFromSave(1000);
            int currencyBefore = gameManager.GetCurrency();

            bool purchased = gameManager.TryPurchaseUpgradeInHome(
                GameManager.UpgradeIdInventoryQuickSlots,
                out int spentCurrency,
                out int resultingTier);

            Assert.That(purchased, Is.False);
            Assert.That(spentCurrency, Is.Zero);
            Assert.That(resultingTier, Is.Zero);
            Assert.That(gameManager.GetCurrency(), Is.EqualTo(currencyBefore));
        }
        finally
        {
            Object.DestroyImmediate(gameManagerObject);
        }
    }

    [Test]
    public void InventoryUpgradeStatus_ReportsNineOfNineAndNoPurchase()
    {
        GameManager gameManager = CreateInactiveGameManager(out GameObject gameManagerObject);
        try
        {
            List<GameManager.HomeUpgradeStatusData> statuses = gameManager.GetHomeUpgradeStatusEntries();
            GameManager.HomeUpgradeStatusData inventoryStatus = statuses.Find(
                status => status.upgradeId == GameManager.UpgradeIdInventoryQuickSlots);

            Assert.That(inventoryStatus, Is.Not.Null);
            Assert.That(inventoryStatus.canPurchase, Is.False);
            Assert.That(inventoryStatus.nextTierCost, Is.EqualTo(-1));
            Assert.That(inventoryStatus.unavailableReason, Does.Contain("9"));
            Assert.That(inventoryStatus.detailTextOverride, Does.Contain("9/9"));
            Assert.That(inventoryStatus.actionLabelOverride, Is.EqualTo("Maxed"));
        }
        finally
        {
            Object.DestroyImmediate(gameManagerObject);
        }
    }

    private void FillAllQuickSlots()
    {
        for (int i = 0; i < InventoryQuickSlotRules.MaxQuickSlots; i++)
        {
            AddAtQuickSlot(LoadItem(i), i);
        }
    }

    private void AddAtQuickSlot(InventoryItem item, int quickSlotIndex)
    {
        Assert.That(item, Is.Not.Null);
        Assert.That(TryGetGridPosition(quickSlotIndex, out int gridX, out int gridY), Is.True);
        Assert.That(_inventory.AddItemAt(item, gridX, gridY), Is.True);
    }

    private static bool TryGetGridPosition(int quickSlotIndex, out int gridX, out int gridY)
    {
        return InventoryQuickSlotRules.TryGetGridPosition(
            quickSlotIndex,
            gridWidth: 5,
            gridHeight: 5,
            out gridX,
            out gridY);
    }

    private static InventoryItem LoadItem(int index)
    {
        return Resources.Load<InventoryItem>(ItemResourcePaths[index % ItemResourcePaths.Length]);
    }

    private static List<InventorySlotData> CreateSaveEntries(int itemCount)
    {
        List<InventorySlotData> entries = new List<InventorySlotData>(itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            Assert.That(TryGetGridPosition(i, out int gridX, out int gridY), Is.True);
            entries.Add(new InventorySlotData(gridX, gridY, LoadItem(i).ItemId));
        }

        return entries;
    }

    private static void AssertSnapshotsEqual(InventorySlotData[] first, InventorySlotData[] second)
    {
        Assert.That(second.Length, Is.EqualTo(first.Length));
        for (int i = 0; i < first.Length; i++)
        {
            Assert.That(second[i].gridX, Is.EqualTo(first[i].gridX));
            Assert.That(second[i].gridY, Is.EqualTo(first[i].gridY));
            Assert.That(second[i].itemId, Is.EqualTo(first[i].itemId));
        }
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        return (T)field.GetValue(target);
    }

    private static void InvokePrivate(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method.Invoke(target, arguments);
    }

    private static void ResetInventorySingleton()
    {
        FieldInfo field = typeof(InventorySystem).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field.SetValue(null, null);
    }

    private static GameManager CreateInactiveGameManager(out GameObject gameManagerObject)
    {
        gameManagerObject = new GameObject("InventoryQuickSlotTests.GameManager");
        gameManagerObject.SetActive(false);
        return gameManagerObject.AddComponent<GameManager>();
    }
}
