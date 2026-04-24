using Game.Player;

namespace Game.Networking
{
    /// <summary>
    /// Centralized owner-key mapping for host-authoritative NGO flows.
    /// </summary>
    public static class NetworkOwnerKeyUtility
    {
        private const string NetworkOwnerPrefix = "net_client_";

        public static string GetOwnerKeyForSender(ulong senderClientId)
        {
            return $"{NetworkOwnerPrefix}{senderClientId}";
        }

        public static string NormalizeOwnerKey(string ownerKey)
        {
            return string.IsNullOrWhiteSpace(ownerKey)
                ? PlayerContextRegistry.DefaultLocalPlayerId
                : ownerKey.Trim();
        }

        public static bool IsNetworkOwnerKey(string ownerKey)
        {
            if (string.IsNullOrWhiteSpace(ownerKey))
            {
                return false;
            }

            return ownerKey.Trim().StartsWith(NetworkOwnerPrefix, System.StringComparison.Ordinal);
        }
    }
}
