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
}
