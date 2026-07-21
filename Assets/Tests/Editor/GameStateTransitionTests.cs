using System;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

public sealed class GameStateTransitionTests
{
    [Test]
    public void MenuToMenu_IsSafeNoOp()
    {
        AssertDecision(GameState.Menu, GameState.Menu, GameStateTransitionContext.Local, true, true);
    }

    [Test]
    public void MenuToFreePlay_RequiresSceneRefreshContext()
    {
        AssertDecision(GameState.Menu, GameState.FreePlay, GameStateTransitionContext.Local, false, false);
        AssertDecision(GameState.Menu, GameState.FreePlay, GameStateTransitionContext.SceneRefresh, true, false);
    }

    [Test]
    public void MenuToMinigame_IsRejected()
    {
        AssertDecision(GameState.Menu, GameState.Minigame, GameStateTransitionContext.Local, false, false);
        AssertDecision(GameState.Menu, GameState.Minigame, GameStateTransitionContext.SceneRefresh, false, false);
    }

    [Test]
    public void FreePlayToMinigame_IsAllowedLocally()
    {
        AssertDecision(GameState.FreePlay, GameState.Minigame, GameStateTransitionContext.Local, true, false);
    }

    [Test]
    public void MinigameToFreePlay_IsAllowedLocally()
    {
        AssertDecision(GameState.Minigame, GameState.FreePlay, GameStateTransitionContext.Local, true, false);
    }

    [Test]
    public void FreePlayToMenu_RequiresSceneRefreshContext()
    {
        AssertDecision(GameState.FreePlay, GameState.Menu, GameStateTransitionContext.Local, false, false);
        AssertDecision(GameState.FreePlay, GameState.Menu, GameStateTransitionContext.SceneRefresh, true, false);
    }

    [Test]
    public void MinigameToMenu_RequiresCompletedCleanup()
    {
        Assert.That(GameStateTransitionPolicy.IsTransitionAllowed(
            GameState.Minigame,
            GameState.Menu,
            GameStateTransitionContext.SceneRefresh,
            minigameCleanupCompleted: false), Is.False);
        Assert.That(GameStateTransitionPolicy.IsTransitionAllowed(
            GameState.Minigame,
            GameState.Menu,
            GameStateTransitionContext.SceneRefresh,
            minigameCleanupCompleted: true), Is.True);
    }

    [Test]
    public void MinigameToMinigame_IsRejected()
    {
        AssertDecision(GameState.Minigame, GameState.Minigame, GameStateTransitionContext.Local, false, false);
    }

    [TestCase(-1)]
    [TestCase(99)]
    public void UnknownEnumValue_IsRejected(int rawValue)
    {
        GameState unknown = (GameState)rawValue;

        Assert.That(GameStateTransitionPolicy.IsTransitionAllowed(
            unknown,
            GameState.FreePlay,
            GameStateTransitionContext.Local), Is.False);
        Assert.That(GameStateTransitionPolicy.IsTransitionAllowed(
            GameState.FreePlay,
            unknown,
            GameStateTransitionContext.Local), Is.False);
    }

    [Test]
    public void SuccessfulTransition_ExitsOldAndEntersNewExactlyOnce()
    {
        FakeState freePlay = new FakeState();
        FakeState minigame = new FakeState();
        GameStateTransitionService service = CreateService(new FakeState(), freePlay, minigame);
        GameState current = GameState.FreePlay;
        IGameState implementation = freePlay;
        bool transitioning = false;

        bool changed = service.ChangeState(
            GameState.Minigame,
            ref current,
            ref implementation,
            ref transitioning,
            out string failure);

        Assert.That(changed, Is.True, failure);
        Assert.That(freePlay.ExitCount, Is.EqualTo(1));
        Assert.That(minigame.EnterCount, Is.EqualTo(1));
        Assert.That(current, Is.EqualTo(GameState.Minigame));
        Assert.That(implementation, Is.SameAs(minigame));
        Assert.That(transitioning, Is.False);
    }

    [Test]
    public void NoOpTransition_DoesNotInvokeLifecycle()
    {
        FakeState freePlay = new FakeState();
        GameStateTransitionService service = CreateService(new FakeState(), freePlay, new FakeState());
        GameState current = GameState.FreePlay;
        IGameState implementation = freePlay;
        bool transitioning = false;

        Assert.That(service.ChangeState(
            GameState.FreePlay,
            ref current,
            ref implementation,
            ref transitioning,
            out _), Is.True);
        Assert.That(freePlay.EnterCount, Is.Zero);
        Assert.That(freePlay.ExitCount, Is.Zero);
    }

    [Test]
    public void RejectedTransition_DoesNotInvokeLifecycleOrChangeState()
    {
        FakeState menu = new FakeState();
        FakeState minigame = new FakeState();
        GameStateTransitionService service = CreateService(menu, new FakeState(), minigame);
        GameState current = GameState.Menu;
        IGameState implementation = menu;
        bool transitioning = false;

        Assert.That(service.ChangeState(
            GameState.Minigame,
            ref current,
            ref implementation,
            ref transitioning,
            out _), Is.False);
        Assert.That(current, Is.EqualTo(GameState.Menu));
        Assert.That(implementation, Is.SameAs(menu));
        Assert.That(menu.ExitCount, Is.Zero);
        Assert.That(minigame.EnterCount, Is.Zero);
    }

    [Test]
    public void MissingTargetImplementation_DoesNotChangeCurrentState()
    {
        FakeState freePlay = new FakeState();
        GameStateTransitionService service = CreateService(new FakeState(), freePlay, null);
        GameState current = GameState.FreePlay;
        IGameState implementation = freePlay;
        bool transitioning = false;

        Assert.That(service.ChangeState(
            GameState.Minigame,
            ref current,
            ref implementation,
            ref transitioning,
            out string failure), Is.False);
        Assert.That(failure, Does.Contain("Missing required Minigame"));
        Assert.That(current, Is.EqualTo(GameState.FreePlay));
        Assert.That(implementation, Is.SameAs(freePlay));
        Assert.That(freePlay.ExitCount, Is.Zero);
    }

    [Test]
    public void ActiveTransitionGuard_PreventsDuplicateLifecycle()
    {
        FakeState freePlay = new FakeState();
        FakeState minigame = new FakeState();
        GameStateTransitionService service = CreateService(new FakeState(), freePlay, minigame);
        GameState current = GameState.FreePlay;
        IGameState implementation = freePlay;
        bool transitioning = true;

        Assert.That(service.ChangeState(
            GameState.Minigame,
            ref current,
            ref implementation,
            ref transitioning,
            out string failure), Is.False);
        Assert.That(failure, Does.Contain("another transition is active"));
        Assert.That(freePlay.ExitCount, Is.Zero);
        Assert.That(minigame.EnterCount, Is.Zero);
    }

    [Test]
    public void ExitException_LeavesPreviousStateActiveAndClearsTransitionGuard()
    {
        FakeState freePlay = new FakeState { ThrowOnExit = true };
        FakeState minigame = new FakeState();
        GameStateTransitionService service = CreateService(new FakeState(), freePlay, minigame);
        GameState current = GameState.FreePlay;
        IGameState implementation = freePlay;
        bool transitioning = false;

        Assert.That(service.ChangeState(
            GameState.Minigame,
            ref current,
            ref implementation,
            ref transitioning,
            out string failure), Is.False);
        Assert.That(failure, Does.Contain("exit failed"));
        Assert.That(current, Is.EqualTo(GameState.FreePlay));
        Assert.That(implementation, Is.SameAs(freePlay));
        Assert.That(minigame.EnterCount, Is.Zero);
        Assert.That(transitioning, Is.False);
    }

    [Test]
    public void EnterException_RollsBackPreviousStateAndClearsTransitionGuard()
    {
        FakeState freePlay = new FakeState();
        FakeState minigame = new FakeState { ThrowOnEnter = true };
        GameStateTransitionService service = CreateService(new FakeState(), freePlay, minigame);
        GameState current = GameState.FreePlay;
        IGameState implementation = freePlay;
        bool transitioning = false;

        Assert.That(service.ChangeState(
            GameState.Minigame,
            ref current,
            ref implementation,
            ref transitioning,
            out string failure), Is.False);
        Assert.That(failure, Does.Contain("enter failed"));
        Assert.That(current, Is.EqualTo(GameState.FreePlay));
        Assert.That(implementation, Is.SameAs(freePlay));
        Assert.That(freePlay.ExitCount, Is.EqualTo(1));
        Assert.That(freePlay.EnterCount, Is.EqualTo(1));
        Assert.That(transitioning, Is.False);
    }

    [TestCase(SceneIds.Menu, GameState.Menu)]
    [TestCase(SceneIds.Home, GameState.FreePlay)]
    [TestCase(SceneIds.Gameplay, GameState.FreePlay)]
    public void SceneName_MapsToExpectedState(string sceneName, GameState expected)
    {
        GameStateResolver resolver = new GameStateResolver(new FakeState(), new FakeState(), new FakeState());

        Assert.That(resolver.TryResolveStateFromSceneName(sceneName, out GameState actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void RepeatedSceneRefreshWithSameImplementation_IsIdempotent()
    {
        FakeState menu = new FakeState();
        GameStateTransitionService service = CreateService(menu, new FakeState(), new FakeState());
        GameState current = GameState.Menu;
        IGameState implementation = menu;

        Assert.That(service.RefreshStateFromScene(
            GameState.Menu,
            ref current,
            ref implementation,
            true,
            out _), Is.True);
        Assert.That(menu.EnterCount, Is.Zero);
        Assert.That(menu.ExitCount, Is.Zero);
    }

    [Test]
    public void SameStateWithNewSceneImplementation_RebindsExactlyOnce()
    {
        FakeState previous = new FakeState();
        FakeState replacement = new FakeState();
        GameStateTransitionService service = CreateService(new FakeState(), replacement, new FakeState());
        GameState current = GameState.FreePlay;
        IGameState implementation = previous;

        Assert.That(service.RefreshStateFromScene(
            GameState.FreePlay,
            ref current,
            ref implementation,
            true,
            out string failure), Is.True, failure);
        Assert.That(previous.ExitCount, Is.EqualTo(1));
        Assert.That(replacement.EnterCount, Is.EqualTo(1));
        Assert.That(implementation, Is.SameAs(replacement));
    }

    [Test]
    public void SceneRefreshFromMinigame_RequiresCleanupAndDoesNotMutateOnFailure()
    {
        FakeState minigame = new FakeState();
        FakeState menu = new FakeState();
        GameStateTransitionService service = CreateService(menu, new FakeState(), minigame);
        GameState current = GameState.Minigame;
        IGameState implementation = minigame;

        Assert.That(service.RefreshStateFromScene(
            GameState.Menu,
            ref current,
            ref implementation,
            false,
            out string failure), Is.False);
        Assert.That(failure, Does.Contain("requires completed minigame cleanup"));
        Assert.That(current, Is.EqualTo(GameState.Minigame));
        Assert.That(minigame.ExitCount, Is.Zero);
        Assert.That(menu.EnterCount, Is.Zero);
    }

    [Test]
    public void SceneRefreshFromCleanedMinigame_TransitionsToMenu()
    {
        FakeState minigame = new FakeState();
        FakeState menu = new FakeState();
        GameStateTransitionService service = CreateService(menu, new FakeState(), minigame);
        GameState current = GameState.Minigame;
        IGameState implementation = minigame;

        Assert.That(service.RefreshStateFromScene(
            GameState.Menu,
            ref current,
            ref implementation,
            true,
            out string failure), Is.True, failure);
        Assert.That(current, Is.EqualTo(GameState.Menu));
        Assert.That(minigame.ExitCount, Is.EqualTo(1));
        Assert.That(menu.EnterCount, Is.EqualTo(1));
    }

    private static GameStateTransitionService CreateService(
        IGameState menu,
        IGameState freePlay,
        IGameState minigame)
    {
        return new GameStateTransitionService(new GameStateResolver(menu, freePlay, minigame));
    }

    private static void AssertDecision(
        GameState from,
        GameState to,
        GameStateTransitionContext context,
        bool expectedAllowed,
        bool expectedNoOp)
    {
        GameStateTransitionDecision decision = GameStateTransitionPolicy.Evaluate(from, to, context);
        Assert.That(decision.IsAllowed, Is.EqualTo(expectedAllowed), decision.Reason);
        Assert.That(decision.IsNoOp, Is.EqualTo(expectedNoOp));
    }

    private sealed class FakeState : IGameState
    {
        public int EnterCount { get; private set; }
        public int ExitCount { get; private set; }
        public bool ThrowOnEnter { get; set; }
        public bool ThrowOnExit { get; set; }

        public void OnStateEnter()
        {
            EnterCount++;
            if (ThrowOnEnter)
            {
                throw new InvalidOperationException("enter test failure");
            }
        }

        public void OnStateUpdate()
        {
        }

        public void OnStateExit()
        {
            ExitCount++;
            if (ThrowOnExit)
            {
                throw new InvalidOperationException("exit test failure");
            }
        }
    }
}
