using Game.Minigames;

namespace Game.Core.Events
{
    public struct MinigameEndedEvent
    {
        public MinigameResult Result { get; }
        public string MinigameId { get; }
        public string OwnerPlayerId { get; }

        public MinigameEndedEvent(MinigameResult result)
            : this(result, string.Empty, "local_player_0")
        {
        }

        public MinigameEndedEvent(MinigameResult result, string minigameId, string ownerPlayerId)
        {
            Result = result;
            MinigameId = minigameId;
            OwnerPlayerId = ownerPlayerId;
        }
    }
}
