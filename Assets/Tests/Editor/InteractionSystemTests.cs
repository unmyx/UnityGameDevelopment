using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Interaction;
using Game.Networking;
using Game.Player;
using NUnit.Framework;
using Unity.Netcode;
using UnityEngine;

public sealed class InteractionSystemTests
{
    private readonly List<GameObject> _objects = new List<GameObject>();
    private PauseManager _pauseManager;
    private GameManager _gameManager;

    [SetUp]
    public void SetUp()
    {
        ResetPlayerContextStatics();
        ResetStaticField(typeof(PauseManager), "_instance", null);
        ResetStaticField(typeof(PauseManager), "_isShuttingDown", false);
        ResetStaticField(typeof(GameManager), "_instance", null);

        _pauseManager = CreateInactiveComponent<PauseManager>("InteractionTestPauseManager");
        _gameManager = CreateInactiveComponent<GameManager>("InteractionTestGameManager");
        SetPrivateField(_gameManager, "_currentState", GameState.FreePlay);
        ResetStaticField(typeof(PauseManager), "_instance", _pauseManager);
        ResetStaticField(typeof(GameManager), "_instance", _gameManager);
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (_objects[i] != null)
            {
                UnityEngine.Object.DestroyImmediate(_objects[i]);
            }
        }

        _objects.Clear();
        ResetStaticField(typeof(PauseManager), "_instance", null);
        ResetStaticField(typeof(PauseManager), "_isShuttingDown", false);
        ResetStaticField(typeof(GameManager), "_instance", null);
        ResetPlayerContextStatics();
    }

    [Test]
    public void ColliderAndInteractableOnSameObject_Resolve()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("SameObject", 2f);

        Assert.That(TryResolve(4f, ~0, out InteractionTarget target), Is.True);
        Assert.That(target.Interactable, Is.SameAs(interactable));
        Assert.That(target.HitCollider, Is.SameAs(interactable.GetComponent<Collider>()));
    }

    [Test]
    public void ChildCollider_ResolvesParentInteractable()
    {
        InteractionTestInteractable interactable = CreateChildColliderInteractable("ParentTarget", 2f, 1);

        Assert.That(TryResolve(4f, ~0, out InteractionTarget target), Is.True);
        Assert.That(target.Interactable, Is.SameAs(interactable));
        Assert.That(target.HitCollider.transform.parent, Is.SameAs(interactable.transform));
    }

    [Test]
    public void MultipleChildColliders_ResolveOneParentAndExecuteOnce()
    {
        InteractionTestInteractable interactable = CreateChildColliderInteractable("MultiCollider", 2f, 2);
        InteractionSystem system = CreateInteractionSystem();

        QueueAndConsume(system);

        Assert.That(interactable.InteractCount, Is.EqualTo(1));
    }

    [Test]
    public void ColliderWithoutInteractableParent_DoesNotResolve()
    {
        CreateBox("Blocker", new Vector3(0f, 0f, 2f), false);

        Assert.That(TryResolve(4f, ~0, out _), Is.False);
    }

    [Test]
    public void DisabledInteractable_IsNotValidTarget()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("Disabled", 2f);
        interactable.enabled = false;

        Assert.That(TryResolve(4f, ~0, out _), Is.False);
    }

    [Test]
    public void DestroyedCachedTarget_IsSafelyCleared()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("Destroyed", 2f);
        InteractionSystem system = CreateInteractionSystem();
        RefreshTarget(system);
        Assert.That(system.GetCurrentInteractable(), Is.SameAs(interactable));

        UnityEngine.Object.DestroyImmediate(interactable.gameObject);
        RefreshTarget(system);

        Assert.That(system.GetCurrentInteractable(), Is.Null);
        Assert.That(system.CanInteractWithCurrent(), Is.False);
    }

    [Test]
    public void NearestPhysicalHit_DeterminesResult()
    {
        InteractionTestInteractable nearest = CreatePhysicalInteractable("Nearest", 2f);
        CreatePhysicalInteractable("Farther", 3.5f);

        Assert.That(TryResolve(5f, ~0, out InteractionTarget target), Is.True);
        Assert.That(target.Interactable, Is.SameAs(nearest));
    }

    [Test]
    public void WallInFrontOfInteractable_BlocksTarget()
    {
        CreateBox("Wall", new Vector3(0f, 0f, 2f), false);
        CreatePhysicalInteractable("BehindWall", 4f);

        Assert.That(TryResolve(6f, ~0, out _), Is.False);
    }

    [Test]
    public void InteractableInFrontOfWall_Resolves()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("InFront", 2f);
        CreateBox("Wall", new Vector3(0f, 0f, 4f), false);

        Assert.That(TryResolve(6f, ~0, out InteractionTarget target), Is.True);
        Assert.That(target.Interactable, Is.SameAs(interactable));
    }

    [Test]
    public void InteractableOutsideDistance_DoesNotResolve()
    {
        CreatePhysicalInteractable("TooFar", 5f);

        Assert.That(TryResolve(4f, ~0, out _), Is.False);
    }

    [Test]
    public void TargetMovedOutsideDistanceBeforeConsume_DoesNotExecute()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("MovedAway", 2f);
        InteractionSystem system = CreateInteractionSystem();
        Queue(system);
        interactable.transform.position = new Vector3(0f, 0f, 8f);
        Physics.SyncTransforms();

        Consume(system);

        Assert.That(interactable.InteractCount, Is.Zero);
    }

    [Test]
    public void CameraTurnedBeforeConsume_DoesNotExecuteStaleTarget()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("TurnedAway", 2f);
        InteractionSystem system = CreateInteractionSystem();
        Queue(system);
        system.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        Physics.SyncTransforms();

        Consume(system);

        Assert.That(interactable.InteractCount, Is.Zero);
    }

    [Test]
    public void CameraTeleportedBeforeConsume_DoesNotExecuteStaleTarget()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("TeleportedAway", 2f);
        InteractionSystem system = CreateInteractionSystem();
        Queue(system);
        system.transform.position = new Vector3(20f, 0f, 0f);
        Physics.SyncTransforms();

        Consume(system);

        Assert.That(interactable.InteractCount, Is.Zero);
    }

    [Test]
    public void OneInput_ExecutesExactlyOnce()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("OnePress", 2f);
        InteractionSystem system = CreateInteractionSystem();

        QueueAndConsume(system);

        Assert.That(interactable.InteractCount, Is.EqualTo(1));
    }

    [Test]
    public void RepeatedHeldCallbacksInSameFrame_DoNotRepeatInteraction()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("Held", 2f);
        InteractionSystem system = CreateInteractionSystem();
        QueueAndConsume(system);

        QueueAndConsume(system);

        Assert.That(interactable.InteractCount, Is.EqualTo(1));
    }

    [Test]
    public void MultipleCallbacksBeforeConsume_ExecuteOnce()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("DuplicateCallbacks", 2f);
        InteractionSystem system = CreateInteractionSystem();
        Queue(system);
        Queue(system);

        Consume(system);

        Assert.That(interactable.InteractCount, Is.EqualTo(1));
    }

    [Test]
    public void InputDuringMinigameState_IsIgnored()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("MinigameBlocked", 2f);
        InteractionSystem system = CreateInteractionSystem();
        SetPrivateField(_gameManager, "_currentState", GameState.Minigame);

        QueueAndConsume(system);

        Assert.That(interactable.InteractCount, Is.Zero);
        Assert.That(system.GetCurrentInteractable(), Is.Null);
    }

    [Test]
    public void InputDuringPause_IsIgnored()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("PauseBlocked", 2f);
        InteractionSystem system = CreateInteractionSystem();
        SetPrivateField(_pauseManager, "_isPaused", true);

        QueueAndConsume(system);

        Assert.That(interactable.InteractCount, Is.Zero);
        Assert.That(system.GetCurrentInteractable(), Is.Null);
    }

    [Test]
    public void InputAfterReturningToFreePlay_WorksAgain()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("ReturnFreePlay", 2f);
        InteractionSystem system = CreateInteractionSystem();
        SetPrivateField(_gameManager, "_currentState", GameState.Minigame);
        QueueAndConsume(system);
        SetPrivateField(_gameManager, "_currentState", GameState.FreePlay);
        SetPrivateField(system, "_lastInteractInputFrame", -1);

        QueueAndConsume(system);

        Assert.That(interactable.InteractCount, Is.EqualTo(1));
    }

    [Test]
    public void ValidTarget_UpdatesPromptState()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("Prompt", 2f);
        InteractionSystem system = CreateInteractionSystem();

        RefreshTarget(system);

        Assert.That(system.GetCurrentInteractable(), Is.SameAs(interactable));
        Assert.That(system.CanInteractWithCurrent(), Is.True);
        Assert.That(interactable.EnterCount, Is.EqualTo(1));
    }

    [Test]
    public void LookingAway_ClearsPromptState()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("LookAway", 2f);
        InteractionSystem system = CreateInteractionSystem();
        RefreshTarget(system);
        system.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

        RefreshTarget(system);

        Assert.That(system.CanInteractWithCurrent(), Is.False);
        Assert.That(interactable.ExitCount, Is.EqualTo(1));
    }

    [Test]
    public void MovingOutsideRange_ClearsPromptState()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("PromptOutOfRange", 2f);
        InteractionSystem system = CreateInteractionSystem();
        RefreshTarget(system);
        interactable.transform.position = new Vector3(0f, 0f, 8f);
        Physics.SyncTransforms();

        RefreshTarget(system);

        Assert.That(system.CanInteractWithCurrent(), Is.False);
        Assert.That(interactable.ExitCount, Is.EqualTo(1));
    }

    [Test]
    public void WallInsertedBetweenCameraAndTarget_ClearsPromptState()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("OccludedPrompt", 4f);
        InteractionSystem system = CreateInteractionSystem();
        RefreshTarget(system);
        CreateBox("InsertedWall", new Vector3(0f, 0f, 2f), false);
        Physics.SyncTransforms();

        RefreshTarget(system);

        Assert.That(system.CanInteractWithCurrent(), Is.False);
        Assert.That(interactable.ExitCount, Is.EqualTo(1));
    }

    [Test]
    public void SwitchingFromTargetAToB_UpdatesPromptLifecycle()
    {
        InteractionTestInteractable first = CreatePhysicalInteractable("TargetA", 2f);
        InteractionTestInteractable second = CreatePhysicalInteractable("TargetB", 4f);
        InteractionSystem system = CreateInteractionSystem();
        RefreshTarget(system);
        first.transform.position = new Vector3(5f, 0f, 2f);
        Physics.SyncTransforms();

        RefreshTarget(system);

        Assert.That(first.ExitCount, Is.EqualTo(1));
        Assert.That(second.EnterCount, Is.EqualTo(1));
        Assert.That(system.GetCurrentInteractable(), Is.SameAs(second));
    }

    [Test]
    public void MissingPromptUi_DoesNotPreventGameplayInteraction()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("NoHud", 2f);
        InteractionSystem system = CreateInteractionSystem();

        Assert.DoesNotThrow(() => QueueAndConsume(system));
        Assert.That(interactable.InteractCount, Is.EqualTo(1));
    }

    [Test]
    public void ExplicitLayerMask_RestrictsHits()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("MaskedOut", 2f);
        interactable.gameObject.layer = 8;
        Physics.SyncTransforms();

        Assert.That(TryResolve(4f, 1 << 9, out _), Is.False);
        Assert.That(TryResolve(4f, 1 << 8, out _), Is.True);
    }

    [Test]
    public void EmptyLayerMask_UsesBackwardCompatibleFallback()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("FallbackMask", 2f);

        Assert.That(TryResolve(4f, 0, out InteractionTarget target), Is.True);
        Assert.That(target.Interactable, Is.SameAs(interactable));
        Assert.That(InteractionTargetResolver.GetEffectiveLayerMask(0), Is.EqualTo(Physics.AllLayers));
    }

    [Test]
    public void AuxiliaryTrigger_DoesNotBlockPhysicalInteractable()
    {
        CreateBox("HelperTrigger", new Vector3(0f, 0f, 1f), true);
        InteractionTestInteractable interactable = CreatePhysicalInteractable("PhysicalTarget", 3f);

        Assert.That(TryResolve(5f, ~0, out InteractionTarget target), Is.True);
        Assert.That(target.Interactable, Is.SameAs(interactable));
    }

    [Test]
    public void ExistingTriggerOnlyPickup_RemainsResolvable()
    {
        GameObject pickupObject = CreateBox("TriggerOnlyPickup", new Vector3(0f, 0f, 2f), true);
        InteractableItem pickup = pickupObject.AddComponent<InteractableItem>();

        Assert.That(TryResolve(4f, ~0, out InteractionTarget target), Is.True);
        Assert.That(target.Interactable, Is.SameAs(pickup));
        Assert.That(target.HitCollider.isTrigger, Is.True);
    }

    [Test]
    public void NonOwnerInteractionSystem_DoesNotQualifyForLocalInput()
    {
        InteractionSystem first = CreateInteractionSystem("FirstPlayer");
        InteractionSystem second = CreateInteractionSystem("SecondPlayer");
        Assert.That(PlayerContextRegistry.RegisterOrUpdate(first, "interaction_test_remote"), Is.True);
        Assert.That(PlayerContextRegistry.RegisterOrUpdate(second, "interaction_test_local"), Is.True);
        Assert.That(PlayerContextRegistry.TrySetLocalPlayerId("interaction_test_local"), Is.True);

        Assert.That(InvokePrivate<bool>(first, "IsLocallyOwnedInteractionSystem"), Is.False);
        Assert.That(InvokePrivate<bool>(second, "IsLocallyOwnedInteractionSystem"), Is.True);
    }

    [Test]
    public void LocalOwnerRequest_ExecutesOnlyOnce()
    {
        InteractionTestInteractable interactable = CreatePhysicalInteractable("OwnerTarget", 2f);
        InteractionSystem system = CreateInteractionSystem();
        Assert.That(InvokePrivate<bool>(system, "IsLocallyOwnedInteractionSystem"), Is.True);

        Queue(system);
        Queue(system);
        Consume(system);

        Assert.That(interactable.InteractCount, Is.EqualTo(1));
    }

    [Test]
    public void NetworkBridge_ServerApprovalRpcRemainsEnabled()
    {
        MethodInfo rpc = typeof(NetworkInteractionAuthorityBridge).GetMethod(
            "RequestInteractionServerRpc",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(rpc, Is.Not.Null);
        ServerRpcAttribute attribute = rpc.GetCustomAttribute<ServerRpcAttribute>();
        Assert.That(attribute, Is.Not.Null);
        Assert.That(attribute.RequireOwnership, Is.False);
    }

    [TestCase(0f)]
    [TestCase(-1f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void InvalidDistance_DoesNotProduceTarget(float distance)
    {
        CreatePhysicalInteractable("InvalidDistance", 2f);

        Assert.That(TryResolve(distance, ~0, out _), Is.False);
    }

    [Test]
    public void SetInvalidDistance_RestoresExistingGameplayDefault()
    {
        InteractionSystem system = CreateInteractionSystem();

        system.SetRaycastRange(float.NaN);

        Assert.That(system.GetRaycastRange(), Is.EqualTo(4f));
    }

    private bool TryResolve(float distance, LayerMask mask, out InteractionTarget target)
    {
        Physics.SyncTransforms();
        return InteractionTargetResolver.TryFindInteractable(
            new Ray(Vector3.zero, Vector3.forward),
            distance,
            mask,
            out target);
    }

    private InteractionTestInteractable CreatePhysicalInteractable(string name, float z)
    {
        GameObject gameObject = CreateBox(name, new Vector3(0f, 0f, z), false);
        return gameObject.AddComponent<InteractionTestInteractable>();
    }

    private InteractionTestInteractable CreateChildColliderInteractable(string name, float z, int colliderCount)
    {
        GameObject root = Track(new GameObject(name));
        root.transform.position = new Vector3(0f, 0f, z);
        InteractionTestInteractable interactable = root.AddComponent<InteractionTestInteractable>();
        for (int i = 0; i < colliderCount; i++)
        {
            GameObject child = new GameObject($"Collider_{i}");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(i * 0.1f, 0f, 0f);
            child.AddComponent<BoxCollider>();
        }

        Physics.SyncTransforms();
        return interactable;
    }

    private GameObject CreateBox(string name, Vector3 position, bool isTrigger)
    {
        GameObject gameObject = Track(new GameObject(name));
        gameObject.transform.position = position;
        BoxCollider collider = gameObject.AddComponent<BoxCollider>();
        collider.isTrigger = isTrigger;
        Physics.SyncTransforms();
        return gameObject;
    }

    private InteractionSystem CreateInteractionSystem(string name = "InteractionSystemUnderTest")
    {
        GameObject gameObject = Track(new GameObject(name));
        gameObject.SetActive(false);
        Camera camera = gameObject.AddComponent<Camera>();
        camera.enabled = true;
        InteractionSystem system = gameObject.AddComponent<InteractionSystem>();
        SetPrivateField(system, "_raycastCamera", camera);
        SetPrivateField(system, "_enableInteractionLogs", false);
        SetPrivateField(system, "_showDebugRay", false);
        system.SetRaycastRange(4f);
        gameObject.SetActive(true);
        Physics.SyncTransforms();
        return system;
    }

    private T CreateInactiveComponent<T>(string name) where T : Component
    {
        GameObject gameObject = Track(new GameObject(name));
        gameObject.SetActive(false);
        return gameObject.AddComponent<T>();
    }

    private GameObject Track(GameObject gameObject)
    {
        _objects.Add(gameObject);
        return gameObject;
    }

    private static void QueueAndConsume(InteractionSystem system)
    {
        Queue(system);
        Consume(system);
    }

    private static void Queue(InteractionSystem system)
    {
        InvokePrivate<object>(system, "OnInteractPerformed");
    }

    private static void Consume(InteractionSystem system)
    {
        InvokePrivate<object>(system, "HandleQueuedInteractRequest");
    }

    private static void RefreshTarget(InteractionSystem system)
    {
        Physics.SyncTransforms();
        InvokePrivate<object>(system, "UpdateRaycastInteraction");
    }

    private static T InvokePrivate<T>(object target, string methodName)
    {
        MethodInfo method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing method {methodName}.");
        object result = method.Invoke(target, null);
        return result == null ? default : (T)result;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field {fieldName}.");
        field.SetValue(target, value);
    }

    private static void ResetStaticField(Type type, string fieldName, object value)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing static field {type.Name}.{fieldName}.");
        field.SetValue(null, value);
    }

    private static void ResetPlayerContextStatics()
    {
        FieldInfo contextsField = typeof(PlayerContextRegistry).GetField(
            "ContextsById",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(contextsField, Is.Not.Null);
        object contexts = contextsField.GetValue(null);
        MethodInfo clear = contexts.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
        Assert.That(clear, Is.Not.Null);
        clear.Invoke(contexts, null);

        ResetStaticField(
            typeof(PlayerContextRegistry),
            "_localPlayerId",
            PlayerContextRegistry.DefaultLocalPlayerId);
        ResetStaticField(typeof(PlayerContextLocator), "_soloFallbackContext", null);
        ResetStaticField(typeof(PlayerContextLocator), "_startupRealtime", -1f);
        ResetStaticField(typeof(PlayerContextLocator), "_hasResolvedLocalContext", false);
    }
}

public sealed class InteractionTestInteractable : BaseInteractable
{
    public int InteractCount { get; private set; }
    public int EnterCount { get; private set; }
    public int ExitCount { get; private set; }

    public override void Interact()
    {
        InteractCount++;
    }

    public override void OnInteractableEnter()
    {
        EnterCount++;
    }

    public override void OnInteractableExit()
    {
        ExitCount++;
    }
}
