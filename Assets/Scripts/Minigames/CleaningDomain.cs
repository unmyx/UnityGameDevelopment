using System;
using System.Collections.Generic;
using Game.Inventory;
using UnityEngine;

namespace Game.Minigames
{
    public static class CleaningToolRules
    {
        public static int GetRequiredPasses(ToolType toolType)
        {
            return toolType switch
            {
                ToolType.Water => 6,
                ToolType.Gasoline => 4,
                ToolType.Chemical => 2,
                _ => 0
            };
        }
    }

    public sealed class CleaningStainProgress
    {
        public int RequiredPasses { get; }
        public int CompletedPasses { get; private set; }
        public bool IsComplete => RequiredPasses > 0 && CompletedPasses >= RequiredPasses;
        public float Progress01 => RequiredPasses <= 0
            ? 0f
            : Mathf.Clamp01(CompletedPasses / (float)RequiredPasses);

        public CleaningStainProgress(ToolType toolType)
            : this(CleaningToolRules.GetRequiredPasses(toolType))
        {
        }

        public CleaningStainProgress(int requiredPasses)
        {
            RequiredPasses = Mathf.Max(0, requiredPasses);
            CompletedPasses = 0;
        }

        public bool TryRegisterPass()
        {
            if (RequiredPasses <= 0 || IsComplete)
            {
                return false;
            }

            CompletedPasses = Mathf.Min(CompletedPasses + 1, RequiredPasses);
            return true;
        }

        public void Reset()
        {
            CompletedPasses = 0;
        }
    }

    public static class CleaningSpawnRules
    {
        public const int RequiredSurfaceCount = 3;
        public const int MinimumStainsPerSurface = 1;
        public const int MaximumStainsPerSurface = 3;

        public static bool TryCreateSurfaceStainCounts(
            int surfaceCount,
            System.Random random,
            out int[] stainCounts)
        {
            stainCounts = Array.Empty<int>();
            if (surfaceCount != RequiredSurfaceCount || random == null)
            {
                return false;
            }

            stainCounts = new int[RequiredSurfaceCount];
            for (int i = 0; i < stainCounts.Length; i++)
            {
                stainCounts[i] = random.Next(MinimumStainsPerSurface, MaximumStainsPerSurface + 1);
            }

            return true;
        }

        public static bool HasRequiredSurfaceCount(int surfaceCount)
        {
            return surfaceCount == RequiredSurfaceCount;
        }
    }

    public sealed class CleaningSurfacePlacementBudget
    {
        private readonly List<Vector3> _acceptedPositions;
        private readonly float _minimumSpacingSquared;

        public int TargetCount { get; }
        public int MaximumAttempts { get; }
        public int Attempts { get; private set; }
        public IReadOnlyList<Vector3> AcceptedPositions => _acceptedPositions;
        public bool IsComplete => _acceptedPositions.Count == TargetCount;
        public bool CanAttempt => !IsComplete && Attempts < MaximumAttempts;

        public CleaningSurfacePlacementBudget(int targetCount, int maximumAttempts, float minimumSpacing)
        {
            TargetCount = Mathf.Max(0, targetCount);
            MaximumAttempts = Mathf.Max(0, maximumAttempts);
            _minimumSpacingSquared = Mathf.Max(0f, minimumSpacing * minimumSpacing);
            _acceptedPositions = new List<Vector3>(TargetCount);
        }

        public bool TryBeginAttempt()
        {
            if (!CanAttempt)
            {
                return false;
            }

            Attempts++;
            return true;
        }

        public bool TryAccept(Vector3 candidate)
        {
            if (IsComplete)
            {
                return false;
            }

            for (int i = 0; i < _acceptedPositions.Count; i++)
            {
                if ((_acceptedPositions[i] - candidate).sqrMagnitude < _minimumSpacingSquared)
                {
                    return false;
                }
            }

            _acceptedPositions.Add(candidate);
            return true;
        }
    }

    public sealed class CleaningSpawnTransaction<T>
    {
        private readonly List<T> _pendingItems = new List<T>();

        public int PendingCount => _pendingItems.Count;

        public void Add(T item)
        {
            _pendingItems.Add(item);
        }

        public void CommitTo(ICollection<T> destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            for (int i = 0; i < _pendingItems.Count; i++)
            {
                destination.Add(_pendingItems[i]);
            }

            _pendingItems.Clear();
        }

        public void Rollback(Action<T> cleanup)
        {
            for (int i = 0; i < _pendingItems.Count; i++)
            {
                cleanup?.Invoke(_pendingItems[i]);
            }

            _pendingItems.Clear();
        }
    }

    public sealed class CleaningStrokeTracker
    {
        private int? _latchedStainId;
        private int _lastPassFrame = int.MinValue;

        public int? LatchedStainId => _latchedStainId;
        public bool IsTrackingToolUse { get; private set; }

        public bool TryRegisterPass(
            bool toolUseActive,
            int? hoveredStainId,
            int frameId,
            out int stainId)
        {
            stainId = -1;
            IsTrackingToolUse = toolUseActive;

            if (!toolUseActive || !hoveredStainId.HasValue)
            {
                _latchedStainId = null;
                return false;
            }

            if (_latchedStainId == hoveredStainId || _lastPassFrame == frameId)
            {
                return false;
            }

            _latchedStainId = hoveredStainId;
            _lastPassFrame = frameId;
            stainId = hoveredStainId.Value;
            return true;
        }

        public void Reset()
        {
            _latchedStainId = null;
            _lastPassFrame = int.MinValue;
            IsTrackingToolUse = false;
        }
    }

    public static class CleaningProgressMath
    {
        public static float CalculateOverallProgress(int completedPasses, int requiredPasses)
        {
            if (requiredPasses <= 0)
            {
                return 0f;
            }

            int boundedCompletedPasses = Mathf.Clamp(completedPasses, 0, requiredPasses);
            return boundedCompletedPasses / (float)requiredPasses;
        }
    }

    public static class CleaningPointerGeometry
    {
        public static bool IsRayOrSweepInsideRadius(
            Ray currentRay,
            bool hasPreviousRay,
            Ray previousRay,
            Vector3 target,
            float radius,
            out float currentDepth)
        {
            currentDepth = Vector3.Dot(target - currentRay.origin, currentRay.direction);
            if (currentDepth <= 0f)
            {
                return false;
            }

            Vector3 currentPoint = currentRay.origin + (currentRay.direction * currentDepth);
            float radiusSquared = Mathf.Max(0f, radius * radius);
            if ((target - currentPoint).sqrMagnitude <= radiusSquared)
            {
                return true;
            }

            if (!hasPreviousRay)
            {
                return false;
            }

            float previousDepth = Vector3.Dot(target - previousRay.origin, previousRay.direction);
            if (previousDepth <= 0f)
            {
                return false;
            }

            Vector3 previousPoint = previousRay.origin + (previousRay.direction * previousDepth);
            return DistanceSquaredToSegment(target, previousPoint, currentPoint) <= radiusSquared;
        }

        private static float DistanceSquaredToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 segment = end - start;
            float segmentLengthSquared = segment.sqrMagnitude;
            if (segmentLengthSquared <= Mathf.Epsilon)
            {
                return (point - start).sqrMagnitude;
            }

            float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / segmentLengthSquared);
            Vector3 closestPoint = start + (segment * t);
            return (point - closestPoint).sqrMagnitude;
        }
    }

    public sealed class CleaningResultGate
    {
        public MinigameResult Result { get; private set; } = MinigameResult.None;
        public bool HasResult => Result != MinigameResult.None;

        public bool TrySet(MinigameResult result)
        {
            if (result == MinigameResult.None || HasResult)
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

    public static class CleaningOutcomeRules
    {
        public static MinigameResult Resolve(bool timeExpired, bool allStainsClean)
        {
            if (timeExpired)
            {
                return MinigameResult.Fail;
            }

            return allStainsClean ? MinigameResult.Pass : MinigameResult.None;
        }
    }
}
