using Game.Systems;
using NUnit.Framework;
using UnityEngine;

public sealed class OffMeshLinkTraversalTests
{
    [Test]
    public void ValidLink_AllowsTraversalAndSelectsOppositeEndpoint()
    {
        OffMeshLinkTraversalSnapshot snapshot = ValidSnapshot();

        Assert.That(
            OffMeshLinkTraversalRules.TryValidateStart(snapshot, false, out Vector3 destination, out OffMeshLinkTraversalFailure failure),
            Is.True);
        Assert.That(failure, Is.EqualTo(OffMeshLinkTraversalFailure.None));
        Assert.That(destination, Is.EqualTo(snapshot.EndPosition));
    }

    [Test]
    public void DisabledAgent_IsRejected()
    {
        AssertFailure(With(agentEnabled: false), OffMeshLinkTraversalFailure.AgentDisabled);
    }

    [Test]
    public void AgentOutsideNavMesh_IsRejected()
    {
        AssertFailure(With(agentOnNavMesh: false), OffMeshLinkTraversalFailure.AgentOffNavMesh);
    }

    [Test]
    public void AgentOutsideOffMeshLink_IsRejected()
    {
        AssertFailure(With(agentOnOffMeshLink: false), OffMeshLinkTraversalFailure.AgentNotOnLink);
    }

    [Test]
    public void InvalidLinkData_IsRejected()
    {
        AssertFailure(With(linkDataValid: false), OffMeshLinkTraversalFailure.InvalidLinkData);
    }

    [Test]
    public void NonFiniteEndpoint_IsRejected()
    {
        AssertFailure(With(endPosition: new Vector3(float.NaN, 0f, 1f)), OffMeshLinkTraversalFailure.InvalidEndpoint);
    }

    [Test]
    public void ZeroDistanceLink_IsRejected()
    {
        AssertFailure(With(endPosition: Vector3.zero), OffMeshLinkTraversalFailure.DegenerateLink);
    }

    [Test]
    public void InactiveComponent_IsRejected()
    {
        AssertFailure(With(componentActive: false), OffMeshLinkTraversalFailure.ComponentInactive);
    }

    [Test]
    public void MissingAgent_IsRejected()
    {
        AssertFailure(With(agentExists: false), OffMeshLinkTraversalFailure.AgentMissing);
    }

    [Test]
    public void MovementBlocked_IsRejectedWithoutChangingSession()
    {
        OffMeshLinkTraversalSession session = new OffMeshLinkTraversalSession();

        AssertFailure(With(movementAllowed: false), OffMeshLinkTraversalFailure.MovementBlocked);
        Assert.That(session.Phase, Is.EqualTo(OffMeshLinkTraversalPhase.Idle));
    }

    [Test]
    public void NonAuthoritativeTraversal_IsRejected()
    {
        AssertFailure(With(authoritative: false), OffMeshLinkTraversalFailure.NotAuthoritative);
    }

    [Test]
    public void ActiveTraversalRequest_IsRejected()
    {
        Assert.That(
            OffMeshLinkTraversalRules.TryValidateStart(ValidSnapshot(), true, out _, out OffMeshLinkTraversalFailure failure),
            Is.False);
        Assert.That(failure, Is.EqualTo(OffMeshLinkTraversalFailure.TraversalAlreadyActive));
    }

    [Test]
    public void NormalDuration_IsFiniteAndPositive()
    {
        Assert.That(OffMeshLinkTraversalRules.TryCalculateDuration(1.75f, 3.5f, out float duration), Is.True);
        Assert.That(duration, Is.EqualTo(0.5f).Within(0.0001f));
    }

    [TestCase(0f)]
    [TestCase(-3.5f)]
    [TestCase(float.NaN)]
    public void NonPositiveOrNaNSpeed_UsesBoundedMinimumSpeed(float speed)
    {
        Assert.That(OffMeshLinkTraversalRules.TryCalculateDuration(1f, speed, out float duration), Is.True);
        Assert.That(duration, Is.EqualTo(OffMeshLinkTraversalRules.MaximumDurationSeconds));
        Assert.That(float.IsInfinity(duration), Is.False);
        Assert.That(float.IsNaN(duration), Is.False);
    }

    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void InfiniteSpeed_IsRejected(float speed)
    {
        Assert.That(OffMeshLinkTraversalRules.TryCalculateDuration(1f, speed, out _), Is.False);
    }

    [Test]
    public void InfiniteDistance_IsRejected()
    {
        Assert.That(OffMeshLinkTraversalRules.TryCalculateDuration(float.PositiveInfinity, 3.5f, out _), Is.False);
    }

    [Test]
    public void Duration_DoesNotFallBelowMinimum()
    {
        Assert.That(OffMeshLinkTraversalRules.TryCalculateDuration(0.02f, 1000f, out float duration), Is.True);
        Assert.That(duration, Is.EqualTo(OffMeshLinkTraversalRules.MinimumDurationSeconds));
    }

    [Test]
    public void Duration_DoesNotExceedMaximum()
    {
        Assert.That(OffMeshLinkTraversalRules.TryCalculateDuration(1000f, 0.1f, out float duration), Is.True);
        Assert.That(duration, Is.EqualTo(OffMeshLinkTraversalRules.MaximumDurationSeconds));
    }

    [Test]
    public void Session_AllowsExactlyOneActiveToken()
    {
        OffMeshLinkTraversalSession session = new OffMeshLinkTraversalSession();

        Assert.That(session.TryBegin(out uint first), Is.True);
        Assert.That(first, Is.Not.Zero);
        Assert.That(session.TryBegin(out uint second), Is.False);
        Assert.That(second, Is.Zero);
        Assert.That(session.ActiveToken, Is.EqualTo(first));
    }

    [Test]
    public void TraversalPhases_AdvanceInOrder()
    {
        OffMeshLinkTraversalSession session = BeginSession(out uint token);

        Assert.That(session.Phase, Is.EqualTo(OffMeshLinkTraversalPhase.Preparing));
        Assert.That(session.TryMarkTraversing(token), Is.True);
        Assert.That(session.Phase, Is.EqualTo(OffMeshLinkTraversalPhase.Traversing));
        Assert.That(session.TryBeginCompletion(token), Is.True);
        Assert.That(session.Phase, Is.EqualTo(OffMeshLinkTraversalPhase.Completing));
        Assert.That(session.TryComplete(token), Is.True);
        Assert.That(session.Phase, Is.EqualTo(OffMeshLinkTraversalPhase.Idle));
    }

    [Test]
    public void Cancel_InvalidatesOldToken()
    {
        OffMeshLinkTraversalSession session = BeginSession(out uint oldToken);

        Assert.That(session.Cancel(), Is.True);
        Assert.That(session.IsCurrent(oldToken), Is.False);
        Assert.That(session.TryBeginCompletion(oldToken), Is.False);
    }

    [Test]
    public void OldToken_CannotCompleteNewTraversal()
    {
        OffMeshLinkTraversalSession session = BeginSession(out uint oldToken);
        session.Cancel();
        Assert.That(session.TryBegin(out uint newToken), Is.True);
        Assert.That(session.TryMarkTraversing(newToken), Is.True);

        Assert.That(session.TryBeginCompletion(oldToken), Is.False);
        Assert.That(session.IsCurrent(newToken), Is.True);
    }

    [Test]
    public void Completion_CanBeClaimedOnlyOnce()
    {
        OffMeshLinkTraversalSession session = BeginSession(out uint token);
        session.TryMarkTraversing(token);

        Assert.That(session.TryBeginCompletion(token), Is.True);
        Assert.That(session.TryBeginCompletion(token), Is.False);
    }

    [Test]
    public void Cleanup_IsIdempotent()
    {
        OffMeshLinkTraversalSession session = BeginSession(out _);

        Assert.That(session.Cancel(), Is.True);
        Assert.That(session.Cancel(), Is.False);
        Assert.That(session.IsActive, Is.False);
    }

    [Test]
    public void NewSpawnSession_DoesNotInheritOldToken()
    {
        OffMeshLinkTraversalSession oldSession = BeginSession(out uint oldToken);
        oldSession.Cancel();
        OffMeshLinkTraversalSession newSession = new OffMeshLinkTraversalSession();

        Assert.That(newSession.IsActive, Is.False);
        Assert.That(newSession.ActiveToken, Is.Zero);
        Assert.That(newSession.IsCurrent(oldToken), Is.False);
    }

    [Test]
    public void ReverseApproach_SelectsStartEndpoint()
    {
        Assert.That(
            OffMeshLinkTraversalRules.TryResolveDestination(
                new Vector3(0f, 0f, 1.9f),
                Vector3.zero,
                new Vector3(0f, 0f, 2f),
                out Vector3 destination),
            Is.True);
        Assert.That(destination, Is.EqualTo(Vector3.zero));
    }

    [Test]
    public void ForwardApproach_SelectsEndEndpoint()
    {
        Assert.That(
            OffMeshLinkTraversalRules.TryResolveDestination(
                new Vector3(0f, 0f, 0.1f),
                Vector3.zero,
                new Vector3(0f, 0f, 2f),
                out Vector3 destination),
            Is.True);
        Assert.That(destination, Is.EqualTo(new Vector3(0f, 0f, 2f)));
    }

    [Test]
    public void Completion_RequiresAgentToRemainOnLink()
    {
        Assert.That(OffMeshLinkTraversalRules.CanComplete(Completion(agentOnOffMeshLink: false)), Is.False);
    }

    [Test]
    public void Completion_RequiresEnabledAgentOnNavMesh()
    {
        Assert.That(OffMeshLinkTraversalRules.CanComplete(Completion(agentEnabled: false)), Is.False);
        Assert.That(OffMeshLinkTraversalRules.CanComplete(Completion(agentOnNavMesh: false)), Is.False);
    }

    [Test]
    public void Completion_RequiresActiveAuthoritativeComponent()
    {
        Assert.That(OffMeshLinkTraversalRules.CanComplete(Completion(componentActive: false)), Is.False);
        Assert.That(OffMeshLinkTraversalRules.CanComplete(Completion(authoritative: false)), Is.False);
        Assert.That(OffMeshLinkTraversalRules.CanComplete(Completion()), Is.True);
    }

    private static OffMeshLinkTraversalSession BeginSession(out uint token)
    {
        OffMeshLinkTraversalSession session = new OffMeshLinkTraversalSession();
        Assert.That(session.TryBegin(out token), Is.True);
        return session;
    }

    private static void AssertFailure(
        OffMeshLinkTraversalSnapshot snapshot,
        OffMeshLinkTraversalFailure expected)
    {
        Assert.That(
            OffMeshLinkTraversalRules.TryValidateStart(snapshot, false, out _, out OffMeshLinkTraversalFailure actual),
            Is.False);
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static OffMeshLinkTraversalSnapshot ValidSnapshot()
    {
        return With();
    }

    private static OffMeshLinkTraversalSnapshot With(
        bool componentActive = true,
        bool agentExists = true,
        bool agentEnabled = true,
        bool agentOnNavMesh = true,
        bool agentOnOffMeshLink = true,
        bool linkDataValid = true,
        bool movementAllowed = true,
        bool authoritative = true,
        Vector3? currentPosition = null,
        Vector3? startPosition = null,
        Vector3? endPosition = null)
    {
        return new OffMeshLinkTraversalSnapshot(
            componentActive,
            agentExists,
            agentEnabled,
            agentOnNavMesh,
            agentOnOffMeshLink,
            linkDataValid,
            movementAllowed,
            authoritative,
            currentPosition ?? Vector3.zero,
            startPosition ?? Vector3.zero,
            endPosition ?? new Vector3(0f, 0f, 2f));
    }

    private static OffMeshLinkCompletionSnapshot Completion(
        bool componentActive = true,
        bool agentExists = true,
        bool agentEnabled = true,
        bool agentOnNavMesh = true,
        bool agentOnOffMeshLink = true,
        bool authoritative = true)
    {
        return new OffMeshLinkCompletionSnapshot(
            componentActive,
            agentExists,
            agentEnabled,
            agentOnNavMesh,
            agentOnOffMeshLink,
            authoritative);
    }
}
