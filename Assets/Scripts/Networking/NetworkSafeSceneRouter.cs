using System.Collections.Generic;
using Game.Core;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Networking
{
    /// <summary>
    /// Centralized scene routing helper that preserves offline behavior while enforcing
    /// host-authoritative scene loads during active NGO sessions.
    /// </summary>
    public static class NetworkSafeSceneRouter
    {
        private const string MenuSceneName = SceneIds.Menu;
        private const string HomeSceneName = SceneIds.Home;
        private const string GameplaySceneName = SceneIds.Gameplay;
        private const string NetworkSandboxSceneName = SceneIds.NetworkSandbox;

        private static readonly HashSet<string> LoggedBlockedClientRoutes = new HashSet<string>();

        public static bool TryRoute(string sceneName, Object context = null, bool allowClientLocalLoad = false)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                return false;
            }

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
            {
                SceneManager.LoadScene(sceneName);
                return true;
            }

            if (manager.IsServer)
            {
                if (manager.SceneManager != null && manager.NetworkConfig != null && manager.NetworkConfig.EnableSceneManagement)
                {
                    SceneEventProgressStatus status = manager.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
                    return status == SceneEventProgressStatus.Started || status == SceneEventProgressStatus.SceneEventInProgress;
                }

                SceneManager.LoadScene(sceneName);
                return true;
            }

            if (allowClientLocalLoad)
            {
                SceneManager.LoadScene(sceneName);
                return true;
            }

            LogBlockedClientRouteOnce(sceneName, context);
            return true;
        }

        public static bool TryRouteToMenu(Object context = null, bool allowClientLocalLoad = true)
        {
            return TryRoute(MenuSceneName, context, allowClientLocalLoad);
        }

        public static bool TryRouteToHome(Object context = null)
        {
            return TryRoute(HomeSceneName, context);
        }

        public static bool TryRouteToGameplay(Object context = null)
        {
            return TryRoute(GameplaySceneName, context);
        }

        public static bool TryRouteToNetworkSandbox(Object context = null, bool allowClientLocalLoad = true)
        {
            return TryRoute(NetworkSandboxSceneName, context, allowClientLocalLoad);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void LogBlockedClientRouteOnce(string sceneName, Object context)
        {
            string key = $"blocked_non_server_client_local_scene_load::{sceneName}";
            if (LoggedBlockedClientRoutes.Contains(key))
            {
                return;
            }

            LoggedBlockedClientRoutes.Add(key);
            Debug.LogWarning(
                $"[NetworkSafeSceneRouter] blocked_non_server_client_local_scene_load scene='{sceneName}'. " +
                "Active NGO client is non-server; local scene load is suppressed to avoid desync.",
                context);
        }
    }
}
