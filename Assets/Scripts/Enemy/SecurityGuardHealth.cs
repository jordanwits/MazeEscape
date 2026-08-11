using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Health/damage for the Severance security guard. Mirrors <see cref="ZombieHealth"/> and
/// <see cref="SkeletonHealth"/>: damage is server-authoritative (the guard below no-ops on non-server
/// peers) and the AI decides only the <i>reaction</i>, never whether a hit counts.
///
/// The guard used to be unkillable — the poise meter in <see cref="SecurityGuardAI"/> was the whole
/// player-facing combat system. Poise survives unchanged as the stagger mechanic; this pool sits
/// underneath it, so punching him now both chips poise and kills him eventually.
///
/// Death itself is a full-body animation (no ragdoll, like the zombie), driven by
/// <see cref="SecurityGuardAI.HandleDeath"/> and replicated to clients through
/// <see cref="NetworkSecurityGuardAvatar"/>'s dead-state NetworkVariable.
/// </summary>
[DisallowMultipleComponent]
public class SecurityGuardHealth : MonoBehaviour
{
    [Header("Health")]
    [Tooltip("Three times a zombie's 100 — he is the level's hunter, not a shambler. Absolute damage " +
             "(flare impact 40 + 30 of burn) therefore costs about five flares.")]
    [SerializeField] float maxHealth = 300f;
    [Tooltip("Player melee arrives as a FRACTION of the target's max health (PlayerController scales it per " +
             "species so one punch is always the same share of any enemy) — which means raising Max Health " +
             "alone cannot make him tankier: every enemy would still die in the same four punches. THIS is the " +
             "knob that does. 0.5 halves each melee hit, so a fist takes 1/8 of him instead of 1/4: 8 punches " +
             "or 4 sword swings, against a zombie's 4 and 2. Absolute damage is deliberately unaffected — it " +
             "already scales off the bigger pool.")]
    [SerializeField, Range(0.05f, 1f)] float meleeDamageScale = 0.5f;

    [Header("Death")]
    [SerializeField] SecurityGuardAI guardAI;
    [Tooltip("Seconds after death before the corpse stops blocking. Covers the fall so the body is on the " +
             "floor before players can walk through it.")]
    [SerializeField] float disableColliderDelay = 3f;
    [Tooltip("Seconds the corpse lingers before it despawns.")]
    [SerializeField] float destroyDelay = 60f;

    NetworkObject _networkObject;
    Collider[] _guardColliders;

    public float MaxHealth => maxHealth;
    public float CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }

    /// <summary>Read by <see cref="NetworkSecurityGuardAvatar"/> so observers drop their collision proxy on the same beat.</summary>
    public float DisableColliderDelay => disableColliderDelay;

    void Reset()
    {
        CacheReferences();
    }

    void Awake()
    {
        CacheReferences();
        _networkObject = GetComponent<NetworkObject>();
        CurrentHealth = Mathf.Max(1f, maxHealth);
    }

    void CacheReferences()
    {
        if (guardAI == null)
            guardAI = GetComponent<SecurityGuardAI>();

        if (_guardColliders == null || _guardColliders.Length == 0)
            _guardColliders = GetComponentsInChildren<Collider>(true);
    }

    /// <summary>
    /// Server-authoritative damage entry point. Signature matches <see cref="ZombieHealth.TakeDamage"/> so the
    /// player-melee dispatch in <see cref="PlayerController"/> reaches guards exactly as it reaches zombies.
    /// </summary>
    public bool TakeDamage(float amount, bool fromPlayerMelee = false, Transform attacker = null, PlayerHealth attackerHealth = null)
    {
        if (_networkObject != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer)
            return false;

        if (IsDead || amount <= 0f)
            return false;

        // Only the fraction-of-max-health melee path is scaled down — see the meleeDamageScale tooltip.
        if (fromPlayerMelee)
            amount *= meleeDamageScale;

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        if (CurrentHealth <= 0f)
        {
            Die();
            return true;
        }

        // The AI's poise system decides the reaction (hyper-armor, stagger, counter-kick) — damage always lands.
        if (guardAI != null)
            guardAI.OnDamageTaken(fromPlayerMelee, attacker, attackerHealth);

        return true;
    }

    public void Die()
    {
        if (_networkObject != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer)
            return;

        if (IsDead)
            return;

        IsDead = true;
        CurrentHealth = 0f;

        if (guardAI != null)
            guardAI.HandleDeath();

        StartCoroutine(DeathCleanupRoutine());
    }

    IEnumerator DeathCleanupRoutine()
    {
        if (disableColliderDelay > 0f)
            yield return new WaitForSeconds(disableColliderDelay);

        // His CharacterController IS his only collider (see EnemyClientCollisionProxy), so this is what
        // stops the corpse body-blocking a corridor. Clients drop their mirrored proxy off the replicated
        // dead flag instead — NetworkSecurityGuardAvatar.ApplyDeadState.
        for (int i = 0; i < _guardColliders.Length; i++)
        {
            Collider guardCollider = _guardColliders[i];
            if (guardCollider != null)
                guardCollider.enabled = false;
        }

        if (destroyDelay > 0f)
            yield return new WaitForSeconds(destroyDelay);

        if (_networkObject != null && _networkObject.IsSpawned)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                _networkObject.Despawn(true);
            yield break;
        }

        Destroy(gameObject);
    }
}
