using UnityEngine;
using System.Collections.Generic;
using Game.Inventory;
using Game.Minigames;

namespace Game.Core
{
    /// <summary>
    /// Objective represents a task or goal for the player to complete.
    /// 
    /// Features:
    /// - Define minigame completion requirements (which minigames, how many times, pass/fail)
    /// - Track reward currency and inventory items
    /// - Can be created as ScriptableObject assets for data-driven objectives
    /// 
    /// Usage:
    /// 1. Create a new Objective asset via Create > Game > Objective
    /// 2. Set minigame requirements and rewards
    /// 3. Reference in ObjectiveManager or hardcode in minigame start sequences
    /// 
    /// Example:
    /// "Complete 3 Welding minigames" →
    ///   minigameId = "welding"
    ///   requiredCompletions = 3
    ///   rewardCurrency = 100
    /// </summary>
    [CreateAssetMenu(fileName = "NewObjective", menuName = "Game/Objective")]
    public class Objective : ScriptableObject
    {
        [SerializeField]
        private string _objectiveId = "objective_default";

        [SerializeField]
        private string _objectiveName = "Objective";

        [SerializeField]
        [TextArea(2, 4)]
        private string _objectiveDescription = "Complete this objective.";

        [SerializeField]
        private ObjectiveType _objectiveType = ObjectiveType.Quest;

        [SerializeField]
        private string _targetMinigameId = "";

        [SerializeField]
        [Range(1, 10)]
        private int _requiredCompletions = 1;

        [SerializeField]
        private bool _countPassOnly = true;

        [SerializeField]
        private int _rewardCurrency = 0;

        [SerializeField]
        private List<InventoryItem> _rewardItems = new List<InventoryItem>();

        [SerializeField]
        private bool _isActive = true;

        // Runtime state
        private int _completionCount = 0;
        private bool _isCompleted = false;

        public string ObjectiveId => _objectiveId;
        public string ObjectiveName => _objectiveName;
        public string ObjectiveDescription => _objectiveDescription;
        public ObjectiveType Type => _objectiveType;
        public string TargetMinigameId => _targetMinigameId;
        public int RequiredCompletions => _requiredCompletions;
        public bool CountPassOnly => _countPassOnly;
        public int RewardCurrency => _rewardCurrency;
        public List<InventoryItem> RewardItems => _rewardItems;
        public bool IsActive => _isActive;

        public int CompletionCount => _completionCount;
        public bool IsCompleted => _isCompleted;

        public float ProgressPercent => _requiredCompletions > 0 
            ? Mathf.Clamp01((float)_completionCount / _requiredCompletions) 
            : 0f;

        /// <summary>
        /// Reset objective completion state (for restarting).
        /// </summary>
        public void Reset()
        {
            _completionCount = 0;
            _isCompleted = false;
        }

        /// <summary>
        /// Increment completion count. Returns true if objective just completed.
        /// </summary>
        public bool IncrementProgress()
        {
            if (_isCompleted)
                return false;

            _completionCount++;

            if (_completionCount >= _requiredCompletions)
            {
                _isCompleted = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check if this objective should count the given minigame completion.
        /// </summary>
        public bool ShouldCountMinigame(string minigameId, MinigameResult result)
        {
            if (!_isActive || _isCompleted)
                return false;

            if (minigameId != _targetMinigameId)
                return false;

            if (_countPassOnly && result != MinigameResult.Pass)
                return false;

            return true;
        }

        public override string ToString()
        {
            return $"[{_objectiveId}] {_objectiveName} ({_completionCount}/{_requiredCompletions})";
        }
    }

    public enum ObjectiveType
    {
        Quest,
        Progression,
        Challenge
    }
}
