using System.Collections.Generic;
using UnityEngine;
using Game.Core;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Game.Input;
using Game.Player;

namespace Game.Minigames
{
    /// <summary>
    /// LieMinigame uses a two-phase flow:
    /// 1) Select an excuse in dialogue.
    /// 2) Pass timing challenge (indicator inside green zone).
    /// </summary>
    public class LieMinigame : BaseMinigame
    {
        [SerializeField] private int _difficulty = 5;
        [SerializeField] private float _indicatorSpeed = 2f;
        [SerializeField] private float _targetZoneWidth = 0.3f;
        [SerializeField] private float _targetZoneCenter = 0.5f;
        [SerializeField] private int _maxAttempts = 1;
        [SerializeField] private bool _showTargetZone = true;
        [SerializeField] private KeyCode _triggerKey = KeyCode.Space;

        [Header("Dialogue Defaults")]
        [SerializeField] private string _questionText = "Hey! What are you doing in here?!";
        [SerializeField] private string _answer1Text = "I'm just going to the bathroom.";
        [SerializeField] private string _answer2Text = "I'm looking for the boss, can you tell me where he is?";
        [SerializeField] private string _answer3Text = "I'm just passing by.";
        [SerializeField] private float _answer1ZoneMultiplier = 1.2f;
        [SerializeField] private float _answer2ZoneMultiplier = 1f;
        [SerializeField] private float _answer3ZoneMultiplier = 0.8f;
        [SerializeField] private float _suspicionPenaltyPerRepeat = 0.08f;
        [SerializeField] private float _suspicionMinMultiplier = 0.6f;

        [Header("Cursor")]
        [SerializeField] private bool _unlockCursorDuringMinigame = true;

        [Header("Follow-Up Dialogue")]
        [SerializeField] private bool _requireFollowUpConfirm = true;

        private string _triggerKeyLabel = "SPACE";
        private float _indicatorPosition;
        private float _indicatorDirection = 1f;
        private int _attemptsRemaining;
        private bool _isInputEnabled;
        private bool _hasSucceeded;
        private float _accuracy;
        private float _baseTargetZoneWidth;
        private int _selectedAnswerIndex = -1;
        private bool _hasSelectedAnswer;
        private float _selectedAnswerSuspicionMultiplier = 1f;
        private string _selectedAnswerFollowUpText = string.Empty;
        private string _selectedAnswerBranchHook = string.Empty;
        private string _selectedAnswerNextStepId = string.Empty;
        private string _postResolutionFollowUpText = string.Empty;
        private string _postResultQuestionText = string.Empty;
        private MinigameResult _pendingResult = MinigameResult.None;
        private bool _pendingFollowUpConfirmRequest;

        private enum GameState
        {
            WaitingForAnswer,
            WaitingForPress,
            WaitingForPostResultAnswer,
            ShowingFollowUp,
            PressRegistered,
            Complete
        }

        private sealed class AnswerOptionConfig
        {
            public string Id;
            public string Text;
            public float ZoneWidthMultiplier;
            public string FollowUpText;
            public string BranchHook;
            public string NextStepId;
        }

        private readonly List<AnswerOptionConfig> _answerOptions = new();
        private readonly List<AnswerOptionConfig> _postResultAnswerOptions = new();
        private GameState _gameState = GameState.WaitingForAnswer;
        private Canvas _uiCanvas;
        private GameObject _uiRoot;
        private LieMinigameUI _uiController;
        private CursorLockMode _previousCursorLockMode;
        private bool _previousCursorVisible;
        private bool _hasLoggedMissingEventSystem;
        private bool _usesPresentationCursorAuthority;

        private const int UiSortingOrder = 500;

        protected override void OnInitialize()
        {
            LoadParameters();
            _attemptsRemaining = _maxAttempts;
            _indicatorPosition = 0f;
            _indicatorDirection = 1f;
            _hasSucceeded = false;
            _hasSelectedAnswer = false;
            _selectedAnswerIndex = -1;
            _selectedAnswerSuspicionMultiplier = 1f;
            _selectedAnswerFollowUpText = string.Empty;
            _selectedAnswerBranchHook = string.Empty;
            _selectedAnswerNextStepId = string.Empty;
            _postResolutionFollowUpText = string.Empty;
            _postResultQuestionText = string.Empty;
            _postResultAnswerOptions.Clear();
            _pendingResult = MinigameResult.None;
            _pendingFollowUpConfirmRequest = false;
            _targetZoneWidth = _baseTargetZoneWidth;
        }

        protected override void OnStart()
        {
            _isInputEnabled = false;
            _gameState = GameState.WaitingForAnswer;
            _pendingFollowUpConfirmRequest = false;

            if (_unlockCursorDuringMinigame)
            {
                // TODO(MP-6): Replace global cursor-lock toggles with per-player presentation ownership when concurrent minigames are supported.
                AcquireMinigameCursorAuthority();
            }

            EnsureUI();

            if (_uiController == null)
            {
                Debug.LogError("[LieMinigame] Required UI setup is missing; cannot start lie minigame.", this);
                SetResult(MinigameResult.Fail);
                return;
            }
        }

        protected override void OnUpdate()
        {
            if (_gameState == GameState.WaitingForAnswer || _gameState == GameState.WaitingForPostResultAnswer)
            {
                return;
            }

            if (_gameState == GameState.ShowingFollowUp)
            {
                if (ConsumeFollowUpConfirmRequest() || IsTriggerPressed())
                {
                    ConfirmFollowUp();
                }

                return;
            }

            if (!_isInputEnabled)
            {
                return;
            }

            UpdateIndicator();
            HandleInput();
        }

        protected override void OnEnd()
        {
            _isInputEnabled = false;
            _pendingFollowUpConfirmRequest = false;
            CleanupUI();

            if (_unlockCursorDuringMinigame)
            {
                ReleaseMinigameCursorAuthority();
            }
        }

        private void LoadParameters()
        {
            if (_minigameData == null)
            {
                return;
            }

            int defaultDifficulty = _difficulty;
            float defaultSpeed = _indicatorSpeed;
            float defaultTargetZoneWidth = _targetZoneWidth;
            int defaultMaxAttempts = _maxAttempts;
            bool defaultShowTargetZone = _showTargetZone;

            bool hasIndicatorSpeedParameter = HasParameter("indicator_speed");
            bool hasTargetZoneWidthParameter = HasParameter("target_zone_width");

            _difficulty = Mathf.Clamp(GetParameterInt("difficulty", defaultDifficulty), 0, 10);
            _indicatorSpeed = GetParameterFloat("indicator_speed", defaultSpeed);
            _maxAttempts = Mathf.Max(1, GetParameterInt("max_attempts", defaultMaxAttempts));
            _showTargetZone = GetParameterBool("show_target_zone", defaultShowTargetZone);
            _targetZoneCenter = Mathf.Clamp01(GetParameterFloat("target_zone_center", _targetZoneCenter));

            if (hasTargetZoneWidthParameter)
            {
                _baseTargetZoneWidth = Mathf.Clamp01(GetParameterFloat("target_zone_width", defaultTargetZoneWidth));
            }
            else
            {
                // Preserve existing behavior when no explicit target width is provided.
                _baseTargetZoneWidth = Mathf.Lerp(0.5f, 0.15f, _difficulty / 10f);
            }

            _triggerKey = KeyCode.Space;
            _triggerKeyLabel = _triggerKey.ToString().ToUpperInvariant();

            object keyObj = GetParameter("trigger_key");
            if (keyObj is string keyString)
            {
                if (System.Enum.TryParse(keyString, true, out KeyCode parsedKey))
                {
                    _triggerKey = parsedKey;
                    _triggerKeyLabel = parsedKey.ToString().ToUpperInvariant();
                }
                else if (!string.IsNullOrWhiteSpace(keyString))
                {
                    _triggerKeyLabel = keyString.ToUpperInvariant();
                }
            }

            if (!hasIndicatorSpeedParameter)
            {
                // Preserve existing behavior when no explicit speed is provided.
                _indicatorSpeed *= 1f + (_difficulty * 0.1f);
            }

            LoadDialogueParameters();
        }

        private void LoadDialogueParameters()
        {
            bool loadedFromAsset = TryLoadDialogueFromAsset();

            _questionText = GetParameterString("npc_question", _questionText);
            _suspicionPenaltyPerRepeat = Mathf.Max(0f, GetParameterFloat("suspicion_penalty_per_repeat", _suspicionPenaltyPerRepeat));
            _suspicionMinMultiplier = Mathf.Clamp(GetParameterFloat("suspicion_min_multiplier", _suspicionMinMultiplier), 0.1f, 1f);

            if (!loadedFromAsset)
            {
                _answerOptions.Clear();
                _answerOptions.Add(BuildAnswerOption(0, "answer_1", _answer1Text, _answer1ZoneMultiplier));
                _answerOptions.Add(BuildAnswerOption(1, "answer_2", _answer2Text, _answer2ZoneMultiplier));
                _answerOptions.Add(BuildAnswerOption(2, "answer_3", _answer3Text, _answer3ZoneMultiplier));
            }
            else
            {
                ApplyAnswerParameterOverrides();
            }

            if (_answerOptions.Count <= 0)
            {
                _answerOptions.Add(BuildAnswerOption(0, "answer_1", _answer1Text, _answer1ZoneMultiplier));
            }
        }

        private bool TryLoadDialogueFromAsset()
        {
            LieDialogueSet dialogueSet = ResolveDialogueSetParameter();
            if (dialogueSet == null)
            {
                return false;
            }

            string requestedStepId = GetParameterString("lie_dialogue_step_id", string.Empty);
            LieDialogueStep dialogueStep = string.IsNullOrWhiteSpace(requestedStepId)
                ? dialogueSet.GetFirstStep()
                : dialogueSet.GetStepById(requestedStepId);

            if (dialogueStep == null)
            {
                dialogueStep = dialogueSet.GetFirstStep();
            }

            if (dialogueStep == null)
            {
                return false;
            }

            _questionText = string.IsNullOrWhiteSpace(dialogueStep.PromptText)
                ? _questionText
                : dialogueStep.PromptText;

            _suspicionPenaltyPerRepeat = Mathf.Max(0f, dialogueSet.SuspicionPenaltyPerRepeat);
            _suspicionMinMultiplier = Mathf.Clamp(dialogueSet.SuspicionMinMultiplier, 0.1f, 1f);

            _answerOptions.Clear();
            int answerLimit = Mathf.Min(3, dialogueStep.Answers.Count);
            for (int i = 0; i < answerLimit; i++)
            {
                LieDialogueAnswer answer = dialogueStep.Answers[i];
                if (answer == null)
                {
                    continue;
                }

                string fallbackId = $"answer_{i + 1}";
                string answerId = string.IsNullOrWhiteSpace(answer.AnswerId) ? fallbackId : answer.AnswerId;
                string answerText = string.IsNullOrWhiteSpace(answer.AnswerText) ? $"Answer {i + 1}" : answer.AnswerText;

                _answerOptions.Add(new AnswerOptionConfig
                {
                    Id = answerId,
                    Text = answerText,
                    ZoneWidthMultiplier = Mathf.Clamp(answer.ZoneWidthMultiplier, 0.1f, 2f),
                    FollowUpText = answer.FollowUpText ?? string.Empty,
                    BranchHook = answer.BranchHook ?? string.Empty,
                    NextStepId = answer.NextStepId ?? string.Empty
                });
            }

            return _answerOptions.Count > 0;
        }

        private LieDialogueSet ResolveDialogueSetParameter()
        {
            object dialogueAssetObject = GetParameter("lie_dialogue_set");
            if (dialogueAssetObject is LieDialogueSet dialogueSet)
            {
                return dialogueSet;
            }

            string dialogueSetPath = GetParameterString("lie_dialogue_set_path", string.Empty);
            if (!string.IsNullOrWhiteSpace(dialogueSetPath))
            {
                return Resources.Load<LieDialogueSet>(dialogueSetPath);
            }

            return null;
        }

        private void ApplyAnswerParameterOverrides()
        {
            for (int i = 0; i < _answerOptions.Count; i++)
            {
                AnswerOptionConfig option = _answerOptions[i];
                int answerIndex = i + 1;

                option.Id = GetParameterString($"answer_{answerIndex}_id", option.Id);
                option.Text = GetParameterString($"answer_{answerIndex}_text", option.Text);
                option.ZoneWidthMultiplier = Mathf.Clamp(GetParameterFloat($"answer_{answerIndex}_zone_multiplier", option.ZoneWidthMultiplier), 0.1f, 2f);
                option.FollowUpText = GetParameterString($"answer_{answerIndex}_followup_text", option.FollowUpText);
                option.BranchHook = GetParameterString($"answer_{answerIndex}_branch_hook", option.BranchHook);
                option.NextStepId = GetParameterString($"answer_{answerIndex}_next_step_id", option.NextStepId);

                _answerOptions[i] = option;
            }
        }

        private AnswerOptionConfig BuildAnswerOption(int index, string fallbackId, string defaultText, float defaultZoneMultiplier)
        {
            int answerIndex = index + 1;
            string id = GetParameterString($"answer_{answerIndex}_id", fallbackId);
            string text = GetParameterString($"answer_{answerIndex}_text", defaultText);
            float zoneMultiplier = Mathf.Clamp(GetParameterFloat($"answer_{answerIndex}_zone_multiplier", defaultZoneMultiplier), 0.1f, 2f);
            string followUpText = GetParameterString($"answer_{answerIndex}_followup_text", string.Empty);
            string branchHook = GetParameterString($"answer_{answerIndex}_branch_hook", string.Empty);
            string nextStepId = GetParameterString($"answer_{answerIndex}_next_step_id", string.Empty);

            if (string.IsNullOrWhiteSpace(id))
            {
                id = fallbackId;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                text = $"Answer {answerIndex}";
            }

            return new AnswerOptionConfig
            {
                Id = id,
                Text = text,
                ZoneWidthMultiplier = zoneMultiplier,
                FollowUpText = followUpText,
                BranchHook = branchHook,
                NextStepId = nextStepId
            };
        }

        public bool SelectAnswer(int answerIndex)
        {
            if (_gameState != GameState.WaitingForAnswer)
            {
                return false;
            }

            if (answerIndex < 0 || answerIndex >= _answerOptions.Count)
            {
                return false;
            }

            AnswerOptionConfig selected = _answerOptions[answerIndex];
            _selectedAnswerSuspicionMultiplier = LieSuspicionMemory.GetSuspicionMultiplier(selected.Id, _suspicionPenaltyPerRepeat, _suspicionMinMultiplier);

            float totalMultiplier = selected.ZoneWidthMultiplier * _selectedAnswerSuspicionMultiplier;
            _targetZoneWidth = Mathf.Clamp01(_baseTargetZoneWidth * totalMultiplier);
            _selectedAnswerIndex = answerIndex;
            _hasSelectedAnswer = true;
            _selectedAnswerFollowUpText = selected.FollowUpText ?? string.Empty;
            _selectedAnswerBranchHook = selected.BranchHook ?? string.Empty;
            _selectedAnswerNextStepId = selected.NextStepId ?? string.Empty;
            _postResolutionFollowUpText = string.Empty;

            LieSuspicionMemory.RegisterChoice(selected.Id);

            StartTimingPhase();
            return true;
        }

        private void StartTimingPhase()
        {
            _attemptsRemaining = _maxAttempts;
            _indicatorPosition = 0f;
            _indicatorDirection = 1f;
            _isInputEnabled = true;
            _gameState = GameState.WaitingForPress;
        }

        private int GetParameterInt(string key, int defaultValue)
        {
            if (!TryGetParameterValue(key, out object value))
                return defaultValue;

            if (value is int intValue)
                return intValue;

            if (value is float floatValue)
                return Mathf.RoundToInt(floatValue);

            if (value is double doubleValue)
                return Mathf.RoundToInt((float)doubleValue);

            if (value is long longValue)
                return (int)longValue;

            if (value is string stringValue && int.TryParse(stringValue, out int parsedInt))
                return parsedInt;

            return defaultValue;
        }

        private float GetParameterFloat(string key, float defaultValue)
        {
            if (!TryGetParameterValue(key, out object value))
                return defaultValue;

            if (value is float floatValue)
                return floatValue;
            if (value is int intValue)
                return intValue;

            if (value is double doubleValue)
                return (float)doubleValue;

            if (value is long longValue)
                return longValue;

            if (value is string stringValue && float.TryParse(stringValue, out float parsedFloat))
                return parsedFloat;

            return defaultValue;
        }

        private string GetParameterString(string key, string defaultValue)
        {
            if (!TryGetParameterValue(key, out object value) || value == null)
            {
                return defaultValue;
            }

            if (value is string stringValue)
            {
                return string.IsNullOrWhiteSpace(stringValue) ? defaultValue : stringValue;
            }

            return value.ToString();
        }

        private bool GetParameterBool(string key, bool defaultValue)
        {
            if (!TryGetParameterValue(key, out object value))
                return defaultValue;

            if (value is bool boolValue)
                return boolValue;

            if (value is string stringValue && bool.TryParse(stringValue, out bool parsedBool))
                return parsedBool;

            return defaultValue;
        }

        private bool HasParameter(string key)
        {
            return TryGetParameterValue(key, out _);
        }

        private bool TryGetParameterValue(string key, out object value)
        {
            value = null;

            if (string.IsNullOrWhiteSpace(key) || _minigameData == null || _minigameData.parameters == null)
            {
                return false;
            }

            return _minigameData.parameters.TryGetValue(key, out value);
        }

        private void UpdateIndicator()
        {
            if (_gameState == GameState.PressRegistered || _gameState == GameState.Complete)
                return;

            _indicatorPosition += _indicatorDirection * _indicatorSpeed * Time.deltaTime;

            if (_indicatorPosition >= 1f)
            {
                _indicatorPosition = 1f;
                _indicatorDirection = -1f;
            }
            else if (_indicatorPosition <= 0f)
            {
                _indicatorPosition = 0f;
                _indicatorDirection = 1f;
            }
        }

        private void HandleInput()
        {
            if (_gameState != GameState.WaitingForPress)
                return;

            if (IsTriggerPressed())
            {
                OnPlayerPressed();
            }
        }

        private bool IsTriggerPressed()
        {
            if (InputManager.Instance == null)
            {
                return false;
            }

            switch (_triggerKey)
            {
                case KeyCode.E:
                    return InputManager.Instance.IsInteractPressed();
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                    return InputManager.Instance.IsSprintPressed();
                case KeyCode.C:
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                    return InputManager.Instance.IsCrouchPressed();
                case KeyCode.Escape:
                    return InputManager.Instance.IsPausePressed();
                case KeyCode.Space:
                default:
                    return InputManager.Instance.IsJumpPressed();
            }
        }

        private void OnPlayerPressed()
        {
            _gameState = GameState.PressRegistered;
            float distanceFromCenter = Mathf.Abs(_indicatorPosition - _targetZoneCenter);
            float zoneHalfWidth = Mathf.Max(0.0001f, _targetZoneWidth * 0.5f);
            bool isInZone = distanceFromCenter <= zoneHalfWidth;

            _accuracy = isInZone ? 1f - (distanceFromCenter / zoneHalfWidth) : 0f;

            if (_accuracy > 0f)
            {
                _hasSucceeded = true;
                FinalizeResultOrShowFollowUp(MinigameResult.Pass);
            }
            else
            {
                _attemptsRemaining--;

                if (_attemptsRemaining > 0)
                {
                    ResetForNextAttempt();
                }
                else
                {
                    FinalizeResultOrShowFollowUp(MinigameResult.Fail);
                }
            }
        }

        private void FinalizeResultOrShowFollowUp(MinigameResult result)
        {
            _pendingResult = result;
            _postResolutionFollowUpText = string.Empty;
            _postResultQuestionText = string.Empty;
            _postResultAnswerOptions.Clear();

            if (TryEnterPostResultDialogue(result))
            {
                return;
            }

            _postResolutionFollowUpText = ResolvePostResolutionFollowUp(result == MinigameResult.Pass);

            if (_requireFollowUpConfirm && !string.IsNullOrWhiteSpace(_postResolutionFollowUpText))
            {
                _isInputEnabled = false;
                _gameState = GameState.ShowingFollowUp;
                return;
            }

            CompletePendingResult();
        }

        private bool TryEnterPostResultDialogue(MinigameResult result)
        {
            LieDialogueSet dialogueSet = ResolveDialogueSetParameter();
            if (dialogueSet == null)
            {
                return false;
            }

            bool passed = result == MinigameResult.Pass;
            LieDialogueStep initialStep = ResolveInitialPostResultStep(dialogueSet, passed);
            if (initialStep == null)
            {
                return false;
            }

            return TryPresentPostResultStep(initialStep);
        }

        private LieDialogueStep ResolveInitialPostResultStep(LieDialogueSet dialogueSet, bool passed)
        {
            if (!string.IsNullOrWhiteSpace(_selectedAnswerNextStepId))
            {
                LieDialogueStep selectedAnswerStep = dialogueSet.GetStepById(_selectedAnswerNextStepId);
                if (selectedAnswerStep != null)
                {
                    return selectedAnswerStep;
                }
            }

            return dialogueSet.GetStepByResult(passed);
        }

        private bool TryPresentPostResultStep(LieDialogueStep step)
        {
            if (step == null)
            {
                return false;
            }

            _postResultQuestionText = step.PromptText ?? string.Empty;
            _postResultAnswerOptions.Clear();

            int answerLimit = Mathf.Min(3, step.Answers.Count);
            for (int i = 0; i < answerLimit; i++)
            {
                LieDialogueAnswer answer = step.Answers[i];
                if (answer == null)
                {
                    continue;
                }

                string fallbackId = $"post_result_answer_{i + 1}";
                string answerId = string.IsNullOrWhiteSpace(answer.AnswerId) ? fallbackId : answer.AnswerId;
                string answerText = string.IsNullOrWhiteSpace(answer.AnswerText) ? $"Continue {i + 1}" : answer.AnswerText;

                _postResultAnswerOptions.Add(new AnswerOptionConfig
                {
                    Id = answerId,
                    Text = answerText,
                    ZoneWidthMultiplier = Mathf.Clamp(answer.ZoneWidthMultiplier, 0.1f, 2f),
                    FollowUpText = answer.FollowUpText ?? string.Empty,
                    BranchHook = answer.BranchHook ?? string.Empty,
                    NextStepId = answer.NextStepId ?? string.Empty
                });
            }

            if (_postResultAnswerOptions.Count > 0)
            {
                _isInputEnabled = false;
                _gameState = GameState.WaitingForPostResultAnswer;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_postResultQuestionText))
            {
                _postResolutionFollowUpText = _postResultQuestionText;
                if (_requireFollowUpConfirm)
                {
                    _isInputEnabled = false;
                    _gameState = GameState.ShowingFollowUp;
                    return true;
                }
            }

            return false;
        }

        private void CompletePendingResult()
        {
            if (_pendingResult == MinigameResult.None)
            {
                return;
            }

            MinigameResult result = _pendingResult;
            _pendingResult = MinigameResult.None;
            _pendingFollowUpConfirmRequest = false;
            _gameState = GameState.Complete;
            SetResult(result);
        }

        private bool ConsumeFollowUpConfirmRequest()
        {
            if (!_pendingFollowUpConfirmRequest)
            {
                return false;
            }

            _pendingFollowUpConfirmRequest = false;
            return true;
        }

        private string ResolvePostResolutionFollowUp(bool passed)
        {
            LieDialogueSet dialogueSet = ResolveDialogueSetParameter();
            if (dialogueSet == null)
            {
                return _selectedAnswerFollowUpText;
            }

            if (!string.IsNullOrWhiteSpace(_selectedAnswerNextStepId))
            {
                LieDialogueStep nextStep = dialogueSet.GetStepById(_selectedAnswerNextStepId);
                if (nextStep != null && !string.IsNullOrWhiteSpace(nextStep.PromptText))
                {
                    return nextStep.PromptText;
                }
            }

            LieDialogueStep resolvedStep = dialogueSet.GetStepByResult(passed);
            if (resolvedStep != null && !string.IsNullOrWhiteSpace(resolvedStep.PromptText))
            {
                return resolvedStep.PromptText;
            }

            if (passed && !string.IsNullOrWhiteSpace(dialogueSet.DefaultPostPassLine))
            {
                return dialogueSet.DefaultPostPassLine;
            }

            if (!passed && !string.IsNullOrWhiteSpace(dialogueSet.DefaultPostFailLine))
            {
                return dialogueSet.DefaultPostFailLine;
            }

            return _selectedAnswerFollowUpText;
        }

        private void ResetForNextAttempt()
        {
            _gameState = GameState.WaitingForPress;
            _indicatorPosition = 0f;
            _indicatorDirection = 1f;
        }

        private void EnsureUI()
        {
            if (!EnsureEventSystemExists())
            {
                return;
            }

            if (_uiCanvas == null)
            {
                GameObject canvasObject = new GameObject("LieMinigameCanvas");
                _uiCanvas = canvasObject.AddComponent<Canvas>();
                _uiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _uiCanvas.sortingOrder = UiSortingOrder;

                CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;

                canvasObject.AddComponent<GraphicRaycaster>();
            }

            if (_uiRoot == null)
            {
                _uiRoot = new GameObject("LieGameplayPanel");
                _uiRoot.transform.SetParent(_uiCanvas.transform, false);

                RectTransform panelRect = _uiRoot.AddComponent<RectTransform>();
                panelRect.anchorMin = Vector2.zero;
                panelRect.anchorMax = Vector2.one;
                panelRect.offsetMin = Vector2.zero;
                panelRect.offsetMax = Vector2.zero;
                panelRect.anchoredPosition = Vector2.zero;

                Image background = _uiRoot.AddComponent<Image>();
                background.color = new Color(0f, 0f, 0f, 0.12f);
                background.raycastTarget = false;

                _uiController = _uiRoot.AddComponent<LieMinigameUI>();
            }

            if (_uiController == null)
            {
                _uiController = _uiRoot.GetComponent<LieMinigameUI>();
            }

            if (_uiController != null)
            {
                _uiController.Initialize(this, _uiCanvas);
            }
        }

        private bool EnsureEventSystemExists()
        {
            if (EventSystem.current != null)
            {
                return true;
            }

            EventSystem sceneEventSystem = Object.FindAnyObjectByType<EventSystem>();
            if (sceneEventSystem != null)
            {
                return true;
            }

            if (!_hasLoggedMissingEventSystem)
            {
                _hasLoggedMissingEventSystem = true;
                Debug.LogError(
                    "[LieMinigame] Missing required EventSystem in scene. " +
                    "Add EventSystem + InputSystemUIInputModule to scene setup.",
                    this);
            }

            SetResult(MinigameResult.Fail);
            return false;
        }

        private void CleanupUI()
        {
            if (_uiCanvas != null)
            {
                Object.Destroy(_uiCanvas.gameObject);
            }

            _uiCanvas = null;
            _uiRoot = null;
            _uiController = null;
        }

        public bool IsWaitingForAnswerSelection()
        {
            return _gameState == GameState.WaitingForAnswer;
        }

        public bool IsWaitingForPostResultAnswerSelection()
        {
            return _gameState == GameState.WaitingForPostResultAnswer;
        }

        public bool IsShowingFollowUp()
        {
            return _gameState == GameState.ShowingFollowUp;
        }

        public bool SelectPostResultAnswer(int answerIndex)
        {
            if (_gameState != GameState.WaitingForPostResultAnswer)
            {
                return false;
            }

            if (answerIndex < 0 || answerIndex >= _postResultAnswerOptions.Count)
            {
                return false;
            }

            AnswerOptionConfig selected = _postResultAnswerOptions[answerIndex];
            _postResultAnswerOptions.Clear();

            LieDialogueSet dialogueSet = ResolveDialogueSetParameter();
            if (dialogueSet != null && !string.IsNullOrWhiteSpace(selected.NextStepId))
            {
                LieDialogueStep nextStep = dialogueSet.GetStepById(selected.NextStepId);
                if (nextStep != null)
                {
                    if (TryPresentPostResultStep(nextStep))
                    {
                        return true;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(_postResolutionFollowUpText) && !string.IsNullOrWhiteSpace(selected.FollowUpText))
            {
                _postResolutionFollowUpText = selected.FollowUpText;
                if (_requireFollowUpConfirm)
                {
                    _gameState = GameState.ShowingFollowUp;
                    return true;
                }
            }

            if (string.IsNullOrWhiteSpace(_postResolutionFollowUpText))
            {
                _postResolutionFollowUpText = ResolvePostResolutionFollowUp(_pendingResult == MinigameResult.Pass);
            }

            if (_requireFollowUpConfirm && !string.IsNullOrWhiteSpace(_postResolutionFollowUpText))
            {
                _gameState = GameState.ShowingFollowUp;
                return true;
            }

            CompletePendingResult();
            return true;
        }

        public bool ConfirmFollowUp()
        {
            if (_gameState != GameState.ShowingFollowUp || _pendingResult == MinigameResult.None)
            {
                return false;
            }

            CompletePendingResult();
            return true;
        }

        private void AcquireMinigameCursorAuthority()
        {
            if (PlayerContextLocator.TryAcquireLocalCursorAuthority("minigame_lie", CursorLockMode.None, true))
            {
                _usesPresentationCursorAuthority = true;
                return;
            }

            _usesPresentationCursorAuthority = false;
            _previousCursorLockMode = Cursor.lockState;
            _previousCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void ReleaseMinigameCursorAuthority()
        {
            if (_usesPresentationCursorAuthority)
            {
                PlayerContextLocator.TryReleaseLocalCursorAuthority("minigame_lie");
                _usesPresentationCursorAuthority = false;
                return;
            }

            Cursor.lockState = _previousCursorLockMode;
            Cursor.visible = _previousCursorVisible;
        }

        public bool RequestFollowUpConfirm()
        {
            if (_gameState != GameState.ShowingFollowUp || _pendingResult == MinigameResult.None)
            {
                return false;
            }

            _pendingFollowUpConfirmRequest = true;
            return true;
        }

        public string GetQuestionText()
        {
            if (_gameState == GameState.ShowingFollowUp && !string.IsNullOrWhiteSpace(_postResolutionFollowUpText))
            {
                return _postResolutionFollowUpText;
            }

            if (_gameState == GameState.WaitingForPostResultAnswer && !string.IsNullOrWhiteSpace(_postResultQuestionText))
            {
                return _postResultQuestionText;
            }

            return _questionText;
        }

        public int GetPostResultAnswerCount()
        {
            return _postResultAnswerOptions.Count;
        }

        public string GetPostResultAnswerText(int answerIndex)
        {
            if (answerIndex < 0 || answerIndex >= _postResultAnswerOptions.Count)
            {
                return string.Empty;
            }

            return _postResultAnswerOptions[answerIndex].Text;
        }

        public int GetAnswerCount()
        {
            return _answerOptions.Count;
        }

        public string GetAnswerText(int answerIndex)
        {
            if (answerIndex < 0 || answerIndex >= _answerOptions.Count)
            {
                return string.Empty;
            }

            return _answerOptions[answerIndex].Text;
        }

        public int GetSelectedAnswerIndex()
        {
            return _selectedAnswerIndex;
        }

        public bool HasSelectedAnswer()
        {
            return _hasSelectedAnswer;
        }

        public float GetSelectedAnswerSuspicionMultiplier()
        {
            return _selectedAnswerSuspicionMultiplier;
        }

        public string GetSelectedAnswerFollowUpText()
        {
            return _selectedAnswerFollowUpText;
        }

        public string GetSelectedAnswerBranchHook()
        {
            return _selectedAnswerBranchHook;
        }

        public string GetSelectedAnswerNextStepId()
        {
            return _selectedAnswerNextStepId;
        }

        public string GetPostResolutionFollowUpText()
        {
            return _postResolutionFollowUpText;
        }

        public float GetTargetZoneStart()
        {
            return _targetZoneCenter - (_targetZoneWidth * 0.5f);
        }

        public float GetTargetZoneEnd()
        {
            return _targetZoneCenter + (_targetZoneWidth * 0.5f);
        }

        public bool IsIndicatorInZone()
        {
            if (!_showTargetZone)
                return false;

            float distanceFromCenter = Mathf.Abs(_indicatorPosition - _targetZoneCenter);
            return distanceFromCenter <= (_targetZoneWidth * 0.5f);
        }

        public float GetIndicatorPosition()
        {
            return _indicatorPosition;
        }

        public float GetTargetZoneCenter()
        {
            return _targetZoneCenter;
        }

        public float GetTargetZoneWidth()
        {
            return _targetZoneWidth;
        }

        public float GetAccuracy()
        {
            return _accuracy;
        }

        public int GetAttemptsRemaining()
        {
            return _attemptsRemaining;
        }

        public int GetMaxAttempts()
        {
            return _maxAttempts;
        }

        public string GetTriggerKeyDisplayName()
        {
            return _triggerKeyLabel;
        }

        public bool HasSucceeded()
        {
            return _hasSucceeded;
        }

        private void OnDrawGizmosSelected()
        {
            float zoneStart = GetTargetZoneStart();
            float zoneEnd = GetTargetZoneEnd();

            Gizmos.color = Color.green;
            Gizmos.DrawLine(new Vector3(zoneStart * 10, 0, 0), new Vector3(zoneEnd * 10, 0, 0));
            Gizmos.DrawWireCube(new Vector3((zoneStart + zoneEnd) * 5, 0, 0), new Vector3((zoneEnd - zoneStart) * 10, 0.5f, 0.5f));

            Gizmos.color = Color.red;
            float indicatorX = _indicatorPosition * 10;
            Gizmos.DrawLine(new Vector3(indicatorX, -1, 0), new Vector3(indicatorX, 1, 0));
        }
    }
}
