using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Game.Inventory;
using Game.Core;
using Game.Input;
using Game.Player;

namespace Game.UI
{
    /// <summary>
    /// Mirrors the first 9 inventory slots into a fixed HUD bar.
    /// Intended for a 1x9 middle-bottom gameplay display.
    /// </summary>
    public class InventoryGridUI : MonoBehaviour
    {
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
        private bool _hasLoggedMissingHeldItemAnchor;
        private static bool _hasLoggedMissingHeldItemAnchorSession;
        private string _lastHeldItemAnchorBindFailureReason = string.Empty;

        private void Awake()
        {
            EnsureSlotImageSafety();
            EnsureSlotGridLayoutBinding();
            EnsureSlotGridCentered();
            _selectedSlotIndex = 0;
            EnsureHeldItemAnchor();

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
            TrySubscribe();
            TrySubscribeInput();
            TryRefreshHeldItemAnchorBinding();
            StartCoroutine(DelayedInitialRefresh());
        }

        private void OnDisable()
        {
            Unsubscribe();
            UnsubscribeInput();
            ClearHeldItemObject();
            ClearPreviewObject();
            DisablePreviewImage();
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
            if (!TryRebindHeldItemAnchorFromScene())
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

        private static bool IsMinigameActive()
        {
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
            if (_heldItemAnchor != null)
            {
                return _heldItemAnchor;
            }

            if (TryRebindHeldItemAnchorFromScene())
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

        private bool TryRebindHeldItemAnchorFromScene()
        {
            if (_heldItemAnchor != null)
            {
                _lastHeldItemAnchorBindFailureReason = string.Empty;
                return true;
            }

            FirstPersonCamera firstPersonCamera = Object.FindAnyObjectByType<FirstPersonCamera>();
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

            Transform reboundAnchor = cameraTransform.Find(HeldItemAnchorChildName);
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

        private void EnsureSelectedSlotIsVisible(int unlockedQuickSlots)
        {
            int clamped = Mathf.Clamp(_selectedSlotIndex, 0, Mathf.Max(0, unlockedQuickSlots - 1));
            if (_selectedSlotIndex != clamped)
            {
                _selectedSlotIndex = clamped;
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
