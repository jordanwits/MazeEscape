using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Interactable teleport orb. When a player uses it, the SERVER picks a random NavMesh-validated floor
/// point in the maze and teleports that player there via <see cref="NetworkPlayerRespawn.ServerTeleport"/>
/// (owner-authoritative move). The destination is guaranteed walkable, floor-level, inset from walls, and
/// clearance-checked, so the player never falls through the map or clips geometry.
///
/// This component only adds interaction + the teleport action. The look lives on the child
/// InnerOrb / OuterEnergyShell / Wisps built earlier. It mirrors <see cref="MazeChest"/>'s convention:
/// a NetworkBehaviour reachable via GetComponentInParent from a solid (non-trigger) interaction collider,
/// exposing IsInInteractRange + a TryRequestUse entry point, with a Server RPC that resolves the caller.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class TeleportOrb : NetworkBehaviour
{
    [Header("Interaction")]
    [Tooltip("Max distance (m) the player can be from the orb to use it. Matches the interact prompt range.")]
    [SerializeField] float interactMaxDistance = 5f;

    [Header("Teleport")]
    [Tooltip("Seconds the player must hold E (while aiming at the orb) to trigger the teleport. The crosshair ring fills and the interact SFX both run over this time.")]
    [Range(0.25f, 8f)]
    [SerializeField] float interactHoldSeconds = 2.3f;
    [Tooltip("Try to land the player at least this far (m) from the orb, so the teleport actually relocates them. Relaxed automatically on small maps.")]
    [SerializeField] float minTeleportDistance = 6f;

    [Header("Reuse")]
    [Tooltip("If true the orb can be used only once and then goes dark. If false it can be reused after the cooldown.")]
    [SerializeField] bool consumeOnUse = false;
    [Tooltip("Reusable orbs ignore repeat uses for this many seconds (prevents accidental double-trigger). Ignored when Consume On Use is set.")]
    [SerializeField, Min(0f)] float reuseCooldownSeconds = 2f;

    [Header("Consumed visuals (optional)")]
    [Tooltip("Colliders disabled when the orb is consumed so it can no longer be interacted with. If empty, uses every collider on this prefab.")]
    [SerializeField] Collider[] interactionColliders;
    [Tooltip("Particle systems stopped when the orb is consumed (e.g. the Wisps).")]
    [SerializeField] ParticleSystem[] consumeStopsParticles;

    [Header("Audio")]
    [Tooltip("Looping ambient hum played from the orb (3D/spatial), routed through the SFX bus.")]
    [SerializeField] AudioClip ambienceClip;
    [Tooltip("Charge sound played WHILE a player holds E on the orb. The hold duration is matched to this clip's length.")]
    [SerializeField] AudioClip interactClip;
    [SerializeField, Range(0f, 1f)] float ambienceVolume = 0.7f;
    [SerializeField, Min(0.1f)] float ambienceMaxDistance = 12f;
    [SerializeField, Range(0f, 1f)] float interactVolume = 0.95f;
    [SerializeField, Min(0.1f)] float interactAudioMaxDistance = 10f;

    readonly NetworkVariable<bool> _consumed = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    bool _offlineConsumed;
    float _serverCooldownUntil;
    float _offlineCooldownUntil;
    AudioSource _ambienceSource;
    AudioSource _interactSource;

    public bool IsConsumed => IsSpawned ? _consumed.Value : _offlineConsumed;

    /// <summary>
    /// How long the player must hold to activate, from the Interact Hold Seconds slider. The crosshair ring
    /// fills and the interact SFX play over this time. Defaults to roughly the interact clip length.
    /// </summary>
    public float ChargeDuration => interactHoldSeconds;

    void Awake()
    {
        if (interactionColliders == null || interactionColliders.Length == 0)
            interactionColliders = GetComponentsInChildren<Collider>(true);
    }

    void Start()
    {
        // Route once GameAudioManager (execution order -20) is up, then start the ambient loop.
        EnsureAudioSources();
        if (!IsConsumed)
            PlayAmbience();
    }

    public override void OnNetworkSpawn()
    {
        _consumed.OnValueChanged += OnConsumedChanged;
        if (_consumed.Value)
            ApplyConsumedVisual();
    }

    public override void OnNetworkDespawn()
    {
        _consumed.OnValueChanged -= OnConsumedChanged;
    }

    public bool IsInInteractRange(Vector3 worldPosition)
    {
        float maxSqr = interactMaxDistance * interactMaxDistance;
        return (transform.position - worldPosition).sqrMagnitude <= maxSqr;
    }

    /// <summary>
    /// Client entry point, called by PlayerController when the local player presses interact while aiming
    /// at the orb. Online: forwards to the server (which re-validates and teleports the caller). Offline:
    /// teleports the local interactor directly.
    /// </summary>
    public void TryRequestUse(Vector3 interactorPosition, PlayerController interactor)
    {
        if (IsConsumed)
            return;
        if (!IsInInteractRange(interactorPosition))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool localOnly = nm == null || !nm.IsListening || !IsSpawned;

        if (localOnly)
        {
            if (Time.time < _offlineCooldownUntil)
                return;
            if (interactor == null || !interactor.TryGetComponent(out NetworkPlayerRespawn respawn))
                return;
            if (!TryResolveDestination(interactor.transform, out Vector3 dest, out Quaternion rot))
                return;

            respawn.LocalTeleport(dest, rot);

            if (consumeOnUse)
            {
                _offlineConsumed = true;
                ApplyConsumedVisual();
            }
            else
            {
                _offlineCooldownUntil = Time.time + reuseCooldownSeconds;
            }
            return;
        }

        UseOrbServerRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void UseOrbServerRpc(RpcParams rpcParams = default)
    {
        if (!ServerCanUse())
            return;

        ulong senderId = rpcParams.Receive.SenderClientId;
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out NetworkClient client)
            || client.PlayerObject == null)
        {
            return;
        }

        NetworkObject playerObject = client.PlayerObject;
        if (!IsInInteractRange(playerObject.transform.position))
            return;

        // Don't teleport a player who is dead/respawning or currently carried by the Jailor.
        if (playerObject.TryGetComponent(out NetworkPlayerAvatar avatar) && avatar.IsCarriedByJailor)
            return;
        if (!playerObject.TryGetComponent(out NetworkPlayerRespawn respawn))
            return;

        if (!TryResolveDestination(playerObject.transform, out Vector3 dest, out Quaternion rot))
            return;

        respawn.ServerTeleport(dest, rot);

        if (consumeOnUse)
            _consumed.Value = true;
        else
            _serverCooldownUntil = Time.time + reuseCooldownSeconds;
    }

    bool ServerCanUse()
    {
        if (consumeOnUse && _consumed.Value)
            return false;
        if (!consumeOnUse && Time.time < _serverCooldownUntil)
            return false;
        return true;
    }

    /// <summary>
    /// Resolves a validated teleport destination for the given player (server/host or offline caller).
    /// Prefers a random walkable NavMesh point from the maze coordinator; falls back to a registered
    /// spawn point if NavMesh sampling is unavailable.
    /// </summary>
    bool TryResolveDestination(Transform playerTransform, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = playerTransform.rotation;

        float radius = 0.3f;
        float height = 1.8f;
        if (playerTransform.TryGetComponent(out CharacterController cc))
        {
            radius = cc.radius;
            height = cc.height;
        }

        bool found = false;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.TryGetComponent(out ProceduralMazeCoordinator coordinator) && coordinator != null)
        {
            found = coordinator.TryGetRandomWalkableTeleportPoint(
                transform.position, minTeleportDistance, radius, height, out position);
        }

        if (!found
            && MultiplayerSpawnRegistry.Instance != null
            && MultiplayerSpawnRegistry.Instance.TryGetRespawnSpawn(out Vector3 spawnPos, out Quaternion spawnRot))
        {
            position = spawnPos;
            rotation = spawnRot;
            found = true;
        }

        if (!found)
            return false;

        // Keep the player upright, preserving only their yaw so they don't end up tilted.
        Vector3 euler = rotation.eulerAngles;
        rotation = Quaternion.Euler(0f, euler.y, 0f);
        return true;
    }

    void EnsureAudioSources()
    {
        if (_ambienceSource == null)
            _ambienceSource = CreateOrbAudioSource("OrbAmbienceAudio", ambienceClip, true, ambienceVolume, ambienceMaxDistance);
        if (_interactSource == null)
            _interactSource = CreateOrbAudioSource("OrbInteractAudio", interactClip, false, interactVolume, interactAudioMaxDistance);
    }

    AudioSource CreateOrbAudioSource(string sourceName, AudioClip clip, bool loop, float volume, float maxDistance)
    {
        GameObject go = new GameObject(sourceName) { layer = gameObject.layer };
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 0.45f, 0f);   // emit from the orb, not the pedestal base
        AudioSource src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.loop = loop;
        src.playOnAwake = false;
        src.spatialBlend = 1f;                       // fully 3D / positional
        src.rolloffMode = AudioRolloffMode.Linear;
        src.dopplerLevel = 0f;
        src.minDistance = Mathf.Min(1.5f, maxDistance);
        src.maxDistance = maxDistance;
        src.volume = volume;
        GameAudioManager.RouteSfxSource(src);        // no-op if the manager isn't up yet
        return src;
    }

    void PlayAmbience()
    {
        EnsureAudioSources();
        if (_ambienceSource != null && ambienceClip != null && !_ambienceSource.isPlaying)
        {
            _ambienceSource.clip = ambienceClip;
            _ambienceSource.Play();
        }
    }

    /// <summary>Called by the interacting player's controller when the hold-charge begins (plays the interact SFX from the start).</summary>
    public void BeginInteractCharge()
    {
        EnsureAudioSources();
        if (_interactSource == null || interactClip == null)
            return;
        _interactSource.clip = interactClip;
        _interactSource.Stop();
        _interactSource.time = 0f;
        _interactSource.Play();
    }

    /// <summary>Called when the hold ends (release / look away / complete) — stops the interact SFX so it only sounds while held.</summary>
    public void EndInteractCharge()
    {
        if (_interactSource != null && _interactSource.isPlaying)
            _interactSource.Stop();
    }

    void OnConsumedChanged(bool previous, bool current)
    {
        if (current)
            ApplyConsumedVisual();
    }

    void ApplyConsumedVisual()
    {
        if (interactionColliders != null)
        {
            for (int i = 0; i < interactionColliders.Length; i++)
            {
                if (interactionColliders[i] != null)
                    interactionColliders[i].enabled = false;
            }
        }

        if (consumeStopsParticles != null)
        {
            for (int i = 0; i < consumeStopsParticles.Length; i++)
            {
                if (consumeStopsParticles[i] != null)
                    consumeStopsParticles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        if (_ambienceSource != null)
            _ambienceSource.Stop();
        if (_interactSource != null)
            _interactSource.Stop();
    }
}
