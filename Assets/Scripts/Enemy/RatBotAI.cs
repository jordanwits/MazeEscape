using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Weeping-angel style carnival ambusher. Server-authoritative, mirrors the WindupMonkey pattern:
/// the server runs all logic, a <see cref="ServerNetworkAnimator"/> replicates animator parameters and a
/// NetworkTransform replicates movement. This component stays enabled on clients only to lerp the jaw
/// hinge and apply the replicated freeze-frame pose.
///
/// Behaviour: spawns Dormant, looping its Idle clip like a harmless prop. The first time any living
/// player actually sees it (view cone + line of sight + range) it activates (Stalking) — but keeps
/// idling until everyone looks away. While Stalking it only moves when unobserved: each movement burst
/// re-rolls crawl vs left/right sneak, and the instant any player sees it mid-move it freezes on the
/// exact animation frame (state hash + normalized time replicate so every peer shows the same statue).
/// Reaching a player unobserved triggers the placeholder pounce: freeze, jaw snaps open, scream, and the
/// player is ragdoll-thrown via the server-authoritative hit path (a real grab/lift/throw animation can
/// replace the visuals later — the sequencing hooks are already here). After the throw it flees to a far
/// NavMesh point (deliberately ignoring observation) and goes fully Dormant there, re-armable by sight.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class RatBotAI : NetworkBehaviour
{
    enum RatBotState : byte
    {
        Dormant = 0,   // idle loop, pretending to be a prop; activates when first seen
        Stalking = 1,  // weeping-angel: moves unobserved, statue-freezes when seen
        Pouncing = 2,  // committed attack: jaw open + scream + ragdoll throw (ignores observation)
        Fleeing = 3,   // running to a far point after a throw (ignores observation), then Dormant
    }

    const int LocomotionIdle = 0;
    const int LocomotionSneakLeft = 1;
    const int LocomotionSneakRight = 2;
    const int LocomotionCrawl = 3;
    const int LocomotionSprint = 4;

    [Header("Refs (auto-resolved if left empty)")]
    [SerializeField] Animator animator;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [Tooltip("Hinge transform the lower jaw mesh hangs from. Rotated locally by the jaw-open amount; not animator-driven, so the scream works with any (or no) body animation.")]
    [SerializeField] Transform jawHinge;
    [SerializeField] AudioSource screamAudioSource;
    [Tooltip("One-shot played on every peer when the pounce starts. Fine to leave empty until a scream is recorded.")]
    [SerializeField] AudioClip screamClip;

    [Header("Observation (what counts as 'a player is looking at him')")]
    [Tooltip("Players farther than this can never observe (or activate) him — roughly the distance fog/cull edge where he stops being visible anyway.")]
    [SerializeField, Min(1f)] float maxObserveDistance = 26f;
    [Tooltip("Half-angle (degrees) of each player's view cone, measured from the camera forward. ~48 approximates the horizontal screen edge at the default FOV.")]
    [SerializeField, Range(5f, 89f)] float viewConeHalfAngleDegrees = 48f;
    [Tooltip("Layers that can block a player's line of sight to him (walls). Hits on the player itself or on this RatBot never block.")]
    [SerializeField] LayerMask lineOfSightMask = Physics.DefaultRaycastLayers;
    [Tooltip("Two sight-sample heights (metres above his root): low body point and head point. Seeing either counts as observed.")]
    [SerializeField, Min(0f)] float lowSightSampleHeight = 0.25f;
    [SerializeField, Min(0f)] float highSightSampleHeight = 1.1f;
    [Tooltip("He starts moving only after being unobserved for this long — hides freeze/unfreeze flicker when a player's aim skims the view-cone edge.")]
    [SerializeField, Min(0f)] float unfreezeDelay = 0.25f;

    [Header("Locomotion")]
    [SerializeField, Min(0f)] float sneakSpeed = 2.4f;
    [SerializeField, Min(0f)] float crawlSpeed = 1.4f;
    [Tooltip("Speed while sprinting after a fleeing player (and while fleeing). Player run speed is 4.8, so keep this a touch higher so he slowly closes on a sprinter rather than falling behind.")]
    [SerializeField, Min(0f)] float sprintSpeed = 5.2f;
    [Tooltip("Chance [0,1] that a movement burst uses the Low Crawl instead of a sneak. Re-rolled every time he unfreezes.")]
    [SerializeField, Range(0f, 1f)] float crawlChance = 0.35f;
    [Tooltip("While stalking unobserved, if the target is fleeing faster than this (m/s), he drops the creep and sprints to keep up. Below it he returns to the rolled sneak/crawl.")]
    [SerializeField, Min(0f)] float sprintChaseSpeedThreshold = 3.6f;
    [SerializeField, Min(0f)] float turnSpeedDegreesPerSecond = 540f;
    [SerializeField, Min(0f)] float gravity = 20f;
    [Tooltip("Seconds between NavMesh repaths while stalking/fleeing.")]
    [SerializeField, Min(0.05f)] float repathInterval = 0.3f;
    [Tooltip("Animator int parameter: 0 Idle, 1 SneakL, 2 SneakR, 3 Crawl.")]
    [SerializeField] string locomotionIntParam = "Locomotion";
    [Tooltip("Body-facing yaw offset (degrees) from the travel direction while sneaking LEFT. The Mixamo cover sneaks are strafes — he travels where the HEAD looks, with the body twisted away (~±110°, measured from the clips). 0 would make him moonwalk chest/back-first.")]
    [SerializeField] float sneakLeftFacingYawOffsetDegrees = -110f;
    [Tooltip("Same as above for the RIGHT cover sneak.")]
    [SerializeField] float sneakRightFacingYawOffsetDegrees = 114f;
    [Tooltip("Facing offset for the Sprint run. The run clip faces straight ahead (~0°), so he runs where his chest points.")]
    [SerializeField] float sprintFacingYawOffsetDegrees = 0f;

    [Header("Pounce (placeholder until a grab/scream/throw animation exists)")]
    [Tooltip("Horizontal catch distance while moving unobserved.")]
    [SerializeField, Min(0.1f)] float catchRadius = 1.5f;
    [Tooltip("Extra reach allowed at the impact frame so a target sprinting away right as the pounce starts can still be clipped.")]
    [SerializeField, Min(0f)] float catchReachPadding = 0.5f;
    [Tooltip("Seconds after the pounce starts (jaw open + scream) before the throw lands.")]
    [SerializeField, Min(0f)] float pounceHitDelay = 0.25f;
    [Tooltip("Total pounce length; after this he closes the jaw and flees.")]
    [SerializeField, Min(0.1f)] float pounceDuration = 1.1f;
    [SerializeField, Min(0f)] float pounceDamage = 15f;
    [Tooltip("Horizontal throw speed (matches the Clown hammer scale — ForceMode.VelocityChange).")]
    [SerializeField, Min(0f)] float knockbackForwardSpeed = 13f;
    [SerializeField, Min(0f)] float knockbackUpwardSpeed = 5f;
    [SerializeField] ForceMode knockbackForceMode = ForceMode.VelocityChange;

    [Header("Flee (after a throw, or when the target escapes)")]
    [Tooltip("While stalking, if he can't see his target (and isn't being watched) for this long, he gives up and flees. Set 0 to disable the escape-flee (he'd then only flee after a throw).")]
    [SerializeField, Min(0f)] float loseSightFleeDelay = 2f;
    [Tooltip("Flee runs at sprintSpeed and plays the Sprint clip; this is a fallback only if sprintSpeed is 0.")]
    [SerializeField, Min(0f)] float fleeSpeed = 3.6f;
    [Tooltip("How far away the flee destination is sampled — how far he bolts before settling back to dormant.")]
    [SerializeField, Min(1f)] float fleeDistance = 42f;
    [Tooltip("Candidate NavMesh points sampled; the one farthest from every living player wins. More samples = better odds of a genuinely far, reachable point at long flee distances.")]
    [SerializeField, Range(1, 24)] int fleeSampleCount = 12;
    [Tooltip("Give-up timer: if he hasn't reached the flee point by then he goes Dormant where he stands. Scaled up with fleeDistance so a long flee actually completes instead of timing out early.")]
    [SerializeField, Min(1f)] float fleeTimeoutSeconds = 24f;
    [SerializeField, Min(0.2f)] float fleeArriveRadius = 1.5f;

    [Header("Jaw")]
    [Tooltip("Local euler rotation added to the jaw hinge's rest pose when fully open. Sign/axis depends on how the hinge was authored — tune in the prefab.")]
    [SerializeField] Vector3 jawOpenEuler = new Vector3(-30f, 0f, 0f);
    [SerializeField, Min(0.1f)] float jawLerpSpeed = 14f;

    // ---- replicated state (server writes, everyone reads) ----
    readonly NetworkVariable<byte> _netState = new((byte)RatBotState.Dormant);
    readonly NetworkVariable<bool> _netFrozen = new(false);
    // Exact statue pose: every peer hard-Plays this state at this normalized time, so all players see the
    // identical frame rather than "wherever my local animator happened to be" (NetworkAnimator drift).
    readonly NetworkVariable<int> _netFreezeStateHash = new(0);
    readonly NetworkVariable<float> _netFreezeNormalizedTime = new(0f);
    readonly NetworkVariable<float> _netJawOpen = new(0f);

    ServerNetworkAnimator _serverNetworkAnimator;
    RatBotState _state = RatBotState.Dormant; // authoritative mirror (also drives offline play mode)
    bool _frozen;
    int _locomotion = LocomotionIdle;
    float _lastObservedTime = float.NegativeInfinity;
    float _nextRepathTime;
    float _verticalVelocity;

    // pounce
    PlayerHealth _pounceTargetHealth;
    PlayerRagdollController _pounceTargetRagdoll;
    NetworkPlayerRagdoll _pounceTargetNetRagdoll;
    float _pounceStartTime;
    bool _pounceHitApplied;

    // flee
    Vector3 _fleeDestination;
    float _fleeStartTime;

    // stalking target
    PlayerHealth _stalkTarget;
    int _rolledCreepGait = LocomotionSneakLeft; // the crawl/sneak chosen this burst; sprint overrides it live
    Vector3 _targetLastPos;
    bool _hasTargetLastPos;
    float _targetSmoothedSpeed;
    bool _targetMovingAway;
    float _lastSawTargetTime; // last time he saw (or was seen by) the target; drives the escape-flee

    // jaw visual (mirrored locally so offline play works without NetworkVariables)
    float _jawOpenTarget;
    Quaternion _jawClosedLocalRotation = Quaternion.identity;

    // Clients re-Play the freeze frame for a couple frames after (spawn|freeze) so NetworkAnimator's own
    // state sync can't land a frame later and shift the statue pose.
    int _freezeReapplyFrames;
    int _idleStateHash; // cached "Idle" state hash, used to substitute the idle pose when freezing mid-sprint

    // per-player component caches (server only)
    readonly Dictionary<PlayerHealth, Transform> _eyeByPlayer = new();
    readonly Dictionary<PlayerHealth, PlayerRagdollController> _ragdollByPlayer = new();
    static readonly RaycastHit[] s_SightHits = new RaycastHit[16];

    bool ShouldSimulate =>
        NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || IsServer;

    bool IsNetworkedAuthority =>
        IsSpawned && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>(true);
        if (navMeshAgent == null) navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null) characterController = GetComponent<CharacterController>();
        if (jawHinge == null) jawHinge = FindChildByName(transform, "JawHinge");
        if (screamAudioSource == null) screamAudioSource = GetComponent<AudioSource>();

        if (jawHinge != null)
            _jawClosedLocalRotation = jawHinge.localRotation;

        if (animator != null)
        {
            // Never let renderer culling pause the animator — a paused animator would desync the
            // exact-frame statue pose between peers.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        _idleStateHash = Animator.StringToHash("Idle");

        if (navMeshAgent != null)
        {
            // Zombie/Clown idiom: the agent only computes paths; the CharacterController does the moving.
            navMeshAgent.updatePosition = false;
            navMeshAgent.updateRotation = false;
        }

        if (screamAudioSource != null)
            GameAudioManager.RouteSfxSource(screamAudioSource);

        EnsureAnimationSync();
    }

    void EnsureAnimationSync()
    {
        if (animator == null)
            return;
        _serverNetworkAnimator = animator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = animator.gameObject.AddComponent<ServerNetworkAnimator>();
    }

    public override void OnNetworkSpawn()
    {
        _netState.OnValueChanged += HandleNetStateChanged;
        _netFrozen.OnValueChanged += HandleNetFrozenChanged;
        _netFreezeNormalizedTime.OnValueChanged += HandleNetFreezeTimeChanged;
        _netJawOpen.OnValueChanged += HandleNetJawOpenChanged;

        ApplyAuthorityState();

        // Late joiner: adopt the current replicated pose/jaw without side effects (no scream replay).
        _state = (RatBotState)_netState.Value;
        _jawOpenTarget = _netJawOpen.Value;
        if (_netFrozen.Value)
            ApplyFrozenVisual(true);
    }

    public override void OnNetworkDespawn()
    {
        _netState.OnValueChanged -= HandleNetStateChanged;
        _netFrozen.OnValueChanged -= HandleNetFrozenChanged;
        _netFreezeNormalizedTime.OnValueChanged -= HandleNetFreezeTimeChanged;
        _netJawOpen.OnValueChanged -= HandleNetJawOpenChanged;
    }

    void ApplyAuthorityState()
    {
        bool shouldSimulate = ShouldSimulate;

        if (navMeshAgent != null)
            navMeshAgent.enabled = shouldSimulate;

        if (characterController != null)
            characterController.enabled = shouldSimulate;

        // Observer clients disable the CC (server drives movement) — keep him solid for remote players
        // and client-thrown props via the mirrored kinematic capsule.
        EnemyClientCollisionProxy.Apply(characterController, shouldSimulate);
    }

    // ------------------------------------------------------------------ update loop

    void Update()
    {
        UpdateJawVisual();

        if (!ShouldSimulate)
        {
            ClientReapplyFreezeFrame();
            return;
        }

        switch (_state)
        {
            case RatBotState.Dormant:
                UpdateDormant();
                break;
            case RatBotState.Stalking:
                UpdateStalking();
                break;
            case RatBotState.Pouncing:
                UpdatePouncing();
                break;
            case RatBotState.Fleeing:
                UpdateFleeing();
                break;
        }
    }

    void UpdateDormant()
    {
        ApplyMotion(Vector3.zero); // gravity only, so he settles onto the floor at spawn

        // The first genuine look wakes him. He keeps looping Idle (still reads as a prop) and won't
        // take a single step until everyone looks away — see UpdateStalking.
        if (IsObservedByAnyLivingPlayer())
        {
            _lastObservedTime = Time.time;
            _lastSawTargetTime = Time.time; // arm the escape-flee timer from activation, not from t=0
            SetState(RatBotState.Stalking);
        }
    }

    void UpdateStalking()
    {
        bool observed = IsObservedByAnyLivingPlayer();

        // Keep a valid target and its velocity fresh every frame (both the freeze read below and the
        // sprint/escape-flee logic depend on it).
        if (!IsValidStalkTarget(_stalkTarget))
        {
            _stalkTarget = AcquireStalkTarget();
            ResetTargetTracking(); // also arms the escape-flee timer for the fresh target
        }
        UpdateTargetTracking();

        // He "still has" the target as long as he can see it OR it is looking at him — either way it hasn't
        // escaped. Only when both fail does the lose-sight timer run toward the escape-flee.
        if (_stalkTarget != null && (observed || BotHasLineOfSightTo(_stalkTarget)))
            _lastSawTargetTime = Time.time;

        if (observed)
        {
            _lastObservedTime = Time.time;
            // Caught mid-stride: statue-freeze on this exact frame. If he hasn't moved yet (still on the
            // Idle loop) the idle keeps playing — a frozen "prop" that suddenly stops swaying would be a tell.
            if (!_frozen && _locomotion != LocomotionIdle)
                FreezeNow();
            ApplyMotion(Vector3.zero);
            return;
        }

        if (Time.time - _lastObservedTime < unfreezeDelay)
        {
            ApplyMotion(Vector3.zero);
            return;
        }

        // Unobserved long enough: this is the start of (or return to) a movement burst. Roll the CREEP gait
        // for this burst; a live sprint override (below) can supersede it while the target is fleeing.
        if (_frozen)
        {
            Unfreeze();
            _rolledCreepGait = RollStalkLocomotion(); // re-roll crawl vs sneak every burst
        }
        else if (_locomotion == LocomotionIdle)
        {
            _rolledCreepGait = RollStalkLocomotion(); // very first step out of the dormant idle
        }

        if (_stalkTarget == null)
        {
            // Nobody upright to hunt (all dead/ragdolled): stand down visually but stay armed.
            if (_locomotion != LocomotionIdle)
                SetLocomotion(LocomotionIdle);
            ApplyMotion(Vector3.zero);
            return;
        }

        // Target escaped: no line of sight to it (and it isn't watching him) for the delay → give up and flee.
        if (loseSightFleeDelay > 0f && Time.time - _lastSawTargetTime >= loseSightFleeDelay)
        {
            BeginFlee();
            return;
        }

        // Fleeing target → drop the creep and sprint after it; otherwise use the rolled sneak/crawl.
        bool chase = _targetSmoothedSpeed >= sprintChaseSpeedThreshold && _targetMovingAway;
        int desiredLoco = chase ? LocomotionSprint : _rolledCreepGait;
        if (_locomotion != desiredLoco)
            SetLocomotion(desiredLoco);

        if (navMeshAgent != null && navMeshAgent.enabled && Time.time >= _nextRepathTime)
        {
            _nextRepathTime = Time.time + repathInterval;
            navMeshAgent.SetDestination(_stalkTarget.transform.position);
        }

        MoveAlongPath(CurrentLocomotionSpeed());

        // Catch: he reached someone while nobody was watching.
        Vector3 flatToTarget = _stalkTarget.transform.position - transform.position;
        flatToTarget.y = 0f;
        if (flatToTarget.magnitude <= catchRadius)
            StartPounce(_stalkTarget);
    }

    void UpdateTargetTracking()
    {
        if (_stalkTarget == null)
        {
            _hasTargetLastPos = false;
            _targetSmoothedSpeed = 0f;
            _targetMovingAway = false;
            return;
        }

        Vector3 pos = _stalkTarget.transform.position;
        if (_hasTargetLastPos && Time.deltaTime > 1e-5f)
        {
            Vector3 delta = pos - _targetLastPos;
            delta.y = 0f;
            float instant = delta.magnitude / Time.deltaTime;
            _targetSmoothedSpeed = Mathf.Lerp(_targetSmoothedSpeed, instant, 1f - Mathf.Exp(-8f * Time.deltaTime));

            Vector3 awayDir = pos - transform.position;
            awayDir.y = 0f;
            _targetMovingAway = delta.sqrMagnitude > 1e-6f && awayDir.sqrMagnitude > 1e-4f
                && Vector3.Dot(delta.normalized, awayDir.normalized) > 0.1f;
        }
        _targetLastPos = pos;
        _hasTargetLastPos = true;
    }

    void ResetTargetTracking()
    {
        _hasTargetLastPos = false;
        _targetSmoothedSpeed = 0f;
        _targetMovingAway = false;
        _lastSawTargetTime = Time.time; // don't let a freshly acquired target instantly trip the escape-flee
    }

    void UpdatePouncing()
    {
        ApplyMotion(Vector3.zero);

        if (!_pounceHitApplied && Time.time >= _pounceStartTime + pounceHitDelay)
        {
            _pounceHitApplied = true;
            TryApplyPounceHit();
        }

        if (Time.time >= _pounceStartTime + pounceDuration)
        {
            SetJawOpen(0f);
            Unfreeze();
            BeginFlee();
        }
    }

    void UpdateFleeing()
    {
        // Deliberately ignores observation — after a throw he books it even while being watched,
        // then goes fully dormant at the far point (re-armable by sight).
        bool arrived;
        Vector3 flat = _fleeDestination - transform.position;
        flat.y = 0f;
        arrived = flat.magnitude <= fleeArriveRadius;

        if (arrived || Time.time - _fleeStartTime > fleeTimeoutSeconds)
        {
            GoDormant();
            return;
        }

        if (navMeshAgent != null && navMeshAgent.enabled && Time.time >= _nextRepathTime)
        {
            _nextRepathTime = Time.time + repathInterval;
            navMeshAgent.SetDestination(_fleeDestination);
        }

        MoveAlongPath(sprintSpeed > 0f ? sprintSpeed : fleeSpeed);
    }

    // ------------------------------------------------------------------ state transitions (server)

    void SetState(RatBotState newState)
    {
        if (_state == newState)
            return;
        _state = newState;

        if (IsNetworkedAuthority)
            _netState.Value = (byte)newState; // OnValueChanged fires on server + clients → ApplyStateVisual
        else
            ApplyStateVisual(newState);       // offline play mode
    }

    void StartPounce(PlayerHealth target)
    {
        _pounceTargetHealth = target;
        _pounceTargetRagdoll = target != null ? target.GetComponent<PlayerRagdollController>() : null;
        _pounceTargetNetRagdoll = target != null ? target.GetComponent<NetworkPlayerRagdoll>() : null;
        _pounceStartTime = Time.time;
        _pounceHitApplied = false;

        if (target != null)
            FaceInstant(target.transform.position);

        // Placeholder attack visual: statue-freeze mid-stride with the jaw snapping open + scream.
        // When a real grab/lift/throw clip exists, replace this freeze with the attack state and keep
        // the same timing hooks (pounceHitDelay → throw, pounceDuration → flee).
        FreezeNow();
        SetJawOpen(1f);
        SetState(RatBotState.Pouncing);
    }

    void TryApplyPounceHit()
    {
        if (_pounceTargetHealth == null || _pounceTargetHealth.IsDead)
            return;

        // Clean dodge whiffs: only connect if they're still within reach at the impact frame (Clown idiom).
        Vector3 flatToTarget = _pounceTargetHealth.transform.position - transform.position;
        flatToTarget.y = 0f;
        if (flatToTarget.magnitude > catchRadius + catchReachPadding)
            return;

        Vector3 knockDir = flatToTarget;
        if (knockDir.sqrMagnitude > 1e-4f)
            knockDir.Normalize();
        else
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            knockDir = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
        }

        Vector3 hitPoint = _pounceTargetHealth.transform.position + Vector3.up;
        Vector3 force = (knockDir * knockbackForwardSpeed) + (Vector3.up * knockbackUpwardSpeed);

        bool inNetSession = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        if (inNetSession && _pounceTargetNetRagdoll != null)
        {
            _pounceTargetNetRagdoll.RequestTrapHitFromServer(force, hitPoint, pounceDamage, knockbackForceMode);
        }
        // No networked ragdoll on the target: local damage + ragdoll path (matches ClownAI/RagdollTrap).
        else if (_pounceTargetRagdoll != null)
        {
            bool survived = true;
            if (pounceDamage > 0f)
            {
                _pounceTargetHealth.TakeDamage(pounceDamage);
                survived = !_pounceTargetHealth.IsDead;
            }
            _pounceTargetRagdoll.ActivateRagdoll(force, hitPoint, knockbackForceMode, allowAutoRecovery: survived);
        }
    }

    void BeginFlee()
    {
        _pounceTargetHealth = null;
        _pounceTargetRagdoll = null;
        _pounceTargetNetRagdoll = null;
        _stalkTarget = null;

        _fleeDestination = PickFleeDestination();
        _fleeStartTime = Time.time;
        _nextRepathTime = 0f;
        SetLocomotion(LocomotionSprint); // sprint away
        SetState(RatBotState.Fleeing);
    }

    void GoDormant()
    {
        SetLocomotion(LocomotionIdle);
        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.ResetPath();
        _lastObservedTime = float.NegativeInfinity;
        SetState(RatBotState.Dormant);
    }

    Vector3 PickFleeDestination()
    {
        var players = PlayerHealthRegistry.All;

        // Try the full flee distance first; if the maze geometry starves that ring of reachable points
        // (small maze, walls, out-of-bounds straight-line samples), fall back to progressively nearer rings
        // so he always bolts genuinely far rather than snapping back to his own position → instant dormant.
        foreach (float ring in new[] { fleeDistance, fleeDistance * 0.65f, fleeDistance * 0.4f })
        {
            Vector3 ringBest = transform.position;
            float ringScore = float.NegativeInfinity;
            bool ringFound = false;

            for (int i = 0; i < fleeSampleCount; i++)
            {
                Vector2 dir2 = Random.insideUnitCircle.normalized;
                Vector3 candidate = transform.position + new Vector3(dir2.x, 0f, dir2.y) * ring;
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 8f, NavMesh.AllAreas))
                    continue;

                // Reject points that snapped back toward him (dead ring / wall) — those aren't a real flee.
                if ((hit.position - transform.position).sqrMagnitude < (ring * 0.5f) * (ring * 0.5f))
                    continue;

                // Among the genuinely-far points this ring, the one farthest from every living player wins.
                float score = float.PositiveInfinity;
                for (int p = 0; p < players.Count; p++)
                {
                    PlayerHealth player = players[p];
                    if (player == null || player.IsDead)
                        continue;
                    score = Mathf.Min(score, (player.transform.position - hit.position).sqrMagnitude);
                }

                if (score > ringScore)
                {
                    ringScore = score;
                    ringBest = hit.position;
                    ringFound = true;
                }
            }

            if (ringFound)
                return ringBest;
        }

        return transform.position; // nowhere far to go — GoDormant will fire immediately (rare, tiny mazes)
    }

    // ------------------------------------------------------------------ freeze (statue) handling

    void FreezeNow()
    {
        if (_frozen || animator == null)
            return;
        _frozen = true;

        // A frozen mid-run frame reads badly (mid-stride lean, one foot in the air). If he's caught while
        // sprinting, snap to the neutral Idle stance for the statue instead of the live run frame. The creep
        // gaits (sneak/crawl) still freeze exactly where they were — those poses read fine.
        if (_locomotion == LocomotionSprint)
        {
            SetLocomotion(LocomotionIdle);       // param -> Idle (replicates) so nothing re-triggers Sprint
            animator.Play(_idleStateHash, 0, 0f); // snap straight to Idle, no blend
            animator.Update(0f);                  // apply this frame so the captured state IS Idle
        }

        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
        float normalizedTime = info.normalizedTime % 1f;
        if (normalizedTime < 0f)
            normalizedTime += 1f;

        animator.speed = 0f;
        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.isStopped = true;

        if (IsNetworkedAuthority)
        {
            _netFreezeStateHash.Value = info.fullPathHash;
            _netFreezeNormalizedTime.Value = normalizedTime;
            _netFrozen.Value = true;
        }
    }

    void Unfreeze()
    {
        if (!_frozen)
            return;
        _frozen = false;

        if (animator != null)
            animator.speed = 1f;
        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.isStopped = false;

        if (IsNetworkedAuthority)
            _netFrozen.Value = false;
    }

    void HandleNetFrozenChanged(bool previousValue, bool currentValue) => ApplyFrozenVisual(currentValue);

    void HandleNetFreezeTimeChanged(float previousValue, float currentValue)
    {
        // Freeze pose updated while already frozen (unfreeze+refreeze inside one tick): re-snap.
        if (_netFrozen.Value)
            ApplyFrozenVisual(true);
    }

    void ApplyFrozenVisual(bool frozen)
    {
        if (animator == null)
            return;

        if (frozen)
        {
            // The server's animator is already sitting on this exact frame; clients hard-snap to it.
            if (!IsServer)
            {
                animator.Play(_netFreezeStateHash.Value, 0, _netFreezeNormalizedTime.Value);
                _freezeReapplyFrames = 2;
            }
            animator.speed = 0f;
        }
        else
        {
            animator.speed = 1f;
        }
    }

    void ClientReapplyFreezeFrame()
    {
        // NetworkAnimator's own sync (initial or in-flight) can land a frame after our snap and nudge the
        // statue off-pose; re-assert the exact frame for a couple of frames after every snap.
        if (_freezeReapplyFrames <= 0 || !_netFrozen.Value || animator == null)
            return;
        _freezeReapplyFrames--;
        animator.Play(_netFreezeStateHash.Value, 0, _netFreezeNormalizedTime.Value);
        animator.speed = 0f;
    }

    // ------------------------------------------------------------------ observation (server)

    bool IsObservedByAnyLivingPlayer()
    {
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || player.IsDead)
                continue;

            Transform eye = ResolveEye(player);
            Vector3 eyePosition;
            Vector3 eyeForward;
            if (eye != null)
            {
                eyePosition = eye.position;
                eyeForward = eye.forward;
            }
            else
            {
                eyePosition = player.transform.position + Vector3.up * 1.6f;
                eyeForward = player.transform.forward;
            }

            if ((transform.position - eyePosition).sqrMagnitude > maxObserveDistance * maxObserveDistance)
                continue;

            if (CanSeePoint(eyePosition, eyeForward, transform.position + Vector3.up * lowSightSampleHeight, player)
                || CanSeePoint(eyePosition, eyeForward, transform.position + Vector3.up * highSightSampleHeight, player))
                return true;
        }

        return false;
    }

    bool CanSeePoint(Vector3 eyePosition, Vector3 eyeForward, Vector3 samplePoint, PlayerHealth player)
    {
        Vector3 toSample = samplePoint - eyePosition;
        float distance = toSample.magnitude;
        if (distance <= 0.5f)
            return true; // standing on top of him — always "seen"

        Vector3 direction = toSample / distance;
        if (Vector3.Angle(eyeForward, direction) > viewConeHalfAngleDegrees)
            return false;

        return HasClearSightLine(eyePosition, direction, distance, player);
    }

    bool HasClearSightLine(Vector3 origin, Vector3 direction, float distance, PlayerHealth player)
    {
        int mask = lineOfSightMask.value == 0 ? Physics.DefaultRaycastLayers : lineOfSightMask.value;
        int hitCount = Physics.RaycastNonAlloc(origin, direction, s_SightHits, distance, mask, QueryTriggerInteraction.Ignore);

        // A hit on this RatBot IS the sight line landing; anything else (walls, props, another player)
        // closer than him blocks it. Hits on the looking player itself are ignored (ray starts at their eye).
        float nearestBlocker = float.PositiveInfinity;
        float nearestSelf = float.PositiveInfinity;
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = s_SightHits[i];
            s_SightHits[i] = default;
            if (hit.transform == null)
                continue;
            if (player != null && (hit.transform == player.transform || hit.transform.IsChildOf(player.transform)))
                continue;

            if (hit.transform == transform || hit.transform.IsChildOf(transform))
                nearestSelf = Mathf.Min(nearestSelf, hit.distance);
            else
                nearestBlocker = Mathf.Min(nearestBlocker, hit.distance);
        }

        if (float.IsPositiveInfinity(nearestBlocker))
            return true; // nothing in the way at all
        return nearestSelf < nearestBlocker;
    }

    /// <summary>
    /// Does the RatBot have a clear line to the target (wall-blocked = no)? This is the BOT→player direction,
    /// separate from the player→bot observation check: no view cone (he's an omnidirectional stalker), just a
    /// raw sight line from his head to the target's low and high points. Drives the escape-flee timer.
    /// </summary>
    bool BotHasLineOfSightTo(PlayerHealth target)
    {
        if (target == null)
            return false;
        Vector3 origin = transform.position + Vector3.up * highSightSampleHeight;
        return BotSightClearTo(origin, target, 0.3f) || BotSightClearTo(origin, target, 1.4f);
    }

    bool BotSightClearTo(Vector3 origin, PlayerHealth target, float targetHeight)
    {
        Vector3 point = target.transform.position + Vector3.up * targetHeight;
        Vector3 to = point - origin;
        float distance = to.magnitude;
        if (distance <= 0.5f)
            return true;

        Vector3 direction = to / distance;
        int mask = lineOfSightMask.value == 0 ? Physics.DefaultRaycastLayers : lineOfSightMask.value;
        int hitCount = Physics.RaycastNonAlloc(origin, direction, s_SightHits, distance, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = s_SightHits[i];
            s_SightHits[i] = default;
            if (hit.transform == null)
                continue;
            if (hit.transform == transform || hit.transform.IsChildOf(transform))
                continue; // his own colliders at the ray origin
            if (hit.transform == target.transform || hit.transform.IsChildOf(target.transform))
                continue; // reaching the target is success, not a block
            return false;  // a wall/prop/other player sits between them
        }
        return true;
    }

    Transform ResolveEye(PlayerHealth player)
    {
        if (_eyeByPlayer.TryGetValue(player, out Transform cached) && cached != null)
            return cached;

        // The camera pitch node: local rotation carries the replicated look pitch on non-owner peers
        // (NetworkPlayerAvatar), parent chain carries the replicated body yaw — so on the server its
        // world forward is the player's true view direction.
        PlayerController controller = player.GetComponent<PlayerController>();
        Transform eye = controller != null ? controller.LookPitchTransform : null;
        if (eye == null)
            eye = FindChildByName(player.transform, "CameraPitch");

        _eyeByPlayer[player] = eye;
        return eye;
    }

    // ------------------------------------------------------------------ targeting (server)

    bool IsValidStalkTarget(PlayerHealth target)
    {
        if (target == null || target.IsDead)
            return false;
        PlayerRagdollController ragdoll = ResolveRagdoll(target);
        return ragdoll == null || (!ragdoll.IsRagdolled && !ragdoll.IsHeld);
    }

    PlayerHealth AcquireStalkTarget()
    {
        PlayerHealth best = null;
        float bestSqr = float.PositiveInfinity;
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (!IsValidStalkTarget(player))
                continue;
            float sqr = (player.transform.position - transform.position).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = player;
            }
        }

        return best;
    }

    PlayerRagdollController ResolveRagdoll(PlayerHealth player)
    {
        if (_ragdollByPlayer.TryGetValue(player, out PlayerRagdollController cached) && cached != null)
            return cached;
        PlayerRagdollController ragdoll = player.GetComponent<PlayerRagdollController>();
        _ragdollByPlayer[player] = ragdoll;
        return ragdoll;
    }

    // ------------------------------------------------------------------ movement (server)

    void MoveAlongPath(float speed)
    {
        if (navMeshAgent == null || !navMeshAgent.enabled)
        {
            ApplyMotion(Vector3.zero);
            return;
        }

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = speed;

        Vector3 desired = navMeshAgent.desiredVelocity;
        desired.y = 0f;
        if (desired.sqrMagnitude > 0.01f)
        {
            desired = desired.normalized * speed;
            RotateToward(desired);
        }
        else
        {
            desired = Vector3.zero;
        }

        ApplyMotion(desired);
    }

    void ApplyMotion(Vector3 desiredHorizontalVelocity)
    {
        if (characterController == null || !characterController.enabled)
            return;

        if (characterController.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -2f;
        _verticalVelocity -= gravity * Time.deltaTime;

        Vector3 motion = desiredHorizontalVelocity * Time.deltaTime;
        motion.y = _verticalVelocity * Time.deltaTime;
        characterController.Move(motion);

        // Keep the path-only agent glued to the body (zombie idiom).
        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.nextPosition = transform.position;
    }

    void RotateToward(Vector3 horizontalDirection)
    {
        horizontalDirection.y = 0f;
        if (horizontalDirection.sqrMagnitude < 1e-4f)
            return;
        // The sneak clips are strafes: he travels where the head looks, body twisted off-axis. Offset the
        // body facing so the velocity lines up with the head-look direction instead of the chest.
        Quaternion targetRotation = Quaternion.LookRotation(horizontalDirection.normalized, Vector3.up)
            * Quaternion.Euler(0f, CurrentFacingYawOffset(), 0f);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeedDegreesPerSecond * Time.deltaTime);
    }

    float CurrentFacingYawOffset()
    {
        switch (_locomotion)
        {
            case LocomotionSneakLeft: return sneakLeftFacingYawOffsetDegrees;
            case LocomotionSneakRight: return sneakRightFacingYawOffsetDegrees;
            case LocomotionSprint: return sprintFacingYawOffsetDegrees;
            default: return 0f; // crawl/idle clips face straight down the root forward
        }
    }

    float CurrentLocomotionSpeed()
    {
        switch (_locomotion)
        {
            case LocomotionCrawl: return crawlSpeed;
            case LocomotionSprint: return sprintSpeed;
            default: return sneakSpeed; // sneak L/R
        }
    }

    void FaceInstant(Vector3 worldPoint)
    {
        Vector3 flat = worldPoint - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude > 1e-4f)
            transform.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
    }

    // ------------------------------------------------------------------ locomotion / visuals

    int RollStalkLocomotion()
    {
        if (Random.value < crawlChance)
            return LocomotionCrawl;
        return Random.value < 0.5f ? LocomotionSneakLeft : LocomotionSneakRight;
    }

    void SetLocomotion(int locomotion)
    {
        _locomotion = locomotion;
        // The int parameter replicates through ServerNetworkAnimator, so clients transition too.
        if (animator != null && !string.IsNullOrEmpty(locomotionIntParam))
            animator.SetInteger(locomotionIntParam, locomotion);
    }

    void SetJawOpen(float openAmount)
    {
        openAmount = Mathf.Clamp01(openAmount);
        _jawOpenTarget = openAmount;
        if (IsNetworkedAuthority)
            _netJawOpen.Value = openAmount;
    }

    void HandleNetJawOpenChanged(float previousValue, float currentValue) => _jawOpenTarget = currentValue;

    void HandleNetStateChanged(byte previousValue, byte currentValue)
    {
        _state = (RatBotState)currentValue;
        ApplyStateVisual(_state);
    }

    void ApplyStateVisual(RatBotState state)
    {
        // Only side effect a state *change* carries for every peer: the pounce scream.
        if (state == RatBotState.Pouncing)
            PlayScream();
    }

    void PlayScream()
    {
        if (screamAudioSource != null && screamClip != null)
            screamAudioSource.PlayOneShot(screamClip);
    }

    void UpdateJawVisual()
    {
        if (jawHinge == null)
            return;
        Quaternion target = _jawClosedLocalRotation * Quaternion.Euler(jawOpenEuler * _jawOpenTarget);
        jawHinge.localRotation = Quaternion.Slerp(jawHinge.localRotation, target, 1f - Mathf.Exp(-jawLerpSpeed * Time.deltaTime));
    }

    static Transform FindChildByName(Transform root, string childName)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            if (transforms[i] != null && transforms[i].name == childName)
                return transforms[i];
        }

        return null;
    }
}
