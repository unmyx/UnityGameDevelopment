using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Core;
using Game.Core.Events;
using Game.Interaction;
using Game.Inventory;
using Game.Minigames;
using Game.Player;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class SceneBoundMinigameBindingTests
{
    private const string GameplayScenePath = "Assets/Scenes/GameplayScene.unity";
    private readonly List<GameObject> _transientObjects = new List<GameObject>();

    [TearDown]
    public void TearDown()
    {
        for (int i = _transientObjects.Count - 1; i >= 0; i--)
        {
            if (_transientObjects[i] != null)
            {
                UnityEngine.Object.DestroyImmediate(_transientObjects[i]);
            }
        }

        _transientObjects.Clear();
    }

    [Test]
    public void GameplayScene_CleaningInstanceSatisfiesRequiredSceneBindingContract()
    {
        WithGameplayScene(scene =>
        {
            PipeInteractable interactable = FindUniqueComponent<PipeInteractable>(scene);

            Assert.That(interactable.ValidateSceneBindings(out string reason), Is.True, reason);
        });
    }

    [Test]
    public void GameplayScene_WeldingInstanceSatisfiesRequiredSceneBindingContract()
    {
        WithGameplayScene(scene =>
        {
            WeldingInteractable interactable = FindUniqueComponent<WeldingInteractable>(scene);

            Assert.That(interactable.ValidateSceneBindings(out string reason), Is.True, reason);
        });
    }

    [Test]
    public void CleaningToolText_IsOptionalForSceneBindingContract()
    {
        WithGameplayScene(scene =>
        {
            PipeInteractable interactable = FindUniqueComponent<PipeInteractable>(scene);
            ClearObjectReference(interactable, "_toolText");

            Assert.That(interactable.ValidateSceneBindings(out string reason), Is.True, reason);
        });
    }

    [TestCase("_cleaningCanvas")]
    [TestCase("_timerText")]
    [TestCase("_progressText")]
    public void CleaningMissingRequiredUiBinding_IsRejected(string fieldName)
    {
        WithGameplayScene(scene =>
        {
            PipeInteractable interactable = FindUniqueComponent<PipeInteractable>(scene);
            ClearObjectReference(interactable, fieldName);

            Assert.That(interactable.ValidateSceneBindings(out string reason), Is.False);
            Assert.That(reason, Does.Contain(fieldName));
        });
    }

    [TestCase("_minigameCanvas")]
    [TestCase("_coverageFillImage")]
    [TestCase("_timerText")]
    public void WeldingMissingRequiredUiBinding_IsRejected(string fieldName)
    {
        WithGameplayScene(scene =>
        {
            WeldingInteractable interactable = FindUniqueComponent<WeldingInteractable>(scene);
            ClearObjectReference(interactable, fieldName);

            Assert.That(interactable.ValidateSceneBindings(out string reason), Is.False);
            Assert.That(reason, Does.Contain(fieldName));
        });
    }

    [Test]
    public void CleaningRejectedStart_PreservesStateInputCursorAndInteractableAndClearsToolSnapshot()
    {
        AssertRejectedStart<PipeInteractable>(
            ToolType.Water,
            @"\[PipeInteractable\] Blocking cleaning minigame start.*_cleaningCanvas",
            "Cleaning station is unavailable");
    }

    [Test]
    public void WeldingRejectedStart_PreservesStateInputCursorAndInteractableAndClearsToolSnapshot()
    {
        AssertRejectedStart<WeldingInteractable>(
            ToolType.Electric,
            @"\[WeldingInteractable\] Blocking welding minigame start.*_minigameCanvas",
            "Welding station is unavailable");
    }

    private void AssertRejectedStart<TInteractable>(
        ToolType selectedTool,
        string warningPattern,
        string expectedFeedback)
        where TInteractable : BaseInteractable
    {
        InventorySystem inventory = CreateObject("SceneBinding.Inventory").AddComponent<InventorySystem>();
        InventoryItem tool = LoadToolAsset(selectedTool);
        Assert.That(inventory.AddItemAt(tool, 0, 0, PlayerContextRegistry.DefaultLocalPlayerId), Is.True);

        GameObject playerObject = CreateObject("SceneBinding.Player");
        playerObject.SetActive(false);
        playerObject.AddComponent<CharacterController>();
        PlayerInputHandler inputHandler = playerObject.AddComponent<PlayerInputHandler>();
        PlayerController playerController = playerObject.AddComponent<PlayerController>();
        playerObject.SetActive(true);
        Assert.That(PlayerContextRegistry.RegisterOrUpdate(playerController, PlayerContextRegistry.DefaultLocalPlayerId), Is.True);
        Assert.That(PlayerContextRegistry.TryGetLocalContext(out PlayerContext context), Is.True);
        context.SetSelectedQuickSlotIndex(0);

        GameObject gameManagerObject = CreateObject("SceneBinding.GameManager");
        gameManagerObject.SetActive(false);
        GameManager gameManager = gameManagerObject.AddComponent<GameManager>();
        GameState stateBefore = gameManager.CurrentState;
        bool inputEnabledBefore = inputHandler.enabled;
        bool inputActiveBefore = inputHandler.isActiveAndEnabled;
        CursorLockMode cursorLockBefore = Cursor.lockState;
        bool cursorVisibleBefore = Cursor.visible;

        TInteractable interactable = CreateObject(typeof(TInteractable).Name).AddComponent<TInteractable>();
        int startedEventCount = 0;
        string feedback = null;
        Action<MinigameStartedEvent> startedHandler = _ => startedEventCount++;
        Action<PlayerFeedbackEvent> feedbackHandler = eventData => feedback = eventData.Message;
        EventBus.Subscribe(startedHandler);
        EventBus.Subscribe(feedbackHandler);

        try
        {
            LogAssert.Expect(LogType.Warning, new Regex(warningPattern));
            interactable.Interact();
            interactable.Interact();

            Assert.That(gameManager.CurrentState, Is.EqualTo(stateBefore));
            Assert.That(inputHandler.enabled, Is.EqualTo(inputEnabledBefore));
            Assert.That(inputHandler.isActiveAndEnabled, Is.EqualTo(inputActiveBefore));
            Assert.That(Cursor.lockState, Is.EqualTo(cursorLockBefore));
            Assert.That(Cursor.visible, Is.EqualTo(cursorVisibleBefore));
            Assert.That(interactable.CanInteract, Is.True);
            Assert.That(startedEventCount, Is.Zero);
            Assert.That(feedback, Does.Contain(expectedFeedback));
            Assert.That(GetPrivateField<ToolType>(interactable, "_interactionToolSnapshot"), Is.EqualTo(ToolType.None));
        }
        finally
        {
            EventBus.Unsubscribe(startedHandler);
            EventBus.Unsubscribe(feedbackHandler);
            PlayerContextRegistry.Unregister(playerController, PlayerContextRegistry.DefaultLocalPlayerId);
        }
    }

    private GameObject CreateObject(string name)
    {
        GameObject gameObject = new GameObject(name);
        _transientObjects.Add(gameObject);
        return gameObject;
    }

    private static InventoryItem LoadToolAsset(ToolType toolType)
    {
        string resourceName = toolType == ToolType.Water ? "ToolWater" : "ToolElectric";
        InventoryItem item = Resources.Load<InventoryItem>($"Items/{resourceName}");
        Assert.That(item, Is.Not.Null);
        return item;
    }

    private static void WithGameplayScene(Action<Scene> assertion)
    {
        Scene scene = EditorSceneManager.OpenScene(GameplayScenePath, OpenSceneMode.Additive);
        try
        {
            assertion(scene);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
        }
    }

    private static T FindUniqueComponent<T>(Scene scene) where T : Component
    {
        T[] matches = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true))
            .ToArray();
        Assert.That(matches, Has.Length.EqualTo(1), $"Expected exactly one {typeof(T).Name} in GameplayScene.");
        return matches[0];
    }

    private static void ClearObjectReference(UnityEngine.Object target, string fieldName)
    {
        SerializedObject serializedObject = new SerializedObject(target);
        SerializedProperty property = serializedObject.FindProperty(fieldName);
        Assert.That(property, Is.Not.Null, $"Missing serialized field '{fieldName}'.");
        property.objectReferenceValue = null;
        serializedObject.ApplyModifiedPropertiesWithoutUndo();
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{fieldName}'.");
        return (T)field.GetValue(target);
    }
}
