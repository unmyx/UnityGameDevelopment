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
}
