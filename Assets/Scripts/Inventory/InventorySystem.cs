using UnityEngine;
using System;
using System.Collections.Generic;
using Game.Core;
using Game.Player;
using Unity.Netcode;
using UnityEngine.SceneManagement;

namespace Game.Inventory
{
    /// <summary>
    /// InventorySystem manages a grid-based inventory with fixed dimensions.
    /// 
    /// Features:
    /// - Fixed 5x5 grid (25 slots total)
    /// - Non-stackable unique items
    /// - Add/remove items with auto-placement
    /// - Query inventory state
    /// - Ready for UI integration via GetGridData()
    /// - Supports future equip/use logic
    /// 
    /// Design:
    /// - Grid stored as InventorySlot[,] for easy UI access
    /// - Items are InventoryItem references (ScriptableObjects)
    /// - Singleton pattern (one inventory per player)
    /// - Events for UI synchronization
    /// 
    /// Usage:
    /// InventorySystem.Instance.AddItem(itemAsset)
    /// InventorySystem.Instance.RemoveItem(itemAsset)
    /// InventorySystem.Instance.IsInventoryFull()
    /// InventorySystem.Instance.GetSlot(x, y)
    /// 
    /// Future Extensions:
    /// - EquipItem(itemAsset) - for equipment system
    /// - UseItem(itemAsset) - for consumables
    /// - SwapItems(item1, item2) - for drag & drop
    /// - SaveInventory() / LoadInventory() - for save system
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class InventorySystem : MonoBehaviour
    {
        private const string ItemResourcesPath = ResourcePaths.InventoryItems;

        private static InventorySystem _instance;
        private static bool _hasLoggedFallbackInstanceLookup;
        private static string _instanceBindingSource = "unbound";
        public static InventorySystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindAnyObjectByType<InventorySystem>();
                    if (_instance != null)
                    {
                        LogFallbackInstanceLookup(_instance, "Instance.get");
                    }
                }
                return _instance;
            }
        }

        private const int GRID_WIDTH = 5;
        private const int GRID_HEIGHT = 5;
        private const int MAX_SLOTS = GRID_WIDTH * GRID_HEIGHT;
        private const int QUICK_SLOT_COUNT = 9;


        private InventorySlot[,] _gridSlots;
        private List<InventoryItem> _allItems;
        private readonly Dictionary<string, InventoryItem> _itemCacheById = new Dictionary<string, InventoryItem>();

        public delegate void InventoryChangeDelegate(InventoryItem item, bool added);
        public event InventoryChangeDelegate OnInventoryChanged;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            _instanceBindingSource = "Awake";
            DetachFromParentIfNeeded();
            DontDestroyOnLoad(gameObject);
            EnsureInitialized();
        }

        private void DetachFromParentIfNeeded()
        {
            if (transform.parent != null)
            {
                transform.SetParent(null, true);
            }
        }

        private void Start()
        {
            EnsureInitialized();
        }

        private void InitializeGrid()
        {
            _gridSlots = new InventorySlot[GRID_WIDTH, GRID_HEIGHT];
            _allItems = new List<InventoryItem>();

            for (int y = 0; y < GRID_HEIGHT; y++)
            {
                for (int x = 0; x < GRID_WIDTH; x++)
                {
                    _gridSlots[x, y] = new InventorySlot(x, y);
                }
            }
        }

        private void EnsureInitialized()
        {
            if (_gridSlots != null && _allItems != null)
            {
                return;
            }

            InitializeGrid();
        }

        public bool AddItem(InventoryItem item)
        {
            return AddItem(item, PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public bool AddItem(InventoryItem item, string ownerPlayerId)
        {
            EnsureInitialized();
            PlayerInventoryAuthority.LogNonLocalOwnerUsage("InventorySystem.AddItem", ownerPlayerId, this);

            if (item == null)
            {
                return false;
            }

            if (!item.IsValid())
            {
                return false;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager != null && IsTrackedValuableAtCapacity(item, gameManager))
            {
                return false;
            }

            InventorySlot emptySlot = FindEmptySlot();
            if (emptySlot == null)
            {
                return false;
            }
            emptySlot.SetItem(item);
            _allItems.Add(item);

            OnInventoryChanged?.Invoke(item, true);

            return true;
        }

        public bool AddItemAt(InventoryItem item, int gridX, int gridY)
        {
            return AddItemAt(item, gridX, gridY, PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public bool AddItemAt(InventoryItem item, int gridX, int gridY, string ownerPlayerId)
        {
            EnsureInitialized();
            PlayerInventoryAuthority.LogNonLocalOwnerUsage("InventorySystem.AddItemAt", ownerPlayerId, this);

            if (item == null)
            {
                return false;
            }

            if (!IsValidPosition(gridX, gridY))
            {
                return false;
            }

            InventorySlot slot = _gridSlots[gridX, gridY];
            if (!slot.IsEmpty())
            {
                return false;
            }

            slot.SetItem(item);
            _allItems.Add(item);

            OnInventoryChanged?.Invoke(item, true);

            return true;
        }

        public bool RemoveItem(InventoryItem item)
        {
            EnsureInitialized();

            if (item == null)
            {
                return false;
            }

            for (int y = 0; y < GRID_HEIGHT; y++)
            {
                for (int x = 0; x < GRID_WIDTH; x++)
                {
                    InventorySlot slot = _gridSlots[x, y];
                    if (slot.GetItem() == item)
                    {
                        slot.ClearSlot();
                        _allItems.Remove(item);

                        OnInventoryChanged?.Invoke(item, false);

                        return true;
                    }
                }
            }

            return false;
        }

        public InventoryItem RemoveItemAt(int gridX, int gridY)
        {
            return RemoveItemAt(gridX, gridY, PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public InventoryItem RemoveItemAt(int gridX, int gridY, string ownerPlayerId)
        {
            EnsureInitialized();
            PlayerInventoryAuthority.LogNonLocalOwnerUsage("InventorySystem.RemoveItemAt", ownerPlayerId, this);

            if (!IsValidPosition(gridX, gridY))
            {
                return null;
            }

            InventorySlot slot = _gridSlots[gridX, gridY];
            InventoryItem item = slot.TakeItem();

            if (item != null)
            {
                _allItems.Remove(item);
                OnInventoryChanged?.Invoke(item, false);
            }

            return item;
        }

        public int RemoveItemsByItemId(string itemId, int maxCount)
        {
            return RemoveItemsByItemId(itemId, maxCount, PlayerInventoryAuthority.GetLocalOwnerPlayerId());
        }

        public int RemoveItemsByItemId(string itemId, int maxCount, string ownerPlayerId)
        {
            EnsureInitialized();
            PlayerInventoryAuthority.LogNonLocalOwnerUsage("InventorySystem.RemoveItemsByItemId", ownerPlayerId, this);

            if (string.IsNullOrWhiteSpace(itemId) || maxCount <= 0)
            {
                return 0;
            }

            string normalizedItemId = itemId.Trim();
            int removedCount = 0;

            for (int y = 0; y < GRID_HEIGHT && removedCount < maxCount; y++)
            {
                for (int x = 0; x < GRID_WIDTH && removedCount < maxCount; x++)
                {
                    InventorySlot slot = _gridSlots[x, y];
                    InventoryItem slotItem = slot.GetItem();
                    if (slotItem == null || !string.Equals(slotItem.ItemId, normalizedItemId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    slot.ClearSlot();
                    _allItems.Remove(slotItem);
                    removedCount++;

                    OnInventoryChanged?.Invoke(slotItem, false);
                }
            }

            return removedCount;
        }

        public bool IsInventoryFull()
        {
            EnsureInitialized();
            return _allItems.Count >= MAX_SLOTS;
        }

        public int GetItemCount()
        {
            EnsureInitialized();
            return _allItems.Count;
        }

        public int GetAvailableSlots()
        {
            EnsureInitialized();
            return MAX_SLOTS - _allItems.Count;
        }

        public bool HasSpace()
        {
            return GetAvailableSlots() > 0;
        }

        public bool ContainsItem(InventoryItem item)
        {
            EnsureInitialized();
            return _allItems.Contains(item);
        }

        public List<InventoryItem> GetAllItems()
        {
            EnsureInitialized();
            return new List<InventoryItem>(_allItems);
        }

        public InventorySlot GetSlot(int gridX, int gridY)
        {
            EnsureInitialized();

            if (!IsValidPosition(gridX, gridY))
            {
                return null;
            }

            return _gridSlots[gridX, gridY];
        }

        public bool TryGetQuickSlotItem(int quickSlotIndex, out InventoryItem item)
        {
            item = null;
            EnsureInitialized();

            if (quickSlotIndex < 0 || quickSlotIndex >= QUICK_SLOT_COUNT)
            {
                return false;
            }

            int gridX = quickSlotIndex % GRID_WIDTH;
            int gridY = quickSlotIndex / GRID_WIDTH;
            InventorySlot slot = _gridSlots[gridX, gridY];
            item = slot != null ? slot.GetItem() : null;
            return item != null;
        }

        public InventorySlot[,] GetGridData()
        {
            EnsureInitialized();
            InventorySlot[,] gridCopy = new InventorySlot[GRID_WIDTH, GRID_HEIGHT];
            System.Array.Copy(_gridSlots, gridCopy, _gridSlots.Length);
            return gridCopy;
        }
        public (int width, int height) GetGridDimensions()
        {
            return (GRID_WIDTH, GRID_HEIGHT);
        }


        private InventorySlot FindEmptySlot()
        {
            for (int y = 0; y < GRID_HEIGHT; y++)
            {
                for (int x = 0; x < GRID_WIDTH; x++)
                {
                    if (_gridSlots[x, y].IsEmpty())
                    {
                        return _gridSlots[x, y];
                    }
                }
            }

            return null;
        }

        private bool IsValidPosition(int gridX, int gridY)
        {
            return gridX >= 0 && gridX < GRID_WIDTH && gridY >= 0 && gridY < GRID_HEIGHT;
        }

        private bool IsTrackedValuableAtCapacity(InventoryItem item, GameManager gameManager)
        {
            if (item == null || gameManager == null || !gameManager.IsTrackedStolenLootItem(item.ItemId))
            {
                return false;
            }

            int unlockedQuickSlots = Mathf.Max(0, gameManager.GetUnlockedQuickSlots());
            int trackedValuablesHeld = CountTrackedValuablesInInventory(gameManager);
            if (trackedValuablesHeld < unlockedQuickSlots)
            {
                return false;
            }

            Debug.Log(
                $"[InventorySystem] Blocked tracked valuable pickup '{item.ItemId}' - at capacity ({trackedValuablesHeld}/{unlockedQuickSlots}).",
                this);
            return true;
        }

        private int CountTrackedValuablesInInventory(GameManager gameManager)
        {
            if (gameManager == null || _allItems == null || _allItems.Count == 0)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < _allItems.Count; i++)
            {
                InventoryItem heldItem = _allItems[i];
                if (heldItem == null || !heldItem.IsValid())
                {
                    continue;
                }

                if (gameManager.IsTrackedStolenLootItem(heldItem.ItemId))
                {
                    count++;
                }
            }

            return count;
        }

        public void ClearInventory()
        {
            EnsureInitialized();

            for (int y = 0; y < GRID_HEIGHT; y++)
            {
                for (int x = 0; x < GRID_WIDTH; x++)
                {
                    _gridSlots[x, y].ClearSlot();
                }
            }

            _allItems.Clear();
        }

        public void PrintInventoryState()
        {
        }

         public bool CanEquip(InventoryItem item)
        {
            return item != null && item.CanEquip;
        }

        public bool CanUse(InventoryItem item)
        {
            return item != null && item.CanUse;
        }

        public void EquipItem(InventoryItem item)
        {
            if (item == null || !CanEquip(item))
            {
                return;
            }

        }
        public void UseItem(InventoryItem item)
        {
            if (item == null || !CanUse(item))
            {
                return;
            }
        }

        /// <summary>
        /// Get a snapshot of inventory state for save/load operations.
        /// Returns array of InventorySlotData with grid positions and itemIds.
        /// Empty slots are skipped in the returned array.
        /// </summary>
        public Core.InventorySlotData[] GetInventorySnapshot()
        {
            EnsureInitialized();

            List<Core.InventorySlotData> snapshot = new List<Core.InventorySlotData>();

            for (int y = 0; y < GRID_HEIGHT; y++)
            {
                for (int x = 0; x < GRID_WIDTH; x++)
                {
                    InventoryItem item = _gridSlots[x, y].GetItem();
                    if (item != null && !string.IsNullOrEmpty(item.ItemId))
                    {
                        snapshot.Add(new Core.InventorySlotData(x, y, item.ItemId));
                    }
                }
            }

            return snapshot.ToArray();
        }

        /// <summary>
        /// Restore inventory state from save data.
        /// Used by SaveManager.Load() to reconstruct player's inventory grid.
        /// Clears current inventory and re-adds items at their saved positions.
        /// </summary>
        public void RestoreFromSave(List<Core.InventorySlotData> savedSlotData)
        {
            EnsureInitialized();

            // Clear current inventory
            ClearInventory();

            if (savedSlotData == null || savedSlotData.Count == 0)
            {
                Debug.Log("InventorySystem restored: inventory is empty");
                return;
            }

            int loadedCount = 0;
            int failedCount = 0;

            foreach (var slotData in savedSlotData)
            {
                if (slotData == null || string.IsNullOrEmpty(slotData.itemId))
                    continue;

                InventoryItem item = LoadItemAssetById(slotData.itemId);

                if (item == null)
                {
                    Debug.LogWarning($"Failed to load item asset: {slotData.itemId}");
                    failedCount++;
                    continue;
                }

                // Add item at saved position
                if (AddItemAt(item, slotData.gridX, slotData.gridY))
                {
                    loadedCount++;
                }
                else
                {
                    Debug.LogWarning($"Failed to add item at ({slotData.gridX}, {slotData.gridY}): {slotData.itemId}");
                    failedCount++;
                }
            }

            Debug.Log($"InventorySystem restored: {loadedCount} items loaded, {failedCount} items failed");
        }

        private InventoryItem LoadItemAssetById(string itemId)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return null;
            }

            EnsureItemCacheBuilt();
            if (_itemCacheById.TryGetValue(itemId, out InventoryItem cachedItem))
            {
                return cachedItem;
            }

            return null;
        }

        public InventoryItem LoadItemById(string itemId)
        {
            return LoadItemAssetById(itemId);
        }

        private void EnsureItemCacheBuilt()
        {
            if (_itemCacheById.Count > 0)
            {
                return;
            }

            InventoryItem[] allItems = Resources.LoadAll<InventoryItem>(ItemResourcesPath);
            foreach (InventoryItem item in allItems)
            {
                if (item == null || string.IsNullOrEmpty(item.ItemId))
                {
                    continue;
                }

                _itemCacheById[item.ItemId] = item;
            }
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void LogFallbackInstanceLookup(InventorySystem resolvedInstance, string callsite)
        {
            if (_hasLoggedFallbackInstanceLookup || resolvedInstance == null)
            {
                return;
            }

            _hasLoggedFallbackInstanceLookup = true;
            string sceneName = SceneManager.GetActiveScene().name;
            NetworkManager networkManager = NetworkManager.Singleton;
            string netMode = "offline";
            if (networkManager != null && networkManager.IsListening)
            {
                netMode = networkManager.IsServer
                    ? (networkManager.IsClient ? "host" : "server")
                    : "client";
            }

            Debug.LogWarning(
                $"[InventorySystem] Fallback instance scan used at '{callsite}' in scene '{sceneName}' ({netMode}). " +
                $"Resolved '{resolvedInstance.name}' via FindAnyObjectByType. BindingSource={_instanceBindingSource}. " +
                "Behavior remains permissive for bootstrap/recovery compatibility.");
        }
    }
}

