using UnityEngine;
using Game.Player;

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
            PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.Minigame);
        }

        public void OnStateUpdate()
        {
        }

        public void OnStateExit()
        {
            PlayerContextLocator.TrySetLocalPresentationMode(LocalPlayerPresentationMode.FreePlay);
        }
    }
}
