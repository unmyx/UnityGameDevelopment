using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// MP-9 sandbox authoritative interaction handler implementation.
    /// Executed via NetworkInteractionAuthorityBridge after server validation.
    ///
    /// Deferred cleanup note:
    /// Sandbox-only MP-9 authority handler that is currently not wired in scene/prefab YAML.
    /// If sandbox pickup flow is retired, remove this together with
    /// NetworkSandboxPickupInteractable to keep the pair consistent.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Unity.Netcode.NetworkObject))]
    public class NetworkSandboxPickupAuthority : MonoBehaviour, INetworkAuthoritativeInteractionHandler
    {
        [SerializeField] private bool _enableLogs = true;

        private Unity.Netcode.NetworkObject _networkObject;
        private bool _consumed;

        private void Awake()
        {
            _networkObject = GetComponent<Unity.Netcode.NetworkObject>();
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

            rejectReason = string.Empty;
            return true;
        }

        public void ProcessAuthoritativeInteraction(
            ulong senderClientId,
            Transform senderPlayerTransform)
        {
            if (_consumed || _networkObject == null || !_networkObject.IsSpawned)
                return;

            _consumed = true;
            if (_enableLogs)
                Debug.Log($"[NetworkSandboxPickupAuthority] Pickup accepted from client {senderClientId}. Despawning pickup.");

            _networkObject.Despawn(true);
        }
    }
}
