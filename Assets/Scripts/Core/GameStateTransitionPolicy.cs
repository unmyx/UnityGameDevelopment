using System;

namespace Game.Core
{
    public enum GameStateTransitionContext
    {
        Local,
        SceneRefresh
    }

    public readonly struct GameStateTransitionDecision
    {
        public GameStateTransitionDecision(bool isAllowed, bool isNoOp, string reason)
        {
            IsAllowed = isAllowed;
            IsNoOp = isNoOp;
            Reason = reason ?? string.Empty;
        }

        public bool IsAllowed { get; }
        public bool IsNoOp { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// Single authority for the legal GameState transition matrix.
    /// Runtime guards such as pause and network authority are evaluated by callers before execution.
    /// </summary>
    public static class GameStateTransitionPolicy
    {
        public static GameStateTransitionDecision Evaluate(
            GameState from,
            GameState to,
            GameStateTransitionContext context,
            bool minigameCleanupCompleted = false)
        {
            if (!IsKnownState(from) || !IsKnownState(to))
            {
                return Denied($"Unsupported transition value from='{from}' to='{to}'.");
            }

            if (from == to)
            {
                if (from == GameState.Minigame)
                {
                    return Denied("Minigame cannot transition to itself.");
                }

                return Allowed(isNoOp: true);
            }

            if (context == GameStateTransitionContext.Local)
            {
                if (from == GameState.FreePlay && to == GameState.Minigame)
                {
                    return Allowed();
                }

                if (from == GameState.Minigame && to == GameState.FreePlay)
                {
                    return Allowed();
                }

                return Denied($"Local transition '{from}' -> '{to}' is not allowed.");
            }

            if (context != GameStateTransitionContext.SceneRefresh)
            {
                return Denied($"Unsupported transition context '{context}'.");
            }

            if (to == GameState.Minigame)
            {
                return Denied("A scene refresh cannot enter Minigame state.");
            }

            if (from == GameState.Minigame && !minigameCleanupCompleted)
            {
                return Denied($"Scene transition '{from}' -> '{to}' requires completed minigame cleanup.");
            }

            bool isSceneMappedTransition =
                (from == GameState.Menu && to == GameState.FreePlay)
                || (from == GameState.FreePlay && to == GameState.Menu)
                || (from == GameState.Minigame && (to == GameState.FreePlay || to == GameState.Menu));

            return isSceneMappedTransition
                ? Allowed()
                : Denied($"Scene refresh transition '{from}' -> '{to}' is not allowed.");
        }

        public static bool IsTransitionAllowed(
            GameState from,
            GameState to,
            GameStateTransitionContext context,
            bool minigameCleanupCompleted = false)
        {
            return Evaluate(from, to, context, minigameCleanupCompleted).IsAllowed;
        }

        private static bool IsKnownState(GameState state)
        {
            return Enum.IsDefined(typeof(GameState), state);
        }

        private static GameStateTransitionDecision Allowed(bool isNoOp = false)
        {
            return new GameStateTransitionDecision(true, isNoOp, string.Empty);
        }

        private static GameStateTransitionDecision Denied(string reason)
        {
            return new GameStateTransitionDecision(false, false, reason);
        }
    }
}
