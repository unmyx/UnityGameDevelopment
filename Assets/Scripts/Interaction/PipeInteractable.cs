using System;
using System.Collections.Generic;
using Game.Core;
using Game.Minigames;
using TMPro;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// Starts the world-surface cleaning minigame from world interaction.
    /// </summary>
    public class PipeInteractable : MinigameInteractableBase
    {
        private const int FixedSwipesPerStain = 6;
        private const string DailyTaskType = "cleaning";

        [SerializeField]
        private Canvas _cleaningCanvas;

        [Header("Cleaning HUD")]
        [SerializeField]
        private TMP_Text _timerText;

        [SerializeField]
        private TMP_Text _progressText;

        [Header("World View Camera")]
        [SerializeField]
        [Tooltip("Station child transform defining the final cleaning camera pose.")]
        private Transform _stationCameraPose;

        [SerializeField]
        [Tooltip("Station child transform the cleaning camera looks at.")]
        private Transform _stationCameraLookTarget;

        [SerializeField]
        private Transform _worldCleaningSurfaceRoot;

        [SerializeField]
        [Min(0f)]
        private float _cameraTransitionDuration = 0.45f;

        [SerializeField]
        [Min(0f)]
        private float _cameraReturnDuration = 0.3f;

        [SerializeField]
        private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.25f, -0.75f);

        [SerializeField]
        private Vector3 _worldLookTargetLocalOffset = Vector3.zero;

        [SerializeField]
        [Range(-35f, 45f)]
        private float _worldTopDownAngleBias = 16f;

        [SerializeField]
        [Range(0f, 120f)]
        private float _worldCameraFovOverride = 52f;

        [SerializeField]
        private bool _freezePlayerMovementInWorldView = true;

        [Header("World Cleaning Interaction")]
        [SerializeField]
        [Min(8f)]
        private float _worldStainScreenRadiusPixels = 52f;

        [SerializeField]
        [Min(0.1f)]
        private float _worldMinMouseMovePixels = 1.5f;

        [SerializeField]
        [Min(0.001f)]
        private float _worldSwipeGainPerPixel = 0.02f;

        [SerializeField]
        [Min(0.01f)]
        private float _worldStainMarkerScale = 0.05f;

        [SerializeField]
        [Min(1)]
        private int _worldStainsPerSurface = 2;

        protected override MinigameData BuildMinigameData()
        {
            if (!TryResolveRequiredReferences(out string validationError))
            {
                Debug.LogError($"[PipeInteractable] Blocking cleaning minigame start: {validationError}", this);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = "cleaning",
                displayName = "Clean the Pipes",
                timeLimit = 0f
            };

            float cleaningEffectivenessMultiplier = 1f;
            float cleaningDayDifficultyMultiplier = 1f;
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                cleaningEffectivenessMultiplier = Mathf.Max(0.01f, gameManager.GetCleaningEffectivenessMultiplier());
                cleaningDayDifficultyMultiplier = Mathf.Max(0.01f, gameManager.GetCleaningDayDifficultyMultiplier());
            }

            data.SetParameter("canvas", _cleaningCanvas);
            data.SetParameter("timer_text", _timerText);
            data.SetParameter("progress_text", _progressText);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_cleaning_surface_root", _worldCleaningSurfaceRoot);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("world_freeze_player", _freezePlayerMovementInWorldView);
            data.SetParameter("world_stain_screen_radius", _worldStainScreenRadiusPixels);
            data.SetParameter("world_min_mouse_move_pixels", _worldMinMouseMovePixels);
            data.SetParameter("world_swipe_gain_per_pixel", _worldSwipeGainPerPixel * cleaningEffectivenessMultiplier * cleaningDayDifficultyMultiplier);
            data.SetParameter("world_stain_marker_scale", _worldStainMarkerScale);
            data.SetParameter("world_stains_per_surface", _worldStainsPerSurface);
            data.SetParameter("world_fixed_swipes_per_stain", FixedSwipesPerStain);

            return data;
        }

        protected override void StartMinigame(MinigameData data)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                gameManager.RegisterDailyTaskLaunchContext(DailyTaskType, GetDailyTaskLocationKey());
            }

            MinigameManager.Instance?.StartMinigame<CleaningMinigame>(data);
        }

        public string GetDailyTaskLocationKey()
        {
            Transform container = transform.parent;
            string containerPath = BuildHierarchyPath(container != null ? container : transform);

            if (container == null)
            {
                return $"{DailyTaskType}:{containerPath}:0";
            }

            PipeInteractable[] interactables = container.GetComponentsInChildren<PipeInteractable>(true);
            Array.Sort(interactables, CompareByHierarchyPath);

            int index = 0;
            for (int i = 0; i < interactables.Length; i++)
            {
                if (interactables[i] == this)
                {
                    index = i;
                    break;
                }
            }

            return $"{DailyTaskType}:{containerPath}:{index}";
        }

        private static int CompareByHierarchyPath(PipeInteractable a, PipeInteractable b)
        {
            string pathA = BuildHierarchyPath(a != null ? a.transform : null);
            string pathB = BuildHierarchyPath(b != null ? b.transform : null);
            return string.CompareOrdinal(pathA, pathB);
        }

        private static string BuildHierarchyPath(Transform transformNode)
        {
            if (transformNode == null)
            {
                return string.Empty;
            }

            List<string> segments = new List<string>(8);
            Transform current = transformNode;
            while (current != null)
            {
                segments.Add($"{current.name}[{current.GetSiblingIndex()}]");
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments);
        }

        private bool TryResolveRequiredReferences(out string validationError)
        {
            List<string> failures = new List<string>(7);
            if (_cleaningCanvas == null)
            {
                failures.Add("missing CleaningCanvas reference");
            }

            if (_timerText == null)
            {
                failures.Add("missing cleaning TimerText reference");
            }

            if (_progressText == null)
            {
                failures.Add("missing cleaning ProgressText reference");
            }

            if (_worldCleaningSurfaceRoot == null)
            {
                failures.Add("missing CleaningSurfaces root");
            }

            if (_stationCameraPose == null)
            {
                failures.Add("missing MinigameCameraPose");
            }

            if (_stationCameraLookTarget == null)
            {
                failures.Add("missing MinigameLookTarget");
            }

            if (_worldCleaningSurfaceRoot != null)
            {
                int activeSurfaceCount = CountActiveCleaningSurfaces(_worldCleaningSurfaceRoot);
                if (activeSurfaceCount <= 0)
                {
                    failures.Add("CleaningSurfaces has zero active collidable surfaces");
                }
            }

            if (failures.Count > 0)
            {
                validationError = string.Join("; ", failures);
                return false;
            }

            validationError = string.Empty;
            return true;
        }

        private static int CountActiveCleaningSurfaces(Transform surfacesRoot)
        {
            if (surfacesRoot == null || !surfacesRoot.gameObject.activeInHierarchy)
            {
                return 0;
            }

            int count = 0;
            Transform[] transforms = surfacesRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform candidate = transforms[i];
                if (candidate == null || candidate == surfacesRoot)
                {
                    continue;
                }

                if (!candidate.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (!candidate.name.StartsWith("CleaningSurface", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (HasEnabledCollider(candidate))
                {
                    count++;
                }
            }

            if (count > 0)
            {
                return count;
            }

            return HasEnabledCollider(surfacesRoot) ? 1 : 0;
        }

        private static bool HasEnabledCollider(Transform root)
        {
            if (root == null)
            {
                return false;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

