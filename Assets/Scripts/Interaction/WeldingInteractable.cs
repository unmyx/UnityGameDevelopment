using System.Collections.Generic;
using System;
using Game.Core;
using Game.Core.Events;
using Game.Minigames;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Interaction
{
    /// <summary>
    /// Starts the world-anchor welding minigame from world interaction.
    /// </summary>
    public class WeldingInteractable : MinigameInteractableBase
    {
        private const string DailyTaskType = "welding";

        [SerializeField] private Canvas _minigameCanvas;

        [Header("World Weld Progress UI")]
        [SerializeField] private TMP_Text _coverageText;
        [SerializeField] private Image _coverageFillImage;
        [SerializeField] private TMP_Text _timerText;

        [Header("World View Camera")]
        [SerializeField] private bool _useWorldStationView = true;

        [SerializeField]
        [Tooltip("Station child transform defining the final minigame camera pose.")]
        private Transform _stationCameraPose;

        [SerializeField]
        [Tooltip("Station child transform the minigame camera looks at during transition.")]
        private Transform _stationCameraLookTarget;

        [SerializeField]
        [Min(0f)]
        private float _cameraTransitionDuration = 0.45f;

        [SerializeField]
        [Min(0f)]
        private float _cameraReturnDuration = 0.3f;

        [Header("World View Framing Tuning")]
        [SerializeField]
        [Tooltip("Runtime camera local-space offset from MinigameCameraPose. Increase Y for higher bird-view framing.")]
        private Vector3 _worldCameraLocalOffset = new Vector3(0f, 1.35f, -0.8f);

        [SerializeField]
        [Tooltip("Look target local-space offset from MinigameLookTarget.")]
        private Vector3 _worldLookTargetLocalOffset = Vector3.zero;

        [SerializeField]
        [Tooltip("Additional pitch bias in degrees applied after look-at rotation. Positive values tilt more top-down.")]
        [Range(-35f, 45f)]
        private float _worldTopDownAngleBias = 18f;

        [SerializeField]
        [Tooltip("Optional FOV override for welding world camera. Set <= 0 to reuse gameplay camera FOV.")]
        [Range(0f, 120f)]
        private float _worldCameraFovOverride = 50f;

        [Header("World Weld Targets")]
        [SerializeField]
        [Tooltip("Container with manual world-space weld point anchors.")]
        private Transform _weldAnchorsRoot;

        [SerializeField]
        [Range(1, 12)]
        private int _activeWorldWeldCount = 3;

        [SerializeField]
        [Min(0.1f)]
        private float _worldAnchorWeldRate = 1f;

        [SerializeField]
        [Min(12f)]
        private float _worldAnchorScreenRadiusPixels = 60f;

        [SerializeField]
        private bool _showWorldAnchorMarkers = true;

        protected override MinigameData BuildMinigameData()
        {
            if (!TryResolveRequiredReferences(out string validationError))
            {
                Debug.LogError($"[WeldingInteractable] Blocking welding minigame start: {validationError}", this);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = "welding",
                displayName = "Weld the Metal",
                timeLimit = 0f
            };

            float weldingEffectivenessMultiplier = 1f;
            float weldingDayDifficultyMultiplier = 1f;
            int activeWorldWeldCount = _activeWorldWeldCount;
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                weldingEffectivenessMultiplier = Mathf.Max(0.01f, gameManager.GetWeldingEffectivenessMultiplier());
                weldingDayDifficultyMultiplier = Mathf.Max(0.01f, gameManager.GetWeldingDayDifficultyMultiplier());
                activeWorldWeldCount = Mathf.Max(1, _activeWorldWeldCount + gameManager.GetWeldingActiveTargetCountBonusForCurrentDay());
            }

            data.SetParameter("canvas", _minigameCanvas);
            data.SetParameter("coverage_text", _coverageText);
            data.SetParameter("coverage_fill_image", _coverageFillImage);
            data.SetParameter("timer_text", _timerText);
            data.SetParameter("world_view_enabled", _useWorldStationView);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("world_weld_anchors_root", _weldAnchorsRoot);
            data.SetParameter("world_active_weld_count", activeWorldWeldCount);
            data.SetParameter("world_anchor_weld_rate", _worldAnchorWeldRate * weldingEffectivenessMultiplier * weldingDayDifficultyMultiplier);
            data.SetParameter("world_anchor_screen_radius", _worldAnchorScreenRadiusPixels);
            data.SetParameter("world_anchor_markers", _showWorldAnchorMarkers);

            return data;
        }

        protected override void StartMinigame(MinigameData data)
        {
            GameManager gameManager = GameManager.Instance;
            string taskKey = GetDailyTaskLocationKey();

            if (gameManager != null
                && !gameManager.CanLaunchTaskAtLocation(DailyTaskType, taskKey, out string blockedReason))
            {
                if (!string.IsNullOrWhiteSpace(blockedReason))
                {
                    EventBus.Publish(new PlayerFeedbackEvent(blockedReason));
                }

                return;
            }

            if (gameManager != null)
            {
                gameManager.RegisterDailyTaskLaunchContext(DailyTaskType, taskKey);
            }

            MinigameManager.Instance?.StartMinigame<WeldingFillMinigame>(data);
        }

        public string GetDailyTaskLocationKey()
        {
            Transform container = transform.parent;
            string containerPath = BuildHierarchyPath(container != null ? container : transform);

            if (container == null)
            {
                return $"{DailyTaskType}:{containerPath}:0";
            }

            WeldingInteractable[] interactables = container.GetComponentsInChildren<WeldingInteractable>(true);
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

        private static int CompareByHierarchyPath(WeldingInteractable a, WeldingInteractable b)
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
            if (_minigameCanvas == null)
            {
                failures.Add("missing WeldingCanvas");
            }

            if (_coverageFillImage == null)
            {
                failures.Add("missing coverage Fill image / progress UI");
            }
            else if (_coverageFillImage.type != Image.Type.Filled || _coverageFillImage.fillMethod != Image.FillMethod.Horizontal)
            {
                failures.Add("coverage Fill image is not configured as Filled + Horizontal");
            }

            if (_timerText == null)
            {
                failures.Add("missing welding TimerText reference");
            }

            if (_weldAnchorsRoot == null)
            {
                failures.Add("missing WeldAnchors root");
            }
            else if (CountActiveChildren(_weldAnchorsRoot) == 0)
            {
                failures.Add("WeldAnchors has zero active children");
            }

            if (_useWorldStationView)
            {
                if (_stationCameraPose == null)
                {
                    failures.Add("missing MinigameCameraPose while world view is enabled");
                }

                if (_stationCameraLookTarget == null)
                {
                    failures.Add("missing MinigameLookTarget while world view is enabled");
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

        private static int CountActiveChildren(Transform root)
        {
            if (root == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child != null && child.gameObject.activeInHierarchy)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
