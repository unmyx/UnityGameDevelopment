namespace Game.Core.Events
{
    /// <summary>
    /// MP-19 targeted catch payload published on caught client after server-authoritative catch registration.
    /// </summary>
    public readonly struct NpcCatchTriggeredEvent
    {
        public string OwnerKey { get; }
        public ulong TargetClientId { get; }
        public ulong CatchToken { get; }
        public ulong NpcNetworkObjectId { get; }
        public float ServerTime { get; }
        public bool PendingLie { get; }

        public NpcCatchTriggeredEvent(
            string ownerKey,
            ulong targetClientId,
            ulong catchToken,
            ulong npcNetworkObjectId,
            float serverTime,
            bool pendingLie)
        {
            OwnerKey = ownerKey;
            TargetClientId = targetClientId;
            CatchToken = catchToken;
            NpcNetworkObjectId = npcNetworkObjectId;
            ServerTime = serverTime;
            PendingLie = pendingLie;
        }
    }
}
