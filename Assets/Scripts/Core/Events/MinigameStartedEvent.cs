namespace Game.Core.Events
{
    public struct MinigameStartedEvent
    {
        public string MinigameId { get; }

        public MinigameStartedEvent(string minigameId)
        {
            MinigameId = minigameId;
        }
    }
}
