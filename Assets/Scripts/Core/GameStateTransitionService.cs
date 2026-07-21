namespace Game.Core
{
    public sealed class GameStateTransitionService
    {
        private GameStateResolver _resolver;

        public GameStateTransitionService(GameStateResolver resolver)
        {
            SetResolver(resolver);
        }

        public void SetResolver(GameStateResolver resolver)
        {
            _resolver = resolver;
        }

        public bool RefreshStateFromScene(
            GameState targetState,
            ref GameState currentState,
            ref IGameState currentStateImplementation,
            bool minigameCleanupCompleted,
            out string failureReason)
        {
            if (_resolver == null)
            {
                failureReason = "State resolver is not initialized.";
                return false;
            }

            GameStateTransitionDecision decision = GameStateTransitionPolicy.Evaluate(
                currentState,
                targetState,
                GameStateTransitionContext.SceneRefresh,
                minigameCleanupCompleted);
            if (!decision.IsAllowed)
            {
                failureReason = decision.Reason;
                return false;
            }

            if (!_resolver.TryResolveStateImplementation(targetState, out IGameState implementation, out failureReason))
            {
                return false;
            }

            if (currentState == targetState
                && ReferenceEquals(currentStateImplementation, implementation)
                && GameStateResolver.IsUsableImplementation(currentStateImplementation))
            {
                failureReason = string.Empty;
                return true;
            }

            bool isTransitioning = false;
            return ExecuteLifecycleTransition(
                targetState,
                implementation,
                ref currentState,
                ref currentStateImplementation,
                ref isTransitioning,
                out failureReason);
        }

        public bool CanChangeState(GameState currentState, GameState newState, out bool isNoOp, out string failureReason)
        {
            GameStateTransitionDecision decision = GameStateTransitionPolicy.Evaluate(
                currentState,
                newState,
                GameStateTransitionContext.Local);
            isNoOp = decision.IsNoOp;
            failureReason = decision.Reason;
            return decision.IsAllowed;
        }

        public bool ChangeState(
            GameState newState,
            ref GameState currentState,
            ref IGameState currentStateImplementation,
            ref bool isTransitioning,
            out string failureReason)
        {
            failureReason = string.Empty;

            if (_resolver == null)
            {
                failureReason = "State resolver is not initialized.";
                return false;
            }

            if (!CanChangeState(currentState, newState, out bool isNoOp, out failureReason))
            {
                return false;
            }

            if (isNoOp)
            {
                return true;
            }

            if (isTransitioning)
            {
                failureReason = $"Transition '{currentState}' -> '{newState}' rejected because another transition is active.";
                return false;
            }

            if (!_resolver.TryResolveStateImplementation(newState, out IGameState nextStateImplementation, out failureReason))
            {
                return false;
            }

            return ExecuteLifecycleTransition(
                newState,
                nextStateImplementation,
                ref currentState,
                ref currentStateImplementation,
                ref isTransitioning,
                out failureReason);
        }

        private static bool ExecuteLifecycleTransition(
            GameState newState,
            IGameState nextStateImplementation,
            ref GameState currentState,
            ref IGameState currentStateImplementation,
            ref bool isTransitioning,
            out string failureReason)
        {
            GameState previousState = currentState;
            IGameState previousImplementation = currentStateImplementation;
            bool previousExited = false;

            isTransitioning = true;
            try
            {
                if (GameStateResolver.IsUsableImplementation(previousImplementation))
                {
                    try
                    {
                        previousImplementation.OnStateExit();
                        previousExited = true;
                    }
                    catch (System.Exception exception)
                    {
                        failureReason = $"State '{previousState}' exit failed: {exception.Message}";
                        return false;
                    }
                }

                currentState = newState;
                currentStateImplementation = nextStateImplementation;
                try
                {
                    currentStateImplementation.OnStateEnter();
                    failureReason = string.Empty;
                    return true;
                }
                catch (System.Exception exception)
                {
                    currentState = previousState;
                    currentStateImplementation = previousImplementation;

                    string rollbackFailure = string.Empty;
                    if (previousExited && GameStateResolver.IsUsableImplementation(previousImplementation))
                    {
                        try
                        {
                            previousImplementation.OnStateEnter();
                        }
                        catch (System.Exception rollbackException)
                        {
                            rollbackFailure = $" Rollback enter also failed: {rollbackException.Message}";
                        }
                    }

                    failureReason = $"State '{newState}' enter failed: {exception.Message}.{rollbackFailure}";
                    return false;
                }
            }
            finally
            {
                isTransitioning = false;
            }
        }
    }
}
