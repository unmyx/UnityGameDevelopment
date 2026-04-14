using System.Collections.Generic;
using UnityEngine;

namespace Game.Minigames
{
    /// <summary>
    /// Tracks repeated lie answers across encounters so repeated excuses become less credible.
    /// </summary>
    public static class LieSuspicionMemory
    {
        private static readonly Dictionary<string, int> RepeatCounts = new();

        public static int GetRepeatCount(string answerId)
        {
            if (string.IsNullOrWhiteSpace(answerId))
            {
                return 0;
            }

            return RepeatCounts.TryGetValue(answerId, out int count) ? count : 0;
        }

        public static float GetSuspicionMultiplier(string answerId, float penaltyPerRepeat, float minMultiplier)
        {
            penaltyPerRepeat = Mathf.Max(0f, penaltyPerRepeat);
            minMultiplier = Mathf.Clamp01(minMultiplier);

            int repeatCount = GetRepeatCount(answerId);
            float multiplier = 1f - (repeatCount * penaltyPerRepeat);
            return Mathf.Max(minMultiplier, multiplier);
        }

        public static void RegisterChoice(string answerId)
        {
            if (string.IsNullOrWhiteSpace(answerId))
            {
                return;
            }

            RepeatCounts[answerId] = GetRepeatCount(answerId) + 1;
        }

        public static void ResetAll()
        {
            RepeatCounts.Clear();
        }
    }
}
