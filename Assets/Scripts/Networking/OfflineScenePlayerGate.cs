using Game.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Networking
{
    /// <summary>
    /// Suppresses offline scene Player roots when an NGO session is active.
    /// Keeps offline solo scene Player objects untouched.
    /// </summary>
    [DisallowMultipleComponent]
    public class OfflineScenePlayerGate : MonoBehaviour
    {
        [SerializeField] private bool _disableWhenNetworkListening = true;

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
            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                return;
            }

            if (!IsNetworkSession())
            {
                return;
            }

            GameObject[] roots = activeScene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null || !root.activeInHierarchy)
                {
                    continue;
                }

                if (!string.Equals(root.name, "Player", System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (!IsOfflineScenePlayerRoot(root))
                {
                    continue;
                }

                OfflineScenePlayerGate gate = root.GetComponent<OfflineScenePlayerGate>();
                if (gate == null)
                {
                    gate = root.AddComponent<OfflineScenePlayerGate>();
                }

                gate.ApplyGate();
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
