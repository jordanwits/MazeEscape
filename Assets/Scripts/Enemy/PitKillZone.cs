using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine.AI;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class PitKillZone : MonoBehaviour
{
    [SerializeField] bool destroyIfNoZombieHealth;
    [SerializeField] bool addKinematicRigidbody = true;
    [Header("Jailor safety")]
    [Tooltip("If the Jailor enters this pit trigger, teleport him back to the nearest NavMesh point instead of allowing pit grabs.")]
    [SerializeField] bool rescueJailorFromPit = true;
    [SerializeField, Min(0.05f)] float jailorRescueCooldown = 0.2f;
    [SerializeField, Min(0.5f)] float jailorRescueSampleRadius = 12f;
    [SerializeField, Min(0f)] float jailorRescueLift = 0.08f;

    [Header("Audio")]
    [SerializeField] AudioClip spikeStabClip;
    [SerializeField, Range(0f, 1f)] float spikeStabVolume = 1f;
    [Tooltip("Min seconds between spike SFX for the same victim (OnTriggerStay). Floored at 1s in code.")]
    [SerializeField, Min(0.05f)] float spikeSoundSameColliderCooldown = 1f;

    /// <summary>Floor for the per-victim SFX window. The authored 0.35s predates the sound playing on every
    /// peer off local detection, where a body's several colliders arrive over more frames.</summary>
    const float MinSpikeSoundWindowSeconds = 1f;

    /// <summary>
    /// Every live pit, so the replicated player-kill path can resolve which one stabbed from the kill position
    /// alone (see <see cref="PlayNearestPitStabSfx"/>). Pits are deterministic local maze geometry, so the same
    /// pit exists at the same place on every peer — no networked identity is needed.
    /// </summary>
    static readonly List<PitKillZone> s_ActiveZones = new();

    Collider _zoneCollider;
    AudioSource _spikeAudio;
    EntityId _lastSpikeSoundColliderEntity;
    float _nextSpikeSoundTime;
    float _nextJailorRescueTime;

    void Reset()
    {
        ConfigureZone();
#if UNITY_EDITOR
        AutoAssignSpikeStabClipInEditor();
#endif
    }

    void Awake()
    {
        ConfigureZone();
        EnsureSpikeAudioSource();
#if UNITY_EDITOR
        AutoAssignSpikeStabClipInEditor();
#endif
    }

    void OnEnable()
    {
        s_ActiveZones.Add(this);
    }

    void OnDisable()
    {
        s_ActiveZones.Remove(this);
    }

    /// <summary>
    /// Plays the spike stab for a replicated pit kill: finds the live pit nearest the kill point and sounds it
    /// from there. Called on every peer from <see cref="NetworkPlayerRagdoll"/>'s relay Rpc, so the stab is
    /// heard once, positionally, by everyone — instead of only by whoever's local trigger happened to win the
    /// race against death replication.
    /// </summary>
    public static void PlayNearestPitStabSfx(Vector3 worldPosition, float maxDistance = 6f)
    {
        PitKillZone nearest = null;
        float bestSqr = maxDistance * maxDistance;

        for (int i = s_ActiveZones.Count - 1; i >= 0; i--)
        {
            PitKillZone zone = s_ActiveZones[i];
            if (zone == null)
            {
                s_ActiveZones.RemoveAt(i); // level unload destroyed it without OnDisable running
                continue;
            }

            float sqr = (zone.transform.position - worldPosition).sqrMagnitude;
            if (sqr > bestSqr)
                continue;

            bestSqr = sqr;
            nearest = zone;
        }

        if (nearest != null)
            nearest.PlaySpikeStabFromRelay();
    }

    void OnTriggerEnter(Collider other)
    {
        TryKill(other);
    }

    void OnTriggerStay(Collider other)
    {
        TryKill(other);
    }

    void ConfigureZone()
    {
        _zoneCollider = GetComponent<Collider>();
        if (_zoneCollider != null)
            _zoneCollider.isTrigger = true;

        if (!addKinematicRigidbody)
            return;

        Rigidbody zoneRigidbody = GetComponent<Rigidbody>();
        if (zoneRigidbody == null)
            zoneRigidbody = gameObject.AddComponent<Rigidbody>();

        zoneRigidbody.isKinematic = true;
        zoneRigidbody.useGravity = false;
    }

    /// <summary>
    /// The kill is server-only; the spike stab is not. A networked PLAYER kill relays the sound through the
    /// victim's own player object (<see cref="NetworkPlayerRagdoll.BroadcastPitStabSfxFromServer"/>), because
    /// a third-party observer's blocking-proxy-vs-trigger race usually loses to death replication and it heard
    /// nothing. Offline — and for zombies, which have no player object to relay through — each peer still
    /// sounds it off its OWN trigger detection. <see cref="ShouldApplyPitKills"/> keeps every state change on
    /// the server exactly as before.
    /// </summary>
    void TryKill(Collider other)
    {
        if (other == null)
            return;

        bool applyKills = ShouldApplyPitKills();

        JailorAI jailor = other.GetComponentInParent<JailorAI>();
        if (jailor != null)
        {
            if (applyKills)
                TryRescueJailorFromPit(jailor);
            return;
        }

        PlayerHealth playerHealth = other.GetComponentInParent<PlayerHealth>();
        if (playerHealth != null)
        {
            if (!CanPitKillPlayer(playerHealth))
                return;

            // Another peer's copy of a player knocked down moments ago runs a presentation-only launch sim (see
            // NetworkPlayerRagdoll) that is not where the owner's body really is. On the server that body is judged
            // from its streamed pose instead (ServerTryKillStreamedRagdoll), so these bones must not kill.
            if (playerHealth.TryGetComponent(out PlayerRagdollController ragdoll) && ragdoll.IsObserverLocalSimActive)
                return;

            // Offline there is no Rpc to carry the stab, so local detection sounds it. In a session it rides
            // the server's relay below — which loops back to the host too — so nothing plays locally here.
            bool networked = IsNetworkedSession();
            if (!networked)
                TryPlaySpikeStabSfx(other);

            if (!applyKills)
                return;

            KillPlayer(playerHealth, other.bounds.center, networked);
            return;
        }

        ZombieHealth zombieHealth = other.GetComponentInParent<ZombieHealth>();
        if (zombieHealth != null)
        {
            if (zombieHealth.IsDead)
                return;

            TryPlaySpikeStabSfx(other);

            if (applyKills)
                zombieHealth.Die(fromPit: true);
            return;
        }

        if (applyKills && destroyIfNoZombieHealth)
            Destroy(other.transform.root.gameObject);
    }

    static bool CanPitKillPlayer(PlayerHealth playerHealth)
    {
        if (playerHealth.IsDead || IsCarriedByJailor(playerHealth))
            return false;

        NetworkPlayerRespawn playerRespawn = playerHealth.GetComponent<NetworkPlayerRespawn>();
        return playerRespawn == null || !playerRespawn.ShouldIgnorePitKill();
    }

    static void KillPlayer(PlayerHealth playerHealth, Vector3 stabPoint, bool networked)
    {
        if (networked)
        {
            // Relayed ahead of the kill so the stab is not queued behind the death/ragdoll Rpcs.
            NetworkPlayerRagdoll netRagdoll = playerHealth.GetComponent<NetworkPlayerRagdoll>();
            if (netRagdoll != null)
                netRagdoll.BroadcastPitStabSfxFromServer(stabPoint);
        }

        playerHealth.TakeDamage(playerHealth.MaxHealth);
    }

    /// <summary>
    /// Server: pit kills for a joined client's free ragdoll, judged on the pose its owner streams. The server's own
    /// copy of that body can't be used: its short local launch sim diverges from the owner's (and is ignored in
    /// <see cref="TryKill"/>), and after the hand-over its bones are a puppet with no colliders, so a body that
    /// tumbled into a pit lay on the spikes alive until the get-up snapped the root and its proxy capsule in. Any
    /// streamed bone inside a live pit kills, as any bone collider of the host's own body does.
    /// </summary>
    public static void ServerTryKillStreamedRagdoll(PlayerHealth playerHealth, Vector3[] bonePositions)
    {
        if (playerHealth == null || bonePositions == null || !ShouldApplyPitKills() || !CanPitKillPlayer(playerHealth))
            return;

        for (int z = s_ActiveZones.Count - 1; z >= 0; z--)
        {
            PitKillZone zone = s_ActiveZones[z];
            if (zone == null)
            {
                s_ActiveZones.RemoveAt(z); // level unload destroyed it without OnDisable running
                continue;
            }

            for (int b = 0; b < bonePositions.Length; b++)
            {
                if (!zone.ContainsPoint(bonePositions[b]))
                    continue;

                KillPlayer(playerHealth, bonePositions[b], IsNetworkedSession());
                return;
            }
        }
    }

    bool ContainsPoint(Vector3 worldPoint)
    {
        if (_zoneCollider == null || !_zoneCollider.enabled || !_zoneCollider.bounds.Contains(worldPoint))
            return false;

        // The kill zones are box triggers; ClosestPoint hands back the point itself when it lies inside.
        return (_zoneCollider.ClosestPoint(worldPoint) - worldPoint).sqrMagnitude < 1e-6f;
    }

    static bool ShouldApplyPitKills()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return true;
        return nm.IsServer;
    }

    static bool IsNetworkedSession()
    {
        NetworkManager nm = NetworkManager.Singleton;
        return nm != null && nm.IsListening;
    }

    void TryRescueJailorFromPit(JailorAI jailor)
    {
        if (!rescueJailorFromPit || jailor == null || Time.time < _nextJailorRescueTime)
            return;

        _nextJailorRescueTime = Time.time + Mathf.Max(0.05f, jailorRescueCooldown);

        Transform jailorTransform = jailor.transform;
        float sampleRadius = Mathf.Max(0.5f, jailorRescueSampleRadius);
        // Sample from several heights so deep pits still resolve to rim NavMesh; avoid relying on isOnNavMesh before Warp.
        Vector3[] origins =
        {
            jailorTransform.position + Vector3.up,
            jailorTransform.position + Vector3.up * 4f,
            jailorTransform.position + Vector3.up * 10f,
            jailorTransform.position,
        };

        NavMeshHit hit = default;
        bool sampled = false;
        for (int i = 0; i < origins.Length; i++)
        {
            if (NavMesh.SamplePosition(origins[i], out hit, sampleRadius, NavMesh.AllAreas))
            {
                sampled = true;
                break;
            }
        }

        if (!sampled && NavMesh.SamplePosition(jailorTransform.position, out hit, sampleRadius * 2f, NavMesh.AllAreas))
            sampled = true;

        if (!sampled)
            return;

        // Extra vertical lift avoids sampling the pit floor NavMesh and reduces snap-back onto the pit link immediately after warp.
        float lift = Mathf.Max(0.12f, jailorRescueLift);
        Vector3 safePosition = hit.position + Vector3.up * lift;
        jailor.NotifyRescuedFromPitWarp(safePosition);
    }

    static bool IsCarriedByJailor(PlayerHealth player)
    {
        if (player == null)
            return false;

        NetworkPlayerAvatar avatar = player.GetComponent<NetworkPlayerAvatar>();
        return avatar != null && avatar.IsCarriedByJailor;
    }

    void EnsureSpikeAudioSource()
    {
        if (_spikeAudio != null)
            return;

        _spikeAudio = GetComponent<AudioSource>();
        if (_spikeAudio == null)
            _spikeAudio = gameObject.AddComponent<AudioSource>();

        _spikeAudio.playOnAwake = false;
        _spikeAudio.loop = false;
        _spikeAudio.spatialBlend = 1f;
        _spikeAudio.minDistance = 0.5f;
        _spikeAudio.maxDistance = 35f;
        _spikeAudio.rolloffMode = AudioRolloffMode.Linear;
    }

    /// <summary>
    /// The relay counterpart of <see cref="TryPlaySpikeStabSfx"/>: no victim collider to key the window on, so
    /// it gates purely on time — the server sends exactly one of these per kill.
    /// </summary>
    void PlaySpikeStabFromRelay()
    {
        if (spikeStabClip == null)
            return;

        EnsureSpikeAudioSource();
        if (_spikeAudio == null)
            return;

        float now = Time.time;
        if (now < _nextSpikeSoundTime)
            return;
        _nextSpikeSoundTime = now + Mathf.Max(MinSpikeSoundWindowSeconds, spikeSoundSameColliderCooldown);

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_spikeAudio);

        _spikeAudio.PlayOneShot(spikeStabClip, Mathf.Max(0f, spikeStabVolume));
    }

    void TryPlaySpikeStabSfx(Collider other)
    {
        if (spikeStabClip == null || _spikeAudio == null || other == null)
            return;

        // Keyed on the victim's ROOT, not the individual collider: a body sweeps this trigger with its
        // controller, its blocking proxy and (once it goes limp) a dozen bone colliders, and a per-collider key
        // let every one of them bypass the window. One victim = one stab.
        EntityId id = other.transform.root.GetEntityId();
        float now = Time.time;
        if (now < _nextSpikeSoundTime && id == _lastSpikeSoundColliderEntity)
            return;

        _lastSpikeSoundColliderEntity = id;
        _nextSpikeSoundTime = now + Mathf.Max(MinSpikeSoundWindowSeconds, spikeSoundSameColliderCooldown);

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_spikeAudio);

        _spikeAudio.PlayOneShot(spikeStabClip, Mathf.Max(0f, spikeStabVolume));
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        AutoAssignSpikeStabClipInEditor();
    }

    void AutoAssignSpikeStabClipInEditor()
    {
        if (spikeStabClip == null)
            spikeStabClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/SpikeStab.wav");
    }
#endif
}
