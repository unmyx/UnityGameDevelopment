using UnityEngine;
using UnityEngine.UI;
using Game.Core;
using Game.Core.Events;
using Game.Interaction;
using Game.Minigames;
using TMPro;

namespace Game.UI
{
    /// <summary>
    /// Displays currency, objectives, and transient feedback during free play.
    /// The HUD stays visible during free play, hides during minigames, and does not block minigame input.
    /// </summary>
    public class GameplayHUD : MonoBehaviour
    {
        [Header("Currency Display")]
        [SerializeField]
        [Tooltip("Text component showing currency amount")]
        private TextMeshProUGUI _currencyText;

        [SerializeField]
        [Tooltip("Optional image for currency icon")]
        private Image _currencyIcon;

        [Header("Objectives Display")]
        [SerializeField]
        [Tooltip("Text component showing objective progress list")]
        private TextMeshProUGUI _objectivesText;

        [Header("Run Progress Display")]
        [SerializeField]
        [Tooltip("Optional text component showing current day.")]
        private TextMeshProUGUI _dayText;

        [SerializeField]
        [Tooltip("Optional text component showing top-level run phase.")]
        private TextMeshProUGUI _runPhaseText;

        [Header("HUD Container")]
        [SerializeField]
        [Tooltip("Root panel/group containing all HUD elements")]
        private GameObject _hudContainer;

        [Header("TMP Font")]
        [SerializeField]
        [Tooltip("Optional TMP font asset override. Falls back to TMP project settings.")]
        private TMP_FontAsset _hudFontAsset;

        [Header("Feedback Display")]
        [SerializeField]
        [Tooltip("Text used for transient item/upgrade feedback.")]
        private TextMeshProUGUI _feedbackText;

        [SerializeField]
        [Tooltip("Seconds to keep pickup/upgrade feedback visible.")]
        private float _feedbackDuration = 2f;

        [Header("Crosshair")]
        [SerializeField]
        [Tooltip("Optional crosshair text element. If missing, one is created at runtime.")]
        private TextMeshProUGUI _crosshairText;

        [SerializeField]
        [Tooltip("Crosshair glyph shown at screen center.")]
        private string _crosshairGlyph = "+";

        [SerializeField]
        [Tooltip("Crosshair font size.")]
        private float _crosshairSize = 26f;

        [Header("Interaction Prompt")]
        [SerializeField]
        [Tooltip("Optional generic interaction prompt text shown near crosshair when the current target can be interacted with.")]
        private TextMeshProUGUI _interactionPromptText;

        [SerializeField]
        [Tooltip("Scene InteractionSystem used as source of truth for prompt visibility.")]
        private InteractionSystem _interactionSystem;

        [Header("Minigame References")]
        [SerializeField]
        [Tooltip("Optional direct reference to CleaningCanvas.")]
        private Canvas _cleaningCanvas;

        [SerializeField]
        [Tooltip("Optional direct reference to WeldingCanvas.")]
        private Canvas _weldingCanvas;

        private Canvas _canvas;
        private CanvasGroup _canvasGroup;
        private CanvasGroup _hudContainerCanvasGroup;
        private int _currentCurrency = -1;
        private int _currentDayWorkEarnings = -1;
        private int _currentDay = -1;
        private int _currentRunPhase = -1;
        private string _lastObjectivesDisplay = string.Empty;
        private float _feedbackTimeRemaining;
        private string _lastFeedbackMessage = string.Empty;
        private bool _hasLoggedMissingPromptText;
        private bool _hasLoggedMissingInteractionSystem;

        private void Awake()
        {
            _canvas = GetComponent<Canvas>();
            _canvasGroup = GetComponent<CanvasGroup>();

            if (!ValidateRequiredReferences())
            {
                enabled = false;
                return;
            }

            if (_canvas != null)
            {
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.overrideSorting = true;
                _canvas.sortingOrder = 100;
            }

            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
            _hudContainerCanvasGroup.interactable = false;
            _hudContainerCanvasGroup.blocksRaycasts = false;
            ConfigureCrosshairText(_crosshairText);

            ApplyTextFont(_currencyText);
            ApplyTextFont(_objectivesText);
            ApplyTextFont(_dayText);
            ApplyTextFont(_runPhaseText);
            ApplyTextFont(_feedbackText);
            ApplyTextFont(_crosshairText);
            ApplyTextFont(_interactionPromptText);

            DisableRaycastTarget(_currencyText);
            DisableRaycastTarget(_objectivesText);
            DisableRaycastTarget(_dayText);
            DisableRaycastTarget(_runPhaseText);
            DisableRaycastTarget(_feedbackText);
            DisableRaycastTarget(_crosshairText);
            DisableRaycastTarget(_interactionPromptText);

            if (_currencyText != null)
            {
                _currencyText.textWrappingMode = TextWrappingModes.NoWrap;
            }

            ValidatePromptReferences();

            if (_currencyIcon != null)
            {
                _currencyIcon.raycastTarget = false;
            }
        }

        private void OnEnable()
        {
            EventBus.Subscribe<CurrencyChangedEvent>(OnCurrencyChanged);
            EventBus.Subscribe<DayWorkEarningsChangedEvent>(OnDayWorkEarningsChanged);
            EventBus.Subscribe<ObjectiveProgressEvent>(OnObjectiveProgress);
            EventBus.Subscribe<ObjectiveCompletedEvent>(OnObjectiveCompleted);
            EventBus.Subscribe<ItemPickedUpEvent>(OnItemPickedUp);
            EventBus.Subscribe<ItemPickupFailedEvent>(OnItemPickupFailed);
            EventBus.Subscribe<AllObjectivesCompletedEvent>(OnAllObjectivesCompleted);
            EventBus.Subscribe<PlayerFeedbackEvent>(OnPlayerFeedback);

            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager != null)
            {
                pauseManager.OnPaused += OnGamePaused;
                pauseManager.OnResumed += OnGameResumed;
            }

            UpdateHUDDisplay();
            UpdateVisibility();
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<CurrencyChangedEvent>(OnCurrencyChanged);
            EventBus.Unsubscribe<DayWorkEarningsChangedEvent>(OnDayWorkEarningsChanged);
            EventBus.Unsubscribe<ObjectiveProgressEvent>(OnObjectiveProgress);
            EventBus.Unsubscribe<ObjectiveCompletedEvent>(OnObjectiveCompleted);
            EventBus.Unsubscribe<ItemPickedUpEvent>(OnItemPickedUp);
            EventBus.Unsubscribe<ItemPickupFailedEvent>(OnItemPickupFailed);
            EventBus.Unsubscribe<AllObjectivesCompletedEvent>(OnAllObjectivesCompleted);
            EventBus.Unsubscribe<PlayerFeedbackEvent>(OnPlayerFeedback);

            PauseManager pauseManager = PauseManager.HasInstance ? PauseManager.Instance : null;
            if (pauseManager != null)
            {
                pauseManager.OnPaused -= OnGamePaused;
                pauseManager.OnResumed -= OnGameResumed;
            }
        }

        private void Update()
        {
            UpdateVisibility();
            UpdateRunPhaseDisplay();
            TickFeedbackDisplay();
        }

        private void UpdateVisibility()
        {
            bool shouldBeVisible = !IsMinigamePresentationActive();

            bool shouldShowCrosshair = shouldBeVisible && IsFreeGameplayUnpaused();
            if (_crosshairText != null)
            {
                _crosshairText.gameObject.SetActive(shouldShowCrosshair);
            }

            UpdateInteractionPromptVisibility(shouldShowCrosshair);

            if (_hudContainerCanvasGroup != null)
            {
                _hudContainerCanvasGroup.alpha = shouldBeVisible ? 1f : 0f;
                _hudContainerCanvasGroup.interactable = false;
                _hudContainerCanvasGroup.blocksRaycasts = false;
            }
        }

        private bool IsMinigamePresentationActive()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null && gameManager.CurrentState == GameState.Minigame)
            {
                return true;
            }

            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager != null && minigameManager.IsMinigameActive())
            {
                return true;
            }

            if (_cleaningCanvas == null)
            {
                _cleaningCanvas = minigameManager != null ? minigameManager.GetCleaningCanvas() : null;
            }

            if (_weldingCanvas == null)
            {
                _weldingCanvas = minigameManager != null ? minigameManager.GetWeldingCanvas() : null;
            }

            if ((_cleaningCanvas != null && _cleaningCanvas.enabled) || (_weldingCanvas != null && _weldingCanvas.enabled))
            {
                return true;
            }

            return false;
        }

        private void OnCurrencyChanged(CurrencyChangedEvent eventData)
        {
            _currentCurrency = eventData.Amount;
            UpdateCurrencyDisplay(force: true);
        }

        private void OnDayWorkEarningsChanged(DayWorkEarningsChangedEvent eventData)
        {
            _currentDayWorkEarnings = eventData.Amount;
            UpdateCurrencyDisplay(force: true);
        }

        private void OnObjectiveProgress(ObjectiveProgressEvent eventData)
        {
            UpdateObjectivesDisplay();
        }

        private void OnObjectiveCompleted(ObjectiveCompletedEvent eventData)
        {
            UpdateObjectivesDisplay();
        }

        private void OnAllObjectivesCompleted(AllObjectivesCompletedEvent eventData)
        {
            UpdateObjectivesDisplay();
        }

        private void OnItemPickedUp(ItemPickedUpEvent eventData)
        {
            ShowFeedback($"Picked up: {eventData.ItemName}");
        }

        private void OnItemPickupFailed(ItemPickupFailedEvent eventData)
        {
            ShowFeedback($"Cannot pick up: {eventData.ItemName}");
        }

        private void OnPlayerFeedback(PlayerFeedbackEvent eventData)
        {
            if (string.IsNullOrWhiteSpace(eventData.Message))
            {
                return;
            }

            ShowFeedback(eventData.Message.Trim());
        }

        private void OnGamePaused()
        {
            UpdateVisibility();
        }

        private void OnGameResumed()
        {
            UpdateVisibility();
        }

        private void UpdateHUDDisplay()
        {
            UpdateCurrencyDisplay();
            UpdateObjectivesDisplay();
            UpdateRunPhaseDisplay(force: true);
        }

        private void UpdateCurrencyDisplay(bool force = false)
        {
            if (_currencyText == null && _currencyIcon == null)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            int currentCurrency = gameManager.GetCurrency();
            int currentDayWorkEarnings = gameManager.GetDayWorkEarnings();
            if (!force && _currentCurrency == currentCurrency && _currentDayWorkEarnings == currentDayWorkEarnings)
            {
                return;
            }

            _currentCurrency = currentCurrency;
            _currentDayWorkEarnings = currentDayWorkEarnings;

            if (_currencyText != null)
            {
                _currencyText.text = $"Wallet: {GetCurrencyPrefix()}{currentCurrency} | Today: {GetCurrencyPrefix()}{_currentDayWorkEarnings}";
            }
        }

        private void UpdateRunPhaseDisplay(bool force = false)
        {
            if (_dayText == null && _runPhaseText == null)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            int currentDay = gameManager.GetCurrentDay();
            GameManager.RunPhase currentRunPhase = gameManager.GetCurrentRunPhase();
            int currentRunPhaseValue = (int)currentRunPhase;

            if (!force && _currentDay == currentDay && _currentRunPhase == currentRunPhaseValue)
            {
                return;
            }

            _currentDay = currentDay;
            _currentRunPhase = currentRunPhaseValue;

            if (_dayText != null)
            {
                _dayText.text = $"Day: {currentDay}";
            }

            if (_runPhaseText != null)
            {
                _runPhaseText.text = $"Phase: {currentRunPhase}";
            }
        }

        private void UpdateObjectivesDisplay()
        {
            if (_objectivesText == null)
            {
                return;
            }

            ObjectiveManager objectiveManager = ObjectiveManager.Instance;
            if (objectiveManager == null)
            {
                return;
            }

            System.Collections.Generic.List<Objective> activeObjectives = objectiveManager.GetActiveObjectives();
            System.Collections.Generic.List<Objective> completedObjectives = objectiveManager.GetCompletedObjectives();

            string objectivesList;
            if (activeObjectives.Count > 0)
            {
                System.Text.StringBuilder builder = new System.Text.StringBuilder();
                builder.AppendLine("Objectives:");
                foreach (Objective obj in activeObjectives)
                {
                    builder.AppendLine($"- {obj.ObjectiveName} ({obj.CompletionCount}/{obj.RequiredCompletions})");
                }

                objectivesList = builder.ToString().TrimEnd();
            }
            else if (completedObjectives.Count > 0)
            {
                objectivesList = "Mission Complete!";
            }
            else
            {
                objectivesList = "No objectives";
            }

            if (_lastObjectivesDisplay == objectivesList)
            {
                return;
            }

            _lastObjectivesDisplay = objectivesList;
            _objectivesText.text = objectivesList;
        }

        private void ApplyTextFont(TextMeshProUGUI textComponent)
        {
            if (textComponent == null)
            {
                return;
            }

            TMP_FontAsset resolvedFont = ResolveHudFont();
            if (resolvedFont != null)
            {
                textComponent.font = resolvedFont;
            }
        }

        private TMP_FontAsset ResolveHudFont()
        {
            if (_hudFontAsset != null)
            {
                return _hudFontAsset;
            }

            if (TMP_Settings.defaultFontAsset != null)
            {
                return TMP_Settings.defaultFontAsset;
            }

            if (TMP_Settings.fallbackFontAssets != null && TMP_Settings.fallbackFontAssets.Count > 0)
            {
                return TMP_Settings.fallbackFontAssets[0];
            }

            return null;
        }

        private string GetCurrencyPrefix()
        {
            return "$";
        }

        private static void DisableRaycastTarget(TMP_Text textComponent)
        {
            if (textComponent != null)
            {
                textComponent.raycastTarget = false;
            }
        }

        private void ShowFeedback(string message)
        {
            if (_feedbackText == null || string.IsNullOrEmpty(message))
            {
                return;
            }

            if (_feedbackTimeRemaining > 0f && string.Equals(_lastFeedbackMessage, message, System.StringComparison.Ordinal))
            {
                return;
            }

            _feedbackText.text = message;
            _lastFeedbackMessage = message;
            _feedbackTimeRemaining = Mathf.Max(0.1f, _feedbackDuration);
        }

        private void TickFeedbackDisplay()
        {
            if (_feedbackTimeRemaining <= 0f)
            {
                return;
            }

            _feedbackTimeRemaining -= Time.deltaTime;
            if (_feedbackTimeRemaining > 0f)
            {
                return;
            }

            if (_feedbackText != null)
            {
                _feedbackText.text = string.Empty;
            }

            _lastFeedbackMessage = string.Empty;
        }

        private bool IsFreeGameplayUnpaused()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.CurrentState != GameState.FreePlay)
            {
                return false;
            }

            PauseManager pauseManager = PauseManager.Instance;
            return pauseManager == null || !pauseManager.IsPaused;
        }

        private bool ValidateRequiredReferences()
        {
            bool isValid = true;

            if (_canvasGroup == null)
            {
                Debug.LogError("[GameplayHUD] Missing CanvasGroup on HUD root. Add a CanvasGroup to HUD Canvas.", this);
                isValid = false;
            }

            if (_hudContainer == null)
            {
                Debug.LogError("[GameplayHUD] Missing HUD Container reference. Assign the HUD root container explicitly.", this);
                isValid = false;
            }
            else
            {
                _hudContainerCanvasGroup = _hudContainer.GetComponent<CanvasGroup>();
                if (_hudContainerCanvasGroup == null)
                {
                    Debug.LogError("[GameplayHUD] Missing CanvasGroup on HUD Container. Add CanvasGroup to the assigned HUD container.", this);
                    isValid = false;
                }
            }

            if (_crosshairText == null)
            {
                Debug.LogError("[GameplayHUD] Missing Crosshair Text reference. Assign HUD Canvas/CrosshairText explicitly.", this);
                isValid = false;
            }

            return isValid;
        }

        private void ValidatePromptReferences()
        {
            if (_interactionPromptText == null && !_hasLoggedMissingPromptText)
            {
                _hasLoggedMissingPromptText = true;
                Debug.LogWarning("[GameplayHUD] Interaction prompt text is not assigned. The generic E prompt will stay hidden.", this);
            }

            if (_interactionSystem == null && !_hasLoggedMissingInteractionSystem)
            {
                _hasLoggedMissingInteractionSystem = true;
                Debug.LogWarning("[GameplayHUD] InteractionSystem reference is not assigned. The generic E prompt will stay hidden.", this);
            }
        }

        private void UpdateInteractionPromptVisibility(bool canShowGameplayCenterUi)
        {
            if (_interactionPromptText == null)
            {
                return;
            }

            bool shouldShowPrompt =
                canShowGameplayCenterUi
                && _interactionSystem != null
                && _interactionSystem.CanInteractWithCurrent();

            _interactionPromptText.gameObject.SetActive(shouldShowPrompt);
        }

        private void ConfigureCrosshairText(TextMeshProUGUI crosshairText)
        {
            if (crosshairText == null)
            {
                return;
            }

            crosshairText.text = string.IsNullOrEmpty(_crosshairGlyph) ? "+" : _crosshairGlyph;
            crosshairText.fontSize = Mathf.Max(8f, _crosshairSize);
            crosshairText.alignment = TextAlignmentOptions.Center;
            crosshairText.color = Color.white;
            crosshairText.raycastTarget = false;
        }
    }
}
