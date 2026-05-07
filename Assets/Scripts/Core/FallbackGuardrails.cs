namespace Game.Core
{
    internal enum FallbackWindow
    {
        Bootstrap,
        Recovery,
        SceneTransition,
        OwnershipRebind,
        LateNetworkSpawnSync,
        Stable
    }

    internal static class FallbackGuardrails
    {
        public static string ToToken(FallbackWindow window)
        {
            switch (window)
            {
                case FallbackWindow.Bootstrap:
                    return "bootstrap";
                case FallbackWindow.Recovery:
                    return "recovery";
                case FallbackWindow.SceneTransition:
                    return "scene_transition";
                case FallbackWindow.OwnershipRebind:
                    return "ownership_rebind";
                case FallbackWindow.LateNetworkSpawnSync:
                    return "late_network_spawn_sync";
                default:
                    return "stable";
            }
        }
    }
}
