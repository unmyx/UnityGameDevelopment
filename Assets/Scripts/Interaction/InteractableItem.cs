using UnityEngine;
using Game.Inventory;
using Game.Core;
using Game.Core.Events;
using Game.Player;
using Game.Networking;
using Unity.Netcode;

namespace Game.Interaction
{
    /// <summary>
    /// InteractableItem represents a pickable item in the world.
    /// Extends BaseInteractable to provide pickup/collection functionality.
    /// 
    /// Features:
    /// - Adds items to inventory via InventorySystem
    /// - Visual feedback on interaction (highlight, color change)
    /// - Pickup behavior (destroy or disable)
    /// - Respects inventory capacity (doesn't destroy if full)
    /// - Event feedback on pickup success/failure
    /// 
    /// Setup:
    /// 1. Create a GameObject with a collider and this script
    /// 2. Optionally add a rigidbody (kinematic) for physics
    /// 3. Assign an InventoryItem ScriptableObject in inspector
    /// 4. Game will automatically add item to inventory on pickup
    /// 
    /// Behavior:
    /// - If inventory has space: Item added, object destroyed/disabled
    /// - If inventory full: Item NOT added, object remains (can retry later)
    /// - Event fired: "ItemPickedUp" on success, "ItemPickupFailed" on failure
    /// </summary>
    public class InteractableItem : BaseInteractable
    {
        [SerializeField]
        [Tooltip("The InventoryItem asset to add when picked up")]
        private InventoryItem _inventoryItem;

        [SerializeField]
        private string _itemName = "Item";

        [SerializeField]
        private string _itemDescription = "";

        [SerializeField]
        private bool _destroyOnPickup = true;

        [SerializeField]
        private float _pickupCooldown = 0.1f;

        [Header("Stolen Loot Tracking")]
        [SerializeField]
        [Tooltip("When enabled, successful pickup of this world item is tracked as stolen loot for the current day.")]
        private bool _countsAsStolenLoot = false;

        [Header("Optional Persistence")]
        [SerializeField]
        [Tooltip("When enabled, this collectible will stay consumed across save/load using the persistent ID below.")]
        private bool _persistCollectedState = false;

        [SerializeField]
        [Tooltip("Stable unique ID for this collectible instance (for example: loot.gold_ring_pickup.main).")]
        private string _collectiblePersistenceId = string.Empty;

        private float _lastPickupTime = 0f;
        private Collider _collider;
        private Renderer _renderer;
        private MaterialPropertyBlock _materialPropertyBlock;
        private bool _canOverrideColor;
        private bool _hasBeenPickedUp = false;
        private bool _hasLoggedMissingPersistenceId;
        private bool _stolenLootRegistered;

        private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");

        private void Start()
        {
            if (TryGetPersistentCollectibleId(out string collectibleId, logWarning: true)
                && SaveManager.IsCollectibleConsumed(collectibleId))
            {
                ApplyConsumedPersistenceState();
                return;
            }

            _collider = GetComponent<Collider>();
            _renderer = GetComponent<Renderer>();

            if (_renderer != null)
            {
                _materialPropertyBlock = new MaterialPropertyBlock();
                Material sharedMaterial = _renderer.sharedMaterial;
                _canOverrideColor = sharedMaterial != null && sharedMaterial.HasProperty(ColorPropertyId);
            }
        }

        public override void Interact()
        {
            if (!CanInteract)
            {
                return;
            }

            if (ShouldDeferToNetworkAuthorityBridge())
            {
                return;
            }

            if (Time.time - _lastPickupTime < _pickupCooldown)
            {
                return;
            }

            _lastPickupTime = Time.time;

            // Try to add item to inventory
            bool pickupSucceeded = PickupItem();
            _hasBeenPickedUp = pickupSucceeded;

            // Only destroy/disable if pickup was successful
            if (pickupSucceeded)
            {
                TryRegisterStolenLootPickup();

                if (TryGetPersistentCollectibleId(out string collectibleId, logWarning: true))
                {
                    SaveManager.RegisterConsumedCollectible(collectibleId);
                }

                if (_destroyOnPickup)
                {
                    Destroy(gameObject);
                }
                else
                {
                    gameObject.SetActive(false);
                }
            }
            else
            {
                // Pickup failed (probably inventory full) - fire event for UI feedback
                EventBus.Publish(new ItemPickupFailedEvent(_itemName));
            }
        }

        public override void OnInteractableEnter()
        {
            // Highlight item when player looks at it (unless already picked up)
            if (_hasBeenPickedUp)
                return;

            if (_renderer != null)
            {
                ApplyColorOverride(new Color(1.2f, 1.2f, 1.2f, 1f));
            }
        }

        public override void OnInteractableExit()
        {
            // Restore color when player looks away
            if (_hasBeenPickedUp)
                return;

            if (_renderer != null)
            {
                ApplyColorOverride(Color.white);
            }
        }

        protected virtual bool PickupItem()
        {
            // Use assigned InventoryItem if available
            if (_inventoryItem != null)
            {
                InventorySystem inventorySystem = InventorySystem.Instance;
                if (inventorySystem != null)
                {
                    string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
                    bool success = inventorySystem.AddItem(_inventoryItem, ownerPlayerId);
                    if (success)
                    {
                        // Fire event for UI/game feedback
                        EventBus.Publish(new ItemPickedUpEvent(_inventoryItem.ItemName));
                    }
                    return success;
                }
            }

            // Fallback: no inventory item assigned
            return false;
        }

        public virtual ItemData GetItemData()
        {
            return new ItemData
            {
                itemName = _itemName,
                itemDescription = _itemDescription,
                itemType = ItemType.Generic
            };
        }

        public string GetItemName()
        {
            return _itemName;
        }

        public string GetItemDescription()
        {
            return _itemDescription;
        }

        public bool HasBeenPickedUp()
        {
            return _hasBeenPickedUp;
        }

        public void ResetItem()
        {
            _hasBeenPickedUp = false;
            _stolenLootRegistered = false;
            _canInteract = true;
            gameObject.SetActive(true);
            if (_renderer != null)
            {
                ApplyColorOverride(Color.white);
            }
        }

        public bool TryGetPersistentCollectibleId(out string collectibleId)
        {
            return TryGetPersistentCollectibleId(out collectibleId, logWarning: false);
        }

        public bool TryGetNetworkPickupPayload(out string itemId, out bool countsAsStolenLoot)
        {
            itemId = string.Empty;
            countsAsStolenLoot = _countsAsStolenLoot;

            if (_inventoryItem == null || !_inventoryItem.IsValid() || string.IsNullOrWhiteSpace(_inventoryItem.ItemId))
            {
                return false;
            }

            itemId = _inventoryItem.ItemId.Trim();
            return true;
        }

        public void ConfigureRuntimePersistenceId(string collectibleId)
        {
            _persistCollectedState = true;
            _collectiblePersistenceId = string.IsNullOrWhiteSpace(collectibleId)
                ? string.Empty
                : collectibleId.Trim();
            _hasLoggedMissingPersistenceId = false;
        }

        public void DisablePersistenceForRuntimeDrop()
        {
            _persistCollectedState = false;
            _collectiblePersistenceId = string.Empty;
            _hasLoggedMissingPersistenceId = false;
        }

        public void ApplyConsumedPersistenceState()
        {
            _hasBeenPickedUp = true;
            gameObject.SetActive(false);
        }

        private void TryRegisterStolenLootPickup()
        {
            if (!_countsAsStolenLoot || _stolenLootRegistered)
            {
                return;
            }

            if (_inventoryItem == null || !_inventoryItem.IsValid())
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            if (gameManager.TryRegisterStolenLootPickup(_inventoryItem.ItemId, ownerPlayerId))
            {
                _stolenLootRegistered = true;
            }
        }

        private bool TryGetPersistentCollectibleId(out string collectibleId, bool logWarning)
        {
            collectibleId = null;

            if (!_persistCollectedState)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(_collectiblePersistenceId))
            {
                if (logWarning && !_hasLoggedMissingPersistenceId)
                {
                    _hasLoggedMissingPersistenceId = true;
                    Debug.LogWarning(
                        "[InteractableItem] Persistence is enabled but Collectible Persistence ID is empty. " +
                        "Assign a stable unique ID to avoid collectible reappearance after load.",
                        this);
                }

                return false;
            }

            collectibleId = _collectiblePersistenceId.Trim();
            return collectibleId.Length > 0;
        }

        private void ApplyColorOverride(Color color)
        {
            if (_renderer == null || !_canOverrideColor)
            {
                return;
            }

            if (_materialPropertyBlock == null)
            {
                _materialPropertyBlock = new MaterialPropertyBlock();
            }

            _renderer.GetPropertyBlock(_materialPropertyBlock);
            _materialPropertyBlock.SetColor(ColorPropertyId, color);
            _renderer.SetPropertyBlock(_materialPropertyBlock);
        }

        private bool ShouldDeferToNetworkAuthorityBridge()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
            {
                return false;
            }

            NetworkInteractionAuthorityBridge authorityBridge = GetComponent<NetworkInteractionAuthorityBridge>();
            return authorityBridge != null && authorityBridge.ShouldRouteThroughNetworkAuthority();
        }
    }

    public struct ItemData
    {
        public string itemName;
        public string itemDescription;
        public ItemType itemType;
    }

    public enum ItemType
    {
        Generic,
        Key,
        Currency,
        Consumable,
        Equipment,
        Quest
    }
}


