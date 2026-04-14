namespace Game.Core
{
    /// <summary>
    /// Interface for game state implementations.
    /// Defines the lifecycle of a game state with enter, update, and exit callbacks.
    /// </summary>
    public interface IGameState
    {
        void OnStateEnter();
        void OnStateUpdate();
        void OnStateExit();
    }
}
