using System.Collections.Generic;
using Game.Inventory;
using UnityEngine;

namespace Game.Minigames
{
    public readonly struct WeldingToolDefinition
    {
        public ToolType ToolType { get; }
        public float InitialRadius { get; }
        public float MinimumRadius { get; }
        public float MaximumRadius { get; }
        public float RadiusStep { get; }

        public bool IsValid => ToolType != ToolType.None
            && float.IsFinite(InitialRadius)
            && float.IsFinite(MinimumRadius)
            && float.IsFinite(MaximumRadius)
            && float.IsFinite(RadiusStep)
            && MinimumRadius > 0f
            && MaximumRadius >= MinimumRadius
            && InitialRadius >= MinimumRadius
            && InitialRadius <= MaximumRadius
            && RadiusStep > 0f;

        public WeldingToolDefinition(
            ToolType toolType,
            float initialRadius,
            float minimumRadius,
            float maximumRadius,
            float radiusStep)
        {
            ToolType = toolType;
            InitialRadius = initialRadius;
            MinimumRadius = minimumRadius;
            MaximumRadius = maximumRadius;
            RadiusStep = radiusStep;
        }
    }

    public static class WeldingToolRules
    {
        private static readonly WeldingToolDefinition ElectricDefinition = new WeldingToolDefinition(
            ToolType.Electric,
            initialRadius: 0.04f,
            minimumRadius: 0.025f,
            maximumRadius: 0.085f,
            radiusStep: 0.01f);

        private static readonly WeldingToolDefinition Co2Definition = new WeldingToolDefinition(
            ToolType.CO2,
            initialRadius: 0.07f,
            minimumRadius: 0.04f,
            maximumRadius: 0.14f,
            radiusStep: 0.01f);

        public static bool TryGetDefinition(ToolType toolType, out WeldingToolDefinition definition)
        {
            switch (toolType)
            {
                case ToolType.Electric:
                    definition = ElectricDefinition;
                    return true;
                case ToolType.CO2:
                    definition = Co2Definition;
                    return true;
                default:
                    definition = default;
                    return false;
            }
        }

        public static WeldingToolDefinition GetDefinition(ToolType toolType)
        {
            return TryGetDefinition(toolType, out WeldingToolDefinition definition)
                ? definition
                : default;
        }
    }

    public sealed class WeldingRadiusController
    {
        private WeldingToolDefinition _definition;
        private int _lastInputFrame = int.MinValue;

        public float CurrentRadius { get; private set; }
        public bool IsSessionActive { get; private set; }

        public bool BeginSession(WeldingToolDefinition definition)
        {
            EndSession();
            if (!definition.IsValid)
            {
                return false;
            }

            _definition = definition;
            CurrentRadius = definition.InitialRadius;
            IsSessionActive = true;
            return true;
        }

        public bool TryApplyScroll(float scrollDeltaY, int frameId)
        {
            if (!IsSessionActive
                || !float.IsFinite(scrollDeltaY)
                || Mathf.Approximately(scrollDeltaY, 0f)
                || _lastInputFrame == frameId)
            {
                return false;
            }

            _lastInputFrame = frameId;
            float direction = scrollDeltaY > 0f ? 1f : -1f;
            float adjusted = Mathf.Clamp(
                CurrentRadius + (_definition.RadiusStep * direction),
                _definition.MinimumRadius,
                _definition.MaximumRadius);
            if (Mathf.Approximately(adjusted, CurrentRadius))
            {
                return false;
            }

            CurrentRadius = adjusted;
            return true;
        }

        public void EndSession()
        {
            _definition = default;
            CurrentRadius = 0f;
            IsSessionActive = false;
            _lastInputFrame = int.MinValue;
        }
    }

    public sealed class WeldingCoverageMask
    {
        private readonly bool[] _coveredSamples;

        public int SampleCount => _coveredSamples.Length;
        public int CoveredCount { get; private set; }
        public float Progress01 => SampleCount <= 0
            ? 0f
            : Mathf.Clamp01(CoveredCount / (float)SampleCount);
        public bool IsComplete => SampleCount > 0 && CoveredCount == SampleCount;

        public WeldingCoverageMask(int sampleCount)
        {
            _coveredSamples = sampleCount > 0 ? new bool[sampleCount] : System.Array.Empty<bool>();
        }

        public bool IsCovered(int sampleIndex)
        {
            return sampleIndex >= 0
                && sampleIndex < _coveredSamples.Length
                && _coveredSamples[sampleIndex];
        }

        public int StampSegment(
            bool isInsideActiveZone,
            float startT,
            float endT,
            float radiusT,
            float spacingT)
        {
            if (!isInsideActiveZone
                || SampleCount == 0
                || !float.IsFinite(startT)
                || !float.IsFinite(endT)
                || !float.IsFinite(radiusT)
                || radiusT <= 0f)
            {
                return 0;
            }

            float clampedStart = Mathf.Clamp01(startT);
            float clampedEnd = Mathf.Clamp01(endT);
            float travelDistance = Mathf.Abs(clampedEnd - clampedStart);
            float safeSpacing = float.IsFinite(spacingT) && spacingT > 0f ? spacingT : radiusT;
            int requestedStampCount = Mathf.Max(1, Mathf.CeilToInt(travelDistance / Mathf.Max(0.0001f, safeSpacing)));
            int stampCount = Mathf.Min(requestedStampCount, Mathf.Max(1, SampleCount * 2));
            int coveredBefore = CoveredCount;

            for (int stampIndex = 0; stampIndex <= stampCount; stampIndex++)
            {
                float u = stampCount > 0 ? stampIndex / (float)stampCount : 0f;
                MarkSamplesWithinRadius(Mathf.Lerp(clampedStart, clampedEnd, u), radiusT);
            }

            return CoveredCount - coveredBefore;
        }

        public void Clear()
        {
            System.Array.Clear(_coveredSamples, 0, _coveredSamples.Length);
            CoveredCount = 0;
        }

        private void MarkSamplesWithinRadius(float centerT, float radiusT)
        {
            for (int i = 0; i < _coveredSamples.Length; i++)
            {
                if (_coveredSamples[i])
                {
                    continue;
                }

                float sampleT = _coveredSamples.Length > 1 ? i / (float)(_coveredSamples.Length - 1) : 0f;
                if (Mathf.Abs(sampleT - centerT) <= radiusT)
                {
                    _coveredSamples[i] = true;
                    CoveredCount++;
                }
            }
        }
    }

    public static class WeldingProgressMath
    {
        public static float CalculateOverallProgress(IReadOnlyList<WeldingCoverageMask> zones)
        {
            if (zones == null || zones.Count == 0)
            {
                return 0f;
            }

            float total = 0f;
            for (int i = 0; i < zones.Count; i++)
            {
                total += zones[i]?.Progress01 ?? 0f;
            }

            return Mathf.Clamp01(total / zones.Count);
        }

        public static bool AreAllZonesComplete(IReadOnlyList<WeldingCoverageMask> zones)
        {
            if (zones == null || zones.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i] == null || !zones[i].IsComplete)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static class WeldingMarkerBudget
    {
        public const int MinimumSamplesPerZone = 24;
        public const int MaximumSamplesPerZone = 64;

        public static int ClampSampleCount(int requestedSamples)
        {
            return Mathf.Clamp(requestedSamples, MinimumSamplesPerZone, MaximumSamplesPerZone);
        }

        public static int CalculateVisualObjectLimit(int zoneCount, int requestedSamples)
        {
            int safeZoneCount = Mathf.Max(0, zoneCount);
            return safeZoneCount * (ClampSampleCount(requestedSamples) + 3);
        }
    }

    public sealed class WeldingResultGate
    {
        public MinigameResult Result { get; private set; } = MinigameResult.None;

        public bool TrySet(MinigameResult result)
        {
            if (result == MinigameResult.None || Result != MinigameResult.None)
            {
                return false;
            }

            Result = result;
            return true;
        }

        public void Reset()
        {
            Result = MinigameResult.None;
        }
    }
}
