using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lingering flare fire planted by a <see cref="FlareProjectile"/> hit. Attached to a damaged enemy it
/// follows their chest and ticks burn damage on the server for <see cref="burnDurationSeconds"/>; resting
/// in the world it is purely a light/particle source. A registered network prefab — the server spawns it
/// and clients replicate position through NetworkTransform, so every peer sees the same flames. Offline it
/// runs exactly the same logic locally.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class FlareBurnEffect : NetworkBehaviour
{
    [Header("Burn")]
    [Tooltip("Seconds of ticking fire damage after the hit.")]
    [SerializeField] float burnDurationSeconds = 3f;
    [SerializeField] float tickIntervalSeconds = 0.5f;
    [Tooltip("Damage per tick while attached to an enemy (3s / 0.5s * 5 = 30 total).")]
    [SerializeField] float damagePerTick = 5f;
    [Tooltip("How long the visual lingers (and a world-resting flare burns) before despawning.")]
    [SerializeField] float worldLifeSeconds = 4.5f;
    [Tooltip("Chest-height offset above the followed enemy root.")]
    [SerializeField] Vector3 followOffset = new Vector3(0f, 1.15f, 0f);

    [Header("Cosmetics")]
    [SerializeField] ParticleSystem flames;
    [SerializeField] Light burnLight;
    [SerializeField, Range(0f, 1f)] float lightFlicker = 0.45f;

    Transform _followRoot;
    ZombieHealth _zombie;
    SkeletonHealth _skeleton;
    SecurityGuardHealth _guard;
    ClownHealth _clown;
    BomberHealth _bomber;
    Transform _attacker;
    PlayerHealth _attackerHealth;
    float _baseLightIntensity;
    bool _authorityInitialized;

    static bool NetworkActive => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    /// <summary>Spawns a burn attached to a damaged enemy. Call on the server (or offline). Returns null without a prefab.</summary>
    public static FlareBurnEffect SpawnAttached(
        GameObject prefab, Transform enemyRoot, ZombieHealth zombie, SkeletonHealth skeleton,
        SecurityGuardHealth guard, ClownHealth clown, Transform attacker, PlayerHealth attackerHealth,
        BomberHealth bomber = null)
    {
        FlareBurnEffect fx = SpawnCommon(prefab, enemyRoot != null ? enemyRoot.position + Vector3.up * 1.15f : Vector3.zero);
        if (fx == null)
            return null;

        fx._followRoot = enemyRoot;
        fx._zombie = zombie;
        fx._skeleton = skeleton;
        fx._guard = guard;
        fx._clown = clown;
        fx._bomber = bomber;
        fx._attacker = attacker;
        fx._attackerHealth = attackerHealth;
        fx.BeginAuthorityLife();
        return fx;
    }

    /// <summary>Spawns a burn resting in the world (missed shots, walls, unkillable enemies).</summary>
    public static FlareBurnEffect SpawnWorld(GameObject prefab, Vector3 position)
    {
        FlareBurnEffect fx = SpawnCommon(prefab, position);
        if (fx == null)
            return null;

        fx.BeginAuthorityLife();
        return fx;
    }

    static FlareBurnEffect SpawnCommon(GameObject prefab, Vector3 position)
    {
        if (prefab == null)
            return null;

        GameObject go = Instantiate(prefab, position, Quaternion.identity);
        FlareBurnEffect fx = go.GetComponent<FlareBurnEffect>();
        if (fx == null)
        {
            Destroy(go);
            return null;
        }

        if (NetworkActive && NetworkManager.Singleton.IsServer && fx.TryGetComponent(out NetworkObject netObj))
            netObj.Spawn();

        return fx;
    }

    void BeginAuthorityLife()
    {
        _authorityInitialized = true;
        StartCoroutine(AuthorityLifeRoutine());
    }

    void Awake()
    {
        if (burnLight != null)
            _baseLightIntensity = burnLight.intensity;
    }

    void Update()
    {
        if (burnLight != null && lightFlicker > 0f)
        {
            float n = Mathf.PerlinNoise(Time.time * 21f, transform.position.z * 2.7f);
            burnLight.intensity = _baseLightIntensity * (1f - lightFlicker * n);
        }
    }

    void LateUpdate()
    {
        if (!_authorityInitialized || _followRoot == null)
            return;

        transform.position = _followRoot.position + followOffset;
    }

    IEnumerator AuthorityLifeRoutine()
    {
        float burnEnd = Time.time + burnDurationSeconds;
        float nextTick = Time.time + tickIntervalSeconds;

        while (Time.time < burnEnd)
        {
            yield return null;

            if (_followRoot == null)
                break;

            if (Time.time >= nextTick)
            {
                nextTick += tickIntervalSeconds;
                ApplyBurnTick();
            }
        }

        // Let the flames die down instead of vanishing: stop emission, keep light fading briefly.
        float linger = Mathf.Max(0f, worldLifeSeconds - burnDurationSeconds);
        if (flames != null)
            flames.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        float fadeStart = Time.time;
        while (Time.time - fadeStart < linger)
        {
            if (burnLight != null)
                _baseLightIntensity = Mathf.Max(0f, _baseLightIntensity - Time.deltaTime * 2.2f);
            yield return null;
        }

        if (IsSpawned)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                NetworkObject.Despawn(true);
            yield break;
        }

        Destroy(gameObject);
    }

    void ApplyBurnTick()
    {
        if (_zombie != null && !_zombie.IsDead)
        {
            _zombie.TakeDamage(damagePerTick, fromPlayerMelee: false, attacker: _attacker, attackerHealth: _attackerHealth);
            return;
        }

        if (_skeleton != null && !_skeleton.IsDead)
        {
            _skeleton.TakeDamage(damagePerTick, fromPlayerMelee: false, attacker: _attacker, attackerHealth: _attackerHealth);
            return;
        }

        if (_guard != null && !_guard.IsDead)
        {
            _guard.TakeDamage(damagePerTick, fromPlayerMelee: false, attacker: _attacker, attackerHealth: _attackerHealth);
            return;
        }

        if (_clown != null && !_clown.IsDead)
        {
            _clown.TakeDamage(damagePerTick, fromPlayerMelee: false, attacker: _attacker, attackerHealth: _attackerHealth);
            return;
        }

        // A burning Bomber is on a second timer: the burn can finish him, and finishing him sets off his
        // dynamite wherever he has run to by then.
        if (_bomber != null && !_bomber.IsDead)
            _bomber.TakeDamage(damagePerTick, fromPlayerMelee: false, attacker: _attacker, attackerHealth: _attackerHealth);
    }
}
