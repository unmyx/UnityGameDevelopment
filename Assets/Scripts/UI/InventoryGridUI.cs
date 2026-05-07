using System.Collections;
using System;
using UnityEngine;
using UnityEngine.UI;
using Game.Inventory;
using Game.Core;
using Game.Input;
using Game.Player;
using Game.Interaction;
using Game.Minigames;
using Game.Networking;

namespace Game.UI
{
    /// <summary>
    /// Mirrors the first 9 inventory slots into a fixed HUD bar.
    /// Intended for a 1x9 middle-bottom gameplay display.
    /// </summary>
    public class InventoryGridUI : MonoBehaviour
    {
        private static readonly System.Collections.Generic.HashSet<string> LoggedFallbackValidationWarnings =
            new System.Collections.Generic.HashSet<string>();
        private const string LocalPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;

        [Serializable]
        private class DropPrefabEntry
        {
            public string itemId = string.Empty;
            public GameObject pickupPrefab;
        }

        [Header("Slot Icon Images (1-9)")]
        [SerializeField]
        [Tooltip("Assign icon Image components for Slot_1 through Slot_9.")]
        private Image[] _slotIcons = new Image[9];

        [Header("Slot Background Images (1-9)")]
        [SerializeField]
        [Tooltip("Assign slot background Image components for Slot_1 through Slot_9.")]
        private Image[] _slotBackgrounds = new Image[9];

        [Header("Slot Layout")]
        [SerializeField]
        [Tooltip("Optional SlotGrid HorizontalLayoutGroup used to keep unlocked slots centered.")]
        private HorizontalLayoutGroup _slotGridLayoutGroup;

        [Header("Selected Slot Visuals")]
        [SerializeField]
        [Tooltip("Color applied to selected slot background.")]
        private Color _selectedSlotColor = new Color(0.95f, 0.85f, 0.25f, 0.95f);

        [SerializeField]
        [Tooltip("Color applied to non-selected slot backgrounds.")]
        private Color _unselectedSlotColor = new Color(0.18f, 0.18f, 0.18f, 0.85f);

        [Header("Visual Settings")]
        [SerializeField]
        [Tooltip("Optional sprite shown when a slot is empty. Leave null for no sprite.")]
        private Sprite _emptySlotSprite;

        [SerializeField]
        [Tooltip("Color used when slot is empty.")]
        private Color _emptySlotColor = new Color(1f, 1f, 1f, 0f);

        [SerializeField]
        [Tooltip("Color used when slot contains an item icon.")]
        private Color _filledSlotColor = Color.white;

        [Header("Held Item (First Person)")]
        [SerializeField]
        [Tooltip("Required anchor for held item objects under the player camera (for example: Player/PlayerBody/Camera/HeldItemAnchor).")]
        private Transform _heldItemAnchor;

        [SerializeField]
        [Tooltip("Target largest bounds size for held models after auto-normalization.")]
        private float _heldModelTargetSize = 0.14f;

        [Header("Selected Item Model Preview")]
        [SerializeField]
        [Tooltip("When enabled, shows a selected-item UI model preview above the toolbar. Disabled by default for normal gameplay HUD.")]
        private bool _enableSelectedItemModelPreview = false;

        [SerializeField]
        [Tooltip("Optional RawImage used to render selected item model preview.")]
        private RawImage _selectedItemPreviewRawImage;

        [SerializeField]
        [Tooltip("Automatically creates a preview RawImage when one is not assigned.")]
        private bool _autoCreatePreviewRawImage = true;

        [SerializeField]
        [Tooltip("Screen-space size for auto-created preview RawImage.")]
        private Vector2 _previewSize = new Vector2(170f, 170f);

        [SerializeField]
        [Tooltip("Anchored position for auto-created preview RawImage.")]
        private Vector2 _previewAnchoredPosition = new Vector2(0f, 110f);

        [SerializeField]
        [Tooltip("Runtime RenderTexture resolution for the model preview.")]
        private int _previewTextureSize = 512;

        [SerializeField]
        [Tooltip("Euler rotation used for preview camera framing.")]
        private Vector3 _previewCameraEulerAngles = new Vector3(15f, -145f, 0f);

        [SerializeField]
        [Tooltip("Extra framing multiplier around rendered model bounds.")]
        private float _previewFramingPadding = 1.25f;

        [SerializeField]
        [Tooltip("Background color used by the preview camera.")]
        private Color _previewBackgroundColor = new Color(0f, 0f, 0f, 0f);

        [SerializeField]
        [Tooltip("Far-away world origin used to isolate preview rendering from gameplay scene geometry.")]
        private Vector3 _previewWorldOrigin = new Vector3(10000f, 10000f, 10000f);

        [Header("Drop Selected Valuable (MVP)")]
        [SerializeField]
        private KeyCode _dropSelectedItemKey = KeyCode.G;

        [SerializeField]
        [Min(0.05f)]
        private float _dropCooldownSeconds = 0.12f;

        [SerializeField]
        [Min(0.1f)]
        private float _dropForwardDistance = 1.1f;

        [SerializeField]
        [Min(0f)]
        private float _dropUpwardOffset = 0.15f;

        [SerializeField]
        [Min(0.5f)]
        private float _dropRaycastDistance = 3f;

        [SerializeField]
        [Min(0f)]
        private float _dropSurfaceNormalOffset = 0.05f;

        [SerializeField]
        [Tooltip("Tracked valuable itemId to world pickup prefab mappings used by drop.")]
        private DropPrefabEntry[] _trackedValuableDropPrefabs =
        {
            new DropPrefabEntry { itemId = "wedding_ring_gold" },
            new DropPrefabEntry { itemId = "wedding_ring_silver" }
        };

        private const int VisibleSlotCount = 9;
        private const string PreviewCameraName = "InventoryPreviewCamera";
        private const string PreviewRootName = "InventoryPreviewRoot";
        private const string PreviewRawImageName = "SelectedItemPreview";
        private const string HeldItemAnchorChildName = "HeldItemAnchor";
        private bool _subscribed;
        private bool _inputSubscribed;
        private int _selectedSlotIndex;
        private GameObject _currentHeldItemObject;
        private InventoryItem _currentHeldItemItem;
        private GameObject _currentPreviewObject;
        private InventoryItem _currentPreviewItem;
        private RenderTexture _previewRenderTexture;
        private Camera _previewCamera;
        private Transform _previewRoot;
        private bool _wasMinigameActiveLastFrame;
        private float _nextAllowedDropTime;
        private bool _hasLoggedMissingHeldItemAnchor;
        private static bool _hasLoggedMissingHeldItemAnchorSession;
        private bool _hasLoggedCompatibilityCameraFallback;
        private string _lastHeldItemAnchorBindFailureReason = string.Empty;
        private bool _awaitingNetworkDropResponse;
        private ulong _pendingNetworkDropRequestId;
        private string _pendingNetworkDropItemId = string.Empty;
        private int _pendingNetworkDropGridX = -1;
        private int _pendingNetworkDropGridY = -1;
        private string _pendingNetworkDropOwnerPlayerId = string.Empty;
        private static ulong _networkDropRequestSequence;

        private void Awake()
        {
            EnsureSlotImageSafety();
            EnsureSlotGridLayoutBinding();
            EnsureSlotGridCentered();
            _selectedSlotIndex = 0;
            // Intentionally avoid strict held-anchor binding in Awake.
            // Camera/context registration may not be ready yet during bootstrap,
            // and binding is retried safely in OnEnable/DelayedInitialRefresh/use-time paths.

            if (_enableSelectedItemModelPreview)
            {
                EnsurePreviewRawImage();
                EnsurePreviewRuntime();
            }
            else
            {
                DisablePreviewImage();
            }
        }

        private void OnEnable()
        {
            RegisterLocalContext();
            TrySubscribe();
            TrySubscribeInput();
            NetworkSessionProgressAuthority.OnTrackedLootDropResponse += HandleTrackedLootDropResponse;
            TryRefreshHeldItemAnchorBinding();
            StartCoroutine(DelayedInitialRefresh());
        }

        private void OnDisable()
        {
            PlayerContextRegistry.Unregister(this, LocalPlayerId);
            Unsubscribe();
            UnsubscribeInput();
            NetworkSessionProgressAuthority.OnTrackedLootDropResponse -= HandleTrackedLootDropResponse;
            ClearHeldItemObject();
            ClearPreviewObject();
            DisablePreviewImage();
            ClearPendingNetworkDropRequest();
        }

        private void OnDestroy()
        {
            if (_previewCamera != null)
            {
                Destroy(_previewCamera.gameObject);
                _previewCamera = null;
            }

            if (_previewRoot != null)
            {
                Destroy(_previewRoot.gameObject);
                _previewRoot = null;
            }

            if (_previewRenderTexture != null)
            {
                _previewRenderTexture.Release();
                Destroy(_previewRenderTexture);
                _previewRenderTexture = null;
            }
        }

        private IEnumerator DelayedInitialRefresh()
        {
            yield return null;
            TryRefreshHeldItemAnchorBinding();
            RefreshAllSlots();
        }

        public void TryRefreshHeldItemAnchorBinding()
        {
            if (!TryRebindHeldItemAnchorFromScene(forceRebindForNetworkSession: true))
            {
                return;
            }

            if (IsMinigameActive())
            {
                return;
            }

            InventoryItem selectedItem = GetItemAtVisibleIndex(_selectedSlotIndex);
            if (selectedItem == null)
            {
                return;
            }

            SelectSlot(_selectedSlotIndex, forceRefresh: true);
        }

        private void TrySubscribe()
        {
            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null || _subscribed)
            {
                return;
            }

            inventorySystem.OnInventoryChanged += HandleInventoryChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null || !_subscribed)
            {
                _subscribed = false;
                return;
            }

            inventorySystem.OnInventoryChanged -= HandleInventoryChanged;
            _subscribed = false;
        }

        private void HandleInventoryChanged(InventoryItem item, bool added)
        {
            RefreshAllSlots();
        }

        private void Update()
        {
            RegisterLocalContext();

            if (!_subscribed)
            {
                TrySubscribe();
            }

            if (!_inputSubscribed)
            {
                TrySubscribeInput();
            }

            bool isMinigameActive = IsMinigameActive();
            if (isMinigameActive)
            {
                ClearHeldItemObject();
                ClearPreviewObject();
                DisablePreviewImage();
            }

            if (_wasMinigameActiveLastFrame && !isMinigameActive)
            {
                RefreshAllSlots();
            }

            if (!isMinigameActive)
            {
                TryHandleDropInput();
            }

            _wasMinigameActiveLastFrame = isMinigameActive;
        }

        public void RefreshAllSlots()
        {
            InventorySystem inventorySystem = InventorySystem.Instance;
            int unlockedQuickSlots = GetUnlockedQuickSlotCount();
            EnsureSelectedSlotIsVisible(unlockedQuickSlots);
            ApplySlotVisibility(unlockedQuickSlots);

            if (inventorySystem == null)
            {
                ClearAllSlots();
                ApplySelectedSlotVisuals();
                RefreshHeldItemObject();
                SyncSelectedSlotToLocalContext();
                return;
            }

            (int width, int height) = inventorySystem.GetGridDimensions();
            int maxSlots = Mathf.Min(VisibleSlotCount, _slotIcons.Length);

            for (int index = 0; index < maxSlots; index++)
            {
                if (index >= unlockedQuickSlots)
                {
                    SetSlotVisual(index, null);
                    continue;
                }

                int x = index % width;
                int y = index / width;

                if (y >= height)
                {
                    SetSlotVisual(index, null);
                    continue;
                }

                InventorySlot slot = inventorySystem.GetSlot(x, y);
                InventoryItem slotItem = slot != null ? slot.GetItem() : null;
                SetSlotVisual(index, slotItem);
            }

            ApplySelectedSlotVisuals();
            RefreshHeldItemObject();
            RefreshSelectedItemPreview();
            SyncSelectedSlotToLocalContext();
        }

        public void ForceRefreshInventoryAndHeldItem()
        {
            RefreshAllSlots();
        }

        public int GetSelectedSlotIndex()
        {
            return _selectedSlotIndex;
        }

        public void RestoreSelectedSlotFromSave(int slotIndex)
        {
            SelectSlot(slotIndex, forceRefresh: true);
        }

        private void ClearAllSlots()
        {
            for (int i = 0; i < _slotIcons.Length; i++)
            {
                SetSlotVisual(i, null);
            }
        }

        private void SetSlotVisual(int index, InventoryItem item)
        {
            if (index < 0 || index >= _slotIcons.Length)
            {
                return;
            }

            Image iconImage = _slotIcons[index];
            if (iconImage == null)
            {
                return;
            }

            if (item != null && item.ItemIcon != null)
            {
                iconImage.sprite = item.ItemIcon;
                iconImage.color = _filledSlotColor;
                iconImage.enabled = true;
            }
            else
            {
                iconImage.sprite = _emptySlotSprite;
                iconImage.color = _emptySlotColor;
                iconImage.enabled = _emptySlotSprite != null;
            }
        }

        private void EnsureSlotImageSafety()
        {
            for (int i = 0; i < _slotIcons.Length; i++)
            {
                if (_slotIcons[i] != null)
                {
                    _slotIcons[i].raycastTarget = false;
                }
            }

            for (int i = 0; i < _slotBackgrounds.Length; i++)
            {
                if (_slotBackgrounds[i] != null)
                {
                    _slotBackgrounds[i].raycastTarget = false;
                }
            }

        }

        private void TrySubscribeInput()
        {
            InputManager inputManager = InputManager.Instance;
            if (inputManager == null || _inputSubscribed)
            {
                return;
            }

            inputManager.OnSelectSlot += HandleSelectSlotInput;
            _inputSubscribed = true;
        }

        private void UnsubscribeInput()
        {
            InputManager inputManager = InputManager.Instance;
            if (inputManager == null || !_inputSubscribed)
            {
                _inputSubscribed = false;
                return;
            }

            inputManager.OnSelectSlot -= HandleSelectSlotInput;
            _inputSubscribed = false;
        }

        private void HandleSelectSlotInput(int slotIndex)
        {
            if (IsMinigameActive())
            {
                return;
            }

            int unlockedQuickSlots = GetUnlockedQuickSlotCount();
            if (slotIndex < 0 || slotIndex >= unlockedQuickSlots)
            {
                return;
            }

            SelectSlot(slotIndex);
        }

        private void SelectSlot(int slotIndex, bool forceRefresh = false)
        {
            int unlockedQuickSlots = GetUnlockedQuickSlotCount();
            int clampedIndex = Mathf.Clamp(slotIndex, 0, Mathf.Max(0, unlockedQuickSlots - 1));
            int previousIndex = _selectedSlotIndex;

            if (!forceRefresh && previousIndex == clampedIndex)
            {
                return;
            }

            _selectedSlotIndex = clampedIndex;
            SyncSelectedSlotToLocalContext();
            ApplySelectedSlotVisuals();

            if (forceRefresh || previousIndex != _selectedSlotIndex)
            {
                RefreshSlotVisualAtIndex(previousIndex);
            }

            RefreshSlotVisualAtIndex(_selectedSlotIndex);

            if (forceRefresh)
            {
                ClearHeldItemObject();
                ClearPreviewObject();
            }

            RefreshHeldItemObject();
            RefreshSelectedItemPreview();
        }

        private void RefreshSlotVisualAtIndex(int index)
        {
            if (index < 0 || index >= VisibleSlotCount || index >= GetUnlockedQuickSlotCount())
            {
                return;
            }

            InventoryItem slotItem = GetItemAtVisibleIndex(index);
            SetSlotVisual(index, slotItem);
        }

        private void ApplySelectedSlotVisuals()
        {
            int unlockedQuickSlots = GetUnlockedQuickSlotCount();
            int maxSlots = Mathf.Min(VisibleSlotCount, _slotBackgrounds.Length);
            for (int i = 0; i < maxSlots; i++)
            {
                Image slotBackground = _slotBackgrounds[i];
                if (slotBackground == null)
                {
                    continue;
                }

                if (i >= unlockedQuickSlots)
                {
                    slotBackground.color = _unselectedSlotColor;
                    continue;
                }

                slotBackground.color = i == _selectedSlotIndex ? _selectedSlotColor : _unselectedSlotColor;
            }
        }

        private int GetUnlockedQuickSlotCount()
        {
            GameManager gameManager = GameManager.Instance;
            int unlocked = gameManager != null ? gameManager.GetUnlockedQuickSlots() : VisibleSlotCount;
            return Mathf.Clamp(unlocked, 1, VisibleSlotCount);
        }

        private void RefreshHeldItemObject()
        {
            if (IsMinigameActive())
            {
                ClearHeldItemObject();
                return;
            }

            InventoryItem selectedItem = GetItemAtVisibleIndex(_selectedSlotIndex);
            if (selectedItem == _currentHeldItemItem && _currentHeldItemObject != null)
            {
                return;
            }

            ClearHeldItemObject();
            if (selectedItem == null)
            {
                return;
            }

            Transform anchor = EnsureHeldItemAnchor();
            if (anchor == null)
            {
                return;
            }

            GameObject spawned = null;
            GameObject visualPrefab = GetHeldVisualPrefab(selectedItem);
            if (visualPrefab != null)
            {
                spawned = Instantiate(visualPrefab, anchor, false);
                spawned.transform.localPosition = Vector3.zero;
                spawned.transform.localRotation = Quaternion.identity;
                PrepareVisualInstance(spawned);
                NormalizeHeldItemInstance(spawned.transform);

                if (!TryGetRendererBounds(spawned.transform, out _))
                {
                    Debug.LogWarning(
                        $"[InventoryGridUI] Held visual for item '{selectedItem.ItemId}' has no enabled renderer bounds. Visual may be invisible.",
                        this);
                }
            }
            else
            {
                Debug.LogWarning(
                    $"[InventoryGridUI] Selected item '{selectedItem.ItemId}' has no held/preview prefab. Held visual was not created.",
                    this);
            }

            if (visualPrefab != null && spawned == null)
            {
                Debug.LogWarning(
                    $"[InventoryGridUI] Held visual instantiation failed for item '{selectedItem.ItemId}'.",
                    this);
            }

            _currentHeldItemItem = selectedItem;
            _currentHeldItemObject = spawned;
        }

        private void RefreshSelectedItemPreview()
        {
            if (!_enableSelectedItemModelPreview)
            {
                ClearPreviewObject();
                DisablePreviewImage();
                return;
            }

            EnsurePreviewRawImage();
            EnsurePreviewRuntime();

            if (_selectedItemPreviewRawImage == null || _previewCamera == null || _previewRoot == null)
            {
                return;
            }

            if (IsMinigameActive())
            {
                ClearPreviewObject();
                DisablePreviewImage();
                return;
            }

            InventoryItem selectedItem = GetItemAtVisibleIndex(_selectedSlotIndex);
            if (selectedItem == null)
            {
                ClearPreviewObject();
                DisablePreviewImage();
                _currentPreviewItem = null;
                return;
            }

            GameObject visualPrefab = GetPreviewVisualPrefab(selectedItem);
            if (visualPrefab == null)
            {
                ClearPreviewObject();
                DisablePreviewImage();
                _currentPreviewItem = null;
                return;
            }

            if (selectedItem == _currentPreviewItem && _currentPreviewObject != null)
            {
                RenderPreviewModel();
                EnablePreviewImage();
                return;
            }

            ClearPreviewObject();

            _currentPreviewObject = Instantiate(visualPrefab, _previewRoot, false);
            _currentPreviewObject.name = $"Preview_{selectedItem.ItemName}";
            _currentPreviewObject.transform.localPosition = Vector3.zero;
            _currentPreviewObject.transform.localRotation = Quaternion.identity;
            _currentPreviewObject.transform.localScale = Vector3.one;

            PrepareVisualInstance(_currentPreviewObject);
            FramePreviewObject(_currentPreviewObject.transform);

            _currentPreviewItem = selectedItem;
            RenderPreviewModel();
            EnablePreviewImage();
        }

        private InventoryItem GetItemAtVisibleIndex(int visibleIndex)
        {
            if (visibleIndex < 0 || visibleIndex >= GetUnlockedQuickSlotCount())
            {
                return null;
            }

            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null)
            {
                return null;
            }

            (int width, int height) = inventorySystem.GetGridDimensions();
            int x = visibleIndex % width;
            int y = visibleIndex / width;

            if (y >= height)
            {
                return null;
            }

            InventorySlot slot = inventorySystem.GetSlot(x, y);
            return slot != null ? slot.GetItem() : null;
        }

        private void TryHandleDropInput()
        {
            if (!UnityEngine.Input.GetKeyDown(_dropSelectedItemKey))
            {
                return;
            }

            if (Time.time < _nextAllowedDropTime)
            {
                return;
            }

            if (!TryDropSelectedTrackedValuable())
            {
                return;
            }

            _nextAllowedDropTime = Time.time + Mathf.Max(0.05f, _dropCooldownSeconds);
        }

        private bool TryDropSelectedTrackedValuable()
        {
            if (!CanDropInCurrentState())
            {
                return false;
            }

            InventorySystem inventorySystem = InventorySystem.Instance;
            GameManager gameManager = GameManager.Instance;
            if (inventorySystem == null || gameManager == null)
            {
                return false;
            }

            string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();

            if (!TryResolveSelectedSlotGridCoordinates(out int gridX, out int gridY))
            {
                return false;
            }

            InventorySlot selectedSlot = inventorySystem.GetSlot(gridX, gridY);
            InventoryItem selectedItem = selectedSlot != null ? selectedSlot.GetItem() : null;
            if (selectedItem == null || !selectedItem.IsValid())
            {
                return false;
            }

            if (!gameManager.IsTrackedStolenLootItem(selectedItem.ItemId))
            {
                return false;
            }

            if (!TryResolveDropPrefab(selectedItem.ItemId, out GameObject dropPrefab))
            {
                Debug.LogWarning($"[InventoryGridUI] No drop prefab mapping found for tracked valuable '{selectedItem.ItemId}'.", this);
                return false;
            }

            if (!TryComputeDropSpawnPose(out Vector3 spawnPosition, out Quaternion spawnRotation))
            {
                return false;
            }

            if (IsNetworkSession())
            {
                if (_awaitingNetworkDropResponse)
                {
                    return false;
                }

                if (!NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority requester)
                    || requester == null
                    || !requester.IsSpawned)
                {
                    ClearPendingNetworkDropRequest();
                    Debug.LogWarning("[InventoryGridUI] Cannot request network drop: NetworkSessionProgressAuthority requester is unavailable.", this);
                    return false;
                }

                ulong requestId = ++_networkDropRequestSequence;
                _awaitingNetworkDropResponse = true;
                _pendingNetworkDropRequestId = requestId;
                _pendingNetworkDropItemId = selectedItem.ItemId;
                _pendingNetworkDropGridX = gridX;
                _pendingNetworkDropGridY = gridY;
                _pendingNetworkDropOwnerPlayerId = ownerPlayerId;

                try
                {
                    requester.RequestTrackedLootDrop(selectedItem.ItemId, spawnPosition, spawnRotation, requestId);
                }
                catch (Exception exception)
                {
                    ClearPendingNetworkDropRequest();
                    _pendingNetworkDropItemId = null;
                    Debug.LogWarning(
                        $"[InventoryGridUI] Failed to send tracked-loot drop request for '{selectedItem.ItemId}' requestId={requestId}: {exception.Message}",
                        this);
                    return false;
                }

                return true;
            }

            InventoryItem removedItem = inventorySystem.RemoveItemAt(gridX, gridY, ownerPlayerId);
            if (removedItem == null)
            {
                return false;
            }

            GameObject spawned = Instantiate(dropPrefab, spawnPosition, spawnRotation);
            if (spawned == null)
            {
                bool restored = RestoreRemovedItemAfterDropFailure(inventorySystem, removedItem, gridX, gridY, ownerPlayerId);
                Debug.LogWarning(
                    $"[InventoryGridUI] Failed to instantiate dropped item prefab for '{removedItem.ItemId}'. " +
                    (restored ? "Restored item to inventory." : "Rollback failed; item may be lost."),
                    this);
                return false;
            }

            InteractableItem droppedInteractable = spawned.GetComponent<InteractableItem>();
            if (droppedInteractable == null)
            {
                Destroy(spawned);
                bool restored = RestoreRemovedItemAfterDropFailure(inventorySystem, removedItem, gridX, gridY, ownerPlayerId);
                Debug.LogWarning(
                    $"[InventoryGridUI] Dropped prefab for '{removedItem.ItemId}' is missing InteractableItem on root. " +
                    (restored ? "Restored item to inventory." : "Rollback failed; item may be lost."),
                    this);
                return false;
            }

            droppedInteractable.DisablePersistenceForRuntimeDrop();
            gameManager.TryUnregisterStolenLootForDrop(removedItem.ItemId, ownerPlayerId, 1);
            RefreshAllSlots();
            return true;
        }

        private bool TryResolveSelectedSlotGridCoordinates(out int x, out int y)
        {
            x = 0;
            y = 0;

            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null)
            {
                return false;
            }

            int unlockedQuickSlots = GetUnlockedQuickSlotCount();
            int clampedIndex = Mathf.Clamp(_selectedSlotIndex, 0, Mathf.Max(0, unlockedQuickSlots - 1));

            (int width, int height) = inventorySystem.GetGridDimensions();
            x = clampedIndex % width;
            y = clampedIndex / width;

            return y < height;
        }

        private bool TryResolveDropPrefab(string itemId, out GameObject pickupPrefab)
        {
            pickupPrefab = null;
            if (string.IsNullOrWhiteSpace(itemId) || _trackedValuableDropPrefabs == null)
            {
                return false;
            }

            string normalizedItemId = itemId.Trim();
            for (int i = 0; i < _trackedValuableDropPrefabs.Length; i++)
            {
                DropPrefabEntry entry = _trackedValuableDropPrefabs[i];
                if (entry == null || entry.pickupPrefab == null || string.IsNullOrWhiteSpace(entry.itemId))
                {
                    continue;
                }

                if (!string.Equals(entry.itemId.Trim(), normalizedItemId, StringComparison.Ordinal))
                {
                    continue;
                }

                pickupPrefab = entry.pickupPrefab;
                return true;
            }

            return false;
        }

        private bool TryComputeDropSpawnPose(out Vector3 position, out Quaternion rotation)
        {
            Transform cameraTransform = GetLocalOwnedCameraTransform();

            if (cameraTransform == null)
            {
                position = transform.position + transform.forward;
                rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
                return true;
            }

            Vector3 forward = cameraTransform.forward;
            if (forward.sqrMagnitude <= 0.0001f)
            {
                forward = transform.forward;
            }

            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.0001f)
            {
                forward = transform.forward;
                forward.y = 0f;
            }

            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            Vector3 candidate = cameraTransform.position
                + (forward * Mathf.Max(0.1f, _dropForwardDistance))
                + (Vector3.up * Mathf.Max(0f, _dropUpwardOffset));

            Vector3 rayOrigin = candidate + Vector3.up;
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, Mathf.Max(0.5f, _dropRaycastDistance), ~0, QueryTriggerInteraction.Ignore))
            {
                position = hit.point + (hit.normal * Mathf.Max(0f, _dropSurfaceNormalOffset));
            }
            else
            {
                position = candidate;
            }

            rotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
            return true;
        }

        private bool CanDropInCurrentState()
        {
            if (IsMinigameActive())
            {
                return false;
            }

            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null && pauseManager.IsPaused)
            {
                return false;
            }

            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager != null && minigameManager.IsMinigameActiveForOwner(LocalPlayerId))
            {
                return false;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return false;
            }

            return gameManager.CurrentState == GameState.FreePlay
                && gameManager.GetCurrentRunPhase() == GameManager.RunPhase.Work;
        }

        private static bool RestoreRemovedItemAfterDropFailure(
            InventorySystem inventorySystem,
            InventoryItem removedItem,
            int gridX,
            int gridY,
            string ownerPlayerId)
        {
            if (inventorySystem == null || removedItem == null)
            {
                return false;
            }

            if (inventorySystem.AddItemAt(removedItem, gridX, gridY, ownerPlayerId))
            {
                return true;
            }

            return inventorySystem.AddItem(removedItem, ownerPlayerId);
        }

        private static bool IsMinigameActive()
        {
            if (PlayerContextLocator.TryGetLocalPresentationMode(out LocalPlayerPresentationMode mode)
                && mode == LocalPlayerPresentationMode.Minigame)
            {
                return true;
            }

            GameManager gameManager = GameManager.Instance;
            return gameManager != null && gameManager.CurrentState == GameState.Minigame;
        }

        private static GameObject GetHeldVisualPrefab(InventoryItem item)
        {
            if (item == null)
            {
                return null;
            }

            if (item.HeldPrefab != null)
            {
                return item.HeldPrefab;
            }

            return item.PreviewPrefab;
        }

        private static GameObject GetPreviewVisualPrefab(InventoryItem item)
        {
            if (item == null)
            {
                return null;
            }

            if (item.PreviewPrefab != null)
            {
                return item.PreviewPrefab;
            }

            return item.HeldPrefab;
        }

        private Transform EnsureHeldItemAnchor()
        {
            if (IsHeldAnchorValidForLocalContext(_heldItemAnchor))
            {
                return _heldItemAnchor;
            }

            if (TryRebindHeldItemAnchorFromScene(forceRebindForNetworkSession: true))
            {
                return _heldItemAnchor;
            }

            if (!_hasLoggedMissingHeldItemAnchor && !_hasLoggedMissingHeldItemAnchorSession)
            {
                _hasLoggedMissingHeldItemAnchor = true;
                _hasLoggedMissingHeldItemAnchorSession = true;
                string failureReason = string.IsNullOrEmpty(_lastHeldItemAnchorBindFailureReason)
                    ? "No runtime anchor candidate was resolved."
                    : _lastHeldItemAnchorBindFailureReason;
                Debug.LogError(
                    $"[InventoryGridUI] Missing Held Item Anchor reference. Assign InventoryGridUI._heldItemAnchor to Player/PlayerBody/Camera/HeldItemAnchor. Runtime rebind failed: {failureReason}",
                    this);
            }

            return null;
        }

        private bool TryRebindHeldItemAnchorFromScene(bool forceRebindForNetworkSession = false)
        {
            if (!forceRebindForNetworkSession && IsHeldAnchorValidForLocalContext(_heldItemAnchor))
            {
                _lastHeldItemAnchorBindFailureReason = string.Empty;
                return true;
            }

            _heldItemAnchor = null;

            PlayerContextLocator.TryGetLocalFirstPersonCamera(out FirstPersonCamera firstPersonCamera);
            if (firstPersonCamera == null && PlayerContextLocator.IsCompatibilityFallbackAllowed())
            {
                ValidateFirstPersonCameraFallbackAmbiguity("held_anchor_rebind");
                PlayerContextLocator.TryGetFirstPersonCamera(out firstPersonCamera);
                if (firstPersonCamera != null && !_hasLoggedCompatibilityCameraFallback)
                {
                    _hasLoggedCompatibilityCameraFallback = true;
                    Debug.LogWarning("[InventoryGridUI] Using compatibility fallback to resolve FirstPersonCamera for held-item anchor.", this);
                }
            }

            if (firstPersonCamera == null)
            {
                _lastHeldItemAnchorBindFailureReason = "No active FirstPersonCamera component was found.";
                return false;
            }

            Transform cameraTransform = firstPersonCamera.transform;
            if (cameraTransform == null)
            {
                _lastHeldItemAnchorBindFailureReason = "Resolved FirstPersonCamera had no valid transform.";
                return false;
            }

            HierarchyLookup.TryFindChild(
                cameraTransform,
                HeldItemAnchorChildName,
                out Transform reboundAnchor,
                this,
                nameof(_heldItemAnchor));
            if (reboundAnchor == null)
            {
                _lastHeldItemAnchorBindFailureReason =
                    $"FirstPersonCamera '{cameraTransform.name}' has no '{HeldItemAnchorChildName}' child.";
                return false;
            }

            _heldItemAnchor = reboundAnchor;
            _hasLoggedMissingHeldItemAnchor = false;
            _lastHeldItemAnchorBindFailureReason = string.Empty;
            return true;
        }

        private bool IsHeldAnchorValidForLocalContext(Transform anchor)
        {
            if (anchor == null || anchor.parent == null)
            {
                return false;
            }

            Transform localCamera = GetLocalOwnedCameraTransform();
            if (localCamera == null)
            {
                return false;
            }

            return ReferenceEquals(anchor.parent, localCamera)
                   && string.Equals(anchor.name, HeldItemAnchorChildName, StringComparison.Ordinal);
        }

        private Transform GetLocalOwnedCameraTransform()
        {
            PlayerContextLocator.TryGetLocalFirstPersonCamera(out FirstPersonCamera firstPersonCamera);
            if (firstPersonCamera == null && PlayerContextLocator.IsCompatibilityFallbackAllowed())
            {
                ValidateFirstPersonCameraFallbackAmbiguity("owned_camera_transform");
                PlayerContextLocator.TryGetFirstPersonCamera(out firstPersonCamera);
                if (firstPersonCamera != null && !_hasLoggedCompatibilityCameraFallback)
                {
                    _hasLoggedCompatibilityCameraFallback = true;
                    Debug.LogWarning("[InventoryGridUI] Using compatibility fallback to resolve local camera transform.", this);
                }
            }

            return firstPersonCamera != null ? firstPersonCamera.transform : null;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void ValidateFirstPersonCameraFallbackAmbiguity(string source)
        {
            FirstPersonCamera[] candidates = FindObjectsByType<FirstPersonCamera>(FindObjectsInactive.Include);
            int count = candidates != null ? candidates.Length : 0;
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string key = $"INV_FP_CAMERA_AMBIGUITY|{sceneName}|{source}|{count}";
            if (!LoggedFallbackValidationWarnings.Add(key))
            {
                return;
            }

            if (count == 0 || count > 1)
            {
                Debug.LogWarning(
                    $"[FallbackValidation][INV_FP_CAMERA_AMBIGUITY] scene='{sceneName}' source='{source}' candidates='{count}' risk='wrong_camera_bind'",
                    this);
            }
        }

        private static bool IsNetworkSession()
        {
            Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
            return manager != null && manager.IsListening;
        }

        private void HandleTrackedLootDropResponse(NetworkSessionProgressAuthority.TrackedLootDropResponse response)
        {
            if (!_awaitingNetworkDropResponse)
            {
                Debug.LogWarning(
                    $"[InventoryGridUI] Ignored tracked-loot drop response requestId={response.requestId} itemId='{response.itemId}' because no pending request is active.",
                    this);
                return;
            }

            if (response.requestId != _pendingNetworkDropRequestId)
            {
                Debug.LogWarning(
                    $"[InventoryGridUI] Ignored tracked-loot drop response requestId={response.requestId} itemId='{response.itemId}'. Pending requestId={_pendingNetworkDropRequestId}.",
                    this);
                return;
            }

            if (!response.success)
            {
                Debug.LogWarning(
                    $"[InventoryGridUI] Network drop failed for '{response.itemId}' requestId={response.requestId}: {response.reason}",
                    this);
                ClearPendingNetworkDropRequest();
                return;
            }

            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null)
            {
                ClearPendingNetworkDropRequest();
                return;
            }

            InventoryItem removedItem = inventorySystem.RemoveItemAt(
                _pendingNetworkDropGridX,
                _pendingNetworkDropGridY,
                _pendingNetworkDropOwnerPlayerId);

            if (removedItem == null || !string.Equals(removedItem.ItemId, response.itemId, StringComparison.Ordinal))
            {
                Debug.LogWarning(
                    $"[InventoryGridUI] Slot-first removal miss after successful network drop. " +
                    $"requestId={response.requestId}, itemId='{response.itemId}', removedItem='{removedItem?.ItemId ?? "<null>"}', " +
                    $"slot=({_pendingNetworkDropGridX},{_pendingNetworkDropGridY}), owner='{_pendingNetworkDropOwnerPlayerId}'.",
                    this);

                int removedCount = inventorySystem.RemoveItemsByItemId(response.itemId, 1, _pendingNetworkDropOwnerPlayerId);
                if (removedCount <= 0)
                {
                    Debug.LogWarning(
                        $"[InventoryGridUI] ItemId fallback removal miss after successful network drop. " +
                        $"itemId='{response.itemId}', requestId={response.requestId}, " +
                        $"slot=({_pendingNetworkDropGridX},{_pendingNetworkDropGridY}), owner='{_pendingNetworkDropOwnerPlayerId}', " +
                        $"localOwner='{PlayerInventoryAuthority.GetLocalOwnerPlayerId()}'. Requesting snapshot refresh.",
                        this);

                    if (NetworkSessionProgressAuthority.TryGetLocalRequester(out NetworkSessionProgressAuthority requester)
                        && requester != null
                        && requester.IsSpawned)
                    {
                        requester.RequestRefreshStolenLootSnapshot();
                    }
                }
            }

            ReconcileTrackedValuableInventoryCount(response.itemId, _pendingNetworkDropOwnerPlayerId, response.requestId);
            RefreshAllSlots();
            ClearPendingNetworkDropRequest();
        }

        private void ReconcileTrackedValuableInventoryCount(string itemId, string ownerPlayerId, ulong requestId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            InventorySystem inventorySystem = InventorySystem.Instance;
            if (gameManager == null || inventorySystem == null)
            {
                return;
            }

            if (!gameManager.IsTrackedStolenLootItem(itemId))
            {
                return;
            }

            int localCount = CountLocalInventoryItemsById(inventorySystem, itemId);
            int authoritativeCount = CountAuthoritativeTrackedLootById(gameManager, ownerPlayerId, itemId);
            int excess = localCount - authoritativeCount;
            if (excess <= 0)
            {
                return;
            }

            int removed = inventorySystem.RemoveItemsByItemId(itemId, excess, ownerPlayerId);
            Debug.LogWarning(
                $"[InventoryGridUI] Reconciled tracked-valuable inventory after network drop. " +
                $"itemId='{itemId}', requestId={requestId}, owner='{ownerPlayerId}', " +
                $"localBefore={localCount}, authoritative={authoritativeCount}, removed={removed}.",
                this);
        }

        private static int CountLocalInventoryItemsById(InventorySystem inventorySystem, string itemId)
        {
            if (inventorySystem == null || string.IsNullOrWhiteSpace(itemId))
            {
                return 0;
            }

            System.Collections.Generic.List<InventoryItem> localItems = inventorySystem.GetAllItems();
            if (localItems == null || localItems.Count == 0)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < localItems.Count; i++)
            {
                InventoryItem inventoryItem = localItems[i];
                if (inventoryItem == null || !inventoryItem.IsValid())
                {
                    continue;
                }

                if (string.Equals(inventoryItem.ItemId, itemId, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountAuthoritativeTrackedLootById(GameManager gameManager, string ownerPlayerId, string itemId)
        {
            if (gameManager == null || string.IsNullOrWhiteSpace(itemId))
            {
                return 0;
            }

            System.Collections.Generic.List<StolenLootEntryData> snapshot = gameManager.GetStolenLootThisDaySnapshot(ownerPlayerId);
            if (snapshot == null || snapshot.Count == 0)
            {
                return 0;
            }

            int total = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                StolenLootEntryData entry = snapshot[i];
                if (entry == null || !string.Equals(entry.itemId, itemId, StringComparison.Ordinal))
                {
                    continue;
                }

                total += Mathf.Max(0, entry.count);
            }

            return total;
        }

        private void ClearPendingNetworkDropRequest()
        {
            _awaitingNetworkDropResponse = false;
            _pendingNetworkDropRequestId = 0UL;
            _pendingNetworkDropItemId = string.Empty;
            _pendingNetworkDropGridX = -1;
            _pendingNetworkDropGridY = -1;
            _pendingNetworkDropOwnerPlayerId = string.Empty;
        }

        private void RegisterLocalContext()
        {
            PlayerContextRegistry.RegisterOrUpdate(this, LocalPlayerId);
            if (PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext) && localContext != null)
            {
                int contextSelectedSlot = Mathf.Max(0, localContext.SelectedQuickSlotIndex);
                if (contextSelectedSlot != _selectedSlotIndex)
                {
                    _selectedSlotIndex = contextSelectedSlot;
                }
            }

            SyncSelectedSlotToLocalContext();
        }

        private void EnsureSelectedSlotIsVisible(int unlockedQuickSlots)
        {
            int clamped = Mathf.Clamp(_selectedSlotIndex, 0, Mathf.Max(0, unlockedQuickSlots - 1));
            if (_selectedSlotIndex != clamped)
            {
                _selectedSlotIndex = clamped;
            }

            SyncSelectedSlotToLocalContext();
        }

        private void SyncSelectedSlotToLocalContext()
        {
            if (PlayerContextLocator.TryGetLocalContext(out PlayerContext localContext) && localContext != null)
            {
                localContext.SetSelectedQuickSlotIndex(_selectedSlotIndex);
            }
        }

        private void ApplySlotVisibility(int unlockedQuickSlots)
        {
            int maxSlots = Mathf.Max(_slotIcons.Length, _slotBackgrounds.Length);
            for (int i = 0; i < maxSlots; i++)
            {
                GameObject slotRoot = GetSlotRoot(i);
                if (slotRoot == null)
                {
                    continue;
                }

                bool shouldBeActive = i < unlockedQuickSlots;
                if (slotRoot.activeSelf != shouldBeActive)
                {
                    slotRoot.SetActive(shouldBeActive);
                }
            }
        }

        private GameObject GetSlotRoot(int index)
        {
            if (index < 0 || index >= VisibleSlotCount)
            {
                return null;
            }

            if (index < _slotBackgrounds.Length && _slotBackgrounds[index] != null)
            {
                return _slotBackgrounds[index].gameObject;
            }

            if (index < _slotIcons.Length && _slotIcons[index] != null)
            {
                Transform iconTransform = _slotIcons[index].transform;
                if (iconTransform != null)
                {
                    return iconTransform.parent != null ? iconTransform.parent.gameObject : iconTransform.gameObject;
                }
            }

            return null;
        }

        private void EnsureSlotGridLayoutBinding()
        {
            if (_slotGridLayoutGroup == null)
            {
                _slotGridLayoutGroup = GetComponentInChildren<HorizontalLayoutGroup>(includeInactive: true);
            }
        }

        private void EnsureSlotGridCentered()
        {
            if (_slotGridLayoutGroup == null)
            {
                return;
            }

            _slotGridLayoutGroup.childAlignment = TextAnchor.MiddleCenter;
        }

        private void EnsurePreviewRawImage()
        {
            if (!_enableSelectedItemModelPreview)
            {
                return;
            }

            if (_selectedItemPreviewRawImage != null)
            {
                _selectedItemPreviewRawImage.raycastTarget = false;
                return;
            }

            if (!_autoCreatePreviewRawImage)
            {
                return;
            }

            RectTransform parentRect = transform as RectTransform;
            if (parentRect == null)
            {
                return;
            }

            Transform existing = parentRect.Find(PreviewRawImageName);
            if (existing != null)
            {
                _selectedItemPreviewRawImage = existing.GetComponent<RawImage>();
                if (_selectedItemPreviewRawImage != null)
                {
                    _selectedItemPreviewRawImage.raycastTarget = false;
                }

                return;
            }

            GameObject previewObject = new GameObject(PreviewRawImageName, typeof(RectTransform), typeof(RawImage));
            previewObject.transform.SetParent(parentRect, false);

            RectTransform previewRect = previewObject.GetComponent<RectTransform>();
            previewRect.anchorMin = new Vector2(0.5f, 0f);
            previewRect.anchorMax = new Vector2(0.5f, 0f);
            previewRect.pivot = new Vector2(0.5f, 0f);
            previewRect.sizeDelta = _previewSize;
            previewRect.anchoredPosition = _previewAnchoredPosition;

            _selectedItemPreviewRawImage = previewObject.GetComponent<RawImage>();
            _selectedItemPreviewRawImage.raycastTarget = false;
            _selectedItemPreviewRawImage.color = Color.white;
            _selectedItemPreviewRawImage.enabled = false;
        }

        private void EnsurePreviewRuntime()
        {
            if (!_enableSelectedItemModelPreview)
            {
                return;
            }

            if (_previewRoot == null)
            {
                GameObject previewRootObject = new GameObject(PreviewRootName);
                _previewRoot = previewRootObject.transform;
                _previewRoot.position = _previewWorldOrigin;
                _previewRoot.rotation = Quaternion.identity;
            }

            if (_previewRenderTexture == null)
            {
                int textureSize = Mathf.Max(128, _previewTextureSize);
                _previewRenderTexture = new RenderTexture(textureSize, textureSize, 16, RenderTextureFormat.ARGB32)
                {
                    name = "InventoryItemPreviewRT"
                };
                _previewRenderTexture.Create();
            }

            if (_previewCamera == null)
            {
                GameObject previewCameraObject = new GameObject(PreviewCameraName, typeof(Camera));
                _previewCamera = previewCameraObject.GetComponent<Camera>();
                _previewCamera.clearFlags = CameraClearFlags.SolidColor;
                _previewCamera.backgroundColor = _previewBackgroundColor;
                _previewCamera.fieldOfView = 28f;
                _previewCamera.nearClipPlane = 0.01f;
                _previewCamera.farClipPlane = 200f;
                _previewCamera.allowMSAA = false;
                _previewCamera.allowHDR = false;
                _previewCamera.enabled = false;
            }

            _previewCamera.targetTexture = _previewRenderTexture;

            if (_selectedItemPreviewRawImage != null)
            {
                _selectedItemPreviewRawImage.texture = _previewRenderTexture;
            }
        }

        private static void PrepareVisualInstance(GameObject instance)
        {
            if (instance == null)
            {
                return;
            }

            Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
            }

            Rigidbody[] rigidbodies = instance.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < rigidbodies.Length; i++)
            {
                rigidbodies[i].isKinematic = true;
                rigidbodies[i].detectCollisions = false;
            }

            MonoBehaviour[] behaviours = instance.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour != null)
                {
                    behaviour.enabled = false;
                }
            }
        }

        private void NormalizeHeldItemInstance(Transform instanceTransform)
        {
            if (instanceTransform == null)
            {
                return;
            }

            if (!TryGetRendererBounds(instanceTransform, out Bounds bounds))
            {
                return;
            }

            float largestDimension = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (largestDimension > 0.0001f)
            {
                float targetSize = Mathf.Max(0.01f, _heldModelTargetSize);
                float scaleMultiplier = targetSize / largestDimension;
                instanceTransform.localScale *= scaleMultiplier;
            }

            if (TryGetRendererBounds(instanceTransform, out Bounds recenteredBounds))
            {
                Vector3 localCenter = instanceTransform.InverseTransformPoint(recenteredBounds.center);
                instanceTransform.localPosition -= localCenter;
            }
        }

        private void FramePreviewObject(Transform instanceTransform)
        {
            if (instanceTransform == null || _previewCamera == null || _previewRoot == null)
            {
                return;
            }

            if (!TryGetRendererBounds(instanceTransform, out Bounds bounds))
            {
                instanceTransform.position = _previewRoot.position;
                _previewCamera.transform.position = _previewRoot.position + new Vector3(0f, 0f, -1.2f);
                _previewCamera.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                return;
            }

            Vector3 localCenter = instanceTransform.InverseTransformPoint(bounds.center);
            instanceTransform.localPosition -= localCenter;

            if (!TryGetRendererBounds(instanceTransform, out bounds))
            {
                return;
            }

            float radius = Mathf.Max(0.03f, bounds.extents.magnitude * Mathf.Max(1f, _previewFramingPadding));
            Quaternion cameraRotation = Quaternion.Euler(_previewCameraEulerAngles);
            Vector3 viewDirection = cameraRotation * Vector3.forward;

            float halfFovRad = _previewCamera.fieldOfView * Mathf.Deg2Rad * 0.5f;
            float distance = radius / Mathf.Max(0.1f, Mathf.Sin(halfFovRad));

            Vector3 lookTarget = _previewRoot.position;
            _previewCamera.transform.SetPositionAndRotation(lookTarget - (viewDirection * distance), cameraRotation);
        }

        private void RenderPreviewModel()
        {
            if (_previewCamera == null)
            {
                return;
            }

            _previewCamera.Render();
        }

        private static bool TryGetRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            if (root == null)
            {
                return false;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return hasBounds;
        }

        private void EnablePreviewImage()
        {
            if (_selectedItemPreviewRawImage != null)
            {
                _selectedItemPreviewRawImage.enabled = true;
            }
        }

        private void DisablePreviewImage()
        {
            if (_selectedItemPreviewRawImage != null)
            {
                _selectedItemPreviewRawImage.enabled = false;
            }
        }

        private void ClearPreviewObject()
        {
            if (_currentPreviewObject != null)
            {
                Destroy(_currentPreviewObject);
                _currentPreviewObject = null;
            }

            _currentPreviewItem = null;
        }

        private void ClearHeldItemObject()
        {
            if (_currentHeldItemObject != null)
            {
                Destroy(_currentHeldItemObject);
                _currentHeldItemObject = null;
            }

            _currentHeldItemItem = null;
        }
    }
}
