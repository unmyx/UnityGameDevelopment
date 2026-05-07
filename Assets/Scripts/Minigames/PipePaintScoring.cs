using UnityEngine;

namespace Game.Minigames
{
    internal static class PipePaintScoring
    {
        internal struct CoverageResult
        {
            public int paintedCount;
            public int totalCount;
            public float coverage01;
        }

        internal static CoverageResult BuildCoverageResult(int paintedCount, int totalCount)
        {
            int safeTotal = Mathf.Max(1, totalCount);
            int safePainted = Mathf.Clamp(paintedCount, 0, safeTotal);
            return new CoverageResult
            {
                paintedCount = safePainted,
                totalCount = safeTotal,
                coverage01 = safePainted / (float)safeTotal
            };
        }

        internal static float ComputeAggregateCoverage01(float sumCoverage01, int count)
        {
            if (count <= 0)
            {
                return 0f;
            }

            return Mathf.Clamp01(sumCoverage01 / count);
        }
    }
}
