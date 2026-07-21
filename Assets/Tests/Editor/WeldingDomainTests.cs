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

    [TestCase(ToolType.Electric, 0.04f)]
    [TestCase(ToolType.CO2, 0.07f)]
    public void RadiusSession_StartsAtToolInitialRadius(ToolType toolType, float expectedRadius)
    {
        WeldingRadiusController radius = new WeldingRadiusController();

        Assert.That(radius.BeginSession(WeldingToolRules.GetDefinition(toolType)), Is.True);
        Assert.That(radius.CurrentRadius, Is.EqualTo(expectedRadius).Within(0.0001f));
    }

    [Test]
    public void RadiusIncrease_UsesExactlyOneStep()
    {
        WeldingToolDefinition definition = WeldingToolRules.GetDefinition(ToolType.Electric);
        WeldingRadiusController radius = new WeldingRadiusController();
        radius.BeginSession(definition);

        Assert.That(radius.TryApplyScroll(120f, 1), Is.True);
        Assert.That(radius.CurrentRadius, Is.EqualTo(definition.InitialRadius + definition.RadiusStep).Within(0.0001f));
    }

    [Test]
    public void RadiusDecrease_UsesExactlyOneStep()
    {
        WeldingToolDefinition definition = WeldingToolRules.GetDefinition(ToolType.CO2);
        WeldingRadiusController radius = new WeldingRadiusController();
        radius.BeginSession(definition);

        Assert.That(radius.TryApplyScroll(-120f, 1), Is.True);
        Assert.That(radius.CurrentRadius, Is.EqualTo(definition.InitialRadius - definition.RadiusStep).Within(0.0001f));
    }

    [Test]
    public void Radius_IsClampedToToolBounds()
    {
        WeldingToolDefinition definition = WeldingToolRules.GetDefinition(ToolType.Electric);
        WeldingRadiusController radius = new WeldingRadiusController();
        radius.BeginSession(definition);

        for (int frame = 0; frame < 100; frame++) radius.TryApplyScroll(1f, frame);
        Assert.That(radius.CurrentRadius, Is.EqualTo(definition.MaximumRadius).Within(0.0001f));
        for (int frame = 100; frame < 200; frame++) radius.TryApplyScroll(-1f, frame);
        Assert.That(radius.CurrentRadius, Is.EqualTo(definition.MinimumRadius).Within(0.0001f));
    }

    [Test]
    public void RadiusInput_AfterTerminalStateHasNoEffect()
    {
        WeldingRadiusController radius = new WeldingRadiusController();
        radius.BeginSession(WeldingToolRules.GetDefinition(ToolType.Electric));
        radius.EndSession();

        Assert.That(radius.TryApplyScroll(1f, 2), Is.False);
        Assert.That(radius.CurrentRadius, Is.Zero);
    }

    [Test]
    public void RadiusRestart_UsesNewToolInitialRadius()
    {
        WeldingRadiusController radius = new WeldingRadiusController();
        radius.BeginSession(WeldingToolRules.GetDefinition(ToolType.Electric));
        radius.TryApplyScroll(1f, 1);
        radius.EndSession();

        radius.BeginSession(WeldingToolRules.GetDefinition(ToolType.CO2));

        Assert.That(radius.CurrentRadius, Is.EqualTo(0.07f).Within(0.0001f));
    }

    [Test]
    public void MultipleRadiusCallbacksInSameFrame_ApplyAtMostOneStep()
    {
        WeldingToolDefinition definition = WeldingToolRules.GetDefinition(ToolType.CO2);
        WeldingRadiusController radius = new WeldingRadiusController();
        radius.BeginSession(definition);

        Assert.That(radius.TryApplyScroll(1f, 10), Is.True);
        Assert.That(radius.TryApplyScroll(1f, 10), Is.False);
        Assert.That(radius.TryApplyScroll(-1f, 10), Is.False);
        Assert.That(radius.CurrentRadius, Is.EqualTo(definition.InitialRadius + definition.RadiusStep).Within(0.0001f));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void InvalidScrollInput_DoesNotCorruptRadius(float scrollDelta)
    {
        WeldingRadiusController radius = new WeldingRadiusController();
        radius.BeginSession(WeldingToolRules.GetDefinition(ToolType.Electric));

        Assert.That(radius.TryApplyScroll(scrollDelta, 1), Is.False);
        Assert.That(float.IsNaN(radius.CurrentRadius), Is.False);
        Assert.That(float.IsInfinity(radius.CurrentRadius), Is.False);
        Assert.That(radius.CurrentRadius, Is.GreaterThan(0f));
    }
}
