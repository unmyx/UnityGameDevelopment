using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Reflection;
using Game.Core;

namespace Game.Networking
{
    /// <summary>
    /// MP-7 host-first NGO sandbox bootstrap.
    /// Isolated to the NetworkSandbox scene so existing solo scenes remain unchanged.
    /// </summary>
    public class NetworkSessionBootstrap : MonoBehaviour
    {
        private const string SandboxSceneName = SceneIds.NetworkSandbox;
        private const string InitialNetworkSceneName = SceneIds.Home;
        private static NetworkSessionBootstrap _instance;
        private static bool _hasLoggedSandboxAudioListenerIntent;

        [Header("Sandbox Networking")]
        [SerializeField] private string _address = "127.0.0.1";
        [SerializeField] private ushort _port = 7777;
        [SerializeField] private bool _showDebugOverlay = true;
        [SerializeField] private bool _autoStartFromRuntimeMode = true;
        [SerializeField] private int _relayMaxPeers = 3;
        [SerializeField] private string _relayConnectionType = "dtls";

        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private UnityTransport _transport;
        private string _addressField = "127.0.0.1";
        private bool _autoStartAttempted;
        private bool _startupInProgress;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureBootstrapInSandboxScene()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !string.Equals(activeScene.name, SandboxSceneName, System.StringComparison.Ordinal))
            {
                return;
            }

            if (_instance != null || FindAnyObjectByType<NetworkSessionBootstrap>() != null)
            {
                return;
            }

            GameObject bootstrapObject = new GameObject("[NetworkSessionBootstrap]");
            _instance = bootstrapObject.AddComponent<NetworkSessionBootstrap>();
        }

        private void Awake()
        {
            if (!IsSandboxSceneActive())
            {
                Destroy(gameObject);
                return;
            }

            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            ApplyRuntimeConnectionOverrides();
            _addressField = string.IsNullOrWhiteSpace(_address) ? "127.0.0.1" : _address.Trim();
            EnsureNetworkManagerSetup();
            LogSandboxAudioListenerIntentOnce();
        }

        private void Update()
        {
            if (!_autoStartFromRuntimeMode || _autoStartAttempted || _networkManager == null || _networkManager.IsListening || _startupInProgress)
            {
                return;
            }

            ApplyRuntimeConnectionOverrides();
            _autoStartAttempted = true;
            switch (NetworkModeRuntime.StartupMode)
            {
                case NetworkStartupMode.Host:
                    StartHost();
                    break;
                case NetworkStartupMode.Client:
                    StartClient();
                    break;
                case NetworkStartupMode.RelayHost:
                    _ = StartRelayHostAsync();
                    break;
                case NetworkStartupMode.RelayClient:
                    _ = StartRelayClientAsync();
                    break;
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

        }

        private void OnGUI()
        {
            if (!_showDebugOverlay || !IsSandboxSceneActive())
            {
                return;
            }

            const int width = 340;
            GUILayout.BeginArea(new Rect(16, 16, width, 220), GUI.skin.box);
            GUILayout.Label("NGO Sandbox (MP-7 Step 1)");
            GUILayout.Space(6);
            GUILayout.Label($"Status: {GetStatusLabel()}");
            GUILayout.Space(6);
            GUILayout.Label("Address");
            _addressField = GUILayout.TextField(_addressField ?? string.Empty);
            GUILayout.Label($"Port: {_port}");
            GUILayout.Space(8);

            GUI.enabled = _networkManager != null && !_networkManager.IsListening && !_startupInProgress;
            if (GUILayout.Button("Start Host"))
            {
                StartHost();
            }

            if (GUILayout.Button("Start Client"))
            {
                StartClient();
            }

            if (GUILayout.Button("Start Relay Host"))
            {
                _ = StartRelayHostAsync();
            }

            if (GUILayout.Button("Start Relay Client"))
            {
                _ = StartRelayClientAsync();
            }

            GUI.enabled = _networkManager != null && _networkManager.IsListening;
            if (GUILayout.Button("Shutdown Session"))
            {
                Shutdown();
            }

            GUI.enabled = true;
            if (!string.IsNullOrWhiteSpace(NetworkModeRuntime.RelayJoinCode))
            {
                GUILayout.Space(6);
                GUILayout.Label($"Relay Join Code: {NetworkModeRuntime.RelayJoinCode}");
            }
            GUILayout.EndArea();
        }

        private void StartHost()
        {
            if (_networkManager == null || _networkManager.IsListening)
            {
                return;
            }

            ConfigureTransportAddress();
            NetworkModeRuntime.StartupMode = NetworkStartupMode.Host;
            NetworkModeRuntime.Address = _address;
            NetworkModeRuntime.Port = _port;

            if (!_networkManager.StartHost())
            {
                Debug.LogError("[NetworkSessionBootstrap] Failed to start host.");
                NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
                NetworkModeRuntime.LastStartupMessage = "Failed to start host.";
                return;
            }

            NetworkModeRuntime.LastStartupMessage = "Host started.";
            TryEnterInitialNetworkScene(InitialNetworkSceneName);
        }

        private void StartClient()
        {
            if (_networkManager == null || _networkManager.IsListening)
            {
                return;
            }

            ConfigureTransportAddress();
            NetworkModeRuntime.StartupMode = NetworkStartupMode.Client;
            NetworkModeRuntime.Address = _address;
            NetworkModeRuntime.Port = _port;

            if (!_networkManager.StartClient())
            {
                Debug.LogError("[NetworkSessionBootstrap] Failed to start client.");
                NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
                NetworkModeRuntime.LastStartupMessage = "Failed to start client.";
                return;
            }

            NetworkModeRuntime.LastStartupMessage = "Client started.";
        }

        private async System.Threading.Tasks.Task StartRelayHostAsync()
        {
            if (_networkManager == null || _networkManager.IsListening || _startupInProgress)
            {
                return;
            }

            _startupInProgress = true;
            try
            {
                NetworkRelayService.RelayHostStartResult relay = await NetworkRelayService.PrepareHostAsync(_relayMaxPeers, _relayConnectionType);
                if (!relay.Success || relay.RelayServerData == null)
                {
                    NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
                    NetworkModeRuntime.LastStartupMessage = string.IsNullOrWhiteSpace(relay.Error)
                        ? "Failed to prepare Relay host."
                        : relay.Error;
                    return;
                }

                if (!ConfigureRelayTransport(relay.RelayServerData))
                {
                    NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
                    NetworkModeRuntime.LastStartupMessage = "Relay transport setup failed.";
                    return;
                }

                NetworkModeRuntime.StartupMode = NetworkStartupMode.RelayHost;
                NetworkModeRuntime.RelayJoinCode = relay.JoinCode ?? string.Empty;
                NetworkModeRuntime.LastStartupMessage = string.IsNullOrWhiteSpace(NetworkModeRuntime.RelayJoinCode)
                    ? "Relay host started."
                    : $"Relay Host Join Code: {NetworkModeRuntime.RelayJoinCode}";

                if (!_networkManager.StartHost())
                {
                    NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
                    NetworkModeRuntime.LastStartupMessage = "Failed to start Relay host.";
                    return;
                }

                TryEnterInitialNetworkScene(InitialNetworkSceneName);
            }
            finally
            {
                _startupInProgress = false;
            }
        }

        private async System.Threading.Tasks.Task StartRelayClientAsync()
        {
            if (_networkManager == null || _networkManager.IsListening || _startupInProgress)
            {
                return;
            }

            _startupInProgress = true;
            try
            {
                NetworkRelayService.RelayClientJoinResult relay = await NetworkRelayService.PrepareClientAsync(NetworkModeRuntime.RelayJoinCode, _relayConnectionType);
                if (!relay.Success || relay.RelayServerData == null)
                {
                    NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
                    NetworkModeRuntime.LastStartupMessage = string.IsNullOrWhiteSpace(relay.Error)
                        ? "Failed to join Relay."
                        : relay.Error;
                    return;
                }

                if (!ConfigureRelayTransport(relay.RelayServerData))
                {
                    NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
                    NetworkModeRuntime.LastStartupMessage = "Relay transport setup failed.";
                    return;
                }

                NetworkModeRuntime.StartupMode = NetworkStartupMode.RelayClient;
                NetworkModeRuntime.LastStartupMessage = "Relay client starting...";
                if (!_networkManager.StartClient())
                {
                    NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
                    NetworkModeRuntime.LastStartupMessage = "Failed to start Relay client.";
                    return;
                }

                NetworkModeRuntime.LastStartupMessage = "Relay client started.";
            }
            finally
            {
                _startupInProgress = false;
            }
        }

        private void Shutdown()
        {
            if (_networkManager == null || !_networkManager.IsListening)
            {
                return;
            }

            NetworkSessionLifecycleCoordinator.MarkLocalShutdownIntent();
            _networkManager.Shutdown();
            NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
            NetworkModeRuntime.RelayJoinCode = string.Empty;
        }

        private void EnsureNetworkManagerSetup()
        {
            if (_networkManager == null)
            {
                _networkManager = NetworkManager.Singleton;
            }

            if (_networkManager == null)
            {
                Debug.LogError("[NetworkSessionBootstrap] Missing NetworkManager in NetworkSandbox scene. Add a configured in-scene NetworkManager and assign it to bootstrap.");
                return;
            }

            if (_transport == null)
            {
                _transport = _networkManager.GetComponent<UnityTransport>();
            }

            if (_transport == null)
            {
                Debug.LogError("[NetworkSessionBootstrap] Missing UnityTransport on NetworkManager object. Add UnityTransport and assign it in bootstrap.");
                _networkManager = null;
                return;
            }

            if (_networkManager.NetworkConfig == null)
            {
                Debug.LogError("[NetworkSessionBootstrap] NetworkManager has no NetworkConfig. Configure NetworkManager in-scene and assign a PlayerPrefab.");
                _networkManager = null;
                return;
            }

            if (_networkManager.NetworkConfig.NetworkTransport == null)
            {
                Debug.LogError("[NetworkSessionBootstrap] NetworkManager.NetworkConfig.NetworkTransport is not assigned. Assign UnityTransport in NetworkManager config.");
                _networkManager = null;
                return;
            }

            if (_networkManager.NetworkConfig.PlayerPrefab == null)
            {
                Debug.LogError("[NetworkSessionBootstrap] NetworkManager.NetworkConfig.PlayerPrefab is not assigned. Assign the saved network player prefab.");
                _networkManager = null;
                return;
            }

            _networkManager.NetworkConfig.EnableSceneManagement = true;
            ConfigureTransportAddress();
        }

        private void ConfigureTransportAddress()
        {
            if (_transport == null)
            {
                return;
            }

            _address = string.IsNullOrWhiteSpace(_addressField) ? "127.0.0.1" : _addressField.Trim();
            _transport.SetConnectionData(_address, _port, "0.0.0.0");
        }

        private void ApplyRuntimeConnectionOverrides()
        {
            if (NetworkModeRuntime.StartupMode == NetworkStartupMode.Offline)
            {
                return;
            }

            if (NetworkModeRuntime.StartupMode == NetworkStartupMode.Host || NetworkModeRuntime.StartupMode == NetworkStartupMode.Client)
            {
                string runtimeAddress = string.IsNullOrWhiteSpace(NetworkModeRuntime.Address)
                    ? "127.0.0.1"
                    : NetworkModeRuntime.Address.Trim();
                ushort runtimePort = NetworkModeRuntime.Port > 0 ? NetworkModeRuntime.Port : (ushort)7777;

                _address = runtimeAddress;
                _port = runtimePort;
                _addressField = runtimeAddress;
            }
        }

        private bool ConfigureRelayTransport(object relayServerData)
        {
            if (_transport == null || relayServerData == null)
            {
                return false;
            }

            MethodInfo relayMethod = _transport.GetType().GetMethod(
                "SetRelayServerData",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { relayServerData.GetType() },
                null);

            if (relayMethod == null)
            {
                Debug.LogError("[NetworkSessionBootstrap] UnityTransport.SetRelayServerData overload was not found.");
                return false;
            }

            relayMethod.Invoke(_transport, new[] { relayServerData });
            return true;
        }

        private bool TryEnterInitialNetworkScene(string sceneName)
        {
            if (_networkManager == null || !_networkManager.IsServer || !_networkManager.IsListening)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(sceneName))
            {
                return false;
            }

            if (_networkManager.SceneManager == null || !_networkManager.NetworkConfig.EnableSceneManagement)
            {
                return false;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid() && string.Equals(activeScene.name, sceneName, System.StringComparison.Ordinal))
            {
                return true;
            }

            SceneEventProgressStatus status = _networkManager.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
            return status == SceneEventProgressStatus.Started || status == SceneEventProgressStatus.SceneEventInProgress;
        }

        private string GetStatusLabel()
        {
            if (_networkManager == null)
            {
                return "NetworkManager missing";
            }

            if (!_networkManager.IsListening)
            {
                return "Offline";
            }

            if (_networkManager.IsHost)
            {
                return $"Host (clients: {_networkManager.ConnectedClientsList.Count})";
            }

            if (_networkManager.IsClient)
            {
                return "Client";
            }

            return "Listening";
        }

        private static bool IsSandboxSceneActive()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            return activeScene.IsValid()
                   && string.Equals(activeScene.name, SandboxSceneName, System.StringComparison.Ordinal);
        }

        private void LogSandboxAudioListenerIntentOnce()
        {
            if (_hasLoggedSandboxAudioListenerIntent || !IsSandboxSceneActive())
            {
                return;
            }

            Camera bootstrapCamera = Camera.main;
            if (bootstrapCamera == null)
            {
                return;
            }

            AudioListener bootstrapListener = bootstrapCamera.GetComponent<AudioListener>();
            if (bootstrapListener == null || bootstrapListener.enabled)
            {
                return;
            }

            _hasLoggedSandboxAudioListenerIntent = true;
            Debug.Log(
                "[NetworkSessionBootstrap] NetworkSandbox bootstrap camera AudioListener is intentionally disabled. " +
                "Player-owned listeners are enabled by NetworkPlayerOwnershipGate after spawn to avoid duplicate-listener conflicts.",
                bootstrapCamera);
        }
    }
}
