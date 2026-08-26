using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Health/damage for the carnival Bomber. Same shape as <see cref="ClownHealth"/> — server-authoritative,
/// no-ops on non-server peers, and <see cref="BomberAI"/> decides only the <i>reaction</i>, never whether a
/// hit counts — but with one deliberate difference: there is no corpse.
///
/// He is carrying two live sticks of dynamite, so killing him <b>sets them off</b>. That is the whole point
/// of making him killable: shoot him across a corridor and the blast goes off harmlessly at range; punch him
/// and you are standing inside it. It turns the flare gun into the right answer and melee into a bad idea,
/// which is exactly the decision a suicide bomber should be asking of the player.
///
/// He is correspondingly flimsy — a fifth of a zombie's pool — because the counterplay has to be reachable
/// in the second or two before he closes.
/// </summary>
[DisallowMultipleComponent]
public class BomberHealth : MonoBehaviour
{
    [Header("Health")]
    [Tooltip("Deliberately tiny next to the other hunters (zombie 100, guard 300, Clown 400). He is a hollow " +
             "wooden toy whose whole threat is the payload, and the player needs to be able to drop him in " +
             "the window before he arrives. One flare — 40 impact plus 30 of burn — is lethal on its own.")]
    [SerializeField] float maxHealth = 60f;
    [Tooltip("Player melee arrives as a FRACTION of the target's max health (PlayerController scales it per " +
             "species), so raising Max Health alone cannot change how many punches he takes — THIS is that " +
             "knob. Left at 1 he dies in the baseline 4 punches or 2 sword swings. Absolute damage (flares) " +
             "is unaffected and already scales off the smaller pool.")]
    [SerializeField, Range(0.05f, 1f)] float meleeDamageScale = 1f;

    [Header("Death")]
    [SerializeField] BomberAI bomberAI;
    [Tooltip("ON: killing him cooks off the dynamite — a full blast at wherever he was standing, which is " +
             "what makes shooting him from range the correct play. OFF: he simply despawns. Note there is no " +
             "death animation on this rig, so the OFF path has him vanish rather than crumple.")]
    [SerializeField] bool detonateOnDeath = true;

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
        if (bomberAI == null)
            bomberAI = GetComponent<BomberAI>();
    }

    bool IsServerOrOffline =>
        _networkObject == null
        || NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    /// <summary>
    /// Server-authoritative damage entry point. Signature matches <see cref="ZombieHealth.TakeDamage"/> so the
    /// player-melee and flare dispatches reach the Bomber exactly as they reach every other enemy.
    /// </summary>
    public bool TakeDamage(float amount, bool fromPlayerMelee = false, Transform attacker = null, PlayerHealth attackerHealth = null)
    {
        if (!IsServerOrOffline)
            return false;

        if (IsDead || amount <= 0f)
            return false;

        // Only the fraction-of-max-health melee path is scaled — see the meleeDamageScale tooltip.
        if (fromPlayerMelee)
            amount *= meleeDamageScale;

        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        if (CurrentHealth <= 0f)
        {
            Die();
            return true;
        }

        // Surviving a hit wakes him up and points him at whoever fired; the damage itself always lands.
        if (bomberAI != null)
            bomberAI.OnDamageTaken(fromPlayerMelee, attacker, attackerHealth);

        return true;
    }

    public void Die()
    {
        if (!IsServerOrOffline || IsDead)
            return;

        IsDead = true;
        CurrentHealth = 0f;

        // BomberAI.HandleDeath detonates and despawns the body itself, so unlike the other enemies there is
        // no corpse to un-collide, linger and clean up.
        if (bomberAI != null)
        {
            bomberAI.HandleDeath(detonateOnDeath);
            return;
        }

        // No AI attached (misconfigured prefab): still remove the body rather than leaving a live husk.
        if (_networkObject != null && _networkObject.IsSpawned)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                _networkObject.Despawn(true);
            return;
        }
        Destroy(gameObject);
    }
}
