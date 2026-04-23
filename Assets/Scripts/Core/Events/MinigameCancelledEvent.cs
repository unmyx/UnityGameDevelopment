using Game.Minigames;

namespace Game.Core.Events
{
    public struct MinigameCancelledEvent
    {
        public MinigameResult Result { get; }
        public string MinigameId { get; }
        public string OwnerPlayerId { get; }

        public MinigameCancelledEvent(MinigameResult result)
            : this(result, string.Empty, "local_player_0")
        {
        }

        public MinigameCancelledEvent(MinigameResult result, string minigameId, string ownerPlayerId)
        {
            Result = result;
            MinigameId = minigameId;
            OwnerPlayerId = ownerPlayerId;
        }
    }
}
