using Game.Core;
using Game.Core.Events;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Networking
{
    /// <summary>
    /// Centralized NGO lifecycle hardening for disconnect/failure recovery.
    /// Keeps host/client cleanup focused without changing gameplay authority rules.
    /// </summary>
    [DisallowMultipleComponent]
    public class NetworkSessionLifecycleCoordinator : MonoBehaviour
    {
        private const string MenuSceneName = SceneIds.Menu;

        private static NetworkSessionLifecycleCoordinator _instance;
        private static bool _localShutdownRequested;

        private NetworkManager _observedManager;
        private bool _callbacksSubscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureInstance()
        {
            if (_instance != null || FindAnyObjectByType<NetworkSessionLifecycleCoordinator>() != null)
            {
                return;
            }

            GameObject coordinatorObject = new GameObject("[NetworkSessionLifecycleCoordinator]");
            _instance = coordinatorObject.AddComponent<NetworkSessionLifecycleCoordinator>();
        }

        public static void MarkLocalShutdownIntent()
        {
            _localShutdownRequested = true;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            UnsubscribeFromObservedManager();
        }

        private void Update()
        {
            TryBindToNetworkManager();

            if (_localShutdownRequested && (_observedManager == null || !_observedManager.IsListening))
            {
                _localShutdownRequested = false;
            }
        }

        private void TryBindToNetworkManager()
        {
            NetworkManager current = NetworkManager.Singleton;
            if (ReferenceEquals(_observedManager, current))
            {
                return;
            }

            UnsubscribeFromObservedManager();
            _observedManager = current;
            SubscribeToObservedManager();
        }

        private void SubscribeToObservedManager()
        {
            if (_observedManager == null || _callbacksSubscribed)
            {
                return;
            }

            _observedManager.OnClientDisconnectCallback += OnClientDisconnected;
            _callbacksSubscribed = true;
        }

        private void UnsubscribeFromObservedManager()
        {
            if (_observedManager == null || !_callbacksSubscribed)
            {
                _callbacksSubscribed = false;
                return;
            }

            _observedManager.OnClientDisconnectCallback -= OnClientDisconnected;
            _callbacksSubscribed = false;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            NetworkManager manager = _observedManager != null ? _observedManager : NetworkManager.Singleton;
            if (manager == null)
            {
                return;
            }

            if (manager.IsServer && clientId != NetworkManager.ServerClientId)
            {
                NetworkSessionProgressAuthority.TryHandleClientDisconnected(clientId);
            }

            if (!manager.IsServer && clientId == manager.LocalClientId)
            {
                HandleClientLostSession(manager);
            }
        }

        private void HandleClientLostSession(NetworkManager manager)
        {
            bool wasIntentionalShutdown = _localShutdownRequested;
            _localShutdownRequested = false;
            NetworkModeRuntime.StartupMode = NetworkStartupMode.Offline;
            NetworkModeRuntime.RelayJoinCode = string.Empty;

            if (wasIntentionalShutdown)
            {
                return;
            }

            EventBus.Publish(new PlayerFeedbackEvent("Disconnected from host."));

            if (SceneManager.GetActiveScene().IsValid()
                && !string.Equals(SceneManager.GetActiveScene().name, MenuSceneName, System.StringComparison.Ordinal))
            {
                if (NetworkSafeSceneRouter.TryRouteToMenu(this, allowClientLocalLoad: true))
                {
                    return;
                }
            }

            if (manager != null && manager.IsListening)
            {
                MarkLocalShutdownIntent();
                manager.Shutdown();
            }
        }
    }
}
