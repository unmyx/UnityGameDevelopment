using Game.Interaction;
using Game.Inventory;
using Game.UI;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Lightweight player-owned context container used as a migration seam
    /// between current singleplayer lookups and future multiplayer ownership.
    /// </summary>
    public sealed class PlayerContext
    {
        public string PlayerId { get; }
        public PlayerController PlayerController { get; private set; }
        public PlayerInputHandler InputHandler { get; private set; }
        public FirstPersonCamera FirstPersonCamera { get; private set; }
        public InteractionSystem InteractionSystem { get; private set; }
        public InventoryGridUI InventoryGridUI { get; private set; }
        public GameplayHUD GameplayHUD { get; private set; }
        public LocalPlayerPresentationState PresentationState { get; }
        public int SelectedQuickSlotIndex { get; private set; }

        public Transform RootTransform
        {
            get
            {
                if (PlayerController != null)
                {
                    return PlayerController.transform;
                }

                if (FirstPersonCamera != null)
                {
                    return FirstPersonCamera.transform.root;
                }

                if (InteractionSystem != null)
                {
                    return InteractionSystem.transform.root;
                }

                return null;
            }
        }

        public bool HasAnyRuntimeReference =>
            PlayerController != null
            || InputHandler != null
            || FirstPersonCamera != null
            || InteractionSystem != null
            || InventoryGridUI != null
            || GameplayHUD != null;

        public PlayerContext(string playerId)
        {
            PlayerId = string.IsNullOrWhiteSpace(playerId) ? "local_player_0" : playerId.Trim();
            PresentationState = new LocalPlayerPresentationState();
        }

        internal void UpdateReferences(
            PlayerController playerController,
            PlayerInputHandler inputHandler,
            FirstPersonCamera firstPersonCamera,
            InteractionSystem interactionSystem,
            InventoryGridUI inventoryGridUI,
            GameplayHUD gameplayHUD)
        {
            if (playerController != null)
            {
                PlayerController = playerController;
            }

            if (inputHandler != null)
            {
                InputHandler = inputHandler;
            }

            if (firstPersonCamera != null)
            {
                FirstPersonCamera = firstPersonCamera;
            }

            if (interactionSystem != null)
            {
                InteractionSystem = interactionSystem;
            }

            if (inventoryGridUI != null)
            {
                InventoryGridUI = inventoryGridUI;
            }

            if (gameplayHUD != null)
            {
                GameplayHUD = gameplayHUD;
            }
        }

        internal bool ContainsSource(MonoBehaviour source)
        {
            if (source == null)
            {
                return false;
            }

            return ReferenceEquals(source, PlayerController)
                   || ReferenceEquals(source, InputHandler)
                   || ReferenceEquals(source, FirstPersonCamera)
                   || ReferenceEquals(source, InteractionSystem)
                   || ReferenceEquals(source, InventoryGridUI)
                   || ReferenceEquals(source, GameplayHUD);
        }

        internal void ClearReferencesFromSource(MonoBehaviour source)
        {
            if (source == null)
            {
                return;
            }

            if (ReferenceEquals(source, PlayerController))
            {
                PlayerController = null;
            }

            if (ReferenceEquals(source, InputHandler))
            {
                InputHandler = null;
            }

            if (ReferenceEquals(source, FirstPersonCamera))
            {
                FirstPersonCamera = null;
            }

            if (ReferenceEquals(source, InteractionSystem))
            {
                InteractionSystem = null;
            }

            if (ReferenceEquals(source, InventoryGridUI))
            {
                InventoryGridUI = null;
            }

            if (ReferenceEquals(source, GameplayHUD))
            {
                GameplayHUD = null;
            }
        }

        public void SetSelectedQuickSlotIndex(int selectedQuickSlotIndex)
        {
            SelectedQuickSlotIndex = InventoryQuickSlotRules.ClampIndex(selectedQuickSlotIndex);
        }

        public bool TryGetSelectedQuickSlotItem(out InventoryItem item)
        {
            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null)
            {
                item = null;
                return false;
            }

            return inventorySystem.TryGetQuickSlotItem(SelectedQuickSlotIndex, out item);
        }

        public bool TryGetSelectedTool(out ToolType toolType)
        {
            if (TryGetSelectedQuickSlotItem(out InventoryItem item) && item != null)
            {
                toolType = item.ToolType;
                return toolType != ToolType.None;
            }

            toolType = ToolType.None;
            return false;
        }
    }
}
