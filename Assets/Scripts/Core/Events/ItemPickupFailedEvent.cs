namespace Game.Core.Events
{
    public struct ItemPickupFailedEvent
    {
        public string ItemName { get; }

        public ItemPickupFailedEvent(string itemName)
        {
            ItemName = itemName;
        }
    }
}
