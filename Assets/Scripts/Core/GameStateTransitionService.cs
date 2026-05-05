namespace Game.Core
{
    internal sealed class GameStateTransitionService
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
            out string failureReason)
        {
            if (_resolver == null)
            {
                failureReason = "State resolver is not initialized.";
                return false;
            }

            if (!_resolver.TryResolveStateImplementation(targetState, out IGameState implementation, out failureReason))
            {
                currentStateImplementation = null;
                return false;
            }

            currentState = targetState;
            currentStateImplementation = implementation;
            currentStateImplementation.OnStateEnter();
            failureReason = string.Empty;
            return true;
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

            if (currentState == newState || isTransitioning)
            {
                return true;
            }

            if (!_resolver.TryResolveStateImplementation(newState, out IGameState nextStateImplementation, out failureReason))
            {
                return false;
            }

            isTransitioning = true;
            try
            {
                currentStateImplementation?.OnStateExit();

                currentState = newState;
                currentStateImplementation = nextStateImplementation;
                currentStateImplementation.OnStateEnter();
                return true;
            }
            finally
            {
                isTransitioning = false;
            }
        }
    }
}
