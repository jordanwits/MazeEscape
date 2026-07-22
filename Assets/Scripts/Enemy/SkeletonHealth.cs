using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Health/damage for the dungeon Skeleton enemy. Mirrors <see cref="ZombieHealth"/>, but the Skeleton has no
/// death animation: when it dies it breaks apart into a physics bone pile (handled cosmetically per-client by
/// <see cref="NetworkSkeletonAvatar"/> when the replicated dead-state flips). All damage is server-authoritative
/// (the guard below no-ops on non-server peers), matching how the player melee and traps deal damage.
/// </summary>
[DisallowMultipleComponent]
public class SkeletonHealth : MonoBehaviour
{
    [Header("Health")]
    [SerializeField] float maxHealth = 100f;

    [Header("References")]
    [SerializeField] SkeletonAI skeletonAI;

    NetworkObject _networkObject;

    public float MaxHealth => maxHealth;
    public float CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }

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
        if (skeletonAI == null)
            skeletonAI = GetComponent<SkeletonAI>();
    }

    /// <summary>
    /// Server-authoritative damage entry point. Signature matches <see cref="ZombieHealth.TakeDamage"/> so the
    /// player melee dispatch in <see cref="PlayerController"/> can damage skeletons the same way it damages zombies.
    /// </summary>
    public bool TakeDamage(float amount, bool fromPlayerMelee = false, Transform attacker = null, PlayerHealth attackerHealth = null)
    {
        if (_networkObject != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer)
            return false;

        if (IsDead || amount <= 0f)
            return false;

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        if (CurrentHealth <= 0f)
        {
            Die();
            return true;
        }

        // The AI's poise system decides the reaction (flinch, stagger, retaliation) — damage always lands.
        if (skeletonAI != null)
            skeletonAI.OnDamageTaken(fromPlayerMelee, attacker, attackerHealth);

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

        if (skeletonAI != null)
            skeletonAI.HandleDeath();
    }
}
