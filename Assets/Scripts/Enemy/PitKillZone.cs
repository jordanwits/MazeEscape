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
    [Tooltip("Min seconds between spike SFX for the same collider (OnTriggerStay).")]
    [SerializeField, Min(0.05f)] float spikeSoundSameColliderCooldown = 0.35f;

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

    void TryKill(Collider other)
    {
        if (other == null)
            return;

        if (!ShouldApplyPitKills())
            return;

        JailorAI jailor = other.GetComponentInParent<JailorAI>();
        if (jailor != null)
        {
            TryRescueJailorFromPit(jailor);
            return;
        }

        PlayerHealth playerHealth = other.GetComponentInParent<PlayerHealth>();
        if (playerHealth != null)
        {
            if (playerHealth.IsDead)
                return;
            if (IsCarriedByJailor(playerHealth))
                return;

            NetworkPlayerRespawn playerRespawn = playerHealth.GetComponent<NetworkPlayerRespawn>();
            if (playerRespawn != null && playerRespawn.ShouldIgnorePitKill())
                return;

            TryPlaySpikeStabSfx(other);
            playerHealth.TakeDamage(playerHealth.MaxHealth);
            return;
        }

        ZombieHealth zombieHealth = other.GetComponentInParent<ZombieHealth>();
        if (zombieHealth != null)
        {
            if (zombieHealth.IsDead)
                return;

            TryPlaySpikeStabSfx(other);
            zombieHealth.Die(fromPit: true);
            return;
        }

        if (destroyIfNoZombieHealth)
            Destroy(other.transform.root.gameObject);
    }

    static bool ShouldApplyPitKills()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return true;
        return nm.IsServer;
    }

    void TryRescueJailorFromPit(JailorAI jailor)
    {
        if (!rescueJailorFromPit || jailor == null || Time.time < _nextJailorRescueTime)
            return;

        _nextJailorRescueTime = Time.time + Mathf.Max(0.05f, jailorRescueCooldown);

        jailor.AbortOffMeshJumpIfActive();

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

        Vector3 safePosition = hit.position + Vector3.up * Mathf.Max(0f, jailorRescueLift);
        NavMeshAgent jailorAgent = jailor.GetComponent<NavMeshAgent>();
        CharacterController jailorController = jailor.GetComponent<CharacterController>();

        bool ccWasEnabled = jailorController != null && jailorController.enabled;
        if (jailorController != null)
            jailorController.enabled = false;

        if (jailorAgent != null && jailorAgent.enabled)
        {
            jailorAgent.Warp(safePosition);
            if (ccWasEnabled && jailorController != null)
                jailorController.enabled = true;
            return;
        }

        jailorTransform.position = safePosition;

        if (ccWasEnabled && jailorController != null)
            jailorController.enabled = true;
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

    void TryPlaySpikeStabSfx(Collider other)
    {
        if (spikeStabClip == null || _spikeAudio == null || other == null)
            return;

        EntityId id = other.GetEntityId();
        float now = Time.time;
        if (now < _nextSpikeSoundTime && id == _lastSpikeSoundColliderEntity)
            return;

        _lastSpikeSoundColliderEntity = id;
        _nextSpikeSoundTime = now + spikeSoundSameColliderCooldown;

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
