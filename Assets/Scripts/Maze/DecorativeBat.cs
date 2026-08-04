using UnityEngine;

/// <summary>
/// One decorative bat, pooled and driven by <see cref="BatSwarmRoost"/>. Purely local and purely
/// cosmetic: no NetworkObject, no RPCs, no damage, no NavMesh. Every peer runs its own bats off the
/// maze seed, so there is nothing to replicate.
///
/// Flight is two steered phases. <b>Burst</b> aims at a point beside the viewer's head (each bat gets
/// its own lateral/vertical offset so a swarm fans out instead of stacking into one streak), which is
/// what sells the "flies out at your face" beat. Once inside <see cref="passDistance"/> it flips to
/// <b>Flee</b> and steers down the corridor direction the roost handed it, wandering as it goes.
///
/// Nothing here uses physics — steering is a turn-rate-limited velocity, so bats can't be shoved by
/// the player or bounce off a ragdoll. Walls are handled by a forward spherecast (see
/// <see cref="SteerAroundWalls"/>) because a bat clipping through a wall reads as a bug, not a scare.
///
/// The wing geometry is deformed entirely in BatFlap.shader — there is no rig and no animation clip.
/// This script owns the flap <em>angle</em> and pushes it through a MaterialPropertyBlock each frame
/// (see <see cref="ApplyFlap"/> for why the shader's own rate term is pinned to zero). The property
/// block drops these renderers out of the SRP batcher, which is a non-issue at swarm sizes but worth
/// knowing before the pattern gets copied to something numerous.
/// </summary>
[DisallowMultipleComponent]
public class DecorativeBat : MonoBehaviour
{
    enum FlightPhase { Idle, Burst, Flee }

    [Header("Model")]
    [Tooltip("Child transform holding the bat MeshRenderer. Bank and body bob are applied here so they "
        + "don't fight the steering on the root. Auto-found as the first child renderer if left empty.")]
    [SerializeField] Transform model;

    [Header("Speed")]
    [Tooltip("Cruise speed in m/s during the burst toward the player. Real bats manage 5-9 m/s; faster "
        + "reads as more startling but gives the player less time to register what flew past.")]
    [SerializeField] float burstSpeed = 7.5f;
    [Tooltip("Cruise speed in m/s once fleeing away down the corridor.")]
    [SerializeField] float fleeSpeed = 6f;
    [Tooltip("How fast the bat can change heading, in degrees/second. Low values give wide lazy arcs; "
        + "high values give the darting, insect-like turns bats are known for.")]
    [SerializeField] float turnRateDegrees = 420f;

    [Header("Path")]
    [Tooltip("Metres from the viewer at which the bat gives up on its approach point and peels away. "
        + "Below about 1.5 m bats start visibly clipping the camera.")]
    [SerializeField] float passDistance = 1.8f;
    [Tooltip("Sideways spread of the approach point either side of the viewer, in metres. Each bat picks "
        + "a random offset in this range (and a random side), which is what fans the swarm out.")]
    [SerializeField] Vector2 lateralOffsetRange = new(0.5f, 1.9f);
    [Tooltip("Vertical spread of the approach point relative to the viewer's eye, in metres. Biased below "
        + "eye level on purpose — bats aimed above the camera leave through the top of the screen and the "
        + "player never registers them.")]
    [SerializeField] Vector2 verticalOffsetRange = new(-0.8f, 0.15f);
    [Tooltip("How far the launch heading is scattered off 'straight at the player'. High values look "
        + "chaotic but let bats leave without ever committing to the dive.")]
    [SerializeField, Range(0f, 1f)] float launchScatter = 0.35f;

    [Header("Flee altitude")]
    [Tooltip("Height the fleeing bat settles to, in metres relative to the viewer's eye. Without this the "
        + "bat holds whatever altitude it peeled off at and drifts out above the player's sightline — which "
        + "is the main reason a swarm reads as 'flew somewhere overhead' instead of 'flew at me'.")]
    [SerializeField] float fleeHeightOffset = -0.35f;
    [Tooltip("How hard the bat corrects toward that height. 0 disables the altitude hold entirely.")]
    [SerializeField, Range(0f, 2f)] float fleeHeightCorrection = 0.6f;
    [Tooltip("How hard the bat wanders off its heading while fleeing, in degrees. 0 = flies a straight "
        + "line out of the level, which looks scripted.")]
    [SerializeField] float wanderDegrees = 55f;
    [Tooltip("Wander oscillations per second while fleeing.")]
    [SerializeField] float wanderRate = 1.6f;

    [Header("Wall avoidance")]
    [Tooltip("Geometry the bat steers around. Leave as Everything — the cast ignores triggers and the "
        + "bat has no collider of its own, so it can only hit level geometry and props.")]
    [SerializeField] LayerMask wallMask = ~0;
    [Tooltip("Metres ahead the bat looks for walls. Roughly speed x reaction time; too short and it "
        + "clips corners, too long and it swerves in open rooms for no reason.")]
    [SerializeField] float lookAheadDistance = 2.2f;
    [Tooltip("Radius of the forward spherecast. Should comfortably exceed the wingspan (0.44 m) so a "
        + "wing doesn't sink into a wall the body cleared.")]
    [SerializeField] float avoidRadius = 0.35f;
    [Tooltip("Degrees to swing left/right when probing for a way around an obstacle.")]
    [SerializeField] float avoidProbeDegrees = 45f;

    [Header("Body motion")]
    [Tooltip("Metres the body rises and falls with each wingbeat. Bats gain height on the downstroke; "
        + "without this the flight reads as a paper plane.")]
    [SerializeField] float bobAmplitude = 0.07f;
    [Tooltip("Maximum roll into a turn, in degrees.")]
    [SerializeField] float maxBankDegrees = 55f;
    [Tooltip("How quickly the bank angle catches up to the turn, in degrees/second.")]
    [SerializeField] float bankResponseDegrees = 320f;

    [Header("Flap")]
    [Tooltip("Shader flap rate (Hz) during the burst. Bats beat around 10 Hz in a panic launch.")]
    [SerializeField] float burstFlapHz = 10f;
    [Tooltip("Shader flap rate (Hz) once fleeing and settled.")]
    [SerializeField] float cruiseFlapHz = 6.5f;
    [Tooltip("Seconds to ease from the burst flap rate down to cruise.")]
    [SerializeField] float flapSettleSeconds = 1.4f;

    [Header("Lifetime")]
    [Tooltip("Hard cap in seconds. Backstop only — normally the bat despawns once it is past the fog "
        + "end distance and genuinely invisible.")]
    [SerializeField] float maxLifetime = 14f;
    [Tooltip("Extra metres beyond RenderSettings.fogEndDistance before despawning, so the bat is fully "
        + "swallowed by fog rather than winking out at the edge of visibility.")]
    [SerializeField] float despawnFogMargin = 4f;
    [Tooltip("Fallback despawn distance in metres, used when linear fog is off for this level.")]
    [SerializeField] float despawnFallbackDistance = 55f;

    static readonly int PhaseId = Shader.PropertyToID("_Phase");
    static readonly int FlapRateId = Shader.PropertyToID("_FlapRate");

    MeshRenderer _renderer;
    MaterialPropertyBlock _mpb;
    BatSwarmRoost _owner;
    Transform _viewer;

    FlightPhase _phase = FlightPhase.Idle;
    Vector3 _velocity;
    Vector3 _fleeDirection;
    Vector3 _approachOffset;
    float _age;
    float _bank;
    float _flapPhase;
    float _wanderSeed;

    /// <summary>True while this bat is in the air and should not be handed out by the pool.</summary>
    public bool IsFlying => _phase != FlightPhase.Idle;

    void Awake()
    {
        if (model == null)
        {
            MeshRenderer child = GetComponentInChildren<MeshRenderer>(true);
            model = child != null ? child.transform : transform;
        }

        _renderer = model.GetComponent<MeshRenderer>();
        if (_renderer == null)
            _renderer = GetComponentInChildren<MeshRenderer>(true);

        _mpb = new MaterialPropertyBlock();
    }

    /// <summary>
    /// Puts the bat in the air. <paramref name="fleeDirection"/> should point down an actual corridor
    /// opening — the roost derives it from the maze cell so bats don't flee straight into a wall.
    /// </summary>
    public void Launch(BatSwarmRoost owner, Vector3 origin, Vector3 fleeDirection, Transform viewer, float phaseOffset)
    {
        _owner = owner;
        _viewer = viewer;
        _age = 0f;
        _bank = 0f;
        _flapPhase = phaseOffset;
        _wanderSeed = Random.value * 100f;

        _fleeDirection = fleeDirection.sqrMagnitude > 0.0001f
            ? fleeDirection.normalized
            : Random.onUnitSphere;
        _fleeDirection.y *= 0.35f; // corridors are horizontal; keep the exit shallow
        _fleeDirection.Normalize();

        float side = Random.value < 0.5f ? -1f : 1f;
        _approachOffset = new Vector3(
            side * Random.Range(lateralOffsetRange.x, lateralOffsetRange.y),
            Random.Range(verticalOffsetRange.x, verticalOffsetRange.y),
            0f);

        transform.position = origin;

        // Launch heading: mostly at the viewer, scattered so the swarm doesn't leave as one arrow.
        Vector3 toViewer = viewer != null ? (viewer.position - origin) : _fleeDirection;
        if (toViewer.sqrMagnitude < 0.0001f)
            toViewer = _fleeDirection;
        Vector3 heading = (toViewer.normalized + Random.insideUnitSphere * launchScatter).normalized;

        _velocity = heading * burstSpeed;
        transform.rotation = Quaternion.LookRotation(heading, Vector3.up);

        _phase = FlightPhase.Burst;
        gameObject.SetActive(true);

        // phaseOffset seeds _flapPhase, so the swarm never flaps in unison.
        ApplyFlap();
    }

    void Update()
    {
        if (_phase == FlightPhase.Idle)
            return;

        float dt = Time.deltaTime;
        _age += dt;

        Vector3 desired = ComputeDesiredDirection();
        desired = SteerAroundWalls(desired);

        float speed = _phase == FlightPhase.Burst ? burstSpeed : fleeSpeed;
        Vector3 previousHeading = _velocity.sqrMagnitude > 0.0001f ? _velocity.normalized : desired;

        _velocity = Vector3.RotateTowards(
            _velocity,
            desired * speed,
            turnRateDegrees * Mathf.Deg2Rad * dt,
            Mathf.Abs(speed - _velocity.magnitude) + 0.01f);

        transform.position += _velocity * dt;

        Vector3 heading = _velocity.sqrMagnitude > 0.0001f ? _velocity.normalized : previousHeading;
        transform.rotation = Quaternion.LookRotation(heading, Vector3.up);

        UpdateBankAndBob(previousHeading, heading, dt);
        UpdateFlap(dt);
        MaybeFinish();
    }

    Vector3 ComputeDesiredDirection()
    {
        if (_phase == FlightPhase.Burst && _viewer != null)
        {
            // Aim beside the viewer's head rather than at it, so the bat sweeps past instead of
            // ending up inside the camera.
            Vector3 right = _viewer.right;
            Vector3 target = _viewer.position
                           + right * _approachOffset.x
                           + Vector3.up * _approachOffset.y;

            Vector3 toTarget = target - transform.position;
            if (toTarget.magnitude <= passDistance)
            {
                _phase = FlightPhase.Flee;
                return ComputeFleeDirection();
            }

            return toTarget.normalized;
        }

        if (_phase == FlightPhase.Burst)
        {
            // No viewer (shouldn't happen, but don't strand the bat) — go straight to fleeing.
            _phase = FlightPhase.Flee;
        }

        return ComputeFleeDirection();
    }

    Vector3 ComputeFleeDirection()
    {
        // Wander is a slow yaw oscillation around the corridor heading. Two offset frequencies keep
        // it from reading as a clean sine wave.
        float t = Time.time * wanderRate + _wanderSeed;
        float yaw = (Mathf.Sin(t) * 0.7f + Mathf.Sin(t * 1.73f) * 0.3f) * wanderDegrees;
        float pitch = Mathf.Sin(t * 0.83f + 1.1f) * wanderDegrees * 0.25f;

        Vector3 direction = Quaternion.AngleAxis(yaw, Vector3.up)
                          * Quaternion.AngleAxis(pitch, Vector3.right)
                          * _fleeDirection;

        // Altitude hold. The corridor heading is horizontal, so without this the bat keeps whatever
        // height it happened to peel off at — usually still well above the player, where it exits the
        // top of the screen unseen. Pulling toward eye level keeps the escape inside the sightline.
        if (_viewer != null && fleeHeightCorrection > 0f)
        {
            float targetY = _viewer.position.y + fleeHeightOffset;
            float error = targetY - transform.position.y;
            direction.y += Mathf.Clamp(error * fleeHeightCorrection, -1f, 1f);
        }

        return direction.normalized;
    }

    /// <summary>
    /// Forward spherecast; if blocked, probes left and right and takes whichever has more room. Only
    /// the blocked case pays for the extra casts, so the common case is one cast per bat per frame.
    /// </summary>
    Vector3 SteerAroundWalls(Vector3 desired)
    {
        if (lookAheadDistance <= 0f)
            return desired;

        if (!Physics.SphereCast(transform.position, avoidRadius, desired, out RaycastHit _,
                lookAheadDistance, wallMask, QueryTriggerInteraction.Ignore))
            return desired;

        Vector3 best = desired;
        float bestClearance = -1f;

        for (int i = 0; i < 4; i++)
        {
            // ±45°, then ±90° yaw, plus a mild climb — bats tend to go over obstacles.
            float sign = (i % 2 == 0) ? 1f : -1f;
            float magnitude = avoidProbeDegrees * ((i < 2) ? 1f : 2f);
            Vector3 probe = Quaternion.AngleAxis(sign * magnitude, Vector3.up) * desired;
            probe = (probe + Vector3.up * 0.15f).normalized;

            float clearance = Physics.SphereCast(transform.position, avoidRadius, probe, out RaycastHit hit,
                                  lookAheadDistance, wallMask, QueryTriggerInteraction.Ignore)
                ? hit.distance
                : lookAheadDistance;

            if (clearance > bestClearance)
            {
                bestClearance = clearance;
                best = probe;
            }

            if (clearance >= lookAheadDistance)
                break; // fully clear, no need to keep probing
        }

        return best;
    }

    void UpdateBankAndBob(Vector3 previousHeading, Vector3 heading, float dt)
    {
        if (model == null || model == transform)
            return;

        // Bank into the turn: signed yaw change this frame, scaled and damped.
        Vector3 flatPrev = Vector3.ProjectOnPlane(previousHeading, Vector3.up);
        Vector3 flatNow = Vector3.ProjectOnPlane(heading, Vector3.up);
        float turn = 0f;
        if (flatPrev.sqrMagnitude > 0.0001f && flatNow.sqrMagnitude > 0.0001f && dt > 0f)
            turn = Vector3.SignedAngle(flatPrev, flatNow, Vector3.up) / dt;

        float targetBank = Mathf.Clamp(-turn * 0.12f, -maxBankDegrees, maxBankDegrees);
        _bank = Mathf.MoveTowards(_bank, targetBank, bankResponseDegrees * dt);

        model.localRotation = Quaternion.Euler(0f, 0f, _bank);
        model.localPosition = new Vector3(0f, Mathf.Sin(_flapPhase) * bobAmplitude, 0f);
    }

    void UpdateFlap(float dt)
    {
        float settle = flapSettleSeconds > 0f ? Mathf.Clamp01(_age / flapSettleSeconds) : 1f;
        float hz = Mathf.Lerp(burstFlapHz, cruiseFlapHz, settle);

        _flapPhase += 2f * Mathf.PI * hz * dt;
        ApplyFlap();
    }

    /// <summary>
    /// Hands the shader the whole flap angle and zeroes its internal rate.
    ///
    /// BatFlap.shader computes <c>2*pi*_FlapRate*time + _Phase</c>. Left to run on _FlapRate the wing
    /// phase is a function of ABSOLUTE time, so easing the rate from burst to cruise would snap the
    /// phase every time the rate moved. Driving _Phase directly and pinning _FlapRate to 0 makes the
    /// rate change continuous, and keeps the CPU-side body bob locked to the same angle the wings use.
    /// The material's own _FlapRate is left alone for the editor preview, where nothing drives this.
    /// </summary>
    void ApplyFlap()
    {
        if (_renderer == null)
            return;

        _mpb.SetFloat(FlapRateId, 0f);
        _mpb.SetFloat(PhaseId, _flapPhase);
        _renderer.SetPropertyBlock(_mpb);
    }

    void MaybeFinish()
    {
        if (_age >= maxLifetime)
        {
            Finish();
            return;
        }

        if (_phase != FlightPhase.Flee || _viewer == null)
            return;

        // Normal exit: past the fog end, so the bat is already invisible when it disappears.
        float despawnAt = RenderSettings.fog && RenderSettings.fogMode == FogMode.Linear
            ? RenderSettings.fogEndDistance + despawnFogMargin
            : despawnFallbackDistance;

        if ((transform.position - _viewer.position).sqrMagnitude > despawnAt * despawnAt)
            Finish();
    }

    void Finish()
    {
        _phase = FlightPhase.Idle;
        _viewer = null;
        gameObject.SetActive(false);

        if (_owner != null)
            _owner.NotifyBatFinished(this);
    }
}
