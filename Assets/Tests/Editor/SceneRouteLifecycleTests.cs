using System;
using System.Reflection;
using Game.Core;
using Game.Core.Events;
using Game.Minigames;
using Game.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class SceneRouteLifecycleTests
{
    private GameObject _gameManagerObject;
    private GameObject _minigameManagerObject;
    private GameObject _pauseManagerObject;
    private GameObject _canvasObject;

    [TearDown]
    public void TearDown()
    {
        SetStaticField(typeof(GameManager), "_instance", null);
        SetStaticField(typeof(MinigameManager), "_instance", null);
        SetStaticField(typeof(PauseManager), "_instance", null);
        SetStaticField(typeof(PauseManager), "_isShuttingDown", false);

        if (_gameManagerObject != null)
        {
            UnityEngine.Object.DestroyImmediate(_gameManagerObject);
        }

        if (_minigameManagerObject != null)
        {
            UnityEngine.Object.DestroyImmediate(_minigameManagerObject);
        }

        if (_pauseManagerObject != null)
        {
            UnityEngine.Object.DestroyImmediate(_pauseManagerObject);
        }

        if (_canvasObject != null)
        {
            UnityEngine.Object.DestroyImmediate(_canvasObject);
        }

        Time.timeScale = 1f;
    }

    [Test]
    public void EmptyRouteTarget_IsRejectedWithoutOpeningGate()
    {
        SceneRouteGuard guard = new SceneRouteGuard();

        Assert.That(guard.TryBegin("  ", out string failure), Is.False);
        Assert.That(failure, Does.Contain("empty"));
        Assert.That(guard.IsRouteInProgress, Is.False);
    }

    [Test]
    public void DuplicateRouteToSameScene_IsRejected()
    {
        SceneRouteGuard guard = new SceneRouteGuard();

        Assert.That(guard.TryBegin(SceneIds.Menu, out _), Is.True);
        Assert.That(guard.TryBegin(SceneIds.Menu, out string failure), Is.False);
        Assert.That(failure, Does.Contain("already in progress"));
        Assert.That(guard.TargetSceneName, Is.EqualTo(SceneIds.Menu));
    }

    [Test]
    public void ConcurrentRouteToDifferentScene_IsRejectedWithoutReplacingTarget()
    {
        SceneRouteGuard guard = new SceneRouteGuard();

        Assert.That(guard.TryBegin(SceneIds.Menu, out _), Is.True);
        Assert.That(guard.TryBegin(SceneIds.Gameplay, out _), Is.False);
        Assert.That(guard.TargetSceneName, Is.EqualTo(SceneIds.Menu));
    }

    [Test]
    public void Abort_AllowsRouteRetry()
    {
        SceneRouteGuard guard = new SceneRouteGuard();
        Assert.That(guard.TryBegin(SceneIds.Menu, out _), Is.True);

        guard.Abort();

        Assert.That(guard.TryBegin(SceneIds.Gameplay, out _), Is.True);
        Assert.That(guard.TargetSceneName, Is.EqualTo(SceneIds.Gameplay));
    }

    [Test]
    public void UnrelatedSceneCallback_DoesNotCompletePendingRoute()
    {
        SceneRouteGuard guard = new SceneRouteGuard();
        Assert.That(guard.TryBegin(SceneIds.Menu, out _), Is.True);

        guard.Complete(SceneIds.Gameplay);

        Assert.That(guard.IsRouteInProgress, Is.True);
    }

    [Test]
    public void MatchingSceneCallback_CompletesPendingRoute()
    {
        SceneRouteGuard guard = new SceneRouteGuard();
        Assert.That(guard.TryBegin(SceneIds.Menu, out _), Is.True);

        guard.Complete(SceneIds.Menu);

        Assert.That(guard.IsRouteInProgress, Is.False);
        Assert.That(guard.TargetSceneName, Is.Empty);
    }

    [TestCase(false, false, false, SceneRouteAuthorityMode.OfflineLocal)]
    [TestCase(true, true, false, SceneRouteAuthorityMode.ServerAuthoritative)]
    [TestCase(true, false, false, SceneRouteAuthorityMode.BlockedClient)]
    [TestCase(true, false, true, SceneRouteAuthorityMode.ClientLocalAfterShutdown)]
    public void AuthorityPolicy_ResolvesExpectedRouteMode(
        bool isListening,
        bool isServer,
        bool allowClientLocalLoad,
        SceneRouteAuthorityMode expected)
    {
        Assert.That(
            SceneRouteAuthorityPolicy.Resolve(isListening, isServer, allowClientLocalLoad),
            Is.EqualTo(expected));
    }

    [Test]
    public void Cancel_ClearsActiveSessionBeforePublishingSingleTerminalEvent()
    {
        MinigameManager manager = CreateInactiveMinigameManager();
        FakeMinigame minigame = new FakeMinigame("route_test", MinigameResult.Cancelled);
        SetField(manager, "_activeMinigame", minigame);
        SetField(manager, "_activeOwnerPlayerId", "local_player_0");
        SetField(manager, "_activeSessionToken", 17);
        minigame.OnEndAction = manager.CancelActiveMinigame;
        _canvasObject = new GameObject("SceneRouteTestCanvas");
        Canvas canvas = _canvasObject.AddComponent<Canvas>();
        SetField(manager, "_cleaningCanvas", canvas);

        int eventCount = 0;
        int rewardCount = 0;
        Action<MinigameCancelledEvent> handler = eventData =>
        {
            eventCount++;
            Assert.That(eventData.MinigameId, Is.EqualTo("route_test"));
            Assert.That(manager.GetActiveMinigame(), Is.Null);
        };
        Action<MinigameRewardGrantedEvent> rewardHandler = _ => rewardCount++;
        EventBus.Subscribe(handler);
        EventBus.Subscribe(rewardHandler);

        try
        {
            manager.CancelActiveMinigame();
        }
        finally
        {
            EventBus.Unsubscribe(handler);
            EventBus.Unsubscribe(rewardHandler);
        }

        Assert.That(minigame.EndCount, Is.EqualTo(1));
        Assert.That(eventCount, Is.EqualTo(1));
        Assert.That(rewardCount, Is.Zero);
        Assert.That(manager.GetActiveMinigame(), Is.Null);
        Assert.That(canvas.enabled, Is.False);
    }

    [Test]
    public void SceneRoutePreparation_CancelsActiveMinigameBeforeReturningReady()
    {
        MinigameManager minigameManager = CreateInactiveMinigameManager();
        FakeMinigame minigame = new FakeMinigame("route_test", MinigameResult.Cancelled);
        SetField(minigameManager, "_activeMinigame", minigame);
        SetField(minigameManager, "_activeOwnerPlayerId", "local_player_0");

        GameManager gameManager = CreateInactiveGameManager();
        SetField(gameManager, "_currentState", GameState.Minigame);

        int cancelCount = 0;
        Action<MinigameCancelledEvent> handler = _ => cancelCount++;
        EventBus.Subscribe(handler);

        bool prepared;
        string failure;
        try
        {
            prepared = gameManager.TryPrepareForSceneRoute(SceneIds.Menu, out failure);
        }
        finally
        {
            EventBus.Unsubscribe(handler);
        }

        Assert.That(prepared, Is.True, failure);
        Assert.That(gameManager.IsSceneRoutePending, Is.True);
        Assert.That(minigame.EndCount, Is.EqualTo(1));
        Assert.That(cancelCount, Is.EqualTo(1));
        Assert.That(minigameManager.GetActiveMinigame(), Is.Null);
        Assert.That(gameManager.CurrentState, Is.EqualTo(GameState.Minigame));
    }

    [Test]
    public void SceneRoutePreparation_RejectsSecondRequestWithoutRepeatingCleanup()
    {
        MinigameManager minigameManager = CreateInactiveMinigameManager();
        FakeMinigame minigame = new FakeMinigame("route_test", MinigameResult.Cancelled);
        SetField(minigameManager, "_activeMinigame", minigame);
        SetField(minigameManager, "_activeOwnerPlayerId", "local_player_0");

        GameManager gameManager = CreateInactiveGameManager();
        SetField(gameManager, "_currentState", GameState.Minigame);

        Assert.That(gameManager.TryPrepareForSceneRoute(SceneIds.Menu, out string firstFailure), Is.True, firstFailure);
        Assert.That(gameManager.TryPrepareForSceneRoute(SceneIds.Gameplay, out string secondFailure), Is.False);
        Assert.That(secondFailure, Does.Contain("already pending"));
        Assert.That(minigame.EndCount, Is.EqualTo(1));
    }

    [Test]
    public void RoutePreparationFromPause_ResumesTimeBeforeRouteLock()
    {
        GameManager gameManager = CreateInactiveGameManager();
        SetField(gameManager, "_currentState", GameState.FreePlay);

        _pauseManagerObject = new GameObject("SceneRouteTestPauseManager");
        _pauseManagerObject.SetActive(false);
        PauseManager pauseManager = _pauseManagerObject.AddComponent<PauseManager>();
        SetField(pauseManager, "_isPaused", true);
        SetStaticField(typeof(PauseManager), "_instance", pauseManager);
        Time.timeScale = 0f;

        Assert.That(gameManager.TryPrepareForSceneRoute(SceneIds.Menu, out string failure), Is.True, failure);
        Assert.That(pauseManager.IsPaused, Is.False);
        Assert.That(Time.timeScale, Is.EqualTo(1f));
        Assert.That(gameManager.IsSceneRoutePending, Is.True);
    }

    [Test]
    public void TerminalEventWithoutTrackedActiveSession_DoesNotChangeState()
    {
        GameManager gameManager = CreateInactiveGameManager();
        SetField(gameManager, "_currentState", GameState.Minigame);

        InvokePrivate(
            gameManager,
            "OnMinigameEnded",
            new MinigameEndedEvent(MinigameResult.Pass, "stale_session", "local_player_0"));

        Assert.That(gameManager.CurrentState, Is.EqualTo(GameState.Minigame));
    }

    [Test]
    public void CancelEventDuringSceneRoute_DoesNotTemporarilyReturnToFreePlay()
    {
        GameManager gameManager = CreateInactiveGameManager();
        SetField(gameManager, "_currentState", GameState.Minigame);
        SetField(gameManager, "_activeLocalMinigameId", "route_test");
        SetField(gameManager, "_isSceneRoutePending", true);

        InvokePrivate(
            gameManager,
            "OnMinigameCancelled",
            new MinigameCancelledEvent(MinigameResult.Cancelled, "route_test", "local_player_0"));

        Assert.That(gameManager.CurrentState, Is.EqualTo(GameState.Minigame));
        Assert.That(GetField<string>(gameManager, "_activeLocalMinigameId"), Is.Empty);
    }

    [Test]
    public void ReturnToFreePlayRequest_IsRejectedWhileMinigameIsStillActive()
    {
        MinigameManager minigameManager = CreateInactiveMinigameManager();
        FakeMinigame minigame = new FakeMinigame("active_test", MinigameResult.None);
        SetField(minigameManager, "_activeMinigame", minigame);
        SetField(minigameManager, "_activeOwnerPlayerId", "local_player_0");

        GameManager gameManager = CreateInactiveGameManager();
        SetField(gameManager, "_currentState", GameState.Minigame);

        LogAssert.Expect(
            LogType.Warning,
            new System.Text.RegularExpressions.Regex(
                "Rejected GameState transition.*active minigame must finish"));

        gameManager.RequestReturnToFreePlay();

        Assert.That(gameManager.CurrentState, Is.EqualTo(GameState.Minigame));
        Assert.That(minigameManager.GetActiveMinigame(), Is.SameAs(minigame));
    }

    private MinigameManager CreateInactiveMinigameManager()
    {
        _minigameManagerObject = new GameObject("SceneRouteTestMinigameManager");
        _minigameManagerObject.SetActive(false);
        MinigameManager manager = _minigameManagerObject.AddComponent<MinigameManager>();
        SetStaticField(typeof(MinigameManager), "_instance", manager);
        return manager;
    }

    private GameManager CreateInactiveGameManager()
    {
        _gameManagerObject = new GameObject("SceneRouteTestGameManager");
        _gameManagerObject.SetActive(false);
        GameManager manager = _gameManagerObject.AddComponent<GameManager>();
        SetStaticField(typeof(GameManager), "_instance", manager);
        return manager;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{fieldName}'.");
        field.SetValue(target, value);
    }

    private static T GetField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing field '{fieldName}'.");
        return (T)field.GetValue(target);
    }

    private static void InvokePrivate(object target, string methodName, object argument)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null, $"Missing method '{methodName}'.");
        method.Invoke(target, new[] { argument });
    }

    private static void SetStaticField(Type type, string fieldName, object value)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null, $"Missing static field '{fieldName}'.");
        field.SetValue(null, value);
    }

    private sealed class FakeMinigame : IMinigame
    {
        private readonly string _id;
        private readonly MinigameResult _result;

        public FakeMinigame(string id, MinigameResult result)
        {
            _id = id;
            _result = result;
        }

        public int EndCount { get; private set; }
        public Action OnEndAction { get; set; }

        public void Initialize(MinigameData data) { }
        public void OnMinigameStart() { }
        public void OnMinigameUpdate() { }
        public void OnMinigameEnd()
        {
            EndCount++;
            OnEndAction?.Invoke();
        }
        public bool IsActive() => true;
        public MinigameResult GetResult() => _result;
        public MinigameData GetMinigameData() => new MinigameData { minigameId = _id };
        public string GetMinigameId() => _id;
    }
}
