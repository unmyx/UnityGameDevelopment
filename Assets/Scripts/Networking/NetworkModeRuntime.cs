namespace Game.Networking
{
    public enum NetworkStartupMode
    {
        Offline = 0,
        Host = 1,
        Client = 2,
        RelayHost = 3,
        RelayClient = 4
    }

    /// <summary>
    /// Lightweight runtime seam for MP-7 networking startup state.
    /// </summary>
    public static class NetworkModeRuntime
    {
        public static NetworkStartupMode StartupMode { get; set; } = NetworkStartupMode.Offline;
        public static string Address { get; set; } = "127.0.0.1";
        public static ushort Port { get; set; } = 7777;
        public static string RelayJoinCode { get; set; } = string.Empty;
        public static string LastStartupMessage { get; set; } = string.Empty;
    }
}
