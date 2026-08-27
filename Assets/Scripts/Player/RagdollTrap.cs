using System.Collections.Generic;
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
        [Tooltip("Along the parent PivotSwingTrap's swing-out direction at the hit point — bats the victim down the corridor like the pad actually would. Falls back to Push Away From Trap Horizontal when no swing trap is present.")]
        AlongSwingArcHorizontal,
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

    /// <summary>
    /// Every live trap, so the replicated hit path can resolve which one made a clank from the hit position
    /// alone (see <see cref="PlayNearestTrapImpactSfx"/>). Traps are deterministic local maze geometry, so the
    /// same trap exists at the same place on every peer — no networked identity is needed.
    /// </summary>
    static readonly List<RagdollTrap> s_ActiveTraps = new();

    AudioSource _hitAudio;
    EntityId _lastMetallicColliderEntity;
    float _nextMetallicSoundTime;

    /// <summary>
    /// Player prefabs often have multiple colliders (e.g. CharacterController + body trigger) that can all
    /// fire <see cref="OnTriggerEnter"/> in the same physics step. Without this, one swing applies several hits.
    /// </summary>
    float _lastPlayerHitFixedTime = -1f;
    EntityId _lastPlayerHitHealthEntity;
    EntityId _lastPlayerHitTrapEntity;

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
            trapHitMetallicClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Dungeon/MetalicWack.wav");
    }
#endif

    void OnEnable()
    {
        s_ActiveTraps.Add(this);
    }

    void OnDisable()
    {
        s_ActiveTraps.Remove(this);
    }

    /// <summary>
    /// Plays the trap clank for a replicated hit: finds the live trap nearest the hit point and sounds it from
    /// the trap itself. Called on every peer from <see cref="NetworkPlayerRagdoll"/>'s hit RPC, so the impact is
    /// heard once, positionally, by everyone — instead of only by whoever's local trigger happened to fire.
    /// </summary>
    public static void PlayNearestTrapImpactSfx(Vector3 worldPosition, float maxDistance = 6f)
    {
        RagdollTrap nearest = null;
        float bestSqr = maxDistance * maxDistance;

        for (int i = s_ActiveTraps.Count - 1; i >= 0; i--)
        {
            RagdollTrap trap = s_ActiveTraps[i];
            if (trap == null)
            {
                s_ActiveTraps.RemoveAt(i); // level unload destroyed it without OnDisable running
                continue;
            }

            float sqr = (trap.transform.position - worldPosition).sqrMagnitude;
            if (sqr > bestSqr)
                continue;

            bestSqr = sqr;
            nearest = trap;
        }

        if (nearest != null)
            nearest.PlayTrapHitMetallicFromRelay();
    }

    /// <summary>
    /// The relay counterpart of <see cref="TryPlayTrapHitMetallic"/>: no victim collider to key the cooldown on,
    /// so it gates purely on time.
    /// </summary>
    void PlayTrapHitMetallicFromRelay()
    {
        if (trapHitMetallicClip == null)
            return;

        EnsureHitAudioSource();
        if (_hitAudio == null)
            return;

        float now = Time.time;
        if (now < _nextMetallicSoundTime)
            return;
        _nextMetallicSoundTime = now + trapHitSoundSameColliderCooldown;

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_hitAudio);

        _hitAudio.PlayOneShot(trapHitMetallicClip, Mathf.Max(0f, trapHitMetallicVolume));
    }

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

        // Key the cooldown on the hit object's ROOT, not the individual collider: a ragdolling victim has
        // ~11 bone colliders that can sweep the trigger within a few frames (they enable the moment ragdoll
        // starts), and a per-collider key let every one of them bypass the cooldown — machine-gun clanks.
        // One victim = one clank per window.
        EntityId id = other.transform.root.GetEntityId();
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

    bool IsDuplicatePlayerTrapHitThisPhysicsStep(PlayerHealth health)
    {
        if (health == null)
            return false;

        float t = Time.fixedTime;
        EntityId healthEntity = health.GetEntityId();
        EntityId trapEntity = GetEntityId();
        if (Mathf.Approximately(t, _lastPlayerHitFixedTime)
            && healthEntity == _lastPlayerHitHealthEntity
            && trapEntity == _lastPlayerHitTrapEntity)
        {
            return true;
        }

        _lastPlayerHitFixedTime = t;
        _lastPlayerHitHealthEntity = healthEntity;
        _lastPlayerHitTrapEntity = trapEntity;
        return false;
    }

    void TryHit(Collider other)
    {
        if (swingTrapDamageGate != null && !swingTrapDamageGate.CanDealSwingTrapDamage)
            return;

        PlayerHealth playerHealth = other.GetComponentInParent<PlayerHealth>();
        if (playerHealth != null && IsCarriedByJailor(playerHealth))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool networked = nm != null && nm.IsListening;

        // Server-authoritative hit detection. In multiplayer ONLY the server detects and applies trap hits, tested
        // against its authoritative collider poses. A client's late/interpolated blade collider must never author or
        // request a hit — that let the non-authoritative victim's own diverged collider decide the hit and was the
        // host/client hitbox inconsistency.
        if (networked && !nm.IsServer)
        {
            // A PLAYER hit's clank now rides the server's hit RPC (see NetworkPlayerRagdoll.StartRagdollClientRpc),
            // which lands on every peer — playing one here too would double it up for whoever's local trigger fired.
            // Enemy kills have no such RPC, so those still cue from local detection.
            ZombieHealth zClient = other.GetComponentInParent<ZombieHealth>();
            SkeletonHealth sClient = other.GetComponentInParent<SkeletonHealth>();
            if ((zClient != null && !zClient.IsDead) || (sClient != null && !sClient.IsDead))
                TryPlayTrapHitMetallic(other);
            return;
        }

        // From here on: server (networked) or fully local (offline / single player).
        ZombieHealth zombieHealth = other.GetComponentInParent<ZombieHealth>();
        if (zombieHealth != null && !zombieHealth.IsDead)
        {
            TryPlayTrapHitMetallic(other);
            zombieHealth.Die();
            return;
        }

        // Skeletons die to swing-pad traps too (crumble to a bone pile) — kiting enemies into traps is a
        // legitimate tactic. Pits stay zombie-only: SkeletonAI refuses to walk over drops by design.
        SkeletonHealth skeletonHealth = other.GetComponentInParent<SkeletonHealth>();
        if (skeletonHealth != null && !skeletonHealth.IsDead)
        {
            TryPlayTrapHitMetallic(other);
            skeletonHealth.Die();
            return;
        }

        PlayerRagdollController ragdoll = other.GetComponentInParent<PlayerRagdollController>();
        if (ragdoll != null && ragdoll.IsRagdolled)
            return;

        if (playerHealth != null && IsDuplicatePlayerTrapHitThisPhysicsStep(playerHealth))
            return;

        NetworkPlayerRagdoll netRagdoll = other.GetComponentInParent<NetworkPlayerRagdoll>();

        Vector3 hitCenter = other.bounds.center;
        Vector3 dir = ResolveKnockbackDirection(hitCenter);
        Vector3 force = dir * impulseMagnitude;
        if (upwardImpulse > 0f)
            force += Vector3.up * upwardImpulse;
        Vector3 hitPoint = hitCenter;

        if (networked && netRagdoll != null)
        {
            // networked here implies IsServer (clients returned above); the server relays the ragdoll to the owner.
            // The clank rides that same RPC — which loops back to the host — so there is exactly one positional
            // clank per hit on every peer and none played locally here.
            netRagdoll.RequestTrapHitFromServer(force, hitPoint, TrapDamageAmount, forceMode,
                NetworkPlayerRagdoll.TrapImpactSfxKind.TrapMetallic);
            return;
        }

        PlayerHealth health = playerHealth;
        if (health != null && health.IsDead)
            return;

        if (health == null && ragdoll == null)
            return;

        TryPlayTrapHitMetallic(other);

        // Ragdoll before damage, for the same reason as the networked path above (see
        // NetworkPlayerRagdoll.RequestTrapHitFromServer): PlayerHealth raises Died synchronously and
        // PlayerRagdollController's offline death hook starts a ZERO-force ragdoll, so damaging first made this
        // call a no-op against the already-ragdolled guard and a lethal trap dropped the body on the spot.
        bool lethal = health != null && health.CurrentHealth <= TrapDamageAmount;
        if (ragdoll != null)
            ragdoll.ActivateRagdoll(force, hitPoint, forceMode, allowAutoRecovery: !lethal);

        health?.TakeDamage(TrapDamageAmount);
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
                return PushAwayFromTrapHorizontalDirection(otherBoundsCenter);
            case KnockbackDirectionMode.AlongSwingArcHorizontal:
                Vector3 tangent = swingTrapDamageGate != null
                    ? swingTrapDamageGate.GetOutwardSwingTangent(otherBoundsCenter)
                    : Vector3.zero;
                return tangent.sqrMagnitude > 1e-6f
                    ? tangent
                    : PushAwayFromTrapHorizontalDirection(otherBoundsCenter);
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

    Vector3 PushAwayFromTrapHorizontalDirection(Vector3 otherBoundsCenter)
    {
        Vector3 dir = otherBoundsCenter - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
            dir = -transform.forward;
        dir.y = 0f;
        return dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
    }
}
