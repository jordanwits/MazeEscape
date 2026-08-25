using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The live grenade thrown from a <see cref="FlashbangItem"/>. Spawned and simulated by the server
/// (a registered network prefab — clients see it through its NetworkTransform); offline it simulates
/// locally. Flies a ballistic arc, bounces off the world, and detonates a fixed number of seconds after it
/// left the hand regardless of what it hit on the way.
///
/// Detonation is a pure blind, never damage: every player and every <see cref="IBlindableEnemy"/> inside
/// the radius with a clear line to the burst is blinded — players get a full-white screen that fades back
/// (owner-side, via <see cref="PlayerController.ApplyFlashbangBlind"/>), enemies get an
/// <see cref="EnemyBlindEffect"/> that makes them stumble in circles and stops them attacking. The flash and
/// bang themselves are local, non-networked FX played on every peer (<see cref="FlashbangFlashFx"/>).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class FlashbangGrenade : NetworkBehaviour
{
    [Header("Fuse")]
    [Tooltip("Seconds from leaving the thrower's hand to detonation.")]
    [SerializeField, Min(0.1f)] float fuseSeconds = 3f;

    [Header("Flight")]
    [SerializeField] float castRadius = 0.06f;
    [Tooltip("How much speed survives a bounce (0 = dead stop, 1 = perfectly elastic).")]
    [SerializeField, Range(0f, 1f)] float bounciness = 0.38f;
    [Tooltip("How much speed ALONG the surface survives a bounce — low values stop it skidding down corridors.")]
    [SerializeField, Range(0f, 1f)] float surfaceFriction = 0.55f;
    [Tooltip("Below this speed the grenade stops bouncing and just sits there ticking.")]
    [SerializeField, Min(0f)] float restSpeed = 0.6f;
    [SerializeField] float spinDegreesPerSecond = 520f;

    [Header("Blast")]
    [Tooltip("Anything further than this from the burst is unaffected.")]
    [SerializeField, Min(0.5f)] float blindRadius = 12f;
    [Tooltip("Seconds a caught player or enemy stays blinded.")]
    [SerializeField, Min(0.1f)] float blindSeconds = 5f;
    [Tooltip("A wall between the burst and the victim blocks the flash entirely.")]
    [SerializeField] bool requireLineOfSight = true;
    [Tooltip("Facing away from the burst still blinds, but only this fraction as strongly (players only — an enemy is either blinded or not).")]
    [SerializeField, Range(0.1f, 1f)] float lookingAwayScale = 0.55f;
    [Tooltip("Inside this fraction of the radius the flash is at full strength before it starts falling off.")]
    [SerializeField, Range(0f, 1f)] float fullStrengthRadiusFraction = 0.35f;
    [Tooltip("The bang, played positionally on every peer at the burst.")]
    [SerializeField] AudioClip bangClip;
    [SerializeField, Range(0f, 1f)] float bangVolume = 1f;

    static readonly RaycastHit[] s_castHits = new RaycastHit[16];
    static readonly Collider[] s_blastOverlap = new Collider[192];
    static readonly List<IBlindableEnemy> s_blastEnemies = new List<IBlindableEnemy>(16);

    Vector3 _velocity;
    Transform _throwerRoot;
    float _detonateAt;
    bool _launched;
    bool _finished;
    bool _atRest;
    Vector3 _spinAxis = Vector3.right;

    /// <summary>Seconds a victim stays blinded — exposed so tuning lives on this prefab alone.</summary>
    public float BlindSeconds => blindSeconds;

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

    /// <summary>Authority-side setup. Call right after Instantiate (before Spawn online).</summary>
    public void Launch(Vector3 origin, Vector3 velocity, Transform throwerRoot)
    {
        transform.position = origin;
        _velocity = velocity;
        _throwerRoot = throwerRoot;
        _detonateAt = Time.time + fuseSeconds;
        _launched = true;
        _atRest = false;

        // Tumble about an axis perpendicular to the throw so it reads as an over-the-shoulder toss.
        Vector3 forward = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : Vector3.forward;
        _spinAxis = Vector3.Cross(forward, Vector3.up);
        if (_spinAxis.sqrMagnitude < 0.0001f)
            _spinAxis = Vector3.right;
        _spinAxis.Normalize();
    }

    void Update()
    {
        // A thrown flashbang is deliberately dark — the only light it ever emits is the burst itself, so a
        // grenade in the air never gives away where it is about to land. Tumble only.
        if (_launched && !_finished && !_atRest)
            transform.rotation = Quaternion.AngleAxis(spinDegreesPerSecond * Time.deltaTime, _spinAxis) * transform.rotation;
    }

    void FixedUpdate()
    {
        if (!_launched || _finished || !IsAuthority)
            return;

        // The fuse is the ONLY thing that detonates it — three seconds after the throw, wherever it ended up.
        if (Time.time >= _detonateAt)
        {
            Detonate();
            return;
        }

        if (_atRest)
            return;

        float dt = Time.fixedDeltaTime;
        _velocity += Physics.gravity * dt;
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

        if (!hasHit)
        {
            transform.position += step;
            return;
        }

        // Land just short of the surface, then reflect what is left of the velocity.
        transform.position += dir * Mathf.Max(0f, best.distance - 0.01f);
        Bounce(best.normal);
    }

    void Bounce(Vector3 normal)
    {
        Vector3 into = Vector3.Project(_velocity, normal);
        Vector3 along = _velocity - into;
        _velocity = along * surfaceFriction - into * bounciness;

        if (_velocity.magnitude < restSpeed)
        {
            _velocity = Vector3.zero;
            _atRest = true;
        }
    }

    bool ShouldIgnore(Collider collider)
    {
        // Never collide with the thrower (it leaves the hand from inside their collider stack)…
        if (_throwerRoot != null && collider.transform.IsChildOf(_throwerRoot))
            return true;

        // …and roll past every player and enemy body rather than pinballing off them.
        if (collider.GetComponentInParent<PlayerHealth>() != null)
            return true;
        if (collider.GetComponentInParent<IBlindableEnemy>() != null)
            return true;
        if (collider.GetComponentInParent<FlashbangGrenade>() != null)
            return true;

        return false;
    }

    void Detonate()
    {
        _finished = true;
        Vector3 point = transform.position;

        BlindPlayersInBlast(point);
        BlindEnemiesInBlast(point);

        if (IsSpawned)
        {
            DetonateFxClientRpc(point);
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                NetworkObject.Despawn(true);
            return;
        }

        FlashbangFlashFx.Play(point, bangClip, bangVolume);
        Destroy(gameObject);
    }

    void BlindPlayersInBlast(Vector3 point)
    {
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || player.IsDead)
                continue;

            Vector3 eyes = EyePoint(player.transform);
            if (!TryGetBlastStrength(point, eyes, out float strength))
                continue;

            // Looking straight at the burst is a full whiteout; with your back to it, much less.
            Vector3 toBurst = point - eyes;
            if (toBurst.sqrMagnitude > 0.0001f)
            {
                float facing = Vector3.Dot(player.transform.forward, toBurst.normalized);
                strength *= Mathf.Lerp(lookingAwayScale, 1f, Mathf.InverseLerp(-1f, 1f, facing));
            }

            if (strength <= 0.02f)
                continue;

            SendPlayerBlind(player, strength);
        }
    }

    void SendPlayerBlind(PlayerHealth player, float strength)
    {
        // The whiteout is a screen effect, so it has to run on the machine that owns that player's view.
        if (!IsSpawned)
        {
            player.GetComponent<PlayerController>()?.ApplyFlashbangBlind(blindSeconds, strength);
            return;
        }

        NetworkObject playerObject = player.GetComponent<NetworkObject>();
        if (playerObject == null)
            return;

        BlindPlayerClientRpc(
            playerObject.NetworkObjectId,
            blindSeconds,
            strength,
            new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { playerObject.OwnerClientId } }
            });
    }

    void BlindEnemiesInBlast(Vector3 point)
    {
        // Enemy prefabs do not share a single layer (the Jailor sits on Default, the Zombie and the guards on
        // Enemy), so the blast sweeps every layer once and filters by component. It runs exactly once per
        // grenade, so the wide query costs nothing meaningful.
        int count = Physics.OverlapSphereNonAlloc(
            point, blindRadius, s_blastOverlap, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        count = Mathf.Min(count, s_blastOverlap.Length);

        s_blastEnemies.Clear();
        for (int i = 0; i < count; i++)
        {
            Collider collider = s_blastOverlap[i];
            if (collider == null)
                continue;

            IBlindableEnemy enemy = collider.GetComponentInParent<IBlindableEnemy>();
            if (enemy == null || s_blastEnemies.Contains(enemy))
                continue;

            s_blastEnemies.Add(enemy);
        }

        for (int i = 0; i < s_blastEnemies.Count; i++)
        {
            if (s_blastEnemies[i] is not Component enemyComponent || enemyComponent == null)
                continue;

            Vector3 eyes = EyePoint(enemyComponent.transform);
            if (!TryGetBlastStrength(point, eyes, out _))
                continue;

            // Unlike a player, an enemy is either blinded or not — no partial whiteout to scale.
            EnemyBlindEffect.Apply(enemyComponent, blindSeconds);
        }

        s_blastEnemies.Clear();
    }

    /// <summary>
    /// Distance falloff plus the wall check. <paramref name="strength"/> is 1 near the burst and eases to 0
    /// at the edge of the radius.
    /// </summary>
    bool TryGetBlastStrength(Vector3 burst, Vector3 victimEyes, out float strength)
    {
        strength = 0f;
        Vector3 toVictim = victimEyes - burst;
        float distance = toVictim.magnitude;
        if (distance > blindRadius)
            return false;

        if (requireLineOfSight && distance > 0.05f && IsBlockedByWorld(burst, victimEyes, distance))
            return false;

        float falloffStart = blindRadius * fullStrengthRadiusFraction;
        strength = distance <= falloffStart
            ? 1f
            : 1f - Mathf.InverseLerp(falloffStart, blindRadius, distance);
        strength = Mathf.Clamp01(strength);
        return strength > 0.02f;
    }

    bool IsBlockedByWorld(Vector3 from, Vector3 to, float distance)
    {
        Vector3 dir = (to - from) / distance;
        int count = Physics.RaycastNonAlloc(
            from, dir, s_castHits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        count = Mathf.Min(count, s_castHits.Length);

        for (int i = 0; i < count; i++)
        {
            Collider collider = s_castHits[i].collider;
            if (collider == null)
                continue;
            // Bodies do not shield anyone from a flash; only the level does.
            if (collider.GetComponentInParent<PlayerHealth>() != null)
                continue;
            if (collider.GetComponentInParent<IBlindableEnemy>() != null)
                continue;
            return true;
        }

        return false;
    }

    static Vector3 EyePoint(Transform root)
    {
        return root != null ? root.position + Vector3.up * 1.5f : Vector3.zero;
    }

    [ClientRpc]
    void BlindPlayerClientRpc(ulong playerNetworkObjectId, float seconds, float strength, ClientRpcParams clientRpcParams = default)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return;
        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject playerObject) || playerObject == null)
            return;

        playerObject.GetComponent<PlayerController>()?.ApplyFlashbangBlind(seconds, strength);
    }

    [ClientRpc]
    void DetonateFxClientRpc(Vector3 point)
    {
        FlashbangFlashFx.Play(point, bangClip, bangVolume);
    }
}
