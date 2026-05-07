using Game.Interaction;
using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// MP-9 sandbox BaseInteractable adapter.
    /// Forwards local interaction requests to the generic network authority bridge.
    ///
    /// Deferred cleanup note:
    /// Sandbox-only experiment adapter that is currently not wired in scene/prefab YAML.
    /// Keep temporarily to preserve MP-9 sandbox context.
    /// Safe removal candidate only if the sandbox pickup path is officially retired.
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
