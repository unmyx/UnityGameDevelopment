using UnityEngine;

namespace Game.Interaction
{
    public readonly struct InteractionTarget
    {
        public InteractionTarget(
            BaseInteractable interactable,
            Collider hitCollider,
            Vector3 hitPoint,
            float distance)
        {
            Interactable = interactable;
            HitCollider = hitCollider;
            HitPoint = hitPoint;
            Distance = distance;
            TargetTransform = interactable != null ? interactable.transform : null;
        }

        public BaseInteractable Interactable { get; }
        public Collider HitCollider { get; }
        public Vector3 HitPoint { get; }
        public float Distance { get; }
        public Transform TargetTransform { get; }
    }

    /// <summary>
    /// Owns the raycast, hierarchy resolution and validation rules shared by prompt and execution.
    /// </summary>
    public static class InteractionTargetResolver
    {
        public const int FallbackLayerMask = Physics.AllLayers;

        private const float TriggerRayAdvance = 0.001f;
        private const int MaxSkippedTriggerHits = 16;

        public static bool TryFindInteractable(
            Ray ray,
            float maxDistance,
            LayerMask interactionLayerMask,
            out InteractionTarget target)
        {
            target = default;
            if (!IsFinitePositive(maxDistance) || ray.direction.sqrMagnitude <= Mathf.Epsilon)
            {
                return false;
            }

            ray.direction = ray.direction.normalized;
            int effectiveLayerMask = GetEffectiveLayerMask(interactionLayerMask);

            bool hasPhysicalHit = Physics.Raycast(
                ray,
                out RaycastHit physicalHit,
                maxDistance,
                effectiveLayerMask,
                QueryTriggerInteraction.Ignore);

            float physicalBlockerDistance = hasPhysicalHit ? physicalHit.distance : maxDistance;
            InteractionTarget physicalTarget = default;
            bool hasPhysicalTarget = hasPhysicalHit
                                     && TryCreateTarget(physicalHit, out physicalTarget);

            bool hasTriggerOnlyTarget = TryFindTriggerOnlyInteractable(
                ray,
                physicalBlockerDistance,
                effectiveLayerMask,
                out InteractionTarget triggerOnlyTarget);

            if (hasTriggerOnlyTarget
                && (!hasPhysicalTarget || triggerOnlyTarget.Distance < physicalTarget.Distance))
            {
                target = triggerOnlyTarget;
                return true;
            }

            if (!hasPhysicalTarget)
            {
                return false;
            }

            target = physicalTarget;
            return true;
        }

        public static bool TryResolveInteractable(
            Collider hitCollider,
            out BaseInteractable interactable)
        {
            interactable = null;
            if (hitCollider == null || !hitCollider.enabled || !hitCollider.gameObject.activeInHierarchy)
            {
                return false;
            }

            Transform candidateTransform = hitCollider.transform;
            while (candidateTransform != null)
            {
                if (candidateTransform.TryGetComponent(out BaseInteractable candidate)
                    && IsValidInteractable(candidate))
                {
                    interactable = candidate;
                    return true;
                }

                candidateTransform = candidateTransform.parent;
            }

            return false;
        }

        public static bool IsValidTarget(InteractionTarget target)
        {
            return IsValidInteractable(target.Interactable)
                   && target.HitCollider != null
                   && target.HitCollider.enabled
                   && target.HitCollider.gameObject.activeInHierarchy
                   && IsFiniteNonNegative(target.Distance);
        }

        public static int GetEffectiveLayerMask(LayerMask interactionLayerMask)
        {
            return interactionLayerMask.value == 0
                ? FallbackLayerMask
                : interactionLayerMask.value;
        }

        private static bool TryCreateTarget(RaycastHit hit, out InteractionTarget target)
        {
            target = default;
            if (!TryResolveInteractable(hit.collider, out BaseInteractable interactable))
            {
                return false;
            }

            target = new InteractionTarget(interactable, hit.collider, hit.point, hit.distance);
            return IsValidTarget(target);
        }

        private static bool TryFindTriggerOnlyInteractable(
            Ray ray,
            float maximumDistanceBeforePhysicalBlocker,
            int layerMask,
            out InteractionTarget target)
        {
            target = default;
            if (maximumDistanceBeforePhysicalBlocker <= 0f)
            {
                return false;
            }

            float traveledDistance = 0f;
            for (int i = 0; i < MaxSkippedTriggerHits; i++)
            {
                float remainingDistance = maximumDistanceBeforePhysicalBlocker - traveledDistance;
                if (remainingDistance <= 0f)
                {
                    return false;
                }

                Ray segmentRay = new Ray(
                    ray.origin + (ray.direction * traveledDistance),
                    ray.direction);

                if (!Physics.Raycast(
                        segmentRay,
                        out RaycastHit hit,
                        remainingDistance,
                        layerMask,
                        QueryTriggerInteraction.Collide))
                {
                    return false;
                }

                float hitDistanceFromOrigin = traveledDistance + hit.distance;
                if (!hit.collider.isTrigger)
                {
                    return false;
                }

                if (TryResolveInteractable(hit.collider, out BaseInteractable interactable)
                    && IsSupportedTriggerOnlyInteractable(interactable))
                {
                    target = new InteractionTarget(
                        interactable,
                        hit.collider,
                        hit.point,
                        hitDistanceFromOrigin);
                    return IsValidTarget(target);
                }

                traveledDistance = hitDistanceFromOrigin + TriggerRayAdvance;
            }

            return false;
        }

        private static bool IsSupportedTriggerOnlyInteractable(BaseInteractable interactable)
        {
            // Existing ring pickup prefabs use InteractableItem on their only trigger collider.
            // Other triggers remain transparent to interaction and cannot take over the ray.
            return interactable is InteractableItem;
        }

        private static bool IsValidInteractable(BaseInteractable interactable)
        {
            return interactable != null
                   && interactable.isActiveAndEnabled
                   && interactable.gameObject.activeInHierarchy
                   && interactable.CanInteract;
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFiniteNonNegative(float value)
        {
            return value >= 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
