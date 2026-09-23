using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Health/damage for the Mutant, Level03's final boss. Same contract as <see cref="SecurityGuardHealth"/> —
/// server-authoritative (no-ops on networked clients), and <see cref="MutantAI"/> decides only the reaction —
/// with two boss-specific twists:
///   • the pool scales with the number of players when he wakes, and
///   • player melee is scaled down by that same factor, because PlayerController deals melee as a FRACTION of
///     max health: without the division, a bigger pool would take exactly as many punches as a small one.
/// The corpse is not despawned — he is the last thing between the players and the exit.
/// </summary>
[DisallowMultipleComponent]
public class MutantHealth : MonoBehaviour
{
    [Header("Health")]
    [Tooltip("Pool for a single player. Absolute damage (flare impact 40 + 30 of burn) rides this directly: " +
             "about 13 flares solo.")]
    [SerializeField, Min(1f)] float baseMaxHealth = 900f;
    [Tooltip("Each player beyond the first adds this fraction of the base pool when he wakes (0.5 → 4 players " +
             "face 2.5x). Melee is divided by the same factor, so every weapon scales with the party size.")]
    [SerializeField, Min(0f)] float extraHealthPerPlayer = 0.5f;
    [Tooltip("Player melee arrives as a fraction of Max Health (fist 0.25, sword 0.6). This scale makes the " +
             "boss tanky: at 0.1 a fist takes 1/40 of him and a sword 1/17, solo.")]
    [SerializeField, Range(0.01f, 1f)] float meleeDamageScale = 0.1f;

    [Header("Death")]
    [SerializeField] MutantAI mutantAI;
    [Tooltip("Seconds after death before the corpse stops blocking — covers the fall.")]
    [SerializeField, Min(0f)] float disableColliderDelay = 2.8f;

    NetworkObject _networkObject;
    Collider[] _colliders;
    float _playerMultiplier = 1f;
    bool _scaledForParty;

    public float MaxHealth { get; private set; }
    public float CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }
    public float HealthFraction => MaxHealth > 0f ? CurrentHealth / MaxHealth : 0f;
    public float DisableColliderDelay => disableColliderDelay;

    bool IsNetworkClient => _networkObject != null
        && NetworkManager.Singleton != null
        && NetworkManager.Singleton.IsListening
        && !NetworkManager.Singleton.IsServer;

    void Reset()
    {
        mutantAI = GetComponent<MutantAI>();
    }

    void Awake()
    {
        if (mutantAI == null)
            mutantAI = GetComponent<MutantAI>();
        _networkObject = GetComponent<NetworkObject>();
        _colliders = GetComponentsInChildren<Collider>(true);
        MaxHealth = Mathf.Max(1f, baseMaxHealth);
        CurrentHealth = MaxHealth;
    }

    /// <summary>
    /// Server: size the pool for the party the moment he wakes. Only ever grows an untouched pool — a hit taken
    /// while he was still dormant keeps its share.
    /// </summary>
    public void ServerScaleForParty(int livingPlayers)
    {
        if (IsNetworkClient || IsDead || _scaledForParty)
            return;

        _scaledForParty = true;
        _playerMultiplier = 1f + Mathf.Max(0f, extraHealthPerPlayer) * Mathf.Max(0, livingPlayers - 1);
        float fraction = HealthFraction;
        MaxHealth = Mathf.Max(1f, baseMaxHealth) * _playerMultiplier;
        CurrentHealth = MaxHealth * fraction;
    }

    /// <summary>Server-authoritative damage entry point; same signature as the other enemy healths.</summary>
    public bool TakeDamage(float amount, bool fromPlayerMelee = false, Transform attacker = null, PlayerHealth attackerHealth = null)
    {
        if (IsNetworkClient || IsDead || amount <= 0f)
            return false;

        if (fromPlayerMelee)
            amount *= meleeDamageScale / Mathf.Max(1f, _playerMultiplier);

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        if (CurrentHealth <= 0f)
        {
            Die();
            return true;
        }

        if (mutantAI != null)
            mutantAI.OnDamageTaken(fromPlayerMelee, attacker, attackerHealth);

        return true;
    }

    public void Die()
    {
        if (IsNetworkClient || IsDead)
            return;

        IsDead = true;
        CurrentHealth = 0f;

        if (mutantAI != null)
            mutantAI.HandleDeath();

        StartCoroutine(DisableCollidersRoutine());
    }

    IEnumerator DisableCollidersRoutine()
    {
        if (disableColliderDelay > 0f)
            yield return new WaitForSeconds(disableColliderDelay);

        // His CharacterController is his only collider; clients drop their mirrored proxy off the replicated
        // dead flag on the same delay (NetworkMutantAvatar).
        for (int i = 0; i < _colliders.Length; i++)
            if (_colliders[i] != null)
                _colliders[i].enabled = false;
    }
}
