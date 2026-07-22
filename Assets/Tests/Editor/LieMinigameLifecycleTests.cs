using System;
using System.Reflection;
using Game.Minigames;
using NUnit.Framework;
using UnityEngine;

public class LieMinigameLifecycleTests
{
    private GameObject _gameObject;
    private LieMinigame _minigame;

    [SetUp]
    public void SetUp()
    {
        _gameObject = new GameObject("LieMinigameLifecycleTests");
        _minigame = _gameObject.AddComponent<LieMinigame>();
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(_gameObject);
    }

    [Test]
    public void StartTimingPhase_GeneratesZoneFromInjectedRandomSample()
    {
        InitializeTimingSession(0.2f, 3, () => 0f);

        Assert.That(_minigame.GetTargetZoneCenter(), Is.EqualTo(0.1f).Within(0.000001f));
        Assert.That(_minigame.GetTargetZoneStart(), Is.EqualTo(0f).Within(0.000001f));
        Assert.That(_minigame.GetTargetZoneEnd(), Is.EqualTo(0.2f).Within(0.000001f));
    }

    [Test]
    public void Restart_GeneratesTargetZoneFromFreshRandomSample()
    {
        float sample = 0.1f;
        Func<float> samples = () =>
        {
            float current = sample;
            sample = 0.9f;
            return current;
        };

        InitializeTimingSession(0.2f, 3, samples);
        float firstCenter = _minigame.GetTargetZoneCenter();
        InitializeTimingSession(0.2f, 3, samples);

        Assert.That(_minigame.GetTargetZoneCenter(), Is.Not.EqualTo(firstCenter));
    }

    [Test]
    public void ResetForNextAttempt_RetainsTargetZoneAndResetsIndicator()
    {
        InitializeTimingSession(0.2f, 3, () => 0.75f);
        float center = _minigame.GetTargetZoneCenter();
        SetField("_indicatorPosition", 0.7f);
        SetField("_indicatorDirection", -1f);

        Invoke(_minigame, "ResetForNextAttempt");

        Assert.That(_minigame.GetTargetZoneCenter(), Is.EqualTo(center));
        Assert.That(_minigame.GetIndicatorPosition(), Is.EqualTo(0f));
        Assert.That(GetField<float>("_indicatorDirection"), Is.EqualTo(1f));
    }

    [Test]
    public void FailedAttempt_RetainsTargetZoneForFollowingAttempt()
    {
        InitializeTimingSession(0.2f, 2, () => 0.75f);
        float center = _minigame.GetTargetZoneCenter();
        SetField("_indicatorPosition", 0f);

        Assert.That(HandleTrigger(true), Is.True);
        Assert.That(_minigame.GetAttemptsRemaining(), Is.EqualTo(1));
        Assert.That(_minigame.GetTargetZoneCenter(), Is.EqualTo(center));
    }

    [Test]
    public void TriggerCheck_DoesNotMoveTargetZone()
    {
        InitializeTimingSession(0.2f, 3, () => 0.5f);
        float center = _minigame.GetTargetZoneCenter();
        float width = _minigame.GetTargetZoneWidth();
        SetField("_indicatorPosition", center);

        Assert.That(HandleTrigger(true), Is.True);
        Assert.That(_minigame.GetTargetZoneCenter(), Is.EqualTo(center));
        Assert.That(_minigame.GetTargetZoneWidth(), Is.EqualTo(width));
    }

    [Test]
    public void TriggerAtZoneBoundary_SucceedsEvenWhenAccuracyIsZero()
    {
        InitializeTimingSession(0.2f, 3, () => 0.5f);
        SetField("_requireFollowUpConfirm", false);
        SetField("_indicatorPosition", _minigame.GetTargetZoneStart());

        Assert.That(HandleTrigger(true), Is.True);
        Assert.That(_minigame.HasSucceeded(), Is.True);
        Assert.That(_minigame.GetResult(), Is.EqualTo(MinigameResult.Pass));
        Assert.That(_minigame.GetAccuracy(), Is.EqualTo(0f));
    }

    [Test]
    public void SuccessfulTerminalResult_CannotBeRegisteredTwice()
    {
        InitializeTimingSession(0.2f, 3, () => 0.5f);
        SetField("_requireFollowUpConfirm", false);
        SetField("_indicatorPosition", _minigame.GetTargetZoneCenter());

        Assert.That(HandleTrigger(true), Is.True);
        Assert.That(HandleTrigger(true), Is.False);
        Assert.That(_minigame.GetResult(), Is.EqualTo(MinigameResult.Pass));
        Assert.That(_minigame.GetAttemptsRemaining(), Is.EqualTo(3));
    }

    [Test]
    public void FinalFailure_CannotBeRegisteredTwice()
    {
        InitializeTimingSession(0.2f, 1, () => 1f);
        SetField("_requireFollowUpConfirm", false);
        SetField("_indicatorPosition", 0f);

        Assert.That(HandleTrigger(true), Is.True);
        Assert.That(HandleTrigger(true), Is.False);
        Assert.That(_minigame.GetResult(), Is.EqualTo(MinigameResult.Fail));
        Assert.That(_minigame.GetAttemptsRemaining(), Is.EqualTo(0));
    }

    [Test]
    public void End_StopsMovementAndInputAndClearsPendingState()
    {
        InitializeTimingSession(0.2f, 3, () => 0.5f);
        SetField("_pendingFollowUpConfirmRequest", true);

        Invoke(_minigame, "OnEnd");

        Assert.That(GetField<bool>("_isInputEnabled"), Is.False);
        Assert.That(GetField<bool>("_pendingFollowUpConfirmRequest"), Is.False);
        Assert.That(GetField("_targetRandomSampleProvider"), Is.Null);
        Assert.That(HandleTrigger(true), Is.False);
        Assert.That(GetGameState(), Is.EqualTo("Complete"));
    }

    [Test]
    public void Disable_ClearsPendingInputAndPreventsTriggerHandling()
    {
        InitializeTimingSession(0.2f, 3, () => 0.5f);
        SetField("_pendingFollowUpConfirmRequest", true);

        Invoke(_minigame, "OnDisable");

        Assert.That(GetField<bool>("_isInputEnabled"), Is.False);
        Assert.That(GetField<bool>("_pendingFollowUpConfirmRequest"), Is.False);
        Assert.That(GetField("_targetRandomSampleProvider"), Is.Null);
        Assert.That(HandleTrigger(true), Is.False);
    }

    [Test]
    public void Reinitialize_ClearsPreviousSessionState()
    {
        InitializeTimingSession(0.2f, 2, () => 0.25f);
        SetField("_attemptsRemaining", 0);
        SetField("_indicatorPosition", 0.8f);
        SetField("_indicatorDirection", -1f);
        SetField("_hasSucceeded", true);
        SetField("_pendingFollowUpConfirmRequest", true);

        InitializeTimingSession(0.2f, 2, () => 0.75f);

        Assert.That(_minigame.GetAttemptsRemaining(), Is.EqualTo(2));
        Assert.That(_minigame.GetIndicatorPosition(), Is.EqualTo(0f));
        Assert.That(GetField<float>("_indicatorDirection"), Is.EqualTo(1f));
        Assert.That(_minigame.HasSucceeded(), Is.False);
        Assert.That(GetField<bool>("_pendingFollowUpConfirmRequest"), Is.False);
        Assert.That(GetGameState(), Is.EqualTo("WaitingForPress"));
    }

    private void InitializeTimingSession(float width, int attempts, Func<float> randomSampleProvider)
    {
        MinigameData data = new MinigameData();
        data.SetParameter("target_zone_width", width);
        data.SetParameter("max_attempts", attempts);
        _minigame.Initialize(data);
        SetField("_targetRandomSampleProvider", randomSampleProvider);
        Invoke(_minigame, "StartTimingPhase");
    }

    private bool HandleTrigger(bool triggerPressed)
    {
        return (bool)Invoke(_minigame, "TryHandleTriggerPress", triggerPressed);
    }

    private string GetGameState()
    {
        return GetField("_gameState").ToString();
    }

    private void SetField(string fieldName, object value)
    {
        GetFieldInfo(fieldName).SetValue(_minigame, value);
    }

    private object GetField(string fieldName)
    {
        return GetFieldInfo(fieldName).GetValue(_minigame);
    }

    private T GetField<T>(string fieldName)
    {
        return (T)GetField(fieldName);
    }

    private static object Invoke(object target, string methodName, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.That(method, Is.Not.Null, $"Expected method '{methodName}' was not found.");
        return method.Invoke(target, arguments);
    }

    private static FieldInfo GetFieldInfo(string fieldName)
    {
        FieldInfo field = typeof(LieMinigame).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(field, Is.Not.Null, $"Expected field '{fieldName}' was not found.");
        return field;
    }
}
