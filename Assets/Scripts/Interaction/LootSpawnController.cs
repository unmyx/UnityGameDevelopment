using Game.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// Scene-local daily loot spawner driven by hand-authored LootSpawnPoint markers.
    /// </summary>
    public class LootSpawnController : MonoBehaviour
    {
        [Serializable]
        private class LootSpawnEntry
        {
            public string lootTypeId = string.Empty;
            public GameObject lootPrefab;
            [Min(0)] public int guaranteedCount = 1;
            [Range(0f, 1f)] public float extraSpawnChance = 0.35f;
            [Min(0)] public int maxExtraCount = 1;
        }

        [Header("Spawn Table")]
        [SerializeField]
        private LootSpawnEntry[] _lootEntries =
        {
            new LootSpawnEntry
            {
                lootTypeId = "wedding_ring_gold",
                guaranteedCount = 1,
                extraSpawnChance = 0.35f,
                maxExtraCount = 1
            },
            new LootSpawnEntry
            {
                lootTypeId = "wedding_ring_silver",
                guaranteedCount = 1,
                extraSpawnChance = 0.35f,
                maxExtraCount = 1
            }
        };

        [Header("Determinism")]
        [SerializeField]
        private int _seedOffset = 104729;

        [Header("Runtime Checks")]
        [SerializeField]
        [Min(0.1f)]
        private float _dayCheckIntervalSeconds = 0.5f;

        private readonly List<GameObject> _spawnedLoot = new List<GameObject>();
        private GameManager _gameManager;
        private float _nextDayCheckTime;
        private int _lastSpawnedDay = -1;

        private IEnumerator Start()
        {
            while (_gameManager == null)
            {
                _gameManager = GameManager.Instance;
                if (_gameManager == null)
                {
                    yield return null;
                }
            }

            TryRebuildForCurrentDay(force: true);
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextDayCheckTime)
            {
                return;
            }

            _nextDayCheckTime = Time.unscaledTime + Mathf.Max(0.1f, _dayCheckIntervalSeconds);
            TryRebuildForCurrentDay(force: false);
        }

        private void OnDisable()
        {
            ClearSpawnedLoot();
        }

        private void TryRebuildForCurrentDay(bool force)
        {
            if (_gameManager == null)
            {
                _gameManager = GameManager.Instance;
                if (_gameManager == null)
                {
                    return;
                }
            }

            int currentDay = Mathf.Max(1, _gameManager.GetCurrentDay());
            if (!force && currentDay == _lastSpawnedDay)
            {
                return;
            }

            RebuildForDay(currentDay);
        }

        private void RebuildForDay(int day)
        {
            ClearSpawnedLoot();
            _lastSpawnedDay = day;

            LootSpawnPoint[] allPoints = GetComponentsInChildren<LootSpawnPoint>(includeInactive: false);
            if (allPoints == null || allPoints.Length == 0)
            {
                return;
            }

            Dictionary<string, List<LootSpawnPoint>> pointsByType = BuildPointsByType(allPoints);
            System.Random random = new System.Random(BuildSeed(day));
            HashSet<string> usedPointIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < _lootEntries.Length; i++)
            {
                LootSpawnEntry entry = _lootEntries[i];
                if (!TryValidateEntry(entry, out string normalizedLootType))
                {
                    continue;
                }

                if (!pointsByType.TryGetValue(normalizedLootType, out List<LootSpawnPoint> typedPoints)
                    || typedPoints == null
                    || typedPoints.Count == 0)
                {
                    continue;
                }

                List<LootSpawnPoint> candidates = new List<LootSpawnPoint>(typedPoints.Count);
                for (int pointIndex = 0; pointIndex < typedPoints.Count; pointIndex++)
                {
                    LootSpawnPoint point = typedPoints[pointIndex];
                    if (point == null)
                    {
                        continue;
                    }

                    string normalizedPointId = NormalizeId(point.PointId);
                    if (string.IsNullOrEmpty(normalizedPointId) || usedPointIds.Contains(normalizedPointId))
                    {
                        continue;
                    }

                    candidates.Add(point);
                }

                if (candidates.Count == 0)
                {
                    continue;
                }

                int targetSpawnCount = Mathf.Max(0, entry.guaranteedCount);
                int extraRolls = Mathf.Max(0, entry.maxExtraCount);
                float clampedChance = Mathf.Clamp01(entry.extraSpawnChance);

                for (int rollIndex = 0; rollIndex < extraRolls; rollIndex++)
                {
                    if (random.NextDouble() <= clampedChance)
                    {
                        targetSpawnCount++;
                    }
                }

                targetSpawnCount = Mathf.Min(targetSpawnCount, candidates.Count);
                for (int spawnIndex = 0; spawnIndex < targetSpawnCount; spawnIndex++)
                {
                    int candidateIndex = random.Next(candidates.Count);
                    LootSpawnPoint selectedPoint = candidates[candidateIndex];
                    candidates.RemoveAt(candidateIndex);
                    if (selectedPoint == null)
                    {
                        continue;
                    }

                    string selectedPointId = NormalizeId(selectedPoint.PointId);
                    if (string.IsNullOrEmpty(selectedPointId) || usedPointIds.Contains(selectedPointId))
                    {
                        continue;
                    }

                    try
                    {
                        UnityEngine.Object spawnedObject = UnityEngine.Object.Instantiate(
                            (UnityEngine.Object)entry.lootPrefab,
                            selectedPoint.transform.position,
                            selectedPoint.transform.rotation,
                            transform);

                        GameObject spawnedLoot = spawnedObject as GameObject;
                        if (spawnedLoot == null && spawnedObject is Component spawnedComponent)
                        {
                            spawnedLoot = spawnedComponent.gameObject;
                        }

                        if (spawnedLoot == null)
                        {
                            string referencedType = spawnedObject == null ? "null" : spawnedObject.GetType().FullName;
                            string referencedName = spawnedObject == null ? "null" : spawnedObject.name;
                            Debug.LogError(
                                $"[LootSpawnController] Spawn failed to resolve GameObject. " +
                                $"LootType='{normalizedLootType}', PointId='{selectedPointId}', " +
                                $"SpawnedType='{referencedType}', SpawnedName='{referencedName}'.",
                                this);
                            continue;
                        }

                        InteractableItem interactableItem = spawnedLoot.GetComponent<InteractableItem>();
                        if (interactableItem == null)
                        {
                            Debug.LogError(
                                $"[LootSpawnController] Spawned loot is missing InteractableItem on root. " +
                                $"LootType='{normalizedLootType}', PointId='{selectedPointId}', SpawnedName='{spawnedLoot.name}'.",
                                spawnedLoot);
                            Destroy(spawnedLoot);
                            continue;
                        }

                        string runtimeCollectibleId = BuildRuntimeCollectibleId(day, normalizedLootType, selectedPointId);
                        interactableItem.ConfigureRuntimePersistenceId(runtimeCollectibleId);

                        _spawnedLoot.Add(spawnedLoot);
                        usedPointIds.Add(selectedPointId);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError(
                            $"[LootSpawnController] Exception while spawning loot. " +
                            $"LootType='{normalizedLootType}', PointId='{selectedPointId}', Prefab='{entry.lootPrefab?.name ?? "null"}'. " +
                            $"Exception: {exception.GetType().Name} - {exception.Message}",
                            this);
                        continue;
                    }
                }
            }
        }

        private void ClearSpawnedLoot()
        {
            for (int i = 0; i < _spawnedLoot.Count; i++)
            {
                GameObject spawnedObject = _spawnedLoot[i];
                if (spawnedObject == null)
                {
                    continue;
                }

                Destroy(spawnedObject);
            }

            _spawnedLoot.Clear();
        }

        private Dictionary<string, List<LootSpawnPoint>> BuildPointsByType(LootSpawnPoint[] points)
        {
            Dictionary<string, List<LootSpawnPoint>> pointsByType = new Dictionary<string, List<LootSpawnPoint>>(StringComparer.Ordinal);
            HashSet<string> seenPointIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < points.Length; i++)
            {
                LootSpawnPoint point = points[i];
                if (point == null)
                {
                    continue;
                }

                string pointId = NormalizeId(point.PointId);
                string lootTypeId = NormalizeId(point.LootTypeId);
                if (string.IsNullOrEmpty(pointId) || string.IsNullOrEmpty(lootTypeId))
                {
                    continue;
                }

                if (!seenPointIds.Add(pointId))
                {
                    Debug.LogWarning($"[LootSpawnController] Duplicate LootSpawnPoint id '{pointId}' ignored.", point);
                    continue;
                }

                if (!pointsByType.TryGetValue(lootTypeId, out List<LootSpawnPoint> list))
                {
                    list = new List<LootSpawnPoint>();
                    pointsByType[lootTypeId] = list;
                }

                list.Add(point);
            }

            return pointsByType;
        }

        private bool TryValidateEntry(LootSpawnEntry entry, out string normalizedLootType)
        {
            normalizedLootType = string.Empty;
            if (entry == null || entry.lootPrefab == null)
            {
                return false;
            }

            normalizedLootType = NormalizeId(entry.lootTypeId);
            return !string.IsNullOrEmpty(normalizedLootType);
        }

        private int BuildSeed(int day)
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + day;
                hash = (hash * 31) + _seedOffset;
                hash = (hash * 31) + ComputeStableHash(gameObject.scene.name);
                return hash;
            }
        }

        private static string BuildRuntimeCollectibleId(int day, string lootTypeId, string pointId)
        {
            return $"lootspawn.gameplay.day{Mathf.Max(1, day)}.{lootTypeId}.{pointId}";
        }

        private static string NormalizeId(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }

        private static int ComputeStableHash(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            unchecked
            {
                int hash = 23;
                for (int i = 0; i < value.Length; i++)
                {
                    hash = (hash * 31) + value[i];
                }

                return hash;
            }
        }
    }
}
