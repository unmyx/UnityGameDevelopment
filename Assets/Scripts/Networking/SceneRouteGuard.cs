using System;

namespace Game.Networking
{
    public enum SceneRouteAuthorityMode
    {
        OfflineLocal,
        ServerAuthoritative,
        ClientLocalAfterShutdown,
        BlockedClient
    }

    public static class SceneRouteAuthorityPolicy
    {
        public static SceneRouteAuthorityMode Resolve(
            bool isListening,
            bool isServer,
            bool allowClientLocalLoad)
        {
            if (!isListening)
            {
                return SceneRouteAuthorityMode.OfflineLocal;
            }

            if (isServer)
            {
                return SceneRouteAuthorityMode.ServerAuthoritative;
            }

            return allowClientLocalLoad
                ? SceneRouteAuthorityMode.ClientLocalAfterShutdown
                : SceneRouteAuthorityMode.BlockedClient;
        }
    }

    /// <summary>
    /// Idempotence gate shared by offline and network scene routes.
    /// </summary>
    public sealed class SceneRouteGuard
    {
        public bool IsRouteInProgress { get; private set; }
        public string TargetSceneName { get; private set; } = string.Empty;

        public bool TryBegin(string targetSceneName, out string failureReason)
        {
            if (string.IsNullOrWhiteSpace(targetSceneName))
            {
                failureReason = "Target scene name is empty.";
                return false;
            }

            string normalizedTarget = targetSceneName.Trim();
            if (IsRouteInProgress)
            {
                failureReason = string.Equals(TargetSceneName, normalizedTarget, StringComparison.Ordinal)
                    ? $"Route to '{normalizedTarget}' is already in progress."
                    : $"Route to '{TargetSceneName}' is already in progress; request for '{normalizedTarget}' was rejected.";
                return false;
            }

            IsRouteInProgress = true;
            TargetSceneName = normalizedTarget;
            failureReason = string.Empty;
            return true;
        }

        public void Complete(string loadedSceneName)
        {
            if (!IsRouteInProgress)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(loadedSceneName)
                && !string.Equals(TargetSceneName, loadedSceneName.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            Reset();
        }

        public void Abort()
        {
            Reset();
        }

        private void Reset()
        {
            IsRouteInProgress = false;
            TargetSceneName = string.Empty;
        }
    }
}
