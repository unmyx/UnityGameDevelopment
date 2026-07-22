using System.Collections.Generic;
using System.Reflection;
using Game.Systems;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

public sealed class NpcPathingIntegrationTests
{
    private readonly List<GameObject> _objects = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            GameObject gameObject = _objects[i];
            if (gameObject == null)
            {
                continue;
            }

            NPCController controller = gameObject.GetComponent<NPCController>();
            if (controller != null)
            {
                Invoke<object>(controller, "OnDisable");
            }

            Object.DestroyImmediate(gameObject);
        }

        _objects.Clear();
    }

    [Test]
    public void InvalidDestination_DoesNotCreatePendingAttempt()
    {
        NPCController controller = CreateController(out _);
        SetField(controller, "_state", NPCController.NPCState.Chasing);

        Assert.That(
            Invoke<bool>(controller, "TrySetAgentDestination", new Vector3(float.NaN, 0f, 0f), "Test", false),
            Is.False);
        Assert.That(GetScheduler(controller).HasAttempt, Is.False);
    }

    [Test]
    public void AgentOutsideNavMesh_DoesNotCreatePendingAttempt()
    {
        NPCController controller = CreateController(out _);
        SetField(controller, "_state", NPCController.NPCState.Chasing);

        Assert.That(Invoke<bool>(controller, "TrySetAgentDestination", Vector3.one, "Test", true), Is.False);
        Assert.That(GetScheduler(controller).HasAttempt, Is.False);
    }

    [Test]
    public void ActiveTraversal_BlocksDestinationRequest()
    {
        NPCController controller = CreateController(out _);
        SetField(controller, "_state", NPCController.NPCState.Chasing);
        BeginTraversal(controller);

        Assert.That(Invoke<bool>(controller, "TrySetAgentDestination", Vector3.one, "Test", true), Is.False);
        Assert.That(GetScheduler(controller).HasAttempt, Is.False);
    }

    [Test]
    public void ArrivalOutsideNavMesh_IsFalse()
    {
        NPCController controller = CreateController(out _);

        Assert.That(Invoke<bool>(controller, "HasReachedDestination"), Is.False);
    }

    [Test]
    public void FailedRoamingSelection_EntersControlledRetry()
    {
        NPCController controller = CreateController(out _);
        SetField(controller, "_state", NPCController.NPCState.Roaming);

        Assert.That(Invoke<bool>(controller, "TryPickRoamTarget"), Is.False);
        NpcRoamingRetryState retry = GetField<NpcRoamingRetryState>(controller, "_roamingRetryState");
        Assert.That(retry.CanSelect(Time.time, false, false), Is.False);
    }

    [Test]
    public void Disable_ClearsRepathRecoveryAndRoamingState()
    {
        NPCController controller = CreateController(out _);
        PopulatePathingState(controller);

        Invoke<object>(controller, "OnDisable");

        AssertPathingStateReset(controller);
    }

    [Test]
    public void AuthorityLoss_ClearsRepathRecoveryAndRoamingState()
    {
        NPCController controller = CreateController(out _);
        PopulatePathingState(controller);

        Invoke<object>(controller, "HandleMovementAuthorityChanged", false);

        AssertPathingStateReset(controller);
    }

    [Test]
    public void ResetAfterMinigame_ClearsPendingDestination()
    {
        NPCController controller = CreateController(out _);
        PopulatePathingState(controller);

        controller.ResetAfterMinigame();

        AssertPathingStateReset(controller);
    }

    [Test]
    public void StateTransition_DiscardsPreviousStatesDestination()
    {
        NPCController controller = CreateController(out _);
        SetField(controller, "_state", NPCController.NPCState.Roaming);
        PopulatePathingState(controller);

        Invoke<object>(controller, "ChangeState", NPCController.NPCState.Chasing);

        Assert.That(controller.CurrentNpcState, Is.EqualTo(NPCController.NPCState.Chasing));
        Assert.That(GetScheduler(controller).HasAttempt, Is.False);
    }

    [Test]
    public void CompletedTraversal_AllowsImmediateFreshRepath()
    {
        NPCController controller = CreateController(out _);
        NpcRepathScheduler scheduler = GetScheduler(controller);
        scheduler.RecordAttempt(Request(Vector3.zero), true);
        SetField(controller, "_wasOffMeshLinkTraversalActive", true);

        Invoke<object>(controller, "HandleCompletedOffMeshLinkTraversal");

        Assert.That(scheduler.HasAttempt, Is.False);
        Assert.That(GetField<bool>(controller, "_wasOffMeshLinkTraversalActive"), Is.False);
    }

    [Test]
    public void TraversalCompletion_DoesNotInvalidateWhileTokenIsActive()
    {
        NPCController controller = CreateController(out _);
        NpcRepathScheduler scheduler = GetScheduler(controller);
        scheduler.RecordAttempt(Request(Vector3.zero), true);
        SetField(controller, "_wasOffMeshLinkTraversalActive", true);
        BeginTraversal(controller);

        Invoke<object>(controller, "HandleCompletedOffMeshLinkTraversal");

        Assert.That(scheduler.HasAttempt, Is.True);
    }

    [Test]
    public void ReadOnlyMovementData_IsSafeWithoutNavMeshPlacement()
    {
        NPCController controller = CreateController(out _);

        Assert.That(controller.CurrentMovementSpeed, Is.Zero);
        Assert.That(controller.IsMoving, Is.False);
    }

    private NPCController CreateController(out NavMeshAgent agent)
    {
        GameObject root = new GameObject("NpcPathingIntegrationTest");
        _objects.Add(root);
        root.SetActive(false);
        agent = root.AddComponent<NavMeshAgent>();
        NPCController controller = root.AddComponent<NPCController>();
        root.SetActive(true);
        Invoke<object>(controller, "Awake");
        Invoke<object>(controller, "OnEnable");
        return controller;
    }

    private static void PopulatePathingState(NPCController controller)
    {
        GetScheduler(controller).RecordAttempt(Request(Vector3.one), false);
        GetField<NpcPathRecoveryTracker>(controller, "_pathRecoveryTracker").RecordDestinationFailure();
        GetField<NpcRoamingRetryState>(controller, "_roamingRetryState").RecordFailure(Time.time);
        SetField(controller, "_roamingPathIssueElapsed", 1f);
    }

    private static void AssertPathingStateReset(NPCController controller)
    {
        Assert.That(GetScheduler(controller).HasAttempt, Is.False);
        Assert.That(GetScheduler(controller).ConsecutiveFailures, Is.Zero);
        Assert.That(
            GetField<NpcPathRecoveryTracker>(controller, "_pathRecoveryTracker").ConsecutiveFailures,
            Is.Zero);
        Assert.That(GetField<float>(controller, "_roamingPathIssueElapsed"), Is.Zero);
        Assert.That(GetField<float>(controller, "_losePlayerTimer"), Is.Zero);
        Assert.That(GetField<bool>(controller, "_isSearchingLastKnownPosition"), Is.False);
        Assert.That(GetField<bool>(controller, "_hasLastKnownPlayerPosition"), Is.False);
        Assert.That(GetField<bool>(controller, "_hasRoamTarget"), Is.False);
    }

    private static NpcRepathRequest Request(Vector3 destination)
    {
        return new NpcRepathRequest(
            true,
            true,
            true,
            true,
            true,
            false,
            false,
            destination,
            0f,
            1);
    }

    private static NpcRepathScheduler GetScheduler(NPCController controller)
    {
        return GetField<NpcRepathScheduler>(controller, "_repathScheduler");
    }

    private static void BeginTraversal(NPCController controller)
    {
        OffMeshLinkTraversalSession session =
            GetField<OffMeshLinkTraversalSession>(controller, "_offMeshLinkTraversal");
        Assert.That(session.TryBegin(out uint token), Is.True);
        Assert.That(session.TryMarkTraversing(token), Is.True);
    }

    private static T GetField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{fieldName}'.");
        return (T)field.GetValue(target);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{fieldName}'.");
        field.SetValue(target, value);
    }

    private static T Invoke<T>(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing method '{methodName}'.");
        object result = method.Invoke(target, arguments);
        return result == null ? default : (T)result;
    }
}
