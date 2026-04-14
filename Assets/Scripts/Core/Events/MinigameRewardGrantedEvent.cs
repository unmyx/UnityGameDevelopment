using Game.Minigames;

namespace Game.Core.Events
{
    public struct MinigameRewardGrantedEvent
    {
        public RewardGrantedData Data { get; }

        public MinigameRewardGrantedEvent(RewardGrantedData data)
        {
            Data = data;
        }
    }
}
