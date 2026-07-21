using System;
using System.Reflection;
using Game.Minigames;
using NUnit.Framework;
using UnityEngine;

public class LieMinigameInputTests
{
    private GameObject _gameObject;
    private LieMinigame _minigame;

    [SetUp]
    public void SetUp()
    {
        _gameObject = new GameObject("LieMinigameInputTests");
        _minigame = _gameObject.AddComponent<LieMinigame>();

        SetField("_attemptsRemaining", 3);
        SetField("_indicatorPosition", 0f);
        SetField("_targetZoneCenter", 1f);
        SetField("_targetZoneWidth", 0.1f);
        SetField("_isInputEnabled", true);
        SetGameState("WaitingForPress");
    }

    [TearDown]
    public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(_gameObject);
    }

    [Test]
    public void HeldTrigger_DecrementsAttemptsOnlyOnceUntilReleased()
    {
        object latch = GetField("_triggerPressLatch");

        Assert.That(HandleTrigger(TryConsume(latch, true)), Is.True);
        Assert.That(GetField<int>("_attemptsRemaining"), Is.EqualTo(2));

        for (int frame = 0; frame < 10; frame++)
        {
            Assert.That(HandleTrigger(TryConsume(latch, true)), Is.False);
        }

        Assert.That(GetField<int>("_attemptsRemaining"), Is.EqualTo(2));
        Assert.That(TryConsume(latch, false), Is.False);
        Assert.That(HandleTrigger(TryConsume(latch, true)), Is.True);
        Assert.That(GetField<int>("_attemptsRemaining"), Is.EqualTo(1));
    }

    [Test]
    public void TriggerHeldBeforeTimingPhase_RequiresReleaseBeforeFirstAttempt()
    {
        object latch = GetField("_triggerPressLatch");
        Invoke(latch, "Arm", true);

        Assert.That(HandleTrigger(TryConsume(latch, true)), Is.False);
        Assert.That(GetField<int>("_attemptsRemaining"), Is.EqualTo(3));
        Assert.That(TryConsume(latch, false), Is.False);
        Assert.That(HandleTrigger(TryConsume(latch, true)), Is.True);
        Assert.That(GetField<int>("_attemptsRemaining"), Is.EqualTo(2));
    }

    [Test]
    public void CompletedMinigame_IgnoresNewTriggerPress()
    {
        object latch = GetField("_triggerPressLatch");
        SetField("_isInputEnabled", false);
        SetGameState("Complete");

        Assert.That(TryConsume(latch, false), Is.False);
        Assert.That(HandleTrigger(TryConsume(latch, true)), Is.False);
        Assert.That(GetField<int>("_attemptsRemaining"), Is.EqualTo(3));
    }

    private bool HandleTrigger(bool triggerPressed)
    {
        return (bool)Invoke(_minigame, "TryHandleTriggerPress", triggerPressed);
    }

    private static bool TryConsume(object latch, bool isPressed)
    {
        return (bool)Invoke(latch, "TryConsume", isPressed);
    }

    private void SetGameState(string stateName)
    {
        FieldInfo field = GetFieldInfo("_gameState");
        field.SetValue(_minigame, Enum.Parse(field.FieldType, stateName));
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
