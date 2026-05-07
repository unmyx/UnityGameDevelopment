using UnityEngine;

namespace Game.Minigames
{
    [System.Serializable]
    public struct MeasureCutDifficultyProfile
    {
        [Min(5f)] public float timeLimitSeconds;
        [Min(0.01f)] public float lineToleranceWorld;
        [Range(0f, 100f)] public float requiredQuality;

        public static MeasureCutDifficultyProfile Easy =>
            new MeasureCutDifficultyProfile { timeLimitSeconds = 35f, lineToleranceWorld = 0.1f, requiredQuality = 30f };

        public static MeasureCutDifficultyProfile Medium =>
            new MeasureCutDifficultyProfile { timeLimitSeconds = 30f, lineToleranceWorld = 0.08f, requiredQuality = 35f };

        public static MeasureCutDifficultyProfile Hard =>
            new MeasureCutDifficultyProfile { timeLimitSeconds = 24f, lineToleranceWorld = 0.06f, requiredQuality = 45f };
    }
}
