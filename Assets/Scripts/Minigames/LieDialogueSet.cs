using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Minigames
{
    [CreateAssetMenu(fileName = "LieDialogueSet", menuName = "Game/Minigames/Lie Dialogue Set")]
    public class LieDialogueSet : ScriptableObject
    {
        [SerializeField] private string _dialogueSetId = "default";
        [SerializeField] private float _suspicionPenaltyPerRepeat = 0.08f;
        [SerializeField] private float _suspicionMinMultiplier = 0.6f;
        [SerializeField] private string _defaultResultPassStepId = string.Empty;
        [SerializeField] private string _defaultResultFailStepId = string.Empty;
        [SerializeField, TextArea(2, 4)] private string _defaultPostPassLine = string.Empty;
        [SerializeField, TextArea(2, 4)] private string _defaultPostFailLine = string.Empty;
        [SerializeField] private List<LieDialogueStep> _steps = new List<LieDialogueStep>
        {
            LieDialogueStep.CreateDefaultIntroStep(),
            LieDialogueStep.CreateDefaultPassStep(),
            LieDialogueStep.CreateDefaultFailStep()
        };

        public string DialogueSetId => _dialogueSetId;
        public float SuspicionPenaltyPerRepeat => Mathf.Max(0f, _suspicionPenaltyPerRepeat);
        public float SuspicionMinMultiplier => Mathf.Clamp(_suspicionMinMultiplier, 0.1f, 1f);
        public string DefaultPostPassLine => _defaultPostPassLine;
        public string DefaultPostFailLine => _defaultPostFailLine;
        public IReadOnlyList<LieDialogueStep> Steps => _steps;

        public LieDialogueStep GetFirstStep()
        {
            if (_steps == null || _steps.Count == 0)
            {
                return null;
            }

            return _steps[0];
        }

        public LieDialogueStep GetStepById(string stepId)
        {
            if (string.IsNullOrWhiteSpace(stepId) || _steps == null)
            {
                return null;
            }

            for (int i = 0; i < _steps.Count; i++)
            {
                LieDialogueStep step = _steps[i];
                if (step != null && string.Equals(step.StepId, stepId, StringComparison.OrdinalIgnoreCase))
                {
                    return step;
                }
            }

            return null;
        }

        public LieDialogueStep GetStepByResult(bool passed)
        {
            string stepId = passed ? _defaultResultPassStepId : _defaultResultFailStepId;
            if (string.IsNullOrWhiteSpace(stepId))
            {
                return null;
            }

            return GetStepById(stepId);
        }
    }

    [Serializable]
    public class LieDialogueStep
    {
        [SerializeField] private string _stepId = "intro";
        [SerializeField, TextArea(2, 5)] private string _promptText = "Hey! What are you doing in here?!";
        [SerializeField] private List<LieDialogueAnswer> _answers = new List<LieDialogueAnswer>
        {
            new LieDialogueAnswer("excuse_bathroom", "I'm just going to the bathroom.", 1.2f),
            new LieDialogueAnswer("excuse_find_boss", "I'm looking for the boss, can you tell me where he is?", 1f),
            new LieDialogueAnswer("excuse_passing_by", "I'm just passing by.", 0.8f)
        };

        public string StepId => _stepId;
        public string PromptText => _promptText;
        public IReadOnlyList<LieDialogueAnswer> Answers => _answers;

        public static LieDialogueStep CreateDefaultIntroStep()
        {
            return new LieDialogueStep
            {
                _stepId = "intro",
                _promptText = "Hey! What are you doing in here?!",
                _answers = new List<LieDialogueAnswer>
                {
                    new LieDialogueAnswer("excuse_bathroom", "I'm just going to the bathroom.", 1.2f)
                    {
                        FollowUpText = "Alright, make it quick.",
                        BranchHook = "bathroom",
                        NextStepId = "after_pass"
                    },
                    new LieDialogueAnswer("excuse_find_boss", "I'm looking for the boss, can you tell me where he is?", 1f)
                    {
                        FollowUpText = "The boss is busy. Move along.",
                        BranchHook = "find_boss",
                        NextStepId = "after_pass"
                    },
                    new LieDialogueAnswer("excuse_passing_by", "I'm just passing by.", 0.8f)
                    {
                        FollowUpText = "Then keep moving.",
                        BranchHook = "passing_by",
                        NextStepId = "after_pass"
                    }
                }
            };
        }

        public static LieDialogueStep CreateDefaultPassStep()
        {
            return new LieDialogueStep
            {
                _stepId = "after_pass",
                _promptText = "Fine. Don't cause trouble.",
                _answers = new List<LieDialogueAnswer>()
            };
        }

        public static LieDialogueStep CreateDefaultFailStep()
        {
            return new LieDialogueStep
            {
                _stepId = "after_fail",
                _promptText = "I don't believe you. You're coming with me.",
                _answers = new List<LieDialogueAnswer>()
            };
        }
    }

    [Serializable]
    public class LieDialogueAnswer
    {
        [SerializeField] private string _answerId = "answer";
        [SerializeField, TextArea(1, 3)] private string _answerText = "Answer";
        [SerializeField] private float _zoneWidthMultiplier = 1f;
        [SerializeField, TextArea(1, 3)] private string _followUpText = string.Empty;
        [SerializeField] private string _branchHook = string.Empty;
        [SerializeField] private string _nextStepId = string.Empty;

        public string AnswerId => _answerId;
        public string AnswerText => _answerText;
        public float ZoneWidthMultiplier => _zoneWidthMultiplier;
        public string FollowUpText
        {
            get => _followUpText;
            set => _followUpText = value;
        }

        public string BranchHook
        {
            get => _branchHook;
            set => _branchHook = value;
        }

        public string NextStepId
        {
            get => _nextStepId;
            set => _nextStepId = value;
        }

        public LieDialogueAnswer(string answerId, string answerText, float zoneWidthMultiplier)
        {
            _answerId = answerId;
            _answerText = answerText;
            _zoneWidthMultiplier = zoneWidthMultiplier;
        }
    }
}
