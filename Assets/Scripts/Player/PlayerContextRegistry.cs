using System;
using System.Collections.Generic;
using Game.Interaction;
using Game.UI;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Runtime registry for player-owned contexts.
    /// Keeps MP-1 lightweight while preserving current solo behavior.
    /// </summary>
    public static class PlayerContextRegistry
    {
        public const string DefaultLocalPlayerId = "local_player_0";

        private static readonly Dictionary<string, PlayerContext> ContextsById =
            new Dictionary<string, PlayerContext>(StringComparer.Ordinal);
        private static string _localPlayerId = DefaultLocalPlayerId;

        public static IReadOnlyCollection<PlayerContext> Contexts => ContextsById.Values;

        public static bool RegisterOrUpdate(MonoBehaviour source, string preferredPlayerId = null)
        {
            if (source == null)
            {
                return false;
            }

            ResolveComponents(
                source,
                out PlayerController playerController,
                out PlayerInputHandler inputHandler,
                out FirstPersonCamera firstPersonCamera,
                out InteractionSystem interactionSystem,
                out InventoryGridUI inventoryGridUI,
                out GameplayHUD gameplayHUD);

            string playerId = ResolvePlayerId(preferredPlayerId, playerController);
            if (string.IsNullOrEmpty(playerId))
            {
                return false;
            }

            if (!ContextsById.TryGetValue(playerId, out PlayerContext context))
            {
                context = new PlayerContext(playerId);
                ContextsById[playerId] = context;
            }

            context.UpdateReferences(playerController, inputHandler, firstPersonCamera, interactionSystem, inventoryGridUI, gameplayHUD);

            if (!string.IsNullOrWhiteSpace(preferredPlayerId))
            {
                TrySetLocalPlayerId(playerId);
            }
            else if (!TryGetLocalContext(out _))
            {
                _localPlayerId = playerId;
            }

            ClearLocalPlayerIfMissing();
            return true;
        }

        public static bool Unregister(MonoBehaviour source, string preferredPlayerId = null)
        {
            if (source == null)
            {
                return false;
            }

            if (!TryResolveContextIdForSource(source, preferredPlayerId, out string playerId))
            {
                return false;
            }

            if (!ContextsById.TryGetValue(playerId, out PlayerContext context) || context == null)
            {
                return false;
            }

            context.ClearReferencesFromSource(source);
            if (!context.HasAnyRuntimeReference)
            {
                ContextsById.Remove(playerId);
            }

            ClearLocalPlayerIfMissing();
            return true;
        }

        public static bool TryGetContext(string playerId, out PlayerContext context)
        {
            if (string.IsNullOrWhiteSpace(playerId))
            {
                context = null;
                return false;
            }

            return ContextsById.TryGetValue(playerId.Trim(), out context) && context != null;
        }

        public static bool TryGetPrimaryContext(out PlayerContext context)
        {
            foreach (PlayerContext candidate in ContextsById.Values)
            {
                if (candidate != null && candidate.HasAnyRuntimeReference)
                {
                    context = candidate;
                    return true;
                }
            }

            context = null;
            return false;
        }

        public static bool TrySetLocalPlayerId(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId))
            {
                return false;
            }

            string normalizedId = playerId.Trim();
            if (!ContextsById.ContainsKey(normalizedId))
            {
                return false;
            }

            _localPlayerId = normalizedId;
            return true;
        }

        public static bool TryGetLocalContext(out PlayerContext context)
        {
            if (!string.IsNullOrWhiteSpace(_localPlayerId)
                && ContextsById.TryGetValue(_localPlayerId, out PlayerContext localContext)
                && localContext != null
                && localContext.HasAnyRuntimeReference)
            {
                context = localContext;
                return true;
            }

            ClearLocalPlayerIfMissing();

            if (ContextsById.TryGetValue(DefaultLocalPlayerId, out PlayerContext defaultContext)
                && defaultContext != null
                && defaultContext.HasAnyRuntimeReference)
            {
                _localPlayerId = DefaultLocalPlayerId;
                context = defaultContext;
                return true;
            }

            context = null;
            return false;
        }

        public static void ClearLocalPlayerIfMissing()
        {
            if (string.IsNullOrWhiteSpace(_localPlayerId))
            {
                return;
            }

            if (!ContextsById.TryGetValue(_localPlayerId, out PlayerContext localContext)
                || localContext == null
                || !localContext.HasAnyRuntimeReference)
            {
                _localPlayerId = string.Empty;
            }
        }

        private static string ResolvePlayerId(string preferredPlayerId, PlayerController playerController)
        {
            if (!string.IsNullOrWhiteSpace(preferredPlayerId))
            {
                return preferredPlayerId.Trim();
            }

            if (playerController != null)
            {
                return $"player_{playerController.GetHashCode()}";
            }

            return string.Empty;
        }

        private static bool TryResolveContextIdForSource(
            MonoBehaviour source,
            string preferredPlayerId,
            out string playerId)
        {
            playerId = string.Empty;

            if (!string.IsNullOrWhiteSpace(preferredPlayerId))
            {
                playerId = preferredPlayerId.Trim();
                return true;
            }

            PlayerController playerController = source as PlayerController ?? source.GetComponentInParent<PlayerController>();
            playerId = ResolvePlayerId(null, playerController);
            if (!string.IsNullOrEmpty(playerId))
            {
                return true;
            }

            foreach (KeyValuePair<string, PlayerContext> entry in ContextsById)
            {
                if (entry.Value != null && entry.Value.ContainsSource(source))
                {
                    playerId = entry.Key;
                    return true;
                }
            }

            return false;
        }

        private static void ResolveComponents(
            MonoBehaviour source,
            out PlayerController playerController,
            out PlayerInputHandler inputHandler,
            out FirstPersonCamera firstPersonCamera,
            out InteractionSystem interactionSystem,
            out InventoryGridUI inventoryGridUI,
            out GameplayHUD gameplayHUD)
        {
            playerController = source as PlayerController ?? source.GetComponentInParent<PlayerController>();
            inputHandler = source as PlayerInputHandler
                           ?? source.GetComponentInParent<PlayerInputHandler>()
                           ?? (playerController != null ? playerController.GetComponent<PlayerInputHandler>() : null);
            firstPersonCamera = source as FirstPersonCamera
                                ?? source.GetComponentInParent<FirstPersonCamera>()
                                ?? source.GetComponentInChildren<FirstPersonCamera>(true)
                                ?? (playerController != null ? playerController.GetComponentInChildren<FirstPersonCamera>(true) : null);
            interactionSystem = source as InteractionSystem
                                ?? source.GetComponentInParent<InteractionSystem>()
                                ?? source.GetComponentInChildren<InteractionSystem>(true)
                                ?? (playerController != null ? playerController.GetComponentInChildren<InteractionSystem>(true) : null);
            inventoryGridUI = source as InventoryGridUI;
            gameplayHUD = source as GameplayHUD;
        }
    }
}
