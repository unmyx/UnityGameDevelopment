using Game.Interaction;
using Unity.Netcode;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// Reusable host-authoritative interaction bridge.
    /// Local player requests interaction; server validates and delegates authoritative execution.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkInteractionAuthorityBridge : NetworkBehaviour
    {
        [SerializeField] private BaseInteractable _interactable;
        [SerializeField] private MonoBehaviour _authoritativeHandler;
        [SerializeField] private float _maxInteractDistance = 3f;
        [SerializeField] private bool _enableLogs = true;

        private NetworkObject _networkObject;
        private INetworkAuthoritativeInteractionHandler _handler;
        private bool _interactionProcessed;

        private void Awake()
        {
            _networkObject = GetComponent<NetworkObject>();
            ResolveHandler();
            ResolveInteractable();
        }

        public bool ShouldRouteThroughNetworkAuthority()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening;
        }

        public void RequestAuthoritativeInteraction()
        {
            if (!ShouldRouteThroughNetworkAuthority())
            {
                return;
            }

            if (_interactionProcessed)
            {
                return;
            }

            if (_networkObject == null || !_networkObject.IsSpawned)
            {
                LogRejected(NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0u, "target_not_spawned");
                return;
            }

            RequestInteractionServerRpc(_networkObject.NetworkObjectId);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestInteractionServerRpc(
            ulong targetNetworkObjectId,
            ServerRpcParams serverRpcParams = default)
        {
            if (!IsServer)
            {
                return;
            }

            ulong senderClientId = serverRpcParams.Receive.SenderClientId;
            if (!TryValidateTarget(targetNetworkObjectId, out string targetRejectReason))
            {
                LogRejected(senderClientId, targetRejectReason);
                return;
            }

            if (!TryGetSenderPlayerTransform(senderClientId, out Transform senderPlayerTransform, out string senderRejectReason))
            {
                LogRejected(senderClientId, senderRejectReason);
                return;
            }

            if (!IsInteractableReady(out string interactableRejectReason))
            {
                LogRejected(senderClientId, interactableRejectReason);
                return;
            }

            float maxDistanceSqr = _maxInteractDistance * _maxInteractDistance;
            float distanceSqr = (senderPlayerTransform.position - transform.position).sqrMagnitude;
            if (distanceSqr > maxDistanceSqr)
            {
                LogRejected(senderClientId, $"out_of_range (distance={Mathf.Sqrt(distanceSqr):0.00}, max={_maxInteractDistance:0.00})");
                return;
            }

            ResolveHandler();
            if (_handler == null)
            {
                LogRejected(senderClientId, "authoritative_handler_missing");
                return;
            }

            if (!_handler.CanProcessAuthoritativeInteraction(senderClientId, senderPlayerTransform, out string handlerRejectReason))
            {
                LogRejected(senderClientId, handlerRejectReason);
                return;
            }

            _interactionProcessed = true;
            if (_interactable != null)
            {
                _interactable.SetCanInteract(false);
            }

            if (_enableLogs)
            {
                Game.Core.DevelopmentDiagnostics.Log($"[NetworkInteractionAuthorityBridge] Interaction accepted from client {senderClientId} for object {_networkObject.NetworkObjectId}.");
            }

            _handler.ProcessAuthoritativeInteraction(senderClientId, senderPlayerTransform);
        }

        private bool TryValidateTarget(ulong targetNetworkObjectId, out string rejectReason)
        {
            rejectReason = "unknown_target_state";

            if (_interactionProcessed)
            {
                rejectReason = "already_processed";
                return false;
            }

            if (_networkObject == null || !_networkObject.IsSpawned)
            {
                rejectReason = "target_not_spawned";
                return false;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || manager.SpawnManager == null)
            {
                rejectReason = "network_manager_missing";
                return false;
            }

            if (!manager.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkObjectId, out NetworkObject requestedTarget)
                || requestedTarget == null)
            {
                rejectReason = "target_lookup_failed";
                return false;
            }

            if (requestedTarget != _networkObject)
            {
                rejectReason = "target_mismatch";
                return false;
            }

            rejectReason = string.Empty;
            return true;
        }

        private static bool TryGetSenderPlayerTransform(
            ulong senderClientId,
            out Transform senderPlayerTransform,
            out string rejectReason)
        {
            senderPlayerTransform = null;
            rejectReason = "unknown_sender_state";

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null)
            {
                rejectReason = "network_manager_missing";
                return false;
            }

            if (!manager.ConnectedClients.TryGetValue(senderClientId, out NetworkClient senderClient) || senderClient == null)
            {
                rejectReason = "sender_client_missing";
                return false;
            }

            if (senderClient.PlayerObject == null || !senderClient.PlayerObject.IsSpawned)
            {
                rejectReason = "sender_player_object_missing";
                return false;
            }

            senderPlayerTransform = senderClient.PlayerObject.transform;
            if (senderPlayerTransform == null)
            {
                rejectReason = "sender_player_transform_missing";
                return false;
            }

            rejectReason = string.Empty;
            return true;
        }

        private bool IsInteractableReady(out string rejectReason)
        {
            ResolveInteractable();
            if (_interactable == null)
            {
                rejectReason = "interactable_missing";
                return false;
            }

            if (!_interactable.isActiveAndEnabled || !_interactable.gameObject.activeInHierarchy)
            {
                rejectReason = "interactable_disabled";
                return false;
            }

            if (!_interactable.CanInteract)
            {
                rejectReason = "interactable_blocked";
                return false;
            }

            rejectReason = string.Empty;
            return true;
        }

        private void ResolveHandler()
        {
            if (_authoritativeHandler != null && _authoritativeHandler is INetworkAuthoritativeInteractionHandler existing)
            {
                _handler = existing;
                return;
            }

            _handler = GetComponent<INetworkAuthoritativeInteractionHandler>();
            _authoritativeHandler = _handler as MonoBehaviour;
        }

        private void ResolveInteractable()
        {
            if (_interactable == null)
            {
                _interactable = GetComponent<BaseInteractable>();
            }
        }

        private void LogRejected(ulong senderClientId, string rejectReason)
        {
            if (_enableLogs)
            {
                Game.Core.DevelopmentDiagnostics.Log($"[NetworkInteractionAuthorityBridge] Interaction rejected from client {senderClientId}: {rejectReason}");
            }
        }
    }
}

