using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Networking
{
    /// <summary>
    /// MP-7 host-first NGO sandbox bootstrap.
    /// Isolated to the NetworkSandbox scene so existing solo scenes remain unchanged.
    /// </summary>
    public class NetworkSessionBootstrap : MonoBehaviour
    {
        private const string SandboxSceneName = "NetworkSandbox";
        private static NetworkSessionBootstrap _instance;

        [Header("Sandbox Networking")]
        [SerializeField] private string _address = "127.0.0.1";
        [SerializeField] private ushort _port = 7777;
        [SerializeField] private bool _showDebugOverlay = true;
        [SerializeField] private bool _autoStartFromRuntimeMode = true;

        [SerializeField] private NetworkManager _networkManager;
        [SerializeField] private UnityTransport _transport;
        private string _addressField = "127.0.0.1";
        private bool _autoStartAttempted;

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
            _addressField = string.IsNullOrWhiteSpace(_address) ? "127.0.0.1" : _address.Trim();
            EnsureNetworkManagerSetup();
        }

        private void Update()
        {
            if (!_autoStartFromRuntimeMode || _autoStartAttempted || _networkManager == null || _networkManager.IsListening)
            {
                return;
            }

            _autoStartAttempted = true;
            switch (NetworkModeRuntime.StartupMode)
            {
                case NetworkStartupMode.Host:
                    StartHost();
                    break;
                case NetworkStartupMode.Client:
                    StartClient();
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

            GUI.enabled = _networkManager != null && !_networkManager.IsListening;
            if (GUILayout.Button("Start Host"))
            {
                StartHost();
            }

            if (GUILayout.Button("Start Client"))
            {
                StartClient();
            }

            GUI.enabled = _networkManager != null && _networkManager.IsListening;
            if (GUILayout.Button("Shutdown Session"))
            {
                Shutdown();
            }

            GUI.enabled = true;
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
            }
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
            }
        }

        private void Shutdown()
        {
            if (_networkManager == null || !_networkManager.IsListening)
            {
                return;
            }

            _networkManager.Shutdown();
            NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
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

            _networkManager.NetworkConfig.EnableSceneManagement = false;
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
    }
}
