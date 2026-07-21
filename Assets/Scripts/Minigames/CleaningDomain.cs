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
}
