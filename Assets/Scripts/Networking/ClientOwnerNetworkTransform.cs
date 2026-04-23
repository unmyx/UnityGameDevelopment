using Unity.Netcode.Components;

namespace Game.Networking
{
    /// <summary>
    /// Minimal NGO sandbox transform mode for MP-7:
    /// owner/client authoritative movement replication.
    /// </summary>
    public class ClientOwnerNetworkTransform : NetworkTransform
    {
        protected override bool OnIsServerAuthoritative()
        {
            return false;
        }
    }
}

