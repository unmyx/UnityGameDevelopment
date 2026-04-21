using UnityEngine;
using System.Collections.Generic;

namespace Game.Core
{
    /// <summary>
    /// SaveData is the root JSON-serializable object containing all persistent game state.
    ///
    /// Structure:
    /// - Currency (int)
    /// - Owned tools (toolId → tier mapping)
    /// - Inventory grid (list of items with grid positions)
    /// - Objectives (completed IDs + active progress)
    ///
    /// Design:
    /// - All nested classes are [System.Serializable] for JsonUtility
    /// - Dictionaries wrapped in List<KeyValuePair> for JSON compatibility
    /// - ScriptableObject references stored as ID strings (resolved on load)
    /// - NO MonoBehaviours or direct asset references in this structure
    ///
    /// Usage:
    /// SaveData data = SaveManager.Load();
    /// SaveManager.Save(data);
    /// </summary>
    [System.Serializable]
    public class SaveData
    {
        public int currency;
        public int dayWorkEarnings;
        public int currentDay = 1;
        public int currentRunPhase;
        public bool workdayCompleted;
        public float currentWorkHour = 7f;
        public int nextTaskWaveIndex;
        public int consecutiveFailedWorkdays;
        public bool runFailed;
        public string runFailedReason;
        public int failedLieEscalationCountThisDay;
        public bool hasPlayerTransform;
        public Vector3 playerPosition;
        public Quaternion playerRotation;
        public int selectedInventorySlotIndex;
        public List<ToolDataEntry> ownedTools = new List<ToolDataEntry>();
        public List<InventorySlotData> inventory = new List<InventorySlotData>();
        public ObjectivesSaveData objectives = new ObjectivesSaveData();
        public List<string> consumedCollectibleIds = new List<string>();
        public List<DailyTaskAssignmentData> dailyTaskAssignments = new List<DailyTaskAssignmentData>();
        public List<GeneratedTaskWaveData> generatedTaskWaves = new List<GeneratedTaskWaveData>();
        public List<string> unlockedTaskKeys = new List<string>();
        public List<StolenLootEntryData> stolenLootThisDay = new List<StolenLootEntryData>();
    }

    [System.Serializable]
    public class ToolDataEntry
    {
        public string toolId;
        public int tier;

        public ToolDataEntry(string toolId, int tier)
        {
            this.toolId = toolId;
            this.tier = tier;
        }
    }

    [System.Serializable]
    public class DailyTaskAssignmentData
    {
        public string taskType;
        public string taskKey;
        public bool isCompleted;
    }

    [System.Serializable]
    public class StolenLootEntryData
    {
        public string itemId;
        public int count;
    }

    [System.Serializable]
    public class GeneratedTaskWaveData
    {
        public float unlockHour;
        public List<string> taskKeys = new List<string>();
    }

    [System.Serializable]
    public class ObjectivesSaveData
    {
        public List<string> completedObjectiveIds = new List<string>();
        public List<ObjectiveProgressData> activeObjectives = new List<ObjectiveProgressData>();
        public bool isMissionComplete;
    }

    [System.Serializable]
    public class ObjectiveProgressData
    {
        public string objectiveId;
        public int currentProgress;

        public ObjectiveProgressData(string objectiveId, int progress)
        {
            this.objectiveId = objectiveId;
            this.currentProgress = progress;
        }
    }
}