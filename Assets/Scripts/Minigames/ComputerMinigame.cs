using Game.Core;
using Game.Core.Events;
using Game.Input;
using Game.Player;
using UnityEngine;

namespace Game.Minigames
{
    /// <summary>
    /// UI-based browser minigame used by Home computer terminals.
    /// </summary>
    public class ComputerMinigame : BaseMinigame
    {
        [Header("UI")]
        [SerializeField]
        private Canvas _computerCanvas;

        [SerializeField]
        private bool _unlockCursorDuringMinigame = true;

        [Header("World View")]
        [SerializeField]
        private Transform _worldViewPose;

        [SerializeField]
        private Transform _worldViewLookTarget;

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

        [SerializeField]
        private bool _suppressGameplayCameraRendering = true;

        private Camera _gameplayViewCamera;
        private Camera _worldViewCamera;
        private ComputerMinigameUI _uiController;
        private Coroutine _cameraTransitionRoutine;

        private bool _isSetupValid = true;
        private string _setupFailureReason = string.Empty;
        private bool _isFinishing;
        private bool _isReturningToGameplayView;
        private float _returnTransitionElapsed;
        private Vector3 _returnTransitionStartPosition;
        private Quaternion _returnTransitionStartRotation;
        private MinigameResult _pendingResult = MinigameResult.None;
        private bool _gameplayCameraWasEnabled;
        private bool _hasGameplayCameraRenderOverride;

        private CursorLockMode _previousCursorLockMode;
        private bool _previousCursorVisible;

        protected override void OnInitialize()
        {
            LoadParameters();

            _isSetupValid = true;
            _setupFailureReason = string.Empty;
            _isFinishing = false;
            _isReturningToGameplayView = false;
            _pendingResult = MinigameResult.None;

            if (_computerCanvas == null)
            {
                FailSetup("Missing required ComputerCanvas reference.");
                return;
            }

            _computerCanvas.enabled = false;
            ResolveGameplayViewCamera();
            ResolveUiController();
            RefreshUiRuntimeState();
        }

        protected override void OnStart()
        {
            if (!_isSetupValid)
            {
                Debug.LogError($"[ComputerMinigame] Start blocked by invalid setup: {_setupFailureReason}", this);
                SetResult(MinigameResult.Fail);
                return;
            }

            if (_computerCanvas != null)
            {
                _computerCanvas.enabled = true;
            }

            if (_unlockCursorDuringMinigame)
            {
                _previousCursorLockMode = Cursor.lockState;
                _previousCursorVisible = Cursor.visible;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (_uiController != null)
            {
                _uiController.Show();
                _uiController.SetStatusMessage(string.Empty);
            }

            StartWorldViewPresentation();
        }

        protected override void OnUpdate()
        {
            if (_isFinishing)
            {
                UpdateFinishFlow();
                return;
            }

            InputManager inputManager = InputManager.Instance;
            if (inputManager != null && inputManager.IsPausePressed())
            {
                RequestFinish(MinigameResult.Cancelled);
            }
        }

        protected override void OnEnd()
        {
            if (_cameraTransitionRoutine != null)
            {
                StopCoroutine(_cameraTransitionRoutine);
                _cameraTransitionRoutine = null;
            }

            UnsubscribeUiEvents();

            if (_uiController != null)
            {
                _uiController.Hide();
            }

            if (_computerCanvas != null)
            {
                _computerCanvas.enabled = false;
            }

            if (_unlockCursorDuringMinigame)
            {
                Cursor.lockState = _previousCursorLockMode;
                Cursor.visible = _previousCursorVisible;
            }

            TearDownWorldViewPresentation();

            _isFinishing = false;
            _isReturningToGameplayView = false;
            _pendingResult = MinigameResult.None;
        }

        private void LoadParameters()
        {
            if (_minigameData == null)
            {
                return;
            }

            Canvas canvas = GetParameter<Canvas>("canvas");
            if (canvas != null)
            {
                _computerCanvas = canvas;
            }

            _worldViewPose = GetParameter<Transform>("world_camera_pose") ?? _worldViewPose;
            _worldViewLookTarget = GetParameter<Transform>("world_look_target") ?? _worldViewLookTarget;

            _cameraTransitionDuration = Mathf.Max(0f, GetFloatParameter("world_camera_transition", _cameraTransitionDuration));
            _cameraReturnDuration = Mathf.Max(0f, GetFloatParameter("world_camera_return", _cameraReturnDuration));
            _worldCameraLocalOffset = GetVector3Parameter("world_camera_local_offset", _worldCameraLocalOffset);
            _worldLookTargetLocalOffset = GetVector3Parameter("world_look_target_local_offset", _worldLookTargetLocalOffset);
            _worldTopDownAngleBias = Mathf.Clamp(GetFloatParameter("world_top_down_angle_bias", _worldTopDownAngleBias), -45f, 60f);
            _worldCameraFovOverride = Mathf.Clamp(GetFloatParameter("world_camera_fov", _worldCameraFovOverride), 0f, 120f);
            _unlockCursorDuringMinigame = GetBoolParameter("unlock_cursor_during_minigame", _unlockCursorDuringMinigame);

            _minigameData.timeLimit = 0f;
        }

        private void ResolveUiController()
        {
            if (_computerCanvas == null)
            {
                FailSetup("Cannot initialize computer UI without ComputerCanvas.");
                return;
            }

            _uiController = _computerCanvas.GetComponent<ComputerMinigameUI>();
            if (_uiController == null)
            {
                _uiController = _computerCanvas.gameObject.AddComponent<ComputerMinigameUI>();
            }

            _uiController.Initialize(_computerCanvas);
            SubscribeUiEvents();
        }

        private void SubscribeUiEvents()
        {
            if (_uiController == null)
            {
                return;
            }

            _uiController.CloseRequested -= HandleCloseRequested;
            _uiController.SellRequested -= HandleSellRequested;
            _uiController.UpgradeRequested -= HandleUpgradeRequested;

            _uiController.CloseRequested += HandleCloseRequested;
            _uiController.SellRequested += HandleSellRequested;
            _uiController.UpgradeRequested += HandleUpgradeRequested;
        }

        private void UnsubscribeUiEvents()
        {
            if (_uiController == null)
            {
                return;
            }

            _uiController.CloseRequested -= HandleCloseRequested;
            _uiController.SellRequested -= HandleSellRequested;
            _uiController.UpgradeRequested -= HandleUpgradeRequested;
        }

        private void HandleCloseRequested()
        {
            RequestFinish(MinigameResult.Cancelled);
        }

        private void HandleSellRequested(string itemId)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                UpdateStatusMessage("Sell unavailable.");
                return;
            }

            bool success = gameManager.TrySellTrackedStolenLootItemUnitInHome(
                itemId,
                out int payoutAmount,
                out int remainingTrackedCount);
            if (!success)
            {
                UpdateStatusMessage("Sell unavailable.");
                RefreshUiRuntimeState();
                return;
            }

            string message = payoutAmount > 0
                ? $"Sold 1 item for ${payoutAmount}."
                : "Nothing to sell.";

            EventBus.Publish(new PlayerFeedbackEvent(message));
            UpdateStatusMessage(message);
            RefreshUiRuntimeState();
        }

        private void HandleUpgradeRequested(string upgradeId)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                UpdateStatusMessage("Upgrade unavailable.");
                return;
            }

            bool success = gameManager.TryPurchaseUpgradeInHome(upgradeId, out int spentCurrency, out int resultingTier);
            string displayName = GetUpgradeDisplayName(upgradeId);

            string message;
            if (success)
            {
                message = $"{displayName} upgraded to Tier {resultingTier} (-${spentCurrency}).";
            }
            else
            {
                string unavailableReason = ResolveUpgradeUnavailableReason(upgradeId);
                message = string.IsNullOrWhiteSpace(unavailableReason)
                    ? $"{displayName} unavailable."
                    : $"{displayName} unavailable: {unavailableReason}";
            }

            EventBus.Publish(new PlayerFeedbackEvent(message));
            UpdateStatusMessage(message);
            RefreshUiRuntimeState();
        }

        private void RefreshUiRuntimeState()
        {
            if (_uiController == null)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                _uiController.SetRuntimeSummary(0, 0);
                _uiController.SetSellEntries(null);
                _uiController.SetUpgradeEntries(null);
                return;
            }

            System.Collections.Generic.List<GameManager.SellableStolenLootEntryData> sellEntries =
                gameManager.GetSellableStolenLootEntriesInHome();
            _uiController.SetRuntimeSummary(gameManager.GetCurrency(), sellEntries != null ? sellEntries.Count : 0);
            _uiController.SetSellEntries(sellEntries);
            _uiController.SetUpgradeEntries(gameManager.GetHomeUpgradeStatusEntries());
        }

        private void UpdateStatusMessage(string message)
        {
            if (_uiController != null)
            {
                _uiController.SetStatusMessage(message);
            }
        }

        private static string GetUpgradeDisplayName(string upgradeId)
        {
            if (string.Equals(upgradeId, GameManager.UpgradeIdInventoryQuickSlots, System.StringComparison.Ordinal))
            {
                return "Inventory Upgrade";
            }

            if (string.Equals(upgradeId, GameManager.UpgradeIdCleaningTool, System.StringComparison.Ordinal))
            {
                return "Cleaning Upgrade";
            }

            if (string.Equals(upgradeId, GameManager.UpgradeIdWeldingTool, System.StringComparison.Ordinal))
            {
                return "Welding Upgrade";
            }

            return "Upgrade";
        }

        private static string ResolveUpgradeUnavailableReason(string upgradeId)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return string.Empty;
            }

            System.Collections.Generic.List<GameManager.HomeUpgradeStatusData> statuses =
                gameManager.GetHomeUpgradeStatusEntries();
            if (statuses == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < statuses.Count; i++)
            {
                GameManager.HomeUpgradeStatusData status = statuses[i];
                if (status == null)
                {
                    continue;
                }

                if (!string.Equals(status.upgradeId, upgradeId, System.StringComparison.Ordinal))
                {
                    continue;
                }

                return status.unavailableReason ?? string.Empty;
            }

            return string.Empty;
        }

        private void RequestFinish(MinigameResult result)
        {
            if (result == MinigameResult.None || _isFinishing)
            {
                return;
            }

            _isFinishing = true;
            _pendingResult = result;

            if (_computerCanvas != null)
            {
                _computerCanvas.enabled = false;
            }

            if (CanSmoothReturnToGameplayView())
            {
                BeginReturnToGameplayView();
                return;
            }

            SetResult(_pendingResult);
        }

        private void UpdateFinishFlow()
        {
            if (!_isReturningToGameplayView)
            {
                SetResult(_pendingResult);
                return;
            }

            if (_worldViewCamera == null || ResolveGameplayViewCamera() == null)
            {
                _isReturningToGameplayView = false;
                SetResult(_pendingResult);
                return;
            }

            float duration = Mathf.Max(0.01f, _cameraReturnDuration);
            _returnTransitionElapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_returnTransitionElapsed / duration);

            _worldViewCamera.transform.SetPositionAndRotation(
                Vector3.Lerp(_returnTransitionStartPosition, _gameplayViewCamera.transform.position, t),
                Quaternion.Slerp(_returnTransitionStartRotation, _gameplayViewCamera.transform.rotation, t));

            if (t >= 1f)
            {
                _isReturningToGameplayView = false;
                SetResult(_pendingResult);
            }
        }

        private void BeginReturnToGameplayView()
        {
            if (_worldViewCamera == null || ResolveGameplayViewCamera() == null)
            {
                _isReturningToGameplayView = false;
                SetResult(_pendingResult);
                return;
            }

            _isReturningToGameplayView = true;
            _returnTransitionElapsed = 0f;
            _returnTransitionStartPosition = _worldViewCamera.transform.position;
            _returnTransitionStartRotation = _worldViewCamera.transform.rotation;
        }

        private bool CanSmoothReturnToGameplayView()
        {
            return _worldViewCamera != null
                && _worldViewCamera.enabled
                && ResolveGameplayViewCamera() != null
                && _cameraReturnDuration > 0f;
        }

        private void StartWorldViewPresentation()
        {
            if (_worldViewPose == null || _worldViewLookTarget == null)
            {
                return;
            }

            ResolveGameplayViewCamera();
            if (!EnsureWorldViewCamera())
            {
                return;
            }

            if (_cameraTransitionRoutine != null)
            {
                StopCoroutine(_cameraTransitionRoutine);
                _cameraTransitionRoutine = null;
            }

            if (_cameraTransitionDuration <= 0f)
            {
                _worldViewCamera.transform.SetPositionAndRotation(GetWorldViewTargetPosition(), GetWorldViewTargetRotation());
                _worldViewCamera.enabled = true;
                SetGameplayCameraRenderingSuppressed(true);
                return;
            }

            _cameraTransitionRoutine = StartCoroutine(SmoothTransitionToWorldView());
        }

        private System.Collections.IEnumerator SmoothTransitionToWorldView()
        {
            if (_worldViewCamera == null || _worldViewPose == null || _worldViewLookTarget == null)
            {
                yield break;
            }

            Transform gameplayTransform = ResolveGameplayViewCamera() != null
                ? _gameplayViewCamera.transform
                : null;

            Vector3 endPosition = GetWorldViewTargetPosition();
            Quaternion endRotation = GetWorldViewTargetRotation();
            Vector3 startPosition = gameplayTransform != null ? gameplayTransform.position : endPosition;
            Quaternion startRotation = gameplayTransform != null ? gameplayTransform.rotation : endRotation;

            _worldViewCamera.transform.SetPositionAndRotation(startPosition, startRotation);
            _worldViewCamera.enabled = true;
            SetGameplayCameraRenderingSuppressed(true);

            float duration = Mathf.Max(0.01f, _cameraTransitionDuration);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                _worldViewCamera.transform.SetPositionAndRotation(
                    Vector3.Lerp(startPosition, endPosition, t),
                    Quaternion.Slerp(startRotation, endRotation, t));
                yield return null;
            }

            _worldViewCamera.transform.SetPositionAndRotation(endPosition, endRotation);
            _cameraTransitionRoutine = null;
        }

        private Camera ResolveGameplayViewCamera()
        {
            if (_gameplayViewCamera != null)
            {
                return _gameplayViewCamera;
            }

            FirstPersonCamera firstPersonCamera = FindAnyObjectByType<FirstPersonCamera>();
            if (firstPersonCamera != null)
            {
                _gameplayViewCamera = firstPersonCamera.GetComponent<Camera>();
                if (_gameplayViewCamera != null)
                {
                    return _gameplayViewCamera;
                }
            }

            _gameplayViewCamera = Camera.main;
            return _gameplayViewCamera;
        }

        private bool EnsureWorldViewCamera()
        {
            if (_worldViewPose == null || _worldViewLookTarget == null)
            {
                return false;
            }

            if (_worldViewCamera == null)
            {
                GameObject cameraObject = new GameObject("[ComputerWorldViewCamera]", typeof(Camera));
                cameraObject.transform.SetParent(transform, false);
                _worldViewCamera = cameraObject.GetComponent<Camera>();
            }

            Camera gameplayCamera = ResolveGameplayViewCamera();
            if (gameplayCamera != null)
            {
                _worldViewCamera.CopyFrom(gameplayCamera);
                _worldViewCamera.depth = gameplayCamera.depth + 1f;
            }

            if (_worldCameraFovOverride > 0f)
            {
                _worldViewCamera.fieldOfView = Mathf.Clamp(_worldCameraFovOverride, 20f, 100f);
            }

            _worldViewCamera.enabled = false;
            return true;
        }

        private void SetGameplayCameraRenderingSuppressed(bool suppressed)
        {
            if (!_suppressGameplayCameraRendering)
            {
                return;
            }

            Camera gameplayCamera = ResolveGameplayViewCamera();
            if (gameplayCamera == null || gameplayCamera == _worldViewCamera)
            {
                return;
            }

            if (suppressed)
            {
                if (_hasGameplayCameraRenderOverride)
                {
                    return;
                }

                _gameplayCameraWasEnabled = gameplayCamera.enabled;
                gameplayCamera.enabled = false;
                _hasGameplayCameraRenderOverride = true;
                return;
            }

            if (!_hasGameplayCameraRenderOverride)
            {
                return;
            }

            gameplayCamera.enabled = _gameplayCameraWasEnabled;
            _hasGameplayCameraRenderOverride = false;
        }

        private Vector3 GetWorldViewTargetPosition()
        {
            if (_worldViewPose == null)
            {
                return Vector3.zero;
            }

            return _worldViewPose.TransformPoint(_worldCameraLocalOffset);
        }

        private Vector3 GetWorldViewLookPoint(Vector3 cameraPosition)
        {
            if (_worldViewLookTarget == null)
            {
                return cameraPosition + Vector3.down;
            }

            return _worldViewLookTarget.TransformPoint(_worldLookTargetLocalOffset);
        }

        private Quaternion GetWorldViewTargetRotation()
        {
            if (_worldViewPose == null)
            {
                return Quaternion.identity;
            }

            Vector3 cameraPosition = GetWorldViewTargetPosition();
            Vector3 lookPoint = GetWorldViewLookPoint(cameraPosition);
            Vector3 toTarget = lookPoint - cameraPosition;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                return _worldViewPose.rotation;
            }

            Quaternion lookRotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
            if (Mathf.Abs(_worldTopDownAngleBias) <= 0.001f)
            {
                return lookRotation;
            }

            Vector3 euler = lookRotation.eulerAngles;
            float signedPitch = NormalizeSignedAngle(euler.x);
            signedPitch = Mathf.Clamp(signedPitch + _worldTopDownAngleBias, -89f, 89f);
            return Quaternion.Euler(signedPitch, euler.y, 0f);
        }

        private static float NormalizeSignedAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f)
            {
                angle -= 360f;
            }

            return angle;
        }

        private void TearDownWorldViewPresentation()
        {
            SetGameplayCameraRenderingSuppressed(false);

            if (_worldViewCamera != null)
            {
                _worldViewCamera.enabled = false;
                Destroy(_worldViewCamera.gameObject);
                _worldViewCamera = null;
            }

            _isReturningToGameplayView = false;
            _returnTransitionElapsed = 0f;
        }

        private float GetFloatParameter(string key, float defaultValue)
        {
            object value = GetParameter(key);
            if (value is float floatValue)
            {
                return floatValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            return defaultValue;
        }

        private Vector3 GetVector3Parameter(string key, Vector3 defaultValue)
        {
            object value = GetParameter(key);
            if (value is Vector3 vectorValue)
            {
                return vectorValue;
            }

            return defaultValue;
        }

        private bool GetBoolParameter(string key, bool defaultValue)
        {
            object value = GetParameter(key);
            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is int intValue)
            {
                return intValue != 0;
            }

            if (value is float floatValue)
            {
                return !Mathf.Approximately(floatValue, 0f);
            }

            return defaultValue;
        }

        private void FailSetup(string reason)
        {
            if (!_isSetupValid)
            {
                return;
            }

            _isSetupValid = false;
            _setupFailureReason = reason;
            Debug.LogError($"[ComputerMinigame] {reason}", this);
        }
    }
}
