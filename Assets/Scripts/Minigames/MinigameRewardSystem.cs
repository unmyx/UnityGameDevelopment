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
        private const string MeasureCutMinigameId = "measure_cut";
        private const string PipePaintMinigameId = "pipe_paint";
        private const string DrillScrewMinigameId = "drill_screw";
        private const int MeasureCutBaseRewardCurrency = 60;
        private const int PipePaintBaseRewardCurrency = 50;
        private const int DrillScrewBaseRewardCurrency = 70;

        private static readonly Dictionary<string, int> PassRewardByMinigameId = new()
        {
            { "welding", 50 },
            { "cleaning", 20 },
            { "measure_cut", MeasureCutBaseRewardCurrency },
            { "pipe_paint", PipePaintBaseRewardCurrency },
            { "drill_screw", DrillScrewBaseRewardCurrency }
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
            DistributeRewards(result, minigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope, null);
        }

        public static void DistributeRewards(
            MinigameResult result,
            string minigameId,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope,
            MinigameData minigameData)
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
                    || string.Equals(normalizedId, WeldingMinigameId, System.StringComparison.Ordinal)
                    || string.Equals(normalizedId, MeasureCutMinigameId, System.StringComparison.Ordinal)
                    || string.Equals(normalizedId, PipePaintMinigameId, System.StringComparison.Ordinal)
                    || string.Equals(normalizedId, DrillScrewMinigameId, System.StringComparison.Ordinal)))
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

                if (string.Equals(normalizedId, MeasureCutMinigameId, System.StringComparison.Ordinal))
                {
                    float qualityScore = ResolveMeasureCutQualityScore(minigameData);
                    rewardCurrency = ComputeMeasureCutScaledReward(configuredRewardCurrency, qualityScore);
                }
                else if (string.Equals(normalizedId, PipePaintMinigameId, System.StringComparison.Ordinal))
                {
                    float coverageScore = ResolvePipePaintCoverageScore(minigameData);
                    rewardCurrency = ComputePipePaintScaledReward(configuredRewardCurrency, coverageScore);
                }
                else if (string.Equals(normalizedId, DrillScrewMinigameId, System.StringComparison.Ordinal))
                {
                    float qualityScore = ResolveDrillScrewQualityScore(minigameData);
                    rewardCurrency = ComputeDrillScrewScaledReward(configuredRewardCurrency, qualityScore);
                }
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

        private static float ResolveMeasureCutQualityScore(MinigameData minigameData)
        {
            if (minigameData == null || minigameData.parameters == null)
            {
                return 100f;
            }

            if (!minigameData.parameters.TryGetValue("measure_cut_quality", out object qualityValue) || qualityValue == null)
            {
                return 100f;
            }

            if (qualityValue is float floatValue)
            {
                return UnityEngine.Mathf.Clamp(floatValue, 0f, 100f);
            }

            if (qualityValue is int intValue)
            {
                return UnityEngine.Mathf.Clamp(intValue, 0f, 100f);
            }

            return 100f;
        }

        private static int ComputeMeasureCutScaledReward(int baseReward, float qualityScore)
        {
            float clampedQuality = UnityEngine.Mathf.Clamp(qualityScore, 0f, 100f);
            float multiplier;
            if (clampedQuality < 50f)
            {
                multiplier = 0.55f;
            }
            else if (clampedQuality < 70f)
            {
                multiplier = 0.75f;
            }
            else if (clampedQuality < 90f)
            {
                multiplier = 0.9f;
            }
            else
            {
                multiplier = 1f;
            }

            return UnityEngine.Mathf.Max(0, UnityEngine.Mathf.RoundToInt(baseReward * multiplier));
        }

        private static float ResolvePipePaintCoverageScore(MinigameData minigameData)
        {
            if (minigameData == null || minigameData.parameters == null)
            {
                return 0f;
            }

            if (!minigameData.parameters.TryGetValue("pipe_paint_coverage", out object coverageValue) || coverageValue == null)
            {
                return 0f;
            }

            if (coverageValue is float floatValue)
            {
                return UnityEngine.Mathf.Clamp(floatValue, 0f, 100f);
            }

            if (coverageValue is int intValue)
            {
                return UnityEngine.Mathf.Clamp(intValue, 0, 100);
            }

            return 0f;
        }

        private static int ComputePipePaintScaledReward(int baseReward, float coverageScore)
        {
            float clampedCoverage = UnityEngine.Mathf.Clamp(coverageScore, 0f, 100f);
            float multiplier = clampedCoverage >= 85f ? 1f : 0.5f;
            return UnityEngine.Mathf.Max(0, UnityEngine.Mathf.RoundToInt(baseReward * multiplier));
        }

        private static float ResolveDrillScrewQualityScore(MinigameData minigameData)
        {
            if (minigameData == null || minigameData.parameters == null)
            {
                return 0f;
            }

            if (!minigameData.parameters.TryGetValue("drill_screw_quality", out object qualityValue) || qualityValue == null)
            {
                return 0f;
            }

            if (qualityValue is float floatValue)
            {
                return UnityEngine.Mathf.Clamp(floatValue, 0f, 100f);
            }

            if (qualityValue is int intValue)
            {
                return UnityEngine.Mathf.Clamp(intValue, 0, 100);
            }

            return 0f;
        }

        private static int ComputeDrillScrewScaledReward(int baseReward, float qualityScore)
        {
            float clampedQuality = UnityEngine.Mathf.Clamp(qualityScore, 0f, 100f);
            float multiplier = 0f;
            if (clampedQuality >= 90f)
            {
                multiplier = 1f;
            }
            else if (clampedQuality >= 50f)
            {
                multiplier = 0.5f;
            }

            return UnityEngine.Mathf.Max(0, UnityEngine.Mathf.RoundToInt(baseReward * multiplier));
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

        public static void DistributeNetworkMeasureCutSessionReward(
            MinigameResult result,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope,
            float qualityScore)
        {
            MinigameData rewardData = new MinigameData();
            rewardData.SetParameter("measure_cut_quality", qualityScore);
            DistributeRewards(result, MeasureCutMinigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope, rewardData);
        }

        public static void DistributeNetworkPipePaintSessionReward(
            MinigameResult result,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope,
            float coverageScore)
        {
            MinigameData rewardData = new MinigameData();
            rewardData.SetParameter("pipe_paint_coverage", coverageScore);
            DistributeRewards(result, PipePaintMinigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope, rewardData);
        }

        public static void DistributeNetworkDrillScrewSessionReward(
            MinigameResult result,
            string ownerPlayerId,
            int sessionToken,
            int dedupeLifecycleScope,
            float qualityScore)
        {
            MinigameData rewardData = new MinigameData();
            rewardData.SetParameter("drill_screw_quality", qualityScore);
            DistributeRewards(result, DrillScrewMinigameId, ownerPlayerId, sessionToken, dedupeLifecycleScope, rewardData);
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
