using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// BaseInteractable is an abstract base class for all interactable objects in the game.
    /// Subclasses must implement the Interact() method to define custom interaction behavior.
    /// 
    /// Features:
    /// - Enforces consistent interaction interface across all interactables
    /// - Provides CanInteract property for conditional interaction
    /// - Optional lifecycle callbacks (OnInteractableEnter, OnInteractableExit)
    /// - Easy to extend with custom interaction logic
    /// 
    /// Usage:
    /// Create a subclass and override Interact() to define what happens when interacted with.
    /// Use CanInteract to prevent interaction when needed (e.g., during animations).
    /// 
    /// Example:
    /// public class Door : BaseInteractable
    /// {
    ///     public override void Interact()
    ///     {
    ///         // Door opening logic
    ///     }
    /// }
    /// </summary>
    public abstract class BaseInteractable : MonoBehaviour
    {
        [SerializeField]
        protected bool _canInteract = true;
        public bool CanInteract
        {
            get { return _canInteract; }
            protected set { _canInteract = value; }
        }

        public abstract void Interact();
        public virtual void OnInteractableEnter()
        {
        }

        public virtual void OnInteractableExit()
        {
        }
        
        public void SetCanInteract(bool canInteract)
        {
            _canInteract = canInteract;
        }
    }
}

