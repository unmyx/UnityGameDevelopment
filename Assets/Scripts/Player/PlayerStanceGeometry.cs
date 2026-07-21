using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Pure geometry rules shared by the runtime crouch controller and Edit Mode tests.
    /// </summary>
    public static class PlayerStanceGeometry
    {
        private const float MinimumHeightEpsilon = 0.001f;

        public static float NormalizeHeight(float requestedHeight, float radius, float fallbackHeight)
        {
            float safeRadius = IsFinite(radius) ? Mathf.Max(0f, radius) : 0f;
            float minimumHeight = Mathf.Max(MinimumHeightEpsilon, safeRadius * 2f);
            float safeFallback = IsFinite(fallbackHeight)
                ? Mathf.Max(minimumHeight, fallbackHeight)
                : minimumHeight;

            return IsFinite(requestedHeight)
                ? Mathf.Max(minimumHeight, requestedHeight)
                : safeFallback;
        }

        public static Vector3 CenterPreservingBottom(
            Vector3 referenceCenter,
            float referenceHeight,
            float targetHeight)
        {
            float referenceBottom = BottomY(referenceCenter, referenceHeight);
            Vector3 targetCenter = referenceCenter;
            targetCenter.y = referenceBottom + (targetHeight * 0.5f);
            return targetCenter;
        }

        public static float BottomY(Vector3 center, float height)
        {
            return center.y - (height * 0.5f);
        }

        public static float TopSphereCenterY(Vector3 center, float height, float radius)
        {
            return center.y + Mathf.Max(0f, (height * 0.5f) - Mathf.Max(0f, radius));
        }

        public static float CameraYForHeight(
            float height,
            float crouchingHeight,
            float standingHeight,
            float crouchingCameraY,
            float standingCameraY)
        {
            float stanceRange = standingHeight - crouchingHeight;
            if (!IsFinite(height)
                || !IsFinite(stanceRange)
                || Mathf.Abs(stanceRange) <= Mathf.Epsilon)
            {
                return standingCameraY;
            }

            float standingBlend = Mathf.InverseLerp(crouchingHeight, standingHeight, height);
            float cameraY = Mathf.Lerp(crouchingCameraY, standingCameraY, standingBlend);
            return Mathf.Clamp(
                cameraY,
                Mathf.Min(crouchingCameraY, standingCameraY),
                Mathf.Max(crouchingCameraY, standingCameraY));
        }

        public static float MoveHeight(
            float currentHeight,
            float targetHeight,
            float crouchingHeight,
            float standingHeight,
            float transitionSpeed,
            float deltaTime)
        {
            if (!IsFinite(currentHeight) || !IsFinite(targetHeight))
            {
                return targetHeight;
            }

            float stanceRange = Mathf.Abs(standingHeight - crouchingHeight);
            float safeSpeed = IsFinite(transitionSpeed) ? Mathf.Max(0f, transitionSpeed) : 0f;
            float safeDeltaTime = IsFinite(deltaTime) ? Mathf.Max(0f, deltaTime) : 0f;
            return Mathf.MoveTowards(currentHeight, targetHeight, stanceRange * safeSpeed * safeDeltaTime);
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
