using UnityEngine;

namespace Game.Minigames
{
    internal static class DrillScrewScoring
    {
        internal static float ComputeDrillHoleQuality(float completion01, float averageDistanceNormalized, float overdrill01, float earlyReleasePenalty01)
        {
            float completionScore = Mathf.Clamp01(completion01);
            float alignmentScore = 1f - Mathf.Clamp01(averageDistanceNormalized);
            float overdrillPenalty = Mathf.Clamp01(overdrill01);
            float earlyPenalty = Mathf.Clamp01(earlyReleasePenalty01);

            float score = (completionScore * 0.5f) + (alignmentScore * 0.5f);
            score -= (overdrillPenalty * 0.35f) + (earlyPenalty * 0.2f);
            return Mathf.Clamp01(score) * 100f;
        }

        internal static float ComputeScrewHoleQuality(float tightness01, float targetTightness01, float overTight01)
        {
            float clampedTightness = Mathf.Max(0f, tightness01);
            float target = Mathf.Clamp01(targetTightness01);

            float delta = Mathf.Abs(clampedTightness - target);
            float baseline = Mathf.Clamp01(1f - delta);

            float underTightPenalty = clampedTightness < target ? Mathf.Clamp01((target - clampedTightness) / Mathf.Max(0.01f, target)) : 0f;
            float overTightPenalty = Mathf.Clamp01(overTight01);

            float score = baseline;
            score -= underTightPenalty * 0.25f;
            score -= overTightPenalty * 0.65f;
            return Mathf.Clamp01(score) * 100f;
        }

        internal static float ComputeAggregateQuality(float drillAverageScore, float screwAverageScore)
        {
            return Mathf.Clamp((drillAverageScore * 0.5f) + (screwAverageScore * 0.5f), 0f, 100f);
        }
    }
}
