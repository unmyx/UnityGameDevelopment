using System;
using System.Collections.Generic;
using Game.Minigames;
using UnityEngine;

namespace Game.Interaction
{
    /// <summary>
    /// Launches the Home computer browser minigame using the shared minigame flow.
    /// </summary>
    public class ComputerInteractable : MinigameInteractableBase
    {
        private const string MinigameId = "computer";
        private const string DisplayName = "Computer";
        private const string CameraPoseChildName = "MinigameCameraPose";
        private const string LookTargetChildName = "MinigameLookTarget";
        private const string FallbackCanvasName = "ComputerCanvas";

        [Header("Required References")]
        [SerializeField]
        private Canvas _computerCanvas;

        [SerializeField]
        [Tooltip("Child transform defining the final camera pose while using the computer.")]
        private Transform _stationCameraPose;

        [SerializeField]
        [Tooltip("Child transform used as look-at target while using the computer.")]
        private Transform _stationCameraLookTarget;

        [Header("Camera Transition")]
        [SerializeField]
        [Min(0f)]
        private float _cameraTransitionDuration = 0.35f;

        [SerializeField]
        [Min(0f)]
        private float _cameraReturnDuration = 0.25f;

        [SerializeField]
        private Vector3 _worldCameraLocalOffset = new Vector3(0f, 0.18f, -0.42f);

        [SerializeField]
        private Vector3 _worldLookTargetLocalOffset = Vector3.zero;

        [SerializeField]
        [Range(-35f, 45f)]
        private float _worldTopDownAngleBias;

        [SerializeField]
        [Range(0f, 120f)]
        private float _worldCameraFovOverride = 42f;

        [Header("Input")]
        [SerializeField]
        private bool _unlockCursorDuringMinigame = true;

        protected override MinigameData BuildMinigameData()
        {
            if (!TryResolveRequiredReferences(out string validationError))
            {
                Debug.LogError($"[ComputerInteractable] Blocking computer minigame start: {validationError}", this);
                return null;
            }

            MinigameData data = new MinigameData
            {
                minigameId = MinigameId,
                displayName = DisplayName,
                timeLimit = 0f
            };

            data.SetParameter("canvas", _computerCanvas);
            data.SetParameter("world_camera_pose", _stationCameraPose);
            data.SetParameter("world_look_target", _stationCameraLookTarget);
            data.SetParameter("world_camera_transition", _cameraTransitionDuration);
            data.SetParameter("world_camera_return", _cameraReturnDuration);
            data.SetParameter("world_camera_local_offset", _worldCameraLocalOffset);
            data.SetParameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            data.SetParameter("world_top_down_angle_bias", _worldTopDownAngleBias);
            data.SetParameter("world_camera_fov", _worldCameraFovOverride);
            data.SetParameter("unlock_cursor_during_minigame", _unlockCursorDuringMinigame);
            return data;
        }

        protected override void StartMinigame(MinigameData data)
        {
            MinigameManager minigameManager = MinigameManager.Instance;
            if (minigameManager == null)
            {
                Debug.LogError(
                    "[ComputerInteractable] Cannot start computer minigame: MinigameManager is unavailable in the active scene.",
                    this);
                return;
            }

            IMinigame startedMinigame = minigameManager.StartMinigame<ComputerMinigame>(data);
            if (startedMinigame == null)
            {
                Debug.LogError(
                    "[ComputerInteractable] Computer minigame startup was rejected by MinigameManager.",
                    this);
            }
        }

        private bool TryResolveRequiredReferences(out string validationError)
        {
            List<string> failures = new List<string>(4);

            if (_stationCameraPose == null)
            {
                Transform pose = transform.Find(CameraPoseChildName);
                if (pose != null)
                {
                    _stationCameraPose = pose;
                }
            }

            if (_stationCameraLookTarget == null)
            {
                Transform lookTarget = transform.Find(LookTargetChildName);
                if (lookTarget != null)
                {
                    _stationCameraLookTarget = lookTarget;
                }
            }

            if (_computerCanvas == null)
            {
                _computerCanvas = FindCanvasByName(FallbackCanvasName);
            }

            if (_computerCanvas == null)
            {
                failures.Add("missing ComputerCanvas reference");
            }

            if (_stationCameraPose == null)
            {
                failures.Add($"missing {CameraPoseChildName} child transform");
            }

            if (_stationCameraLookTarget == null)
            {
                failures.Add($"missing {LookTargetChildName} child transform");
            }

            if (failures.Count > 0)
            {
                validationError = string.Join("; ", failures);
                return false;
            }

            validationError = string.Empty;
            return true;
        }

        private static Canvas FindCanvasByName(string canvasName)
        {
            if (string.IsNullOrWhiteSpace(canvasName))
            {
                return null;
            }

            Canvas[] canvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include);
            for (int i = 0; i < canvases.Length; i++)
            {
                Canvas candidate = canvases[i];
                if (candidate == null)
                {
                    continue;
                }

                if (string.Equals(candidate.name, canvasName, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
