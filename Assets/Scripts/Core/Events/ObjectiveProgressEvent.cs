namespace Game.Core.Events
{
    public struct ObjectiveProgressEvent
    {
        public Objective Objective { get; }

        public ObjectiveProgressEvent(Objective objective)
        {
            Objective = objective;
        }
    }
}
