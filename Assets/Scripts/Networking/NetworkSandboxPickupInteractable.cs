using Game.Interaction;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// MP-9 sandbox BaseInteractable adapter.
    /// Forwards local interaction requests to the generic network authority bridge.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkInteractionAuthorityBridge))]
    public class NetworkSandboxPickupInteractable : BaseInteractable
    {
        [SerializeField] private NetworkInteractionAuthorityBridge _networkAuthorityBridge;

        private void Awake()
        {
            if (_networkAuthorityBridge == null)
            {
                _networkAuthorityBridge = GetComponent<NetworkInteractionAuthorityBridge>();
            }
        }

        public override void Interact()
        {
            if (!CanInteract || _networkAuthorityBridge == null)
            {
                return;
            }

            _networkAuthorityBridge.RequestAuthoritativeInteraction();
        }
    }
}
