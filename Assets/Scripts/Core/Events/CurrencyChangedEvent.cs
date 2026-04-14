namespace Game.Core.Events
{
    public struct CurrencyChangedEvent
    {
        public int Amount { get; }

        public CurrencyChangedEvent(int amount)
        {
            Amount = amount;
        }
    }
}
