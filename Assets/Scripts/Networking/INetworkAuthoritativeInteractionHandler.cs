using UnityEngine;

namespace Game.Networking
{
    /// <summary>
    /// Implemented by interactable-side handlers that execute world mutations on the server.
    /// </summary>
    public interface INetworkAuthoritativeInteractionHandler
    {
        bool CanProcessAuthoritativeInteraction(
            ulong senderClientId,
            Transform senderPlayerTransform,
            out string rejectReason);

        void ProcessAuthoritativeInteraction(
            ulong senderClientId,
            Transform senderPlayerTransform);
    }
}

