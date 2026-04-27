using Unity.Netcode;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Example trap: add a trigger collider, assign a force direction (world space) and magnitude.
/// In multiplayer, only the server applies ragdoll so all clients stay in sync.
/// </summary>
[RequireComponent(typeof(Collider))]
public class RagdollTrap : MonoBehaviour
{
    const float TrapDamageAmount = 25f;

    public enum KnockbackDirectionMode
    {
        CustomWorldVector,
        OppositeTrapForward,
        TrapForward,
        [Tooltip("From trap position toward the player on the floor — works even if trap forward is wrong.")]
        PushAwayFromTrapHorizontal,
    }

    [SerializeField] KnockbackDirectionMode knockbackDirection = KnockbackDirectionMode.PushAwayFromTrapHorizontal;
    [Tooltip("With Velocity Change: horizontal push in m/s. With Impulse: impulse strength (mass-dependent).")]
    [SerializeField] float impulseMagnitude = 12f;
    [SerializeField] Vector3 forceDirectionWorld = Vector3.forward;
    [Tooltip("If true, knockback direction ignores vertical component (slide along the floor).")]
    [SerializeField] bool knockbackHorizontalOnly = true;
    [Tooltip("Added upward delta. With Velocity Change this is m/s upward.")]
    [SerializeField] float upwardImpulse = 5f;
    [Tooltip("Velocity Change = reliable knock (m/s). Impulse = physics impulse at hips.")]
    [SerializeField] ForceMode forceMode = ForceMode.VelocityChange;
    [Tooltip("If set (or auto-found on a parent), hits only apply while that swing trap is swung out — not while it returns to rest.")]
    [SerializeField] PivotSwingTrap swingTrapDamageGate;

    [Header("Audio")]
    [SerializeField] AudioClip trapHitMetallicClip;
    [SerializeField, Range(0f, 1f)] float trapHitMetallicVolume = 0.9f;
    [Tooltip("Min seconds between hit sounds for the same collider (OnTriggerStay spam).")]
    [SerializeField, Min(0.05f)] float trapHitSoundSameColliderCooldown = 0.32f;

    AudioSource _hitAudio;
    EntityId _lastMetallicColliderEntity;
    float _nextMetallicSoundTime;

    void Awake()
    {
        if (swingTrapDamageGate == null)
            swingTrapDamageGate = GetComponentInParent<PivotSwingTrap>();

        EnsureHitAudioSource();
#if UNITY_EDITOR
        AutoAssignTrapHitClipInEditor();
#endif
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        AutoAssignTrapHitClipInEditor();
    }

    void AutoAssignTrapHitClipInEditor()
    {
        if (trapHitMetallicClip == null)
            trapHitMetallicClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/MetalicWack.wav");
    }
#endif

    void EnsureHitAudioSource()
    {
        if (_hitAudio != null)
            return;

        _hitAudio = GetComponent<AudioSource>();
        if (_hitAudio == null)
            _hitAudio = gameObject.AddComponent<AudioSource>();

        _hitAudio.playOnAwake = false;
        _hitAudio.loop = false;
        _hitAudio.spatialBlend = 1f;
        _hitAudio.minDistance = 0.5f;
        _hitAudio.maxDistance = 35f;
        _hitAudio.rolloffMode = AudioRolloffMode.Linear;
    }

    bool TryPlayTrapHitMetallic(Collider other)
    {
        if (trapHitMetallicClip == null || _hitAudio == null || other == null)
            return false;

        EntityId id = other.GetEntityId();
        float now = Time.time;
        if (now < _nextMetallicSoundTime && id == _lastMetallicColliderEntity)
            return false;

        _lastMetallicColliderEntity = id;
        _nextMetallicSoundTime = now + trapHitSoundSameColliderCooldown;

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_hitAudio);

        _hitAudio.PlayOneShot(trapHitMetallicClip, Mathf.Max(0f, trapHitMetallicVolume));
        return true;
    }

    void Reset()
    {
        Collider c = GetComponent<Collider>();
        if (c != null)
            c.isTrigger = true;
    }

    void OnTriggerEnter(Collider other)
    {
        if (!isActiveAndEnabled)
            return;

        TryHit(other);
    }

    void OnTriggerStay(Collider other)
    {
        if (!isActiveAndEnabled)
            return;

        PlayerRagdollController ragdoll = other.GetComponentInParent<PlayerRagdollController>();
        if (ragdoll != null && ragdoll.IsRagdolled)
            return;

        TryHit(other);
    }

    void TryHit(Collider other)
    {
        if (swingTrapDamageGate != null && !swingTrapDamageGate.CanDealSwingTrapDamage)
            return;

        PlayerHealth playerHealth = other.GetComponentInParent<PlayerHealth>();
        if (playerHealth != null && IsCarriedByJailor(playerHealth))
            return;

        ZombieHealth zombieHealth = other.GetComponentInParent<ZombieHealth>();
        if (zombieHealth != null && !zombieHealth.IsDead)
        {
            NetworkManager networkManager = NetworkManager.Singleton;
            if (networkManager != null && networkManager.IsListening)
            {
                if (networkManager.IsServer)
                {
                    TryPlayTrapHitMetallic(other);
                    zombieHealth.Die();
                    return;
                }

                PivotSwingTrap pivot = swingTrapDamageGate != null
                    ? swingTrapDamageGate
                    : GetComponentInParent<PivotSwingTrap>();
                NetworkObject zombieNetObj = other.GetComponentInParent<NetworkObject>();
                if (pivot != null && zombieNetObj != null)
                {
                    TryPlayTrapHitMetallic(other);
                    pivot.RequestZombieTrapKillServerRpc(zombieNetObj.NetworkObjectId);
                }

                return;
            }

            TryPlayTrapHitMetallic(other);
            zombieHealth.Die();
            return;
        }

        NetworkPlayerRagdoll netRagdoll = other.GetComponentInParent<NetworkPlayerRagdoll>();
        PlayerRagdollController ragdoll = other.GetComponentInParent<PlayerRagdollController>();

        Vector3 hitCenter = other.bounds.center;
        Vector3 dir = ResolveKnockbackDirection(hitCenter);
        Vector3 force = dir * impulseMagnitude;
        if (upwardImpulse > 0f)
            force += Vector3.up * upwardImpulse;
        Vector3 hitPoint = hitCenter;

        NetworkManager net = NetworkManager.Singleton;
        if (net != null && net.IsListening && netRagdoll != null)
        {
            if (net.IsServer)
            {
                TryPlayTrapHitMetallic(other);
                netRagdoll.RequestTrapHitFromServer(force, hitPoint, TrapDamageAmount, forceMode);
                return;
            }

            NetworkObject playerNetObj = other.GetComponentInParent<NetworkObject>();
            if (playerNetObj != null && playerNetObj.IsOwner)
            {
                TryPlayTrapHitMetallic(other);
                netRagdoll.RequestTrapHitServerRpc(force, hitPoint, TrapDamageAmount, (byte)forceMode);
            }

            return;
        }

        PlayerHealth health = playerHealth;
        if (health != null && health.IsDead)
            return;

        if (health == null && ragdoll == null)
            return;

        TryPlayTrapHitMetallic(other);
        health?.TakeDamage(TrapDamageAmount);

        if (ragdoll != null)
            ragdoll.ActivateRagdoll(force, hitPoint, forceMode);
    }

    static bool IsCarriedByJailor(PlayerHealth player)
    {
        if (player == null)
            return false;

        NetworkPlayerAvatar avatar = player.GetComponent<NetworkPlayerAvatar>();
        return avatar != null && avatar.IsCarriedByJailor;
    }

    Vector3 ResolveKnockbackDirection(Vector3 otherBoundsCenter)
    {
        Vector3 dir;
        switch (knockbackDirection)
        {
            case KnockbackDirectionMode.PushAwayFromTrapHorizontal:
                dir = otherBoundsCenter - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f)
                    dir = -transform.forward;
                dir.y = 0f;
                return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
            case KnockbackDirectionMode.TrapForward:
                dir = transform.forward;
                break;
            case KnockbackDirectionMode.OppositeTrapForward:
                dir = -transform.forward;
                break;
            default:
                dir = forceDirectionWorld.sqrMagnitude > 1e-6f ? forceDirectionWorld : transform.forward;
                break;
        }

        if (knockbackHorizontalOnly)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-6f)
                dir = -transform.forward;
            dir.y = 0f;
        }

        return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
    }
}
