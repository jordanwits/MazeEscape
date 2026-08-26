using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The live decoy thrown from a <see cref="DecoyGrenadeItem"/>. Flies and bounces exactly like a
/// <see cref="FlashbangGrenade"/> — same ballistic step, same bounce model — but what happens at the end of
/// the fuse is the opposite. A flashbang goes off once and is gone; a decoy switches ON and then sits there
/// squawking for several seconds, dragging every hunter in earshot toward it.
///
/// The pull is a repeating pulse rather than a single ping, and that is the whole point: an enemy who
/// wanders into range halfway through still gets called, an enemy who reaches the decoy has his interest
/// topped up so he mills around it instead of losing interest and walking off, and the player gets a
/// window they can actually escape through rather than one instant of misdirection.
///
/// Deliberately NOT line-of-sight gated. This is a noise, not a flash — being round the corner from it is
/// exactly when you want it to work. Only distance matters.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DecoyGrenade : NetworkBehaviour
{
    [Header("Fuse")]
    [Tooltip("Seconds from leaving the hand to the decoy switching on. Short on purpose - you throw this to run away from something.")]
    [SerializeField, Min(0.1f)] float fuseSeconds = 1.5f;

    [Header("Flight")]
    [SerializeField] float castRadius = 0.06f;
    [Tooltip("How much speed survives a bounce (0 = dead stop, 1 = perfectly elastic).")]
    [SerializeField, Range(0f, 1f)] float bounciness = 0.38f;
    [Tooltip("How much speed ALONG the surface survives a bounce - low values stop it skidding down corridors.")]
    [SerializeField, Range(0f, 1f)] float surfaceFriction = 0.55f;
    [Tooltip("Below this speed the decoy stops bouncing and settles.")]
    [SerializeField, Min(0f)] float restSpeed = 0.6f;
    [SerializeField] float spinDegreesPerSecond = 520f;

    [Header("Lure")]
    [Tooltip("How long the decoy keeps calling once it switches on.")]
    [SerializeField, Min(0.5f)] float callSeconds = 9f;
    [Tooltip("How far the noise carries in a straight line. Generous on purpose - this has to reach a hunter you cannot see.")]
    [SerializeField, Min(1f)] float hearingRadius = 30f;
    [Tooltip("How far a hunter may actually have to WALK to get here. Hearing it through a wall is fine; hiking around half the level is not - past this the lure is ignored even though the noise carries that far. Maze corridors wander, so this needs real headroom over hearingRadius.")]
    [SerializeField, Min(1f)] float maxTravelDistance = 60f;
    [Tooltip("Seconds between lure pulses. Each pulse re-calls everyone still in range.")]
    [SerializeField, Min(0.1f)] float lurePulseSeconds = 1f;

    [Header("Audio")]
    [Tooltip("The racket it makes, played positionally on every peer from switch-on until it winds down.")]
    [SerializeField] AudioClip decoyClip;
    [SerializeField, Range(0f, 1f)] float decoyVolume = 1f;
    [Tooltip("Loop the clip until the noise winds down. Off = play it once at switch-on.")]
    [SerializeField] bool loopClip = true;
    [Tooltip("How long the racket is audible once it switches on. Shorter than the call window on purpose - a decoy that keeps squawking for the full lure is exhausting to be near, and the pull does not need the noise to keep working. 0 or anything past the call = audible for the whole thing.")]
    [SerializeField, Min(0f)] float soundSeconds = 6f;
    [Tooltip("Seconds spent fading out at the end of the racket. A looping clip cut dead mid-waveform clicks; this also reads as the thing running out of steam.")]
    [SerializeField, Min(0.01f)] float soundFadeSeconds = 0.6f;
    [Tooltip("Inside this distance the decoy is at full volume for a listening player.")]
    [SerializeField, Min(0.5f)] float audioMinDistance = 4f;
    [Tooltip("Beyond this a player cannot hear it at all. Independent of hearingRadius, which is what ENEMIES use.")]
    [SerializeField, Min(1f)] float audioMaxDistance = 45f;

    [Header("Debug")]
    [Tooltip("Log why each hunter was or was not called, once per pulse. Server-side only. Leave OFF in normal play - it is one line per enemy per second.")]
    [SerializeField] bool logLureDecisions;

    static readonly RaycastHit[] s_castHits = new RaycastHit[16];
    static readonly List<ILurableEnemy> s_lurables = new List<ILurableEnemy>(24);
    static NavMeshPath s_path;

    Vector3 _velocity;
    Transform _throwerRoot;
    float _activateAt;
    float _silenceAt;
    float _nextPulseAt;
    bool _launched;
    bool _active;
    bool _finished;
    bool _atRest;
    Vector3 _spinAxis = Vector3.right;
    AudioSource _source;
    float _muteAt;

    /// <summary>How long this decoy keeps calling once lit - exposed so tuning lives on the prefab alone.</summary>
    public float CallSeconds => callSeconds;

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
        _activateAt = Time.time + fuseSeconds;
        _launched = true;
        _atRest = false;

        Vector3 forward = velocity.sqrMagnitude > 0.0001f ? velocity.normalized : Vector3.forward;
        _spinAxis = Vector3.Cross(forward, Vector3.up);
        if (_spinAxis.sqrMagnitude < 0.0001f)
            _spinAxis = Vector3.right;
        _spinAxis.Normalize();
    }

    void Update()
    {
        // Tumbles until it settles, then lies still and shouts. Like the flashbang it emits nothing at all
        // in flight, so a decoy in the air never gives away where it is going to land.
        if (_launched && !_finished && !_atRest)
            transform.rotation = Quaternion.AngleAxis(spinDegreesPerSecond * Time.deltaTime, _spinAxis) * transform.rotation;

        UpdateNoiseWindDown();
    }

    /// <summary>
    /// Winds the racket down once <see cref="soundSeconds"/> is up, leaving the decoy to keep pulling
    /// hunters in silence for the rest of its call.
    ///
    /// Deliberately driven from Update rather than FixedUpdate: the noise is a purely local effect that
    /// every peer runs for itself, and clients never reach FixedUpdate's authority-only body at all.
    /// </summary>
    void UpdateNoiseWindDown()
    {
        if (_source == null || _muteAt <= 0f)
            return;

        float past = Time.time - _muteAt;
        if (past < 0f)
            return;

        float t = Mathf.Clamp01(past / Mathf.Max(0.01f, soundFadeSeconds));
        _source.volume = Mathf.Clamp01(decoyVolume) * (1f - t);
        if (t < 1f)
            return;

        _source.Stop();
        _muteAt = 0f; // done - stop touching the source for the rest of the call
    }

    void FixedUpdate()
    {
        if (!_launched || _finished || !IsAuthority)
            return;

        if (!_active && Time.time >= _activateAt)
            Activate();

        if (_active)
        {
            if (Time.time >= _silenceAt)
            {
                Finish();
                return;
            }

            if (Time.time >= _nextPulseAt)
            {
                _nextPulseAt = Time.time + Mathf.Max(0.1f, lurePulseSeconds);
                PulseLure(transform.position);
            }
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
        if (collider.GetComponentInParent<ILurableEnemy>() != null)
            return true;
        if (collider.GetComponentInParent<DecoyGrenade>() != null)
            return true;

        return false;
    }

    void Activate()
    {
        _active = true;
        _silenceAt = Time.time + Mathf.Max(0.5f, callSeconds);
        _nextPulseAt = 0f; // first pulse immediately, so it grabs attention the instant it lights up

        if (IsSpawned)
        {
            ActivateFxClientRpc();
            return;
        }

        BeginLocalNoise();
    }

    void Finish()
    {
        _finished = true;

        if (IsSpawned)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                NetworkObject.Despawn(true);
            return;
        }

        Destroy(gameObject);
    }

    /// <summary>
    /// Call every hunter in earshot to this spot. Runs on the authority only.
    ///
    /// Uses the per-type registries rather than an OverlapSphere: the hearing radius is far bigger than a
    /// flashbang blast and sweeping 30m of maze geometry every pulse would return hundreds of colliders to
    /// filter, most of them walls.
    /// </summary>
    void PulseLure(Vector3 point)
    {
        // Send them to a spot that is actually ON the mesh, not to wherever the grenade physically came to
        // rest (it can settle against a wall, on a crate, in a corner the agents cannot occupy). If the
        // decoy cannot be resolved onto the mesh at all, nobody can path to it and nobody is called.
        if (!NavMesh.SamplePosition(point, out NavMeshHit decoyHit, 6f, NavMesh.AllAreas))
        {
            if (logLureDecisions)
                Debug.LogWarning($"[Decoy] no NavMesh within 6m of the decoy at {point} - nobody can be called.", this);
            return;
        }
        Vector3 target = decoyHit.position;

        s_lurables.Clear();
        CollectLurables(s_lurables);

        if (logLureDecisions)
            Debug.Log($"[Decoy] pulse at {target} | candidates={s_lurables.Count} " +
                      $"(clowns={ClownAIRegistry.All.Count} jailors={JailorAIRegistry.All.Count} guards={SecurityGuardAIRegistry.All.Count})", this);

        float r2 = hearingRadius * hearingRadius;
        for (int i = 0; i < s_lurables.Count; i++)
        {
            ILurableEnemy enemy = s_lurables[i];
            string who = enemy is Component ec && ec != null ? ec.gameObject.name : "?";

            // A hunter already on a player is deaf to this - a decoy baits, it never rescues.
            if (enemy.IsPursuingPlayer)
            {
                if (logLureDecisions) Debug.Log($"[Decoy]   {who}: SKIP - pursuing a player", this);
                continue;
            }

            Vector3 ear = enemy.LureListenPosition;
            float straight = Vector3.Distance(ear, point);
            if ((ear - point).sqrMagnitude > r2)
            {
                if (logLureDecisions) Debug.Log($"[Decoy]   {who}: SKIP - {straight:F1}m out of {hearingRadius}m earshot", this);
                continue;
            }

            // Straight-line earshot is NOT enough. Sound carries through a wall, but legs do not: without
            // this an enemy one wall away from the decoy but a long way around by corridor would accept the
            // lure, path partially, and shove himself into the dead end nearest the noise. Require a
            // COMPLETE route and a sane walk length; otherwise he simply never heard it.
            bool walkable = IsWalkable(ear, target, out float travel, out string why);
            if (!walkable)
            {
                if (logLureDecisions) Debug.Log($"[Decoy]   {who}: SKIP - unreachable ({why}), straight {straight:F1}m", this);
                continue;
            }
            if (travel > maxTravelDistance)
            {
                if (logLureDecisions) Debug.Log($"[Decoy]   {who}: SKIP - {travel:F1}m walk exceeds {maxTravelDistance}m (straight {straight:F1}m)", this);
                continue;
            }

            if (logLureDecisions) Debug.Log($"[Decoy]   {who}: CALLED - {travel:F1}m walk (straight {straight:F1}m)", this);
            enemy.LureToNoise(target);
        }

        s_lurables.Clear();
    }

    /// <summary>
    /// True when a full NavMesh route exists from <paramref name="from"/> to <paramref name="to"/>, with
    /// <paramref name="travel"/> set to its real walked length (not the straight-line distance).
    ///
    /// A PathPartial result is the important one to reject: that is precisely the "walks into the dead end
    /// nearest the noise" case, because the agent happily follows a partial path as far as it goes.
    /// </summary>
    static bool IsWalkable(Vector3 from, Vector3 to, out float travel, out string why)
    {
        travel = float.PositiveInfinity;
        why = null;

        // CalculatePath from an off-mesh start always fails, and enemies drift off the mesh constantly.
        if (!NavMesh.SamplePosition(from, out NavMeshHit fromHit, 4f, NavMesh.AllAreas))
        {
            why = "enemy is not within 4m of any NavMesh";
            return false;
        }

        s_path ??= new NavMeshPath();
        if (!NavMesh.CalculatePath(fromHit.position, to, NavMesh.AllAreas, s_path))
        {
            why = "CalculatePath failed outright";
            return false;
        }
        if (s_path.status != NavMeshPathStatus.PathComplete)
        {
            why = "path is " + s_path.status; // PathPartial = walled off / dead end
            return false;
        }

        Vector3[] corners = s_path.corners;
        if (corners.Length < 2)
        {
            travel = 0f;
            return true;
        }

        float sum = 0f;
        for (int i = 1; i < corners.Length; i++)
            sum += Vector3.Distance(corners[i - 1], corners[i]);
        travel = sum;
        return true;
    }

    /// <summary>
    /// The three hunters a decoy works on. Null-checked as concrete types, never as the interface: a
    /// destroyed MonoBehaviour compares false to null through an interface reference, so an interface-typed
    /// null check would happily call into a dead object.
    /// </summary>
    static void CollectLurables(List<ILurableEnemy> into)
    {
        IReadOnlyList<ClownAI> clowns = ClownAIRegistry.All;
        for (int i = 0; i < clowns.Count; i++)
        {
            ClownAI c = clowns[i];
            if (c != null)
                into.Add(c);
        }

        IReadOnlyList<JailorAI> jailors = JailorAIRegistry.All;
        for (int i = 0; i < jailors.Count; i++)
        {
            JailorAI j = jailors[i];
            if (j != null)
                into.Add(j);
        }

        IReadOnlyList<SecurityGuardAI> guards = SecurityGuardAIRegistry.All;
        for (int i = 0; i < guards.Count; i++)
        {
            SecurityGuardAI g = guards[i];
            if (g != null)
                into.Add(g);
        }
    }

    /// <summary>
    /// The noise itself, built on the grenade rather than on a throwaway FX object (the flashbang's
    /// approach) because unlike a bang this has to keep playing from a moving-then-settling position for
    /// several seconds, and the grenade is already the thing every peer is watching.
    /// </summary>
    void BeginLocalNoise()
    {
        if (decoyClip == null || _source != null)
            return;

        _source = gameObject.AddComponent<AudioSource>();
        _source.playOnAwake = false;
        _source.clip = decoyClip;
        _source.loop = loopClip;
        _source.volume = Mathf.Clamp01(decoyVolume);
        _source.spatialBlend = 1f;
        _source.dopplerLevel = 0f;
        _source.rolloffMode = AudioRolloffMode.Linear;
        _source.minDistance = Mathf.Max(0.5f, audioMinDistance);
        _source.maxDistance = Mathf.Max(audioMinDistance + 1f, audioMaxDistance);
        GameAudioManager.RouteSfxSource(_source);
        _source.Play();

        // Zero (or anything at/past the call) means "audible the whole time" - leave the wind-down off and
        // let the despawn kill it, exactly as it behaved before this knob existed.
        // _muteAt is when the fade STARTS, so soundSeconds is the whole audible window, fade included.
        float audible = Mathf.Max(0f, soundSeconds);
        float fade = Mathf.Max(0.01f, soundFadeSeconds);
        _muteAt = audible > 0f && audible < Mathf.Max(0.5f, callSeconds)
            ? Time.time + Mathf.Max(0.05f, audible - fade)
            : 0f;
    }

    public override void OnNetworkDespawn()
    {
        // Kill the racket the instant it despawns, on every peer, rather than letting a looping clip hang
        // on for a frame while the object tears down.
        if (_source != null)
            _source.Stop();
        base.OnNetworkDespawn();
    }

    [ClientRpc]
    void ActivateFxClientRpc()
    {
        BeginLocalNoise();
    }
}
