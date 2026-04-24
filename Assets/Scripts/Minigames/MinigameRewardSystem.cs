using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using Game.Networking;
using Unity.Netcode;

namespace Game.Minigames
{
    /// <summary>
    /// Handles deterministic currency payouts for successful minigame completions.
    /// </summary>
    public static class MinigameRewardSystem
    {
        private const string CleaningMinigameId = "cleaning";
        private const string WeldingMinigameId = "welding";

        private static readonly Dictionary<string, int> PassRewardByMinigameId = new()
        {
            { "welding", 50 },
            { "cleaning", 20 }
        };

        private static readonly HashSet<string> RewardedSessionKeys = new HashSet<string>();

        public static void ClearRuntimeCaches()
        {
            RewardedSessionKeys.Clear();
        }

        /// <summary>
        /// Distribute rewards for a minigame completion. Rewards are pass-only and session-unique.
        /// </summary>
        public static void DistributeRewards(MinigameResult result, string minigameId, int sessionToken)
        {
            DistributeRewards(result, minigameId, "local_player_0", sessionToken, dedupeLifecycleScope: 0);
        }

        public static void DistributeRewards(MinigameResult result, string minigameId, string ownerPlayerId, int sessionToken)
        {
            DistributeRewards(result, minigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope: 0);
        }

        /// <summary>
        /// Distribute rewards for a minigame completion. Rewards are pass-only and
        /// unique per manager lifecycle scope + session token.
        /// </summary>
        public static void DistributeRewards(MinigameResult result, string minigameId, string ownerPlayerId, int sessionToken, int dedupeLifecycleScope)
        {
            if (result != MinigameResult.Pass)
            {
                return;
            }

            string normalizedId = NormalizeMinigameId(minigameId);

            NetworkManager manager = NetworkManager.Singleton;
            bool isNetworkSession = manager != null && manager.IsListening;
            bool isNonAuthoritativeClient = isNetworkSession && manager.IsClient && !manager.IsServer;
            if (isNonAuthoritativeClient
                && (string.Equals(normalizedId, CleaningMinigameId, System.StringComparison.Ordinal)
                    || string.Equals(normalizedId, WeldingMinigameId, System.StringComparison.Ordinal)))
            {
                // MP-21/MP-22: Cleaning/Welding rewards resolve only through authoritative session resolution.
                return;
            }

            if (isNonAuthoritativeClient)
            {
                if (NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority authority))
                {
                    authority.RequestMinigameRewardClaim(result, minigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope);
                }
                return;
            }

            string dedupeKey = BuildRewardDedupeKey(sessionToken, dedupeLifecycleScope);
            if (dedupeKey == null || !RewardedSessionKeys.Add(dedupeKey))
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            bool isRewardEligible = gameManager.TryConsumeDailyTaskRewardEligibility(normalizedId);

            int rewardCurrency = 0;
            if (isRewardEligible && PassRewardByMinigameId.TryGetValue(normalizedId, out int configuredRewardCurrency))
            {
                rewardCurrency = configuredRewardCurrency;
            }

            if (rewardCurrency > 0)
            {
                gameManager.AddDayWorkEarnings(rewardCurrency);
            }

            RewardGrantedData rewardEventData = new RewardGrantedData
            {
                rewardGrantId = BuildRewardGrantId(dedupeKey, normalizedId, ownerPlayerId),
                minigameId = minigameId,
                ownerPlayerId = string.IsNullOrWhiteSpace(ownerPlayerId) ? "local_player_0" : ownerPlayerId.Trim(),
                result = result,
                currencyAwarded = rewardCurrency,
                itemsAwarded = 0
            };

            // If ObjectiveManager is not ready (scene/menu transition timing),
            // defer objective progress handling until it rebinds and syncs.
            if (!ObjectiveManager.IsReadyForRewardEvents)
            {
                ObjectiveManager.EnqueuePendingRewardEvent(rewardEventData);
                return;
            }

            EventBus.Publish(new MinigameRewardGrantedEvent(rewardEventData));
        }

        public static void DistributeNetworkCleaningSessionReward(
            MinigameResult result,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope)
        {
            DistributeRewards(result, CleaningMinigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope);
        }

        public static void DistributeNetworkWeldingSessionReward(
            MinigameResult result,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope)
        {
            DistributeRewards(result, WeldingMinigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope);
        }

        private static string NormalizeMinigameId(string minigameId)
        {
            return (minigameId ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string BuildRewardDedupeKey(int sessionToken, int dedupeLifecycleScope)
        {
            if (sessionToken <= 0)
            {
                return null;
            }

            if (dedupeLifecycleScope > 0)
            {
                return $"{dedupeLifecycleScope}:{sessionToken}";
            }

            // Legacy callers that do not pass a scope keep prior token-only semantics.
            return $"legacy:{sessionToken}";
        }

        private static string BuildRewardGrantId(string dedupeKey, string minigameId, string ownerPlayerId)
        {
            if (string.IsNullOrEmpty(dedupeKey))
            {
                return string.Empty;
            }

            string normalizedOwner = string.IsNullOrWhiteSpace(ownerPlayerId)
                ? "local_player_0"
                : ownerPlayerId.Trim();
            string normalizedMinigame = string.IsNullOrWhiteSpace(minigameId)
                ? string.Empty
                : minigameId.Trim().ToLowerInvariant();
            return $"{dedupeKey}:{normalizedMinigame}:{normalizedOwner}";
        }
    }

    /// <summary>
    /// Data passed with MinigameRewardGrantedEvent.
    /// </summary>
    public class RewardGrantedData
    {
        public string rewardGrantId;
        public string minigameId;
        public string ownerPlayerId;
        public MinigameResult result;
        public int currencyAwarded;
        public int itemsAwarded;

        public override string ToString()
        {
            return $"[Reward:{rewardGrantId}] {minigameId}: {result} | Currency: +{currencyAwarded} | Items: {itemsAwarded}";
        }
    }
}
