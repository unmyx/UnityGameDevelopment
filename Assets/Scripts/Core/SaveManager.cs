using UnityEngine;
using System.Collections.Generic;
using System;
using Game.Inventory;
using Game.Interaction;
using Game.Minigames;

namespace Game.Core
{
    /// <summary>
    /// SaveManager provides static Save() and Load() methods for persistent game data.
    ///
    /// Features:
    /// - Serializes GameManager, InventorySystem, ObjectiveManager state to JSON
    /// - Stores file in Application.persistentDataPath (works on all platforms)
    /// - Handles asset reference loading via ScriptableObject IDs
    /// - No MonoBehaviour required - pure static utility
    ///
    /// Usage:
    /// SaveManager.Save();     // Serializes all managers to JSON file
    /// SaveManager.Load();     // Deserializes and restores all managers
    ///
    /// File Location:
    /// Application.persistentDataPath + "/gamenamenf_save.json"
    ///
    /// Integration:
    /// - Call Save() from pause menu "Save Game" button
    /// - Call Load() from pause menu "Continue Game" button or main menu "Load" button
    /// - Or call Save() on objective completion for auto-save
    /// </summary>
    public static class SaveManager
    {
        public readonly struct SaveContext
        {
            public SaveContext(GameManager gameManager, ObjectiveManager objectiveManager, InventorySystem inventorySystem)
            {
                GameManager = gameManager;
                ObjectiveManager = objectiveManager;
                InventorySystem = inventorySystem;
            }

            public GameManager GameManager { get; }
            public ObjectiveManager ObjectiveManager { get; }
            public InventorySystem InventorySystem { get; }
        }

        private static readonly HashSet<string> ConsumedCollectibleIds = new HashSet<string>(StringComparer.Ordinal);

        private static string SaveFilePath
        {
            get { return System.IO.Path.Combine(Application.persistentDataPath, "gamenamenf_save.json"); }
        }

        public static bool IsCollectibleConsumed(string collectibleId)
        {
            if (!TryNormalizeCollectibleId(collectibleId, out string normalizedCollectibleId))
            {
                return false;
            }

            return ConsumedCollectibleIds.Contains(normalizedCollectibleId);
        }

        public static void RegisterConsumedCollectible(string collectibleId)
        {
            if (!TryNormalizeCollectibleId(collectibleId, out string normalizedCollectibleId))
            {
                return;
            }

            ConsumedCollectibleIds.Add(normalizedCollectibleId);
        }

        /// <summary>
        /// Save all persistent game state to JSON file.
        /// Reads from GameManager, InventorySystem, ObjectiveManager via public accessors.
        /// </summary>
        public static void Save()
        {
            if (!TryBuildLegacyContext("Save", out SaveContext context, out string failureReason))
            {
                Debug.LogError($"[SaveManager] {failureReason}");
                return;
            }

            Save(context);
        }

        public static bool Save(SaveContext context)
        {
            try
            {
                if (!TryValidateContext(context, "Save", out string failureReason))
                {
                    Debug.LogError($"[SaveManager] {failureReason}");
                    return false;
                }

                SaveData data = new SaveData();

                // Currency remains persisted from GameManager.
                data.currency = context.GameManager.GetCurrency();
                data.dayWorkEarnings = context.GameManager.GetDayWorkEarnings();
                data.currentDay = context.GameManager.GetCurrentDay();
                data.currentRunPhase = (int)context.GameManager.GetCurrentRunPhase();
                data.workdayCompleted = context.GameManager.IsWorkdayCompleted();
                data.consecutiveFailedWorkdays = context.GameManager.GetConsecutiveFailedWorkdays();
                data.runFailed = context.GameManager.IsRunFailed();
                data.runFailedReason = context.GameManager.GetRunFailedReason();
                data.failedLieEscalationCountThisDay = context.GameManager.GetFailedLieEscalationCountThisDay();
                data.dailyTaskAssignments = context.GameManager.GetDailyTaskAssignmentsForSave();
                data.stolenLootThisDay = context.GameManager.GetStolenLootThisDaySnapshot();

                if (context.GameManager.TryGetAuthoritativePlayerTransform(out Transform playerTransform) && playerTransform != null)
                {
                    data.hasPlayerTransform = true;
                    data.playerPosition = playerTransform.position;
                    data.playerRotation = playerTransform.rotation;
                }

                data.selectedInventorySlotIndex = context.GameManager.GetSelectedInventorySlotIndexForSave();
                data.ownedTools = context.GameManager.GetOwnedToolUpgradesForSave();

                // Gather inventory state from InventorySystem
                InventorySlotData[] inventorySnapshot = context.InventorySystem.GetInventorySnapshot();
                foreach (var slot in inventorySnapshot)
                {
                    if (slot != null && !string.IsNullOrEmpty(slot.itemId))
                    {
                        data.inventory.Add(slot);
                    }
                }

                // Gather objectives from ObjectiveManager
                ObjectiveStates objectiveStates = context.ObjectiveManager.GetObjectiveStates();
                foreach (var objectiveId in objectiveStates.completedObjectiveIds)
                {
                    data.objectives.completedObjectiveIds.Add(objectiveId);
                }
                foreach (var activeObj in objectiveStates.activeObjectives)
                {
                    data.objectives.activeObjectives.Add(new ObjectiveProgressData(activeObj.objectiveId, activeObj.currentProgress));
                }
                data.objectives.isMissionComplete = objectiveStates.isMissionComplete;

                List<string> consumedCollectibles = new List<string>(ConsumedCollectibleIds);
                consumedCollectibles.Sort(StringComparer.Ordinal);
                data.consumedCollectibleIds.AddRange(consumedCollectibles);

                // Serialize to JSON
                string json = JsonUtility.ToJson(data, true);

                // Write to file
                System.IO.File.WriteAllText(SaveFilePath, json);

                Debug.Log($"<color=green>Game saved successfully!</color> File: {SaveFilePath}");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"<color=red>Failed to save game!</color> Error: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Load all persistent game state from JSON file.
        /// Restores GameManager, InventorySystem, ObjectiveManager via public restore methods.
        /// Returns true if load successful, false if file doesn't exist or error occurs.
        /// </summary>
        public static bool Load()
        {
            if (!TryBuildLegacyContext("Load", out SaveContext context, out string failureReason))
            {
                Debug.LogError($"[SaveManager] {failureReason}");
                return false;
            }

            return Load(context);
        }

        public static bool Load(SaveContext context)
        {
            try
            {
                if (!TryValidateContext(context, "Load", out string failureReason))
                {
                    Debug.LogError($"[SaveManager] {failureReason}");
                    return false;
                }

                if (!System.IO.File.Exists(SaveFilePath))
                {
                    Debug.LogWarning($"<color=yellow>Save file not found:</color> {SaveFilePath}");
                    return false;
                }

                string json = System.IO.File.ReadAllText(SaveFilePath);
                SaveData data = JsonUtility.FromJson<SaveData>(json);

                if (data == null)
                {
                    Debug.LogError("<color=red>Failed to deserialize save file!</color>");
                    return false;
                }

                // Restore runtime currency owned by GameManager.
                context.GameManager.RestoreCurrencyFromSave(data.currency);
                context.GameManager.RestoreDayWorkEarningsFromSave(data.dayWorkEarnings);
                context.GameManager.RestoreRunProgressFromSave(
                    data.currentDay,
                    data.currentRunPhase,
                    data.workdayCompleted,
                    data.consecutiveFailedWorkdays,
                    data.runFailed,
                    data.runFailedReason);
                context.GameManager.RestoreFailedLieEscalationCountThisDayFromSave(data.failedLieEscalationCountThisDay);
                context.GameManager.RestoreDailyTaskAssignmentsFromSave(data.dailyTaskAssignments);
                context.GameManager.RestoreStolenLootThisDayFromSave(data.stolenLootThisDay);
                context.GameManager.RestoreOwnedToolUpgradesFromSave(data.ownedTools);

                // Restore InventorySystem (grid state)
                context.InventorySystem.RestoreFromSave(data.inventory);

                // Restore ObjectiveManager (objectives and progress)
                context.ObjectiveManager.RestoreFromSave(data.objectives);
                context.ObjectiveManager.SyncAfterLoad();

                if (data.hasPlayerTransform)
                {
                    context.GameManager.RestorePlayerTransformFromSave(data.playerPosition, data.playerRotation);
                }

                context.GameManager.RestoreSelectedInventorySlotFromSave(data.selectedInventorySlotIndex);

                RestoreConsumedCollectibles(data.consumedCollectibleIds);
                ApplyConsumedCollectibleStateToScene();

                Debug.Log($"<color=green>Game loaded successfully!</color> File: {SaveFilePath}");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"<color=red>Failed to load game!</color> Error: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Read only run failure metadata from save without requiring gameplay dependencies.
        /// Returns true when metadata was read successfully.
        /// </summary>
        public static bool TryReadRunFailureMeta(out bool runFailed, out string reason)
        {
            runFailed = false;
            reason = string.Empty;

            try
            {
                if (!System.IO.File.Exists(SaveFilePath))
                {
                    return false;
                }

                string json = System.IO.File.ReadAllText(SaveFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return false;
                }

                SaveData data = JsonUtility.FromJson<SaveData>(json);
                if (data == null)
                {
                    return false;
                }

                runFailed = data.runFailed || data.currentRunPhase == (int)GameManager.RunPhase.GameOver;
                reason = string.IsNullOrWhiteSpace(data.runFailedReason)
                    ? string.Empty
                    : data.runFailedReason.Trim();

                if (!runFailed)
                {
                    reason = string.Empty;
                }

                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SaveManager] Failed to read run failure metadata: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clears static runtime caches that can leak state between runs.
        /// </summary>
        public static void ClearRuntimeCaches()
        {
            ConsumedCollectibleIds.Clear();
            ObjectiveManager.ClearRuntimeCaches();
            MinigameRewardSystem.ClearRuntimeCaches();
        }

        /// <summary>
        /// Delete the save file (for testing or "new game" functionality).
        /// </summary>
        public static void DeleteSave()
        {
            try
            {
                if (System.IO.File.Exists(SaveFilePath))
                {
                    System.IO.File.Delete(SaveFilePath);
                    Debug.Log("<color=yellow>Save file deleted.</color>");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"<color=red>Failed to delete save file!</color> Error: {e.Message}");
            }
        }

        /// <summary>
        /// Check if a save file exists.
        /// Useful for "Continue" button enabling/disabling in main menu.
        /// </summary>
        public static bool SaveFileExists()
        {
            return System.IO.File.Exists(SaveFilePath);
        }

        /// <summary>
        /// Get the full path where save file is stored (for debugging).
        /// </summary>
        public static string GetSaveFilePath()
        {
            return SaveFilePath;
        }

        private static void RestoreConsumedCollectibles(List<string> savedConsumedCollectibleIds)
        {
            ConsumedCollectibleIds.Clear();

            if (savedConsumedCollectibleIds == null || savedConsumedCollectibleIds.Count == 0)
            {
                return;
            }

            foreach (string collectibleId in savedConsumedCollectibleIds)
            {
                if (!TryNormalizeCollectibleId(collectibleId, out string normalizedCollectibleId))
                {
                    continue;
                }

                ConsumedCollectibleIds.Add(normalizedCollectibleId);
            }
        }

        private static void ApplyConsumedCollectibleStateToScene()
        {
            if (ConsumedCollectibleIds.Count == 0)
            {
                return;
            }

            InteractableItem[] interactableItems = UnityEngine.Object.FindObjectsByType<InteractableItem>(FindObjectsInactive.Include);
            for (int i = 0; i < interactableItems.Length; i++)
            {
                InteractableItem interactableItem = interactableItems[i];
                if (interactableItem == null)
                {
                    continue;
                }

                if (!interactableItem.TryGetPersistentCollectibleId(out string collectibleId))
                {
                    continue;
                }

                if (ConsumedCollectibleIds.Contains(collectibleId))
                {
                    interactableItem.ApplyConsumedPersistenceState();
                }
            }
        }

        private static bool TryNormalizeCollectibleId(string collectibleId, out string normalizedCollectibleId)
        {
            normalizedCollectibleId = string.IsNullOrWhiteSpace(collectibleId)
                ? null
                : collectibleId.Trim();

            return !string.IsNullOrEmpty(normalizedCollectibleId);
        }

        private static bool TryBuildLegacyContext(string operationName, out SaveContext context, out string failureReason)
        {
            GameManager gameManager = GameManager.Instance;
            bool hasObjectiveManager = ObjectiveManager.TryGetInstance(out ObjectiveManager objectiveManager);
            InventorySystem inventorySystem = InventorySystem.Instance;

            List<string> missing = new List<string>(3);
            if (gameManager == null)
            {
                missing.Add("GameManager");
            }

            if (!hasObjectiveManager || objectiveManager == null)
            {
                missing.Add("ObjectiveManager");
            }

            if (inventorySystem == null)
            {
                missing.Add("InventorySystem");
            }

            if (missing.Count > 0)
            {
                context = default;
                failureReason =
                    $"{operationName} failed because required dependencies are missing: {string.Join(", ", missing)}. " +
                    "Assign and initialize required managers before invoking SaveManager.";
                return false;
            }

            context = new SaveContext(gameManager, objectiveManager, inventorySystem);
            failureReason = string.Empty;
            return true;
        }

        private static bool TryValidateContext(SaveContext context, string operationName, out string failureReason)
        {
            List<string> missing = new List<string>(3);
            if (context.GameManager == null)
            {
                missing.Add("GameManager");
            }

            if (context.ObjectiveManager == null)
            {
                missing.Add("ObjectiveManager");
            }

            if (context.InventorySystem == null)
            {
                missing.Add("InventorySystem");
            }

            if (missing.Count == 0)
            {
                failureReason = string.Empty;
                return true;
            }

            failureReason =
                $"{operationName} failed because SaveContext is missing required references: {string.Join(", ", missing)}.";
            return false;
        }
    }
}
