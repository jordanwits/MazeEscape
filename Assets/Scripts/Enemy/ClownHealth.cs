using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Health/damage for the carnival Clown. Mirrors <see cref="SecurityGuardHealth"/> (itself modelled on
/// <see cref="ZombieHealth"/>/<see cref="SkeletonHealth"/>): damage is server-authoritative — the component
/// no-ops on non-server peers — and <see cref="ClownAI"/> decides only the <i>reaction</i>, never whether a
/// hit counts.
///
/// The Clown was unkillable: players could only ever run from him. He is still the heaviest thing in
/// Level02 — this pool is bigger than the Severance guard's — but he now goes down if a group commits to
/// it, which is what makes the shop's weapons worth buying.
///
/// Killing him does not clear the level: <see cref="Die"/> asks
/// <see cref="ProceduralMazeCoordinator.TryServerScheduleMazeHunterRespawn{T}"/> for a replacement, placed
/// well away from the players and out of their line of sight so the arrival is never witnessed.
///
/// Death is a full-body animation (no ragdoll — the Clown rig has none), driven by
/// <see cref="ClownAI.HandleDeath"/> and replicated to clients through <see cref="NetworkClownAvatar"/>'s
/// dead-state NetworkVariable.
/// </summary>
[DisallowMultipleComponent]
public class ClownHealth : MonoBehaviour
{
    [Header("Health")]
    [Tooltip("Four times a zombie's 100 and a third again over the Severance guard's 300 — he is the " +
             "carnival's apex threat and he grows as he closes. Absolute damage (flare impact 40 + 30 of " +
             "burn) therefore costs about six flares.")]
    [SerializeField] float maxHealth = 400f;
    [Tooltip("Player melee arrives as a FRACTION of the target's max health (PlayerController scales it per " +
             "species so one punch is always the same share of any enemy) — which means raising Max Health " +
             "alone cannot make him tankier: every enemy would still die in the same four punches. THIS is " +
             "the knob that does. 0.4 takes each melee hit to a tenth of him instead of a quarter: 10 punches " +
             "or 5 sword swings, against the guard's 8 and 4 and a zombie's 4 and 2. Absolute damage is " +
             "deliberately unaffected — it already scales off the bigger pool.")]
    [SerializeField, Range(0.05f, 1f)] float meleeDamageScale = 0.4f;

    [Header("Respawn")]
    [Tooltip("Kill him and the carnival sends another one out — Level02 is never permanently cleared of its " +
             "hunter. The replacement is placed by ProceduralMazeCoordinator at a cell that is both far from " +
             "every player and out of their line of sight, so nobody watches him pop in. The type guard means " +
             "this only ever spawns a Clown: on a level whose Enemy 2 slot holds something else it no-ops.")]
    [SerializeField] bool respawnOnDeath = true;
    [Tooltip("Seconds between his death and the replacement appearing. Long enough that the kill reads as won " +
             "before the next one starts hunting — and long enough that the party has usually moved on from " +
             "the body. The wait runs on the coordinator, not on the corpse, so it survives the despawn below.")]
    [SerializeField] float respawnDelaySeconds = 45f;
    [Tooltip("Hard floor on how far the replacement may spawn from the nearest player. Cells closer than this " +
             "are only used when no out-of-sight cell in the maze is farther away.")]
    [SerializeField] float respawnMinPlayerDistance = 25f;
    [Tooltip("Preferred distance, taken whenever the maze offers it. Past WorldRenderCuller's 48m cut the " +
             "spawn is not even drawn, so this sits just inside that — line of sight is checked either way.")]
    [SerializeField] float respawnPreferredPlayerDistance = 45f;

    [Header("Death")]
    [SerializeField] ClownAI clownAI;
    [Tooltip("Seconds after death before the corpse stops blocking. Covers the fall so the body is on the " +
             "floor before players can walk through it.")]
    [SerializeField] float disableColliderDelay = 3f;
    [Tooltip("Seconds the corpse lingers before it despawns.")]
    [SerializeField] float destroyDelay = 60f;

    NetworkObject _networkObject;
    Collider[] _clownColliders;

    public float MaxHealth => maxHealth;
    public float CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }

    /// <summary>Read by <see cref="NetworkClownAvatar"/> so observers drop their collision proxy on the same beat.</summary>
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
        if (clownAI == null)
            clownAI = GetComponent<ClownAI>();

        if (_clownColliders == null || _clownColliders.Length == 0)
            _clownColliders = GetComponentsInChildren<Collider>(true);
    }

    /// <summary>
    /// Server-authoritative damage entry point. Signature matches <see cref="ZombieHealth.TakeDamage"/> so the
    /// player-melee and flare dispatches reach the Clown exactly as they reach every other enemy.
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

        // The AI decides the reaction (he turns on whoever hit him) — the damage itself always lands.
        if (clownAI != null)
            clownAI.OnDamageTaken(fromPlayerMelee, attacker, attackerHealth);

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

        if (clownAI != null)
            clownAI.HandleDeath();

        // Queued on the coordinator (a DontDestroyOnLoad component on the NetworkManager object), NOT here:
        // this corpse despawns itself a minute from now, which would cancel a coroutine started on it. The
        // coordinator also drops the queued respawn if the level changes before it fires.
        if (respawnOnDeath)
        {
            ProceduralMazeCoordinator.TryServerScheduleMazeHunterRespawn<ClownAI>(
                respawnDelaySeconds, respawnMinPlayerDistance, respawnPreferredPlayerDistance);
        }

        StartCoroutine(DeathCleanupRoutine());
    }

    IEnumerator DeathCleanupRoutine()
    {
        if (disableColliderDelay > 0f)
            yield return new WaitForSeconds(disableColliderDelay);

        // His CharacterController IS his blocking collider (see EnemyClientCollisionProxy), so this is what
        // stops the corpse body-blocking a corridor. Clients drop their mirrored proxy off the replicated
        // dead flag instead — NetworkClownAvatar.ApplyDeadState.
        for (int i = 0; i < _clownColliders.Length; i++)
        {
            Collider clownCollider = _clownColliders[i];
            if (clownCollider != null)
                clownCollider.enabled = false;
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
