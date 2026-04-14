namespace Game.Core.Events
{
    public struct ObjectiveCompletedEvent
    {
        public Objective Objective { get; }

        public ObjectiveCompletedEvent(Objective objective)
        {
            Objective = objective;
        }
    }
}
