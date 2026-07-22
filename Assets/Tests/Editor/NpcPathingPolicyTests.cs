using Game.Systems;
using NUnit.Framework;
using UnityEngine;

public sealed class NpcPathingPolicyTests
{
    [Test]
    public void Chase_DoesNotRepathEveryFrame()
    {
        NpcRepathScheduler scheduler = RecordSuccessfulAttempt();

        Assert.That(scheduler.CanRequest(Request(time: 0.01f, frame: 2, destination: new Vector3(1f, 0f, 0f))), Is.False);
    }

    [Test]
    public void Repath_IsAllowedAfterIntervalWhenPathNeedsRepair()
    {
        NpcRepathScheduler scheduler = RecordSuccessfulAttempt();

        Assert.That(scheduler.CanRequest(Request(time: 0.25f, frame: 2, pathNeedsRepair: true)), Is.False);
        Assert.That(scheduler.CanRequest(Request(time: 0.35f, frame: 3, pathNeedsRepair: true)), Is.True);
    }

    [Test]
    public void SignificantTargetMovement_AllowsRepathAfterInterval()
    {
        NpcRepathScheduler scheduler = RecordSuccessfulAttempt();

        Assert.That(scheduler.CanRequest(Request(time: 0.25f, frame: 2, destination: new Vector3(0.35f, 0f, 0f))), Is.True);
    }

    [Test]
    public void SmallTargetMovement_DoesNotRequestRepath()
    {
        NpcRepathScheduler scheduler = RecordSuccessfulAttempt();

        Assert.That(scheduler.CanRequest(Request(time: 1f, frame: 2, destination: new Vector3(0.2f, 0f, 0f))), Is.False);
    }

    [Test]
    public void Traversal_BlocksRepath()
    {
        Assert.That(new NpcRepathScheduler().CanRequest(Request(traversalActive: true)), Is.False);
    }

    [Test]
    public void DisabledAgent_BlocksRepath()
    {
        Assert.That(new NpcRepathScheduler().CanRequest(Request(agentEnabled: false)), Is.False);
    }

    [Test]
    public void AgentOutsideNavMesh_BlocksRepath()
    {
        Assert.That(new NpcRepathScheduler().CanRequest(Request(agentOnNavMesh: false)), Is.False);
    }

    [Test]
    public void NonAuthoritativeInstance_BlocksRepath()
    {
        Assert.That(new NpcRepathScheduler().CanRequest(Request(authoritative: false)), Is.False);
    }

    [Test]
    public void SameFrame_AllowsAtMostOneAttempt()
    {
        NpcRepathScheduler scheduler = new NpcRepathScheduler();
        NpcRepathRequest request = Request(frame: 10);
        Assert.That(scheduler.CanRequest(request), Is.True);
        Assert.That(scheduler.RecordAttempt(request, false), Is.True);

        Assert.That(scheduler.CanRequest(request), Is.False);
        Assert.That(scheduler.RecordAttempt(request, false), Is.False);
        Assert.That(scheduler.ConsecutiveFailures, Is.EqualTo(1));
    }

    [Test]
    public void ValidDestination_IsAccepted()
    {
        Assert.That(NpcPathingPolicy.IsValidDestination(new Vector3(1f, 2f, 3f)), Is.True);
    }

    [Test]
    public void NaNDestination_IsRejected()
    {
        Assert.That(NpcPathingPolicy.IsValidDestination(new Vector3(float.NaN, 0f, 0f)), Is.False);
    }

    [Test]
    public void InfiniteDestination_IsRejected()
    {
        Assert.That(NpcPathingPolicy.IsValidDestination(new Vector3(0f, float.PositiveInfinity, 0f)), Is.False);
    }

    [Test]
    public void UnchangedDestination_IsNotResentWithoutRepairNeed()
    {
        NpcRepathScheduler scheduler = RecordSuccessfulAttempt();

        Assert.That(scheduler.CanRequest(Request(time: 10f, frame: 2)), Is.False);
    }

    [Test]
    public void FailedAttempt_IncrementsFailureOnce()
    {
        NpcRepathScheduler scheduler = new NpcRepathScheduler();
        NpcRepathRequest request = Request(frame: 3);

        Assert.That(scheduler.RecordAttempt(request, false), Is.True);
        Assert.That(scheduler.RecordAttempt(request, false), Is.False);
        Assert.That(scheduler.ConsecutiveFailures, Is.EqualTo(1));
    }

    [Test]
    public void FailedAttempt_DoesNotRetryBeforeInterval()
    {
        NpcRepathScheduler scheduler = new NpcRepathScheduler();
        scheduler.RecordAttempt(Request(frame: 1), false);

        Assert.That(scheduler.CanRequest(Request(time: 0.34f, frame: 2)), Is.False);
        Assert.That(scheduler.CanRequest(Request(time: 0.35f, frame: 3)), Is.True);
    }

    [Test]
    public void CompletePath_DoesNotTriggerRecovery()
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        tracker.Tick(10f, true, NpcPathState.Complete, false);

        Assert.That(tracker.ShouldRecoverToRoaming(), Is.False);
    }

    [Test]
    public void PartialPath_StartsControlledTimeout()
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        tracker.Tick(0.5f, true, NpcPathState.Partial, false);

        Assert.That(tracker.PathIssueElapsed, Is.EqualTo(0.5f));
        Assert.That(tracker.ShouldRecoverToRoaming(), Is.False);
    }

    [Test]
    public void InvalidPath_StartsControlledRecovery()
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        tracker.Tick(0.5f, true, NpcPathState.Invalid, false);

        Assert.That(tracker.PathIssueElapsed, Is.EqualTo(0.5f));
    }

    [TestCase(NpcPathState.Partial)]
    [TestCase(NpcPathState.Invalid)]
    public void BrokenPath_LeavesChaseAfterTimeout(NpcPathState state)
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        tracker.Tick(NpcPathingPolicy.PathIssueTimeoutSeconds, true, state, false);

        Assert.That(tracker.ShouldRecoverToRoaming(), Is.True);
        Assert.That(tracker.TryIssueRecovery(), Is.True);
    }

    [Test]
    public void TargetReacquired_ResetsFailureAndTimeoutState()
    {
        NpcPathRecoveryTracker tracker = FailedRecoveryTracker();

        tracker.ResetForTargetAcquired();

        Assert.That(tracker.ConsecutiveFailures, Is.Zero);
        Assert.That(tracker.PathIssueElapsed, Is.Zero);
        Assert.That(tracker.TargetUnavailableElapsed, Is.Zero);
        Assert.That(tracker.ShouldRecoverToRoaming(), Is.False);
    }

    [Test]
    public void ReachingLastKnownPosition_AllowsCallerToRecover()
    {
        Assert.That(NpcPathingPolicy.IsArrival(Arrival()), Is.True);
    }

    [Test]
    public void UnavailableTarget_RecoversAfterHardTimeout()
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        tracker.Tick(NpcPathingPolicy.UnavailableTargetTimeoutSeconds, false, NpcPathState.Complete, false);

        Assert.That(tracker.TryIssueRecovery(), Is.True);
    }

    [Test]
    public void MissingTarget_DoesNotThrow()
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        Assert.DoesNotThrow(() => tracker.Tick(0.1f, false, NpcPathState.None, false));
    }

    [Test]
    public void NewTarget_RestoresNormalChaseState()
    {
        NpcPathRecoveryTracker tracker = FailedRecoveryTracker();
        tracker.ResetForTargetAcquired();

        Assert.That(tracker.TryIssueRecovery(), Is.False);
    }

    [Test]
    public void RecoveryTransition_IsIssuedOnlyOnce()
    {
        NpcPathRecoveryTracker tracker = FailedRecoveryTracker();

        Assert.That(tracker.TryIssueRecovery(), Is.True);
        Assert.That(tracker.TryIssueRecovery(), Is.False);
    }

    [Test]
    public void MaximumFailures_TriggersRecovery()
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        for (int i = 0; i < NpcPathingPolicy.MaximumConsecutiveFailures; i++)
        {
            tracker.RecordDestinationFailure();
        }

        Assert.That(tracker.ShouldRecoverToRoaming(), Is.True);
    }

    [Test]
    public void Roaming_DoesNotSelectWhileTargetExists()
    {
        Assert.That(new NpcRoamingRetryState().CanSelect(1f, false, true), Is.False);
    }

    [Test]
    public void Roaming_ValidTargetCanBeRecorded()
    {
        NpcRoamingRetryState state = new NpcRoamingRetryState();
        state.RecordSuccess();

        Assert.That(state.CanSelect(0f, false, false), Is.True);
    }

    [Test]
    public void Roaming_InvalidTargetWaitsBeforeRetry()
    {
        NpcRoamingRetryState state = new NpcRoamingRetryState();
        state.RecordFailure(1f);

        Assert.That(state.CanSelect(1.49f, false, false), Is.False);
        Assert.That(state.CanSelect(1.5f, false, false), Is.True);
    }

    [Test]
    public void Roaming_TargetAttemptsAreBounded()
    {
        Assert.That(NpcPathingPolicy.MaximumRoamTargetAttempts, Is.EqualTo(12));
    }

    [Test]
    public void FailedRoamingSelection_DoesNotLoopImmediately()
    {
        NpcRoamingRetryState state = new NpcRoamingRetryState();
        state.RecordFailure(2f);

        Assert.That(state.CanSelect(2f, false, false), Is.False);
    }

    [Test]
    public void Roaming_AfterArrivalWaitsWhileOldTargetIsMarkedActive()
    {
        Assert.That(new NpcRoamingRetryState().CanSelect(5f, false, true), Is.False);
    }

    [Test]
    public void Traversal_BlocksRoamingSelection()
    {
        Assert.That(new NpcRoamingRetryState().CanSelect(5f, true, false), Is.False);
    }

    [Test]
    public void PathPending_IsNotArrival()
    {
        Assert.That(NpcPathingPolicy.IsArrival(Arrival(pathPending: true)), Is.False);
    }

    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NaN)]
    public void NonFiniteRemainingDistance_IsNotArrival(float remainingDistance)
    {
        Assert.That(NpcPathingPolicy.IsArrival(Arrival(remainingDistance: remainingDistance)), Is.False);
    }

    [Test]
    public void SmallFiniteRemainingDistance_IsArrival()
    {
        Assert.That(NpcPathingPolicy.IsArrival(Arrival(remainingDistance: 0.4f, stoppingDistance: 0.35f)), Is.True);
    }

    [TestCase(NpcPathState.Invalid)]
    [TestCase(NpcPathState.Partial)]
    public void BrokenPath_IsNotArrival(NpcPathState state)
    {
        Assert.That(NpcPathingPolicy.IsArrival(Arrival(pathState: state)), Is.False);
    }

    [Test]
    public void Traversal_PausesRecoveryTimers()
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        tracker.Tick(10f, false, NpcPathState.Invalid, true);

        Assert.That(tracker.PathIssueElapsed, Is.Zero);
        Assert.That(tracker.TargetUnavailableElapsed, Is.Zero);
    }

    [Test]
    public void AuthorityLoss_ResetClearsSchedulerState()
    {
        NpcRepathScheduler scheduler = RecordSuccessfulAttempt();
        scheduler.Reset();

        Assert.That(scheduler.HasAttempt, Is.False);
        Assert.That(scheduler.ConsecutiveFailures, Is.Zero);
    }

    [Test]
    public void Despawn_ResetClearsRecoveryState()
    {
        NpcPathRecoveryTracker tracker = FailedRecoveryTracker();
        tracker.Reset();

        Assert.That(tracker.ShouldRecoverToRoaming(), Is.False);
    }

    [Test]
    public void DisableEnable_DiscardsOldPendingDestination()
    {
        NpcRepathScheduler scheduler = RecordSuccessfulAttempt();
        scheduler.Reset();

        Assert.That(scheduler.CanRequest(Request(destination: new Vector3(5f, 0f, 0f))), Is.True);
    }

    [Test]
    public void CompletedTraversal_CanInvalidateAndAllowImmediateRepath()
    {
        NpcRepathScheduler scheduler = RecordSuccessfulAttempt();
        scheduler.InvalidateDestination();

        Assert.That(scheduler.CanRequest(Request(time: 0.01f, frame: 2)), Is.True);
    }

    private static NpcRepathScheduler RecordSuccessfulAttempt()
    {
        NpcRepathScheduler scheduler = new NpcRepathScheduler();
        NpcRepathRequest request = Request();
        Assert.That(scheduler.RecordAttempt(request, true), Is.True);
        return scheduler;
    }

    private static NpcPathRecoveryTracker FailedRecoveryTracker()
    {
        NpcPathRecoveryTracker tracker = new NpcPathRecoveryTracker();
        for (int i = 0; i < NpcPathingPolicy.MaximumConsecutiveFailures; i++)
        {
            tracker.RecordDestinationFailure();
        }

        return tracker;
    }

    private static NpcRepathRequest Request(
        bool agentExists = true,
        bool agentEnabled = true,
        bool agentOnNavMesh = true,
        bool authoritative = true,
        bool movementAllowed = true,
        bool traversalActive = false,
        bool pathNeedsRepair = false,
        Vector3? destination = null,
        float time = 0f,
        int frame = 1)
    {
        return new NpcRepathRequest(
            agentExists,
            agentEnabled,
            agentOnNavMesh,
            authoritative,
            movementAllowed,
            traversalActive,
            pathNeedsRepair,
            destination ?? Vector3.zero,
            time,
            frame);
    }

    private static NpcArrivalSnapshot Arrival(
        bool pathPending = false,
        bool hasPath = false,
        NpcPathState pathState = NpcPathState.Complete,
        float remainingDistance = 0.35f,
        float stoppingDistance = 0.35f,
        float velocity = 0f)
    {
        return new NpcArrivalSnapshot(
            true,
            true,
            true,
            pathPending,
            hasPath,
            pathState,
            remainingDistance,
            stoppingDistance,
            velocity);
    }
}
