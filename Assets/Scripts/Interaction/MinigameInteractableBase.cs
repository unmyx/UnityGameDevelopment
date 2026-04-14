using UnityEngine;
using Game.Core;
using Game.Minigames;

namespace Game.Interaction
{
    /// <summary>
    /// Shared base for interactables that launch a minigame.
    /// Subclasses only need to provide the minigame data and component type.
    /// </summary>
    public abstract class MinigameInteractableBase : BaseInteractable
    {
        public override void Interact()
        {
            if (!CanInteract)
            {
                return;
            }

            MinigameData data = BuildMinigameData();
            if (data == null || string.IsNullOrEmpty(data.minigameId))
            {
                return;
            }

            StartMinigame(data);
        }

        protected abstract MinigameData BuildMinigameData();

        protected abstract void StartMinigame(MinigameData data);
    }
}
