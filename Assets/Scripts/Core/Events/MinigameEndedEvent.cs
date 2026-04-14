using Game.Minigames;

namespace Game.Core.Events
{
    public struct MinigameEndedEvent
    {
        public MinigameResult Result { get; }

        public MinigameEndedEvent(MinigameResult result)
        {
            Result = result;
        }
    }
}
