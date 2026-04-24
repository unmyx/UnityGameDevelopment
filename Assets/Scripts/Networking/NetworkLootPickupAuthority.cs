using Game.Interaction;
using Game.Inventory;
using Game.Core;
using Game.Core.Events;
using Game.Player;
using Unity.Netcode;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// Host-authoritative loot world-state handler.
    /// Used by NetworkInteractionAuthorityBridge to process loot pickups once on the server.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(InteractableItem))]
    public class NetworkLootPickupAuthority : NetworkBehaviour, INetworkAuthoritativeInteractionHandler
    {
        [SerializeField] private bool _enableLogs = true;

        private NetworkObject _networkObject;
        private InteractableItem _interactableItem;
        private bool _consumed;

        private void Awake()
        {
            _networkObject = GetComponent<NetworkObject>();
            _interactableItem = GetComponent<InteractableItem>();
        }

        public bool CanProcessAuthoritativeInteraction(
            ulong senderClientId,
            Transform senderPlayerTransform,
            out string rejectReason)
        {
            if (_consumed)
            {
                rejectReason = "already_consumed";
                return false;
            }

            if (_networkObject == null || !_networkObject.IsSpawned)
            {
                rejectReason = "target_not_spawned";
                return false;
            }

            if (_interactableItem == null || !_interactableItem.isActiveAndEnabled)
            {
                rejectReason = "interactable_missing";
                return false;
            }

            if (!_interactableItem.TryGetNetworkPickupPayload(out _, out _))
            {
                rejectReason = "invalid_pickup_payload";
                return false;
            }

            rejectReason = string.Empty;
            return true;
        }

        public void ProcessAuthoritativeInteraction(
            ulong senderClientId,
            Transform senderPlayerTransform)
        {
            if (_consumed || _networkObject == null || !_networkObject.IsSpawned)
            {
                return;
            }

            _consumed = true;
            if (_interactableItem != null)
            {
                _interactableItem.SetCanInteract(false);
            }

            if (_interactableItem == null || !_interactableItem.TryGetNetworkPickupPayload(out string itemId, out bool countsAsStolenLoot))
            {
                if (_enableLogs)
                {
                    Debug.LogWarning(
                        $"[NetworkLootPickupAuthority] Missing pickup payload. sender={senderClientId}, object={_networkObject.NetworkObjectId}");
                }

                _networkObject.Despawn(true);
                return;
            }

            string ownerKey = NetworkOwnerKeyUtility.GetOwnerKeyForSender(senderClientId);
            if (countsAsStolenLoot)
            {
                GameManager gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.TryRegisterStolenLootPickup(itemId, ownerKey);
                    NetworkSessionProgressAuthority.TrySyncStolenLootSnapshotToClient(senderClientId, ownerKey);
                }
            }

            ClientRpcParams grantTarget = new ClientRpcParams
            {
                Send = new ClientRpcSendParams
                {
                    TargetClientIds = new[] { senderClientId }
                }
            };

            GrantPickupClientRpc(itemId, countsAsStolenLoot, grantTarget);

            if (_enableLogs)
            {
                Debug.Log($"[NetworkLootPickupAuthority] Loot pickup accepted from client {senderClientId}. Granting item '{itemId}' and despawning network loot.");
            }

            _networkObject.Despawn(true);
        }

        [ClientRpc]
        private void GrantPickupClientRpc(string itemId, bool countsAsStolenLoot, ClientRpcParams clientRpcParams = default)
        {
            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null)
            {
                if (_enableLogs)
                {
                    Debug.LogWarning("[NetworkLootPickupAuthority] Pickup grant failed: InventorySystem missing.");
                }
                return;
            }

            InventoryItem itemAsset = inventorySystem.LoadItemById(itemId);
            if (itemAsset == null || !itemAsset.IsValid())
            {
                if (_enableLogs)
                {
                    Debug.LogWarning($"[NetworkLootPickupAuthority] Pickup grant failed: item asset not found for '{itemId}'.");
                }
                return;
            }

            _ = countsAsStolenLoot;
            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            bool addSucceeded = inventorySystem.AddItem(itemAsset, ownerPlayerId);
            if (!addSucceeded)
            {
                if (_enableLogs)
                {
                    Debug.LogWarning($"[NetworkLootPickupAuthority] Pickup grant failed: InventorySystem.AddItem rejected '{itemId}'.");
                }
                return;
            }

            EventBus.Publish(new ItemPickedUpEvent(itemAsset.ItemName));
        }
    }
}
