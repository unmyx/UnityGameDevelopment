using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

public sealed class InputBindingConflictTests
{
    private const string InputActionsPath = "Assets/InputSystem_Actions.inputactions";
    private InputActionAsset _runtimeAsset;
    private Keyboard _keyboard;
    private object _inputTestFixture;

    [SetUp]
    public void SetUp()
    {
        Type fixtureType = Type.GetType(
            "UnityEngine.InputSystem.InputTestFixture, Unity.InputSystem.TestFramework",
            throwOnError: true);
        _inputTestFixture = Activator.CreateInstance(fixtureType);
        InvokeFixture("Setup");
    }

    [TearDown]
    public void TearDown()
    {
        if (_runtimeAsset != null)
        {
            _runtimeAsset.Disable();
            UnityEngine.Object.DestroyImmediate(_runtimeAsset);
        }

        if (_keyboard != null && _keyboard.added)
        {
            InputSystem.RemoveDevice(_keyboard);
        }

        InvokeFixture("TearDown");
        _inputTestFixture = null;
    }

    [Test]
    public void QuickSlotsOneThroughNine_HaveExclusiveTopRowNumberBindings()
    {
        InputActionMap player = LoadPlayerMap();

        for (int slotNumber = 1; slotNumber <= 9; slotNumber++)
        {
            string expectedPath = $"<Keyboard>/{slotNumber}";
            InputAction slotAction = player.FindAction($"SelectSlot{slotNumber}", true);

            Assert.That(slotAction.bindings, Has.Some.Matches<InputBinding>(
                binding => binding.path == expectedPath));

            foreach (InputAction action in player.actions)
            {
                if (action == slotAction)
                {
                    continue;
                }

                Assert.That(action.bindings, Has.None.Matches<InputBinding>(
                    binding => binding.path == expectedPath),
                    $"Player/{action.name} conflicts with SelectSlot{slotNumber} on {expectedPath}.");
            }
        }
    }

    [Test]
    public void PreviousAndNext_RetainGamepadBindingsWithoutNumericKeyboardBindings()
    {
        InputActionMap player = LoadPlayerMap();
        InputAction previous = player.FindAction("Previous", true);
        InputAction next = player.FindAction("Next", true);

        Assert.That(previous.bindings, Has.None.Matches<InputBinding>(
            binding => binding.path == "<Keyboard>/1"));
        Assert.That(next.bindings, Has.None.Matches<InputBinding>(
            binding => binding.path == "<Keyboard>/2"));
        Assert.That(previous.bindings, Has.Some.Matches<InputBinding>(
            binding => binding.path == "<Gamepad>/dpad/left"));
        Assert.That(next.bindings, Has.Some.Matches<InputBinding>(
            binding => binding.path == "<Gamepad>/dpad/right"));
    }

    [TestCase(Key.Digit1, "SelectSlot1")]
    [TestCase(Key.Digit2, "SelectSlot2")]
    public void SimulatedNumberPress_PerformsOnlyMatchingQuickSlotAction(Key key, string expectedAction)
    {
        InputActionAsset source = LoadAsset();
        _runtimeAsset = UnityEngine.Object.Instantiate(source);
        _keyboard = InputSystem.AddDevice<Keyboard>();
        _runtimeAsset.devices = new InputDevice[] { _keyboard };
        InputActionMap player = _runtimeAsset.FindActionMap("Player", true);
        var performedActions = new List<string>();

        foreach (InputAction action in player.actions)
        {
            action.performed += context => performedActions.Add(context.action.name);
        }

        player.Enable();
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
        InputSystem.Update();
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
        InputSystem.Update();

        Assert.That(performedActions, Is.EqualTo(new[] { expectedAction }));
    }

    [Test]
    public void WeldingRadiusScrollBinding_RemainsMouseScroll()
    {
        InputActionAsset asset = LoadAsset();
        InputAction scrollAction = asset.FindAction("UI/ScrollWheel", true);

        Assert.That(scrollAction.bindings, Has.Some.Matches<InputBinding>(
            binding => binding.path == "<Mouse>/scroll"));
    }

    private static InputActionMap LoadPlayerMap()
    {
        return LoadAsset().FindActionMap("Player", true);
    }

    private static InputActionAsset LoadAsset()
    {
        InputActionAsset asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
        Assert.That(asset, Is.Not.Null, $"Missing Input Actions asset at '{InputActionsPath}'.");
        return asset;
    }

    private void InvokeFixture(string methodName)
    {
        if (_inputTestFixture == null)
        {
            return;
        }

        MethodInfo method = _inputTestFixture.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public);
        Assert.That(method, Is.Not.Null, $"InputTestFixture.{methodName} was not found.");
        method.Invoke(_inputTestFixture, null);
    }
}
