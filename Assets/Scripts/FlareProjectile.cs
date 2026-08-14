using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The burning flare round fired by <see cref="FlareGunItem"/>. Spawned and simulated by the server
/// (a registered network prefab — clients see it through its NetworkTransform); offline it simulates
/// locally. Flies a shallow ballistic arc, passes through players, and on the first world/enemy hit:
/// applies impact damage to <see cref="ZombieHealth"/>/<see cref="SkeletonHealth"/>/<see cref="SecurityGuardHealth"/>, plants a
/// <see cref="FlareBurnEffect"/> (attached to a damaged enemy for the burn DoT, or resting in the world),
/// plays an impact flash on every peer, and despawns.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class FlareProjectile : NetworkBehaviour
{
    [Header("Flight")]
    [SerializeField] float speed = 26f;
    [Tooltip("Fraction of normal gravity — flares fly a shallow arc.")]
    [SerializeField, Range(0f, 1f)] float gravityScale = 0.35f;
    [SerializeField] float castRadius = 0.07f;
    [SerializeField] float lifeSeconds = 6f;

    [Header("Damage")]
    [SerializeField] float impactDamage = 40f;
    [Tooltip("Burn effect planted at the hit (attached to damaged enemies; world-resting otherwise).")]
    [SerializeField] GameObject burnEffectPrefab;
    [Tooltip("Local, non-networked impact burst instantiated on every peer at the hit point.")]
    [SerializeField] GameObject impactEffectPrefab;

    [Header("Cosmetics")]
    [SerializeField] Light flareLight;
    [SerializeField, Range(0f, 1f)] float lightFlicker = 0.25f;

    static readonly RaycastHit[] s_castHits = new RaycastHit[16];

    Vector3 _velocity;
    Transform _shooterRoot;
    PlayerHealth _shooterHealth;
    float _dieAt;
    bool _launched;
    bool _finished;
    float _baseLightIntensity;

    bool IsAuthority
    {
        get
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                return nm.IsServer;
            return true; // offline: whoever spawned it simulates it
        }
    }

    void Awake()
    {
        if (flareLight != null)
            _baseLightIntensity = flareLight.intensity;
    }

    /// <summary>Authority-side setup. Call right after Instantiate (before Spawn online).</summary>
    public void Launch(Vector3 origin, Vector3 direction, Transform shooterRoot, PlayerHealth shooterHealth)
    {
        transform.position = origin;
        _velocity = direction.normalized * speed;
        if (_velocity.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(_velocity.normalized, Vector3.up);
        _shooterRoot = shooterRoot;
        _shooterHealth = shooterHealth;
        _dieAt = Time.time + lifeSeconds;
        _launched = true;
    }

    void Update()
    {
        // Cosmetic light flicker on every peer.
        if (flareLight != null && lightFlicker > 0f)
        {
            float n = Mathf.PerlinNoise(Time.time * 17f, transform.position.x * 3.1f);
            flareLight.intensity = _baseLightIntensity * (1f - lightFlicker * n);
        }
    }

    void FixedUpdate()
    {
        if (!_launched || _finished || !IsAuthority)
            return;

        if (Time.time >= _dieAt)
        {
            FinishWithoutImpact();
            return;
        }

        float dt = Time.fixedDeltaTime;
        _velocity += Physics.gravity * gravityScale * dt;
        Vector3 step = _velocity * dt;
        float dist = step.magnitude;
        if (dist <= Mathf.Epsilon)
            return;

        Vector3 dir = step / dist;
        int count = Physics.SphereCastNonAlloc(
            transform.position, castRadius, dir, s_castHits, dist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        RaycastHit best = default;
        bool hasHit = false;
        float bestDist = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = s_castHits[i];
            if (h.collider == null || h.distance >= bestDist)
                continue;
            if (ShouldIgnore(h.collider))
                continue;
            best = h;
            bestDist = h.distance;
            hasHit = true;
        }

        if (hasHit)
        {
            HandleImpact(best);
            return;
        }

        transform.position += step;
        if (_velocity.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(_velocity.normalized, Vector3.up);
    }

    bool ShouldIgnore(Collider collider)
    {
        // Never collide with the shooter (the muzzle sits inside their collider stack)…
        if (_shooterRoot != null && collider.transform.IsChildOf(_shooterRoot))
            return true;

        // …and pass through every player: no friendly fire, no body-blocked shots in a co-op corridor.
        if (collider.GetComponentInParent<PlayerHealth>() != null)
            return true;

        if (collider.GetComponentInParent<FlareProjectile>() != null)
            return true;

        return false;
    }

    void HandleImpact(RaycastHit hit)
    {
        _finished = true;
        Vector3 point = hit.point;
        Vector3 normal = hit.normal;

        ZombieHealth zombie = hit.collider.GetComponentInParent<ZombieHealth>();
        SkeletonHealth skeleton = hit.collider.GetComponentInParent<SkeletonHealth>();
        SecurityGuardHealth guard = hit.collider.GetComponentInParent<SecurityGuardHealth>();
        ClownHealth clown = hit.collider.GetComponentInParent<ClownHealth>();

        if (zombie != null && !zombie.IsDead)
        {
            zombie.TakeDamage(impactDamage, fromPlayerMelee: false, attacker: _shooterRoot, attackerHealth: _shooterHealth);
            FlareBurnEffect.SpawnAttached(burnEffectPrefab, zombie.transform, zombie, null, null, null, _shooterRoot, _shooterHealth);
        }
        else if (skeleton != null && !skeleton.IsDead)
        {
            skeleton.TakeDamage(impactDamage, fromPlayerMelee: false, attacker: _shooterRoot, attackerHealth: _shooterHealth);
            FlareBurnEffect.SpawnAttached(burnEffectPrefab, skeleton.transform, null, skeleton, null, null, _shooterRoot, _shooterHealth);
        }
        else if (guard != null && !guard.IsDead)
        {
            guard.TakeDamage(impactDamage, fromPlayerMelee: false, attacker: _shooterRoot, attackerHealth: _shooterHealth);
            FlareBurnEffect.SpawnAttached(burnEffectPrefab, guard.transform, null, null, guard, null, _shooterRoot, _shooterHealth);
        }
        else if (clown != null && !clown.IsDead)
        {
            clown.TakeDamage(impactDamage, fromPlayerMelee: false, attacker: _shooterRoot, attackerHealth: _shooterHealth);
            FlareBurnEffect.SpawnAttached(burnEffectPrefab, clown.transform, null, null, null, clown, _shooterRoot, _shooterHealth);
        }
        else
        {
            // World hit (or an unkillable enemy): the flare keeps burning where it landed for a few seconds.
            FlareBurnEffect.SpawnWorld(burnEffectPrefab, point + normal * 0.05f);
        }

        if (IsSpawned)
            ImpactFxClientRpc(point, normal);
        else
            SpawnImpactFxLocal(point, normal);

        FinishAndDespawn();
    }

    void FinishWithoutImpact()
    {
        _finished = true;
        FlareBurnEffect.SpawnWorld(burnEffectPrefab, transform.position);
        FinishAndDespawn();
    }

    void FinishAndDespawn()
    {
        if (IsSpawned)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                NetworkObject.Despawn(true);
            return;
        }

        Destroy(gameObject);
    }

    [ClientRpc]
    void ImpactFxClientRpc(Vector3 point, Vector3 normal)
    {
        SpawnImpactFxLocal(point, normal);
    }

    void SpawnImpactFxLocal(Vector3 point, Vector3 normal)
    {
        if (impactEffectPrefab == null)
            return;

        Quaternion rot = normal.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(normal)
            : Quaternion.identity;
        GameObject fx = Instantiate(impactEffectPrefab, point, rot);
        Destroy(fx, 3f);
    }
}
