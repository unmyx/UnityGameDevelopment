namespace Game.Core.Events
{
    public struct MinigameStartedEvent
    {
        public string MinigameId { get; }
        public string OwnerPlayerId { get; }

        public MinigameStartedEvent(string minigameId)
            : this(minigameId, "local_player_0")
        {
        }

        public MinigameStartedEvent(string minigameId, string ownerPlayerId)
        {
            MinigameId = minigameId;
            OwnerPlayerId = ownerPlayerId;
        }
    }
}
