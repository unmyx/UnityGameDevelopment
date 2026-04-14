using Game.Minigames;

namespace Game.Core.Events
{
    public struct MinigameCancelledEvent
    {
        public MinigameResult Result { get; }

        public MinigameCancelledEvent(MinigameResult result)
        {
            Result = result;
        }
    }
}
