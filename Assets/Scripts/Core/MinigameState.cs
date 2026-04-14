using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// MinigameState implementation of IGameState.
    /// GameManager owns transition decisions in/out of this state.
    /// </summary>
    public class MinigameState : MonoBehaviour, IGameState
    {
        public void OnStateEnter()
        {
        }

        public void OnStateUpdate()
        {
        }

        public void OnStateExit()
        {
        }
    }
}
