namespace Game.Inventory
{
    public static class InventoryQuickSlotRules
    {
        public const int MaxQuickSlots = 9;

        public static bool IsValidIndex(int quickSlotIndex)
        {
            return quickSlotIndex >= 0 && quickSlotIndex < MaxQuickSlots;
        }

        public static int ClampIndex(int quickSlotIndex)
        {
            return UnityEngine.Mathf.Clamp(quickSlotIndex, 0, MaxQuickSlots - 1);
        }

        public static bool TryGetGridPosition(
            int quickSlotIndex,
            int gridWidth,
            int gridHeight,
            out int gridX,
            out int gridY)
        {
            gridX = -1;
            gridY = -1;
            if (!IsValidIndex(quickSlotIndex) || gridWidth <= 0 || gridHeight <= 0)
            {
                return false;
            }

            int resolvedX = quickSlotIndex % gridWidth;
            int resolvedY = quickSlotIndex / gridWidth;
            if (resolvedX < 0 || resolvedX >= gridWidth || resolvedY < 0 || resolvedY >= gridHeight)
            {
                return false;
            }

            gridX = resolvedX;
            gridY = resolvedY;
            return true;
        }

        public static bool TryGetQuickSlotIndex(
            int gridX,
            int gridY,
            int gridWidth,
            int gridHeight,
            out int quickSlotIndex)
        {
            quickSlotIndex = -1;
            if (gridWidth <= 0
                || gridHeight <= 0
                || gridX < 0
                || gridX >= gridWidth
                || gridY < 0
                || gridY >= gridHeight)
            {
                return false;
            }

            int resolvedIndex = (gridY * gridWidth) + gridX;
            if (!IsValidIndex(resolvedIndex))
            {
                return false;
            }

            quickSlotIndex = resolvedIndex;
            return true;
        }

        public static bool TryGetIndexForUserSlotNumber(int userSlotNumber, out int quickSlotIndex)
        {
            quickSlotIndex = userSlotNumber - 1;
            return IsValidIndex(quickSlotIndex);
        }
    }
}
