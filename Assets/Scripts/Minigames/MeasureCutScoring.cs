using System.Collections.Generic;
using UnityEngine;

namespace Game.Minigames
{
    internal static class MeasureCutScoring
    {
        public readonly struct CutScoreResult
        {
            public readonly float quality01;
            public readonly float averageDistanceWorld;
            public readonly float completion01;

            public CutScoreResult(float quality01, float averageDistanceWorld, float completion01)
            {
                this.quality01 = Mathf.Clamp01(quality01);
                this.averageDistanceWorld = Mathf.Max(0f, averageDistanceWorld);
                this.completion01 = Mathf.Clamp01(completion01);
            }
        }

        public static CutScoreResult ScoreHorizontalCut(
            IList<Vector3> sampledPoints,
            Vector3 lineStart,
            Vector3 lineEnd,
            float toleranceWorld)
        {
            if (sampledPoints == null || sampledPoints.Count <= 1)
            {
                return new CutScoreResult(0f, 999f, 0f);
            }

            Vector3 lineDirection = lineEnd - lineStart;
            float lineLength = lineDirection.magnitude;
            if (lineLength <= 0.0001f)
            {
                return new CutScoreResult(0f, 999f, 0f);
            }

            Vector3 lineDirNormalized = lineDirection / lineLength;
            float tolerance = Mathf.Max(0.0001f, toleranceWorld);

            float distanceAccum = 0f;
            float alongMin = 1f;
            float alongMax = 0f;

            for (int i = 0; i < sampledPoints.Count; i++)
            {
                Vector3 point = sampledPoints[i];
                Vector3 fromStart = point - lineStart;
                float along = Mathf.Clamp01(Vector3.Dot(fromStart, lineDirNormalized) / lineLength);
                alongMin = Mathf.Min(alongMin, along);
                alongMax = Mathf.Max(alongMax, along);

                Vector3 projectedPoint = lineStart + (lineDirNormalized * (along * lineLength));
                distanceAccum += Vector3.Distance(point, projectedPoint);
            }

            float averageDistance = distanceAccum / sampledPoints.Count;
            float distancePenalty = Mathf.Clamp01(averageDistance / tolerance);
            float accuracy01 = 1f - distancePenalty;

            float completion01 = Mathf.Clamp01(alongMax - alongMin);
            float completionPenalty = Mathf.Abs(1f - completion01);
            float completionScore = 1f - completionPenalty;

            float quality = (accuracy01 * 0.7f) + (completionScore * 0.3f);
            return new CutScoreResult(quality, averageDistance, completion01);
        }
    }
}
