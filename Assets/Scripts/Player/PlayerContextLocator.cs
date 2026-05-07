using Game.Interaction;
using Game.UI;
using UnityEngine;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine.SceneManagement;

namespace Game.Player
{
    /// <summary>
    /// Backward-compatible context bridge.
    /// Uses registry first, then falls back to legacy solo lookups.
    /// </summary>
    public static class PlayerContextLocator
    {
        private const float BootstrapFallbackGraceSeconds = 8f;
        private static PlayerContext _soloFallbackContext;
        private static float _startupRealtime = -1f;
        private static bool _hasResolvedLocalContext;
        private static readonly HashSet<string> LoggedFallbackCallsites = new HashSet<string>();
        private static readonly HashSet<string> LoggedFallbackDetails = new HashSet<string>();

        public static bool TryGetPrimaryContext(out PlayerContext context)
        {
            if (PlayerContextRegistry.TryGetPrimaryContext(out context))
            {
                return true;
            }

            if (!IsCompatibilityFallbackAllowed())
            {
                context = null;
                return false;
            }

            LogCompatibilityFallback("TryGetPrimaryContext");
            return TryBuildSoloFallbackContext(out context);
        }

        public static bool TryGetLocalContext(out PlayerContext context)
        {
            if (PlayerContextRegistry.TryGetLocalContext(out context))
            {
                _hasResolvedLocalContext = true;
                return true;
            }

            if (!IsCompatibilityFallbackAllowed())
            {
                context = null;
                return false;
            }

            if (TryBuildSoloFallbackContext(out context) && context != null)
            {
                PlayerContextRegistry.TrySetLocalPlayerId(context.PlayerId);
                LogCompatibilityFallback("TryGetLocalContext");
                return true;
            }

            return false;
        }

        public static bool IsCompatibilityFallbackAllowed()
        {
            if (_startupRealtime < 0f)
            {
                _startupRealtime = Time.realtimeSinceStartup;
            }

            if (PlayerContextRegistry.TryGetLocalContext(out PlayerContext localContext)
                && localContext != null
                && localContext.HasAnyRuntimeReference)
            {
                _hasResolvedLocalContext = true;
                return false;
            }

            if (!_hasResolvedLocalContext)
            {
                return (Time.realtimeSinceStartup - _startupRealtime) <= BootstrapFallbackGraceSeconds;
            }

            return PlayerContextRegistry.Contexts.Count == 0;
        }

        public static bool TryGetLocalPlayerTransform(out Transform playerTransform)
        {
            if (TryGetLocalContext(out PlayerContext context))
            {
                Transform root = context.RootTransform;
                if (root != null)
                {
                    playerTransform = root;
                    return true;
                }
            }

            playerTransform = null;
            return false;
        }

        public static bool TryGetAuthoritativePlayerTransform(out Transform playerTransform)
        {
            if (TryGetLocalPlayerTransform(out playerTransform))
            {
                return true;
            }

            if (!IsCompatibilityFallbackAllowed())
            {
                playerTransform = null;
                return false;
            }

            if (TryGetPrimaryContext(out PlayerContext context))
            {
                Transform root = context.RootTransform;
                if (root != null)
                {
                    playerTransform = root;
                    return true;
                }
            }

            if (TryBuildSoloFallbackContext(out context))
            {
                LogCompatibilityFallback("TryGetAuthoritativePlayerTransform");
                Transform fallbackRoot = context.RootTransform;
                if (fallbackRoot != null)
                {
                    playerTransform = fallbackRoot;
                    return true;
                }
            }

            playerTransform = null;
            return false;
        }

        public static bool TryGetLocalFirstPersonCamera(out FirstPersonCamera firstPersonCamera)
        {
            if (TryGetLocalContext(out PlayerContext context) && context.FirstPersonCamera != null)
            {
                firstPersonCamera = context.FirstPersonCamera;
                return true;
            }

            firstPersonCamera = null;
            return false;
        }

        public static bool TryGetFirstPersonCamera(out FirstPersonCamera firstPersonCamera)
        {
            if (TryGetLocalFirstPersonCamera(out firstPersonCamera))
            {
                return true;
            }

            if (!IsCompatibilityFallbackAllowed())
            {
                firstPersonCamera = null;
                return false;
            }

            if (TryGetPrimaryContext(out PlayerContext context) && context.FirstPersonCamera != null)
            {
                LogCompatibilityFallback("TryGetFirstPersonCamera");
                firstPersonCamera = context.FirstPersonCamera;
                return true;
            }

            firstPersonCamera = null;
            return false;
        }

        public static bool TryGetLocalInteractionSystem(out InteractionSystem interactionSystem)
        {
            if (TryGetLocalContext(out PlayerContext context) && context.InteractionSystem != null)
            {
                interactionSystem = context.InteractionSystem;
                return true;
            }

            interactionSystem = null;
            return false;
        }

        public static bool TryGetInteractionSystem(out InteractionSystem interactionSystem)
        {
            if (TryGetLocalInteractionSystem(out interactionSystem))
            {
                return true;
            }

            if (!IsCompatibilityFallbackAllowed())
            {
                interactionSystem = null;
                return false;
            }

            if (TryGetPrimaryContext(out PlayerContext context) && context.InteractionSystem != null)
            {
                LogCompatibilityFallback("TryGetInteractionSystem");
                interactionSystem = context.InteractionSystem;
                return true;
            }

            interactionSystem = null;
            return false;
        }

        public static bool TryGetLocalInventoryGridUI(out InventoryGridUI inventoryGridUI)
        {
            if (TryGetLocalContext(out PlayerContext context) && context.InventoryGridUI != null)
            {
                inventoryGridUI = context.InventoryGridUI;
                return true;
            }

            inventoryGridUI = null;
            return false;
        }

        public static bool TryGetInventoryGridUI(out InventoryGridUI inventoryGridUI)
        {
            if (TryGetLocalInventoryGridUI(out inventoryGridUI))
            {
                return true;
            }

            if (!IsCompatibilityFallbackAllowed())
            {
                inventoryGridUI = null;
                return false;
            }

            if (TryGetPrimaryContext(out PlayerContext context) && context.InventoryGridUI != null)
            {
                LogCompatibilityFallback("TryGetInventoryGridUI(primary)");
                inventoryGridUI = context.InventoryGridUI;
                return true;
            }

            LogCompatibilityFallback("TryGetInventoryGridUI(find)");
            inventoryGridUI = Object.FindAnyObjectByType<InventoryGridUI>();
            return inventoryGridUI != null;
        }

        public static bool TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
        {
            if (TryGetLocalContext(out PlayerContext context)
                && context != null
                && context.PresentationState != null)
            {
                presentationState = context.PresentationState;
                return true;
            }

            presentationState = null;
            return false;
        }

        public static bool TryGetLocalPresentationMode(out LocalPlayerPresentationMode mode)
        {
            if (TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
                && presentationState != null)
            {
                mode = presentationState.Mode;
                return true;
            }

            mode = LocalPlayerPresentationMode.Unknown;
            return false;
        }

        public static bool TrySetLocalPresentationMode(LocalPlayerPresentationMode mode)
        {
            if (!TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
                || presentationState == null)
            {
                return false;
            }

            presentationState.SetLocalMode(mode);
            return true;
        }

        public static bool BeginLocalMinigamePresentation(string minigameId, string ownerPlayerId)
        {
            if (!TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
                || presentationState == null)
            {
                return false;
            }

            presentationState.BeginMinigamePresentation(minigameId, ownerPlayerId);
            return true;
        }

        public static bool EndLocalMinigamePresentation(string ownerPlayerId)
        {
            if (!TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
                || presentationState == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ownerPlayerId)
                && !PlayerInventoryAuthority.IsLocalOwner(ownerPlayerId))
            {
                return false;
            }

            presentationState.EndMinigamePresentation();
            return true;
        }

        public static bool TryAcquireLocalCursorAuthority(string authorityOwner, CursorLockMode lockMode, bool visible)
        {
            if (!TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
                || presentationState == null)
            {
                return false;
            }

            return presentationState.TryAcquireCursorAuthority(authorityOwner, lockMode, visible);
        }

        public static bool TryReleaseLocalCursorAuthority(string authorityOwner)
        {
            if (!TryGetLocalPresentationState(out LocalPlayerPresentationState presentationState)
                || presentationState == null)
            {
                return false;
            }

            return presentationState.TryReleaseCursorAuthority(authorityOwner);
        }

        private static bool TryBuildSoloFallbackContext(out PlayerContext context)
        {
            PlayerController playerController = Object.FindAnyObjectByType<PlayerController>();
            FirstPersonCamera firstPersonCamera = Object.FindAnyObjectByType<FirstPersonCamera>();
            PlayerInputHandler inputHandler = playerController != null
                ? playerController.GetComponent<PlayerInputHandler>()
                : Object.FindAnyObjectByType<PlayerInputHandler>();
            InteractionSystem interactionSystem = playerController != null
                ? playerController.GetComponentInChildren<InteractionSystem>(true)
                : Object.FindAnyObjectByType<InteractionSystem>();
            InventoryGridUI inventoryGridUI = Object.FindAnyObjectByType<InventoryGridUI>();
            GameplayHUD gameplayHUD = Object.FindAnyObjectByType<GameplayHUD>();

            if (playerController == null
                && firstPersonCamera == null
                && inputHandler == null
                && interactionSystem == null
                && inventoryGridUI == null)
            {
                context = null;
                return false;
            }

            _soloFallbackContext ??= new PlayerContext(PlayerContextRegistry.DefaultLocalPlayerId);
            _soloFallbackContext.UpdateReferences(
                playerController,
                inputHandler,
                firstPersonCamera,
                interactionSystem,
                inventoryGridUI,
                gameplayHUD);

            LogFallbackResolutionDetails(
                "TryBuildSoloFallbackContext",
                playerController,
                firstPersonCamera,
                inputHandler,
                interactionSystem,
                inventoryGridUI,
                gameplayHUD);

            // TODO(MP-4): Remove scene-wide compatibility fallback scans after deterministic bootstrap/rebind is fully explicit per scene.
            context = _soloFallbackContext;
            return true;
        }

        private static void LogCompatibilityFallback(string callsite)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (string.IsNullOrEmpty(callsite) || LoggedFallbackCallsites.Contains(callsite))
            {
                return;
            }

            LoggedFallbackCallsites.Add(callsite);
            string sceneName = SceneManager.GetActiveScene().name;
            NetworkManager networkManager = NetworkManager.Singleton;
            string netMode = "offline";
            if (networkManager != null && networkManager.IsListening)
            {
                netMode = networkManager.IsServer
                    ? (networkManager.IsClient ? "host" : "server")
                    : "client";
            }

            if (_startupRealtime < 0f)
            {
                _startupRealtime = Time.realtimeSinceStartup;
            }

            bool bootstrapWindow = (Time.realtimeSinceStartup - _startupRealtime) <= BootstrapFallbackGraceSeconds;
            string fallbackWindow = bootstrapWindow ? "bootstrap_window" : "recovery_window";
            Debug.Log(
                $"[PlayerContextLocator] Compatibility fallback used at '{callsite}' in scene '{sceneName}' ({netMode}, {fallbackWindow}). " +
                "Fallback remains permissive for bootstrap/recovery compatibility. TODO(MP-4): remove after explicit bootstrap/rebind.");
#endif
        }

        private static void LogFallbackResolutionDetails(
            string callsite,
            PlayerController playerController,
            FirstPersonCamera firstPersonCamera,
            PlayerInputHandler inputHandler,
            InteractionSystem interactionSystem,
            InventoryGridUI inventoryGridUI,
            GameplayHUD gameplayHUD)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (string.IsNullOrEmpty(callsite) || LoggedFallbackDetails.Contains(callsite))
            {
                return;
            }

            LoggedFallbackDetails.Add(callsite);
            string sceneName = SceneManager.GetActiveScene().name;
            Debug.Log(
                $"[PlayerContextLocator] Fallback scan resolution at '{callsite}' in scene '{sceneName}': " +
                $"playerController={(playerController != null ? playerController.name : "null")}, " +
                $"firstPersonCamera={(firstPersonCamera != null ? firstPersonCamera.name : "null")}, " +
                $"inputHandler={(inputHandler != null ? inputHandler.name : "null")}, " +
                $"interactionSystem={(interactionSystem != null ? interactionSystem.name : "null")}, " +
                $"inventoryGridUI={(inventoryGridUI != null ? inventoryGridUI.name : "null")}, " +
                $"gameplayHUD={(gameplayHUD != null ? gameplayHUD.name : "null")}.");
#endif
        }
    }

    /// <summary>
    /// Lightweight MP-4 ownership seam for inventory-facing calls.
    /// Keeps solo behavior deterministic while making ownership explicit at callsites.
    /// </summary>
    public static class PlayerInventoryAuthority
    {
        public static string GetLocalOwnerPlayerId()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager != null && networkManager.IsListening)
            {
                return $"net_client_{networkManager.LocalClientId}";
            }

            if (PlayerContextLocator.TryGetLocalContext(out PlayerContext context)
                && context != null
                && !string.IsNullOrWhiteSpace(context.PlayerId))
            {
                return context.PlayerId.Trim();
            }

            return PlayerContextRegistry.DefaultLocalPlayerId;
        }

        public static string NormalizeOwnerPlayerId(string ownerPlayerId)
        {
            return string.IsNullOrWhiteSpace(ownerPlayerId)
                ? GetLocalOwnerPlayerId()
                : ownerPlayerId.Trim();
        }

        public static bool IsLocalOwner(string ownerPlayerId)
        {
            string normalizedOwner = NormalizeOwnerPlayerId(ownerPlayerId);
            return string.Equals(
                normalizedOwner,
                GetLocalOwnerPlayerId(),
                System.StringComparison.Ordinal);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        public static void LogNonLocalOwnerUsage(string callsite, string ownerPlayerId, Object contextObject = null)
        {
            if (IsLocalOwner(ownerPlayerId))
            {
                return;
            }

            Debug.LogWarning(
                $"[PlayerInventoryAuthority] Non-local owner '{NormalizeOwnerPlayerId(ownerPlayerId)}' used at '{callsite}'. " +
                "Owner-aware storage is active; verify caller ownership routing.",
                contextObject);
        }
    }
}
