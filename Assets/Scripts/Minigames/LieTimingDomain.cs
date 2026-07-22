using System;

namespace Game.Minigames
{
    public readonly struct LieTargetZone
    {
        public LieTargetZone(float center, float width)
        {
            Center = center;
            Width = width;
        }

        public float Center { get; }
        public float Width { get; }
        public float HalfWidth => Width * 0.5f;
        public float Start => Center - HalfWidth;
        public float End => Center + HalfWidth;
    }

    public readonly struct LieIndicatorState
    {
        public LieIndicatorState(float position, float direction)
        {
            Position = position;
            Direction = direction;
        }

        public float Position { get; }
        public float Direction { get; }
    }

    public static class LieTimingDomain
    {
        public const float DefaultTargetZoneWidth = 0.3f;
        private const double ReflectionBoundaryEpsilon = 0.0000001d;

        public static float NormalizeTargetZoneWidth(float width)
        {
            if (!IsFinite(width) || width <= 0f)
            {
                return DefaultTargetZoneWidth;
            }

            return Math.Min(width, 1f);
        }

        public static LieTargetZone CreateTargetZone(float width, float randomSample)
        {
            float normalizedWidth = NormalizeTargetZoneWidth(width);
            float normalizedSample = IsFinite(randomSample)
                ? Clamp01(randomSample)
                : 0.5f;
            float halfWidth = normalizedWidth * 0.5f;
            float center = halfWidth + (normalizedSample * (1f - normalizedWidth));

            return new LieTargetZone(center, normalizedWidth);
        }

        public static LieTargetZone CreateTargetZoneAtCenter(float center, float width)
        {
            float normalizedWidth = NormalizeTargetZoneWidth(width);
            float halfWidth = normalizedWidth * 0.5f;
            float normalizedCenter = IsFinite(center) ? center : 0.5f;
            normalizedCenter = Math.Max(halfWidth, Math.Min(1f - halfWidth, normalizedCenter));

            return new LieTargetZone(normalizedCenter, normalizedWidth);
        }

        public static LieIndicatorState StepIndicator(
            float position,
            float direction,
            float speed,
            float deltaTime)
        {
            float normalizedPosition = IsFinite(position) ? Clamp01(position) : 0f;
            float normalizedDirection = IsFinite(direction) && direction < 0f ? -1f : 1f;

            if (!IsFinite(speed) || speed <= 0f || !IsFinite(deltaTime) || deltaTime <= 0f)
            {
                return new LieIndicatorState(normalizedPosition, normalizedDirection);
            }

            double phase = normalizedDirection > 0f
                ? normalizedPosition
                : 2d - normalizedPosition;
            double distance = (double)speed * deltaTime;
            double reflectedPhase = PositiveModulo(phase + distance, 2d);

            if (reflectedPhase <= ReflectionBoundaryEpsilon
                || 2d - reflectedPhase <= ReflectionBoundaryEpsilon)
            {
                reflectedPhase = 0d;
            }
            else if (Math.Abs(reflectedPhase - 1d) <= ReflectionBoundaryEpsilon)
            {
                reflectedPhase = 1d;
            }

            if (reflectedPhase < 1d)
            {
                return new LieIndicatorState((float)reflectedPhase, 1f);
            }

            return new LieIndicatorState((float)(2d - reflectedPhase), -1f);
        }

        public static bool IsIndicatorInZone(float indicatorPosition, LieTargetZone zone)
        {
            if (!IsFinite(indicatorPosition))
            {
                return false;
            }

            return indicatorPosition >= zone.Start && indicatorPosition <= zone.End;
        }

        public static float CalculateAccuracy(float indicatorPosition, LieTargetZone zone)
        {
            if (!IsIndicatorInZone(indicatorPosition, zone) || zone.HalfWidth <= 0f)
            {
                return 0f;
            }

            if (indicatorPosition <= zone.Start || indicatorPosition >= zone.End)
            {
                return 0f;
            }

            float distanceFromCenter = Math.Abs(indicatorPosition - zone.Center);
            return Clamp01(1f - (distanceFromCenter / zone.HalfWidth));
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }

        private static double PositiveModulo(double value, double divisor)
        {
            double remainder = value % divisor;
            return remainder < 0d ? remainder + divisor : remainder;
        }
    }
}
