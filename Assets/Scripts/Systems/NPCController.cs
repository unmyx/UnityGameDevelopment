using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.Core.Events;
using Game.Inventory;
using Game.Minigames;
using Game.Networking;
using Game.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Systems
{
    public class NPCController : MonoBehaviour
    {
        public enum NPCState
        {
            Idle,
            Roaming,
            Chasing
        }

        public enum DetectionForwardSource
        {
            HeadForward,
            RootForward,
            AgentVelocity,
            AgentDesiredVelocity
        }

        [Header("References")]
        // TODO(MP-2): Replace single serialized player target with player-context aware target arbitration.
        [SerializeField] private Transform player;
        [SerializeField] private Transform head;

        [Header("Movement")]
        [SerializeField] private float roamRadius = 6f;
        [SerializeField] private float stoppingDistance = 0.35f;
        [SerializeField] private float roamDelay = 1.5f;
        [SerializeField] private float roamMinTargetDistance = 1.25f;
        [SerializeField] private float roamArriveDelay = 0.5f;

        [Header("Detection")]
        [SerializeField] private float detectionDistance = 8f;
        [SerializeField] private float viewAngle = 90f;
        [SerializeField] private float catchDistance = 1.4f;
        [SerializeField] private float losePlayerDelay = 2f;
        [SerializeField] private float detectionHoldTime = 0.3f;
        [SerializeField] private DetectionForwardSource detectionForwardSource = DetectionForwardSource.HeadForward;
        [SerializeField] private bool usePlanarFovChecks = true;
        [SerializeField] private float closeRangeBypassDistance = 1.75f;
        [SerializeField] private int lineOfSightMask = int.MaxValue;
        [SerializeField] private QueryTriggerInteraction lineOfSightTriggerInteraction = QueryTriggerInteraction.Ignore;
        [SerializeField] private float fallbackEyeHeight = 1.5f;
        [SerializeField] private float targetLockDurationSeconds = 1.25f;
        [SerializeField] private float retargetCooldownSeconds = 0.3f;
        [SerializeField] private float targetBreakDistanceMultiplier = 1.75f;

        [Header("Suspicion")]
        [SerializeField] private float suspicionLevel;
        [SerializeField] private float suspicionIncreasePerSecond = 0.6f;
        [SerializeField] private float suspicionDecayPerSecond = 0.35f;
        [SerializeField] private float suspicionChaseThreshold = 0.65f;

        [Header("Context Zones")]
        [SerializeField]
        [Tooltip("Player inside any configured zone is treated as non-restricted and will not build suspicion.")]
        private List<Collider> nonRestrictedZones = new();

        [Header("Post Minigame")]
        [SerializeField] private float postLieMinigameGraceDuration = 2f;
        [SerializeField] private float networkLieResultTimeoutSeconds = 20f;

        [Header("Post Lie Fail Sequence")]
        [SerializeField] private float postLieFailAlertDuration = 1.5f;
        [SerializeField] private float postLieFailChaseDuration = 3f;
        [SerializeField] private int postLieFailCatchCurrencyPenalty = 10;

        [Header("Lie Minigame Defaults")]
        [SerializeField] private float lieIndicatorSpeed = 2f;
        [SerializeField] private float lieTargetZoneWidth = 0.3f;
        [SerializeField] private int lieMaxAttempts = 1;
        [SerializeField] private int lieDifficulty = 5;
        [SerializeField] private string lieTriggerKey = "Space";
        [SerializeField] private LieDialogueSet lieDialogueSet;
        [SerializeField] private List<LieDialogueSet> lieDialogueSets = new();
        [SerializeField] private bool randomizeLieDialogueSetSelection = true;
        [SerializeField] private string fallbackLieDialogueResourcePath = "Dialogue/Lie/DefaultLieDialogueSet";
        [SerializeField] private string lieDialogueEntryStepId = "intro";
        [SerializeField] private string lieQuestionText = "Hey! What are you doing in here?!";
        [SerializeField] private string lieAnswer1Text = "I'm just going to the bathroom.";
        [SerializeField] private string lieAnswer2Text = "I'm looking for the boss, can you tell me where he is?";
        [SerializeField] private string lieAnswer3Text = "I'm just passing by.";
        [SerializeField] private float lieAnswer1ZoneMultiplier = 1.2f;
        [SerializeField] private float lieAnswer2ZoneMultiplier = 1f;
        [SerializeField] private float lieAnswer3ZoneMultiplier = 0.8f;
        [SerializeField] private float lieSuspicionPenaltyPerRepeat = 0.08f;
        [SerializeField] private float lieSuspicionMinMultiplier = 0.6f;

        [Header("Debug")]
        [SerializeField] private bool enableDebugLogs = true;
        [SerializeField] private bool drawDetectionGizmos = true;

        private NPCState _state = NPCState.Idle;
        private Vector3 _spawnPosition;
        private Vector3 _roamTarget;
        private float _idleTimer;
        private float _roamPauseTimer;
        private bool _hasRoamTarget;
        private bool _isRoamPaused;
        private bool _hadDetectionLastFrame;
        private bool _hadRawDetectionLastFrame;
        private bool _awaitingMinigameEnd;
        private bool _hasLastKnownPlayerPosition;
        private bool _isSearchingLastKnownPosition;
        private bool _isDetectingThisFrame;
        private bool _hasLoggedMissingPlayerReference;
        private bool _hasLoggedMissingHeadReference;
        private NavMeshAgent _agent;
        private Vector3 _freezeProbeStartPosition;
        private Vector3 _lastKnownPlayerPosition;
        private Vector3 _lastDetectionRayOrigin;
        private Vector3 _lastDetectionRayDirection = Vector3.forward;
        private Vector3 _lastDetectionForward = Vector3.forward;
        private Vector3 _lastDetectionTarget;
        private Coroutine _freezeProbeRoutine;
        private Behaviour _fallbackDisabledMovementComponent;
        private float _losePlayerTimer;
        private float _detectionTimer;
        private float _postLieMinigameGraceTimer;
        private float _postLieFailAlertTimer;
        private float _postLieFailChaseTimer;
        private float _lastDetectionDistance;
        private float _lastDetectionAngle;
        private bool _lastDetectionWasInRange;
        private bool _lastDetectionWasInVisionCone;
        private bool _lastDetectionHadLineOfSight;
        private bool _lastDetectionUsedCloseRangeBypass;
        private bool _loggedAgentOffNavMesh;
        private Vector3 _lastLoggedDestination;
        private bool _hasLastLoggedDestination;
        private bool _isPlayerInNonRestrictedZoneThisFrame;
        private bool _isPostLieFailAlertActive;
        private bool _isPostLieFailChaseActive;
        private bool _hasResolvedLieFailResolution;

        private bool hasCaughtPlayer;
        private bool triggeredMinigame;
        private string _activeLieMinigameOwnerPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;
        private ulong _activeTargetClientId = NetworkNpcAuthorityBridge.NoTargetClientId;
        private ulong _lockedTargetClientId = NetworkNpcAuthorityBridge.NoTargetClientId;
        private float _targetLockUntilTime;
        private float _retargetCooldownUntilTime;
        private ulong _lastIssuedCatchToken;
        private string _lastCatchOwnerKey = PlayerContextRegistry.DefaultLocalPlayerId;
        private float _lastCatchServerTime;
        private bool _isCrossingLink = false;
        private int _ownerCatchStateDay = -1;

        private readonly Dictionary<string, NpcCatchStateRuntime> _ownerCatchStates =
            new Dictionary<string, NpcCatchStateRuntime>(System.StringComparer.Ordinal);

        private const int RoamTargetAttempts = 12;
        private const float FreezeProbeDistanceTolerance = 0.05f;
        private const float MinRoamSampleDistance = 0.5f;
        private const float MaxRoamSampleDistance = 1.25f;
        private const float DestinationRefreshThreshold = 0.1f;
        private const float AgentNavPositionSampleRadius = 0.6f;

        public NPCState CurrentNpcState => _state;
        public ulong CurrentTargetClientId => _activeTargetClientId;
        public ulong CurrentCatchToken => _lastIssuedCatchToken;
        public float CurrentLastCatchServerTime => _lastCatchServerTime;
        public string CurrentLastCatchOwnerKey => _lastCatchOwnerKey;
        public bool IsCaughtOrCooldownActive =>
            hasCaughtPlayer
            || _awaitingMinigameEnd
            || _isPostLieFailAlertActive
            || _isPostLieFailChaseActive
            || IsInPostLieMinigameGracePeriod()
            || HasServerCatchCooldownForCurrentTarget();

        private sealed class NpcCatchStateRuntime
        {
            public string ownerKey;
            public ulong targetClientId;
            public float suspicion01;
            public bool isChaseActive;
            public bool isCaught;
            public float cooldownUntilTime;
            public int catchCountThisDay;
            public bool pendingLie;
            public ulong catchToken;
            public float lastCatchServerTime;
        }

        private void Reset()
        {
            if (player == null)
            {
                if (PlayerContextLocator.TryGetLocalPlayerTransform(out Transform playerTransform)
                    && playerTransform != null)
                {
                    player = playerTransform;
                }
                else if (PlayerContextLocator.IsCompatibilityFallbackAllowed()
                         && PlayerContextLocator.TryGetAuthoritativePlayerTransform(out playerTransform)
                         && playerTransform != null)
                {
                    player = playerTransform;
                }
            }

            if (head == null)
            {
                head = transform;
            }
        }

        private void Awake()
        {
            _spawnPosition = transform.position;
            _agent = GetComponent<NavMeshAgent>();
            if (_agent == null)
            {
                Debug.LogError("[NPCController] NavMeshAgent is missing. NPC movement requires NavMeshAgent.");
            }
            else
            {
                _agent.stoppingDistance = Mathf.Max(0f, stoppingDistance);
                _agent.autoBraking = true;
            }

            TryAutoAssignReferences();
        }

        private void OnEnable()
        {
            EventBus.Subscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Subscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void OnDisable()
        {
            EventBus.Unsubscribe<MinigameEndedEvent>(OnMinigameEnded);
            EventBus.Unsubscribe<MinigameCancelledEvent>(OnMinigameCancelled);
        }

        private void Update()
        {
            if (!CanRunAuthoritativeUpdate())
            {
                StopAgent();
                return;
            }

            if (IsAuthoritativeNetworkServer())
            {
                EnsureOwnerCatchStateDayIsCurrent();
                ResolveServerTargetPlayer();
                TryResolveTimedOutPendingLieCatch();
            }
            else
            {
                _activeTargetClientId = NetworkNpcAuthorityBridge.NoTargetClientId;
            }

            TryAutoAssignReferences();

            if (_postLieMinigameGraceTimer > 0f)
            {
                _postLieMinigameGraceTimer = Mathf.Max(0f, _postLieMinigameGraceTimer - Time.deltaTime);
            }

            if (_isPostLieFailAlertActive)
            {
                UpdatePostLieFailAlertPhase();
                return;
            }

            if (_agent.isOnOffMeshLink && !_isCrossingLink)
            {
                StartCoroutine(CrossDoorLink());
                return;
            }

            switch (_state)
            {
                case NPCState.Idle:
                    HandleIdle();
                    break;
                case NPCState.Roaming:
                    HandleRoaming();
                    break;
                case NPCState.Chasing:
                    HandleChasing();
                    break;
            }
        }

        private IEnumerator CrossDoorLink()
        {
            _isCrossingLink = true;

            _agent.autoTraverseOffMeshLink = false;
            OffMeshLinkData data = _agent.currentOffMeshLinkData;
            Vector3 start = _agent.transform.position;
            Vector3 end = data.endPos;

            float duration = Vector3.Distance(start, end) / _agent.speed;
            float t = 0f;

            while (t < 1f)
            {
                t += Time.deltaTime / duration;
                _agent.transform.position = Vector3.Lerp(start, end, t);
                yield return null;
            }

            _agent.CompleteOffMeshLink();
            _isCrossingLink = false;
        }

        private void HandleIdle()
        {
            bool isPlayerDetected = DetectPlayer();
            if (UpdateSuspicionAndShouldEscalate(isPlayerDetected))
            {
                ChangeState(NPCState.Chasing);
                return;
            }

            _idleTimer += Time.deltaTime;
            if (_idleTimer >= roamDelay)
            {
                TryPickRoamTarget();
                ChangeState(NPCState.Roaming);
            }
        }

        private void HandleRoaming()
        {
            bool isPlayerDetected = DetectPlayer();
            if (UpdateSuspicionAndShouldEscalate(isPlayerDetected))
            {
                ChangeState(NPCState.Chasing);
                return;
            }

            if (!_hasRoamTarget)
            {
                TryPickRoamTarget();
                return;
            }

            if (_isRoamPaused)
            {
                _roamPauseTimer -= Time.deltaTime;
                if (_roamPauseTimer <= 0f)
                {
                    _isRoamPaused = false;
                    TryPickRoamTarget();
                }

                return;
            }

            if (ShouldRefreshRoamDestination())
            {
                SetAgentDestination(_roamTarget, "Roaming");
            }

            bool reached = HasReachedDestination();
            if (reached)
            {
                StopAgent();
                _isRoamPaused = true;
                _roamPauseTimer = roamArriveDelay;
            }
        }

        private bool UpdateSuspicionAndShouldEscalate(bool isRawDetected)
        {
            bool isInNonRestrictedZone = IsPlayerInNonRestrictedZone();
            _isPlayerInNonRestrictedZoneThisFrame = isInNonRestrictedZone;

            float increaseRate = Mathf.Max(0f, suspicionIncreasePerSecond);
            float decayRate = Mathf.Max(0f, suspicionDecayPerSecond);
            float threshold = Mathf.Clamp01(suspicionChaseThreshold);

            string ownerKeyForSuspicion = ResolveCurrentTargetOwnerKey();
            NpcCatchStateRuntime trackedState = GetOrCreateOwnerCatchState(ownerKeyForSuspicion, _activeTargetClientId);
            if (trackedState != null)
            {
                if (isRawDetected && !isInNonRestrictedZone)
                {
                    trackedState.suspicion01 += increaseRate * Time.deltaTime;
                }
                else
                {
                    trackedState.suspicion01 -= decayRate * Time.deltaTime;
                }

                trackedState.suspicion01 = Mathf.Clamp01(trackedState.suspicion01);
                trackedState.isChaseActive = isRawDetected && !isInNonRestrictedZone && trackedState.suspicion01 >= threshold;
                suspicionLevel = trackedState.suspicion01;
                return trackedState.isChaseActive;
            }

            if (isRawDetected && !isInNonRestrictedZone)
            {
                suspicionLevel += increaseRate * Time.deltaTime;
            }
            else
            {
                suspicionLevel -= decayRate * Time.deltaTime;
            }

            suspicionLevel = Mathf.Clamp01(suspicionLevel);
            return isRawDetected && !isInNonRestrictedZone && suspicionLevel >= threshold;
        }

        private bool IsPlayerInNonRestrictedZone()
        {
            if (player == null || nonRestrictedZones == null || nonRestrictedZones.Count == 0)
            {
                return false;
            }

            Vector3 playerPosition = player.position;
            Vector3 playerDetectionTarget = GetPlayerDetectionTarget();

            for (int i = 0; i < nonRestrictedZones.Count; i++)
            {
                Collider zone = nonRestrictedZones[i];
                if (zone == null || !zone.enabled || !zone.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Bounds bounds = zone.bounds;
                if (bounds.Contains(playerPosition) || bounds.Contains(playerDetectionTarget))
                {
                    return true;
                }
            }

            return false;
        }

        private void HandleChasing()
        {
            // Freeze NPC movement/retargeting while waiting for minigame completion.
            if (_awaitingMinigameEnd)
            {
                StopAgent();
                return;
            }

            if (_isPostLieFailChaseActive)
            {
                UpdatePostLieFailChasePhase();
                return;
            }

            bool isPlayerDetected = DetectPlayer();
            if (isPlayerDetected)
            {
                _losePlayerTimer = 0f;
                _isSearchingLastKnownPosition = false;

                if (player != null)
                {
                    _lastKnownPlayerPosition = player.position;
                    _hasLastKnownPlayerPosition = true;
                    SetAgentDestination(player.position, "Chasing");

                    float distanceToPlayer = Vector3.Distance(transform.position, player.position);
                    if (!hasCaughtPlayer && !IsInPostLieMinigameGracePeriod() && distanceToPlayer <= catchDistance)
                    {
                        if (IsPauseBlockingLieTrigger())
                        {
                            return;
                        }

                        Log("Player caught.");
                        HandleAuthoritativeCatch();
                        return;
                    }
                }
            }
            else
            {
                _losePlayerTimer += Time.deltaTime;

                bool reachedLastKnownPosition = true;
                if (_hasLastKnownPlayerPosition)
                {
                    _isSearchingLastKnownPosition = true;
                    SetAgentDestination(_lastKnownPlayerPosition, "LastKnownPosition");
                    reachedLastKnownPosition = HasReachedDestination();
                }

                if (_losePlayerTimer >= losePlayerDelay && reachedLastKnownPosition)
                {
                    _isSearchingLastKnownPosition = false;
                    _losePlayerTimer = 0f;
                    _detectionTimer = 0f;
                    ChangeState(NPCState.Roaming);
                    return;
                }
            }
        }

        private bool DetectPlayer()
        {
            if (player == null || head == null)
            {
                _hadDetectionLastFrame = false;
                _hadRawDetectionLastFrame = false;
                _isDetectingThisFrame = false;
                _lastDetectionHadLineOfSight = false;
                return false;
            }

            if (IsInPostLieMinigameGracePeriod())
            {
                _detectionTimer = 0f;
                _hadDetectionLastFrame = false;
                _hadRawDetectionLastFrame = false;
                _isDetectingThisFrame = false;
                _lastDetectionHadLineOfSight = false;
                return false;
            }

            Vector3 detectionTarget = GetPlayerDetectionTarget();
            Vector3 detectionOrigin = GetDetectionOrigin();
            Vector3 detectionForward = GetDetectionForward();
            Vector3 toPlayer = detectionTarget - detectionOrigin;
            float distanceToPlayer = toPlayer.magnitude;
            Vector3 directionToPlayer = distanceToPlayer > 0.001f ? toPlayer / distanceToPlayer : detectionForward;

            Vector3 directionForFov = GetDirectionForFov(directionToPlayer, detectionForward);
            float distanceForRange = usePlanarFovChecks
                ? Vector3.Distance(FlattenY(detectionOrigin), FlattenY(detectionTarget))
                : distanceToPlayer;

            _lastDetectionRayOrigin = detectionOrigin;
            _lastDetectionRayDirection = directionToPlayer;
            _lastDetectionForward = detectionForward;
            _lastDetectionTarget = detectionTarget;
            _lastDetectionDistance = distanceForRange;

            Debug.DrawRay(detectionOrigin, directionToPlayer * detectionDistance, Color.yellow);
            Debug.DrawRay(detectionOrigin, detectionForward * Mathf.Min(2f, detectionDistance), Color.cyan);

            bool isInRange = distanceForRange <= detectionDistance;
            float angleToPlayer = Vector3.Angle(detectionForward, directionForFov);
            bool usedCloseRangeBypass = distanceForRange <= closeRangeBypassDistance;
            bool isInVisionCone = usedCloseRangeBypass || angleToPlayer <= viewAngle * 0.5f;

            _lastDetectionAngle = angleToPlayer;
            _lastDetectionWasInRange = isInRange;
            _lastDetectionWasInVisionCone = isInVisionCone;
            _lastDetectionUsedCloseRangeBypass = usedCloseRangeBypass;

            bool rawDetected = false;
            float rayDistance = Mathf.Min(detectionDistance, distanceToPlayer + 0.25f);
            bool hasLineOfSight = false;
            if (isInRange && isInVisionCone && Physics.Raycast(detectionOrigin, directionToPlayer, out RaycastHit hit, rayDistance, lineOfSightMask, lineOfSightTriggerInteraction))
            {
                Transform hitTransform = hit.transform;
                hasLineOfSight = IsPlayerHit(hitTransform);
                rawDetected = hasLineOfSight;
            }

            _lastDetectionHadLineOfSight = hasLineOfSight;

            if (rawDetected)
            {
                _detectionTimer = detectionHoldTime;
                _lastKnownPlayerPosition = player.position;
                _hasLastKnownPlayerPosition = true;
            }
            else
            {
                _detectionTimer = Mathf.Max(0f, _detectionTimer - Time.deltaTime);
            }

            bool detected = rawDetected || _detectionTimer > 0f;
            _isDetectingThisFrame = detected;

            if (rawDetected && !_hadRawDetectionLastFrame)
            {
                Log("Player detected.");
            }

            _hadRawDetectionLastFrame = rawDetected;
            _hadDetectionLastFrame = detected;
            return detected;
        }

        private Vector3 GetPlayerDetectionTarget()
        {
            if (player == null)
            {
                return transform.position;
            }

            if (player.TryGetComponent(out CharacterController characterController))
            {
                return characterController.bounds.center;
            }

            return player.position + Vector3.up * 0.9f;
        }

        private Vector3 GetDetectionOrigin()
        {
            if (head != null && head != transform)
            {
                return head.position;
            }

            return transform.position + Vector3.up * Mathf.Max(0f, fallbackEyeHeight);
        }

        private Vector3 GetDetectionForward()
        {
            Vector3 forward;
            switch (detectionForwardSource)
            {
                case DetectionForwardSource.RootForward:
                    forward = transform.forward;
                    break;
                case DetectionForwardSource.AgentVelocity:
                    forward = _agent != null && _agent.velocity.sqrMagnitude > 0.01f ? _agent.velocity.normalized : Vector3.zero;
                    break;
                case DetectionForwardSource.AgentDesiredVelocity:
                    forward = _agent != null && _agent.desiredVelocity.sqrMagnitude > 0.01f ? _agent.desiredVelocity.normalized : Vector3.zero;
                    break;
                case DetectionForwardSource.HeadForward:
                default:
                    forward = head != null ? head.forward : Vector3.zero;
                    break;
            }

            if (forward.sqrMagnitude <= 0.0001f)
            {
                forward = transform.forward;
            }

            if (usePlanarFovChecks)
            {
                forward = FlattenY(forward);
                if (forward.sqrMagnitude <= 0.0001f)
                {
                    forward = FlattenY(transform.forward);
                }
            }

            return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        }

        private Vector3 GetDirectionForFov(Vector3 directionToPlayer, Vector3 fallbackForward)
        {
            if (!usePlanarFovChecks)
            {
                return directionToPlayer;
            }

            Vector3 planarDirection = FlattenY(directionToPlayer);
            if (planarDirection.sqrMagnitude <= 0.0001f)
            {
                return fallbackForward;
            }

            return planarDirection.normalized;
        }

        private static Vector3 FlattenY(Vector3 vector)
        {
            vector.y = 0f;
            return vector;
        }

        private bool IsPlayerHit(Transform hitTransform)
        {
            if (player == null || hitTransform == null)
            {
                return false;
            }

            if (hitTransform == player || hitTransform.IsChildOf(player))
            {
                return true;
            }

            Transform playerParent = player.parent;
            return playerParent != null && (hitTransform == playerParent || hitTransform.IsChildOf(playerParent));
        }

        private void TryCatchPlayer()
        {
            if (player == null)
            {
                return;
            }

            float distanceToPlayer = Vector3.Distance(transform.position, player.position);
            if (!hasCaughtPlayer && distanceToPlayer < catchDistance)
            {
                if (IsPauseBlockingLieTrigger())
                {
                    return;
                }

                Log("Player caught.");
                HandleAuthoritativeCatch();
            }
        }

        private void HandleAuthoritativeCatch()
        {
            if (IsAuthoritativeNetworkServer())
            {
                string ownerKey = ResolveCurrentTargetOwnerKey();
                NpcCatchStateRuntime ownerState = GetOrCreateOwnerCatchState(ownerKey, _activeTargetClientId);
                if (ownerState == null)
                {
                    return;
                }

                float now = Time.time;
                if (ownerState.isCaught || ownerState.cooldownUntilTime > now)
                {
                    return;
                }

                ownerState.isCaught = true;
                ownerState.pendingLie = true;
                ownerState.catchToken++;
                ownerState.lastCatchServerTime = now;
                ownerState.catchCountThisDay = Mathf.Max(0, ownerState.catchCountThisDay) + 1;
                ownerState.targetClientId = _activeTargetClientId;
                hasCaughtPlayer = true;
                _awaitingMinigameEnd = true;
                triggeredMinigame = false;
                _lastIssuedCatchToken = ownerState.catchToken;
                _lastCatchOwnerKey = ownerState.ownerKey;
                _lastCatchServerTime = now;

                NetworkSessionProgressAuthority.TryRegisterNpcCatch(
                    ownerState.targetClientId,
                    ownerState.ownerKey,
                    ownerState.catchToken,
                    ResolveNpcNetworkObjectId(),
                    ownerState.lastCatchServerTime,
                    ownerState.pendingLie);
                return;
            }

            TriggerLieMinigame();
        }

        private void TriggerLieMinigame()
        {
            if (IsPauseBlockingLieTrigger())
            {
                return;
            }

            if (_awaitingMinigameEnd)
            {
                return;
            }

            Log("Triggering lie minigame.");
            MinigameManager manager = MinigameManager.Instance;
            if (manager != null)
            {
                MinigameData data = BuildLieMinigameData();
                string ownerPlayerId = PlayerInventoryAuthority.GetLocalOwnerPlayerId();
                IMinigame active = manager.StartMinigame<LieMinigame>(data, ownerPlayerId);
                if (active != null)
                {
                    hasCaughtPlayer = true;
                    _awaitingMinigameEnd = true;
                    triggeredMinigame = true;
                    _activeLieMinigameOwnerPlayerId = ownerPlayerId;

                    if (player != null)
                    {
                        _freezeProbeStartPosition = player.position;
                        if (_freezeProbeRoutine != null)
                        {
                            StopCoroutine(_freezeProbeRoutine);
                        }

                        _freezeProbeRoutine = StartCoroutine(VerifyPlayerFreezeAfterTrigger());
                    }
                }
                else
                {
                    hasCaughtPlayer = false;
                    _awaitingMinigameEnd = false;
                    triggeredMinigame = false;
                    _activeLieMinigameOwnerPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;
                    Debug.LogWarning("[NPCController] Lie minigame did not start (another minigame may already be active).");
                }
            }
            else
            {
                hasCaughtPlayer = false;
                _awaitingMinigameEnd = false;
                triggeredMinigame = false;
                _activeLieMinigameOwnerPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;
                Debug.LogWarning("[NPCController] MinigameManager instance not found.");
            }
        }

        private static bool IsPauseBlockingLieTrigger()
        {
            PauseManager pauseManager = PauseManager.Instance;
            return pauseManager != null && pauseManager.IsPaused;
        }

        private MinigameData BuildLieMinigameData()
        {
            MinigameData data = new MinigameData
            {
                minigameId = "lie_detection",
                displayName = "Detect the Lie",
                difficulty = lieDifficulty
            };

            data.SetParameter("indicator_speed", lieIndicatorSpeed);
            data.SetParameter("target_zone_width", lieTargetZoneWidth);
            data.SetParameter("max_attempts", lieMaxAttempts);
            data.SetParameter("show_target_zone", true);
            data.SetParameter("trigger_key", lieTriggerKey);

            LieDialogueSet selectedDialogueSet = ResolveLieDialogueSet();
            if (selectedDialogueSet != null)
            {
                data.SetParameter("lie_dialogue_set", selectedDialogueSet);

                if (!string.IsNullOrWhiteSpace(lieDialogueEntryStepId))
                {
                    data.SetParameter("lie_dialogue_step_id", lieDialogueEntryStepId);
                }
            }
            else if (!string.IsNullOrWhiteSpace(fallbackLieDialogueResourcePath))
            {
                data.SetParameter("lie_dialogue_set_path", fallbackLieDialogueResourcePath);

                if (!string.IsNullOrWhiteSpace(lieDialogueEntryStepId))
                {
                    data.SetParameter("lie_dialogue_step_id", lieDialogueEntryStepId);
                }
            }
            else
            {
                data.SetParameter("npc_question", lieQuestionText);

                data.SetParameter("answer_1_id", "excuse_bathroom");
                data.SetParameter("answer_1_text", lieAnswer1Text);
                data.SetParameter("answer_1_zone_multiplier", lieAnswer1ZoneMultiplier);

                data.SetParameter("answer_2_id", "excuse_find_boss");
                data.SetParameter("answer_2_text", lieAnswer2Text);
                data.SetParameter("answer_2_zone_multiplier", lieAnswer2ZoneMultiplier);

                data.SetParameter("answer_3_id", "excuse_passing_by");
                data.SetParameter("answer_3_text", lieAnswer3Text);
                data.SetParameter("answer_3_zone_multiplier", lieAnswer3ZoneMultiplier);

                data.SetParameter("suspicion_penalty_per_repeat", lieSuspicionPenaltyPerRepeat);
                data.SetParameter("suspicion_min_multiplier", lieSuspicionMinMultiplier);
            }

            return data;
        }

        private LieDialogueSet ResolveLieDialogueSet()
        {
            if (lieDialogueSet != null)
            {
                return lieDialogueSet;
            }

            if (lieDialogueSets == null || lieDialogueSets.Count == 0)
            {
                return null;
            }

            List<LieDialogueSet> validDialogueSets = new List<LieDialogueSet>();
            for (int i = 0; i < lieDialogueSets.Count; i++)
            {
                if (lieDialogueSets[i] != null)
                {
                    validDialogueSets.Add(lieDialogueSets[i]);
                }
            }

            if (validDialogueSets.Count == 0)
            {
                return null;
            }

            if (!randomizeLieDialogueSetSelection)
            {
                return validDialogueSets[0];
            }

            int index = Random.Range(0, validDialogueSets.Count);
            return validDialogueSets[index];
        }

        private IEnumerator VerifyPlayerFreezeAfterTrigger()
        {
            yield return null;
            yield return null;

            if (player == null)
            {
                yield break;
            }

            bool stateLocked = GameManager.Instance != null && GameManager.Instance.CurrentState == GameState.Minigame;
            bool movedAfterTrigger = Vector3.Distance(_freezeProbeStartPosition, player.position) > FreezeProbeDistanceTolerance;

            if (!stateLocked || movedAfterTrigger)
            {
                TryDisableFallbackMovementComponent();
            }
        }

        private void TryDisableFallbackMovementComponent()
        {
            if (player == null || _fallbackDisabledMovementComponent != null)
            {
                return;
            }

            Behaviour[] behaviours = player.GetComponents<Behaviour>();
            Behaviour bestCandidate = null;
            int bestScore = -1;

            for (int i = 0; i < behaviours.Length; i++)
            {
                Behaviour behaviour = behaviours[i];
                if (behaviour == null || !behaviour.enabled || behaviour == this)
                {
                    continue;
                }

                System.Type type = behaviour.GetType();
                int score = 0;

                if (!string.IsNullOrEmpty(type.Namespace) && type.Namespace.Contains("Player"))
                {
                    score += 3;
                }

                if (type.Name.Contains("Controller"))
                {
                    score += 4;
                }

                if (type.Name.Contains("Input"))
                {
                    score += 2;
                }

                if (type.GetMethod("GetVelocity") != null)
                {
                    score += 3;
                }

                if (type.GetProperty("MovementInput") != null)
                {
                    score += 3;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = behaviour;
                }
            }

            if (bestCandidate == null)
            {
                return;
            }

            _fallbackDisabledMovementComponent = bestCandidate;
            _fallbackDisabledMovementComponent.enabled = false;
            Log($"Fallback freeze disabled component: {_fallbackDisabledMovementComponent.GetType().Name}");
        }

        public void ResetAfterMinigame()
        {
            hasCaughtPlayer = false;
            triggeredMinigame = false;
            _awaitingMinigameEnd = false;
            _isPostLieFailAlertActive = false;
            _isPostLieFailChaseActive = false;
            _hasResolvedLieFailResolution = false;
            _hadDetectionLastFrame = false;
            _hadRawDetectionLastFrame = false;
            _detectionTimer = 0f;
            _losePlayerTimer = 0f;
            _idleTimer = 0f;
            _isRoamPaused = false;
            _hasRoamTarget = false;
            _isSearchingLastKnownPosition = false;
            suspicionLevel = 0f;
            _isPlayerInNonRestrictedZoneThisFrame = false;
            _postLieMinigameGraceTimer = Mathf.Max(0f, postLieMinigameGraceDuration);
            _postLieFailAlertTimer = 0f;
            _postLieFailChaseTimer = 0f;
            _activeLieMinigameOwnerPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;

            if (_freezeProbeRoutine != null)
            {
                StopCoroutine(_freezeProbeRoutine);
                _freezeProbeRoutine = null;
            }

            if (_fallbackDisabledMovementComponent != null)
            {
                _fallbackDisabledMovementComponent.enabled = true;
                Log($"Re-enabled fallback component: {_fallbackDisabledMovementComponent.GetType().Name}");
                _fallbackDisabledMovementComponent = null;
            }

            ChangeState(NPCState.Idle);
        }

        private void OnValidate()
        {
            suspicionLevel = Mathf.Clamp01(suspicionLevel);
            suspicionIncreasePerSecond = Mathf.Max(0f, suspicionIncreasePerSecond);
            suspicionDecayPerSecond = Mathf.Max(0f, suspicionDecayPerSecond);
            suspicionChaseThreshold = Mathf.Clamp01(suspicionChaseThreshold);
        }

        private bool IsInPostLieMinigameGracePeriod()
        {
            return _postLieMinigameGraceTimer > 0f;
        }

        private void OnMinigameEnded(MinigameEndedEvent eventData)
        {
            if (!triggeredMinigame)
            {
                return;
            }

            if (!string.Equals(eventData.MinigameId, "lie_detection", System.StringComparison.Ordinal))
            {
                return;
            }

            if (!string.Equals(eventData.OwnerPlayerId, _activeLieMinigameOwnerPlayerId, System.StringComparison.Ordinal))
            {
                return;
            }

            if (eventData.Result == MinigameResult.Fail || eventData.Result == MinigameResult.Timeout)
            {
                HandleLieFailResolution();
                return;
            }

            ResetAfterMinigame();
        }

        private void OnMinigameCancelled(MinigameCancelledEvent eventData)
        {
            if (!triggeredMinigame)
            {
                return;
            }

            if (!string.Equals(eventData.MinigameId, "lie_detection", System.StringComparison.Ordinal))
            {
                return;
            }

            if (!string.Equals(eventData.OwnerPlayerId, _activeLieMinigameOwnerPlayerId, System.StringComparison.Ordinal))
            {
                return;
            }

            ResetAfterMinigame();
        }

        private void HandleLieFailResolution()
        {
            if (_hasResolvedLieFailResolution)
            {
                return;
            }

            _hasResolvedLieFailResolution = true;
            ApplyLieFailStolenLootConfiscation();
            ApplyLieFailImmediateFine();

            bool isArrested = false;
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                isArrested = gameManager.RegisterFailedLieEscalation();
            }

            if (isArrested)
            {
                ResetAfterMinigame();
                return;
            }

            StartPostLieFailSequence();
        }

        private void StartPostLieFailSequence()
        {
            triggeredMinigame = false;
            hasCaughtPlayer = false;
            _awaitingMinigameEnd = false;
            _isPostLieFailAlertActive = true;
            _isPostLieFailChaseActive = false;
            _postLieFailAlertTimer = Mathf.Max(0f, postLieFailAlertDuration);
            _postLieFailChaseTimer = 0f;

            if (_freezeProbeRoutine != null)
            {
                StopCoroutine(_freezeProbeRoutine);
                _freezeProbeRoutine = null;
            }

            if (_fallbackDisabledMovementComponent != null)
            {
                _fallbackDisabledMovementComponent.enabled = true;
                Log($"Re-enabled fallback component: {_fallbackDisabledMovementComponent.GetType().Name}");
                _fallbackDisabledMovementComponent = null;
            }

            StopAgent();
        }

        private void UpdatePostLieFailAlertPhase()
        {
            StopAgent();

            if (_postLieFailAlertTimer > 0f)
            {
                _postLieFailAlertTimer = Mathf.Max(0f, _postLieFailAlertTimer - Time.deltaTime);
                if (_postLieFailAlertTimer > 0f)
                {
                    return;
                }
            }

            BeginPostLieFailChasePhase();
        }

        private void BeginPostLieFailChasePhase()
        {
            _awaitingMinigameEnd = false;
            hasCaughtPlayer = false;
            _isPostLieFailAlertActive = false;
            _isPostLieFailChaseActive = true;
            _postLieFailAlertTimer = 0f;
            _postLieFailChaseTimer = Mathf.Max(0f, postLieFailChaseDuration);
            _postLieMinigameGraceTimer = 0f;
            _losePlayerTimer = 0f;
            _isSearchingLastKnownPosition = false;

            if (player != null)
            {
                _lastKnownPlayerPosition = player.position;
                _hasLastKnownPlayerPosition = true;
                SetAgentDestination(player.position, "PostLieFailChase");
            }

            ChangeState(NPCState.Chasing);
        }

        private void UpdatePostLieFailChasePhase()
        {
            _postLieFailChaseTimer = Mathf.Max(0f, _postLieFailChaseTimer - Time.deltaTime);

            if (_postLieFailChaseTimer <= 0f)
            {
                EndPostLieFailSequence();
                return;
            }

            if (player == null)
            {
                StopAgent();
                return;
            }

            _lastKnownPlayerPosition = player.position;
            _hasLastKnownPlayerPosition = true;
            SetAgentDestination(player.position, "PostLieFailChase");

            float distanceToPlayer = Vector3.Distance(transform.position, player.position);
            if (distanceToPlayer <= catchDistance)
            {
                EndPostLieFailSequence();
            }
        }

        private void EndPostLieFailSequence()
        {
            ResetAfterMinigame();
        }

        private void ApplyLieFailImmediateFine()
        {
            if (postLieFailCatchCurrencyPenalty <= 0)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            gameManager.ModifyCurrency(-postLieFailCatchCurrencyPenalty);
        }

        private void ApplyLieFailStolenLootConfiscation(string ownerOverride = null)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
            {
                return;
            }

            if (IsNonAuthoritativeNetworkClient())
            {
                return;
            }

            string ownerPlayerId = string.IsNullOrWhiteSpace(ownerOverride)
                ? ResolveCaughtPlayerOwnerKey()
                : ownerOverride.Trim();
            List<StolenLootEntryData> stolenLootToConfiscate = gameManager.ConsumeDayStolenLoot(ownerPlayerId);
            if (stolenLootToConfiscate == null || stolenLootToConfiscate.Count <= 0)
            {
                return;
            }

            InventorySystem inventorySystem = InventorySystem.Instance;
            if (inventorySystem == null)
            {
                return;
            }

            bool shouldMutateServerInventory = true;
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager != null
                && networkManager.IsListening
                && networkManager.IsServer
                && NetworkOwnerKeyUtility.IsNetworkOwnerKey(ownerPlayerId))
            {
                string localOwnerKey = NetworkOwnerKeyUtility.GetOwnerKeyForSender(networkManager.LocalClientId);
                shouldMutateServerInventory = string.Equals(ownerPlayerId, localOwnerKey, System.StringComparison.Ordinal);
            }

            for (int i = 0; i < stolenLootToConfiscate.Count; i++)
            {
                StolenLootEntryData stolenEntry = stolenLootToConfiscate[i];
                if (stolenEntry == null)
                {
                    continue;
                }

                string targetItemId = string.IsNullOrWhiteSpace(stolenEntry.itemId)
                    ? string.Empty
                    : stolenEntry.itemId.Trim();
                int remainingToConfiscate = Mathf.Max(0, stolenEntry.count);
                if (string.IsNullOrEmpty(targetItemId) || remainingToConfiscate <= 0)
                {
                    continue;
                }

                if (shouldMutateServerInventory)
                {
                    inventorySystem.RemoveItemsByItemId(targetItemId, remainingToConfiscate, ownerPlayerId);
                }
            }

            if (networkManager != null && networkManager.IsListening && networkManager.IsServer && player != null)
            {
                NetworkObject playerNetworkObject = player.GetComponent<NetworkObject>();
                if (playerNetworkObject != null && playerNetworkObject.IsSpawned)
                {
                    NetworkSessionProgressAuthority.TrySyncStolenLootSnapshotToClient(playerNetworkObject.OwnerClientId, ownerPlayerId);
                }
            }
        }

        private string ResolveCaughtPlayerOwnerKey()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager != null && networkManager.IsListening && networkManager.IsServer && player != null)
            {
                NetworkObject playerNetworkObject = player.GetComponent<NetworkObject>();
                if (playerNetworkObject != null && playerNetworkObject.IsSpawned)
                {
                    return NetworkOwnerKeyUtility.GetOwnerKeyForSender(playerNetworkObject.OwnerClientId);
                }
            }

            return PlayerInventoryAuthority.GetLocalOwnerPlayerId();
        }

        private string ResolveCurrentTargetOwnerKey()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager != null
                && networkManager.IsListening
                && networkManager.IsServer
                && _activeTargetClientId != NetworkNpcAuthorityBridge.NoTargetClientId)
            {
                return NetworkOwnerKeyUtility.GetOwnerKeyForSender(_activeTargetClientId);
            }

            return PlayerInventoryAuthority.GetLocalOwnerPlayerId();
        }

        public MinigameData BuildNetworkLieMinigameData()
        {
            return BuildLieMinigameData();
        }

        public void HandleAuthoritativeClientDisconnect(ulong disconnectedClientId)
        {
            if (!IsAuthoritativeNetworkServer() || disconnectedClientId == NetworkNpcAuthorityBridge.NoTargetClientId)
            {
                return;
            }

            string ownerKey = NetworkOwnerKeyUtility.GetOwnerKeyForSender(disconnectedClientId);
            if (_ownerCatchStates.TryGetValue(ownerKey, out NpcCatchStateRuntime ownerState) && ownerState != null)
            {
                ownerState.targetClientId = NetworkNpcAuthorityBridge.NoTargetClientId;

                if (ownerState.pendingLie || ownerState.isCaught)
                {
                    ResolveAuthoritativeLieOutcome(ownerState, MinigameResult.Timeout, out _);
                }
            }

            if (_activeTargetClientId == disconnectedClientId || _lockedTargetClientId == disconnectedClientId)
            {
                _activeTargetClientId = NetworkNpcAuthorityBridge.NoTargetClientId;
                _lockedTargetClientId = NetworkNpcAuthorityBridge.NoTargetClientId;
                _targetLockUntilTime = 0f;
                _retargetCooldownUntilTime = 0f;
                player = null;
            }
        }

        public bool TryResolveAuthoritativeLieResult(
            ulong senderClientId,
            ulong catchToken,
            MinigameResult result,
            out bool appliedConsequence,
            out string reason)
        {
            appliedConsequence = false;
            reason = string.Empty;

            if (!IsAuthoritativeNetworkServer())
            {
                reason = "NPC authority unavailable.";
                return false;
            }

            if (catchToken == 0UL)
            {
                reason = "Invalid catch token.";
                return false;
            }

            string ownerKey = NetworkOwnerKeyUtility.GetOwnerKeyForSender(senderClientId);
            if (!_ownerCatchStates.TryGetValue(ownerKey, out NpcCatchStateRuntime ownerState) || ownerState == null)
            {
                reason = "No pending catch state.";
                return false;
            }

            if (!ownerState.pendingLie)
            {
                reason = "Catch already resolved.";
                return false;
            }

            if (ownerState.catchToken != catchToken)
            {
                reason = "Catch token mismatch.";
                return false;
            }

            if (ownerState.targetClientId != senderClientId)
            {
                reason = "Sender does not own this catch.";
                return false;
            }

            ResolveAuthoritativeLieOutcome(ownerState, result, out appliedConsequence);
            return true;
        }

        private void ResolveAuthoritativeLieOutcome(
            NpcCatchStateRuntime ownerState,
            MinigameResult result,
            out bool appliedConsequence)
        {
            appliedConsequence = false;
            if (ownerState == null)
            {
                return;
            }

            bool shouldApplyPenalty = result == MinigameResult.Fail
                                      || result == MinigameResult.Timeout
                                      || result == MinigameResult.Cancelled;
            if (shouldApplyPenalty)
            {
                ApplyLieFailStolenLootConfiscation(ownerState.ownerKey);
                ApplyLieFailImmediateFine();

                GameManager gameManager = GameManager.Instance;
                if (gameManager != null)
                {
                    gameManager.RegisterFailedLieEscalation();
                }

                appliedConsequence = true;
            }

            ownerState.pendingLie = false;
            ownerState.isCaught = false;
            ownerState.lastCatchServerTime = Time.time;
            ownerState.cooldownUntilTime = Time.time + Mathf.Max(0.05f, postLieMinigameGraceDuration);
            hasCaughtPlayer = false;
            _awaitingMinigameEnd = false;
            triggeredMinigame = false;
            _activeLieMinigameOwnerPlayerId = PlayerContextRegistry.DefaultLocalPlayerId;
            ResetAfterMinigame();
        }

        private void TryResolveTimedOutPendingLieCatch()
        {
            if (!_awaitingMinigameEnd || string.IsNullOrWhiteSpace(_lastCatchOwnerKey))
            {
                return;
            }

            if (!_ownerCatchStates.TryGetValue(_lastCatchOwnerKey, out NpcCatchStateRuntime ownerState)
                || ownerState == null
                || !ownerState.pendingLie)
            {
                hasCaughtPlayer = false;
                _awaitingMinigameEnd = false;
                return;
            }

            bool targetConnected = false;
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager != null
                && networkManager.IsListening
                && networkManager.IsServer)
            {
                targetConnected = networkManager.ConnectedClients.ContainsKey(ownerState.targetClientId);
            }

            float timeoutSeconds = Mathf.Max(2f, networkLieResultTimeoutSeconds);
            bool isTimedOut = Time.time - ownerState.lastCatchServerTime >= timeoutSeconds;
            if (targetConnected && !isTimedOut)
            {
                return;
            }

            ResolveAuthoritativeLieOutcome(ownerState, MinigameResult.Timeout, out _);
        }

        private ulong ResolveNpcNetworkObjectId()
        {
            NetworkObject networkObject = GetComponent<NetworkObject>();
            return networkObject != null && networkObject.IsSpawned ? networkObject.NetworkObjectId : 0UL;
        }

        private bool TryGetConnectedPlayerTransform(ulong clientId, out Transform playerTransform)
        {
            playerTransform = null;
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null
                || !networkManager.IsListening
                || !networkManager.IsServer
                || !networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient candidateClient)
                || candidateClient == null
                || candidateClient.PlayerObject == null
                || !candidateClient.PlayerObject.IsSpawned)
            {
                return false;
            }

            Transform candidateTransform = candidateClient.PlayerObject.transform;
            if (candidateTransform == null || !candidateTransform.gameObject.activeInHierarchy)
            {
                return false;
            }

            playerTransform = candidateTransform;
            return true;
        }

        private void EnsureOwnerCatchStateDayIsCurrent()
        {
            GameManager gameManager = GameManager.Instance;
            int day = gameManager != null ? gameManager.GetCurrentDay() : 1;
            if (day == _ownerCatchStateDay)
            {
                return;
            }

            _ownerCatchStateDay = day;
            _ownerCatchStates.Clear();
            _lastIssuedCatchToken = 0;
            _lastCatchOwnerKey = PlayerContextRegistry.DefaultLocalPlayerId;
            _lastCatchServerTime = 0f;
        }

        private NpcCatchStateRuntime GetOrCreateOwnerCatchState(string ownerKey, ulong targetClientId)
        {
            string normalizedOwnerKey = string.IsNullOrWhiteSpace(ownerKey)
                ? PlayerContextRegistry.DefaultLocalPlayerId
                : ownerKey.Trim();

            if (!_ownerCatchStates.TryGetValue(normalizedOwnerKey, out NpcCatchStateRuntime state) || state == null)
            {
                state = new NpcCatchStateRuntime
                {
                    ownerKey = normalizedOwnerKey,
                    targetClientId = targetClientId,
                    suspicion01 = 0f,
                    isChaseActive = false,
                    isCaught = false,
                    cooldownUntilTime = 0f,
                    catchCountThisDay = 0,
                    pendingLie = false,
                    catchToken = 0,
                    lastCatchServerTime = 0f
                };
                _ownerCatchStates[normalizedOwnerKey] = state;
            }
            else if (targetClientId != NetworkNpcAuthorityBridge.NoTargetClientId)
            {
                state.targetClientId = targetClientId;
            }

            return state;
        }

        private bool HasServerCatchCooldownForCurrentTarget()
        {
            if (!IsAuthoritativeNetworkServer())
            {
                return false;
            }

            string ownerKey = ResolveCurrentTargetOwnerKey();
            if (!_ownerCatchStates.TryGetValue(ownerKey, out NpcCatchStateRuntime state) || state == null)
            {
                return false;
            }

            return state.cooldownUntilTime > Time.time || state.isCaught;
        }

        private static bool IsNonAuthoritativeNetworkClient()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening && manager.IsClient && !manager.IsServer;
        }

        private static bool IsAuthoritativeNetworkServer()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening && manager.IsServer;
        }

        private static bool CanRunAuthoritativeUpdate()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
            {
                return true;
            }

            return manager.IsServer;
        }

        private void ResolveServerTargetPlayer()
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager == null || !networkManager.IsListening || !networkManager.IsServer)
            {
                return;
            }

            if (_lockedTargetClientId != NetworkNpcAuthorityBridge.NoTargetClientId
                && TryGetConnectedPlayerTransform(_lockedTargetClientId, out Transform lockedTransform)
                && lockedTransform != null)
            {
                float breakDistance = Mathf.Max(catchDistance, detectionDistance) * Mathf.Max(1f, targetBreakDistanceMultiplier);
                float distanceToLockedTarget = Vector3.Distance(transform.position, lockedTransform.position);
                bool keepLockedTarget = Time.time < _targetLockUntilTime || distanceToLockedTarget <= breakDistance;
                if (keepLockedTarget)
                {
                    player = lockedTransform;
                    _activeTargetClientId = _lockedTargetClientId;
                    return;
                }
            }

            if (Time.time < _retargetCooldownUntilTime
                && _activeTargetClientId != NetworkNpcAuthorityBridge.NoTargetClientId
                && TryGetConnectedPlayerTransform(_activeTargetClientId, out Transform cooldownTransform)
                && cooldownTransform != null)
            {
                player = cooldownTransform;
                return;
            }

            Transform bestTransform = null;
            ulong bestClientId = NetworkNpcAuthorityBridge.NoTargetClientId;
            float bestDistanceSqr = float.MaxValue;

            foreach (KeyValuePair<ulong, NetworkClient> entry in networkManager.ConnectedClients)
            {
                ulong candidateClientId = entry.Key;
                if (!TryGetConnectedPlayerTransform(candidateClientId, out Transform candidateTransform) || candidateTransform == null)
                {
                    continue;
                }

                float distanceSqr = (candidateTransform.position - transform.position).sqrMagnitude;
                bool isTie = Mathf.Abs(distanceSqr - bestDistanceSqr) <= 0.0001f;
                if (distanceSqr > bestDistanceSqr && !isTie)
                {
                    continue;
                }

                if (isTie && bestClientId != NetworkNpcAuthorityBridge.NoTargetClientId && candidateClientId > bestClientId)
                {
                    continue;
                }

                bestDistanceSqr = distanceSqr;
                bestTransform = candidateTransform;
                bestClientId = candidateClientId;
            }

            player = bestTransform;
            _activeTargetClientId = bestTransform != null ? bestClientId : NetworkNpcAuthorityBridge.NoTargetClientId;
            if (_activeTargetClientId != _lockedTargetClientId)
            {
                _lockedTargetClientId = _activeTargetClientId;
                _targetLockUntilTime = Time.time + Mathf.Max(0f, targetLockDurationSeconds);
                _retargetCooldownUntilTime = Time.time + Mathf.Max(0f, retargetCooldownSeconds);
            }
        }

        private void TryPickRoamTarget()
        {
            if (!EnsureAgentOnNavMesh())
            {
                _hasRoamTarget = false;
                return;
            }

            int areaMask = _agent != null ? _agent.areaMask : NavMesh.AllAreas;
            float sampleDistance = Mathf.Clamp(roamRadius * 0.2f, MinRoamSampleDistance, MaxRoamSampleDistance);

            if (!TryGetAgentNavPosition(areaMask, out Vector3 agentNavPosition))
            {
                _hasRoamTarget = false;
                return;
            }

            Vector3 selected = agentNavPosition;
            bool found = false;
            NavMeshPath candidatePath = new NavMeshPath();

            for (int i = 0; i < RoamTargetAttempts; i++)
            {
                Vector2 randomOffset = Random.insideUnitCircle * roamRadius;
                Vector3 candidate = _spawnPosition + new Vector3(randomOffset.x, 0f, randomOffset.y);
                if (NavMesh.SamplePosition(candidate, out NavMeshHit navHit, sampleDistance, areaMask))
                {
                    float distanceFromCurrent = Vector3.Distance(agentNavPosition, navHit.position);
                    if (distanceFromCurrent >= roamMinTargetDistance)
                    {
                        bool hasPath = NavMesh.CalculatePath(agentNavPosition, navHit.position, areaMask, candidatePath);
                        if (hasPath && candidatePath.status == NavMeshPathStatus.PathComplete)
                        {
                            selected = navHit.position;
                            found = true;
                            break;
                        }

                        Log($"Rejected roam candidate due to invalid path. Position: {navHit.position}, status: {candidatePath.status}");
                    }
                }
            }

            if (!found)
            {
                if (NavMesh.SamplePosition(agentNavPosition, out NavMeshHit nearbyHit, sampleDistance, areaMask))
                {
                    bool hasPath = NavMesh.CalculatePath(agentNavPosition, nearbyHit.position, areaMask, candidatePath);
                    if (hasPath && candidatePath.status == NavMeshPathStatus.PathComplete)
                    {
                        selected = nearbyHit.position;
                        found = true;
                    }
                    else
                    {
                        Log($"Rejected nearby fallback roam candidate due to invalid path. Position: {nearbyHit.position}, status: {candidatePath.status}");
                    }
                }
            }

            if (!found)
            {
                if (NavMesh.SamplePosition(_spawnPosition, out NavMeshHit spawnHit, roamRadius, areaMask))
                {
                    bool hasPath = NavMesh.CalculatePath(agentNavPosition, spawnHit.position, areaMask, candidatePath);
                    if (hasPath && candidatePath.status == NavMeshPathStatus.PathComplete)
                    {
                        selected = spawnHit.position;
                        found = true;
                    }
                    else
                    {
                        Log($"Rejected spawn fallback roam candidate due to invalid path. Position: {spawnHit.position}, status: {candidatePath.status}");
                    }
                }
            }

            _roamTarget = selected;
            _hasRoamTarget = found;
            _isRoamPaused = false;
            _idleTimer = 0f;

            if (_hasRoamTarget)
            {
                SetAgentDestination(_roamTarget, "Roaming");
            }
        }

        private void ChangeState(NPCState nextState)
        {
            if (_state == nextState)
            {
                return;
            }

            NPCState previous = _state;
            _state = nextState;

            if (previous == NPCState.Chasing || _state == NPCState.Chasing)
            {
                string ownerKey = ResolveCurrentTargetOwnerKey();
                NpcCatchStateRuntime ownerState = GetOrCreateOwnerCatchState(ownerKey, _activeTargetClientId);
                if (ownerState != null)
                {
                    ownerState.isChaseActive = _state == NPCState.Chasing;
                }
            }

            if (_state == NPCState.Idle)
            {
                StopAgent();
            }

            if (_state != NPCState.Chasing)
            {
                _losePlayerTimer = 0f;
            }

            Log($"State changed: {previous} -> {_state}");
        }

        private bool EnsureAgentOnNavMesh()
        {
            if (_agent == null)
            {
                return false;
            }

            if (!_agent.isOnNavMesh)
            {
                if (!_loggedAgentOffNavMesh)
                {
                    Log("NavMeshAgent is not on NavMesh.");
                    _loggedAgentOffNavMesh = true;
                }

                return false;
            }

            _loggedAgentOffNavMesh = false;
            return true;
        }

        private void SetAgentDestination(Vector3 destination, string reason)
        {
            if (!EnsureAgentOnNavMesh())
            {
                return;
            }

            _agent.stoppingDistance = Mathf.Max(0f, stoppingDistance);
            _agent.isStopped = false;
            bool destinationSet = _agent.SetDestination(destination);

            if (destinationSet)
            {
                if (!_hasLastLoggedDestination || Vector3.Distance(_lastLoggedDestination, destination) > 0.15f)
                {
                    Log($"{reason} destination set: {destination}");
                    _lastLoggedDestination = destination;
                    _hasLastLoggedDestination = true;
                }
            }
            else
            {
                Log($"{reason} destination failed: {destination}");
            }
        }

        private bool HasReachedDestination()
        {
            if (!EnsureAgentOnNavMesh())
            {
                return true;
            }

            if (_agent.pathPending)
            {
                return false;
            }

            if (_agent.pathStatus == NavMeshPathStatus.PathInvalid || _agent.pathStatus == NavMeshPathStatus.PathPartial)
            {
                return false;
            }

            bool reached = _agent.remainingDistance <= stoppingDistance;
            if (reached)
            {
                _agent.isStopped = true;
            }

            return reached;
        }

        private void StopAgent()
        {
            if (_agent == null)
            {
                return;
            }

            _agent.isStopped = true;
            if (_agent.isOnNavMesh)
            {
                _agent.ResetPath();
            }
        }

        private void TryAutoAssignReferences()
        {
            if (player == null)
            {
                if (PlayerContextLocator.TryGetLocalPlayerTransform(out Transform playerTransform)
                    && playerTransform != null)
                {
                    player = playerTransform;
                }
                else if (PlayerContextLocator.IsCompatibilityFallbackAllowed()
                         && PlayerContextLocator.TryGetAuthoritativePlayerTransform(out playerTransform)
                         && playerTransform != null)
                {
                    player = playerTransform;
                }
                else if (!_hasLoggedMissingPlayerReference)
                {
                    _hasLoggedMissingPlayerReference = true;
                    Debug.LogWarning(
                        "[NPCController] Player reference is missing and auto-assign by PlayerController failed. " +
                        "Assign Player transform in inspector for reliable detection/chase.",
                        this);
                }
            }

            if (head == null)
            {
                head = transform;
                if (!_hasLoggedMissingHeadReference)
                {
                    _hasLoggedMissingHeadReference = true;
                    Debug.LogWarning(
                        "[NPCController] Head reference is missing. Falling back to NPC root transform for vision origin. " +
                        "Assign Head transform in inspector for reliable vision checks.",
                        this);
                }
            }
        }

        private void Log(string message)
        {
            if (!enableDebugLogs)
            {
                return;
            }

            Debug.Log($"[NPCController] {message}");
        }

        private bool ShouldRefreshRoamDestination()
        {
            if (_agent == null)
            {
                return false;
            }

            if (_agent.pathPending)
            {
                return false;
            }

            if (!_agent.hasPath)
            {
                return true;
            }

            if (_agent.pathStatus != NavMeshPathStatus.PathComplete)
            {
                return true;
            }

            return Vector3.Distance(_agent.destination, _roamTarget) > DestinationRefreshThreshold;
        }

        private bool TryGetAgentNavPosition(int areaMask, out Vector3 navPosition)
        {
            navPosition = transform.position;

            if (_agent == null || !_agent.isOnNavMesh)
            {
                return false;
            }

            if (NavMesh.SamplePosition(_agent.nextPosition, out NavMeshHit agentHit, AgentNavPositionSampleRadius, areaMask))
            {
                navPosition = agentHit.position;
                return true;
            }

            if (NavMesh.SamplePosition(transform.position, out NavMeshHit transformHit, AgentNavPositionSampleRadius, areaMask))
            {
                navPosition = transformHit.position;
                return true;
            }

            return false;
        }

        private void OnDrawGizmos()
        {
            if (!drawDetectionGizmos)
            {
                return;
            }

            Vector3 center = Application.isPlaying ? _spawnPosition : transform.position;

            Color roamColor = _state == NPCState.Roaming ? Color.green : new Color(0f, 1f, 0f, 0.25f);
            Gizmos.color = roamColor;
            Gizmos.DrawWireSphere(center, roamRadius);

            Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, catchDistance);

            if (closeRangeBypassDistance > 0f)
            {
                Gizmos.color = new Color(1f, 0.55f, 0f, 0.45f);
                Gizmos.DrawWireSphere(transform.position, closeRangeBypassDistance);
            }

            Vector3 origin;
            Vector3 forward;
            if (Application.isPlaying)
            {
                origin = _lastDetectionRayOrigin == Vector3.zero ? GetDetectionOrigin() : _lastDetectionRayOrigin;
                forward = _lastDetectionForward.sqrMagnitude > 0.001f ? _lastDetectionForward.normalized : GetDetectionForward();
            }
            else
            {
                origin = head != null && head != transform
                    ? head.position
                    : transform.position + Vector3.up * Mathf.Max(0f, fallbackEyeHeight);
                forward = usePlanarFovChecks ? FlattenY(transform.forward).normalized : transform.forward.normalized;
            }

            float halfAngle = viewAngle * 0.5f;
            Vector3 leftBoundary = Quaternion.AngleAxis(-halfAngle, Vector3.up) * forward;
            Vector3 rightBoundary = Quaternion.AngleAxis(halfAngle, Vector3.up) * forward;

            Color coneColor = _state == NPCState.Chasing ? Color.red : (_isDetectingThisFrame ? Color.yellow : Color.green);
            Gizmos.color = coneColor;
            Gizmos.DrawWireSphere(origin, detectionDistance);
            Gizmos.DrawLine(origin, origin + forward * Mathf.Min(2f, detectionDistance));
            Gizmos.DrawLine(origin, origin + leftBoundary * detectionDistance);
            Gizmos.DrawLine(origin, origin + rightBoundary * detectionDistance);

            if (_lastDetectionRayDirection.sqrMagnitude > 0.001f)
            {
                Color losColor = _lastDetectionHadLineOfSight ? Color.green : (_lastDetectionWasInRange && _lastDetectionWasInVisionCone ? Color.red : Color.gray);
                Gizmos.color = losColor;
                Vector3 losOrigin = _lastDetectionRayOrigin == Vector3.zero ? origin : _lastDetectionRayOrigin;
                Gizmos.DrawLine(losOrigin, losOrigin + _lastDetectionRayDirection.normalized * detectionDistance);
            }

            if (_lastDetectionTarget != Vector3.zero)
            {
                Gizmos.color = _lastDetectionUsedCloseRangeBypass ? new Color(1f, 0.55f, 0f, 0.9f) : new Color(0f, 0.8f, 1f, 0.9f);
                Gizmos.DrawWireSphere(_lastDetectionTarget, 0.12f);
                Gizmos.DrawLine(origin, _lastDetectionTarget);
            }

            if (_isSearchingLastKnownPosition && _hasLastKnownPlayerPosition)
            {
                Gizmos.color = new Color(1f, 0.5f, 0f, 0.8f);
                Gizmos.DrawWireSphere(_lastKnownPlayerPosition, 0.2f);
                Gizmos.DrawLine(transform.position, _lastKnownPlayerPosition);
            }
        }
    }
}
