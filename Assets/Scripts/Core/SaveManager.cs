using UnityEngine;
using System.Collections.Generic;
using System;
using Game.Inventory;
using Game.Interaction;
using Game.Minigames;
using Game.Player;
using Unity.Netcode;

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
                if (IsNonAuthoritativeNetworkClient())
                {
                    Debug.LogWarning("[SaveManager] Save skipped on non-authoritative network client.");
                    return false;
                }

                if (!TryValidateContext(context, "Save", out string failureReason))
                {
                    Debug.LogError($"[SaveManager] {failureReason}");
                    return false;
                }

                SaveData data = new SaveData();

                // Currency remains persisted from GameManager.
                WorldSaveState worldState = BuildWorldState(context);
                data.worldState = worldState;

                // Legacy flat fields remain populated for backward compatibility.
                data.currency = worldState.currency;
                data.dayWorkEarnings = worldState.dayWorkEarnings;
                data.currentDay = worldState.currentDay;
                data.currentRunPhase = worldState.currentRunPhase;
                data.workdayCompleted = worldState.workdayCompleted;
                data.currentWorkHour = worldState.currentWorkHour;
                data.nextTaskWaveIndex = worldState.nextTaskWaveIndex;
                data.consecutiveFailedWorkdays = worldState.consecutiveFailedWorkdays;
                data.runFailed = worldState.runFailed;
                data.runFailedReason = worldState.runFailedReason;
                data.failedLieEscalationCountThisDay = worldState.failedLieEscalationCountThisDay;
                data.dailyTaskAssignments = worldState.dailyTaskAssignments ?? new List<DailyTaskAssignmentData>();
                data.generatedTaskWaves = worldState.generatedTaskWaves ?? new List<GeneratedTaskWaveData>();
                data.unlockedTaskKeys = worldState.unlockedTaskKeys ?? new List<string>();
                data.stolenLootThisDay = worldState.stolenLootThisDay ?? new List<StolenLootEntryData>();
                data.ownedTools = worldState.ownedTools ?? new List<ToolDataEntry>();

                // Gather inventory state from InventorySystem
                InventorySlotData[] inventorySnapshot = context.InventorySystem.GetInventorySnapshot();
                foreach (var slot in inventorySnapshot)
                {
                    if (slot != null && !string.IsNullOrEmpty(slot.itemId))
                    {
                        data.inventory.Add(slot);
                    }
                }

                string localOwnerKey = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
                PlayerSaveState localPlayerState = new PlayerSaveState
                {
                    ownerKey = localOwnerKey,
                    selectedInventorySlotIndex = context.GameManager.GetSelectedInventorySlotIndexForSave(),
                    inventory = new List<InventorySlotData>(data.inventory)
                };

                if (context.GameManager.TryGetAuthoritativePlayerTransform(out Transform playerTransform) && playerTransform != null)
                {
                    localPlayerState.hasPlayerTransform = true;
                    localPlayerState.playerPosition = playerTransform.position;
                    localPlayerState.playerRotation = playerTransform.rotation;
                }

                data.playerStates.Clear();
                data.playerStates.Add(localPlayerState);

                // Legacy flat player fields remain populated for backward compatibility.
                data.hasPlayerTransform = localPlayerState.hasPlayerTransform;
                data.playerPosition = localPlayerState.playerPosition;
                data.playerRotation = localPlayerState.playerRotation;
                data.selectedInventorySlotIndex = localPlayerState.selectedInventorySlotIndex;

                // Gather objectives from ObjectiveManager
                ObjectiveStates objectiveStates = context.ObjectiveManager.GetObjectiveStates();
                foreach (var objectiveId in objectiveStates.completedObjectiveIds)
                {
                    worldState.objectives.completedObjectiveIds.Add(objectiveId);
                }
                foreach (var activeObj in objectiveStates.activeObjectives)
                {
                    worldState.objectives.activeObjectives.Add(new ObjectiveProgressData(activeObj.objectiveId, activeObj.currentProgress));
                }
                worldState.objectives.isMissionComplete = objectiveStates.isMissionComplete;
                data.objectives = worldState.objectives;

                List<string> consumedCollectibles = new List<string>(ConsumedCollectibleIds);
                consumedCollectibles.Sort(StringComparer.Ordinal);
                worldState.consumedCollectibleIds.AddRange(consumedCollectibles);
                data.consumedCollectibleIds = worldState.consumedCollectibleIds;

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
                if (IsNonAuthoritativeNetworkClient())
                {
                    Debug.LogWarning("[SaveManager] Load skipped on non-authoritative network client.");
                    return false;
                }

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

                WorldSaveState worldState = ResolveWorldState(data);
                PlayerSaveState localPlayerState = ResolveLocalPlayerState(data);

                // Restore runtime currency owned by GameManager.
                context.GameManager.RestoreCurrencyFromSave(worldState.currency);
                context.GameManager.RestoreDayWorkEarningsFromSave(worldState.dayWorkEarnings);
                context.GameManager.RestoreRunProgressFromSave(
                    worldState.currentDay,
                    worldState.currentRunPhase,
                    worldState.workdayCompleted,
                    worldState.consecutiveFailedWorkdays,
                    worldState.runFailed,
                    worldState.runFailedReason);
                context.GameManager.RestoreFailedLieEscalationCountThisDayFromSave(worldState.failedLieEscalationCountThisDay);
                context.GameManager.RestoreDailyTaskAssignmentsFromSave(worldState.dailyTaskAssignments);
                context.GameManager.RestoreWorkdayRuntimeFromSave(
                    worldState.currentWorkHour,
                    worldState.nextTaskWaveIndex,
                    worldState.generatedTaskWaves,
                    worldState.unlockedTaskKeys);
                context.GameManager.RestoreStolenLootThisDayFromSaveForAllOwners(worldState.stolenLootThisDay);
                context.GameManager.RestoreOwnedToolUpgradesFromSave(worldState.ownedTools);

                // Restore InventorySystem (grid state)
                context.InventorySystem.RestoreFromSave(localPlayerState.inventory);

                // Restore ObjectiveManager (objectives and progress)
                context.ObjectiveManager.RestoreFromSave(worldState.objectives);
                context.ObjectiveManager.SyncAfterLoad();

                if (localPlayerState.hasPlayerTransform)
                {
                    context.GameManager.RestorePlayerTransformFromSave(localPlayerState.playerPosition, localPlayerState.playerRotation);
                }

                context.GameManager.RestoreSelectedInventorySlotFromSave(localPlayerState.selectedInventorySlotIndex);

                RestoreConsumedCollectibles(worldState.consumedCollectibleIds);
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

                WorldSaveState worldState = ResolveWorldState(data);

                runFailed = worldState.runFailed || worldState.currentRunPhase == (int)GameManager.RunPhase.GameOver;
                reason = string.IsNullOrWhiteSpace(worldState.runFailedReason)
                    ? string.Empty
                    : worldState.runFailedReason.Trim();

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
        /// Read run-phase resume metadata from save without requiring gameplay dependencies.
        /// Returns true when metadata was read successfully.
        /// </summary>
        public static bool TryReadRunResumeMeta(out int currentRunPhase, out int currentDay, out bool runFailed)
        {
            currentRunPhase = (int)GameManager.RunPhase.Home;
            currentDay = 1;
            runFailed = false;

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

                WorldSaveState worldState = ResolveWorldState(data);

                currentDay = Mathf.Max(1, worldState.currentDay);
                if (worldState.currentRunPhase >= (int)GameManager.RunPhase.Work
                    && worldState.currentRunPhase <= (int)GameManager.RunPhase.GameOver)
                {
                    currentRunPhase = worldState.currentRunPhase;
                }

                runFailed = worldState.runFailed || currentRunPhase == (int)GameManager.RunPhase.GameOver;
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SaveManager] Failed to read run resume metadata: {e.Message}");
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

        private static bool IsNonAuthoritativeNetworkClient()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening && manager.IsClient && !manager.IsServer;
        }

        private static WorldSaveState BuildWorldState(SaveContext context)
        {
            return new WorldSaveState
            {
                currency = context.GameManager.GetCurrency(),
                dayWorkEarnings = context.GameManager.GetDayWorkEarnings(),
                currentDay = context.GameManager.GetCurrentDay(),
                currentRunPhase = (int)context.GameManager.GetCurrentRunPhase(),
                workdayCompleted = context.GameManager.IsWorkdayCompleted(),
                currentWorkHour = context.GameManager.GetCurrentWorkHourForSave(),
                nextTaskWaveIndex = context.GameManager.GetNextTaskWaveIndexForSave(),
                consecutiveFailedWorkdays = context.GameManager.GetConsecutiveFailedWorkdays(),
                runFailed = context.GameManager.IsRunFailed(),
                runFailedReason = context.GameManager.GetRunFailedReason(),
                failedLieEscalationCountThisDay = context.GameManager.GetFailedLieEscalationCountThisDay(),
                dailyTaskAssignments = context.GameManager.GetDailyTaskAssignmentsForSave(),
                generatedTaskWaves = context.GameManager.GetGeneratedTaskWavesForSave(),
                unlockedTaskKeys = context.GameManager.GetUnlockedTaskKeysForSave(),
                stolenLootThisDay = context.GameManager.GetStolenLootThisDaySnapshotForAllOwners(),
                ownedTools = context.GameManager.GetOwnedToolUpgradesForSave(),
                objectives = new ObjectivesSaveData(),
                consumedCollectibleIds = new List<string>()
            };
        }

        private static WorldSaveState ResolveWorldState(SaveData data)
        {
            if (data != null && data.worldState != null)
            {
                WorldSaveState structured = data.worldState;
                structured.objectives ??= new ObjectivesSaveData();
                structured.ownedTools ??= new List<ToolDataEntry>();
                structured.dailyTaskAssignments ??= new List<DailyTaskAssignmentData>();
                structured.generatedTaskWaves ??= new List<GeneratedTaskWaveData>();
                structured.unlockedTaskKeys ??= new List<string>();
                structured.stolenLootThisDay ??= new List<StolenLootEntryData>();
                structured.consumedCollectibleIds ??= new List<string>();
                return structured;
            }

            return new WorldSaveState
            {
                currency = data != null ? data.currency : 0,
                dayWorkEarnings = data != null ? data.dayWorkEarnings : 0,
                currentDay = data != null ? data.currentDay : 1,
                currentRunPhase = data != null ? data.currentRunPhase : 0,
                workdayCompleted = data != null && data.workdayCompleted,
                currentWorkHour = data != null ? data.currentWorkHour : 7f,
                nextTaskWaveIndex = data != null ? data.nextTaskWaveIndex : 0,
                consecutiveFailedWorkdays = data != null ? data.consecutiveFailedWorkdays : 0,
                runFailed = data != null && data.runFailed,
                runFailedReason = data != null ? data.runFailedReason : string.Empty,
                failedLieEscalationCountThisDay = data != null ? data.failedLieEscalationCountThisDay : 0,
                ownedTools = data?.ownedTools ?? new List<ToolDataEntry>(),
                objectives = data?.objectives ?? new ObjectivesSaveData(),
                consumedCollectibleIds = data?.consumedCollectibleIds ?? new List<string>(),
                dailyTaskAssignments = data?.dailyTaskAssignments ?? new List<DailyTaskAssignmentData>(),
                generatedTaskWaves = data?.generatedTaskWaves ?? new List<GeneratedTaskWaveData>(),
                unlockedTaskKeys = data?.unlockedTaskKeys ?? new List<string>(),
                stolenLootThisDay = data?.stolenLootThisDay ?? new List<StolenLootEntryData>()
            };
        }

        private static PlayerSaveState ResolveLocalPlayerState(SaveData data)
        {
            string localOwnerKey = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
            if (data != null && data.playerStates != null && data.playerStates.Count > 0)
            {
                for (int i = 0; i < data.playerStates.Count; i++)
                {
                    PlayerSaveState candidate = data.playerStates[i];
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.ownerKey))
                    {
                        continue;
                    }

                    if (string.Equals(candidate.ownerKey.Trim(), localOwnerKey, StringComparison.Ordinal))
                    {
                        candidate.inventory ??= new List<InventorySlotData>();
                        return candidate;
                    }
                }

                for (int i = 0; i < data.playerStates.Count; i++)
                {
                    PlayerSaveState candidate = data.playerStates[i];
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.ownerKey))
                    {
                        continue;
                    }

                    if (string.Equals(candidate.ownerKey.Trim(), PlayerContextRegistry.DefaultLocalPlayerId, StringComparison.Ordinal))
                    {
                        candidate.inventory ??= new List<InventorySlotData>();
                        return candidate;
                    }
                }

                for (int i = 0; i < data.playerStates.Count; i++)
                {
                    PlayerSaveState candidate = data.playerStates[i];
                    if (candidate == null)
                    {
                        continue;
                    }

                    candidate.inventory ??= new List<InventorySlotData>();
                    return candidate;
                }
            }

            return new PlayerSaveState
            {
                ownerKey = localOwnerKey,
                hasPlayerTransform = data != null && data.hasPlayerTransform,
                playerPosition = data != null ? data.playerPosition : Vector3.zero,
                playerRotation = data != null ? data.playerRotation : Quaternion.identity,
                selectedInventorySlotIndex = data != null ? data.selectedInventorySlotIndex : 0,
                inventory = data?.inventory ?? new List<InventorySlotData>()
            };
        }
    }
}
