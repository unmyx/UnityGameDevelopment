using Game.Systems;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Networking
{
    /// <summary>
    /// MP-18 server-authoritative NPC bridge.
    /// Server drives AI and movement; clients observe replicated transform/state only.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkTransform))]
    [RequireComponent(typeof(NPCController))]
    [RequireComponent(typeof(NavMeshAgent))]
    public class NetworkNpcAuthorityBridge : NetworkBehaviour
    {
        public const ulong NoTargetClientId = ulong.MaxValue;

        private readonly NetworkVariable<int> _state = new(
            (int)NPCController.NPCState.Idle,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<ulong> _targetClientId = new(
            NoTargetClientId,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> _isCaughtOrCooldown = new(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<ulong> _lastCatchToken = new(
            0UL,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private NPCController _npcController;
        private NavMeshAgent _navMeshAgent;

        public NPCController.NPCState ReplicatedState => (NPCController.NPCState)_state.Value;
        public ulong ReplicatedTargetClientId => _targetClientId.Value;
        public bool ReplicatedIsCaughtOrCooldown => _isCaughtOrCooldown.Value;
        public ulong ReplicatedLastCatchToken => _lastCatchToken.Value;

        private void Awake()
        {
            _npcController = GetComponent<NPCController>();
            _navMeshAgent = GetComponent<NavMeshAgent>();
        }

        public override void OnNetworkSpawn()
        {
            ApplyAuthorityState();
            PushServerState();
        }

        public override void OnNetworkDespawn()
        {
            // If networking stops, fallback to offline behavior.
            if (_npcController != null)
            {
                _npcController.enabled = true;
            }

            if (_navMeshAgent != null)
            {
                _navMeshAgent.enabled = true;
            }
        }

        private void Update()
        {
            ApplyAuthorityState();
            PushServerState();
        }

        private void ApplyAuthorityState()
        {
            NetworkManager manager = NetworkManager.Singleton;
            bool isNetworkSession = manager != null && manager.IsListening;
            bool shouldDriveAuthoritative = !isNetworkSession || IsServer;

            if (_npcController != null && _npcController.enabled != shouldDriveAuthoritative)
            {
                _npcController.enabled = shouldDriveAuthoritative;
            }

            if (_navMeshAgent != null && _navMeshAgent.enabled != shouldDriveAuthoritative)
            {
                _navMeshAgent.enabled = shouldDriveAuthoritative;
            }
        }

        private void PushServerState()
        {
            if (!IsServer || _npcController == null)
            {
                return;
            }

            _state.Value = (int)_npcController.CurrentNpcState;
            _targetClientId.Value = _npcController.CurrentTargetClientId;
            _isCaughtOrCooldown.Value = _npcController.IsCaughtOrCooldownActive;
            _lastCatchToken.Value = _npcController.CurrentCatchToken;
        }
    }
}
