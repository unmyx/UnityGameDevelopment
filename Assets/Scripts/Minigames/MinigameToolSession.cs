using System;
using Game.Inventory;

namespace Game.Minigames
{
    public sealed class MinigameToolSession
    {
        public ToolType ActiveTool { get; private set; } = ToolType.None;

        public bool TryCapture(ToolType selectedTool, Func<ToolType, bool> supportsTool)
        {
            ActiveTool = ToolType.None;
            if (supportsTool == null || !supportsTool(selectedTool))
            {
                return false;
            }

            ActiveTool = selectedTool;
            return true;
        }

        public void Clear()
        {
            ActiveTool = ToolType.None;
        }
    }

    public static class MinigameToolSnapshot
    {
        public const string ParameterKey = "selected_tool_type";

        public static void Set(MinigameData data, ToolType toolType)
        {
            if (data != null)
            {
                data.SetParameter(ParameterKey, toolType);
            }
        }

        public static ToolType Get(MinigameData data)
        {
            object value = data?.GetParameter(ParameterKey);
            return value is ToolType toolType ? toolType : ToolType.None;
        }
    }
}
