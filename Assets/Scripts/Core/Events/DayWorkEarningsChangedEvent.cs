namespace Game.Core.Events
{
    public struct DayWorkEarningsChangedEvent
    {
        public int Amount { get; }

        public DayWorkEarningsChangedEvent(int amount)
        {
            Amount = amount;
        }
    }
}
