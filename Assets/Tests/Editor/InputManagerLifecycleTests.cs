using System.Reflection;
using Game.Input;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

public class InputManagerLifecycleTests
{
    private GameObject _gameObject;
    private InputManager _inputManager;
    private InputActionAsset _inputActionAsset;

    [SetUp]
    public void SetUp()
    {
        ResetSingleton();
        _inputActionAsset = CreateInputActionAsset();

        _gameObject = new GameObject("InputManagerLifecycleTests");
        _gameObject.SetActive(false);
        _inputManager = _gameObject.AddComponent<InputManager>();
        SetInputActionAsset(_inputManager, _inputActionAsset);
        InvokePrivate(_inputManager, "Awake");
    }

    [TearDown]
    public void TearDown()
    {
        if (_gameObject != null)
        {
            Object.DestroyImmediate(_gameObject);
        }

        if (_inputActionAsset != null)
        {
            _inputActionAsset.Disable();
            Object.DestroyImmediate(_inputActionAsset);
        }

        ResetSingleton();
    }

    [Test]
    public void DisableAndReenable_MaintainsExactlyOneCallbackPerAction()
    {
        InputAction jumpAction = _inputActionAsset.FindAction("Player/Jump");
        InputAction slotAction = _inputActionAsset.FindAction("Player/SelectSlot3");
        InputAction cancelAction = _inputActionAsset.FindAction("UI/Cancel");

        InvokePrivate(_inputManager, "OnEnable");
        Assert.That(GetPerformedCallbackCount(jumpAction), Is.EqualTo(1));
        Assert.That(GetPerformedCallbackCount(slotAction), Is.EqualTo(1));
        Assert.That(GetPerformedCallbackCount(cancelAction), Is.EqualTo(1));

        InvokePrivate(_inputManager, "OnEnable");
        Assert.That(GetPerformedCallbackCount(jumpAction), Is.EqualTo(1));
        Assert.That(GetPerformedCallbackCount(slotAction), Is.EqualTo(1));
        Assert.That(GetPerformedCallbackCount(cancelAction), Is.EqualTo(1));

        InvokePrivate(_inputManager, "OnDisable");
        Assert.That(GetPerformedCallbackCount(jumpAction), Is.Zero);
        Assert.That(GetPerformedCallbackCount(slotAction), Is.Zero);
        Assert.That(GetPerformedCallbackCount(cancelAction), Is.Zero);

        for (int cycle = 0; cycle < 3; cycle++)
        {
            InvokePrivate(_inputManager, "OnEnable");
            Assert.That(GetPerformedCallbackCount(jumpAction), Is.EqualTo(1));
            Assert.That(GetPerformedCallbackCount(slotAction), Is.EqualTo(1));
            Assert.That(GetPerformedCallbackCount(cancelAction), Is.EqualTo(1));

            InvokePrivate(_inputManager, "OnDisable");
            Assert.That(GetPerformedCallbackCount(jumpAction), Is.Zero);
            Assert.That(GetPerformedCallbackCount(slotAction), Is.Zero);
            Assert.That(GetPerformedCallbackCount(cancelAction), Is.Zero);
        }
    }

    [Test]
    public void DestroyedManager_RemovesAllActionCallbacks()
    {
        InputAction jumpAction = _inputActionAsset.FindAction("Player/Jump");
        InputAction slotAction = _inputActionAsset.FindAction("Player/SelectSlot3");

        InvokePrivate(_inputManager, "OnEnable");
        Assert.That(GetPerformedCallbackCount(jumpAction), Is.EqualTo(1));
        Assert.That(GetPerformedCallbackCount(slotAction), Is.EqualTo(1));

        InvokePrivate(_inputManager, "OnDestroy");
        Assert.That(GetPerformedCallbackCount(jumpAction), Is.Zero);
        Assert.That(GetPerformedCallbackCount(slotAction), Is.Zero);

        Object.DestroyImmediate(_gameObject);
        _gameObject = null;

        Assert.That(InputManager.Instance, Is.Null);
    }

    private static InputActionAsset CreateInputActionAsset()
    {
        var asset = ScriptableObject.CreateInstance<InputActionAsset>();
        InputActionMap player = asset.AddActionMap("Player");
        player.AddAction("Move", InputActionType.Value);
        player.AddAction("Look", InputActionType.Value);
        player.AddAction("Jump", InputActionType.Button, "<Keyboard>/space");
        player.AddAction("Interact", InputActionType.Button, "<Keyboard>/e");
        player.AddAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
        player.AddAction("Crouch", InputActionType.Button, "<Keyboard>/c");

        for (int i = 0; i < 9; i++)
        {
            player.AddAction(
                $"SelectSlot{i + 1}",
                InputActionType.Button,
                $"<Keyboard>/digit{i + 1}");
        }

        InputActionMap ui = asset.AddActionMap("UI");
        ui.AddAction("Cancel", InputActionType.Button, "<Keyboard>/escape");
        return asset;
    }

    private static void SetInputActionAsset(InputManager manager, InputActionAsset asset)
    {
        FieldInfo field = typeof(InputManager).GetField(
            "_inputActionAsset",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(field, Is.Not.Null);
        field.SetValue(manager, asset);
    }

    private static void InvokePrivate(InputManager manager, string methodName)
    {
        MethodInfo method = typeof(InputManager).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(method, Is.Not.Null);
        method.Invoke(manager, null);
    }

    private static int GetPerformedCallbackCount(InputAction action)
    {
        FieldInfo callbackField = typeof(InputAction).GetField(
            "m_OnPerformed",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(callbackField, Is.Not.Null);
        object callbackArray = callbackField.GetValue(action);
        PropertyInfo lengthProperty = callbackArray.GetType().GetProperty(
            "length",
            BindingFlags.Instance | BindingFlags.Public);

        Assert.That(lengthProperty, Is.Not.Null);
        return (int)lengthProperty.GetValue(callbackArray);
    }

    private static void ResetSingleton()
    {
        FieldInfo field = typeof(InputManager).GetField(
            "_instance",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.That(field, Is.Not.Null);
        field.SetValue(null, null);
    }
}
