using System;
using Game.Core;
using Game.Core.Events;
using Game.Interaction;
using Game.Inventory;
using Game.Minigames;
using Game.Player;
using NUnit.Framework;
using UnityEngine;

public class InventoryMinigameToolTests
{
    private GameObject _inventoryObject;
    private InventorySystem _inventorySystem;

    [TearDown]
    public void TearDown()
    {
        if (_inventoryObject != null)
        {
            UnityEngine.Object.DestroyImmediate(_inventoryObject);
        }
    }

    [Test]
    public void ExistingAndNewItems_DefaultToNoToolCapability()
    {
        InventoryItem transientItem = ScriptableObject.CreateInstance<InventoryItem>();
        try
        {
            Assert.That(transientItem.ToolType, Is.EqualTo(ToolType.None));
            Assert.That(Resources.Load<InventoryItem>("Items/WeddingRingGold").ToolType, Is.EqualTo(ToolType.None));
            Assert.That(Resources.Load<InventoryItem>("Items/WeddingRingSilver").ToolType, Is.EqualTo(ToolType.None));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(transientItem);
        }
    }

    [TestCase("Items/ToolWater", "tool_water", ToolType.Water)]
    [TestCase("Items/ToolGasoline", "tool_gasoline", ToolType.Gasoline)]
    [TestCase("Items/ToolChemical", "tool_chemical", ToolType.Chemical)]
    [TestCase("Items/ToolElectric", "tool_electric", ToolType.Electric)]
    [TestCase("Items/ToolCO2", "tool_co2", ToolType.CO2)]
    public void ToolAssets_HaveStableTypedCapability(string resourcePath, string itemId, ToolType expectedTool)
    {
        InventoryItem item = Resources.Load<InventoryItem>(resourcePath);

        Assert.That(item, Is.Not.Null);
        Assert.That(item.ItemId, Is.EqualTo(itemId));
        Assert.That(item.Type, Is.EqualTo(Game.Inventory.ItemType.Tool));
        Assert.That(item.ToolType, Is.EqualTo(expectedTool));
    }

    [Test]
    public void PlayerContext_ResolvesSelectedQuickSlotItemAndToolFromInventoryModel()
    {
        CreateInventory();
        InventoryItem chemical = Resources.Load<InventoryItem>("Items/ToolChemical");
        Assert.That(_inventorySystem.AddItemAt(chemical, 1, 0, PlayerContextRegistry.DefaultLocalPlayerId), Is.True);

        PlayerContext context = new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);
        context.SetSelectedQuickSlotIndex(1);

        Assert.That(context.TryGetSelectedQuickSlotItem(out InventoryItem selectedItem), Is.True);
        Assert.That(selectedItem, Is.SameAs(chemical));
        Assert.That(context.TryGetSelectedTool(out ToolType selectedTool), Is.True);
        Assert.That(selectedTool, Is.EqualTo(ToolType.Chemical));
    }

    [Test]
    public void PlayerContext_EmptySelectedQuickSlotIsSafe()
    {
        CreateInventory();
        PlayerContext context = new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);
        context.SetSelectedQuickSlotIndex(8);

        Assert.DoesNotThrow(() => context.TryGetSelectedQuickSlotItem(out _));
        Assert.That(context.TryGetSelectedQuickSlotItem(out InventoryItem selectedItem), Is.False);
        Assert.That(selectedItem, Is.Null);
        Assert.That(context.TryGetSelectedTool(out ToolType selectedTool), Is.False);
        Assert.That(selectedTool, Is.EqualTo(ToolType.None));
    }

    [TestCase(ToolType.Water)]
    [TestCase(ToolType.Gasoline)]
    [TestCase(ToolType.Chemical)]
    public void Cleaning_AcceptsOnlyCleaningTools(ToolType toolType)
    {
        Assert.That(CleaningMinigame.SupportsTool(toolType), Is.True);
    }

    [TestCase(ToolType.None)]
    [TestCase(ToolType.Electric)]
    [TestCase(ToolType.CO2)]
    public void Cleaning_RejectsNoneAndWeldingTools(ToolType toolType)
    {
        Assert.That(CleaningMinigame.SupportsTool(toolType), Is.False);
    }

    [TestCase(ToolType.Electric)]
    [TestCase(ToolType.CO2)]
    public void Welding_AcceptsOnlyWeldingTools(ToolType toolType)
    {
        Assert.That(WeldingFillMinigame.SupportsTool(toolType), Is.True);
    }

    [TestCase(ToolType.None)]
    [TestCase(ToolType.Water)]
    [TestCase(ToolType.Gasoline)]
    [TestCase(ToolType.Chemical)]
    public void Welding_RejectsNoneAndCleaningTools(ToolType toolType)
    {
        Assert.That(WeldingFillMinigame.SupportsTool(toolType), Is.False);
    }

    [Test]
    public void SessionSnapshot_DoesNotFollowLaterSlotSelectionChanges()
    {
        MinigameToolSession session = new MinigameToolSession();
        ToolType selectedTool = ToolType.Water;

        Assert.That(session.TryCapture(selectedTool, CleaningMinigame.SupportsTool), Is.True);
        selectedTool = ToolType.Chemical;

        Assert.That(session.ActiveTool, Is.EqualTo(ToolType.Water));
    }

    [Test]
    public void NewSession_CapturesNewSelectionAfterPreviousSessionClears()
    {
        MinigameToolSession session = new MinigameToolSession();
        Assert.That(session.TryCapture(ToolType.Water, CleaningMinigame.SupportsTool), Is.True);

        session.Clear();
        Assert.That(session.TryCapture(ToolType.Chemical, CleaningMinigame.SupportsTool), Is.True);

        Assert.That(session.ActiveTool, Is.EqualTo(ToolType.Chemical));
    }

    [Test]
    public void CancelledSession_ClearsToolSnapshot()
    {
        MinigameToolSession session = new MinigameToolSession();
        Assert.That(session.TryCapture(ToolType.Electric, WeldingFillMinigame.SupportsTool), Is.True);

        session.Clear();

        Assert.That(session.ActiveTool, Is.EqualTo(ToolType.None));
    }

    [Test]
    public void CompletedSession_ClearsToolSnapshot()
    {
        MinigameToolSession session = new MinigameToolSession();
        Assert.That(session.TryCapture(ToolType.CO2, WeldingFillMinigame.SupportsTool), Is.True);

        session.Clear();

        Assert.That(session.ActiveTool, Is.EqualTo(ToolType.None));
    }

    [Test]
    public void CleaningStart_WithIncompatibleSelectedTool_IsRejectedWithoutStateMutation()
    {
        AssertRejectedStart<PipeInteractable>(
            ToolType.Electric,
            "Water, Gasoline, or Chemical");
    }

    [Test]
    public void WeldingStart_WithIncompatibleSelectedTool_IsRejectedWithoutStateMutation()
    {
        AssertRejectedStart<WeldingInteractable>(
            ToolType.Water,
            "Electric or CO2");
    }

    private void AssertRejectedStart<TInteractable>(ToolType selectedTool, string expectedFeedback)
        where TInteractable : BaseInteractable
    {
        CreateInventory();
        InventoryItem item = LoadToolAsset(selectedTool);
        Assert.That(_inventorySystem.AddItemAt(item, 0, 0, PlayerContextRegistry.DefaultLocalPlayerId), Is.True);

        GameObject playerObject = new GameObject("ToolTestPlayer");
        playerObject.SetActive(false);
        PlayerController playerController = playerObject.AddComponent<PlayerController>();
        Assert.That(PlayerContextRegistry.RegisterOrUpdate(playerController, PlayerContextRegistry.DefaultLocalPlayerId), Is.True);
        Assert.That(PlayerContextRegistry.TryGetLocalContext(out PlayerContext context), Is.True);
        context.SetSelectedQuickSlotIndex(0);

        GameObject gameManagerObject = new GameObject("InactiveToolTestGameManager");
        gameManagerObject.SetActive(false);
        GameManager gameManager = gameManagerObject.AddComponent<GameManager>();
        GameState stateBefore = gameManager.CurrentState;

        GameObject interactableObject = new GameObject(typeof(TInteractable).Name);
        TInteractable interactable = interactableObject.AddComponent<TInteractable>();
        int startedEventCount = 0;
        string feedback = null;
        Action<MinigameStartedEvent> startedHandler = _ => startedEventCount++;
        Action<PlayerFeedbackEvent> feedbackHandler = eventData => feedback = eventData.Message;
        EventBus.Subscribe(startedHandler);
        EventBus.Subscribe(feedbackHandler);

        try
        {
            interactable.Interact();

            Assert.That(gameManager.CurrentState, Is.EqualTo(stateBefore));
            Assert.That(interactable.CanInteract, Is.True);
            Assert.That(startedEventCount, Is.Zero);
            Assert.That(feedback, Does.Contain(expectedFeedback));
        }
        finally
        {
            EventBus.Unsubscribe(startedHandler);
            EventBus.Unsubscribe(feedbackHandler);
            PlayerContextRegistry.Unregister(playerController, PlayerContextRegistry.DefaultLocalPlayerId);
            UnityEngine.Object.DestroyImmediate(interactableObject);
            UnityEngine.Object.DestroyImmediate(gameManagerObject);
            UnityEngine.Object.DestroyImmediate(playerObject);
        }
    }

    private static InventoryItem LoadToolAsset(ToolType toolType)
    {
        string resourceName = toolType switch
        {
            ToolType.Water => "ToolWater",
            ToolType.Gasoline => "ToolGasoline",
            ToolType.Chemical => "ToolChemical",
            ToolType.Electric => "ToolElectric",
            ToolType.CO2 => "ToolCO2",
            _ => string.Empty
        };

        return Resources.Load<InventoryItem>($"Items/{resourceName}");
    }

    private void CreateInventory()
    {
        if (_inventorySystem != null)
        {
            return;
        }

        _inventoryObject = new GameObject("InventoryMinigameToolTests.InventorySystem");
        _inventorySystem = _inventoryObject.AddComponent<InventorySystem>();
    }
}
