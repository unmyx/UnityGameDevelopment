namespace Game.Core.Events
{
    /// <summary>
    /// Lightweight player-facing feedback message for short HUD notifications.
    /// </summary>
    public readonly struct PlayerFeedbackEvent
    {
        public readonly string Message;

        public PlayerFeedbackEvent(string message)
        {
            Message = message;
        }
    }
}