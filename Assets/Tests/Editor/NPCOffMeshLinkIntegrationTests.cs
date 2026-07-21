using System.Collections.Generic;
using System.Reflection;
using Game.Networking;
using Game.Systems;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

public sealed class NPCOffMeshLinkIntegrationTests
{
    private readonly List<GameObject> _objects = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (_objects[i] != null)
            {
                NPCController controller = _objects[i].GetComponent<NPCController>();
                if (controller != null)
                {
                    Invoke<object>(controller, "OnDisable");
                }

                Object.DestroyImmediate(_objects[i]);
            }
        }

        _objects.Clear();
    }

    [Test]
    public void Initialization_DisablesAutomaticLinkTraversal()
    {
        NPCController controller = CreateController(out NavMeshAgent agent);

        Assert.That(controller.isActiveAndEnabled, Is.True);
        Assert.That(agent.autoTraverseOffMeshLink, Is.False);
    }

    [Test]
    public void Reenable_RestoresManualTraversalMode()
    {
        NPCController controller = CreateController(out NavMeshAgent agent);
        controller.enabled = false;
        Invoke<object>(controller, "OnDisable");
        agent.autoTraverseOffMeshLink = true;

        controller.enabled = true;
        Invoke<object>(controller, "OnEnable");

        Assert.That(agent.autoTraverseOffMeshLink, Is.False);
    }

    [Test]
    public void AgentOutsideNavMesh_DoesNotStartCoroutine()
    {
        NPCController controller = CreateController(out _);

        Assert.That(Invoke<bool>(controller, "TryStartOffMeshLinkTraversal"), Is.False);
        Assert.That(GetSession(controller).IsActive, Is.False);
        Assert.That(GetField<Coroutine>(controller, "_offMeshLinkTraversalRoutine"), Is.Null);
    }

    [Test]
    public void RepeatedStartRequest_DoesNotReplaceActiveToken()
    {
        NPCController controller = CreateController(out _);
        OffMeshLinkTraversalSession session = BeginTraversal(controller, out uint token);

        Assert.That(Invoke<bool>(controller, "TryStartOffMeshLinkTraversal"), Is.False);
        Assert.That(Invoke<bool>(controller, "TryStartOffMeshLinkTraversal"), Is.False);
        Assert.That(session.ActiveToken, Is.EqualTo(token));
    }

    [Test]
    public void Disable_CancelsTraversalAndInvalidatesToken()
    {
        NPCController controller = CreateController(out _);
        OffMeshLinkTraversalSession session = BeginTraversal(controller, out uint token);

        controller.enabled = false;
        Invoke<object>(controller, "OnDisable");

        Assert.That(session.IsActive, Is.False);
        Assert.That(session.IsCurrent(token), Is.False);
    }

    [Test]
    public void DestroyLifecycle_CancelsTraversalIdempotently()
    {
        NPCController controller = CreateController(out _);
        OffMeshLinkTraversalSession session = BeginTraversal(controller, out _);

        Invoke<object>(controller, "OnDestroy");
        Assert.DoesNotThrow(() => Invoke<object>(controller, "OnDestroy"));

        Assert.That(session.IsActive, Is.False);
    }

    [Test]
    public void AuthorityLoss_CancelsTraversal()
    {
        NPCController controller = CreateController(out _);
        OffMeshLinkTraversalSession session = BeginTraversal(controller, out _);

        Invoke<object>(controller, "HandleMovementAuthorityChanged", false);

        Assert.That(session.IsActive, Is.False);
    }

    [Test]
    public void Reset_CancelsTraversal()
    {
        NPCController controller = CreateController(out _);
        OffMeshLinkTraversalSession session = BeginTraversal(controller, out _);

        controller.ResetAfterMinigame();

        Assert.That(session.IsActive, Is.False);
    }

    [Test]
    public void IdleTransition_CancelsMovementTraversal()
    {
        NPCController controller = CreateController(out _);
        SetField(controller, "_state", NPCController.NPCState.Roaming);
        OffMeshLinkTraversalSession session = BeginTraversal(controller, out _);

        Invoke<object>(controller, "ChangeState", NPCController.NPCState.Idle);

        Assert.That(session.IsActive, Is.False);
    }

    [Test]
    public void RoamingToChasing_UpdatesIntentWithoutCancellingTraversal()
    {
        NPCController controller = CreateController(out _);
        SetField(controller, "_state", NPCController.NPCState.Roaming);
        OffMeshLinkTraversalSession session = BeginTraversal(controller, out uint token);

        Invoke<object>(controller, "ChangeState", NPCController.NPCState.Chasing);

        Assert.That(controller.CurrentNpcState, Is.EqualTo(NPCController.NPCState.Chasing));
        Assert.That(session.IsCurrent(token), Is.True);
    }

    [Test]
    public void DestinationRequest_IsIgnoredDuringTraversal()
    {
        NPCController controller = CreateController(out _);
        BeginTraversal(controller, out _);
        SetField(controller, "_loggedAgentOffNavMesh", false);

        Invoke<object>(controller, "SetAgentDestination", Vector3.one, "Test");

        Assert.That(GetField<bool>(controller, "_loggedAgentOffNavMesh"), Is.False);
    }

    [Test]
    public void Roaming_DoesNotChooseNewTargetDuringTraversal()
    {
        NPCController controller = CreateController(out _);
        BeginTraversal(controller, out _);
        Vector3 expected = new Vector3(2f, 0f, 3f);
        SetField(controller, "_hasRoamTarget", true);
        SetField(controller, "_roamTarget", expected);

        Invoke<object>(controller, "TryPickRoamTarget");

        Assert.That(GetField<bool>(controller, "_hasRoamTarget"), Is.True);
        Assert.That(GetField<Vector3>(controller, "_roamTarget"), Is.EqualTo(expected));
    }

    [Test]
    public void RoamDestinationRefresh_IsBlockedDuringTraversal()
    {
        NPCController controller = CreateController(out _);
        BeginTraversal(controller, out _);

        Assert.That(Invoke<bool>(controller, "ShouldRefreshRoamDestination"), Is.False);
    }

    [Test]
    public void Traversal_DoesNotReportDestinationReached()
    {
        NPCController controller = CreateController(out _);
        BeginTraversal(controller, out _);

        Assert.That(Invoke<bool>(controller, "HasReachedDestination"), Is.False);
    }

    [Test]
    public void Update_DoesNotRunChaseMovementDuringTraversal()
    {
        NPCController controller = CreateController(out _);
        SetField(controller, "_state", NPCController.NPCState.Chasing);
        BeginTraversal(controller, out _);
        SetField(controller, "_hasLastLoggedDestination", false);

        Invoke<object>(controller, "Update");

        Assert.That(GetField<bool>(controller, "_hasLastLoggedDestination"), Is.False);
    }

    [Test]
    public void NetworkDespawn_CancelsTraversalBeforeOfflineFallback()
    {
        NPCController controller = CreateControllerWithBridge(out NetworkNpcAuthorityBridge bridge, out NavMeshAgent agent);
        OffMeshLinkTraversalSession session = BeginTraversal(controller, out uint token);

        bridge.OnNetworkDespawn();

        Assert.That(session.IsCurrent(token), Is.False);
        Assert.That(controller.enabled, Is.True);
        Assert.That(agent.enabled, Is.True);
        Assert.That(agent.autoTraverseOffMeshLink, Is.False);
    }

    [Test]
    public void AuthorityApplication_KeepsAgentInManualMode()
    {
        NPCController controller = CreateControllerWithBridge(out NetworkNpcAuthorityBridge bridge, out NavMeshAgent agent);
        agent.autoTraverseOffMeshLink = true;

        Invoke<object>(bridge, "ApplyAuthorityState");

        Assert.That(controller.enabled, Is.True);
        Assert.That(agent.autoTraverseOffMeshLink, Is.False);
    }

    [Test]
    public void NewController_DoesNotInheritDestroyedControllersToken()
    {
        NPCController first = CreateController(out _);
        OffMeshLinkTraversalSession firstSession = BeginTraversal(first, out uint oldToken);
        Invoke<object>(first, "OnDestroy");
        NPCController second = CreateController(out _);

        Assert.That(firstSession.IsCurrent(oldToken), Is.False);
        Assert.That(GetSession(second).IsActive, Is.False);
        Assert.That(GetSession(second).ActiveToken, Is.Zero);
    }

    private NPCController CreateController(out NavMeshAgent agent)
    {
        GameObject root = Track(new GameObject("NPCOffMeshLinkTest"));
        root.SetActive(false);
        agent = root.AddComponent<NavMeshAgent>();
        agent.autoTraverseOffMeshLink = true;
        NPCController controller = root.AddComponent<NPCController>();
        root.SetActive(true);
        Invoke<object>(controller, "Awake");
        Invoke<object>(controller, "OnEnable");
        return controller;
    }

    private NPCController CreateControllerWithBridge(
        out NetworkNpcAuthorityBridge bridge,
        out NavMeshAgent agent)
    {
        NPCController controller = CreateController(out agent);
        controller.gameObject.SetActive(false);
        bridge = controller.gameObject.AddComponent<NetworkNpcAuthorityBridge>();
        controller.gameObject.SetActive(true);
        Invoke<object>(bridge, "Awake");
        return controller;
    }

    private static OffMeshLinkTraversalSession BeginTraversal(
        NPCController controller,
        out uint token)
    {
        OffMeshLinkTraversalSession session = GetSession(controller);
        Assert.That(session.TryBegin(out token), Is.True);
        Assert.That(session.TryMarkTraversing(token), Is.True);
        return session;
    }

    private GameObject Track(GameObject gameObject)
    {
        _objects.Add(gameObject);
        return gameObject;
    }

    private static OffMeshLinkTraversalSession GetSession(NPCController controller)
    {
        return GetField<OffMeshLinkTraversalSession>(controller, "_offMeshLinkTraversal");
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
