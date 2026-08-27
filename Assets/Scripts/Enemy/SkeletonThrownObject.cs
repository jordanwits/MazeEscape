using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The object a <see cref="SkeletonAI"/> lobs at a player. Server-authoritative: the server flies it along an
/// analytic parabola (so flight is deterministic, no rigidbody needed) and a <c>NetworkTransform</c> replicates
/// the position to clients, so every player sees it travel the same dodgeable arc. The hit is detected and
/// applied on the server — damage + a non-ragdoll shove, matching the close-range bash and the swinging-axe trap.
///
/// The thrown mesh is a placeholder; swap the prefab's visual later without touching this script. Launch params
/// are aimed at the target's position AT RELEASE with no homing, so side-stepping out of the landing path dodges it.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class SkeletonThrownObject : NetworkBehaviour
{
    [Header("Hit detection")]
    [Tooltip("Radius of the per-frame overlap check that looks for a player to hit along the flight path.")]
    [SerializeField] float hitRadius = 0.45f;
    [Tooltip("Layers treated as players (damage target). Defaults to the 'Player' layer.")]
    [SerializeField] LayerMask playerMask;
    [Tooltip("Layers that stop the object (walls / floor). Players and enemies are excluded automatically.")]
    [SerializeField] LayerMask worldMask = ~0;

    [Header("Damage & shove")]
    [SerializeField] float damage = 18f;
    [SerializeField] float pushHorizontalSpeed = 6f;
    [SerializeField] float pushUpwardSpeed = 1.25f;
    [SerializeField, Min(0f)] float pushControlLockSeconds = 0.2f;

    [Header("Spin (cosmetic, flight only)")]
    [SerializeField] Vector3 visualSpinDegPerSec = new Vector3(540f, 0f, 220f);
    [Tooltip("Child transform to spin for visual flair during flight. Defaults to the first child, else this transform.")]
    [SerializeField] Transform visualToSpin;

    [Header("Roll (after landing)")]
    [Tooltip("When the lob lands (or hits a wall) without striking a player, the skull keeps its trajectory into " +
             "physics and rolls on the ground for this long before disappearing. Needs a Rigidbody + Collider.")]
    [SerializeField] float rollDurationSeconds = 3f;
    [Tooltip("Linear damping during the roll so the skull skids to a stop instead of sliding forever.")]
    [SerializeField] float rollDrag = 0.6f;

    [Header("Bounce (on player hit)")]
    [Tooltip("Horizontal speed the skull ricochets away from the player with after a direct hit (it bounces off " +
             "and rolls instead of shattering).")]
    [SerializeField] float bounceHorizontalSpeed = 4f;
    [Tooltip("Upward pop given to the skull as it bounces off the player.")]
    [SerializeField] float bounceUpwardSpeed = 2.5f;

    enum Phase { Flight, Rolling }

    readonly Collider[] _overlap = new Collider[8];

    Vector3 _start;
    Vector3 _target;
    float _arcHeight;
    float _flightDuration;
    float _elapsed;
    bool _launched;
    bool _consumed;
    bool _initialized;
    Vector3 _prevPosition;
    Phase _phase;

    /// <summary>
    /// Replicated flight/rolling phase. <see cref="_phase"/> is written only inside authority-gated code, so on an
    /// observer it stays at its default (Flight) forever — and the cosmetic spin below runs on every peer,
    /// deliberately, so it can be seen in flight. Without this the skull went on spinning at 540°/s where it lay
    /// for the whole roll on every client, while the host watched it tumble to a stop. Replicated rather than
    /// timed locally so a late joiner also sees an already-landed skull at rest.
    /// </summary>
    readonly NetworkVariable<bool> _rollingReplicated = new(false);

    float _rollEndTime;
    Rigidbody _rigidbody;
    Collider _collider;

    static bool IsNetworkActive => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    /// <summary>Authority over flight/damage: the server when networked, otherwise the local instance (offline).</summary>
    bool IsAuthority => !IsNetworkActive || IsServer;

    void Awake()
    {
        InitializeOnce();
    }

    public override void OnNetworkSpawn()
    {
        InitializeOnce();
    }

    void InitializeOnce()
    {
        if (_initialized)
            return;
        _initialized = true;

        if (playerMask.value == 0)
        {
            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer >= 0)
                playerMask = 1 << playerLayer;
        }

        if (visualToSpin == null)
            visualToSpin = transform.childCount > 0 ? transform.GetChild(0) : transform;

        _rigidbody = GetComponent<Rigidbody>();
        _collider = GetComponent<Collider>();
        // Flight is analytic (manual transform moves + overlap checks), so keep physics dormant until the roll.
        if (_rigidbody != null)
        {
            _rigidbody.isKinematic = true;
            _rigidbody.useGravity = false;
            _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
        }
        if (_collider != null)
            _collider.enabled = false;

        _prevPosition = transform.position;
    }

    /// <summary>Authority-only. Begin the arc from <paramref name="start"/> to <paramref name="target"/>.</summary>
    public void Launch(Vector3 start, Vector3 target, float arcHeight, float flightDuration)
    {
        if (!IsAuthority)
            return;

        _start = start;
        _target = target;
        _arcHeight = Mathf.Max(0f, arcHeight);
        _flightDuration = Mathf.Max(0.05f, flightDuration);
        _elapsed = 0f;
        _launched = true;
        _phase = Phase.Flight;
        _prevPosition = start;
        transform.position = start;
    }

    void Update()
    {
        // Runs on every peer (before the authority gate) so observers see the spin during flight — hence the
        // replicated phase rather than the local one, which never leaves Flight off-authority.
        bool stillFlying = IsAuthority ? _phase == Phase.Flight : !_rollingReplicated.Value;
        if (stillFlying && visualToSpin != null && visualSpinDegPerSec != Vector3.zero)
            visualToSpin.Rotate(visualSpinDegPerSec * Time.deltaTime, Space.Self);

        if (!IsAuthority || !_launched || _consumed)
            return;

        if (_phase == Phase.Rolling)
        {
            // Physics owns the transform now; just time out and despawn (NetworkTransform replicates the tumble).
            if (Time.time >= _rollEndTime)
                Despawn();
            return;
        }

        _elapsed += Time.deltaTime;
        float t = Mathf.Clamp01(_elapsed / _flightDuration);

        Vector3 position = EvaluateArc(t);
        transform.position = position;

        // A direct hit damages the player and bounces the skull off (into the roll); otherwise it lands (or clips a wall) and rolls.
        if (TryHitPlayer(position, ArcVelocity(t)))
            return;

        if (CheckWorldBlocked(_prevPosition, position))
        {
            StartRolling(position, ArcVelocity(t));
            return;
        }

        _prevPosition = position;

        if (t >= 1f)
            StartRolling(position, ArcVelocity(1f));
    }

    /// <summary>Velocity (m/s) of the arc at normalized time <paramref name="t"/> — the derivative of EvaluateArc.</summary>
    Vector3 ArcVelocity(float t)
    {
        Vector3 velocity = (_target - _start) / _flightDuration;
        velocity.y += (_arcHeight * 4f * (1f - 2f * t)) / _flightDuration;
        return velocity;
    }

    void StartRolling(Vector3 position, Vector3 handoffVelocity)
    {
        _phase = Phase.Rolling;
        if (IsSpawned && IsServer)
            _rollingReplicated.Value = true; // stops the cosmetic spin on observers too

        _rollEndTime = Time.time + Mathf.Max(0.2f, rollDurationSeconds);
        transform.position = position;

        if (_collider != null)
            _collider.enabled = true;
        if (_rigidbody != null)
        {
            _rigidbody.isKinematic = false;
            _rigidbody.useGravity = true;
            _rigidbody.linearDamping = Mathf.Max(0f, rollDrag);
            // Continue the arc's real velocity (incl. its downward component) so it descends into the ground
            // smoothly instead of stopping mid-air and dropping.
            _rigidbody.linearVelocity = handoffVelocity;
            _rigidbody.angularVelocity = new Vector3(Random.Range(-5f, 5f), Random.Range(-3f, 3f), Random.Range(-5f, 5f));
        }
    }

    Vector3 EvaluateArc(float t)
    {
        Vector3 flat = Vector3.Lerp(_start, _target, t);
        // Parabola peaking at the midpoint: 4h * t * (1 - t).
        float height = _arcHeight * 4f * t * (1f - t);
        flat.y += height;
        return flat;
    }

    bool CheckWorldBlocked(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 1e-4f)
            return false;

        int mask = WorldMaskExcludingActors();
        if (mask == 0)
            return false;

        return Physics.Raycast(from, delta / dist, out _, dist, mask, QueryTriggerInteraction.Ignore);
    }

    int WorldMaskExcludingActors()
    {
        int mask = worldMask.value;
        int playerLayer = LayerMask.NameToLayer("Player");
        int enemyLayer = LayerMask.NameToLayer("Enemy");
        if (playerLayer >= 0) mask &= ~(1 << playerLayer);
        if (enemyLayer >= 0) mask &= ~(1 << enemyLayer);
        return mask;
    }

    bool TryHitPlayer(Vector3 position, Vector3 arcVelocity)
    {
        int mask = playerMask.value != 0 ? playerMask.value : Physics.DefaultRaycastLayers;
        int count = Physics.OverlapSphereNonAlloc(position, hitRadius, _overlap, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider col = _overlap[i];
            if (col == null)
                continue;

            PlayerHealth ph = col.GetComponentInParent<PlayerHealth>();
            if (ph == null || ph.IsDead)
                continue;

            ApplyHit(ph);
            // Deal damage but don't shatter: ricochet off the player and roll. The roll phase doesn't re-run this
            // check, so damage is only applied once.
            StartRolling(position, BounceVelocity(position, arcVelocity, ph.transform.position));
            return true;
        }

        return false;
    }

    /// <summary>Velocity for the skull as it bounces off a player it just struck — pushed back away from them with an upward pop.</summary>
    Vector3 BounceVelocity(Vector3 position, Vector3 arcVelocity, Vector3 playerPosition)
    {
        Vector3 away = position - playerPosition;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-4f)
        {
            // Skull is essentially on top of the player; bounce back along the incoming horizontal direction instead.
            away = new Vector3(-arcVelocity.x, 0f, -arcVelocity.z);
        }
        away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.back;
        return away * bounceHorizontalSpeed + Vector3.up * bounceUpwardSpeed;
    }

    void ApplyHit(PlayerHealth ph)
    {
        ph.TakeDamage(damage);

        Vector3 dir = _target - _start;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : transform.forward;
        Vector3 pushVel = dir * pushHorizontalSpeed;

        NetworkObject no = ph.GetComponent<NetworkObject>();
        if (IsNetworkActive && no != null)
        {
            // Movement is owner-authoritative, so the OWNER must run the shove for their position to replicate.
            ApplyPushRpc(no.NetworkObjectId, pushVel, pushUpwardSpeed, pushControlLockSeconds,
                RpcTarget.Single(no.OwnerClientId, RpcTargetUse.Temp));
        }
        else
        {
            ph.GetComponent<PlayerController>()?.ApplyExternalPush(pushVel, pushUpwardSpeed, pushControlLockSeconds);
        }
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void ApplyPushRpc(ulong playerNetworkObjectId, Vector3 horizontalVelocity, float upwardVelocity,
        float controlLockSeconds, RpcParams rpcParams = default)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return;
        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(playerNetworkObjectId, out NetworkObject no) || no == null)
            return;

        no.GetComponent<PlayerController>()?.ApplyExternalPush(horizontalVelocity, upwardVelocity, controlLockSeconds);
    }

    void Despawn()
    {
        if (_consumed)
            return;
        _consumed = true;

        if (IsNetworkActive && IsSpawned && IsServer)
        {
            NetworkObject.Despawn(true);
            return;
        }

        Destroy(gameObject);
    }
}
