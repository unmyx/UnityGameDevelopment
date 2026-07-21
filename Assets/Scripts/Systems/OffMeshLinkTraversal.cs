using UnityEngine;

namespace Game.Systems
{
    public enum OffMeshLinkTraversalPhase
    {
        Idle,
        Preparing,
        Traversing,
        Completing,
        Cancelled
    }

    public enum OffMeshLinkTraversalFailure
    {
        None,
        ComponentInactive,
        AgentMissing,
        AgentDisabled,
        AgentOffNavMesh,
        AgentNotOnLink,
        InvalidLinkData,
        InvalidEndpoint,
        DegenerateLink,
        MovementBlocked,
        NotAuthoritative,
        TraversalAlreadyActive
    }

    public readonly struct OffMeshLinkTraversalSnapshot
    {
        public OffMeshLinkTraversalSnapshot(
            bool componentActive,
            bool agentExists,
            bool agentEnabled,
            bool agentOnNavMesh,
            bool agentOnOffMeshLink,
            bool linkDataValid,
            bool movementAllowed,
            bool authoritative,
            Vector3 currentPosition,
            Vector3 startPosition,
            Vector3 endPosition)
        {
            ComponentActive = componentActive;
            AgentExists = agentExists;
            AgentEnabled = agentEnabled;
            AgentOnNavMesh = agentOnNavMesh;
            AgentOnOffMeshLink = agentOnOffMeshLink;
            LinkDataValid = linkDataValid;
            MovementAllowed = movementAllowed;
            Authoritative = authoritative;
            CurrentPosition = currentPosition;
            StartPosition = startPosition;
            EndPosition = endPosition;
        }

        public bool ComponentActive { get; }
        public bool AgentExists { get; }
        public bool AgentEnabled { get; }
        public bool AgentOnNavMesh { get; }
        public bool AgentOnOffMeshLink { get; }
        public bool LinkDataValid { get; }
        public bool MovementAllowed { get; }
        public bool Authoritative { get; }
        public Vector3 CurrentPosition { get; }
        public Vector3 StartPosition { get; }
        public Vector3 EndPosition { get; }
    }

    public readonly struct OffMeshLinkCompletionSnapshot
    {
        public OffMeshLinkCompletionSnapshot(
            bool componentActive,
            bool agentExists,
            bool agentEnabled,
            bool agentOnNavMesh,
            bool agentOnOffMeshLink,
            bool authoritative)
        {
            ComponentActive = componentActive;
            AgentExists = agentExists;
            AgentEnabled = agentEnabled;
            AgentOnNavMesh = agentOnNavMesh;
            AgentOnOffMeshLink = agentOnOffMeshLink;
            Authoritative = authoritative;
        }

        public bool ComponentActive { get; }
        public bool AgentExists { get; }
        public bool AgentEnabled { get; }
        public bool AgentOnNavMesh { get; }
        public bool AgentOnOffMeshLink { get; }
        public bool Authoritative { get; }
    }

    public static class OffMeshLinkTraversalRules
    {
        public const float MinimumEffectiveSpeed = 0.1f;
        public const float MinimumDurationSeconds = 0.1f;
        public const float MaximumDurationSeconds = 5f;
        public const float MinimumLinkDistance = 0.01f;

        public static bool TryValidateStart(
            OffMeshLinkTraversalSnapshot snapshot,
            bool traversalAlreadyActive,
            out Vector3 destination,
            out OffMeshLinkTraversalFailure failure)
        {
            destination = default;
            failure = GetStartFailure(snapshot, traversalAlreadyActive);
            if (failure != OffMeshLinkTraversalFailure.None)
            {
                return false;
            }

            return TryResolveDestination(
                snapshot.CurrentPosition,
                snapshot.StartPosition,
                snapshot.EndPosition,
                out destination);
        }

        public static OffMeshLinkTraversalFailure GetStartFailure(
            OffMeshLinkTraversalSnapshot snapshot,
            bool traversalAlreadyActive)
        {
            if (traversalAlreadyActive)
            {
                return OffMeshLinkTraversalFailure.TraversalAlreadyActive;
            }

            if (!snapshot.ComponentActive)
            {
                return OffMeshLinkTraversalFailure.ComponentInactive;
            }

            if (!snapshot.AgentExists)
            {
                return OffMeshLinkTraversalFailure.AgentMissing;
            }

            if (!snapshot.AgentEnabled)
            {
                return OffMeshLinkTraversalFailure.AgentDisabled;
            }

            if (!snapshot.AgentOnNavMesh)
            {
                return OffMeshLinkTraversalFailure.AgentOffNavMesh;
            }

            if (!snapshot.AgentOnOffMeshLink)
            {
                return OffMeshLinkTraversalFailure.AgentNotOnLink;
            }

            if (!snapshot.LinkDataValid)
            {
                return OffMeshLinkTraversalFailure.InvalidLinkData;
            }

            if (!snapshot.Authoritative)
            {
                return OffMeshLinkTraversalFailure.NotAuthoritative;
            }

            if (!snapshot.MovementAllowed)
            {
                return OffMeshLinkTraversalFailure.MovementBlocked;
            }

            if (!IsFinite(snapshot.CurrentPosition)
                || !IsFinite(snapshot.StartPosition)
                || !IsFinite(snapshot.EndPosition))
            {
                return OffMeshLinkTraversalFailure.InvalidEndpoint;
            }

            float linkDistance = Vector3.Distance(snapshot.StartPosition, snapshot.EndPosition);
            if (!IsFinite(linkDistance))
            {
                return OffMeshLinkTraversalFailure.InvalidEndpoint;
            }

            if (linkDistance < MinimumLinkDistance)
            {
                return OffMeshLinkTraversalFailure.DegenerateLink;
            }

            return OffMeshLinkTraversalFailure.None;
        }

        public static bool TryResolveDestination(
            Vector3 currentPosition,
            Vector3 startPosition,
            Vector3 endPosition,
            out Vector3 destination)
        {
            destination = default;
            if (!IsFinite(currentPosition) || !IsFinite(startPosition) || !IsFinite(endPosition))
            {
                return false;
            }

            float linkDistance = Vector3.Distance(startPosition, endPosition);
            if (!IsFinite(linkDistance) || linkDistance < MinimumLinkDistance)
            {
                return false;
            }

            float distanceToStart = Vector3.SqrMagnitude(currentPosition - startPosition);
            float distanceToEnd = Vector3.SqrMagnitude(currentPosition - endPosition);
            if (!IsFinite(distanceToStart) || !IsFinite(distanceToEnd))
            {
                return false;
            }

            destination = distanceToStart <= distanceToEnd ? endPosition : startPosition;
            return true;
        }

        public static bool TryCalculateDuration(float distance, float speed, out float duration)
        {
            duration = 0f;
            if (!IsFinite(distance) || distance < MinimumLinkDistance || float.IsInfinity(speed))
            {
                return false;
            }

            float effectiveSpeed = IsFinite(speed) && speed > MinimumEffectiveSpeed
                ? speed
                : MinimumEffectiveSpeed;
            float rawDuration = distance / effectiveSpeed;
            if (!IsFinite(rawDuration) || rawDuration <= 0f)
            {
                return false;
            }

            duration = Mathf.Clamp(rawDuration, MinimumDurationSeconds, MaximumDurationSeconds);
            return true;
        }

        public static bool CanComplete(OffMeshLinkCompletionSnapshot snapshot)
        {
            return snapshot.ComponentActive
                   && snapshot.AgentExists
                   && snapshot.AgentEnabled
                   && snapshot.AgentOnNavMesh
                   && snapshot.AgentOnOffMeshLink
                   && snapshot.Authoritative;
        }

        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    public sealed class OffMeshLinkTraversalSession
    {
        private uint _generation;
        private uint _activeToken;

        public OffMeshLinkTraversalPhase Phase { get; private set; } = OffMeshLinkTraversalPhase.Idle;
        public bool IsActive => Phase == OffMeshLinkTraversalPhase.Preparing
                                || Phase == OffMeshLinkTraversalPhase.Traversing
                                || Phase == OffMeshLinkTraversalPhase.Completing;
        public uint ActiveToken => IsActive ? _activeToken : 0u;

        public bool TryBegin(out uint token)
        {
            token = 0u;
            if (IsActive)
            {
                return false;
            }

            _generation++;
            if (_generation == 0u)
            {
                _generation++;
            }

            _activeToken = _generation;
            Phase = OffMeshLinkTraversalPhase.Preparing;
            token = _activeToken;
            return true;
        }

        public bool TryMarkTraversing(uint token)
        {
            if (!IsCurrent(token) || Phase != OffMeshLinkTraversalPhase.Preparing)
            {
                return false;
            }

            Phase = OffMeshLinkTraversalPhase.Traversing;
            return true;
        }

        public bool TryBeginCompletion(uint token)
        {
            if (!IsCurrent(token) || Phase != OffMeshLinkTraversalPhase.Traversing)
            {
                return false;
            }

            Phase = OffMeshLinkTraversalPhase.Completing;
            return true;
        }

        public bool TryComplete(uint token)
        {
            if (!IsCurrent(token) || Phase != OffMeshLinkTraversalPhase.Completing)
            {
                return false;
            }

            _activeToken = 0u;
            Phase = OffMeshLinkTraversalPhase.Idle;
            return true;
        }

        public bool Cancel()
        {
            if (!IsActive)
            {
                return false;
            }

            _generation++;
            if (_generation == 0u)
            {
                _generation++;
            }

            _activeToken = 0u;
            Phase = OffMeshLinkTraversalPhase.Cancelled;
            return true;
        }

        public bool IsCurrent(uint token)
        {
            return token != 0u && IsActive && token == _activeToken;
        }
    }
}
