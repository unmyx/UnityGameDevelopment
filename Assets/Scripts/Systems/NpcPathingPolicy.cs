using UnityEngine;

namespace Game.Systems
{
    public enum NpcPathState
    {
        None,
        Pending,
        Complete,
        Partial,
        Invalid
    }

    public readonly struct NpcRepathRequest
    {
        public NpcRepathRequest(
            bool agentExists,
            bool agentEnabled,
            bool agentOnNavMesh,
            bool authoritative,
            bool movementAllowed,
            bool traversalActive,
            bool pathNeedsRepair,
            Vector3 destination,
            float currentTime,
            int frame)
        {
            AgentExists = agentExists;
            AgentEnabled = agentEnabled;
            AgentOnNavMesh = agentOnNavMesh;
            Authoritative = authoritative;
            MovementAllowed = movementAllowed;
            TraversalActive = traversalActive;
            PathNeedsRepair = pathNeedsRepair;
            Destination = destination;
            CurrentTime = currentTime;
            Frame = frame;
        }

        public bool AgentExists { get; }
        public bool AgentEnabled { get; }
        public bool AgentOnNavMesh { get; }
        public bool Authoritative { get; }
        public bool MovementAllowed { get; }
        public bool TraversalActive { get; }
        public bool PathNeedsRepair { get; }
        public Vector3 Destination { get; }
        public float CurrentTime { get; }
        public int Frame { get; }
    }

    public readonly struct NpcArrivalSnapshot
    {
        public NpcArrivalSnapshot(
            bool agentExists,
            bool agentEnabled,
            bool agentOnNavMesh,
            bool pathPending,
            bool hasPath,
            NpcPathState pathState,
            float remainingDistance,
            float stoppingDistance,
            float velocityMagnitude)
        {
            AgentExists = agentExists;
            AgentEnabled = agentEnabled;
            AgentOnNavMesh = agentOnNavMesh;
            PathPending = pathPending;
            HasPath = hasPath;
            PathState = pathState;
            RemainingDistance = remainingDistance;
            StoppingDistance = stoppingDistance;
            VelocityMagnitude = velocityMagnitude;
        }

        public bool AgentExists { get; }
        public bool AgentEnabled { get; }
        public bool AgentOnNavMesh { get; }
        public bool PathPending { get; }
        public bool HasPath { get; }
        public NpcPathState PathState { get; }
        public float RemainingDistance { get; }
        public float StoppingDistance { get; }
        public float VelocityMagnitude { get; }
    }

    public static class NpcPathingPolicy
    {
        public const float RepathIntervalSeconds = 0.25f;
        public const float TargetMovementThreshold = 0.35f;
        public const float FailedRequestRetrySeconds = 0.35f;
        public const float PathIssueTimeoutSeconds = 2f;
        public const float UnavailableTargetTimeoutSeconds = 4f;
        public const int MaximumConsecutiveFailures = 3;
        public const float RoamingRetrySeconds = 0.5f;
        public const int MaximumRoamTargetAttempts = 12;
        public const float ArrivalTolerance = 0.1f;
        public const float ArrivalVelocityThreshold = 0.1f;

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        public static bool IsValidDestination(Vector3 destination)
        {
            return IsFinite(destination);
        }

        public static bool HasTargetMovedMeaningfully(Vector3 previous, Vector3 current)
        {
            if (!IsFinite(previous) || !IsFinite(current))
            {
                return false;
            }

            float distance = Vector3.Distance(previous, current);
            return IsFinite(distance) && distance >= TargetMovementThreshold;
        }

        public static bool IsArrival(NpcArrivalSnapshot snapshot)
        {
            if (!snapshot.AgentExists
                || !snapshot.AgentEnabled
                || !snapshot.AgentOnNavMesh
                || snapshot.PathPending
                || snapshot.PathState != NpcPathState.Complete
                || !IsFinite(snapshot.RemainingDistance)
                || !IsFinite(snapshot.StoppingDistance)
                || !IsFinite(snapshot.VelocityMagnitude))
            {
                return false;
            }

            float threshold = Mathf.Max(0f, snapshot.StoppingDistance) + ArrivalTolerance;
            if (snapshot.RemainingDistance > threshold)
            {
                return false;
            }

            return !snapshot.HasPath || snapshot.VelocityMagnitude <= ArrivalVelocityThreshold;
        }
    }

    public sealed class NpcRepathScheduler
    {
        private bool _hasAttempt;
        private bool _lastAttemptFailed;
        private Vector3 _lastDestination;
        private float _lastAttemptTime;
        private int _lastAttemptFrame = -1;

        public bool HasAttempt => _hasAttempt;
        public Vector3 LastDestination => _lastDestination;
        public int ConsecutiveFailures { get; private set; }

        public bool CanRequest(NpcRepathRequest request)
        {
            if (!request.AgentExists
                || !request.AgentEnabled
                || !request.AgentOnNavMesh
                || !request.Authoritative
                || !request.MovementAllowed
                || request.TraversalActive
                || !NpcPathingPolicy.IsValidDestination(request.Destination)
                || !NpcPathingPolicy.IsFinite(request.CurrentTime)
                || request.Frame == _lastAttemptFrame)
            {
                return false;
            }

            if (!_hasAttempt)
            {
                return true;
            }

            float elapsed = request.CurrentTime - _lastAttemptTime;
            if (!NpcPathingPolicy.IsFinite(elapsed) || elapsed < 0f)
            {
                return false;
            }

            bool needsRepair = request.PathNeedsRepair || _lastAttemptFailed;
            float requiredInterval = needsRepair
                ? NpcPathingPolicy.FailedRequestRetrySeconds
                : NpcPathingPolicy.RepathIntervalSeconds;
            if (elapsed < requiredInterval)
            {
                return false;
            }

            return needsRepair
                   || NpcPathingPolicy.HasTargetMovedMeaningfully(_lastDestination, request.Destination);
        }

        public bool RecordAttempt(NpcRepathRequest request, bool accepted)
        {
            if (request.Frame == _lastAttemptFrame)
            {
                return false;
            }

            _hasAttempt = true;
            _lastDestination = request.Destination;
            _lastAttemptTime = request.CurrentTime;
            _lastAttemptFrame = request.Frame;
            _lastAttemptFailed = !accepted;
            ConsecutiveFailures = accepted ? 0 : ConsecutiveFailures + 1;
            return true;
        }

        public void InvalidateDestination()
        {
            _hasAttempt = false;
            _lastAttemptFailed = false;
            _lastAttemptFrame = -1;
        }

        public void Reset()
        {
            _hasAttempt = false;
            _lastAttemptFailed = false;
            _lastDestination = default;
            _lastAttemptTime = 0f;
            _lastAttemptFrame = -1;
            ConsecutiveFailures = 0;
        }
    }

    public sealed class NpcPathRecoveryTracker
    {
        private bool _recoveryIssued;

        public float PathIssueElapsed { get; private set; }
        public float TargetUnavailableElapsed { get; private set; }
        public int ConsecutiveFailures { get; private set; }

        public void Tick(float deltaTime, bool targetAvailable, NpcPathState pathState, bool traversalActive)
        {
            if (traversalActive || !NpcPathingPolicy.IsFinite(deltaTime) || deltaTime <= 0f)
            {
                return;
            }

            if (targetAvailable)
            {
                TargetUnavailableElapsed = 0f;
            }
            else
            {
                TargetUnavailableElapsed += deltaTime;
            }

            if (pathState == NpcPathState.Partial || pathState == NpcPathState.Invalid)
            {
                PathIssueElapsed += deltaTime;
            }
            else if (pathState == NpcPathState.Complete)
            {
                PathIssueElapsed = 0f;
            }
        }

        public void RecordDestinationFailure()
        {
            ConsecutiveFailures++;
        }

        public void RecordValidPath()
        {
            ConsecutiveFailures = 0;
            PathIssueElapsed = 0f;
        }

        public void ResetForTargetAcquired()
        {
            ConsecutiveFailures = 0;
            PathIssueElapsed = 0f;
            TargetUnavailableElapsed = 0f;
            _recoveryIssued = false;
        }

        public bool ShouldRecoverToRoaming()
        {
            return ConsecutiveFailures >= NpcPathingPolicy.MaximumConsecutiveFailures
                   || PathIssueElapsed >= NpcPathingPolicy.PathIssueTimeoutSeconds
                   || TargetUnavailableElapsed >= NpcPathingPolicy.UnavailableTargetTimeoutSeconds;
        }

        public bool TryIssueRecovery()
        {
            if (_recoveryIssued || !ShouldRecoverToRoaming())
            {
                return false;
            }

            _recoveryIssued = true;
            return true;
        }

        public void Reset()
        {
            ConsecutiveFailures = 0;
            PathIssueElapsed = 0f;
            TargetUnavailableElapsed = 0f;
            _recoveryIssued = false;
        }
    }

    public sealed class NpcRoamingRetryState
    {
        private float _nextAllowedTime;

        public bool CanSelect(float currentTime, bool traversalActive, bool hasTarget)
        {
            return !traversalActive
                   && !hasTarget
                   && NpcPathingPolicy.IsFinite(currentTime)
                   && currentTime >= _nextAllowedTime;
        }

        public void RecordFailure(float currentTime)
        {
            if (NpcPathingPolicy.IsFinite(currentTime))
            {
                _nextAllowedTime = currentTime + NpcPathingPolicy.RoamingRetrySeconds;
            }
        }

        public void RecordSuccess()
        {
            _nextAllowedTime = 0f;
        }

        public void Reset()
        {
            _nextAllowedTime = 0f;
        }
    }
}
