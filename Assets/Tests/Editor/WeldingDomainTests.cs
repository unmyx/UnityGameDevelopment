using Game.Inventory;
using Game.Minigames;
using NUnit.Framework;

public class WeldingDomainTests
{
    [TestCase(ToolType.Electric)]
    [TestCase(ToolType.CO2)]
    public void WeldingTools_HaveValidDefinitions(ToolType toolType)
    {
        Assert.That(WeldingToolRules.TryGetDefinition(toolType, out WeldingToolDefinition definition), Is.True);
        Assert.That(definition.IsValid, Is.True);
        Assert.That(definition.ToolType, Is.EqualTo(toolType));
        Assert.That(definition.InitialRadius, Is.InRange(definition.MinimumRadius, definition.MaximumRadius));
        Assert.That(definition.RadiusStep, Is.GreaterThan(0f));
    }

    [Test]
    public void ElectricAndCo2_HaveDifferentInitialRadii()
    {
        WeldingToolDefinition electric = WeldingToolRules.GetDefinition(ToolType.Electric);
        WeldingToolDefinition co2 = WeldingToolRules.GetDefinition(ToolType.CO2);

        Assert.That(electric.InitialRadius, Is.LessThan(co2.InitialRadius));
    }

    [TestCase(ToolType.None)]
    [TestCase(ToolType.Water)]
    [TestCase(ToolType.Gasoline)]
    [TestCase(ToolType.Chemical)]
    public void NonWeldingTools_HaveNoDefinition(ToolType toolType)
    {
        Assert.That(WeldingToolRules.TryGetDefinition(toolType, out WeldingToolDefinition definition), Is.False);
        Assert.That(definition.IsValid, Is.False);
    }

    [Test]
    public void CapturedTool_DoesNotChangeWithQuickSlotSelection()
    {
        MinigameToolSession session = new MinigameToolSession();
        ToolType selectedTool = ToolType.Electric;
        Assert.That(session.TryCapture(selectedTool, WeldingFillMinigame.SupportsTool), Is.True);

        selectedTool = ToolType.CO2;

        Assert.That(session.ActiveTool, Is.EqualTo(ToolType.Electric));
        Assert.That(WeldingToolRules.GetDefinition(session.ActiveTool).InitialRadius, Is.EqualTo(0.04f));
    }

    [Test]
    public void NewSession_UsesNewToolSnapshot()
    {
        MinigameToolSession session = new MinigameToolSession();
        Assert.That(session.TryCapture(ToolType.Electric, WeldingFillMinigame.SupportsTool), Is.True);
        session.Clear();
        Assert.That(session.TryCapture(ToolType.CO2, WeldingFillMinigame.SupportsTool), Is.True);

        Assert.That(session.ActiveTool, Is.EqualTo(ToolType.CO2));
        Assert.That(WeldingToolRules.GetDefinition(session.ActiveTool).InitialRadius, Is.EqualTo(0.07f));
    }
}
