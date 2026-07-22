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

            if (!CanAcceptPickupForCapacity(senderClientId, out string capacityRejectReason))
            {
                rejectReason = capacityRejectReason;
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

            if (!CanAcceptPickupForCapacity(senderClientId, out string capacityRejectReason))
            {
                _consumed = false;
                if (_interactableItem != null)
                {
                    _interactableItem.SetCanInteract(true);
                }

                if (_enableLogs)
                {
                    Debug.LogWarning(
                        $"[NetworkLootPickupAuthority] Pickup rejected for client {senderClientId}: {capacityRejectReason}. item='{itemId}'.");
                }

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
                Game.Core.DevelopmentDiagnostics.Log($"[NetworkLootPickupAuthority] Loot pickup accepted from client {senderClientId}. Granting item '{itemId}' and despawning network loot.");
            }

            _networkObject.Despawn(true);
        }

        private static bool CanAcceptPickupForCapacity(ulong senderClientId, out string rejectReason)
        {
            rejectReason = string.Empty;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return true;
            }

            if (gameManager.GetCurrentRunPhase() != GameManager.RunPhase.Work || gameManager.IsRunFailed())
            {
                return true;
            }

            string ownerKey = NetworkOwnerKeyUtility.GetOwnerKeyForSender(senderClientId);
            int trackedHeld = 0;
            System.Collections.Generic.List<StolenLootEntryData> snapshot = gameManager.GetStolenLootThisDaySnapshot(ownerKey);
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    StolenLootEntryData entry = snapshot[i];
                    if (entry == null)
                    {
                        continue;
                    }

                    trackedHeld += Mathf.Max(0, entry.count);
                }
            }
            int capacity = Mathf.Max(0, gameManager.GetUnlockedQuickSlots());
            if (trackedHeld >= capacity)
            {
                rejectReason = $"tracked_capacity_full ({trackedHeld}/{capacity})";
                return false;
            }

            return true;
        }

        [ClientRpc]
        private void GrantPickupClientRpc(string itemId, bool countsAsStolenLoot, ClientRpcParams clientRpcParams = default)
        {
            ulong localClientId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0UL;
            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null)
            {
                if (_enableLogs)
                {
                    Debug.LogWarning($"[NetworkLootPickupAuthority] Pickup grant failed on client {localClientId} for item '{itemId}': InventorySystem missing.");
                }
                return;
            }

            InventoryItem itemAsset = inventorySystem.LoadItemById(itemId);
            if (itemAsset == null || !itemAsset.IsValid())
            {
                if (_enableLogs)
                {
                    Debug.LogWarning($"[NetworkLootPickupAuthority] Pickup grant failed on client {localClientId} for item '{itemId}': item asset not found.");
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
                    Debug.LogWarning($"[NetworkLootPickupAuthority] Pickup grant failed on client {localClientId} for item '{itemId}': InventorySystem.AddItem rejected.");
                }
                return;
            }

            if (_enableLogs)
            {
                Game.Core.DevelopmentDiagnostics.Log($"[NetworkLootPickupAuthority] Pickup grant succeeded on client {localClientId} for item '{itemId}'.");
            }

            EventBus.Publish(new ItemPickedUpEvent(itemAsset.ItemName));
        }
    }
}
