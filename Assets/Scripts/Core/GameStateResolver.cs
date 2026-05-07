using System;

namespace Game.Core
{
    internal sealed class GameStateResolver
    {
        private readonly MenuState _menuState;
        private readonly FreePlayState _freePlayState;
        private readonly MinigameState _minigameState;

        public GameStateResolver(MenuState menuState, FreePlayState freePlayState, MinigameState minigameState)
        {
            _menuState = menuState;
            _freePlayState = freePlayState;
            _minigameState = minigameState;
        }

        public GameState SanitizeBootstrapState(GameState configuredState, out bool remappedFromMinigame)
        {
            if (configuredState == GameState.Minigame)
            {
                remappedFromMinigame = true;
                return GameState.FreePlay;
            }

            remappedFromMinigame = false;
            return configuredState;
        }

        public bool TryResolveStateFromSceneName(string activeSceneName, out GameState state)
        {
            if (string.Equals(activeSceneName, SceneIds.Menu, StringComparison.Ordinal))
            {
                state = GameState.Menu;
                return true;
            }

            if (string.Equals(activeSceneName, SceneIds.Home, StringComparison.Ordinal)
                || string.Equals(activeSceneName, SceneIds.Gameplay, StringComparison.Ordinal))
            {
                state = GameState.FreePlay;
                return true;
            }

            state = default;
            return false;
        }

        public bool TryResolveStateImplementation(GameState state, out IGameState implementation, out string failureReason)
        {
            switch (state)
            {
                case GameState.Menu:
                    implementation = _menuState;
                    break;

                case GameState.FreePlay:
                    implementation = _freePlayState;
                    break;

                case GameState.Minigame:
                    implementation = _minigameState;
                    break;

                default:
                    implementation = null;
                    failureReason = $"Unsupported state value '{state}'.";
                    return false;
            }

            if (implementation == null)
            {
                failureReason = $"Missing required {state} state implementation reference.";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }
    }
}
