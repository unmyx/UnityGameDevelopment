namespace Game.Inventory
{
    /// <summary>
    /// InventorySlot represents a single slot in the inventory grid.
    /// It holds the grid position and the item occupying that slot.
    /// 
    /// Design:
    /// - Lightweight data class (not MonoBehaviour)
    /// - Used primarily to represent grid state for UI integration
    /// - Can be serialized for save/load systems
    /// - Immutable grid position (x, y)
    /// 
    /// Usage:
    /// - InventorySystem.GetSlot(x, y) returns an InventorySlot
    /// - UI reads slots to display items
    /// - Don't create slots directly - InventorySystem manages them
    /// 
    /// Future Extensions:
    /// - Add slot effects/highlights (selected, highlighted, etc.)
    /// - Add slot locking  (can't move items out)
    /// - Add slot damage (degradation items)
    /// </summary>
    public class InventorySlot
    {
        public readonly int gridX;
        public readonly int gridY;

        private InventoryItem _item;

        public InventorySlot(int x, int y)
        {
            gridX = x;
            gridY = y;
            _item = null;
        }

        public bool IsEmpty()
        {
            return _item == null;
        }

        public bool HasItem()
        {
            return _item != null;
        }

        public InventoryItem GetItem()
        {
            return _item;
        }

        public void SetItem(InventoryItem item)
        {
            _item = item;
        }

        public void ClearSlot()
        {
            _item = null;
        }

        public InventoryItem TakeItem()
        {
            InventoryItem item = _item;
            _item = null;
            return item;
        }

        public override string ToString()
        {
            if (_item == null)
            {
                return $"[{gridX}, {gridY}] Empty";
            }
            return $"[{gridX}, {gridY}] {_item.ItemName}";
        }
    }
}
