using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Minigames
{
    /// <summary>
    /// Creates and updates the UI layer for LieMinigame.
    /// </summary>
    public class LieMinigameUI : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Reference to the LieMinigame instance")]
        private LieMinigame _lieMinigame;

        [SerializeField]
        [Tooltip("Optional TMP font asset override. Falls back to the project's default TMP font asset.")]
        private TMP_FontAsset _fontAsset;

        [SerializeField]
        [Range(200f, 800f)]
        [Tooltip("Width of the indicator bar in pixels")]
        private float _barWidth = 600f;

        [SerializeField]
        [Range(30f, 100f)]
        [Tooltip("Height of the indicator bar in pixels")]
        private float _barHeight = 50f;

        private Canvas _canvas;
        private RectTransform _panelRect;
        private RectTransform _dialoguePanelRect;
        private RectTransform _timingPanelRect;
        private Image _barBackground;
        private Image _targetZoneImage;
        private Image _indicatorThumb;
        private TextMeshProUGUI _attemptsText;
        private TextMeshProUGUI _promptText;
        private TextMeshProUGUI _feedbackText;
        private TextMeshProUGUI _questionText;
        private readonly Button[] _answerButtons = new Button[3];
        private readonly TextMeshProUGUI[] _answerButtonTexts = new TextMeshProUGUI[3];

        private float _feedbackDisplayTime;
        private const float FeedbackDuration = 1.5f;
        private bool _uiInitialized;

        public void Initialize(LieMinigame lieMinigame, Canvas canvas)
        {
            _lieMinigame = lieMinigame;
            _canvas = canvas;
            _feedbackDisplayTime = 0f;
            _uiInitialized = false;
            FindOrSetupUI();
        }

        private void OnEnable()
        {
            FindOrSetupUI();
        }

        private void OnDisable()
        {
            if (_feedbackText != null)
            {
                _feedbackText.text = string.Empty;
            }
        }

        private void FindOrSetupUI()
        {
            if (_uiInitialized)
            {
                UpdateStaticTexts();
                UpdateTargetZoneSize();
                UpdateDialogueTexts();
                return;
            }

            if (_canvas == null)
            {
                _canvas = GetComponentInParent<Canvas>();
                if (_canvas == null)
                {
                    Debug.LogError("[LieMinigameUI] No Canvas found in scene!");
                    return;
                }
            }

            ConfigureCanvas();

            _panelRect = GetComponent<RectTransform>();
            if (_panelRect == null)
            {
                Debug.LogError("[LieMinigameUI] Script must be on a UI Panel with RectTransform!");
                return;
            }

            ConfigurePanel();
            SetupDialoguePanel();
            SetupTimingPanel();
            UpdateStaticTexts();
            UpdateTargetZoneSize();
            UpdateDialogueTexts();
            RefreshPhaseVisibility();

            _uiInitialized = true;
        }

        private void ConfigureCanvas()
        {
            if (_canvas == null)
            {
                return;
            }

            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;
            _canvas.sortingOrder = Mathf.Max(_canvas.sortingOrder, 500);
        }

        private void ConfigurePanel()
        {
            _panelRect.anchorMin = Vector2.zero;
            _panelRect.anchorMax = Vector2.one;
            _panelRect.offsetMin = Vector2.zero;
            _panelRect.offsetMax = Vector2.zero;
            _panelRect.SetAsLastSibling();
        }

        private void SetupDialoguePanel()
        {
            Transform existing = _panelRect.Find("DialoguePanel");
            if (existing != null)
            {
                _dialoguePanelRect = existing.GetComponent<RectTransform>();
            }
            else
            {
                GameObject panel = new GameObject("DialoguePanel");
                panel.transform.SetParent(_panelRect, false);
                _dialoguePanelRect = panel.AddComponent<RectTransform>();
                _dialoguePanelRect.sizeDelta = new Vector2(980f, 420f);
                _dialoguePanelRect.anchorMin = new Vector2(0.5f, 0.5f);
                _dialoguePanelRect.anchorMax = new Vector2(0.5f, 0.5f);
                _dialoguePanelRect.anchoredPosition = new Vector2(0f, 30f);

                Image bg = panel.AddComponent<Image>();
                bg.color = new Color(0f, 0f, 0f, 0.72f);
                bg.raycastTarget = false;
            }

            SetupQuestionText();
            SetupAnswerButtons();
        }

        private void SetupQuestionText()
        {
            Transform existing = _dialoguePanelRect.Find("QuestionText");
            if (existing != null)
            {
                _questionText = GetOrCreateTmpText(existing.gameObject);
                ApplyTextStyle(_questionText, 34f, FontStyles.Bold, Color.white);
                _questionText.alignment = TextAlignmentOptions.Center;
                _questionText.textWrappingMode = TextWrappingModes.Normal;
                return;
            }

            GameObject questionObj = new GameObject("QuestionText");
            questionObj.transform.SetParent(_dialoguePanelRect, false);
            _questionText = questionObj.AddComponent<TextMeshProUGUI>();
            ApplyTextStyle(_questionText, 34f, FontStyles.Bold, Color.white);
            _questionText.alignment = TextAlignmentOptions.Center;
            _questionText.textWrappingMode = TextWrappingModes.Normal;

            RectTransform questionRect = questionObj.GetComponent<RectTransform>();
            questionRect.sizeDelta = new Vector2(860f, 120f);
            questionRect.anchorMin = new Vector2(0.5f, 1f);
            questionRect.anchorMax = new Vector2(0.5f, 1f);
            questionRect.pivot = new Vector2(0.5f, 1f);
            questionRect.anchoredPosition = new Vector2(0f, -28f);
        }

        private void SetupAnswerButtons()
        {
            for (int i = 0; i < _answerButtons.Length; i++)
            {
                string buttonName = $"AnswerButton{i + 1}";
                Transform existing = _dialoguePanelRect.Find(buttonName);
                GameObject buttonObj;
                Button button;
                TextMeshProUGUI buttonText;

                if (existing != null)
                {
                    buttonObj = existing.gameObject;
                    button = buttonObj.GetComponent<Button>();
                    if (button == null)
                    {
                        button = buttonObj.AddComponent<Button>();
                    }
                }
                else
                {
                    buttonObj = new GameObject(buttonName);
                    buttonObj.transform.SetParent(_dialoguePanelRect, false);

                    Image image = buttonObj.AddComponent<Image>();
                    image.color = new Color(0.16f, 0.2f, 0.28f, 0.98f);
                    image.raycastTarget = true;

                    button = buttonObj.AddComponent<Button>();
                    ColorBlock colors = button.colors;
                    colors.normalColor = image.color;
                    colors.highlightedColor = new Color(0.24f, 0.32f, 0.43f, 1f);
                    colors.pressedColor = new Color(0.08f, 0.12f, 0.2f, 1f);
                    colors.selectedColor = colors.highlightedColor;
                    button.colors = colors;

                    RectTransform buttonRect = buttonObj.GetComponent<RectTransform>();
                    buttonRect.sizeDelta = new Vector2(840f, 72f);
                    buttonRect.anchorMin = new Vector2(0.5f, 1f);
                    buttonRect.anchorMax = new Vector2(0.5f, 1f);
                    buttonRect.pivot = new Vector2(0.5f, 1f);
                    buttonRect.anchoredPosition = new Vector2(0f, -150f - (i * 90f));
                }

                button.onClick.RemoveAllListeners();
                int answerIndex = i;
                button.onClick.AddListener(() => OnAnswerClicked(answerIndex));

                Transform textChild = buttonObj.transform.Find("Label");
                if (textChild != null)
                {
                    buttonText = GetOrCreateTmpText(textChild.gameObject);
                }
                else
                {
                    GameObject labelObj = new GameObject("Label");
                    labelObj.transform.SetParent(buttonObj.transform, false);
                    buttonText = labelObj.AddComponent<TextMeshProUGUI>();

                    RectTransform labelRect = labelObj.GetComponent<RectTransform>();
                    labelRect.anchorMin = Vector2.zero;
                    labelRect.anchorMax = Vector2.one;
                    labelRect.offsetMin = new Vector2(16f, 8f);
                    labelRect.offsetMax = new Vector2(-16f, -8f);
                }

                ApplyTextStyle(buttonText, 24f, FontStyles.Normal, Color.white);
                buttonText.alignment = TextAlignmentOptions.MidlineLeft;
                buttonText.textWrappingMode = TextWrappingModes.Normal;
                buttonText.raycastTarget = false;

                _answerButtons[i] = button;
                _answerButtonTexts[i] = buttonText;
            }
        }

        private void SetupTimingPanel()
        {
            Transform existing = _panelRect.Find("TimingPanel");
            if (existing != null)
            {
                _timingPanelRect = existing.GetComponent<RectTransform>();
            }
            else
            {
                GameObject panel = new GameObject("TimingPanel");
                panel.transform.SetParent(_panelRect, false);
                _timingPanelRect = panel.AddComponent<RectTransform>();
                _timingPanelRect.anchorMin = Vector2.zero;
                _timingPanelRect.anchorMax = Vector2.one;
                _timingPanelRect.offsetMin = Vector2.zero;
                _timingPanelRect.offsetMax = Vector2.zero;
            }

            SetupBarBackground();
            SetupTargetZone();
            SetupIndicatorThumb();
            SetupAttemptsText();
            SetupPromptText();
            SetupFeedbackText();
        }

        private void SetupBarBackground()
        {
            Transform existingBar = _timingPanelRect.Find("BarBackground");
            if (existingBar != null)
            {
                _barBackground = existingBar.GetComponent<Image>();
                return;
            }

            GameObject barObj = new GameObject("BarBackground");
            barObj.transform.SetParent(_timingPanelRect, false);
            _barBackground = barObj.AddComponent<Image>();
            _barBackground.color = new Color(0.08f, 0.08f, 0.08f, 0.95f);
            _barBackground.raycastTarget = false;

            RectTransform barRect = barObj.GetComponent<RectTransform>();
            barRect.sizeDelta = new Vector2(_barWidth, _barHeight);
            barRect.anchorMin = new Vector2(0.5f, 0.5f);
            barRect.anchorMax = new Vector2(0.5f, 0.5f);
            barRect.anchoredPosition = Vector2.zero;
        }

        private void SetupTargetZone()
        {
            Transform existingZone = _timingPanelRect.Find("TargetZone");
            if (existingZone != null)
            {
                _targetZoneImage = existingZone.GetComponent<Image>();
                return;
            }

            GameObject zoneObj = new GameObject("TargetZone");
            zoneObj.transform.SetParent(_timingPanelRect, false);
            _targetZoneImage = zoneObj.AddComponent<Image>();
            _targetZoneImage.color = new Color(0f, 1f, 0f, 0.35f);
            _targetZoneImage.raycastTarget = false;
        }

        private void SetupIndicatorThumb()
        {
            Transform existingThumb = _timingPanelRect.Find("IndicatorThumb");
            if (existingThumb != null)
            {
                _indicatorThumb = existingThumb.GetComponent<Image>();
                return;
            }

            GameObject thumbObj = new GameObject("IndicatorThumb");
            thumbObj.transform.SetParent(_timingPanelRect, false);
            _indicatorThumb = thumbObj.AddComponent<Image>();
            _indicatorThumb.color = Color.white;
            _indicatorThumb.raycastTarget = false;

            RectTransform thumbRect = thumbObj.GetComponent<RectTransform>();
            thumbRect.sizeDelta = new Vector2(12f, _barHeight + 14f);
            thumbRect.anchorMin = new Vector2(0.5f, 0.5f);
            thumbRect.anchorMax = new Vector2(0.5f, 0.5f);
            thumbRect.anchoredPosition = Vector2.zero;
        }

        private void SetupAttemptsText()
        {
            Transform existingAttempts = _timingPanelRect.Find("AttemptsText");
            if (existingAttempts != null)
            {
                _attemptsText = GetOrCreateTmpText(existingAttempts.gameObject);
                ApplyTextStyle(_attemptsText, 24f, FontStyles.Bold, Color.white);
                _attemptsText.alignment = TextAlignmentOptions.Left;
                return;
            }

            GameObject attemptsObj = new GameObject("AttemptsText");
            attemptsObj.transform.SetParent(_timingPanelRect, false);
            _attemptsText = attemptsObj.AddComponent<TextMeshProUGUI>();
            ApplyTextStyle(_attemptsText, 24f, FontStyles.Bold, Color.white);
            _attemptsText.alignment = TextAlignmentOptions.Left;

            RectTransform attemptsRect = attemptsObj.GetComponent<RectTransform>();
            attemptsRect.sizeDelta = new Vector2(240f, 50f);
            attemptsRect.anchorMin = new Vector2(0.5f, 0.5f);
            attemptsRect.anchorMax = new Vector2(0.5f, 0.5f);
            attemptsRect.anchoredPosition = new Vector2(-(_barWidth * 0.5f) + 120f, (_barHeight * 0.5f) + 36f);
        }

        private void SetupPromptText()
        {
            Transform existingPrompt = _timingPanelRect.Find("PromptText");
            if (existingPrompt != null)
            {
                _promptText = GetOrCreateTmpText(existingPrompt.gameObject);
                ApplyTextStyle(_promptText, 20f, FontStyles.Normal, Color.yellow);
                _promptText.alignment = TextAlignmentOptions.Center;
                return;
            }

            GameObject promptObj = new GameObject("PromptText");
            promptObj.transform.SetParent(_timingPanelRect, false);
            _promptText = promptObj.AddComponent<TextMeshProUGUI>();
            ApplyTextStyle(_promptText, 20f, FontStyles.Normal, Color.yellow);
            _promptText.alignment = TextAlignmentOptions.Center;

            RectTransform promptRect = promptObj.GetComponent<RectTransform>();
            promptRect.sizeDelta = new Vector2(_barWidth + 300f, 70f);
            promptRect.anchorMin = new Vector2(0.5f, 0.5f);
            promptRect.anchorMax = new Vector2(0.5f, 0.5f);
            promptRect.anchoredPosition = new Vector2(0f, -(_barHeight * 0.5f) - 56f);
        }

        private void SetupFeedbackText()
        {
            Transform existingFeedback = _timingPanelRect.Find("FeedbackText");
            if (existingFeedback != null)
            {
                _feedbackText = GetOrCreateTmpText(existingFeedback.gameObject);
                ApplyTextStyle(_feedbackText, 32f, FontStyles.Bold, Color.white);
                _feedbackText.alignment = TextAlignmentOptions.Center;
                return;
            }

            GameObject feedbackObj = new GameObject("FeedbackText");
            feedbackObj.transform.SetParent(_timingPanelRect, false);
            _feedbackText = feedbackObj.AddComponent<TextMeshProUGUI>();
            ApplyTextStyle(_feedbackText, 32f, FontStyles.Bold, Color.white);
            _feedbackText.alignment = TextAlignmentOptions.Center;
            _feedbackText.text = string.Empty;

            RectTransform feedbackRect = feedbackObj.GetComponent<RectTransform>();
            feedbackRect.sizeDelta = new Vector2(_barWidth + 100f, 80f);
            feedbackRect.anchorMin = new Vector2(0.5f, 0.5f);
            feedbackRect.anchorMax = new Vector2(0.5f, 0.5f);
            feedbackRect.anchoredPosition = new Vector2(0f, (_barHeight * 0.5f) + 86f);
        }

        private void ApplyTextStyle(TextMeshProUGUI textComponent, float fontSize, FontStyles fontStyle, Color color)
        {
            if (textComponent == null)
            {
                return;
            }

            TMP_FontAsset resolvedFont = ResolveFontAsset();
            if (resolvedFont != null)
            {
                textComponent.font = resolvedFont;
            }

            textComponent.fontSize = fontSize;
            textComponent.fontStyle = fontStyle;
            textComponent.color = color;
            textComponent.raycastTarget = false;
            textComponent.richText = true;
            textComponent.textWrappingMode = TextWrappingModes.NoWrap;
            textComponent.overflowMode = TextOverflowModes.Overflow;
        }

        private TextMeshProUGUI GetOrCreateTmpText(GameObject target)
        {
            if (target == null)
            {
                return null;
            }

            TextMeshProUGUI tmpText = target.GetComponent<TextMeshProUGUI>();
            if (tmpText != null)
            {
                return tmpText;
            }

            Text legacyText = target.GetComponent<Text>();
            tmpText = target.AddComponent<TextMeshProUGUI>();

            if (legacyText != null)
            {
                tmpText.text = legacyText.text;
                tmpText.color = legacyText.color;
                tmpText.fontSize = legacyText.fontSize;
                tmpText.raycastTarget = legacyText.raycastTarget;

                switch (legacyText.alignment)
                {
                    case TextAnchor.MiddleLeft:
                        tmpText.alignment = TextAlignmentOptions.Left;
                        break;
                    case TextAnchor.MiddleRight:
                        tmpText.alignment = TextAlignmentOptions.Right;
                        break;
                    default:
                        tmpText.alignment = TextAlignmentOptions.Center;
                        break;
                }

                legacyText.enabled = false;
            }

            return tmpText;
        }

        private TMP_FontAsset ResolveFontAsset()
        {
            if (_fontAsset != null)
            {
                return _fontAsset;
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

        private void Update()
        {
            if (_lieMinigame == null || !_lieMinigame.IsActive())
                return;

            RefreshPhaseVisibility();

            if (_lieMinigame.IsWaitingForAnswerSelection()
                || _lieMinigame.IsWaitingForPostResultAnswerSelection()
                || _lieMinigame.IsShowingFollowUp())
            {
                UpdateDialogueTexts();
                EnsureDialogueSelection();
                return;
            }

            UpdateTargetZoneSize();
            UpdateIndicatorPosition();
            UpdateAttemptsDisplay();
            UpdateFeedbackDisplay();
        }

        private void RefreshPhaseVisibility()
        {
            bool waitingForAnswer = _lieMinigame != null && _lieMinigame.IsWaitingForAnswerSelection();
            bool waitingForPostResultAnswer = _lieMinigame != null && _lieMinigame.IsWaitingForPostResultAnswerSelection();
            bool showingFollowUp = _lieMinigame != null && _lieMinigame.IsShowingFollowUp();
            bool showDialoguePanel = waitingForAnswer || waitingForPostResultAnswer || showingFollowUp;

            if (_dialoguePanelRect != null)
            {
                _dialoguePanelRect.gameObject.SetActive(showDialoguePanel);
            }

            if (_timingPanelRect != null)
            {
                _timingPanelRect.gameObject.SetActive(!showDialoguePanel);
            }
        }

        private void UpdateDialogueTexts()
        {
            if (_lieMinigame == null)
            {
                return;
            }

            bool waitingForAnswer = _lieMinigame.IsWaitingForAnswerSelection();
            bool waitingForPostResultAnswer = _lieMinigame.IsWaitingForPostResultAnswerSelection();
            bool showingFollowUp = _lieMinigame.IsShowingFollowUp();

            if (_questionText != null)
            {
                _questionText.text = _lieMinigame.GetQuestionText();
            }

            if (showingFollowUp)
            {
                for (int i = 0; i < _answerButtons.Length; i++)
                {
                    bool isContinueButton = i == 0;

                    if (_answerButtons[i] != null)
                    {
                        _answerButtons[i].gameObject.SetActive(isContinueButton);
                        _answerButtons[i].interactable = isContinueButton;
                    }

                    if (isContinueButton && _answerButtonTexts[i] != null)
                    {
                        _answerButtonTexts[i].text = $"Continue ({_lieMinigame.GetTriggerKeyDisplayName()})";
                    }
                }

                return;
            }

            int answerCount = waitingForPostResultAnswer
                ? _lieMinigame.GetPostResultAnswerCount()
                : _lieMinigame.GetAnswerCount();

            for (int i = 0; i < _answerButtons.Length; i++)
            {
                bool isAvailable = i < answerCount;
                if (_answerButtons[i] != null)
                {
                    _answerButtons[i].gameObject.SetActive(isAvailable);
                    _answerButtons[i].interactable = waitingForAnswer || waitingForPostResultAnswer;
                }

                if (isAvailable && _answerButtonTexts[i] != null)
                {
                    _answerButtonTexts[i].text = waitingForPostResultAnswer
                        ? _lieMinigame.GetPostResultAnswerText(i)
                        : _lieMinigame.GetAnswerText(i);
                }
            }
        }

        private void EnsureDialogueSelection()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                return;
            }

            if (eventSystem.currentSelectedGameObject != null)
            {
                return;
            }

            for (int i = 0; i < _answerButtons.Length; i++)
            {
                Button button = _answerButtons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable)
                {
                    continue;
                }

                eventSystem.SetSelectedGameObject(button.gameObject);
                return;
            }
        }

        private void OnAnswerClicked(int answerIndex)
        {
            if (_lieMinigame == null)
            {
                return;
            }

            if (_lieMinigame.IsShowingFollowUp())
            {
                bool queuedFollowUpConfirm = _lieMinigame.RequestFollowUpConfirm();
                if (queuedFollowUpConfirm)
                {
                    for (int i = 0; i < _answerButtons.Length; i++)
                    {
                        if (_answerButtons[i] != null)
                        {
                            _answerButtons[i].interactable = false;
                        }
                    }
                }

                return;
            }

            if (_lieMinigame.IsWaitingForPostResultAnswerSelection())
            {
                _lieMinigame.SelectPostResultAnswer(answerIndex);
                return;
            }

            bool startedTimingPhase = _lieMinigame.SelectAnswer(answerIndex);
            if (!startedTimingPhase)
            {
                return;
            }

            for (int i = 0; i < _answerButtons.Length; i++)
            {
                if (_answerButtons[i] != null)
                {
                    _answerButtons[i].interactable = false;
                }
            }
        }

        private void UpdateIndicatorPosition()
        {
            if (_indicatorThumb == null)
                return;

            float indicatorPos = _lieMinigame.GetIndicatorPosition();
            RectTransform thumbRect = _indicatorThumb.GetComponent<RectTransform>();
            float xPos = NormalizedToBarX(indicatorPos);
            thumbRect.anchoredPosition = new Vector2(xPos, 0f);
            _indicatorThumb.color = _lieMinigame.IsIndicatorInZone() ? new Color(0f, 1f, 0f, 1f) : Color.white;
        }

        private void UpdateTargetZoneSize()
        {
            if (_targetZoneImage == null || _lieMinigame == null)
                return;

            float zoneStart = _lieMinigame.GetTargetZoneStart();
            float zoneEnd = _lieMinigame.GetTargetZoneEnd();
            float zoneWidth = (zoneEnd - zoneStart) * _barWidth;

            RectTransform zoneRect = _targetZoneImage.GetComponent<RectTransform>();
            zoneRect.sizeDelta = new Vector2(zoneWidth, _barHeight);
            zoneRect.anchorMin = new Vector2(0.5f, 0.5f);
            zoneRect.anchorMax = new Vector2(0.5f, 0.5f);

            float zoneCenter = (zoneStart + zoneEnd) * 0.5f;
            float xPos = NormalizedToBarX(zoneCenter);
            zoneRect.anchoredPosition = new Vector2(xPos, 0f);
        }

        private float NormalizedToBarX(float normalizedPosition)
        {
            return (Mathf.Clamp01(normalizedPosition) - 0.5f) * _barWidth;
        }

        private void UpdateAttemptsDisplay()
        {
            if (_attemptsText == null || _lieMinigame == null)
                return;

            int attemptsRemaining = _lieMinigame.GetAttemptsRemaining();
            int maxAttempts = Mathf.Max(1, _lieMinigame.GetMaxAttempts());
            int attemptsUsed = maxAttempts - attemptsRemaining;

            _attemptsText.text = $"Attempts: {attemptsUsed}/{maxAttempts}";
        }

        private void UpdateFeedbackDisplay()
        {
            if (_feedbackText == null || _lieMinigame == null)
                return;

            MinigameResult result = _lieMinigame.GetResult();
            if (result == MinigameResult.None)
                return;

            if (_feedbackDisplayTime == 0f)
            {
                float accuracy = _lieMinigame.GetAccuracy();
                _feedbackText.text = GetFeedbackMessage(accuracy, result);
                _feedbackText.color = result == MinigameResult.Pass ? Color.green : Color.red;
                _feedbackDisplayTime = FeedbackDuration;
                return;
            }

            _feedbackDisplayTime -= Time.deltaTime;
            if (_feedbackDisplayTime <= 0f)
            {
                _feedbackText.text = string.Empty;
                _feedbackDisplayTime = 0f;
            }
        }

        private void UpdateStaticTexts()
        {
            if (_promptText == null || _lieMinigame == null)
                return;

            _promptText.text = $"Press {_lieMinigame.GetTriggerKeyDisplayName()} when indicator enters green zone";
        }

        private string GetFeedbackMessage(float accuracy, MinigameResult result)
        {
            if (result == MinigameResult.Pass)
            {
                if (accuracy >= 0.9f)
                    return $"Perfect! ({accuracy:P0})";
                if (accuracy >= 0.7f)
                    return $"Good! ({accuracy:P0})";
                return $"Okay ({accuracy:P0})";
            }

            return "Miss!";
        }
    }
}
