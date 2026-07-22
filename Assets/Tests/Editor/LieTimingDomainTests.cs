using Game.Minigames;
using NUnit.Framework;

public class LieTimingDomainTests
{
    [Test]
    public void NormalizeTargetZoneWidth_ValidWidthIsUnchanged()
    {
        Assert.That(LieTimingDomain.NormalizeTargetZoneWidth(0.25f), Is.EqualTo(0.25f));
    }

    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.75f)]
    [TestCase(1f)]
    public void CreateTargetZone_CenterKeepsWholeZoneInsideRange(float sample)
    {
        LieTargetZone zone = LieTimingDomain.CreateTargetZone(0.3f, sample);

        Assert.That(zone.Center, Is.InRange(zone.HalfWidth, 1f - zone.HalfWidth));
        Assert.That(zone.Start, Is.GreaterThanOrEqualTo(0f));
        Assert.That(zone.End, Is.LessThanOrEqualTo(1f));
    }

    [Test]
    public void CreateTargetZone_MinimumSampleProducesLeftmostValidZone()
    {
        LieTargetZone zone = LieTimingDomain.CreateTargetZone(0.2f, 0f);

        Assert.That(zone.Center, Is.EqualTo(0.1f).Within(0.000001f));
        Assert.That(zone.Start, Is.EqualTo(0f).Within(0.000001f));
    }

    [Test]
    public void CreateTargetZone_MaximumSampleProducesRightmostValidZone()
    {
        LieTargetZone zone = LieTimingDomain.CreateTargetZone(0.2f, 1f);

        Assert.That(zone.Center, Is.EqualTo(0.9f).Within(0.000001f));
        Assert.That(zone.End, Is.EqualTo(1f).Within(0.000001f));
    }

    [Test]
    public void NormalizeTargetZoneWidth_NegativeWidthUsesSafeDefault()
    {
        Assert.That(
            LieTimingDomain.NormalizeTargetZoneWidth(-0.2f),
            Is.EqualTo(LieTimingDomain.DefaultTargetZoneWidth));
    }

    [Test]
    public void NormalizeTargetZoneWidth_WidthLargerThanRangeIsClamped()
    {
        Assert.That(LieTimingDomain.NormalizeTargetZoneWidth(2f), Is.EqualTo(1f));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void CreateTargetZone_NonFiniteInputsProduceValidZone(float invalidValue)
    {
        LieTargetZone invalidWidthZone = LieTimingDomain.CreateTargetZone(invalidValue, 0.25f);
        LieTargetZone invalidSampleZone = LieTimingDomain.CreateTargetZone(0.2f, invalidValue);

        AssertValidZone(invalidWidthZone);
        AssertValidZone(invalidSampleZone);
    }

    [Test]
    public void CreateTargetZone_DifferentSamplesCanProduceDifferentSessionZones()
    {
        LieTargetZone first = LieTimingDomain.CreateTargetZone(0.2f, 0.1f);
        LieTargetZone second = LieTimingDomain.CreateTargetZone(0.2f, 0.9f);

        Assert.That(second.Center, Is.Not.EqualTo(first.Center));
    }

    [Test]
    public void StepIndicator_NormalMovementDoesNotChangeDirection()
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(0.2f, 1f, 0.5f, 0.2f);

        Assert.That(state.Position, Is.EqualTo(0.3f).Within(0.000001f));
        Assert.That(state.Direction, Is.EqualTo(1f));
    }

    [Test]
    public void StepIndicator_UpperBoundaryPreservesOvershoot()
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(0.9f, 1f, 1f, 0.18f);

        Assert.That(state.Position, Is.EqualTo(0.92f).Within(0.000001f));
        Assert.That(state.Direction, Is.EqualTo(-1f));
    }

    [Test]
    public void StepIndicator_LowerBoundaryPreservesOvershoot()
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(0.1f, -1f, 1f, 0.18f);

        Assert.That(state.Position, Is.EqualTo(0.08f).Within(0.000001f));
        Assert.That(state.Direction, Is.EqualTo(1f));
    }

    [Test]
    public void StepIndicator_ExactUpperBoundaryReversesDirection()
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(0.9f, 1f, 1f, 0.1f);

        Assert.That(state.Position, Is.EqualTo(1f).Within(0.000001f));
        Assert.That(state.Direction, Is.EqualTo(-1f));
    }

    [Test]
    public void StepIndicator_ExactLowerBoundaryReversesDirection()
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(0.1f, -1f, 1f, 0.1f);

        Assert.That(state.Position, Is.EqualTo(0f).Within(0.000001f));
        Assert.That(state.Direction, Is.EqualTo(1f));
    }

    [Test]
    public void StepIndicator_LargeStepReflectsAcrossMultipleBoundaries()
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(0.25f, 1f, 10f, 1f);

        Assert.That(state.Position, Is.EqualTo(0.25f).Within(0.000001f));
        Assert.That(state.Direction, Is.EqualTo(1f));
    }

    [TestCase(0f, 1f, 1000f, 1000f)]
    [TestCase(1f, -1f, 1000f, 1000f)]
    [TestCase(-5f, 1f, 1f, 1f)]
    [TestCase(5f, -1f, 1f, 1f)]
    public void StepIndicator_ResultAlwaysRemainsInsideRange(
        float position,
        float direction,
        float speed,
        float deltaTime)
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(position, direction, speed, deltaTime);

        Assert.That(state.Position, Is.InRange(0f, 1f));
        Assert.That(state.Direction, Is.EqualTo(-1f).Or.EqualTo(1f));
    }

    [Test]
    public void StepIndicator_ZeroDeltaTimeDoesNotMove()
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(0.4f, -1f, 2f, 0f);

        Assert.That(state.Position, Is.EqualTo(0.4f));
        Assert.That(state.Direction, Is.EqualTo(-1f));
    }

    [Test]
    public void StepIndicator_NegativeDeltaTimeDoesNotMove()
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(0.4f, 1f, 2f, -1f);

        Assert.That(state.Position, Is.EqualTo(0.4f));
        Assert.That(state.Direction, Is.EqualTo(1f));
    }

    [TestCase(float.NaN, 1f, 1f, 1f)]
    [TestCase(float.PositiveInfinity, 1f, 1f, 1f)]
    [TestCase(0.4f, float.NaN, 1f, 1f)]
    [TestCase(0.4f, 1f, float.NaN, 1f)]
    [TestCase(0.4f, 1f, float.PositiveInfinity, 1f)]
    [TestCase(0.4f, 1f, -2f, 1f)]
    [TestCase(0.4f, 1f, 2f, float.NaN)]
    [TestCase(0.4f, 1f, 2f, float.PositiveInfinity)]
    public void StepIndicator_InvalidInputsNeverCorruptState(
        float position,
        float direction,
        float speed,
        float deltaTime)
    {
        LieIndicatorState state = LieTimingDomain.StepIndicator(position, direction, speed, deltaTime);

        Assert.That(float.IsNaN(state.Position) || float.IsInfinity(state.Position), Is.False);
        Assert.That(state.Position, Is.InRange(0f, 1f));
        Assert.That(state.Direction, Is.EqualTo(-1f).Or.EqualTo(1f));
    }

    [Test]
    public void StepIndicator_ThirtySixtyAndOneFortyFourFpsConverge()
    {
        LieIndicatorState atThirty = Simulate(30, 3f, 2.3f);
        LieIndicatorState atSixty = Simulate(60, 3f, 2.3f);
        LieIndicatorState atOneFortyFour = Simulate(144, 3f, 2.3f);

        Assert.That(atThirty.Position, Is.EqualTo(atSixty.Position).Within(0.0001f));
        Assert.That(atThirty.Position, Is.EqualTo(atOneFortyFour.Position).Within(0.0001f));
        Assert.That(atThirty.Direction, Is.EqualTo(atSixty.Direction));
        Assert.That(atThirty.Direction, Is.EqualTo(atOneFortyFour.Direction));
    }

    [Test]
    public void IsIndicatorInZone_CenterIsSuccess()
    {
        LieTargetZone zone = LieTimingDomain.CreateTargetZoneAtCenter(0.5f, 0.2f);

        Assert.That(LieTimingDomain.IsIndicatorInZone(0.5f, zone), Is.True);
    }

    [Test]
    public void IsIndicatorInZone_LeftBoundaryIsSuccess()
    {
        LieTargetZone zone = LieTimingDomain.CreateTargetZoneAtCenter(0.5f, 0.2f);

        Assert.That(LieTimingDomain.IsIndicatorInZone(zone.Start, zone), Is.True);
    }

    [Test]
    public void IsIndicatorInZone_RightBoundaryIsSuccess()
    {
        LieTargetZone zone = LieTimingDomain.CreateTargetZoneAtCenter(0.5f, 0.2f);

        Assert.That(LieTimingDomain.IsIndicatorInZone(zone.End, zone), Is.True);
    }

    [Test]
    public void IsIndicatorInZone_ImmediatelyLeftIsFailure()
    {
        LieTargetZone zone = LieTimingDomain.CreateTargetZoneAtCenter(0.5f, 0.2f);

        Assert.That(LieTimingDomain.IsIndicatorInZone(zone.Start - 0.00001f, zone), Is.False);
    }

    [Test]
    public void IsIndicatorInZone_ImmediatelyRightIsFailure()
    {
        LieTargetZone zone = LieTimingDomain.CreateTargetZoneAtCenter(0.5f, 0.2f);

        Assert.That(LieTimingDomain.IsIndicatorInZone(zone.End + 0.00001f, zone), Is.False);
    }

    private static LieIndicatorState Simulate(int framesPerSecond, float seconds, float speed)
    {
        LieIndicatorState state = new LieIndicatorState(0f, 1f);
        int frameCount = (int)(framesPerSecond * seconds);
        float deltaTime = 1f / framesPerSecond;

        for (int frame = 0; frame < frameCount; frame++)
        {
            state = LieTimingDomain.StepIndicator(state.Position, state.Direction, speed, deltaTime);
        }

        return state;
    }

    private static void AssertValidZone(LieTargetZone zone)
    {
        Assert.That(float.IsNaN(zone.Center) || float.IsInfinity(zone.Center), Is.False);
        Assert.That(zone.Width, Is.GreaterThan(0f).And.LessThanOrEqualTo(1f));
        Assert.That(zone.Start, Is.GreaterThanOrEqualTo(0f));
        Assert.That(zone.End, Is.LessThanOrEqualTo(1f));
    }
}
