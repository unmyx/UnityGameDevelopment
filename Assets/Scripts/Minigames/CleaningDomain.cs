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
}
