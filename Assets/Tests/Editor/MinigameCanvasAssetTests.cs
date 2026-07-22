using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Interaction;
using Game.Inventory;
using Game.Minigames;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public sealed class MinigameCanvasAssetTests
{
    private const string GameplayScenePath = "Assets/Scenes/GameplayScene.unity";
    private const string HomeScenePath = "Assets/Scenes/HomeScene.unity";
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
    public void GameplayScene_CleaningToolTextIsBoundToPipeAndCanvas()
    {
        WithScene(GameplayScenePath, scene =>
        {
            Canvas canvas = FindUniqueComponentByObjectName<Canvas>(scene, "CleaningCanvas");
            PipeInteractable pipe = FindUniqueComponentByObjectName<PipeInteractable>(scene, "Pipe");
            TextMeshProUGUI toolText = canvas.GetComponentsInChildren<TextMeshProUGUI>(true)
                .Single(component => component.name == "ToolText");

            SerializedProperty property = new SerializedObject(pipe).FindProperty("_toolText");
            Assert.That(property, Is.Not.Null);
            Assert.That(property.objectReferenceValue, Is.SameAs(toolText));
            Assert.That(toolText.transform.parent, Is.SameAs(canvas.transform));
            Assert.That(toolText.raycastTarget, Is.False);
            Assert.That(toolText.text, Is.Empty);
        });
    }

    [Test]
    public void HomeScene_ComputerCanvasStartsHiddenWithoutChangingCursorOrState()
    {
        CursorLockMode lockStateBefore = Cursor.lockState;
        bool cursorVisibleBefore = Cursor.visible;

        WithScene(HomeScenePath, scene =>
        {
            Canvas canvas = FindUniqueComponentByObjectName<Canvas>(scene, "ComputerCanvas");
            ComputerInteractable station = FindUniqueComponentByObjectName<ComputerInteractable>(scene, "ComputerStation");
            SerializedProperty property = new SerializedObject(station).FindProperty("_computerCanvas");

            Assert.That(canvas.gameObject.activeSelf, Is.True);
            Assert.That(canvas.enabled, Is.False);
            Assert.That(canvas.isActiveAndEnabled, Is.False);
            Assert.That(property.objectReferenceValue, Is.SameAs(canvas));
            Assert.That(canvas.GetComponentsInChildren<Graphic>(true).Any(graphic => graphic.raycastTarget), Is.False);
        });

        Assert.That(Cursor.lockState, Is.EqualTo(lockStateBefore));
        Assert.That(Cursor.visible, Is.EqualTo(cursorVisibleBefore));
    }

    [TestCase(ToolType.Water, "Tool: Water")]
    [TestCase(ToolType.Gasoline, "Tool: Gasoline")]
    [TestCase(ToolType.Chemical, "Tool: Chemical")]
    public void CleaningToolText_DisplaysCapturedTool(ToolType tool, string expected)
    {
        CleaningMinigame minigame = CreateCleaningMinigame(out TextMeshProUGUI toolText);
        SetPrivateField(minigame, "_cleaningToolLabel", tool.ToString());

        InvokePrivate(minigame, "UpdateToolUI");

        Assert.That(toolText.text, Is.EqualTo(expected));
    }

    [Test]
    public void CleaningToolText_DoesNotFollowLaterSelectionChange()
    {
        var session = new MinigameToolSession();
        Assert.That(session.TryCapture(ToolType.Water, CleaningMinigame.SupportsTool), Is.True);
        CleaningMinigame minigame = CreateCleaningMinigame(out TextMeshProUGUI toolText);
        SetPrivateField(minigame, "_cleaningToolLabel", session.ActiveTool.ToString());

        ToolType laterSelection = ToolType.Chemical;
        InvokePrivate(minigame, "UpdateToolUI");

        Assert.That(laterSelection, Is.EqualTo(ToolType.Chemical));
        Assert.That(toolText.text, Is.EqualTo("Tool: Water"));
    }

    [Test]
    public void CleaningToolText_NewSessionRefreshesAndTerminalCleanupClearsText()
    {
        CleaningMinigame minigame = CreateCleaningMinigame(out TextMeshProUGUI toolText);
        SetPrivateField(minigame, "_cleaningToolLabel", ToolType.Gasoline.ToString());
        InvokePrivate(minigame, "UpdateToolUI");
        Assert.That(toolText.text, Is.EqualTo("Tool: Gasoline"));

        SetPrivateField(minigame, "_cleaningToolLabel", ToolType.Chemical.ToString());
        InvokePrivate(minigame, "UpdateToolUI");
        Assert.That(toolText.text, Is.EqualTo("Tool: Chemical"));

        InvokePrivate(minigame, "ClearAssignedUiText");
        Assert.That(toolText.text, Is.Empty);
    }

    [Test]
    public void CleaningToolText_NullFallbackRemainsSafe()
    {
        GameObject minigameObject = CreateObject("Cleaning.NullSafe");
        CleaningMinigame minigame = minigameObject.AddComponent<CleaningMinigame>();

        Assert.DoesNotThrow(() => InvokePrivate(minigame, "UpdateToolUI"));
        Assert.DoesNotThrow(() => InvokePrivate(minigame, "ClearAssignedUiText"));
    }

    [Test]
    public void ComputerCanvasVisibility_StartEndAndRestartAreIdempotent()
    {
        ComputerMinigame minigame = CreateComputerMinigame(out Canvas canvas);

        InvokePrivate(minigame, "SetCanvasVisible", true);
        Assert.That(canvas.enabled, Is.True);
        InvokePrivate(minigame, "SetCanvasVisible", false);
        Assert.That(canvas.enabled, Is.False);
        InvokePrivate(minigame, "SetCanvasVisible", false);
        Assert.That(canvas.enabled, Is.False);
        InvokePrivate(minigame, "SetCanvasVisible", true);
        Assert.That(canvas.enabled, Is.True);
    }

    [Test]
    public void ComputerMinigame_DisableAlwaysHidesCanvas()
    {
        ComputerMinigame minigame = CreateComputerMinigame(out Canvas canvas);
        canvas.enabled = true;

        InvokePrivate(minigame, "OnDisable");

        Assert.That(canvas.enabled, Is.False);
    }

    [Test]
    public void ComputerLifecycle_RoutesStartEndAndDisableThroughVisibilityGate()
    {
        string source = File.ReadAllText("Assets/Scripts/Minigames/ComputerMinigame.cs");

        Assert.That(source, Does.Match(@"(?s)protected override void OnStart\(\).*?SetCanvasVisible\(true\);"));
        Assert.That(source, Does.Match(@"(?s)protected override void OnEnd\(\).*?SetCanvasVisible\(false\);"));
        Assert.That(source, Does.Match(@"(?s)private void OnDisable\(\).*?SetCanvasVisible\(false\);"));
    }

    [Test]
    public void CleaningLifecycle_RefreshesAtStartAndClearsOnEndAndDisable()
    {
        string source = File.ReadAllText("Assets/Scripts/Minigames/CleaningMinigame.cs");

        Assert.That(source, Does.Match(@"(?s)protected override void OnStart\(\).*?UpdateToolUI\(\);"));
        Assert.That(source, Does.Match(@"(?s)protected override void OnEnd\(\).*?ClearAssignedUiText\(\);"));
        Assert.That(source, Does.Match(@"(?s)private void OnDisable\(\).*?ClearAssignedUiText\(\);"));
    }

    [Test]
    public void CleaningWorldStainColor_DoesNotInstantiateRendererMaterial()
    {
        CleaningMinigame minigame = CreateObject("Cleaning.MaterialTest").AddComponent<CleaningMinigame>();
        GameObject stainVisual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _transientObjects.Add(stainVisual);
        Renderer renderer = stainVisual.GetComponent<Renderer>();
        Material sharedMaterial = renderer.sharedMaterial;

        Type stainStateType = typeof(CleaningMinigame).GetNestedType(
            "WorldStainState",
            BindingFlags.NonPublic);
        Assert.That(stainStateType, Is.Not.Null);
        object stainState = Activator.CreateInstance(stainStateType);
        FieldInfo rendererField = stainStateType.GetField("MarkerRenderer", BindingFlags.Instance | BindingFlags.Public);
        Assert.That(rendererField, Is.Not.Null);
        rendererField.SetValue(stainState, renderer);

        Color expectedColor = new Color(0.35f, 0.2f, 0.1f, 1f);
        InvokePrivate(minigame, "ApplyWorldStainColor", stainState, expectedColor);

        Assert.That(renderer.sharedMaterial, Is.SameAs(sharedMaterial));
        MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(propertyBlock);
        Color appliedColor = propertyBlock.GetColor(Shader.PropertyToID("_BaseColor"));
        Assert.That(appliedColor.r, Is.EqualTo(expectedColor.r).Within(0.0001f));
        Assert.That(appliedColor.g, Is.EqualTo(expectedColor.g).Within(0.0001f));
        Assert.That(appliedColor.b, Is.EqualTo(expectedColor.b).Within(0.0001f));
        Assert.That(appliedColor.a, Is.EqualTo(expectedColor.a).Within(0.0001f));
    }

    [Test]
    public void CleaningTimerUi_DoesNotReplaceTextUntilDisplayedSecondChanges()
    {
        CleaningMinigame minigame = CreateObject("Cleaning.TimerTest").AddComponent<CleaningMinigame>();
        TextMeshProUGUI timerText = CreateObject(
            "Cleaning.TimerText",
            typeof(RectTransform),
            typeof(CanvasRenderer)).AddComponent<TextMeshProUGUI>();
        SetPrivateField(minigame, "_timerText", timerText);
        SetPrivateField(minigame, "_remainingTimeSeconds", 17.4f);

        InvokePrivate(minigame, "UpdateTimerUI");
        string firstTextInstance = timerText.text;
        InvokePrivate(minigame, "UpdateTimerUI");

        Assert.That(timerText.text, Is.SameAs(firstTextInstance));

        SetPrivateField(minigame, "_remainingTimeSeconds", 16.4f);
        InvokePrivate(minigame, "UpdateTimerUI");
        Assert.That(timerText.text, Is.EqualTo("Time: 17s"));
    }

    [Test]
    public void WeldingMarkerColor_DoesNotInstantiateRendererMaterial()
    {
        WeldingFillMinigame minigame = CreateObject("Welding.MaterialTest").AddComponent<WeldingFillMinigame>();
        GameObject markerVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _transientObjects.Add(markerVisual);
        Renderer renderer = markerVisual.GetComponent<Renderer>();
        Material sharedMaterial = renderer.sharedMaterial;

        InvokePrivate(minigame, "SetMarkerRendererColor", renderer, Color.cyan);

        Assert.That(renderer.sharedMaterial, Is.SameAs(sharedMaterial));
        FieldInfo propertyBlockField = typeof(WeldingFillMinigame).GetField(
            "_markerPropertyBlock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(propertyBlockField, Is.Not.Null);
        Assert.That(propertyBlockField.GetValue(minigame), Is.Not.Null);
    }

    [Test]
    public void LieAttemptsUi_DoesNotReplaceTextUntilAttemptCountChanges()
    {
        LieMinigame lieMinigame = CreateObject("Lie.PerformanceTest").AddComponent<LieMinigame>();
        SetPrivateField(lieMinigame, "_maxAttempts", 3);
        SetPrivateField(lieMinigame, "_attemptsRemaining", 3);

        GameObject uiObject = CreateObject("Lie.UiPerformanceTest");
        uiObject.SetActive(false);
        LieMinigameUI ui = uiObject.AddComponent<LieMinigameUI>();
        TextMeshProUGUI attemptsText = CreateObject(
            "Lie.AttemptsText",
            typeof(RectTransform),
            typeof(CanvasRenderer)).AddComponent<TextMeshProUGUI>();
        SetPrivateField(ui, "_lieMinigame", lieMinigame);
        SetPrivateField(ui, "_attemptsText", attemptsText);

        InvokePrivate(ui, "UpdateAttemptsDisplay");
        string firstTextInstance = attemptsText.text;
        InvokePrivate(ui, "UpdateAttemptsDisplay");

        Assert.That(attemptsText.text, Is.SameAs(firstTextInstance));

        SetPrivateField(lieMinigame, "_attemptsRemaining", 2);
        InvokePrivate(ui, "UpdateAttemptsDisplay");
        Assert.That(attemptsText.text, Is.EqualTo("Attempts: 1/3"));
    }

    private CleaningMinigame CreateCleaningMinigame(out TextMeshProUGUI toolText)
    {
        GameObject minigameObject = CreateObject("Cleaning.Runtime");
        CleaningMinigame minigame = minigameObject.AddComponent<CleaningMinigame>();
        GameObject textObject = CreateObject("ToolText", typeof(RectTransform), typeof(CanvasRenderer));
        toolText = textObject.AddComponent<TextMeshProUGUI>();
        SetPrivateField(minigame, "_toolText", toolText);
        return minigame;
    }

    private ComputerMinigame CreateComputerMinigame(out Canvas canvas)
    {
        GameObject canvasObject = CreateObject("ComputerCanvas", typeof(RectTransform));
        canvas = canvasObject.AddComponent<Canvas>();
        GameObject minigameObject = CreateObject("Computer.Runtime");
        ComputerMinigame minigame = minigameObject.AddComponent<ComputerMinigame>();
        SetPrivateField(minigame, "_computerCanvas", canvas);
        return minigame;
    }

    private GameObject CreateObject(string name, params Type[] components)
    {
        GameObject gameObject = new GameObject(name, components);
        _transientObjects.Add(gameObject);
        return gameObject;
    }

    private static void WithScene(string path, Action<Scene> assertion)
    {
        Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        try
        {
            assertion(scene);
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
        }
    }

    private static T FindUniqueComponentByObjectName<T>(Scene scene, string objectName) where T : Component
    {
        T[] matches = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<T>(true))
            .Where(component => component.name == objectName)
            .ToArray();
        Assert.That(matches, Has.Length.EqualTo(1));
        return matches[0];
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{fieldName}'.");
        field.SetValue(target, value);
    }

    private static void InvokePrivate(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing method '{methodName}'.");
        method.Invoke(target, arguments);
    }
}
