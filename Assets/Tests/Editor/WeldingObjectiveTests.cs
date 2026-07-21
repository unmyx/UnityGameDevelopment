using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using Game.Core.Events;
using Game.Minigames;
using NUnit.Framework;
using UnityEngine;

public class WeldingObjectiveTests
{
    private const string WeldingObjectiveResourcePath = "Objectives/ObjWelding1";
    private const string WeldingObjectiveId = "obj_welding_1";
    private const string WeldingMinigameId = "welding";

    private readonly List<Objective> _transientObjectives = new List<Objective>();
    private GameObject _managerObject;
    private ObjectiveManager _manager;
    private Objective _weldingAsset;
    private Objective _weldingObjective;

    [SetUp]
    public void SetUp()
    {
        ResetEventBus();
        ResetObjectiveManagerStatics();
        ObjectiveManager.ClearRuntimeCaches();

        _weldingAsset = Resources.Load<Objective>(WeldingObjectiveResourcePath);
        Assert.That(_weldingAsset, Is.Not.Null);
        _weldingAsset.Reset();

        _weldingObjective = Object.Instantiate(_weldingAsset);
        _transientObjectives.Add(_weldingObjective);

        _managerObject = new GameObject("WeldingObjectiveTests_Manager");
        _manager = _managerObject.AddComponent<ObjectiveManager>();
        _manager.SubscribeToMinigameEvents();
    }

    [TearDown]
    public void TearDown()
    {
        ResetEventBus();
        ObjectiveManager.ClearRuntimeCaches();

        if (_managerObject != null)
        {
            Object.DestroyImmediate(_managerObject);
        }

        for (int i = 0; i < _transientObjectives.Count; i++)
        {
            if (_transientObjectives[i] != null)
            {
                Object.DestroyImmediate(_transientObjectives[i]);
            }
        }

        _transientObjectives.Clear();
        _weldingAsset?.Reset();
        ResetObjectiveManagerStatics();
    }

    [Test]
    public void WeldingObjective_RequiresOneCompletedSession()
    {
        Assert.That(_weldingAsset.RequiredCompletions, Is.EqualTo(1));
        Assert.That(_weldingAsset.TargetMinigameId, Is.EqualTo(WeldingMinigameId));
        Assert.That(_weldingAsset.CountPassOnly, Is.True);
    }

    [Test]
    public void OneWeldingRewardEvent_CompletesObjective()
    {
        TrackOnlyWeldingObjective();

        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-1");

        Assert.That(_weldingObjective.IsCompleted, Is.True);
        Assert.That(_manager.GetCompletedObjectives(), Has.Member(_weldingObjective));
        Assert.That(_manager.GetActiveObjectives(), Has.No.Member(_weldingObjective));
    }

    [Test]
    public void OneWeldingRewardEvent_ChangesProgressFromZeroToOne()
    {
        TrackOnlyWeldingObjective();
        Assert.That(_weldingObjective.CompletionCount, Is.Zero);

        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-1");

        Assert.That(_weldingObjective.CompletionCount, Is.EqualTo(1));
        Assert.That(_weldingObjective.ProgressPercent, Is.EqualTo(1f));
    }

    [Test]
    public void WeldingCompletionEvent_IsPublishedExactlyOnce()
    {
        TrackOnlyWeldingObjective();
        int completionEventCount = 0;
        EventBus.Subscribe<ObjectiveCompletedEvent>(_ => completionEventCount++);

        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-1");
        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-1");

        Assert.That(completionEventCount, Is.EqualTo(1));
    }

    [Test]
    public void ObjectiveRewardGate_IsConsumedExactlyOnce()
    {
        TrackOnlyWeldingObjective();

        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-1");
        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-1");

        HashSet<string> rewardedObjectiveIds = GetRewardedObjectiveIds();
        Assert.That(rewardedObjectiveIds.Count, Is.EqualTo(1));
        Assert.That(rewardedObjectiveIds, Contains.Item(WeldingObjectiveId));
    }

    [Test]
    public void DuplicateWeldingRewardAfterCompletion_HasNoEffect()
    {
        TrackOnlyWeldingObjective();
        int progressEventCount = 0;
        EventBus.Subscribe<ObjectiveProgressEvent>(_ => progressEventCount++);

        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-1");
        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-duplicate");

        Assert.That(_weldingObjective.CompletionCount, Is.EqualTo(1));
        Assert.That(progressEventCount, Is.EqualTo(1));
        Assert.That(_manager.GetCompletedObjectives().FindAll(
            objective => objective.ObjectiveId == WeldingObjectiveId).Count, Is.EqualTo(1));
    }

    [Test]
    public void Timeout_DoesNotIncreaseWeldingProgress()
    {
        TrackOnlyWeldingObjective();

        EventBus.Publish(new MinigameEndedEvent(
            MinigameResult.Timeout,
            WeldingMinigameId,
            "local_player_0"));

        Assert.That(_weldingObjective.CompletionCount, Is.Zero);
        Assert.That(_weldingObjective.IsCompleted, Is.False);
    }

    [Test]
    public void Cancel_DoesNotIncreaseWeldingProgress()
    {
        TrackOnlyWeldingObjective();

        EventBus.Publish(new MinigameCancelledEvent(
            MinigameResult.Cancelled,
            WeldingMinigameId,
            "local_player_0"));

        Assert.That(_weldingObjective.CompletionCount, Is.Zero);
        Assert.That(_weldingObjective.IsCompleted, Is.False);
    }

    [Test]
    public void OtherMinigameReward_DoesNotIncreaseWeldingProgress()
    {
        TrackOnlyWeldingObjective();

        PublishReward(MinigameResult.Pass, "cleaning", "cleaning-session-1");

        Assert.That(_weldingObjective.CompletionCount, Is.Zero);
        Assert.That(_weldingObjective.IsCompleted, Is.False);
    }

    [Test]
    public void MinigameEndWithoutRewardEvent_DoesNotIncreaseWeldingProgress()
    {
        TrackOnlyWeldingObjective();

        EventBus.Publish(new MinigameEndedEvent(
            MinigameResult.Pass,
            WeldingMinigameId,
            "local_player_0"));
        EventBus.Publish(new MinigameEndedEvent(
            MinigameResult.Pass,
            WeldingMinigameId,
            "local_player_0"));

        Assert.That(_weldingObjective.CompletionCount, Is.Zero);
    }

    [Test]
    public void LegacySaveProgressZero_RemainsActiveAndIncomplete()
    {
        ObjectivesSaveData savedData = new ObjectivesSaveData();
        savedData.activeObjectives.Add(new ObjectiveProgressData(WeldingObjectiveId, 0));

        _manager.RestoreFromSave(savedData);
        _manager.SyncAfterLoad();

        Objective restored = _manager.GetObjectiveById(WeldingObjectiveId);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored.CompletionCount, Is.Zero);
        Assert.That(restored.IsCompleted, Is.False);
        Assert.That(_manager.GetActiveObjectives(), Has.Member(restored));
        Assert.That(_manager.GetCompletedObjectives(), Has.No.Member(restored));
    }

    [Test]
    public void LegacySaveProgressOne_LoadsAsCompletedWithoutCompletionEvent()
    {
        RestoreLegacyActiveProgress(1, out int completionEventCount);

        Objective restored = _manager.GetObjectiveById(WeldingObjectiveId);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored.IsCompleted, Is.True);
        Assert.That(restored.CompletionCount, Is.EqualTo(1));
        Assert.That(completionEventCount, Is.Zero);
    }

    [TestCase(2)]
    [TestCase(3)]
    public void LegacySaveProgressAboveNewRequirement_IsClampedAndCompleted(int legacyProgress)
    {
        RestoreLegacyActiveProgress(legacyProgress, out int completionEventCount);

        Objective restored = _manager.GetObjectiveById(WeldingObjectiveId);
        Assert.That(restored, Is.Not.Null);
        Assert.That(restored.CompletionCount, Is.EqualTo(1));
        Assert.That(restored.ProgressPercent, Is.EqualTo(1f));
        Assert.That(restored.IsCompleted, Is.True);
        Assert.That(completionEventCount, Is.Zero);
    }

    [Test]
    public void LoadingCompletedObjective_DoesNotGrantRewardOrCompletionAgain()
    {
        int completionEventCount = 0;
        EventBus.Subscribe<ObjectiveCompletedEvent>(_ => completionEventCount++);
        ObjectivesSaveData savedData = new ObjectivesSaveData();
        savedData.completedObjectiveIds.Add(WeldingObjectiveId);

        _manager.RestoreFromSave(savedData);
        _manager.SyncAfterLoad();

        Assert.That(completionEventCount, Is.Zero);
        Assert.That(GetRewardedObjectiveIds(), Contains.Item(WeldingObjectiveId));
        Assert.That(_manager.GetCompletedObjectives().FindAll(
            objective => objective.ObjectiveId == WeldingObjectiveId).Count, Is.EqualTo(1));
    }

    [Test]
    public void WeldingCompletion_AdvancesActiveListOnlyOnce()
    {
        Objective nextObjective = CreateObjective(
            "obj_after_welding",
            "cleaning",
            requiredCompletions: 1);
        _manager.SetObjectives(new List<Objective> { _weldingObjective, nextObjective });
        int completionEventCount = 0;
        EventBus.Subscribe<ObjectiveCompletedEvent>(_ => completionEventCount++);

        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-1");
        PublishReward(MinigameResult.Pass, WeldingMinigameId, "welding-session-duplicate");

        List<Objective> activeObjectives = _manager.GetActiveObjectives();
        Assert.That(activeObjectives.Count, Is.EqualTo(1));
        Assert.That(activeObjectives[0], Is.SameAs(nextObjective));
        Assert.That(completionEventCount, Is.EqualTo(1));
    }

    private void TrackOnlyWeldingObjective()
    {
        _manager.SetObjectives(new List<Objective> { _weldingObjective });
    }

    private void RestoreLegacyActiveProgress(int legacyProgress, out int completionEventCount)
    {
        int observedCompletionEvents = 0;
        EventBus.Subscribe<ObjectiveCompletedEvent>(_ => observedCompletionEvents++);
        ObjectivesSaveData savedData = new ObjectivesSaveData();
        savedData.activeObjectives.Add(new ObjectiveProgressData(WeldingObjectiveId, legacyProgress));

        _manager.RestoreFromSave(savedData);
        _manager.SyncAfterLoad();

        completionEventCount = observedCompletionEvents;
    }

    private static void PublishReward(MinigameResult result, string minigameId, string rewardGrantId)
    {
        EventBus.Publish(new MinigameRewardGrantedEvent(new RewardGrantedData
        {
            rewardGrantId = rewardGrantId,
            minigameId = minigameId,
            ownerPlayerId = "local_player_0",
            result = result,
            currencyAwarded = 0,
            itemsAwarded = 0
        }));
    }

    private Objective CreateObjective(string objectiveId, string minigameId, int requiredCompletions)
    {
        Objective objective = ScriptableObject.CreateInstance<Objective>();
        SetPrivateField(objective, "_objectiveId", objectiveId);
        SetPrivateField(objective, "_targetMinigameId", minigameId);
        SetPrivateField(objective, "_requiredCompletions", requiredCompletions);
        SetPrivateField(objective, "_countPassOnly", true);
        SetPrivateField(objective, "_isActive", true);
        _transientObjectives.Add(objective);
        return objective;
    }

    private HashSet<string> GetRewardedObjectiveIds()
    {
        FieldInfo field = typeof(ObjectiveManager).GetField(
            "_rewardGrantedObjectiveIds",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        return (HashSet<string>)field.GetValue(_manager);
    }

    private static void SetPrivateField<T>(Objective objective, string fieldName, T value)
    {
        FieldInfo field = typeof(Objective).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field.SetValue(objective, value);
    }

    private static void ResetEventBus()
    {
        MethodInfo method = typeof(EventBus).GetMethod(
            "ResetSubscribers",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method.Invoke(null, null);
    }

    private static void ResetObjectiveManagerStatics()
    {
        SetStaticField("_instance", null);
        SetStaticField("_isReadyForRewardEvents", false);
    }

    private static void SetStaticField(string fieldName, object value)
    {
        FieldInfo field = typeof(ObjectiveManager).GetField(
            fieldName,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        field.SetValue(null, value);
    }
}
