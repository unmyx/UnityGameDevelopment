using Game.Inventory;
using Game.Minigames;
using NUnit.Framework;
using UnityEngine;

public class CleaningDomainTests
{
    [TestCase(ToolType.Water, 6)]
    [TestCase(ToolType.Gasoline, 4)]
    [TestCase(ToolType.Chemical, 2)]
    public void RequiredPasses_MatchCleaningToolContract(ToolType toolType, int expectedPasses)
    {
        Assert.That(CleaningToolRules.GetRequiredPasses(toolType), Is.EqualTo(expectedPasses));
    }

    [TestCase(ToolType.None)]
    [TestCase(ToolType.Electric)]
    [TestCase(ToolType.CO2)]
    public void RequiredPasses_InvalidCleaningToolReturnsZero(ToolType toolType)
    {
        Assert.That(CleaningToolRules.GetRequiredPasses(toolType), Is.Zero);
    }

    [TestCase(ToolType.Water)]
    [TestCase(ToolType.Gasoline)]
    [TestCase(ToolType.Chemical)]
    public void RequiredPassesMinusOne_DoesNotCompleteStain(ToolType toolType)
    {
        CleaningStainProgress stain = new CleaningStainProgress(toolType);

        for (int i = 0; i < stain.RequiredPasses - 1; i++)
        {
            Assert.That(stain.TryRegisterPass(), Is.True);
        }

        Assert.That(stain.IsComplete, Is.False);
        Assert.That(stain.Progress01, Is.LessThan(1f));
    }

    [TestCase(ToolType.Water)]
    [TestCase(ToolType.Gasoline)]
    [TestCase(ToolType.Chemical)]
    public void RequiredPasses_CompleteStainExactlyAtN(ToolType toolType)
    {
        CleaningStainProgress stain = new CleaningStainProgress(toolType);

        for (int i = 0; i < stain.RequiredPasses; i++)
        {
            Assert.That(stain.TryRegisterPass(), Is.True);
        }

        Assert.That(stain.IsComplete, Is.True);
        Assert.That(stain.CompletedPasses, Is.EqualTo(stain.RequiredPasses));
        Assert.That(stain.Progress01, Is.EqualTo(1f));
    }

    [Test]
    public void AdditionalPassAfterCompletion_HasNoEffect()
    {
        CleaningStainProgress stain = new CleaningStainProgress(ToolType.Chemical);
        Assert.That(stain.TryRegisterPass(), Is.True);
        Assert.That(stain.TryRegisterPass(), Is.True);

        Assert.That(stain.TryRegisterPass(), Is.False);
        Assert.That(stain.CompletedPasses, Is.EqualTo(2));
        Assert.That(stain.Progress01, Is.EqualTo(1f));
    }

    [Test]
    public void SlotChangeDuringSession_DoesNotChangeRequiredPasses()
    {
        ToolType selectedTool = ToolType.Water;
        CleaningStainProgress stain = new CleaningStainProgress(selectedTool);

        selectedTool = ToolType.Chemical;

        Assert.That(stain.RequiredPasses, Is.EqualTo(6));
    }

    [Test]
    public void NewSession_UsesNewToolSnapshot()
    {
        CleaningStainProgress firstSession = new CleaningStainProgress(ToolType.Water);
        CleaningStainProgress secondSession = new CleaningStainProgress(ToolType.Chemical);

        Assert.That(firstSession.RequiredPasses, Is.EqualTo(6));
        Assert.That(secondSession.RequiredPasses, Is.EqualTo(2));
    }

    [Test]
    public void Reset_ClearsIntegerProgress()
    {
        CleaningStainProgress stain = new CleaningStainProgress(ToolType.Gasoline);
        stain.TryRegisterPass();
        stain.TryRegisterPass();

        stain.Reset();

        Assert.That(stain.CompletedPasses, Is.Zero);
        Assert.That(stain.Progress01, Is.Zero);
        Assert.That(stain.IsComplete, Is.False);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(4)]
    public void SurfaceCounts_OtherThanThreeAreRejected(int surfaceCount)
    {
        bool created = CleaningSpawnRules.TryCreateSurfaceStainCounts(
            surfaceCount,
            new System.Random(1234),
            out int[] counts);

        Assert.That(created, Is.False);
        Assert.That(counts, Is.Empty);
    }

    [Test]
    public void ThreeSurfaces_EachReceiveOneToThreeStains()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            Assert.That(CleaningSpawnRules.TryCreateSurfaceStainCounts(
                CleaningSpawnRules.RequiredSurfaceCount,
                new System.Random(seed),
                out int[] counts), Is.True);
            Assert.That(counts, Has.Length.EqualTo(3));
            Assert.That(counts, Has.All.InRange(1, 3));
            Assert.That(counts[0] + counts[1] + counts[2], Is.InRange(3, 9));
        }
    }

    [Test]
    public void SurfaceCounts_AreDeterministicForSeed()
    {
        CleaningSpawnRules.TryCreateSurfaceStainCounts(3, new System.Random(98765), out int[] first);
        CleaningSpawnRules.TryCreateSurfaceStainCounts(3, new System.Random(98765), out int[] second);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void SurfaceCounts_AreIndependentlySampled()
    {
        bool observedDifferentCountsOnSameSession = false;
        for (int seed = 0; seed < 100 && !observedDifferentCountsOnSameSession; seed++)
        {
            CleaningSpawnRules.TryCreateSurfaceStainCounts(3, new System.Random(seed), out int[] counts);
            observedDifferentCountsOnSameSession = counts[0] != counts[1] || counts[1] != counts[2];
        }

        Assert.That(observedDifferentCountsOnSameSession, Is.True);
    }

    [Test]
    public void PlacementBudget_RejectsPositionsInsideMinimumSpacing()
    {
        CleaningSurfacePlacementBudget placement = new CleaningSurfacePlacementBudget(2, 4, 1f);
        Assert.That(placement.TryBeginAttempt(), Is.True);
        Assert.That(placement.TryAccept(Vector3.zero), Is.True);
        Assert.That(placement.TryBeginAttempt(), Is.True);

        Assert.That(placement.TryAccept(new Vector3(0.5f, 0f, 0f)), Is.False);
        Assert.That(placement.AcceptedPositions, Has.Count.EqualTo(1));
        Assert.That(placement.IsComplete, Is.False);
    }

    [Test]
    public void PlacementBudget_AcceptsPositionsAtMinimumSpacing()
    {
        CleaningSurfacePlacementBudget placement = new CleaningSurfacePlacementBudget(2, 4, 1f);
        placement.TryBeginAttempt();
        placement.TryAccept(Vector3.zero);
        placement.TryBeginAttempt();

        Assert.That(placement.TryAccept(Vector3.right), Is.True);
        Assert.That(placement.IsComplete, Is.True);
    }

    [Test]
    public void PlacementBudget_StopsAfterBoundedFailure()
    {
        CleaningSurfacePlacementBudget placement = new CleaningSurfacePlacementBudget(2, 3, 1f);

        while (placement.TryBeginAttempt())
        {
            placement.TryAccept(Vector3.zero);
        }

        Assert.That(placement.Attempts, Is.EqualTo(3));
        Assert.That(placement.AcceptedPositions, Has.Count.EqualTo(1));
        Assert.That(placement.IsComplete, Is.False);
        Assert.That(placement.CanAttempt, Is.False);
    }

    [Test]
    public void FailedSpawnTransaction_CleansEveryPendingStain()
    {
        CleaningSpawnTransaction<string> transaction = new CleaningSpawnTransaction<string>();
        transaction.Add("stain-a");
        transaction.Add("stain-b");
        int cleanedItems = 0;

        transaction.Rollback(_ => cleanedItems++);

        Assert.That(cleanedItems, Is.EqualTo(2));
        Assert.That(transaction.PendingCount, Is.Zero);
    }

    [Test]
    public void SuccessfulSpawnTransaction_CommitsEveryPendingStain()
    {
        CleaningSpawnTransaction<string> transaction = new CleaningSpawnTransaction<string>();
        transaction.Add("stain-a");
        transaction.Add("stain-b");
        System.Collections.Generic.List<string> committed = new System.Collections.Generic.List<string>();

        transaction.CommitTo(committed);

        Assert.That(committed, Is.EqualTo(new[] { "stain-a", "stain-b" }));
        Assert.That(transaction.PendingCount, Is.Zero);
    }

    [Test]
    public void CursorMovementWithoutToolUse_DoesNotRegisterPass()
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();

        Assert.That(tracker.TryRegisterPass(false, 10, 1, out _), Is.False);
        Assert.That(tracker.LatchedStainId, Is.Null);
    }

    [Test]
    public void SlowStrokeAcrossSeveralFrames_RegistersOnePass()
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();
        int passes = 0;
        tracker.TryRegisterPass(true, null, 1, out _);
        for (int frame = 2; frame <= 12; frame++)
        {
            if (tracker.TryRegisterPass(true, 10, frame, out _))
            {
                passes++;
            }
        }

        Assert.That(passes, Is.EqualTo(1));
    }

    [Test]
    public void FastSingleFrameStroke_RegistersOnePass()
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();

        Assert.That(tracker.TryRegisterPass(true, 10, 42, out int stainId), Is.True);
        Assert.That(stainId, Is.EqualTo(10));
    }

    [Test]
    public void FastSweepAcrossStain_IsDetectedInWorldSpace()
    {
        Vector3 stainPosition = new Vector3(0f, 0f, 10f);
        Ray previousRay = new Ray(Vector3.zero, new Vector3(-1f, 0f, 10f).normalized);
        Ray currentRay = new Ray(Vector3.zero, new Vector3(1f, 0f, 10f).normalized);

        bool hit = CleaningPointerGeometry.IsRayOrSweepInsideRadius(
            currentRay,
            true,
            previousRay,
            stainPosition,
            0.2f,
            out _);

        Assert.That(hit, Is.True);
    }

    [Test]
    public void PointerSweepOutsideWorldRadius_DoesNotHitStain()
    {
        Vector3 stainPosition = new Vector3(0f, 2f, 10f);
        Ray previousRay = new Ray(Vector3.zero, new Vector3(-1f, 0f, 10f).normalized);
        Ray currentRay = new Ray(Vector3.zero, new Vector3(1f, 0f, 10f).normalized);

        bool hit = CleaningPointerGeometry.IsRayOrSweepInsideRadius(
            currentRay,
            true,
            previousRay,
            stainPosition,
            0.2f,
            out _);

        Assert.That(hit, Is.False);
    }

    [Test]
    public void HoldingInsideStain_DoesNotRegisterAdditionalPasses()
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();
        Assert.That(tracker.TryRegisterPass(true, 10, 1, out _), Is.True);

        Assert.That(tracker.TryRegisterPass(true, 10, 2, out _), Is.False);
        Assert.That(tracker.TryRegisterPass(true, 10, 100, out _), Is.False);
    }

    [Test]
    public void ExitAndReenterDuringToolUse_RegistersNextPass()
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();
        Assert.That(tracker.TryRegisterPass(true, 10, 1, out _), Is.True);
        Assert.That(tracker.TryRegisterPass(true, null, 2, out _), Is.False);

        Assert.That(tracker.TryRegisterPass(true, 10, 3, out _), Is.True);
    }

    [Test]
    public void ReleaseAndPressAgainInsideStain_RegistersNextPass()
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();
        Assert.That(tracker.TryRegisterPass(true, 10, 1, out _), Is.True);
        Assert.That(tracker.TryRegisterPass(false, 10, 2, out _), Is.False);

        Assert.That(tracker.TryRegisterPass(true, 10, 3, out _), Is.True);
    }

    [Test]
    public void MultipleCallbacksInSameFrame_RegisterAtMostOnePass()
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();
        int passes = 0;
        if (tracker.TryRegisterPass(true, 10, 7, out _)) passes++;
        if (tracker.TryRegisterPass(true, 10, 7, out _)) passes++;
        if (tracker.TryRegisterPass(true, 11, 7, out _)) passes++;

        Assert.That(passes, Is.EqualTo(1));
    }

    [TestCase(30)]
    [TestCase(60)]
    [TestCase(144)]
    public void SimulatedFrameRate_DoesNotChangeStrokeResult(int framesPerSecond)
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();
        int passes = 0;
        for (int frame = 0; frame < framesPerSecond; frame++)
        {
            int? hoveredStain = frame >= framesPerSecond / 3 ? 10 : null;
            if (tracker.TryRegisterPass(true, hoveredStain, frame, out _))
            {
                passes++;
            }
        }

        Assert.That(passes, Is.EqualTo(1));
    }

    [Test]
    public void StrokeResult_HasNoScreenPixelDistanceInput()
    {
        CleaningStrokeTracker lowResolutionTracker = new CleaningStrokeTracker();
        CleaningStrokeTracker highResolutionTracker = new CleaningStrokeTracker();

        bool lowResolutionResult = lowResolutionTracker.TryRegisterPass(true, 10, 1, out _);
        bool highResolutionResult = highResolutionTracker.TryRegisterPass(true, 10, 1, out _);

        Assert.That(highResolutionResult, Is.EqualTo(lowResolutionResult));
    }

    [Test]
    public void ResultGate_SuccessCanBeEmittedOnlyOnce()
    {
        CleaningResultGate gate = new CleaningResultGate();

        Assert.That(gate.TrySet(MinigameResult.Pass), Is.True);
        Assert.That(gate.TrySet(MinigameResult.Pass), Is.False);
        Assert.That(gate.Result, Is.EqualTo(MinigameResult.Pass));
    }

    [Test]
    public void ResultGate_TimeoutCanBeEmittedOnlyOnce()
    {
        CleaningResultGate gate = new CleaningResultGate();

        Assert.That(gate.TrySet(MinigameResult.Fail), Is.True);
        Assert.That(gate.TrySet(MinigameResult.Fail), Is.False);
        Assert.That(gate.Result, Is.EqualTo(MinigameResult.Fail));
    }

    [Test]
    public void ResultGate_PreventsSuccessAndTimeoutDoubleResult()
    {
        CleaningResultGate gate = new CleaningResultGate();

        Assert.That(gate.TrySet(MinigameResult.Fail), Is.True);
        Assert.That(gate.TrySet(MinigameResult.Pass), Is.False);
        Assert.That(gate.Result, Is.EqualTo(MinigameResult.Fail));
    }

    [Test]
    public void OutcomeAtTimeoutBoundary_PreservesExistingTimeoutPriority()
    {
        MinigameResult result = CleaningOutcomeRules.Resolve(
            timeExpired: true,
            allStainsClean: true);

        Assert.That(result, Is.EqualTo(MinigameResult.Fail));
    }

    [Test]
    public void FinalPassBeforeTimeout_ResolvesAsSuccess()
    {
        MinigameResult result = CleaningOutcomeRules.Resolve(
            timeExpired: false,
            allStainsClean: true);

        Assert.That(result, Is.EqualTo(MinigameResult.Pass));
    }

    [Test]
    public void StrokeReset_ClearsCancelStateAndAllowsCleanRestart()
    {
        CleaningStrokeTracker tracker = new CleaningStrokeTracker();
        tracker.TryRegisterPass(true, 10, 1, out _);

        tracker.Reset();

        Assert.That(tracker.LatchedStainId, Is.Null);
        Assert.That(tracker.IsTrackingToolUse, Is.False);
        Assert.That(tracker.TryRegisterPass(true, 10, 1, out _), Is.True);
    }

    [Test]
    public void RestartedStain_HasNoOldProgress()
    {
        CleaningStainProgress previousSession = new CleaningStainProgress(ToolType.Chemical);
        previousSession.TryRegisterPass();
        CleaningStainProgress restartedSession = new CleaningStainProgress(ToolType.Chemical);

        Assert.That(restartedSession.CompletedPasses, Is.Zero);
        Assert.That(restartedSession.Progress01, Is.Zero);
    }

    [TestCase(-1, 6, 0f)]
    [TestCase(0, 0, 0f)]
    [TestCase(3, 6, 0.5f)]
    [TestCase(6, 6, 1f)]
    [TestCase(99, 6, 1f)]
    public void OverallProgress_IsFiniteAndClamped(int completed, int required, float expected)
    {
        float progress = CleaningProgressMath.CalculateOverallProgress(completed, required);

        Assert.That(progress, Is.EqualTo(expected));
        Assert.That(float.IsNaN(progress), Is.False);
        Assert.That(float.IsInfinity(progress), Is.False);
        Assert.That(progress, Is.InRange(0f, 1f));
    }

    [Test]
    public void EmptyOrInvalidSurfaceSetup_IsRejectedWithoutException()
    {
        Assert.DoesNotThrow(() =>
        {
            bool created = CleaningSpawnRules.TryCreateSurfaceStainCounts(0, null, out int[] counts);
            Assert.That(created, Is.False);
            Assert.That(counts, Is.Empty);
        });
    }
}
