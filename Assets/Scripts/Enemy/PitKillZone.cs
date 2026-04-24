using Unity.Netcode;
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

    [Header("Audio")]
    [SerializeField] AudioClip spikeStabClip;
    [SerializeField, Range(0f, 1f)] float spikeStabVolume = 1f;
    [Tooltip("Min seconds between spike SFX for the same collider (OnTriggerStay).")]
    [SerializeField, Min(0.05f)] float spikeSoundSameColliderCooldown = 0.35f;

    Collider _zoneCollider;
    AudioSource _spikeAudio;
    EntityId _lastSpikeSoundColliderEntity;
    float _nextSpikeSoundTime;

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

        PlayerHealth playerHealth = other.GetComponentInParent<PlayerHealth>();
        if (playerHealth != null)
        {
            if (playerHealth.IsDead)
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
            zombieHealth.Die();
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
