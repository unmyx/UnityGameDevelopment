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
        private static readonly HashSet<string> LoggedDuplicateRoutes = new HashSet<string>();
        private static readonly SceneRouteGuard RouteGuard = new SceneRouteGuard();

        static NetworkSafeSceneRouter()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        public static bool TryRoute(string sceneName, Object context = null, bool allowClientLocalLoad = false)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                return false;
            }

            string normalizedSceneName = sceneName.Trim();
            Scene activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid()
                && string.Equals(activeScene.name, normalizedSceneName, System.StringComparison.Ordinal))
            {
                return true;
            }

            NetworkManager manager = NetworkManager.Singleton;
            SceneRouteAuthorityMode authorityMode = SceneRouteAuthorityPolicy.Resolve(
                manager != null && manager.IsListening,
                manager != null && manager.IsServer,
                allowClientLocalLoad);

            if (authorityMode == SceneRouteAuthorityMode.BlockedClient)
            {
                LogBlockedClientRouteOnce(normalizedSceneName, context);
                return false;
            }

            if (!RouteGuard.TryBegin(normalizedSceneName, out string routeGuardFailure))
            {
                LogDuplicateRouteOnce(normalizedSceneName, routeGuardFailure, context);
                return false;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager != null && !gameManager.TryPrepareForSceneRoute(normalizedSceneName, out string preparationFailure))
            {
                RouteGuard.Abort();
                Debug.LogWarning(
                    $"[NetworkSafeSceneRouter] Scene route to '{normalizedSceneName}' rejected during lifecycle preparation: {preparationFailure}",
                    context);
                return false;
            }

            try
            {
                if (authorityMode == SceneRouteAuthorityMode.ClientLocalAfterShutdown && manager != null)
                {
                    NetworkSessionLifecycleCoordinator.MarkLocalShutdownIntent();
                    manager.Shutdown();
                }

                if (authorityMode == SceneRouteAuthorityMode.ServerAuthoritative
                    && manager != null
                    && manager.SceneManager != null
                    && manager.NetworkConfig != null
                    && manager.NetworkConfig.EnableSceneManagement)
                {
                    SceneEventProgressStatus status = manager.SceneManager.LoadScene(normalizedSceneName, LoadSceneMode.Single);
                    bool accepted = status == SceneEventProgressStatus.Started
                                    || status == SceneEventProgressStatus.SceneEventInProgress;
                    if (!accepted)
                    {
                        AbortRoute(gameManager);
                    }

                    return accepted;
                }

                SceneManager.LoadScene(normalizedSceneName);
                return true;
            }
            catch (System.Exception exception)
            {
                AbortRoute(gameManager);
                Debug.LogError(
                    $"[NetworkSafeSceneRouter] Scene route to '{normalizedSceneName}' failed: {exception.Message}",
                    context);
                return false;
            }
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

        private static void LogDuplicateRouteOnce(string sceneName, string reason, Object context)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string key = $"{sceneName}|{reason}";
            if (!LoggedDuplicateRoutes.Add(key))
            {
                return;
            }

            Debug.LogWarning(
                $"[NetworkSafeSceneRouter] Duplicate/concurrent scene route rejected scene='{sceneName}' reason='{reason}'.",
                context);
#endif
        }

        private static void AbortRoute(GameManager gameManager)
        {
            RouteGuard.Abort();
            if (gameManager != null)
            {
                gameManager.NotifySceneRouteFailed();
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            RouteGuard.Complete(scene.name);
        }
    }
}
