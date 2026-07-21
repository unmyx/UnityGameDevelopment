using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Input;
using Game.Player;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class PlayerJumpInputTests
{
    private readonly List<GameObject> _objects = new List<GameObject>();

    [SetUp]
    public void SetUp()
    {
        SetStaticField(typeof(PauseManager), "_instance", null);
        SetStaticField(typeof(PauseManager), "_isShuttingDown", false);
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            if (_objects[i] != null)
            {
                Object.DestroyImmediate(_objects[i]);
            }
        }

        _objects.Clear();
        SetStaticField(typeof(PauseManager), "_instance", null);
        SetStaticField(typeof(PauseManager), "_isShuttingDown", false);
    }

    [Test]
    public void OneRegisteredPress_CanBeConsumedOnlyOnce()
    {
        JumpPressLatch latch = new JumpPressLatch();
        latch.RegisterPress(10);

        Assert.That(latch.TryConsume(10), Is.True);
        Assert.That(latch.TryConsume(10), Is.False);
    }

    [Test]
    public void DuplicateCallbacksInSameFrame_CollapseToOneRequest()
    {
        JumpPressLatch latch = new JumpPressLatch();
        latch.RegisterPress(10);
        latch.RegisterPress(10);

        Assert.That(latch.TryConsume(10), Is.True);
        Assert.That(latch.TryConsume(10), Is.False);
    }

    [Test]
    public void UnconsumedPress_ExpiresBeforeLandingFrame()
    {
        JumpPressLatch latch = new JumpPressLatch();
        latch.RegisterPress(10);

        Assert.That(latch.TryConsume(11), Is.False);
        Assert.That(latch.HasPendingPress(11), Is.False);
    }

    [Test]
    public void ReleaseAndNewPress_AllowsAnotherRequest()
    {
        JumpPressLatch latch = new JumpPressLatch();
        latch.RegisterPress(10);
        Assert.That(latch.TryConsume(10), Is.True);

        latch.RegisterPress(12);

        Assert.That(latch.TryConsume(12), Is.True);
    }

    [Test]
    public void Clear_RemovesPendingPressAcrossLifecycleBoundary()
    {
        JumpPressLatch latch = new JumpPressLatch();
        latch.RegisterPress(10);

        latch.Clear();

        Assert.That(latch.TryConsume(10), Is.False);
    }

    [Test]
    public void InputManagerCallback_ProducesOneConsumableEdge()
    {
        GameObject managerObject = Track(new GameObject("JumpInputManager"));
        managerObject.SetActive(false);
        InputManager manager = managerObject.AddComponent<InputManager>();
        int callbackCount = 0;
        manager.OnJump += () => callbackCount++;

        Invoke(manager, "HandleJumpPerformed", default(InputAction.CallbackContext));
        Invoke(manager, "HandleJumpPerformed", default(InputAction.CallbackContext));

        Assert.That(callbackCount, Is.EqualTo(1));
        Assert.That(manager.TryConsumeJumpPress(), Is.True);
        Assert.That(manager.TryConsumeJumpPress(), Is.False);
    }

    [Test]
    public void InputManagerDisable_ClearsPendingPress()
    {
        GameObject managerObject = Track(new GameObject("JumpInputManager"));
        managerObject.SetActive(false);
        InputManager manager = managerObject.AddComponent<InputManager>();
        Invoke(manager, "HandleJumpPerformed", default(InputAction.CallbackContext));

        Invoke(manager, "OnDisable");

        Assert.That(manager.TryConsumeJumpPress(), Is.False);
    }

    [Test]
    public void OnePlayerPress_ProducesOneJump()
    {
        CreateLocalPlayer(out PlayerController controller, out PlayerInputHandler inputHandler);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        SetField(controller, "_timeSinceLastGrounded", 0f);

        Invoke(controller, "HandleInput");

        Assert.That(controller.GetVelocity().y, Is.EqualTo(5f).Within(0.0001f));
        Assert.That(inputHandler.JumpPressed, Is.False);
    }

    [Test]
    public void HeldJumpThroughLanding_DoesNotJumpAgain()
    {
        CreateLocalPlayer(out PlayerController controller, out PlayerInputHandler inputHandler);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        SetField(controller, "_timeSinceLastGrounded", 0f);
        Invoke(controller, "HandleInput");

        SetField(controller, "_velocity", Vector3.zero);
        SetField(controller, "_timeSinceLastGrounded", 0f);
        Invoke(controller, "HandleInput");

        Assert.That(controller.GetVelocity().y, Is.Zero.Within(0.0001f));
    }

    [Test]
    public void NewPressAfterRelease_AllowsNextJump()
    {
        CreateLocalPlayer(out PlayerController controller, out PlayerInputHandler inputHandler);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        SetField(controller, "_timeSinceLastGrounded", 0f);
        Invoke(controller, "HandleInput");

        SetField(controller, "_velocity", Vector3.zero);
        SetField(controller, "_timeSinceLastGrounded", 0f);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        Invoke(controller, "HandleInput");

        Assert.That(controller.GetVelocity().y, Is.EqualTo(5f).Within(0.0001f));
    }

    [Test]
    public void JumpOutsideCoyoteWindow_IsRejected()
    {
        CreateLocalPlayer(out PlayerController controller, out PlayerInputHandler inputHandler);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        SetField(controller, "_timeSinceLastGrounded", 0.2f);

        Invoke(controller, "HandleInput");

        Assert.That(controller.GetVelocity().y, Is.Zero.Within(0.0001f));
    }

    [Test]
    public void ExistingCoyoteWindow_RemainsAvailable()
    {
        CreateLocalPlayer(out PlayerController controller, out PlayerInputHandler inputHandler);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        SetField(controller, "_timeSinceLastGrounded", 0.05f);

        Invoke(controller, "HandleInput");

        Assert.That(controller.GetVelocity().y, Is.EqualTo(5f).Within(0.0001f));
    }

    [Test]
    public void JumpDuringPause_IsConsumedAndIgnored()
    {
        CreateLocalPlayer(out _, out PlayerInputHandler inputHandler);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        PauseManager pauseManager = CreateInactiveComponent<PauseManager>("JumpPauseManager");
        SetField(pauseManager, "_isPaused", true);
        SetStaticField(typeof(PauseManager), "_instance", pauseManager);

        Assert.That(inputHandler.ConsumeJumpPress(), Is.False);
        Assert.That(inputHandler.JumpPressed, Is.False);
    }

    [Test]
    public void JumpDuringMinigamePresentation_IsConsumedAndIgnored()
    {
        CreateLocalPlayer(out _, out PlayerInputHandler inputHandler);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        Assert.That(PlayerContextLocator.TryGetLocalContext(out PlayerContext context), Is.True);
        context.PresentationState.SetLocalMode(LocalPlayerPresentationMode.Minigame);

        Assert.That(inputHandler.ConsumeJumpPress(), Is.False);
        Assert.That(inputHandler.JumpPressed, Is.False);
    }

    [Test]
    public void NonOwnerCannotConsumeJumpPress()
    {
        PlayerInputHandler nonOwner = CreateInactiveComponent<PlayerInputHandler>("NonOwnerJumpHandler");
        SetField(nonOwner, "<JumpPressed>k__BackingField", true);

        Assert.That(nonOwner.ConsumeJumpPress(), Is.False);
        Assert.That(nonOwner.JumpPressed, Is.False);
    }

    [Test]
    public void DisableAndReenable_DoesNotReplayOldPress()
    {
        CreateLocalPlayer(out _, out PlayerInputHandler inputHandler);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);

        Invoke(inputHandler, "OnDisable");
        Invoke(inputHandler, "OnEnable");

        Assert.That(inputHandler.ConsumeJumpPress(), Is.False);
    }

    [Test]
    public void ReturningFromMinigame_DoesNotReplayIgnoredPress()
    {
        CreateLocalPlayer(out _, out PlayerInputHandler inputHandler);
        Assert.That(PlayerContextLocator.TryGetLocalContext(out PlayerContext context), Is.True);
        context.PresentationState.SetLocalMode(LocalPlayerPresentationMode.Minigame);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);
        Assert.That(inputHandler.ConsumeJumpPress(), Is.False);

        context.PresentationState.SetLocalMode(LocalPlayerPresentationMode.FreePlay);

        Assert.That(inputHandler.ConsumeJumpPress(), Is.False);
    }

    [Test]
    public void JumpFromCrouch_PreservesExistingAllowedBehavior()
    {
        CreateLocalPlayer(out PlayerController controller, out PlayerInputHandler inputHandler);
        SetField(controller, "_isCrouching", true);
        SetField(controller, "_timeSinceLastGrounded", 0f);
        SetField(inputHandler, "<JumpPressed>k__BackingField", true);

        Invoke(controller, "HandleInput");

        Assert.That(controller.GetVelocity().y, Is.EqualTo(5f).Within(0.0001f));
        Assert.That(controller.IsCrouching(), Is.True);
    }

    private void CreateLocalPlayer(
        out PlayerController controller,
        out PlayerInputHandler inputHandler)
    {
        GameObject player = Track(new GameObject("JumpTestLocalPlayer"));
        player.SetActive(false);
        CharacterController capsule = player.AddComponent<CharacterController>();
        capsule.height = 1.8f;
        capsule.radius = 0.33f;
        capsule.center = new Vector3(0f, 0.9f, 0f);
        inputHandler = player.AddComponent<PlayerInputHandler>();
        controller = player.AddComponent<PlayerController>();
        player.SetActive(true);
        Invoke(controller, "InitializeComponents");
        PlayerContextRegistry.RegisterOrUpdate(inputHandler, PlayerContextRegistry.DefaultLocalPlayerId);
        Assert.That(PlayerContextRegistry.TryGetLocalContext(out PlayerContext context), Is.True);
        context.PresentationState.SetLocalMode(LocalPlayerPresentationMode.FreePlay);
    }

    private T CreateInactiveComponent<T>(string name) where T : MonoBehaviour
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

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{name}'.");
        field.SetValue(target, value);
    }

    private static void SetStaticField(System.Type type, string name, object value)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing static field '{name}'.");
        field.SetValue(null, value);
    }

    private static void Invoke(object target, string name, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing method '{name}'.");
        method.Invoke(target, arguments);
    }
}
