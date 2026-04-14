using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;

namespace Game.Minigames
{
    /// <summary>
    /// Handles deterministic currency payouts for successful minigame completions.
    /// </summary>
    public static class MinigameRewardSystem
    {
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
            DistributeRewards(result, minigameId, sessionToken, dedupeLifecycleScope: 0);
        }

        /// <summary>
        /// Distribute rewards for a minigame completion. Rewards are pass-only and
        /// unique per manager lifecycle scope + session token.
        /// </summary>
        public static void DistributeRewards(MinigameResult result, string minigameId, int sessionToken, int dedupeLifecycleScope)
        {
            if (result != MinigameResult.Pass)
            {
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

            string normalizedId = NormalizeMinigameId(minigameId);
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
                minigameId = minigameId,
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
    }

    /// <summary>
    /// Data passed with MinigameRewardGrantedEvent.
    /// </summary>
    public class RewardGrantedData
    {
        public string minigameId;
        public MinigameResult result;
        public int currencyAwarded;
        public int itemsAwarded;

        public override string ToString()
        {
            return $"[Reward] {minigameId}: {result} | Currency: +{currencyAwarded} | Items: {itemsAwarded}";
        }
    }
}
