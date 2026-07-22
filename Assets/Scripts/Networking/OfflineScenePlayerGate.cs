using Game.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Networking
{
    /// <summary>
    /// Suppresses offline scene Player roots when an NGO session is active.
    /// Keeps offline solo scene Player objects untouched.
    ///
    /// NOTE:
    /// This component is intentionally unwired in scene/prefab YAML.
    /// It is reached via RuntimeInitializeOnLoadMethod bootstrap hooks and
    /// suppresses offline player roots during active NGO sessions.
    /// Do not remove solely because no scene/prefab references are present.
    /// </summary>
    [DisallowMultipleComponent]
    public class OfflineScenePlayerGate : MonoBehaviour
    {
        [SerializeField] private bool _disableWhenNetworkListening = true;
        private static bool _sceneHookInstalled;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _sceneHookInstalled = false;
        }

        private void Awake()
        {
            ApplyGate();
        }

        private void OnEnable()
        {
            ApplyGate();
        }

        private void ApplyGate()
        {
            if (!_disableWhenNetworkListening)
            {
                return;
            }

            if (!IsNetworkSession())
            {
                return;
            }

            if (gameObject.activeSelf)
            {
                gameObject.SetActive(false);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallForActiveScene()
        {
            EnsureSceneHookInstalled();
            ApplyForScene(SceneManager.GetActiveScene());
        }

        private static void EnsureSceneHookInstalled()
        {
            if (_sceneHookInstalled)
            {
                return;
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneHookInstalled = true;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ApplyForScene(scene);
        }

        private static void ApplyForScene(Scene scene)
        {
            if (!scene.IsValid() || !IsNetworkSession())
            {
                return;
            }

            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null || !root.activeInHierarchy || !IsOfflineScenePlayerRoot(root))
                {
                    continue;
                }

                OfflineScenePlayerGate gate = root.GetComponent<OfflineScenePlayerGate>();
                if (gate == null)
                {
                    gate = root.AddComponent<OfflineScenePlayerGate>();
                }

                gate.ApplyGate();
                Game.Core.DevelopmentDiagnostics.Log($"[OfflineScenePlayerGate] Disabled offline scene player root '{root.name}' for network session.", root);
            }
        }

        private static bool IsOfflineScenePlayerRoot(GameObject root)
        {
            if (root == null)
            {
                return false;
            }

            if (root.GetComponentInChildren<NetworkObject>(true) != null)
            {
                return false;
            }

            return root.GetComponentInChildren<PlayerController>(true) != null
                   || root.GetComponentInChildren<PlayerInputHandler>(true) != null
                   || root.GetComponentInChildren<FirstPersonCamera>(true) != null;
        }

        private static bool IsNetworkSession()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening;
        }
    }
}
