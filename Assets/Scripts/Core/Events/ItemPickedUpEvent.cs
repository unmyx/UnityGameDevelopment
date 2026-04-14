namespace Game.Core.Events
{
    public struct ItemPickedUpEvent
    {
        public string ItemName { get; }

        public ItemPickedUpEvent(string itemName)
        {
            ItemName = itemName;
        }
    }
}
