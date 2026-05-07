using Game.Interaction;
using Game.Player;
using Game.Systems;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Networking
{
    /// <summary>
    /// Enables local-driving components only for the owning client.
    /// Keeps remote player replicas visual-only for MP-7 sandbox validation.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkPlayerOwnershipGate : NetworkBehaviour
    {
        private const float SpawnOffsetSpacing = 2.5f;
        private const string SpawnMarkerPrefix = "PlayerSpawn";
        private const float FallbackSpawnProbeHeight = 64f;
        private const float FallbackSpawnProbeDistance = 256f;
        private const float FallbackSpawnLift = 1.25f;

        private static string _cachedSpawnMarkerSceneName = string.Empty;
        private static readonly List<Transform> CachedSpawnMarkers = new List<Transform>();
        private static readonly HashSet<string> LoggedFallbackProbeWarnings = new HashSet<string>();

        private PlayerController _playerController;
        private PlayerInputHandler _inputHandler;
        private CharacterController _characterController;
        private FirstPersonCamera _firstPersonCamera;
        private InteractionSystem[] _interactionSystems;
        private Camera[] _cameras;
        private AudioListener[] _audioListeners;

        private bool _cached;
        private bool _hasAppliedServerSpawnPlacement;

        private void Awake()
        {
            CacheComponents();
        }

        public override void OnNetworkSpawn()
        {
            CacheComponents();
            if (IsServer)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
            }

            ApplyServerAuthoritativeSpawnPlacementIfNeeded();
            ApplyOwnershipState(IsOwner);

            if (IsOwner)
            {
                PlayerContextRegistry.TrySetLocalPlayerId(PlayerContextRegistry.DefaultLocalPlayerId);
            }
        }

        public override void OnGainedOwnership()
        {
            ApplyOwnershipState(true);
            PlayerContextRegistry.TrySetLocalPlayerId(PlayerContextRegistry.DefaultLocalPlayerId);
        }

        public override void OnLostOwnership()
        {
            ApplyOwnershipState(false);
            PlayerContextRegistry.ClearLocalPlayerIfMissing();
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
            }

            ApplyOwnershipState(false);

            if (IsOwner)
            {
                PlayerContextRegistry.ClearLocalPlayerIfMissing();
            }
        }

        private void CacheComponents()
        {
            if (_cached)
            {
                return;
            }

            _playerController = GetComponent<PlayerController>();
            _inputHandler = GetComponent<PlayerInputHandler>();
            _characterController = GetComponent<CharacterController>();
            _firstPersonCamera = GetComponentInChildren<FirstPersonCamera>(true);
            _interactionSystems = GetComponentsInChildren<InteractionSystem>(true);
            _cameras = GetComponentsInChildren<Camera>(true);
            _audioListeners = GetComponentsInChildren<AudioListener>(true);
            _cached = true;
        }

        private void ApplyOwnershipState(bool isOwner)
        {
            if (_inputHandler != null)
            {
                _inputHandler.enabled = isOwner;
            }

            if (_playerController != null)
            {
                _playerController.enabled = isOwner;
            }

            if (_characterController != null)
            {
                _characterController.enabled = isOwner;
            }

            if (_firstPersonCamera != null)
            {
                _firstPersonCamera.enabled = isOwner;
            }

            if (_interactionSystems != null)
            {
                for (int i = 0; i < _interactionSystems.Length; i++)
                {
                    InteractionSystem interactionSystem = _interactionSystems[i];
                    if (interactionSystem != null)
                    {
                        interactionSystem.enabled = isOwner;
                    }
                }
            }

            if (_cameras != null)
            {
                for (int i = 0; i < _cameras.Length; i++)
                {
                    Camera cameraComponent = _cameras[i];
                    if (cameraComponent != null)
                    {
                        cameraComponent.enabled = isOwner;
                    }
                }
            }

            if (_audioListeners != null)
            {
                for (int i = 0; i < _audioListeners.Length; i++)
                {
                    AudioListener audioListener = _audioListeners[i];
                    if (audioListener != null)
                    {
                        audioListener.enabled = isOwner;
                    }
                }
            }

            LogOwnershipPresentationState(isOwner);
        }

        private void ApplyServerAuthoritativeSpawnPlacementIfNeeded()
        {
            if (!IsServer || !IsSpawned || _hasAppliedServerSpawnPlacement)
            {
                return;
            }

            if (!TryResolveSpawnPose(OwnerClientId, out Vector3 spawnPosition, out Quaternion spawnRotation, out string spawnSource))
            {
                return;
            }

            bool wasControllerEnabled = _characterController != null && _characterController.enabled;
            if (wasControllerEnabled)
            {
                _characterController.enabled = false;
            }

            transform.SetPositionAndRotation(spawnPosition, spawnRotation);

            if (wasControllerEnabled)
            {
                _characterController.enabled = true;
            }

            if (_playerController != null)
            {
                _playerController.ResetMovementStateAfterTeleport();
            }

            _hasAppliedServerSpawnPlacement = true;

            Debug.Log(
                $"[NetworkPlayerOwnershipGate] Server spawn placement applied for client {OwnerClientId} " +
                $"at {spawnPosition} via {spawnSource}.",
                this);
        }

        public override void OnDestroy()
        {
            if (IsServer)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
            }

            base.OnDestroy();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!IsServer || !IsSpawned)
            {
                return;
            }

            _hasAppliedServerSpawnPlacement = false;
            ApplyServerAuthoritativeSpawnPlacementIfNeeded();
        }

        private bool TryResolveSpawnPose(ulong ownerClientId, out Vector3 position, out Quaternion rotation, out string source)
        {
            if (TryResolveSpawnPoseFromMarkers(ownerClientId, out position, out rotation))
            {
                source = "scene marker";
                return true;
            }

            position = ResolveFallbackSpawnPosition(ownerClientId);
            rotation = transform.rotation;
            source = "deterministic offset";
            return true;
        }

        private bool TryResolveSpawnPoseFromMarkers(ulong ownerClientId, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;

            RefreshSpawnMarkerCacheIfNeeded();
            if (CachedSpawnMarkers.Count == 0)
            {
                return false;
            }

            int markerIndex = (int)(ownerClientId % (ulong)CachedSpawnMarkers.Count);
            Transform marker = CachedSpawnMarkers[markerIndex];
            if (marker == null)
            {
                return false;
            }

            position = marker.position;
            rotation = marker.rotation;
            return true;
        }

        private static void RefreshSpawnMarkerCacheIfNeeded()
        {
            string activeSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (string.Equals(_cachedSpawnMarkerSceneName, activeSceneName, StringComparison.Ordinal)
                && CachedSpawnMarkers.Count > 0)
            {
                return;
            }

            CachedSpawnMarkers.Clear();
            _cachedSpawnMarkerSceneName = activeSceneName;

            Transform[] allTransforms = FindObjectsByType<Transform>(FindObjectsInactive.Exclude);
            for (int i = 0; i < allTransforms.Length; i++)
            {
                Transform candidate = allTransforms[i];
                if (candidate == null)
                {
                    continue;
                }

                string candidateName = candidate.name;
                if (string.IsNullOrWhiteSpace(candidateName))
                {
                    continue;
                }

                if (!candidateName.StartsWith(SpawnMarkerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (candidate.GetComponentInParent<NetworkObject>() != null)
                {
                    continue;
                }

                CachedSpawnMarkers.Add(candidate);
            }

            CachedSpawnMarkers.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        }

        private Vector3 ResolveFallbackSpawnPosition(ulong ownerClientId)
        {
            Vector3 basePosition = new Vector3(0f, 2f, 0f);
            NetworkManager manager = NetworkManager;
            if (manager == null)
            {
                return basePosition;
            }

            int slot = ownerClientId == NetworkManager.ServerClientId
                ? 0
                : Mathf.Max(1, (int)ownerClientId);
            int row = slot / 2;
            int column = slot % 2;

            float offsetX = slot == 0
                ? 0f
                : (column == 0 ? -SpawnOffsetSpacing : SpawnOffsetSpacing);
            float offsetZ = slot == 0
                ? 0f
                : SpawnOffsetSpacing + (row * SpawnOffsetSpacing);
            Vector3 fallbackPosition = basePosition + new Vector3(offsetX, 0f, offsetZ);
            return ResolveGroundValidatedFallbackPosition(fallbackPosition, ownerClientId);
        }

        private Vector3 ResolveGroundValidatedFallbackPosition(Vector3 fallbackPosition, ulong ownerClientId)
        {
            Vector3 probeOrigin = new Vector3(
                fallbackPosition.x,
                fallbackPosition.y + FallbackSpawnProbeHeight,
                fallbackPosition.z);

            int layerMask = ~0;
            RaycastHit[] hits = Physics.RaycastAll(
                probeOrigin,
                Vector3.down,
                FallbackSpawnProbeDistance,
                layerMask,
                QueryTriggerInteraction.Ignore);

            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));

                bool sawIgnoredDynamicHit = false;
                for (int i = 0; i < hits.Length; i++)
                {
                    RaycastHit hit = hits[i];
                    Transform hitTransform = hit.transform;
                    if (hitTransform == null)
                    {
                        continue;
                    }

                    if (IsSelfOrChildTransform(hitTransform) || IsLikelyDynamicHitRoot(hitTransform))
                    {
                        sawIgnoredDynamicHit = true;
                        continue;
                    }

                    if (sawIgnoredDynamicHit)
                    {
                        LogFallbackProbeWarningOnce(
                            ownerClientId,
                            "dynamic_probe_hit_ignored",
                            $"[NetworkPlayerOwnershipGate] Fallback spawn probe for client {ownerClientId} ignored dynamic/self hit(s) and selected static ground '{hitTransform.name}' on layer {hit.collider.gameObject.layer}.",
                            this);
                    }

                    return hit.point + (Vector3.up * FallbackSpawnLift);
                }

                if (sawIgnoredDynamicHit)
                {
                    LogFallbackProbeWarningOnce(
                        ownerClientId,
                        "no_valid_ground_hit",
                        $"[NetworkPlayerOwnershipGate] Fallback spawn probe for client {ownerClientId} found only dynamic/self hits at xz=({fallbackPosition.x:0.00},{fallbackPosition.z:0.00}). Using raw fallback {fallbackPosition}.",
                        this);
                    return fallbackPosition;
                }
            }

            LogFallbackProbeWarningOnce(
                ownerClientId,
                "no_valid_ground_hit",
                $"[NetworkPlayerOwnershipGate] Fallback spawn probe found no ground for client {ownerClientId} at xz=({fallbackPosition.x:0.00},{fallbackPosition.z:0.00}). Using raw fallback {fallbackPosition}.",
                this);
            return fallbackPosition;
        }

        private bool IsSelfOrChildTransform(Transform candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            return candidate == transform || candidate.IsChildOf(transform);
        }

        private static bool IsLikelyDynamicHitRoot(Transform hitTransform)
        {
            if (hitTransform == null)
            {
                return false;
            }

            return hitTransform.GetComponentInParent<PlayerController>() != null
                   || hitTransform.GetComponentInParent<NPCController>() != null
                   || hitTransform.GetComponentInParent<NetworkObject>() != null
                   || hitTransform.GetComponentInParent<InteractableItem>() != null;
        }

        private static void LogFallbackProbeWarningOnce(ulong ownerClientId, string warningType, string message, UnityEngine.Object context)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            string sceneName = SceneManager.GetActiveScene().name;
            string key = $"{sceneName}|{ownerClientId}|{warningType}";
            if (!LoggedFallbackProbeWarnings.Add(key))
            {
                return;
            }

            Debug.LogWarning(message, context);
#endif
        }

        private void LogOwnershipPresentationState(bool isOwner)
        {
            int totalCameras = _cameras != null ? _cameras.Length : 0;
            int enabledCameras = 0;
            if (_cameras != null)
            {
                for (int i = 0; i < _cameras.Length; i++)
                {
                    if (_cameras[i] != null && _cameras[i].enabled)
                    {
                        enabledCameras++;
                    }
                }
            }

            int totalListeners = _audioListeners != null ? _audioListeners.Length : 0;
            int enabledListeners = 0;
            if (_audioListeners != null)
            {
                for (int i = 0; i < _audioListeners.Length; i++)
                {
                    if (_audioListeners[i] != null && _audioListeners[i].enabled)
                    {
                        enabledListeners++;
                    }
                }
            }

            ulong localClientId = NetworkManager != null ? NetworkManager.LocalClientId : 0UL;
            Debug.Log(
                $"[NetworkPlayerOwnershipGate] client={OwnerClientId} local={localClientId} owner={isOwner} " +
                $"cameras={enabledCameras}/{totalCameras} listeners={enabledListeners}/{totalListeners}.",
                this);
        }
    }
}
