using Game.Inventory;
using Game.Minigames;
using NUnit.Framework;
using System.Collections.Generic;

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

    [Test]
    public void Coverage_RegistersEachSampleOnlyOnce()
    {
        WeldingCoverageMask mask = new WeldingCoverageMask(24);

        int firstPass = mask.StampSegment(true, 0.4f, 0.6f, 0.08f, 0.02f);
        int repeatedPass = mask.StampSegment(true, 0.4f, 0.6f, 0.08f, 0.02f);

        Assert.That(firstPass, Is.GreaterThan(0));
        Assert.That(repeatedPass, Is.Zero);
        Assert.That(mask.CoveredCount, Is.EqualTo(firstPass));
    }

    [Test]
    public void Coverage_OutsideActiveZoneDoesNotProgress()
    {
        WeldingCoverageMask mask = new WeldingCoverageMask(24);

        Assert.That(mask.StampSegment(false, 0f, 1f, 1f, 0.01f), Is.Zero);
        Assert.That(mask.Progress01, Is.Zero);
    }

    [Test]
    public void Coverage_LargerRadiusCoversAtLeastAsManySamples()
    {
        WeldingCoverageMask smallRadius = new WeldingCoverageMask(64);
        WeldingCoverageMask largeRadius = new WeldingCoverageMask(64);

        smallRadius.StampSegment(true, 0.5f, 0.5f, 0.04f, 0.01f);
        largeRadius.StampSegment(true, 0.5f, 0.5f, 0.12f, 0.01f);

        Assert.That(largeRadius.CoveredCount, Is.GreaterThanOrEqualTo(smallRadius.CoveredCount));
    }

    [Test]
    public void Coverage_ProgressIsAlwaysNormalized()
    {
        WeldingCoverageMask mask = new WeldingCoverageMask(24);

        mask.StampSegment(true, -10f, 10f, 10f, 0.00001f);

        Assert.That(mask.Progress01, Is.InRange(0f, 1f));
        Assert.That(mask.Progress01, Is.EqualTo(1f));
        Assert.That(mask.CoveredCount, Is.EqualTo(mask.SampleCount));
    }

    [Test]
    public void OverallProgress_RequiresEveryZoneForCompletion()
    {
        List<WeldingCoverageMask> zones = CreateThreeZones();
        Fill(zones[0]);
        Fill(zones[1]);

        Assert.That(WeldingProgressMath.AreAllZonesComplete(zones), Is.False);
        Assert.That(WeldingProgressMath.CalculateOverallProgress(zones), Is.LessThan(1f));

        Fill(zones[2]);

        Assert.That(WeldingProgressMath.AreAllZonesComplete(zones), Is.True);
        Assert.That(WeldingProgressMath.CalculateOverallProgress(zones), Is.EqualTo(1f));
    }

    [Test]
    public void OverallProgress_EmptyZonesAreSafeAndIncomplete()
    {
        List<WeldingCoverageMask> zones = new List<WeldingCoverageMask>();

        float progress = WeldingProgressMath.CalculateOverallProgress(zones);

        Assert.That(progress, Is.Zero);
        Assert.That(float.IsNaN(progress), Is.False);
        Assert.That(float.IsInfinity(progress), Is.False);
        Assert.That(WeldingProgressMath.AreAllZonesComplete(zones), Is.False);
    }

    [Test]
    public void OverallProgress_NullZoneIsSafeAndPreventsCompletion()
    {
        List<WeldingCoverageMask> zones = CreateThreeZones();
        Fill(zones[0]);
        zones[1] = null;
        Fill(zones[2]);

        Assert.That(WeldingProgressMath.CalculateOverallProgress(zones), Is.InRange(0f, 1f));
        Assert.That(WeldingProgressMath.AreAllZonesComplete(zones), Is.False);
    }

    [Test]
    public void TerminalResult_CanBeRegisteredOnlyOnce()
    {
        WeldingResultGate gate = new WeldingResultGate();

        Assert.That(gate.TrySet(MinigameResult.Pass), Is.True);
        Assert.That(gate.TrySet(MinigameResult.Fail), Is.False);
        Assert.That(gate.Result, Is.EqualTo(MinigameResult.Pass));
    }

    [Test]
    public void TerminalFailure_BlocksLateSuccess()
    {
        WeldingResultGate gate = new WeldingResultGate();

        Assert.That(gate.TrySet(MinigameResult.Fail), Is.True);
        Assert.That(gate.TrySet(MinigameResult.Pass), Is.False);
        Assert.That(gate.Result, Is.EqualTo(MinigameResult.Fail));
    }

    [Test]
    public void TerminalResult_ResetAllowsNewSession()
    {
        WeldingResultGate gate = new WeldingResultGate();
        gate.TrySet(MinigameResult.Fail);

        gate.Reset();

        Assert.That(gate.Result, Is.EqualTo(MinigameResult.None));
        Assert.That(gate.TrySet(MinigameResult.Pass), Is.True);
    }

    [Test]
    public void SessionCleanup_ClearsCoverageAndRadiusInput()
    {
        WeldingCoverageMask mask = new WeldingCoverageMask(24);
        WeldingRadiusController radius = new WeldingRadiusController();
        radius.BeginSession(WeldingToolRules.GetDefinition(ToolType.Electric));
        Fill(mask);

        mask.Clear();
        radius.EndSession();

        Assert.That(mask.CoveredCount, Is.Zero);
        Assert.That(mask.Progress01, Is.Zero);
        Assert.That(radius.IsSessionActive, Is.False);
        Assert.That(radius.TryApplyScroll(1f, 100), Is.False);
    }

    [Test]
    public void RestartedCoverageMask_HasNoPreviousSessionProgress()
    {
        WeldingCoverageMask previousSession = new WeldingCoverageMask(24);
        Fill(previousSession);

        WeldingCoverageMask restartedSession = new WeldingCoverageMask(24);

        Assert.That(previousSession.IsComplete, Is.True);
        Assert.That(restartedSession.CoveredCount, Is.Zero);
        Assert.That(restartedSession.Progress01, Is.Zero);
    }

    [TestCase(0, WeldingMarkerBudget.MinimumSamplesPerZone)]
    [TestCase(1, WeldingMarkerBudget.MinimumSamplesPerZone)]
    [TestCase(48, 48)]
    [TestCase(1000, WeldingMarkerBudget.MaximumSamplesPerZone)]
    public void MarkerSampleCount_IsBounded(int requested, int expected)
    {
        Assert.That(WeldingMarkerBudget.ClampSampleCount(requested), Is.EqualTo(expected));
    }

    [Test]
    public void VisualObjectBudget_IsBoundedForThreeZones()
    {
        int objectLimit = WeldingMarkerBudget.CalculateVisualObjectLimit(3, int.MaxValue);

        Assert.That(objectLimit, Is.EqualTo(3 * (WeldingMarkerBudget.MaximumSamplesPerZone + 3)));
    }

    [TestCase(float.NaN, 0f, 0.1f, 0.01f)]
    [TestCase(0f, float.PositiveInfinity, 0.1f, 0.01f)]
    [TestCase(0f, 1f, float.NegativeInfinity, 0.01f)]
    public void InvalidCoverageInput_DoesNotCorruptProgress(
        float startT,
        float endT,
        float radiusT,
        float spacingT)
    {
        WeldingCoverageMask mask = new WeldingCoverageMask(24);

        Assert.That(mask.StampSegment(true, startT, endT, radiusT, spacingT), Is.Zero);
        Assert.That(mask.Progress01, Is.Zero);
        Assert.That(float.IsNaN(mask.Progress01), Is.False);
    }

    [Test]
    public void ZeroSampleMask_IsSafe()
    {
        WeldingCoverageMask mask = new WeldingCoverageMask(0);

        Assert.That(mask.StampSegment(true, 0f, 1f, 1f, 0.01f), Is.Zero);
        Assert.That(mask.Progress01, Is.Zero);
        Assert.That(mask.IsComplete, Is.False);
    }

    private static List<WeldingCoverageMask> CreateThreeZones()
    {
        return new List<WeldingCoverageMask>
        {
            new WeldingCoverageMask(24),
            new WeldingCoverageMask(24),
            new WeldingCoverageMask(24)
        };
    }

    private static void Fill(WeldingCoverageMask mask)
    {
        mask.StampSegment(true, 0f, 1f, 1f, 0.01f);
    }
}
