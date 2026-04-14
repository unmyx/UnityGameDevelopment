using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Data Transfer Object for inventory slot state during save/load operations.
    /// Contains grid position and item identifier for serialization.
    ///
    /// Design:
    /// - Lightweight serializable class (no MonoBehaviour)
    /// - Used by SaveManager to persist inventory grid state
    /// - References items by ID string (resolved to ScriptableObject on load)
    /// - No game logic - pure data container
    /// </summary>
    [System.Serializable]
    public class InventorySlotData
    {
        public int gridX;
        public int gridY;
        public string itemId;

        /// <summary>
        /// Constructor for creating save data from inventory slot.
        /// </summary>
        public InventorySlotData(int x, int y, string id)
        {
            this.gridX = x;
            this.gridY = y;
            this.itemId = id;
        }
    }
}