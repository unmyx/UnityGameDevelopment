using Game.Inventory;
using Game.Minigames;
using NUnit.Framework;

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
}
