using UnityEngine;

namespace Game.Inventory
{
    /// <summary>
    /// InventoryItem is a ScriptableObject that defines the properties of an item.
    /// This is the data definition for items - one asset per unique item type.
    /// 
    /// Design:
    /// - Each unique item in the game has one InventoryItem asset in Resources/Items/
    /// - Multiple instances of the same item can exist in the inventory
    /// - Grid width/height support for future multi-cell inventory items
    /// - Equip/Use flags for future equipment and consumable logic
    /// 
    /// Usage:
    /// 1. Create a new InventoryItem asset via Create > Inventory > Item
    /// 2. Set properties: name, description, icon, grid size
    /// 3. Reference in code: InventorySystem.AddItem(itemAsset)
    /// 
    /// Future Extensions:
    /// - Equipment system: canEquip flag
    /// - Consumables: canUse flag
    /// - Rarity system: add rarity field
    /// - Value system: add buy/sell prices
    /// </summary>
    /// 
    [CreateAssetMenu(fileName = "NewInventoryItem", menuName = "Game/Inventory/Inventory Item")]
    public class InventoryItem : ScriptableObject
    {

        [SerializeField]
        private string _itemId = "item_default";

        [SerializeField]
        private string _itemName = "Item";

        [SerializeField]
        [TextArea(1, 3)]
        private string _itemDescription = "A generic item.";

        [SerializeField]
        private Sprite _itemIcon;

        [SerializeField]
        [Tooltip("Optional world-space prefab used when this item is shown as a held first-person object.")]
        private GameObject _heldPrefab;

        [SerializeField]
        [Tooltip("Optional prefab used for UI model previews. Falls back to Held Prefab when not assigned.")]
        private GameObject _previewPrefab;

        [SerializeField]
        [Range(1, 5)]
        private int _gridWidth = 1;

        [SerializeField]
        [Range(1, 5)]
        private int _gridHeight = 1;

        [SerializeField]
        private ItemType _itemType = ItemType.Miscellaneous;

        [SerializeField]
        private bool _canEquip = false;

        [SerializeField]
        private bool _canUse = false;

        public string ItemId => _itemId;
        public string ItemName => _itemName;
        public string ItemDescription => _itemDescription;
        public Sprite ItemIcon => _itemIcon;
        public GameObject HeldPrefab => _heldPrefab;
        public GameObject PreviewPrefab => _previewPrefab;
        public int GridWidth => _gridWidth;
        public int GridHeight => _gridHeight;
        public ItemType Type => _itemType;
        public bool CanEquip => _canEquip;
        public bool CanUse => _canUse;

        public bool IsValid()
        {
            if (string.IsNullOrEmpty(_itemId))
            {
                return false;
            }

            if (string.IsNullOrEmpty(_itemName))
            {
                return false;
            }

            return true;
        }

        public override string ToString()
        {
            return $"[{_itemId}] {_itemName} ({_gridWidth}x{_gridHeight})";
        }
    }
    public enum ItemType
    {
        Miscellaneous,
        Key,
        Currency,
        Consumable,
        Equipment,
        Quest,
        Weapon,
        Armor,
        Tool
    }
}

