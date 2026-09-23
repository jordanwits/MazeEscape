using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The Mutant — Level03's final boss. A too-tall clown in a pinstripe suit who waits in the dark exit hall in
/// front of the elevator. Opening the hall door (with him in sight) wakes him: the lights stutter, he bows
/// politely, his head snaps up, and the hunt begins. The exit elevator stays locked until he is dead.
///
/// Nothing about him moves like a person:
///   • <b>Long Reach</b> — the arm cocks and trembles, then fires out up to ~9 m. Whoever it catches is pinned to
///     his hand (the Clown/RatBot held-ragdoll path), hauled in as the limb reels back, swung overhead and
///     slammed into the floor. A teammate who breaks his poise mid-reel drops the victim.
///   • <b>Scythe Sweep</b> — both arms stretch sideways at chest height and he spins; crouch under it. Walls stop
///     the limbs, so in a corridor it reaches furthest along the corridor.
///   • <b>Lights Out</b> — every light around him and around where he's going dies for under a second; when they
///     stutter back he's standing behind someone.
///   • <b>Skitter</b> — at range he drops onto all fours and outruns a sprinting player.
///   • <b>Claw</b> — point-blank swipe so standing next to him is never safe.
///   • <b>Shriek</b> — at half health: neck stretches, head swells, the blast throws everyone back and blows out
///     the nearest lights for good. Phase two is faster, twitchier, and spins further.
/// Passive wrongness (owl neck, twitching, the deeper bow) lives in <see cref="MutantLimbRig"/>; the lights in
/// <see cref="MutantLightDisturbance"/>.
///
/// Server-authoritative like <see cref="SecurityGuardAI"/> (CharacterController + NavMeshAgent path following);
/// clients keep this enabled for presentation only — audio, footsteps and the procedural timelines that
/// <see cref="NetworkMutantAvatar"/> fans out.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(MutantLimbRig))]
public class MutantAI : MonoBehaviour, IBlindableEnemy
{
    public enum SfxKind : byte { Laugh, ReachCatch, SlamImpact, ClawHit, SweepHit, Wake }

    const string FootstepAudioChildName = "Mutant_Footsteps";
    const string FxAudioChildName = "Mutant_Fx";
    const string VoiceAudioChildName = "Mutant_Voice";
    const string BreathAudioChildName = "Mutant_Breath";
    const int RightHandGrabBoneIndex = 0; // NetworkPlayerRagdoll s_GrabBoneNames[0] = "mixamorig:RightHand"

    static readonly List<MutantAI> s_Registry = new();

    /// <summary>Every Mutant in the scene (any peer). Read by <see cref="MutantLightDisturbance"/>.</summary>
    public static IReadOnlyList<MutantAI> Registry => s_Registry;

    enum State { Dormant, Waking, Hunt, Ability, Stagger, Dead }
    enum Ability { None, Reach, Sweep, Claw, Blink, Shriek }

    [Header("References")]
    [SerializeField] Animator animator;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [SerializeField] MutantLimbRig limbRig;
    [SerializeField] MutantHealth health;

    [Header("Waiting / waking")]
    [Tooltip("With the hall door open, any player he can see within this range wakes him.")]
    [SerializeField, Min(1f)] float wakeSightDistance = 40f;
    [Tooltip("Anyone this close wakes him whether the door is open or not.")]
    [SerializeField, Min(0f)] float wakeProximity = 9f;
    [Tooltip("While he waits, his head follows players he can see within this range — the body never moves.")]
    [SerializeField, Min(0f)] float dormantWatchDistance = 34f;
    [SerializeField, Min(0f)] float wakeStutterRadius = 30f;
    [SerializeField, Min(0f)] float wakeStutterSeconds = 1.6f;
    [Tooltip("Length of the Bow state he performs on waking (the clip is 2.73 s).")]
    [SerializeField, Min(0f)] float bowSeconds = 2.73f;
    [Tooltip("Chance he opens the fight with Lights Out when the players are still far down the hall.")]
    [SerializeField, Range(0f, 1f)] float openWithBlinkChance = 0.7f;

    [Header("Movement")]
    [Tooltip("The creeping walk. Slow on purpose — he closes distance with the skitter, the reach and the blink.")]
    [SerializeField, Min(0.1f)] float creepSpeed = 1.35f;
    [Tooltip("All-fours skitter. Faster than a sprinting player (4.8).")]
    [SerializeField, Min(0.1f)] float crawlSpeed = 5.4f;
    [SerializeField, Min(1f)] float phaseTwoSpeedMultiplier = 1.2f;
    [Tooltip("Beyond this he drops onto all fours...")]
    [SerializeField, Min(0f)] float crawlEnterDistance = 9f;
    [Tooltip("...and rises again inside this.")]
    [SerializeField, Min(0f)] float crawlExitDistance = 4.8f;
    [SerializeField] float rotationSpeed = 360f;
    [SerializeField] float crawlRotationSpeed = 620f;
    [SerializeField] float gravity = -20f;
    [SerializeField] float groundedStickDown = 2f;
    [SerializeField] float targetNavMeshSampleRadius = 3f;
    [SerializeField, Min(0.05f)] float repathInterval = 0.2f;
    [Tooltip("While his target is down (ragdolled, held or getting up) he looms this far from the BODY, never over it.")]
    [SerializeField, Min(0.5f)] float downedLoomDistance = 1.8f;

    [Header("Animator")]
    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string strideRateParameter = "StrideRate";
    [SerializeField] string crawlingParameter = "Crawling";
    [Tooltip("Ground speed the in-place clips were authored for, measured from planted-foot travel on this rig: " +
             "CreepWalk 0.45, RunningCrawl 2.9 m/s. Re-measure if a clip is swapped.")]
    [SerializeField, Min(0.05f)] float walkClipGroundSpeed = 0.45f;
    [SerializeField, Min(0.05f)] float crawlClipGroundSpeed = 2.9f;
    [SerializeField] Vector2 walkStrideRange = new Vector2(0.5f, 4f);
    [SerializeField] Vector2 crawlStrideRange = new Vector2(0.6f, 2.5f);

    [Header("Targeting")]
    [SerializeField, Min(0.1f)] float retargetInterval = 1f;
    [Tooltip("He switches prey only when someone else is this much closer (fraction of the current distance).")]
    [SerializeField, Range(0.1f, 1f)] float retargetAdvantage = 0.7f;
    [SerializeField] float eyeHeight = 1.8f;
    [SerializeField] float chestHeight = 1.25f;
    [SerializeField] float crouchedChestHeight = 0.75f;
    [SerializeField] LayerMask sightMask = Physics.DefaultRaycastLayers;
    [Tooltip("Seconds between ability decisions — keeps the random rolls from firing every frame.")]
    [SerializeField, Min(0.05f)] float thinkInterval = 0.25f;
    [SerializeField, Min(0f)] float globalAbilityCooldown = 0.6f;
    [SerializeField, Range(0.2f, 1f)] float phaseTwoCooldownMultiplier = 0.65f;

    [Header("Long Reach")]
    [SerializeField] float reachMinRange = 3.2f;
    [SerializeField] float reachMaxRange = 9.5f;
    [Tooltip("The cocked, trembling arm — the tell. Side-step in the last Commit Lead seconds to dodge.")]
    [SerializeField, Min(0.05f)] float reachWindupSeconds = 0.55f;
    [SerializeField, Min(0f)] float reachCommitLead = 0.15f;
    [SerializeField, Min(0.02f)] float reachExtendSeconds = 0.16f;
    [Tooltip("How far past the aim point the lunge carries (it can catch someone behind the target).")]
    [SerializeField, Min(0f)] float reachOvershoot = 1.5f;
    [SerializeField, Min(0.05f)] float reachCatchRadius = 0.5f;
    [SerializeField, Min(0f)] float reachMissHoldSeconds = 0.12f;
    [SerializeField, Min(0.05f)] float reachRetractSeconds = 0.3f;
    [SerializeField, Min(0.05f)] float reachReelSeconds = 0.6f;
    [SerializeField, Min(0f)] float reachCooldown = 6.5f;
    [Tooltip("Victim's hips relative to his right-hand bone while held.")]
    [SerializeField] Vector3 holdLocalOffset = new Vector3(0f, 0.12f, 0f);
    [SerializeField] Vector3 holdLocalEuler = Vector3.zero;
    [SerializeField, Min(0f)] float holdApproachSeconds = 0.06f;
    [Tooltip("He lets go at least this far out from his own axis, so the body lands clear of his capsule.")]
    [SerializeField, Min(0f)] float releaseMinDistance = 1f;
    [Tooltip("Seconds into the Slam state when the hands hit the floor (clip bottom is 1.73 s).")]
    [SerializeField, Min(0f)] float slamImpactTime = 1.62f;
    [SerializeField, Min(0f)] float slamDamage = 26f;
    [SerializeField, Min(0f)] float slamRecoverySeconds = 0.9f;
    [SerializeField] float slamDownSpeed = 9f;
    [SerializeField] float slamForwardSpeed = 3f;
    [SerializeField] ForceMode slamForceMode = ForceMode.VelocityChange;

    [Header("Scythe Sweep")]
    [SerializeField, Min(0.5f)] float sweepArmLength = 5f;
    [Tooltip("Arms rise and creak outward — the tell to stop and crouch (crouching only works standing still).")]
    [SerializeField, Min(0.1f)] float sweepWindupSeconds = 0.75f;
    [SerializeField, Min(0.1f)] float sweepSpinSeconds = 0.75f;
    [SerializeField] float sweepSpinDegrees = 360f;
    [SerializeField] float phaseTwoSweepDegrees = 540f;
    [SerializeField, Min(0f)] float sweepHoldSeconds = 0.1f;
    [SerializeField, Min(0.05f)] float sweepRetractSeconds = 0.35f;
    [SerializeField, Min(0f)] float sweepDamage = 18f;
    [SerializeField] float sweepKnockSpeed = 8f;
    [SerializeField] float sweepKnockUpSpeed = 3.5f;
    [SerializeField, Min(0f)] float sweepCooldown = 9f;
    [Tooltip("He sweeps when two or more players are within this range (or, sometimes, just his target).")]
    [SerializeField, Min(0.5f)] float sweepTriggerRadius = 4.4f;
    [SerializeField, Range(0f, 1f)] float sweepSoloChance = 0.3f;
    [SerializeField, Min(0f)] float sweepVerticalTolerance = 1.2f;

    [Header("Claw")]
    [SerializeField] float clawRange = 2.3f;
    [SerializeField] float clawStartPadding = 0.3f;
    [Tooltip("Seconds from the Claw state's start to the hit (the state plays at 1.35x: clip hit 1.0 s → 0.74 s).")]
    [SerializeField, Min(0f)] float clawHitTime = 0.74f;
    [SerializeField, Min(0f)] float clawDamage = 14f;
    [SerializeField, Min(0f)] float clawRecovery = 0.55f;
    [SerializeField, Range(0f, 180f)] float clawHitHalfAngle = 70f;
    [SerializeField] float clawDriveSpeed = 2.4f;
    [SerializeField, Min(0f)] float clawCooldown = 1.4f;
    [SerializeField] float clawPushSpeed = 6.5f;
    [SerializeField] float clawPushUpSpeed = 2.5f;
    [SerializeField] float clawPushLockSeconds = 0.3f;
    [Tooltip("When the player he's after gets back up (from his slam, a sweep, a trap) he waits this long before his " +
             "next attack on them — a beat of eye contact instead of a claw landing the instant they stand.")]
    [SerializeField, Min(0f)] float riseGraceSeconds = 0.6f;

    [Header("Lights Out (blink)")]
    [SerializeField, Min(0f)] float blinkMinDistance = 11f;
    [Tooltip("Out of sight this long and he stops walking round and simply arrives.")]
    [SerializeField, Min(0f)] float blinkBlindSightSeconds = 1.6f;
    [SerializeField, Min(0f)] float blinkCooldown = 16f;
    [SerializeField, Min(0f)] float phaseTwoBlinkCooldown = 10f;
    [SerializeField, Min(0.5f)] float blinkBehindDistance = 2.3f;
    [SerializeField, Min(1f)] float blinkLightRadius = 20f;
    [SerializeField, Min(0.01f)] float blinkDarkIn = 0.12f;
    [SerializeField, Min(0.05f)] float blinkHold = 0.75f;
    [SerializeField, Min(0.05f)] float blinkRecover = 0.5f;
    [Tooltip("Seconds after the lights are fully out when he moves.")]
    [SerializeField, Min(0f)] float blinkTeleportDelay = 0.3f;

    [Header("Shriek (phase two)")]
    [SerializeField, Range(0f, 1f)] float phaseTwoHealthFraction = 0.5f;
    [SerializeField, Min(0f)] float shriekImpactTime = 1.05f;
    [Tooltip("Length of the Scream state (clip 2.8 s).")]
    [SerializeField, Min(0f)] float shriekDuration = 2.75f;
    [SerializeField, Min(0f)] float shriekRadius = 9f;
    [SerializeField, Min(0f)] float shriekDamage = 6f;
    [SerializeField] float shriekPushSpeed = 9f;
    [SerializeField] float shriekPushUpSpeed = 3.5f;
    [SerializeField] float shriekPushLockSeconds = 0.55f;
    [SerializeField, Min(0f)] float shriekLightRadius = 14f;
    [SerializeField, Min(0)] int shriekMaxBlownLights = 3;
    [SerializeField, Min(0f)] float shriekShakeSeconds = 0.9f;

    [Header("Poise / stagger")]
    [SerializeField, Min(1f)] float maxPoise = 100f;
    [Tooltip("Player melee chips this per hit: ~5 punches break him.")]
    [SerializeField, Min(0f)] float meleePoiseDamage = 22f;
    [SerializeField, Min(0f)] float poiseRegenDelay = 2.5f;
    [SerializeField, Min(0f)] float poiseRegenPerSecond = 20f;
    [SerializeField, Min(0.1f)] float staggerSeconds = 1.5f;
    [SerializeField, Min(0f)] float staggerImmunitySeconds = 8f;

    [Header("Presence")]
    [SerializeField, Min(0f)] float lightFlickerRadius = 10f;
    [SerializeField, Min(0f)] float phaseTwoLightFlickerRadius = 14f;
    [SerializeField, Min(0f)] float twitchIntensity = 1f;
    [SerializeField, Min(0f)] float phaseTwoTwitchIntensity = 1.8f;

    [Header("Doors")]
    [SerializeField] bool canOpenDoors = true;
    [SerializeField, Min(0.2f)] float doorProbeDistance = 1.5f;
    [SerializeField, Min(0f)] float doorLookAheadSeconds = 0.55f;
    [SerializeField, Min(0.05f)] float doorProbeRadius = 0.35f;
    [SerializeField, Min(0f)] float doorProbeInterval = 0.25f;
    [SerializeField, Min(0f)] float doorSwingClearance = 0.25f;
    [SerializeField, Min(0.1f)] float doorBackOffSpeed = 2f;
    [SerializeField, Min(0f)] float doorBackOffMaxSeconds = 1.25f;

    [Header("Footsteps")]
    [Tooltip("The creeping walk is a shuffle — the toes never leave the floor by more than 2-7 cm — so its steps " +
             "are keyed to the cycle instead of foot height: the normalized Walk-state phases where each foot " +
             "plants (its forward motion reverses). Measured on CreepWalk: right 0.00, left 0.51.")]
    [SerializeField] float[] walkStepPhases = { 0f, 0.51f };
    [Tooltip("On all fours: a toe or hand dropping below this height (m above his root) lands; it must rise past " +
             "Lift Reset to re-arm. 0.05/0.10 gives exactly one contact per limb per RunningCrawl cycle.")]
    [SerializeField] float crawlContactHeight = 0.05f;
    [SerializeField] float crawlLiftResetHeight = 0.10f;

    [Header("Audio")]
    [SerializeField] AudioClip[] footstepClips;
    [Tooltip("One per hand/foot landing on all fours — 11-13 a second at full skitter, so short fleshy scurries " +
             "(palm pat, fingernail clicks, a fingertip drag) that blur into one patter.")]
    [SerializeField] AudioClip[] crawlStepClips;
    [SerializeField, Range(0f, 1f)] float footstepVolume = 0.4f;
    [SerializeField, Range(0f, 1f)] float crawlStepVolume = 0.35f;
    [Tooltip("Dropping onto all fours: both palms slap down and the claws skitter.")]
    [SerializeField] AudioClip crawlDropClip;
    [SerializeField, Range(0f, 1f)] float crawlDropVolume = 0.55f;
    [SerializeField] AudioClip[] boneCrackClips;
    [SerializeField, Range(0f, 1f)] float boneCrackVolume = 0.8f;
    [SerializeField] AudioClip whooshClip;
    [SerializeField, Range(0f, 1f)] float whooshVolume = 0.7f;
    [Tooltip("Wet stick-slip creak under a stretching limb (the fire, the reel-in, the sweep wind-up).")]
    [SerializeField] AudioClip limbCreakClip;
    [SerializeField, Range(0f, 1f)] float limbCreakVolume = 0.75f;
    [SerializeField] AudioClip[] laughClips;
    [SerializeField, Range(0f, 1f)] float laughVolume = 0.85f;
    [Tooltip("Voice clips are pitched down — the Clown's laugh from a longer, emptier throat.")]
    [SerializeField, Range(0.5f, 1.5f)] float voicePitch = 0.8f;
    [SerializeField] AudioClip catchClip;
    [SerializeField] AudioClip slamImpactClip;
    [SerializeField] AudioClip clawHitClip;
    [SerializeField] AudioClip screamClip;
    [SerializeField] AudioClip hurtClip;
    [SerializeField] AudioClip deathClip;
    [SerializeField] AudioClip breathingLoop;
    [SerializeField, Range(0f, 1f)] float breathingVolume = 0.35f;
    [Tooltip("The breathing he does while he waits is for whoever gets close in the dark: full volume inside the " +
             "min, fading linearly to silence at the max.")]
    [SerializeField, Min(0.1f)] float breathingMinDistance = 2f;
    [SerializeField, Min(0.5f)] float breathingMaxDistance = 15f;
    [SerializeField] AudioClip powerDownClip;
    [SerializeField] AudioClip powerUpClip;
    [SerializeField] AudioClip lightPopClip;
    [SerializeField] AudioClip lightBuzzClip;
    [SerializeField, Range(0f, 1f)] float lightSfxVolume = 0.8f;
    [SerializeField] AudioSource footstepAudioSource;
    [SerializeField] AudioSource fxAudioSource;
    [SerializeField] AudioSource voiceAudioSource;
    [Tooltip("The breathing loop's own source (created at runtime if missing). Kept off the voice source so the " +
             "loop's short range and volume never leak onto his voice lines.")]
    [SerializeField] AudioSource breathAudioSource;

    // ---- server simulation state ----
    State _state = State.Dormant;
    Ability _ability = Ability.None;
    Coroutine _abilityRoutine;
    PlayerHealth _targetHealth;
    Transform _target;
    float _nextRetargetTime;
    float _nextThinkTime;
    float _nextAbilityTime;
    float _nextReachTime;
    float _nextSweepTime;
    float _nextClawTime;
    float _nextBlinkTime;
    float _lastSeenTargetTime;
    float _wakeEndTime;
    bool _shriekDone;
    bool _hyperArmor;
    int _phase = 1;
    bool _crawling;
    float _poise;
    float _poiseRegenBlockedUntil;
    float _staggerEndTime;
    float _staggerImmuneUntil;
    float _stallTimer;
    float _nextRepathTime;
    Vector3 _issuedDestination;
    float _driveSpeed;
    float _driveUntil;
    EnemyBlindEffect _blindEffect;
    PlayerHealth _risingTarget;
    float _riseGraceUntil;
    bool _wasBlinded;

    // Victim of a Long Reach.
    PlayerHealth _heldVictim;
    PlayerRagdollController _heldVictimRagdoll;
    NetworkPlayerRagdoll _heldVictimNetRagdoll;

    // Sweep hit bookkeeping.
    readonly HashSet<PlayerHealth> _sweepResolved = new();

    // Arena.
    Vector3 _home;
    Quaternion _homeRotation = Quaternion.identity;
    HingeInteractDoor _arenaDoor;
    bool _exitLockEngaged;

    // Movement.
    Vector3 _horizontalVelocity;
    Vector3 _verticalVelocity;
    float _intendedMoveSpeed;

    // Doors (same technique as SecurityGuardAI).
    readonly RaycastHit[] _doorProbeHits = new RaycastHit[8];
    float _nextDoorProbeTime;
    HingeInteractDoor _swingWaitDoor;
    float _swingWaitUntil;
    HingeInteractDoor _backOffDoor;
    float _backOffDeadline;
    Vector3 _swingCenter;
    float _swingRadius;
    bool _suppressMovementFacing;

    // ---- presentation state (every peer) ----
    bool _presentationAwake;
    int _presentationPhase = 1;
    bool _presentationDead;
    int _lastStateHash;
    Coroutine _stateSfxRoutine;
    Transform _leftToe, _rightToe, _leftHand, _rightHand;
    bool _leftFootArmed, _rightFootArmed, _leftHandArmed, _rightHandArmed;
    float _lastWalkPhase = -1f;

    NetworkObject _networkObject;
    NetworkMutantAvatar _avatar;

    static readonly int DormantHash = Animator.StringToHash("Dormant");
    static readonly int WalkHash = Animator.StringToHash("Walk");
    static readonly int CrawlHash = Animator.StringToHash("Crawl");
    static readonly int SlamHash = Animator.StringToHash("Slam");
    static readonly int ClawHash = Animator.StringToHash("Claw");
    static readonly int ScreamHash = Animator.StringToHash("Scream");
    static readonly int StaggerHash = Animator.StringToHash("Stagger");
    static readonly int FlinchHash = Animator.StringToHash("Flinch");
    static readonly int DeathHash = Animator.StringToHash("Death");

    public bool IsDead => _state == State.Dead || _presentationDead;

    /// <summary>True while he should be tampering with the lights around him (awake and alive).</summary>
    public bool DisturbsLights => _presentationAwake && !_presentationDead;

    public float LightFlickerRadius => _presentationPhase >= 2 ? phaseTwoLightFlickerRadius : lightFlickerRadius;

    bool IsNetworkClient => _networkObject != null
        && NetworkManager.Singleton != null
        && NetworkManager.Singleton.IsListening
        && !NetworkManager.Singleton.IsServer;

    static bool InNetworkSession => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    float PhaseCooldown(float seconds) => _phase >= 2 ? seconds * phaseTwoCooldownMultiplier : seconds;
    float CreepSpeedNow => _phase >= 2 ? creepSpeed * phaseTwoSpeedMultiplier : creepSpeed;
    float CrawlSpeedNow => _phase >= 2 ? crawlSpeed * phaseTwoSpeedMultiplier : crawlSpeed;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
        _avatar = GetComponent<NetworkMutantAvatar>();
        _poise = maxPoise;
        CacheReferences();
        ConfigureAudioSources(allowCreate: true, route: true);
        ApplyAgentSettings();

        _home = transform.position;
        _homeRotation = transform.rotation;

        if (limbRig != null)
        {
            limbRig.CueRaised += HandleRigCue;
            limbRig.TwitchIntensity = twitchIntensity;
        }

        s_Registry.Add(this);
        MutantLightDisturbance.Ensure();
    }

    void OnDestroy()
    {
        s_Registry.Remove(this);
        if (limbRig != null)
            limbRig.CueRaised -= HandleRigCue;

        // Never leave a player pinned to a despawned hand.
        if (!IsNetworkClient)
            ReleaseVictim(0f, Vector3.down);
    }

    void Start()
    {
        if (!_presentationAwake && !_presentationDead)
            StartBreathing();
    }

    void Reset()
    {
        CacheReferences();
        ConfigureAudioSources();
        ApplyAgentSettings();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        CacheReferences();
        ConfigureAudioSources(allowCreate: false);
    }
#endif

    void OnEnable()
    {
        TrySnapToNavMesh();
    }

    /// <summary>
    /// Server: called by <see cref="ProceduralMazeCoordinator"/> right after spawning him in the exit hall. Records
    /// home, finds the hall's entry door (his wake trigger), and locks the exit elevator for as long as he lives.
    /// A Mutant placed without an arena (a dev scene) wakes on sight and locks the nearest elevator if any.
    /// </summary>
    public void ServerConfigureArena(Transform arenaRoot)
    {
        if (IsNetworkClient)
            return;

        _home = transform.position;
        _homeRotation = transform.rotation;
        _arenaDoor = FindArenaDoor(arenaRoot);
        SetExitLock(true);
    }

    // ------------------------------------------------------------------
    // Per-frame
    // ------------------------------------------------------------------

    void Update()
    {
        if (_state == State.Dead)
            return;

        UpdateStateSfxWatcher();

        if (IsNetworkClient)
            return; // AI and movement are server-only; presentation runs from replicated state

        switch (_state)
        {
            case State.Dormant:
                UpdateDormant();
                ApplyMovement(Vector3.zero);
                UpdateAnimatorParameters();
                return;
            case State.Waking:
                UpdateWaking();
                ApplyMovement(Vector3.zero);
                UpdateAnimatorParameters();
                return;
        }

        if (IsBlinded)
        {
            UpdateBlindWander();
            return;
        }

        if (_wasBlinded)
        {
            _wasBlinded = false;
            PlayState("Idle", 0.25f);
        }

        if (_poise < maxPoise && Time.time >= _poiseRegenBlockedUntil)
            _poise = Mathf.Min(maxPoise, _poise + poiseRegenPerSecond * Time.deltaTime);

        Vector3 desired = Vector3.zero;
        switch (_state)
        {
            case State.Hunt:
                desired = UpdateHunt();
                break;
            case State.Ability:
                desired = UpdateAbilityMovement();
                break;
            case State.Stagger:
                UpdateStagger();
                break;
        }

        _suppressMovementFacing = false;
        if (_state == State.Hunt)
            UpdateDoorHandling(ref desired);

        ApplyMovement(desired);
        UpdateAnimatorParameters();
    }

    void LateUpdate()
    {
        if (_state != State.Dead && !_presentationDead)
            UpdateFootstepsFromAnimation();
    }

    // ------------------------------------------------------------------
    // Dormant / waking
    // ------------------------------------------------------------------

    void UpdateDormant()
    {
        bool doorOpen = _arenaDoor == null || _arenaDoor.IsOpen;
        PlayerHealth watched = null;
        float watchedDistance = float.MaxValue;

        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (!IsTargetable(p))
                continue;

            float d = Vector3.Distance(transform.position, p.transform.position);
            bool seen = d <= Mathf.Max(wakeSightDistance, dormantWatchDistance) && HasLineOfSight(p);

            if (d <= wakeProximity || (doorOpen && seen && d <= wakeSightDistance))
            {
                ServerWake(p);
                return;
            }

            if (seen && d <= dormantWatchDistance && d < watchedDistance)
            {
                watched = p;
                watchedDistance = d;
            }
        }

        // He watches through the door gap before it's even open — the head moves, nothing else does.
        SetLookTarget(watched != null ? watched.transform : null);
    }

    void ServerWake(PlayerHealth waker)
    {
        if (_state != State.Dormant)
            return;

        _state = State.Waking;
        if (waker != null)
            SetTarget(waker);

        if (health != null)
            health.ServerScaleForParty(CountLivingPlayers());

        _presentationAwake = true;
        StopBreathing();
        _avatar?.ServerSetAwake();

        Broadcast_WakeStutter(transform.position + Vector3.up * 2f, wakeStutterRadius, wakeStutterSeconds);
        PlayState("Bow", 0.25f);
        _wakeEndTime = Time.time + bowSeconds;
    }

    void UpdateWaking()
    {
        // A slow, deliberate turn toward whoever opened the door, so the bow is for them.
        if (_target != null)
            RotateTowards(_target.position, 70f);

        if (Time.time < _wakeEndTime)
            return;

        _state = State.Hunt;
        PlayState("Idle", 0.2f);
        Broadcast_Sfx(SfxKind.Laugh, transform.position + Vector3.up * 1.8f);
        _nextThinkTime = Time.time + 0.2f;
        _nextAbilityTime = Time.time + 0.2f;
        _nextSweepTime = Time.time + 3f;

        // Far down the hall? Then the first thing they see after the bow is the lights dying.
        if (_target != null && FlatDistance(_target.position) >= blinkMinDistance && Random.value < openWithBlinkChance)
            _nextBlinkTime = 0f;
        else
            _nextBlinkTime = Time.time + 4f;
    }

    // ------------------------------------------------------------------
    // Hunting
    // ------------------------------------------------------------------

    Vector3 UpdateHunt()
    {
        RefreshTarget();
        if (_targetHealth == null)
        {
            SetLookTarget(null);
            _intendedMoveSpeed = 0f;
            return Vector3.zero;
        }

        SetLookTarget(_target);
        Vector3 targetPoint = BodyPosition(_targetHealth);
        float distance = FlatDistance(targetPoint);
        bool los = HasLineOfSight(_targetHealth);
        if (los)
            _lastSeenTargetTime = Time.time;

        // Pushed out every frame they're down, so it runs out riseGraceSeconds after they're back on their feet.
        if (IsPlayerDown(_targetHealth))
        {
            _risingTarget = _targetHealth;
            _riseGraceUntil = Time.time + riseGraceSeconds;
        }

        if (!_shriekDone && health != null && health.HealthFraction <= phaseTwoHealthFraction && _abilityRoutine == null)
        {
            StartAbility(Ability.Shriek, ShriekRoutine());
            return Vector3.zero;
        }

        if (_abilityRoutine == null && Time.time >= _nextAbilityTime && Time.time >= _nextThinkTime)
        {
            _nextThinkTime = Time.time + thinkInterval;
            if (TryChooseAbility(distance, los))
                return Vector3.zero;
        }

        // Skitter hysteresis — two thresholds so he can't flicker between gaits at one boundary.
        if (!_crawling && distance >= crawlEnterDistance)
            _crawling = true;
        else if (_crawling && distance <= crawlExitDistance)
            _crawling = false;

        float speed = _crawling ? CrawlSpeedNow : CreepSpeedNow;
        if (!TrySnapToNavMesh() || !navMeshAgent.isOnNavMesh)
        {
            _intendedMoveSpeed = 0f;
            return Vector3.zero;
        }

        // Over a downed player he waits at arm's length, watching; otherwise he closes to claw range.
        float standOff = IsPlayerDown(_targetHealth) ? downedLoomDistance : Mathf.Max(0.5f, clawRange * 0.6f);
        navMeshAgent.isStopped = false;
        navMeshAgent.speed = speed;
        navMeshAgent.stoppingDistance = standOff;

        Vector3 destination = targetPoint;
        if (NavMesh.SamplePosition(destination, out NavMeshHit navHit, targetNavMeshSampleRadius, NavMesh.AllAreas))
            destination = navHit.position;

        // Interval repath, never while a path is computing (per-frame SetDestination stalls long routes).
        if (!navMeshAgent.pathPending
            && (Time.time >= _nextRepathTime || (destination - _issuedDestination).sqrMagnitude > 4f))
        {
            navMeshAgent.SetDestination(destination);
            _issuedDestination = destination;
            _nextRepathTime = Time.time + repathInterval;
        }

        Vector3 velocity = ClampedDesiredVelocity(speed);
        // The agent's desired velocity keeps pulling inside its stopping distance, and a downed player has no
        // CharacterController to stop him: he walked onto every victim he slammed and stood inside them as they got
        // up — which is what lifted them onto his head and from there onto the roof. Hold the line here.
        if (distance <= standOff)
            velocity = Vector3.zero;
        _intendedMoveSpeed = velocity.magnitude;

        // Stall watchdog: can't route to them (partial path) → the next blink check takes over.
        if (velocity.sqrMagnitude < 0.01f && distance > clawRange + 1f)
            _stallTimer += Time.deltaTime;
        else
            _stallTimer = 0f;

        if (velocity.sqrMagnitude < 0.01f)
            FaceTarget(rotationSpeed);

        return velocity;
    }

    bool TryChooseAbility(float distance, bool los)
    {
        float now = Time.time;
        bool targetDown = IsPlayerDown(_targetHealth);
        // Just back on their feet: no attack lands on them until the beat of eye contact is over.
        bool risingGrace = _targetHealth == _risingTarget && now < _riseGraceUntil;

        if (!targetDown && !risingGrace && los && distance <= clawRange + clawStartPadding && now >= _nextClawTime)
        {
            StartAbility(Ability.Claw, ClawRoutine());
            return true;
        }

        if (now >= _nextSweepTime && !risingGrace)
        {
            int near = CountStandingPlayersWithin(sweepTriggerRadius);
            if (near >= 2 || (!targetDown && distance <= sweepTriggerRadius && Random.value < sweepSoloChance))
            {
                StartAbility(Ability.Sweep, SweepRoutine());
                return true;
            }
        }

        if (!targetDown && !risingGrace && los && distance >= reachMinRange && distance <= reachMaxRange
            && now >= _nextReachTime && IsReachLaneClear(_targetHealth))
        {
            StartAbility(Ability.Reach, ReachRoutine());
            return true;
        }

        bool wantsBlink = distance >= blinkMinDistance
            || (!los && now - _lastSeenTargetTime >= blinkBlindSightSeconds)
            || _stallTimer >= 1.2f;
        if (wantsBlink && now >= _nextBlinkTime)
        {
            if (TryFindBlinkDestination(_targetHealth, out Vector3 destination, out Quaternion facing))
            {
                StartAbility(Ability.Blink, BlinkRoutine(destination, facing));
                return true;
            }
            _nextBlinkTime = now + 1.5f; // nowhere to land yet — look again shortly
        }

        return false;
    }

    void RefreshTarget()
    {
        if (_targetHealth != null && !IsTargetable(_targetHealth))
            ClearTarget();

        if (_targetHealth != null && Time.time < _nextRetargetTime)
            return;
        _nextRetargetTime = Time.time + retargetInterval;

        PlayerHealth best = null;
        float bestScore = float.MaxValue;
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (!IsTargetable(p))
                continue;

            // Someone already on the floor is the last choice — go and make a new victim.
            float score = FlatDistance(BodyPosition(p)) * (IsPlayerDown(p) ? 3f : 1f);
            if (score < bestScore)
            {
                best = p;
                bestScore = score;
            }
        }

        if (best == null)
        {
            ClearTarget();
            return;
        }

        if (_targetHealth == null)
        {
            SetTarget(best);
            return;
        }

        float currentScore = FlatDistance(BodyPosition(_targetHealth)) * (IsPlayerDown(_targetHealth) ? 3f : 1f);
        if (best != _targetHealth && bestScore < currentScore * retargetAdvantage)
            SetTarget(best);
    }

    void SetTarget(PlayerHealth target)
    {
        _targetHealth = target;
        _target = target != null ? target.transform : null;
        if (_target != null)
            _lastSeenTargetTime = Time.time;
    }

    void ClearTarget()
    {
        _targetHealth = null;
        _target = null;
    }

    // ------------------------------------------------------------------
    // Abilities
    // ------------------------------------------------------------------

    void StartAbility(Ability ability, IEnumerator routine)
    {
        if (_abilityRoutine != null)
            StopCoroutine(_abilityRoutine);

        _ability = ability;
        _state = State.Ability;
        _crawling = false;
        _stallTimer = 0f;
        _driveSpeed = 0f;
        StopAgent();
        _horizontalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;
        _abilityRoutine = StartCoroutine(routine);
    }

    void EndAbility(float cooldown, float globalCooldownOverride = -1f)
    {
        switch (_ability)
        {
            case Ability.Reach: _nextReachTime = Time.time + PhaseCooldown(cooldown); break;
            case Ability.Sweep: _nextSweepTime = Time.time + PhaseCooldown(cooldown); break;
            case Ability.Claw: _nextClawTime = Time.time + cooldown; break;
            case Ability.Blink: _nextBlinkTime = Time.time + cooldown; break;
        }

        _nextAbilityTime = Time.time + (globalCooldownOverride >= 0f ? globalCooldownOverride : globalAbilityCooldown);
        _ability = Ability.None;
        _abilityRoutine = null;
        _driveSpeed = 0f;
        _hyperArmor = false;
        if (_state == State.Ability)
            _state = State.Hunt;
    }

    /// <summary>Stagger / blind / death: stop whatever he's doing, drop anyone he's holding, pull the limbs in.</summary>
    void CancelAbility()
    {
        if (_abilityRoutine != null)
        {
            StopCoroutine(_abilityRoutine);
            _abilityRoutine = null;
        }

        bool hadLimbsOut = _ability == Ability.Reach || _ability == Ability.Sweep;
        _ability = Ability.None;
        _driveSpeed = 0f;
        _hyperArmor = false;
        ReleaseVictim(0f, Vector3.down * 2f);
        if (hadLimbsOut)
            Broadcast_CancelLimbs();
    }

    Vector3 UpdateAbilityMovement()
    {
        if (_abilityRoutine == null)
        {
            _state = State.Hunt;
            return Vector3.zero;
        }

        if (_driveSpeed <= 0.01f || Time.time >= _driveUntil || _target == null)
        {
            _intendedMoveSpeed = 0f;
            return Vector3.zero;
        }

        Vector3 gap = _target.position - transform.position;
        gap.y = 0f;
        if (gap.magnitude <= 1.1f)
        {
            _intendedMoveSpeed = 0f;
            return Vector3.zero;
        }

        _intendedMoveSpeed = _driveSpeed;
        Vector3 forward = transform.forward;
        forward.y = 0f;
        return forward.normalized * _driveSpeed;
    }

    // ----- Long Reach -----

    IEnumerator ReachRoutine()
    {
        const int side = (int)MutantLimbRig.ArmSide.Right;
        PlayState("Reach", 0.1f);
        Broadcast_ReachWindup(side, reachWindupSeconds);

        float fireTime = Time.time + reachWindupSeconds;
        float lockTime = fireTime - reachCommitLead;
        while (Time.time < fireTime)
        {
            if (Time.time < lockTime && _target != null)
                RotateTowards(_target.position, 540f);
            yield return null;
        }

        if (_targetHealth == null || !IsTargetable(_targetHealth))
        {
            Broadcast_ReachRetract(side, reachRetractSeconds);
            PlayState("Idle", 0.2f);
            EndAbility(1.5f);
            yield break;
        }

        // Aim where the target is NOW (after the commit lock, a side-step makes this empty air).
        Vector3 shoulder = limbRig.ShoulderWorldPosition(MutantLimbRig.ArmSide.Right);
        Vector3 aim = ChestPoint(_targetHealth);
        Vector3 toAim = aim - shoulder;
        Vector3 dir = toAim.sqrMagnitude > 1e-4f ? toAim.normalized : transform.forward;
        float natural = limbRig.NaturalArmLength(MutantLimbRig.ArmSide.Right);
        float palm = limbRig.PalmOffset;
        float maxLength = Mathf.Min(reachMaxRange + reachOvershoot, toAim.magnitude - palm + reachOvershoot);
        maxLength = Mathf.Max(natural, limbRig.ClearReach(shoulder, dir, maxLength));
        Vector3 palmTarget = shoulder + dir * (maxLength + palm);

        Broadcast_ReachFire(side, palmTarget, reachExtendSeconds, maxLength);

        // The server flies the same tip every peer draws (MutantLimbRig.ReachLengthAt) and catches the first
        // standing player it passes.
        PlayerHealth caught = null;
        float t0 = Time.time;
        Vector3 tip = shoulder;
        while (true)
        {
            float elapsed = Time.time - t0;
            float length = MutantLimbRig.ReachLengthAt(elapsed, reachExtendSeconds, natural, maxLength);
            tip = shoulder + dir * (length + palm);
            caught = FindReachCatch(shoulder, tip);
            if (caught != null || elapsed >= reachExtendSeconds)
                break;
            yield return null;
        }

        if (caught == null)
        {
            yield return new WaitForSeconds(reachMissHoldSeconds);
            Broadcast_ReachRetract(side, reachRetractSeconds);
            yield return new WaitForSeconds(reachRetractSeconds + 0.1f);
            PlayState("Idle", 0.2f);
            yield return new WaitForSeconds(0.3f);
            EndAbility(reachCooldown);
            yield break;
        }

        GrabVictim(caught);
        Broadcast_Sfx(SfxKind.ReachCatch, tip);
        Broadcast_ReachReel(side, reachReelSeconds);
        yield return new WaitForSeconds(reachReelSeconds);

        // Hauled in and still held: overhead, then into the floor. The victim rides his right hand through
        // the whole Slam clip on every peer, so the swing IS the throw.
        _hyperArmor = false;
        PlayState("Slam", 0.15f);
        yield return new WaitForSeconds(slamImpactTime);

        Vector3 forward = transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
        Vector3 impact = NetworkPlayerRagdoll.FindGrabBone(transform, RightHandGrabBoneIndex) is Transform hand
            ? hand.position
            : transform.position + forward;
        if (_heldVictim != null)
        {
            ReleaseVictim(slamDamage, forward * slamForwardSpeed + Vector3.down * slamDownSpeed);
            Broadcast_Sfx(SfxKind.SlamImpact, impact);
        }

        yield return new WaitForSeconds(slamRecoverySeconds);
        PlayState("Idle", 0.25f);
        EndAbility(reachCooldown);
    }

    PlayerHealth FindReachCatch(Vector3 shoulder, Vector3 tip)
    {
        PlayerHealth best = null;
        float bestAlong = float.MaxValue;
        float segLength = (tip - shoulder).magnitude;
        if (segLength < 0.01f)
            return null;

        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (!IsTargetable(p) || IsPlayerDown(p))
                continue;

            // The player as a vertical segment (ankles to crown) against the limb as a segment.
            Vector3 feet = p.transform.position;
            float top = IsCrouching(p) ? 1.05f : 1.65f;
            float distance = SegmentSegmentDistance(shoulder, tip, feet + Vector3.up * 0.3f, feet + Vector3.up * top,
                out float alongLimb);
            if (distance > reachCatchRadius + 0.3f)
                continue;
            if (alongLimb * segLength < bestAlong)
            {
                best = p;
                bestAlong = alongLimb * segLength;
            }
        }
        return best;
    }

    void GrabVictim(PlayerHealth victim)
    {
        if (victim == null)
            return;

        _heldVictim = victim;
        _heldVictimRagdoll = victim.GetComponent<PlayerRagdollController>();
        _heldVictimNetRagdoll = victim.GetComponent<NetworkPlayerRagdoll>();
        IgnoreVictimRagdollCollisions(victim);

        if (InNetworkSession && _heldVictimNetRagdoll != null && _networkObject != null && _networkObject.IsSpawned)
        {
            _heldVictimNetRagdoll.BeginHeldByEnemyFromServer(
                _networkObject.NetworkObjectId, RightHandGrabBoneIndex, holdLocalOffset, holdLocalEuler,
                holdLocalOffset, holdApproachSeconds);
        }
        else if (_heldVictimRagdoll != null)
        {
            Transform bone = NetworkPlayerRagdoll.FindGrabBone(transform, RightHandGrabBoneIndex);
            if (bone != null)
                _heldVictimRagdoll.BeginHeldByPoint(bone, holdLocalOffset, Quaternion.Euler(holdLocalEuler),
                    holdLocalOffset, holdApproachSeconds);
        }
    }

    /// <summary>Server: let go of the reach victim — into the slam, or simply dropped when he's interrupted.</summary>
    void ReleaseVictim(float damage, Vector3 force)
    {
        if (_heldVictim == null)
            return;

        PlayerHealth victim = _heldVictim;
        PlayerRagdollController ragdoll = _heldVictimRagdoll;
        NetworkPlayerRagdoll netRagdoll = _heldVictimNetRagdoll;
        _heldVictim = null;
        _heldVictimRagdoll = null;
        _heldVictimNetRagdoll = null;

        Transform hand = NetworkPlayerRagdoll.FindGrabBone(transform, RightHandGrabBoneIndex);
        Vector3 at = SafeReleasePoint(hand != null ? hand.position : transform.position + Vector3.up);

        if (InNetworkSession && netRagdoll != null)
        {
            netRagdoll.ReleaseSlamFromServer(force, at, damage, (byte)slamForceMode);
            return;
        }

        if (ragdoll == null || victim == null)
            return;

        bool survived = true;
        if (damage > 0f)
        {
            victim.TakeDamage(damage);
            survived = !victim.IsDead;
        }
        ragdoll.ReleaseFromHeld(force, at, slamForceMode, allowAutoRecovery: survived);
    }

    /// <summary>
    /// Where the victim's hips go when he lets go: at his hand, but pushed out to at least
    /// <see cref="releaseMinDistance"/> from his own axis — at the bottom of the slam the hand is only ~0.5 m out, so
    /// the body landed half under him and the victim stood up inside his capsule — and pulled back short of any wall.
    /// The cast runs from his own axis at the hand's height, which his CharacterController keeps clear of walls.
    /// </summary>
    Vector3 SafeReleasePoint(Vector3 hand)
    {
        Vector3 origin = transform.position + Vector3.up * Mathf.Clamp(hand.y - transform.position.y, 0.35f, 2.2f);
        Vector3 flat = hand - origin;
        flat.y = 0f;
        float distance = flat.magnitude;
        Vector3 dir = flat;
        if (distance < 0.05f)
        {
            dir = transform.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f)
                dir = Vector3.forward;
        }
        dir.Normalize();

        distance = Mathf.Max(distance, releaseMinDistance);
        if (Physics.SphereCast(origin, 0.25f, dir, out RaycastHit hit, distance, EnvironmentMask(), QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(0f, hit.distance - 0.05f);
        return origin + dir * distance;
    }

    // ----- Scythe Sweep -----

    IEnumerator SweepRoutine()
    {
        FaceTarget(720f);
        PlayState("SweepHold", 0.15f);

        float baseYaw = transform.eulerAngles.y;
        float degrees = (_phase >= 2 ? phaseTwoSweepDegrees : sweepSpinDegrees) * (Random.value < 0.5f ? -1f : 1f);
        Broadcast_Sweep(sweepWindupSeconds, sweepSpinSeconds, degrees, sweepHoldSeconds, sweepRetractSeconds,
            sweepArmLength, baseYaw);

        _sweepResolved.Clear();
        float t0 = Time.time;
        float previousSpin = 0f;
        while (true)
        {
            float elapsed = Time.time - t0;
            float spin = MutantLimbRig.SweepSpinDegreesAt(elapsed, sweepWindupSeconds, sweepSpinSeconds, degrees);
            if (elapsed > sweepWindupSeconds)
            {
                // Same extension profile MutantLimbRig draws: 30% at the end of the wind-up, full 0.12 s later.
                float reach = sweepArmLength * Mathf.Lerp(0.3f, 1f, Mathf.Clamp01((elapsed - sweepWindupSeconds) / 0.12f));
                ResolveSweepHits(baseYaw, previousSpin, spin, degrees, reach);
            }
            previousSpin = spin;
            if (elapsed >= sweepWindupSeconds + sweepSpinSeconds)
                break;
            yield return null;
        }

        yield return new WaitForSeconds(sweepHoldSeconds + sweepRetractSeconds);
        PlayState("Idle", 0.2f);
        EndAbility(sweepCooldown);
    }

    void ResolveSweepHits(float baseYaw, float previousSpin, float spin, float totalDegrees, float reach)
    {
        if (Mathf.Approximately(previousSpin, spin))
            return;

        Vector3 center = transform.position;
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (p == null || _sweepResolved.Contains(p) || !IsTargetable(p) || IsPlayerDown(p))
                continue;

            Vector3 offset = p.transform.position - center;
            if (Mathf.Abs(offset.y) > sweepVerticalTolerance)
                continue;
            offset.y = 0f;
            float r = offset.magnitude;
            if (r < 0.3f || r > reach + 0.45f)
                continue;

            float bearing = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            bool crossed = false;
            for (int s = 0; s < 2 && !crossed; s++)
            {
                var side = (MutantLimbRig.ArmSide)s;
                float from = limbRig.SweepArmYaw(side, baseYaw, previousSpin);
                float to = limbRig.SweepArmYaw(side, baseYaw, spin);
                crossed = AngleSwept(from, to, bearing);
            }
            if (!crossed)
                continue;

            _sweepResolved.Add(p);

            // Walls stop his arms (MutantLimbRig clamps them), so a wall between you is cover.
            Vector3 armLevel = center + Vector3.up * limbRig.SweepArmHeight;
            if (Physics.Linecast(armLevel, p.transform.position + Vector3.up * limbRig.SweepArmHeight * 0.85f,
                    out _, EnvironmentMask(), QueryTriggerInteraction.Ignore))
                continue;

            // Crouched under it — it passes over. (Crouch only holds while standing still.)
            if (IsCrouching(p))
                continue;

            Vector3 radial = offset / r;
            Vector3 tangent = Vector3.Cross(Vector3.up, radial) * Mathf.Sign(totalDegrees);
            Vector3 force = tangent * sweepKnockSpeed + radial * (sweepKnockSpeed * 0.4f) + Vector3.up * sweepKnockUpSpeed;
            LaunchPlayer(p, force, sweepDamage);
            Broadcast_Sfx(SfxKind.SweepHit, p.transform.position + Vector3.up * 1.2f);
        }
    }

    static bool AngleSwept(float from, float to, float bearing)
    {
        float delta = Mathf.DeltaAngle(from, to);
        float rel = Mathf.DeltaAngle(from, bearing);
        return delta >= 0f ? rel >= 0f && rel <= delta : rel <= 0f && rel >= delta;
    }

    // ----- Claw -----

    IEnumerator ClawRoutine()
    {
        PlayState("Claw", 0.1f);
        float hitAt = Time.time + clawHitTime;
        float lockAt = hitAt - 0.2f;
        _driveSpeed = clawDriveSpeed;
        _driveUntil = hitAt;
        while (Time.time < hitAt)
        {
            if (Time.time < lockAt && _target != null)
                RotateTowards(_target.position, 420f);
            yield return null;
        }
        _driveSpeed = 0f;

        if (!IsBlinded && CanLandCommitted(_targetHealth, transform.forward, clawRange + 0.35f, clawHitHalfAngle))
        {
            _targetHealth.TakeDamage(clawDamage);
            Vector3 push = _targetHealth.transform.position - transform.position;
            push.y = 0f;
            push = push.sqrMagnitude > 1e-4f ? push.normalized : transform.forward;
            PushPlayer(_targetHealth, push * clawPushSpeed, clawPushUpSpeed, clawPushLockSeconds);
            Broadcast_Sfx(SfxKind.ClawHit, _targetHealth.transform.position + Vector3.up * 1.2f);
        }

        yield return new WaitForSeconds(clawRecovery);
        PlayState("Idle", 0.2f);
        EndAbility(clawCooldown);
    }

    // ----- Lights Out -----

    IEnumerator BlinkRoutine(Vector3 destination, Quaternion facing)
    {
        Broadcast_Blackout(transform.position + Vector3.up * 2f, destination + Vector3.up * 2f,
            blinkLightRadius, blinkDarkIn, blinkHold, blinkRecover);

        yield return new WaitForSeconds(blinkDarkIn + blinkTeleportDelay);

        if (_avatar != null)
            _avatar.ServerTeleport(destination, facing);
        else
        {
            bool cc = characterController != null && characterController.enabled;
            if (cc) characterController.enabled = false;
            transform.SetPositionAndRotation(destination, facing);
            if (cc) characterController.enabled = true;
            if (navMeshAgent != null && navMeshAgent.enabled)
                navMeshAgent.Warp(destination);
        }

        PlayState("Idle", 0f);
        yield return new WaitForSeconds(Mathf.Max(0f, blinkHold - blinkTeleportDelay));
        Broadcast_Sfx(SfxKind.Laugh, destination + Vector3.up * 1.8f);

        // The lights come back with him already there — let the claw go straight away.
        EndAbility(_phase >= 2 ? phaseTwoBlinkCooldown : blinkCooldown, 0.1f);
        _nextClawTime = 0f;
    }

    bool TryFindBlinkDestination(PlayerHealth target, out Vector3 destination, out Quaternion facing)
    {
        destination = default;
        facing = Quaternion.identity;
        if (target == null)
            return false;

        Transform t = target.transform;
        Vector3 at = BodyPosition(target);
        Vector3 back = -t.forward;
        back.y = 0f;
        if (back.sqrMagnitude < 1e-4f)
            back = -Vector3.forward;
        back.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, back);

        Vector3[] candidates =
        {
            at + back * blinkBehindDistance,
            at + (back + right).normalized * blinkBehindDistance,
            at + (back - right).normalized * blinkBehindDistance,
            at + right * blinkBehindDistance,
            at - right * blinkBehindDistance,
            at + back * (blinkBehindDistance * 1.8f),
        };

        int mask = EnvironmentMask();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (!NavMesh.SamplePosition(candidates[i], out NavMeshHit hit, 1.2f, NavMesh.AllAreas))
                continue;

            Vector3 p = hit.position;
            if (Vector3.Distance(p, at) < 1.2f || IsAnyPlayerWithin(p, 1.1f))
                continue;

            // Room to stand, and a clear line to the target so the next move can land.
            if (Physics.CheckCapsule(p + Vector3.up * 0.5f, p + Vector3.up * 2f, 0.3f, mask, QueryTriggerInteraction.Ignore))
                continue;
            if (Physics.Linecast(p + Vector3.up * eyeHeight, ChestPoint(target), mask, QueryTriggerInteraction.Ignore))
                continue;

            Vector3 look = at - p;
            look.y = 0f;
            destination = p;
            facing = look.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(look.normalized) : transform.rotation;
            return true;
        }

        return false;
    }

    // ----- Shriek -----

    IEnumerator ShriekRoutine()
    {
        _hyperArmor = true;
        _shriekDone = true;
        EnterPhaseTwo();
        PlayState("Scream", 0.12f);

        yield return new WaitForSeconds(shriekImpactTime);
        Vector3 center = transform.position + Vector3.up * 1.6f;
        Broadcast_Shriek(center);

        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (!IsTargetable(p) || IsPlayerDown(p))
                continue;

            Vector3 away = p.transform.position - transform.position;
            away.y = 0f;
            float d = away.magnitude;
            if (d > shriekRadius)
                continue;

            away = d > 0.01f ? away / d : transform.forward;
            float falloff = Mathf.Lerp(1f, 0.45f, d / Mathf.Max(0.1f, shriekRadius));
            if (shriekDamage > 0f)
                p.TakeDamage(shriekDamage * falloff);
            PushPlayer(p, away * (shriekPushSpeed * falloff), shriekPushUpSpeed, shriekPushLockSeconds);
        }

        yield return new WaitForSeconds(Mathf.Max(0f, shriekDuration - shriekImpactTime));
        PlayState("Idle", 0.25f);
        EndAbility(0f);
    }

    void EnterPhaseTwo()
    {
        _phase = 2;
        ApplyPhasePresentation(2);
        _avatar?.ServerSetPhase(2);
        // A fresh set of tricks for the second half.
        _nextBlinkTime = Mathf.Min(_nextBlinkTime, Time.time + 3f);
        _nextSweepTime = Mathf.Min(_nextSweepTime, Time.time + 2.5f);
    }

    // ------------------------------------------------------------------
    // Damage reactions (server) — called by MutantHealth after the hit is applied
    // ------------------------------------------------------------------

    public void OnDamageTaken(bool fromPlayerMelee, Transform attacker, PlayerHealth attackerHealth)
    {
        if (_state == State.Dead)
            return;

        if (_state == State.Dormant)
        {
            ServerWake(attackerHealth);
            return;
        }

        // Whoever hits him becomes the prey (unless he's busy holding someone).
        if (attackerHealth != null && IsTargetable(attackerHealth) && _heldVictim == null)
            SetTarget(attackerHealth);

        if (!fromPlayerMelee || _state == State.Waking)
            return;

        _poiseRegenBlockedUntil = Time.time + poiseRegenDelay;
        if (_hyperArmor || _state == State.Stagger || Time.time < _staggerImmuneUntil)
            return;

        _poise -= meleePoiseDamage;
        if (_poise <= 0f)
            BeginStagger();
    }

    void BeginStagger()
    {
        CancelAbility();
        _poise = maxPoise;
        _state = State.Stagger;
        _staggerEndTime = Time.time + staggerSeconds;
        _staggerImmuneUntil = _staggerEndTime + staggerImmunitySeconds;
        _nextAbilityTime = _staggerEndTime + 0.4f;
        StopAgent();
        _horizontalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;
        PlayState("Stagger", 0.1f);
    }

    void UpdateStagger()
    {
        _intendedMoveSpeed = 0f;
        if (Time.time < _staggerEndTime)
            return;

        PlayState("Idle", 0.25f);
        _state = State.Hunt;
    }

    // ----- IBlindableEnemy -----

    bool IsBlinded => EnemyBlindEffect.IsBlinded(ref _blindEffect, gameObject);

    public void OnFlashbangBlinded(float seconds)
    {
        if (_state == State.Dead)
            return;

        if (_state == State.Dormant)
            ServerWake(null);

        CancelAbility();
        if (_state != State.Waking)
            _state = State.Hunt;
        StopAgent();
        _horizontalVelocity = Vector3.zero;
        _crawling = false;
        _wasBlinded = true;
        _nextAbilityTime = Time.time + seconds;
        PlayState("Flinch", 0.1f);
    }

    void UpdateBlindWander()
    {
        _wasBlinded = true;

        // Let the flinch play out, then stumble about half-blind.
        if (animator != null && animator.GetCurrentAnimatorStateInfo(0).shortNameHash == FlinchHash
            && animator.GetCurrentAnimatorStateInfo(0).normalizedTime > 0.55f && !animator.IsInTransition(0))
            PlayState("Idle", 0.3f);

        Vector3 wander = _blindEffect != null ? _blindEffect.TickWanderVelocity(transform, creepSpeed) : Vector3.zero;
        _intendedMoveSpeed = wander.magnitude;
        _crawling = false;
        ApplyMovement(wander);
        UpdateAnimatorParameters();
    }

    // ----- Death (every peer) -----

    public void HandleDeath()
    {
        if (_state == State.Dead && _presentationDead)
            return;

        bool server = !IsNetworkClient;
        if (server)
            CancelAbility();

        _state = State.Dead;
        _presentationDead = true;
        StopBreathing();
        limbRig?.CancelAll(0.2f);

        if (navMeshAgent != null && navMeshAgent.enabled)
        {
            if (navMeshAgent.isOnNavMesh)
            {
                navMeshAgent.isStopped = true;
                navMeshAgent.ResetPath();
            }
            navMeshAgent.enabled = false;
        }

        _horizontalVelocity = Vector3.zero;
        _verticalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;

        if (animator != null)
        {
            animator.SetFloat(speedParameter, 0f);
            animator.SetBool(crawlingParameter, false);
            if (server)
                animator.CrossFadeInFixedTime("Death", 0.1f, 0, 0f);
        }

        if (footstepAudioSource != null)
            footstepAudioSource.Stop();

        if (server)
        {
            SetLookTarget(null);
            SetExitLock(false);
        }
    }

    // ------------------------------------------------------------------
    // Presentation — every peer (server/offline via the Broadcast_* wrappers, clients via NetworkMutantAvatar)
    // ------------------------------------------------------------------

    public void ApplyReplicatedAwake()
    {
        _presentationAwake = true;
        StopBreathing();
    }

    public void ApplyReplicatedPhase(int phase) => ApplyPhasePresentation(phase);

    public void ApplyReplicatedLookTarget(Transform target) => limbRig?.SetLookTarget(target);

    void ApplyPhasePresentation(int phase)
    {
        _presentationPhase = phase;
        if (limbRig != null)
            limbRig.TwitchIntensity = phase >= 2 ? phaseTwoTwitchIntensity : twitchIntensity;
    }

    public void PresentReachWindup(int side, float seconds) => limbRig?.BeginReachWindup((MutantLimbRig.ArmSide)side, seconds);

    public void PresentReachFire(int side, Vector3 aimPoint, float extendSeconds, float maxLength)
        => limbRig?.FireReach((MutantLimbRig.ArmSide)side, aimPoint, extendSeconds, maxLength);

    public void PresentReachReel(int side, float seconds) => limbRig?.ReelArm((MutantLimbRig.ArmSide)side, seconds);

    public void PresentReachRetract(int side, float seconds) => limbRig?.RetractArm((MutantLimbRig.ArmSide)side, seconds);

    public void PresentSweep(float windup, float spin, float degrees, float hold, float retract, float armLength, float baseYaw)
        => limbRig?.BeginSweep(windup, spin, degrees, hold, retract, armLength, baseYaw);

    public void PresentCancelLimbs() => limbRig?.CancelAll();

    public void PresentBlackout(Vector3 from, Vector3 to, float radius, float darkIn, float hold, float recover)
    {
        MutantLightDisturbance.Blackout(from, to, radius, darkIn, hold, recover);
        PlayAt(powerDownClip, from, lightSfxVolume, 2f, 40f);
        if ((to - from).sqrMagnitude > 25f)
            PlayAt(powerDownClip, to, lightSfxVolume, 2f, 40f);
        StartCoroutine(PlayDelayed(powerUpClip, to, darkIn + hold, lightSfxVolume));
    }

    public void PresentWakeStutter(Vector3 center, float radius, float seconds)
    {
        MutantLightDisturbance.Stutter(center, radius, seconds);
        PlayAt(lightBuzzClip, center, lightSfxVolume, 3f, 45f);
    }

    public void PresentShriek(Vector3 center)
    {
        PresentShriekBlowOut(center);
        StartCoroutine(ShriekShakeRoutine(center));
    }

    /// <summary>Also run on late joiners so they lose the same lights (fixture positions are identical on every peer).</summary>
    public void PresentShriekBlowOut(Vector3 center)
    {
        MutantLightDisturbance.BlowOut(center, shriekLightRadius, shriekMaxBlownLights);
        PlayAt(lightPopClip, center, lightSfxVolume, 3f, 40f);
    }

    public void PresentSfx(SfxKind kind, Vector3 position)
    {
        switch (kind)
        {
            case SfxKind.Laugh:
                PlayVoice(Pick(laughClips), laughVolume);
                break;
            case SfxKind.ReachCatch:
                PlayAt(catchClip, position, 0.9f, 2f, 30f);
                break;
            case SfxKind.SlamImpact:
                PlayAt(slamImpactClip, position, 1f, 2f, 36f);
                PlayerController.AddScreamShakeToLocalPlayer(position, 3f, 14f, 0.8f);
                break;
            case SfxKind.ClawHit:
            case SfxKind.SweepHit:
                PlayAt(clawHitClip, position, 0.9f, 2f, 28f);
                break;
            case SfxKind.Wake:
                PlayVoice(Pick(laughClips), laughVolume * 0.7f);
                break;
        }
    }

    IEnumerator ShriekShakeRoutine(Vector3 center)
    {
        float end = Time.time + shriekShakeSeconds;
        while (Time.time < end)
        {
            PlayerController.AddScreamShakeToLocalPlayer(center, 4f, 20f, 1f);
            yield return null;
        }
    }

    void HandleRigCue(MutantLimbRig.Cue cue, Vector3 at)
    {
        switch (cue)
        {
            case MutantLimbRig.Cue.ArmWindup:
                PlayFx(Pick(boneCrackClips), boneCrackVolume, 0.85f);
                break;
            case MutantLimbRig.Cue.ArmFire:
                PlayFx(whooshClip, whooshVolume, 0.7f);
                PlayFx(limbCreakClip, limbCreakVolume, 1.3f);
                break;
            case MutantLimbRig.Cue.ArmSnapBack:
                PlayFx(whooshClip, whooshVolume * 0.8f, 1.35f);
                break;
            case MutantLimbRig.Cue.ArmReel:
                PlayFx(limbCreakClip, limbCreakVolume, 0.8f);
                PlayFx(Pick(boneCrackClips), boneCrackVolume * 0.6f, 0.75f);
                break;
            case MutantLimbRig.Cue.NeckCrack:
                PlayFx(Pick(boneCrackClips), boneCrackVolume, 1.25f);
                break;
            case MutantLimbRig.Cue.SweepWindup:
                PlayFx(limbCreakClip, limbCreakVolume, 0.65f);
                PlayFx(Pick(boneCrackClips), boneCrackVolume, 0.75f);
                StartCoroutine(PlayFxDelayed(Pick(boneCrackClips), 0.25f, boneCrackVolume, 0.9f));
                StartCoroutine(PlayFxDelayed(whooshClip, sweepWindupSeconds, whooshVolume, 0.55f));
                break;
        }
    }

    /// <summary>
    /// Sounds keyed off the replicated animator state, so host and clients hear identical cues with no netcode:
    /// the slam and claw whooshes land on the swing, the scream on the open mouth.
    /// </summary>
    void UpdateStateSfxWatcher()
    {
        if (animator == null || !animator.isActiveAndEnabled)
            return;

        int hash = animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
        if (hash == _lastStateHash)
            return;
        _lastStateHash = hash;

        if (_stateSfxRoutine != null)
        {
            StopCoroutine(_stateSfxRoutine);
            _stateSfxRoutine = null;
        }

        if (hash == SlamHash)
            _stateSfxRoutine = StartCoroutine(PlayFxDelayed(whooshClip, 1.3f, whooshVolume, 0.6f));
        else if (hash == ClawHash)
            _stateSfxRoutine = StartCoroutine(PlayFxDelayed(whooshClip, 0.5f, whooshVolume, 1.1f));
        else if (hash == ScreamHash)
            _stateSfxRoutine = StartCoroutine(PlayVoiceDelayed(screamClip, 0.85f, 1f));
        else if (hash == StaggerHash || hash == FlinchHash)
            PlayVoice(hurtClip, 0.9f);
        else if (hash == DeathHash)
            PlayVoice(deathClip, 1f);
        else if (hash == CrawlHash)
            PlayFx(crawlDropClip, crawlDropVolume, 1f); // dropping onto all fours
    }

    /// <summary>
    /// Steps come from the ANIMATION on every peer (the animator replicates), so they can never drift from the
    /// visible stride: the shuffling walk by cycle phase, the skitter by each hand and foot actually landing.
    /// </summary>
    void UpdateFootstepsFromAnimation()
    {
        if (animator == null || footstepAudioSource == null)
            return;

        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);

        if (info.shortNameHash == WalkHash && !animator.IsInTransition(0))
        {
            float phase = info.normalizedTime - Mathf.Floor(info.normalizedTime);
            if (_lastWalkPhase >= 0f && walkStepPhases != null)
                for (int i = 0; i < walkStepPhases.Length; i++)
                    if (PhaseCrossed(_lastWalkPhase, phase, walkStepPhases[i]))
                        PlayStep(footstepClips, footstepVolume);
            _lastWalkPhase = phase;
        }
        else
            _lastWalkPhase = -1f;

        if (info.shortNameHash != CrawlHash)
        {
            _leftFootArmed = _rightFootArmed = _leftHandArmed = _rightHandArmed = false;
            return;
        }

        AudioClip[] clips = crawlStepClips != null && crawlStepClips.Length > 0 ? crawlStepClips : footstepClips;
        StepDetector(_leftToe, ref _leftFootArmed, clips, crawlStepVolume);
        StepDetector(_rightToe, ref _rightFootArmed, clips, crawlStepVolume);
        StepDetector(_leftHand, ref _leftHandArmed, clips, crawlStepVolume * 0.8f);
        StepDetector(_rightHand, ref _rightHandArmed, clips, crawlStepVolume * 0.8f);
    }

    static bool PhaseCrossed(float from, float to, float mark)
    {
        return to >= from ? mark > from && mark <= to : mark > from || mark <= to; // second case: wrapped the cycle
    }

    void StepDetector(Transform bone, ref bool armed, AudioClip[] clips, float volume)
    {
        if (bone == null)
            return;

        float height = transform.InverseTransformPoint(bone.position).y * transform.lossyScale.y;
        if (armed && height <= crawlContactHeight)
        {
            armed = false;
            PlayStep(clips, volume);
        }
        else if (!armed && height >= crawlLiftResetHeight)
            armed = true;
    }

    void PlayStep(AudioClip[] clips, float volume)
    {
        AudioClip clip = Pick(clips);
        if (clip == null)
            return;
        footstepAudioSource.pitch = 1f + Random.Range(-0.07f, 0.07f);
        footstepAudioSource.PlayOneShot(clip, volume);
    }

    void StartBreathing()
    {
        if (breathAudioSource == null || breathingLoop == null)
            return;
        breathAudioSource.clip = breathingLoop;
        breathAudioSource.loop = true;
        breathAudioSource.volume = breathingVolume;
        breathAudioSource.pitch = voicePitch;
        breathAudioSource.Play();
    }

    void StopBreathing()
    {
        if (breathAudioSource != null && breathAudioSource.isPlaying)
            breathAudioSource.Stop();
    }

    void PlayFx(AudioClip clip, float volume, float pitch)
    {
        if (clip == null || fxAudioSource == null)
            return;
        fxAudioSource.pitch = pitch * Random.Range(0.95f, 1.05f);
        fxAudioSource.PlayOneShot(clip, volume);
    }

    void PlayVoice(AudioClip clip, float volume)
    {
        if (clip == null || voiceAudioSource == null)
            return;
        voiceAudioSource.pitch = voicePitch;
        voiceAudioSource.PlayOneShot(clip, volume);
    }

    static void PlayAt(AudioClip clip, Vector3 position, float volume, float minDistance, float maxDistance)
    {
        if (clip != null)
            GameAudioManager.PlaySfxAtPoint(clip, position, volume, minDistance, maxDistance);
    }

    IEnumerator PlayFxDelayed(AudioClip clip, float delay, float volume, float pitch)
    {
        yield return new WaitForSeconds(delay);
        PlayFx(clip, volume, pitch);
    }

    IEnumerator PlayVoiceDelayed(AudioClip clip, float delay, float volume)
    {
        yield return new WaitForSeconds(delay);
        PlayVoice(clip, volume);
    }

    IEnumerator PlayDelayed(AudioClip clip, Vector3 position, float delay, float volume)
    {
        yield return new WaitForSeconds(delay);
        PlayAt(clip, position, volume, 2f, 40f);
    }

    static AudioClip Pick(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0)
            return null;
        return clips[Random.Range(0, clips.Length)];
    }

    // ------------------------------------------------------------------
    // Broadcast wrappers — through the avatar when present (it applies locally too), else locally
    // ------------------------------------------------------------------

    void Broadcast_ReachWindup(int side, float seconds)
    {
        if (_avatar != null) _avatar.BroadcastReachWindup(side, seconds);
        else PresentReachWindup(side, seconds);
    }

    void Broadcast_ReachFire(int side, Vector3 aim, float extend, float maxLength)
    {
        if (_avatar != null) _avatar.BroadcastReachFire(side, aim, extend, maxLength);
        else PresentReachFire(side, aim, extend, maxLength);
    }

    void Broadcast_ReachReel(int side, float seconds)
    {
        if (_avatar != null) _avatar.BroadcastReachReel(side, seconds);
        else PresentReachReel(side, seconds);
    }

    void Broadcast_ReachRetract(int side, float seconds)
    {
        if (_avatar != null) _avatar.BroadcastReachRetract(side, seconds);
        else PresentReachRetract(side, seconds);
    }

    void Broadcast_Sweep(float windup, float spin, float degrees, float hold, float retract, float armLength, float baseYaw)
    {
        if (_avatar != null) _avatar.BroadcastSweep(windup, spin, degrees, hold, retract, armLength, baseYaw);
        else PresentSweep(windup, spin, degrees, hold, retract, armLength, baseYaw);
    }

    void Broadcast_CancelLimbs()
    {
        if (_avatar != null) _avatar.BroadcastCancelLimbs();
        else PresentCancelLimbs();
    }

    void Broadcast_Blackout(Vector3 from, Vector3 to, float radius, float darkIn, float hold, float recover)
    {
        if (_avatar != null) _avatar.BroadcastBlackout(from, to, radius, darkIn, hold, recover);
        else PresentBlackout(from, to, radius, darkIn, hold, recover);
    }

    void Broadcast_WakeStutter(Vector3 center, float radius, float seconds)
    {
        if (_avatar != null) _avatar.BroadcastWakeStutter(center, radius, seconds);
        else PresentWakeStutter(center, radius, seconds);
    }

    void Broadcast_Shriek(Vector3 center)
    {
        if (_avatar != null) _avatar.BroadcastShriek(center);
        else PresentShriek(center);
    }

    void Broadcast_Sfx(SfxKind kind, Vector3 position)
    {
        if (_avatar != null) _avatar.BroadcastSfx(kind, position);
        else PresentSfx(kind, position);
    }

    void SetLookTarget(Transform target)
    {
        limbRig?.SetLookTarget(target);
        _avatar?.ServerSetLookTarget(target);
    }

    // ------------------------------------------------------------------
    // Players
    // ------------------------------------------------------------------

    static bool IsTargetable(PlayerHealth p)
    {
        if (p == null || p.IsDead)
            return false;
        NetworkPlayerAvatar avatar = p.GetComponent<NetworkPlayerAvatar>();
        return avatar == null || (!avatar.IsCarriedByJailor && !avatar.IsSealedInJailCell);
    }

    static bool IsPlayerDown(PlayerHealth p)
    {
        if (p == null)
            return false;
        PlayerRagdollController ragdoll = p.GetComponent<PlayerRagdollController>();
        return ragdoll != null && (ragdoll.IsRagdolled || ragdoll.IsHeld || ragdoll.IsGettingUp);
    }

    static bool IsCrouching(PlayerHealth p)
    {
        PlayerController pc = p != null ? p.GetComponent<PlayerController>() : null;
        return pc != null && pc.IsCrouching;
    }

    /// <summary>
    /// Where a player's body is. A ragdolled or held player's root stays where they went down or were caught — after
    /// a slam that is the far end of the reach, metres past the body lying at his feet — so everything that measures
    /// him against his prey (chasing, looming, blink landings) goes by the hips while they are down.
    /// </summary>
    static Vector3 BodyPosition(PlayerHealth p)
    {
        PlayerRagdollController ragdoll = p.GetComponent<PlayerRagdollController>();
        Transform hips = ragdoll != null && (ragdoll.IsRagdolled || ragdoll.IsHeld) ? ragdoll.HipsTransform : null;
        return hips != null ? hips.position : p.transform.position;
    }

    Vector3 ChestPoint(PlayerHealth p)
    {
        if (IsPlayerDown(p))
            return BodyPosition(p) + Vector3.up * 0.25f;
        return p.transform.position + Vector3.up * (IsCrouching(p) ? crouchedChestHeight : chestHeight);
    }

    static int CountLivingPlayers()
    {
        int count = 0;
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
            if (players[i] != null && !players[i].IsDead)
                count++;
        return Mathf.Max(1, count);
    }

    int CountStandingPlayersWithin(float radius)
    {
        int count = 0;
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (!IsTargetable(p) || IsPlayerDown(p))
                continue;
            if (FlatDistance(p.transform.position) <= radius && Mathf.Abs(p.transform.position.y - transform.position.y) <= sweepVerticalTolerance)
                count++;
        }
        return count;
    }

    static bool IsAnyPlayerWithin(Vector3 point, float radius)
    {
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
            if (players[i] != null && !players[i].IsDead && (BodyPosition(players[i]) - point).sqrMagnitude < radius * radius)
                return true;
        return false;
    }

    bool HasLineOfSight(PlayerHealth p)
    {
        if (p == null)
            return false;
        Vector3 from = transform.position + Vector3.up * eyeHeight;
        return !Physics.Linecast(from, ChestPoint(p), EnvironmentMask(), QueryTriggerInteraction.Ignore);
    }

    bool IsReachLaneClear(PlayerHealth p)
    {
        Vector3 shoulder = limbRig != null
            ? limbRig.ShoulderWorldPosition(MutantLimbRig.ArmSide.Right)
            : transform.position + Vector3.up * 1.75f;
        Vector3 to = ChestPoint(p) - shoulder;
        float d = to.magnitude;
        if (d < 0.1f)
            return true;
        return !Physics.SphereCast(shoulder, 0.2f, to / d, out _, d * 0.9f, EnvironmentMask(), QueryTriggerInteraction.Ignore);
    }

    bool CanLandCommitted(PlayerHealth p, Vector3 committedDirection, float reach, float halfAngle)
    {
        if (p == null || !IsTargetable(p) || IsPlayerDown(p))
            return false;

        Vector3 to = p.transform.position - transform.position;
        to.y = 0f;
        float d = to.magnitude;
        if (d > reach)
            return false;
        if (d > 0.01f && Vector3.Angle(committedDirection, to / d) > halfAngle)
            return false;
        return HasLineOfSight(p);
    }

    void PushPlayer(PlayerHealth p, Vector3 horizontalVelocity, float upward, float lockSeconds)
    {
        if (p == null)
            return;

        NetworkObject playerNo = p.GetComponent<NetworkObject>();
        if (InNetworkSession && _avatar != null && playerNo != null && playerNo.IsSpawned)
            _avatar.ServerRelayPush(playerNo, horizontalVelocity, upward, lockSeconds);
        else
            p.GetComponent<PlayerController>()?.ApplyExternalPush(horizontalVelocity, upward, lockSeconds);
    }

    void LaunchPlayer(PlayerHealth p, Vector3 force, float damage)
    {
        IgnoreVictimRagdollCollisions(p);
        Vector3 hitPoint = p.transform.position + Vector3.up;

        NetworkPlayerRagdoll netRagdoll = p.GetComponent<NetworkPlayerRagdoll>();
        if (InNetworkSession && netRagdoll != null)
        {
            netRagdoll.RequestTrapHitFromServer(force, hitPoint, damage, ForceMode.VelocityChange);
            return;
        }

        PlayerRagdollController ragdoll = p.GetComponent<PlayerRagdollController>();
        bool survived = true;
        if (damage > 0f)
        {
            p.TakeDamage(damage);
            survived = !p.IsDead;
        }
        if (ragdoll != null)
            ragdoll.ActivateRagdoll(force, hitPoint, ForceMode.VelocityChange, allowAutoRecovery: survived);
    }

    /// <summary>
    /// His capsule ignores the victim's ragdoll BONES only (never their CharacterController — see the guard's
    /// hurricane-kick bug), so a body going limp at his feet can't shove him through the floor.
    /// </summary>
    void IgnoreVictimRagdollCollisions(PlayerHealth victim)
    {
        if (victim == null || characterController == null)
            return;
        PlayerRagdollController ragdoll = victim.GetComponent<PlayerRagdollController>();
        IReadOnlyList<Collider> bones = ragdoll != null ? ragdoll.RagdollColliders : null;
        if (bones == null)
            return;
        for (int i = 0; i < bones.Count; i++)
            if (bones[i] != null && bones[i] != characterController)
                Physics.IgnoreCollision(characterController, bones[i], true);
    }

    // ------------------------------------------------------------------
    // Arena / exit lock
    // ------------------------------------------------------------------

    HingeInteractDoor FindArenaDoor(Transform arenaRoot)
    {
        if (arenaRoot == null)
            return null;

        // The hall's entry door is the hinge door furthest from where he waits (the cab has sliding doors).
        HingeInteractDoor[] doors = arenaRoot.GetComponentsInChildren<HingeInteractDoor>(true);
        HingeInteractDoor best = null;
        float bestDistance = -1f;
        for (int i = 0; i < doors.Length; i++)
        {
            float d = (doors[i].transform.position - transform.position).sqrMagnitude;
            if (d > bestDistance)
            {
                best = doors[i];
                bestDistance = d;
            }
        }
        return best;
    }

    /// <summary>Server: lock (or release) the exit elevator nearest to him. Released only by his death.</summary>
    void SetExitLock(bool engaged)
    {
        if (_exitLockEngaged == engaged)
            return;

        ElevatorFinishController controller = FindExitController();
        if (controller == null)
            return;

        controller.ServerSetBossLock(engaged);
        _exitLockEngaged = engaged;
    }

    ElevatorFinishController FindExitController()
    {
        ElevatorFinishController[] all = FindObjectsByType<ElevatorFinishController>(FindObjectsInactive.Exclude);
        ElevatorFinishController best = null;
        float bestDistance = float.MaxValue;
        bool session = InNetworkSession;
        for (int i = 0; i < all.Length; i++)
        {
            ElevatorFinishController c = all[i];
            // In a session only the spawned replica is the brain; offline the in-piece copy is.
            if (session && !c.IsSpawned)
                continue;
            float d = (c.transform.position - _home).sqrMagnitude;
            if (d < bestDistance)
            {
                best = c;
                bestDistance = d;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------
    // Doors (SecurityGuardAI's technique: open unlocked leaves in his path, step out of a leaf's swing first)
    // ------------------------------------------------------------------

    void UpdateDoorHandling(ref Vector3 desiredHorizontalVelocity)
    {
        if (!canOpenDoors)
            return;

        if (_swingWaitDoor != null)
        {
            if (_swingWaitDoor.IsBusy && Time.time < _swingWaitUntil)
            {
                desiredHorizontalVelocity = TryGetSwingEscapeVelocity(out Vector3 escape) ? escape : Vector3.zero;
                _intendedMoveSpeed = desiredHorizontalVelocity.magnitude;
                _suppressMovementFacing = true;
                return;
            }
            _swingWaitDoor = null;
        }

        if (Time.time < _nextDoorProbeTime)
            return;
        _nextDoorProbeTime = Time.time + doorProbeInterval;

        HingeInteractDoor door = FindClosedDoorAhead(desiredHorizontalVelocity);
        if (door == null)
        {
            _backOffDoor = null;
            return;
        }

        bool hasSweep = door.TryGetSwingSweep(out _swingCenter, out _swingRadius);
        if (hasSweep && IsInsideSwingArc())
        {
            if (_backOffDoor != door)
            {
                _backOffDoor = door;
                _backOffDeadline = Time.time + doorBackOffMaxSeconds;
            }

            if (Time.time < _backOffDeadline && TryGetSwingEscapeVelocity(out Vector3 escape))
            {
                desiredHorizontalVelocity = escape;
                _intendedMoveSpeed = escape.magnitude;
                _suppressMovementFacing = true;
                _nextDoorProbeTime = Time.time + Mathf.Min(doorProbeInterval, 0.1f);
                return;
            }
        }

        _backOffDoor = null;
        if (!door.ServerAiOpenIfUnlocked() || !hasSweep)
            return;

        _swingWaitDoor = door;
        _swingWaitUntil = Time.time + 3f;
    }

    HingeInteractDoor FindClosedDoorAhead(Vector3 desiredHorizontalVelocity)
    {
        Vector3 direction = desiredHorizontalVelocity;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.01f)
        {
            direction = transform.forward;
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-4f)
                return null;
        }
        direction.Normalize();

        float reach = Mathf.Max(doorProbeDistance, _horizontalVelocity.magnitude * doorLookAheadSeconds);
        Vector3 origin = transform.position + Vector3.up;
        int hitCount = Physics.SphereCastNonAlloc(origin, doorProbeRadius, direction, _doorProbeHits, reach,
            EnvironmentMask(), QueryTriggerInteraction.Ignore);

        HingeInteractDoor found = null;
        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = _doorProbeHits[i].collider;
            _doorProbeHits[i] = default;
            if (hit == null || found != null)
                continue;

            HingeInteractDoor door = hit.GetComponentInParent<HingeInteractDoor>();
            if (door == null || door.IsOpen || door.IsLocked || door.IsBusy)
                continue;
            found = door;
        }
        return found;
    }

    bool IsInsideSwingArc()
    {
        float radius = _swingRadius + (characterController != null ? characterController.radius * transform.lossyScale.x : 0.35f)
            + doorSwingClearance;
        Vector3 offset = transform.position - _swingCenter;
        offset.y = 0f;
        return offset.sqrMagnitude < radius * radius;
    }

    bool TryGetSwingEscapeVelocity(out Vector3 velocity)
    {
        velocity = Vector3.zero;
        if (!IsInsideSwingArc())
            return false;

        Vector3 away = transform.position - _swingCenter;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-4f)
        {
            away = -transform.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f)
                return false;
        }

        velocity = away.normalized * doorBackOffSpeed;
        return true;
    }

    // ------------------------------------------------------------------
    // Movement / animator plumbing
    // ------------------------------------------------------------------

    void ApplyMovement(Vector3 desiredHorizontalVelocity)
    {
        if (characterController == null || !characterController.enabled)
            return;

        if (characterController.isGrounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -groundedStickDown;

        _horizontalVelocity = _state == State.Stagger ? Vector3.zero : desiredHorizontalVelocity;
        _verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 motion = _horizontalVelocity * Time.deltaTime;
        motion.y = _verticalVelocity.y * Time.deltaTime;
        characterController.Move(motion);

        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.nextPosition = transform.position;

        if (_suppressMovementFacing)
        {
            RotateTowards(_swingCenter, rotationSpeed);
            return;
        }

        // Abilities own his facing (the sweep especially: the spin is on the hips, the root must hold still).
        if (_state == State.Ability)
            return;

        Vector3 flat = _horizontalVelocity;
        flat.y = 0f;
        if (flat.sqrMagnitude > 0.0001f)
        {
            float turn = _crawling ? crawlRotationSpeed : rotationSpeed;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(flat.normalized), turn * Time.deltaTime);
        }
    }

    void UpdateAnimatorParameters()
    {
        if (animator == null)
            return;

        bool crawling = _crawling && _state == State.Hunt;
        animator.SetBool(crawlingParameter, crawling);

        float reference = crawling ? CrawlSpeedNow : CreepSpeedNow;
        animator.SetFloat(speedParameter, reference > 0.001f ? Mathf.Clamp01(_intendedMoveSpeed / reference) : 0f);

        float clipSpeed = crawling ? crawlClipGroundSpeed : walkClipGroundSpeed;
        Vector2 range = crawling ? crawlStrideRange : walkStrideRange;
        float actual = _horizontalVelocity.magnitude;
        animator.SetFloat(strideRateParameter, Mathf.Clamp(actual / Mathf.Max(0.01f, clipSpeed), range.x, range.y));
    }

    void PlayState(string stateName, float fadeSeconds)
    {
        if (animator == null || IsNetworkClient)
            return;
        animator.CrossFadeInFixedTime(stateName, fadeSeconds, 0, 0f);
    }

    void FaceTarget(float degreesPerSecond)
    {
        if (_targetHealth != null)
            RotateTowards(BodyPosition(_targetHealth), degreesPerSecond);
    }

    void RotateTowards(Vector3 worldPoint, float degreesPerSecond)
    {
        Vector3 look = worldPoint - transform.position;
        look.y = 0f;
        if (look.sqrMagnitude < 0.0001f)
            return;
        transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(look.normalized),
            degreesPerSecond * Time.deltaTime);
    }

    float FlatDistance(Vector3 point)
    {
        Vector3 d = point - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    Vector3 ClampedDesiredVelocity(float maxSpeed)
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || !navMeshAgent.isOnNavMesh)
            return Vector3.zero;

        Vector3 desired = navMeshAgent.desiredVelocity;
        desired.y = 0f;
        if (desired.sqrMagnitude > maxSpeed * maxSpeed)
            desired = desired.normalized * maxSpeed;
        return desired;
    }

    void StopAgent()
    {
        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }
    }

    bool TrySnapToNavMesh()
    {
        if (navMeshAgent == null || !navMeshAgent.enabled)
            return false;
        if (navMeshAgent.isOnNavMesh)
            return true;

        float[] radii = { 2f, 6f, 12f };
        for (int i = 0; i < radii.Length; i++)
            if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, radii[i], NavMesh.AllAreas))
                return navMeshAgent.Warp(hit.position);
        return false;
    }

    void ApplyAgentSettings()
    {
        if (navMeshAgent == null)
            return;

        navMeshAgent.speed = creepSpeed;
        navMeshAgent.angularSpeed = rotationSpeed;
        navMeshAgent.acceleration = Mathf.Max(navMeshAgent.acceleration, crawlSpeed * 4f);
        navMeshAgent.updatePosition = false;
        navMeshAgent.updateRotation = false;
        navMeshAgent.baseOffset = 0f;
        navMeshAgent.avoidancePriority = 10; // right of way over everything else in the maze

        if (characterController != null)
        {
            characterController.skinWidth = 0.02f;
            characterController.minMoveDistance = 0.001f;
        }
    }

    int _environmentMask = -1;

    /// <summary>Walls/props only: players and enemies never block his senses or his limbs.</summary>
    int EnvironmentMask()
    {
        if (_environmentMask != -1)
            return _environmentMask;

        int mask = sightMask.value == 0 ? Physics.DefaultRaycastLayers : sightMask.value;
        foreach (string layer in new[] { "Player", "Enemy", "Clown", "Jailor", "Ignore Raycast" })
        {
            int index = LayerMask.NameToLayer(layer);
            if (index >= 0)
                mask &= ~(1 << index);
        }
        _environmentMask = mask;
        return mask;
    }

    void CacheReferences()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (navMeshAgent == null) navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null) characterController = GetComponent<CharacterController>();
        if (limbRig == null) limbRig = GetComponent<MutantLimbRig>();
        if (health == null) health = GetComponent<MutantHealth>();

        if (_leftToe == null)
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                switch (all[i].name)
                {
                    case "mixamorig:LeftToeBase": _leftToe = all[i]; break;
                    case "mixamorig:RightToeBase": _rightToe = all[i]; break;
                    case "mixamorig:LeftHand": _leftHand = all[i]; break;
                    case "mixamorig:RightHand": _rightHand = all[i]; break;
                }
            }
        }
    }

    /// <param name="route">
    /// Hand the sources to <see cref="GameAudioManager"/> (occlusion filter, mixer bus). Only from Awake at runtime:
    /// routing can create the occlusion manager, which Unity forbids inside OnValidate, and in edit mode it would
    /// bake the filter into the prefab.
    /// </param>
    void ConfigureAudioSources(bool allowCreate = true, bool route = false)
    {
        footstepAudioSource = ConfigureSource(footstepAudioSource, FootstepAudioChildName, allowCreate, route, 1.5f, 26f, AudioRolloffMode.Linear);
        // Custom = the logarithmic law faded to silence (see FadingLogRolloff), not Unity's Logarithmic.
        fxAudioSource = ConfigureSource(fxAudioSource, FxAudioChildName, allowCreate, route, 2f, 34f, AudioRolloffMode.Custom);
        voiceAudioSource = ConfigureSource(voiceAudioSource, VoiceAudioChildName, allowCreate, route, 2.5f, 40f, AudioRolloffMode.Custom);
        breathAudioSource = ConfigureSource(breathAudioSource, BreathAudioChildName, allowCreate, route,
            breathingMinDistance, Mathf.Max(breathingMaxDistance, breathingMinDistance + 0.5f), AudioRolloffMode.Linear);
    }

    AudioSource ConfigureSource(AudioSource existing, string childName, bool allowCreate, bool route,
        float minDistance, float maxDistance, AudioRolloffMode rolloff)
    {
        AudioSource source = existing;
        if (source == null)
        {
            Transform child = transform.Find(childName);
            if (child == null && allowCreate)
            {
                child = new GameObject(childName).transform;
                child.SetParent(transform, false);
                child.localPosition = Vector3.up * 1.6f;
            }
            if (child != null)
            {
                source = child.GetComponent<AudioSource>();
                if (source == null && allowCreate)
                    source = child.gameObject.AddComponent<AudioSource>();
            }
        }

        if (source == null)
            return null;

        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 1f;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        source.rolloffMode = rolloff;
        if (rolloff == AudioRolloffMode.Custom)
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, FadingLogRolloff(minDistance, maxDistance));
        source.dopplerLevel = 0f;
        if (route && Application.isPlaying)
            GameAudioManager.RouteSfxSource(source);
        return source;
    }

    /// <summary>
    /// Unity's Logarithmic rolloff never reaches silence — past maxDistance it only keeps falling as 1/distance
    /// (his breathing, when it shared this law, measured -31 dB at 90 m relative to 1 m: plainly audible) — and
    /// <see cref="AudioOcclusionManager"/> stops muffling anything past maxDistance on the assumption it is inaudible
    /// there. So he was faintly audible, unmuffled, from anywhere in the level. This keeps the same 1/distance law
    /// up close and fades it to exactly zero over the far half of the range. Times are distance / maxDistance, as
    /// the Custom rolloff curve expects.
    /// </summary>
    static AnimationCurve FadingLogRolloff(float minDistance, float maxDistance)
    {
        var curve = new AnimationCurve();
        curve.AddKey(0f, 1f);

        float max = Mathf.Max(maxDistance, minDistance + 0.5f);
        for (float d = Mathf.Max(0.05f, minDistance); ; d *= 1.35f)
        {
            float t = Mathf.Min(1f, d / max);
            float fade = Mathf.Clamp01((1f - t) * 2f); // 1 up to half the range, 0 at the max
            fade = fade * fade * (3f - 2f * fade);
            curve.AddKey(t, Mathf.Min(1f, minDistance / Mathf.Min(d, max)) * fade);
            if (t >= 1f)
                break;
        }
        return curve;
    }

    /// <summary>Closest distance between segments p1-q1 and p2-q2; <paramref name="s"/> is the fraction along the first.</summary>
    static float SegmentSegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float s)
    {
        Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float t;
        if (a <= 1e-6f && e <= 1e-6f) { s = t = 0f; return (p1 - p2).magnitude; }
        if (a <= 1e-6f) { s = 0f; t = Mathf.Clamp01(f / e); }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= 1e-6f) { t = 0f; s = Mathf.Clamp01(-c / a); }
            else
            {
                float b = Vector3.Dot(d1, d2), denom = a * e - b * b;
                s = denom > 1e-6f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0f) { t = 0f; s = Mathf.Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; s = Mathf.Clamp01((b - c) / a); }
            }
        }
        return ((p1 + d1 * s) - (p2 + d2 * t)).magnitude;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, wakeProximity);
        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, reachMaxRange);
        Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, sweepArmLength);
    }
}
