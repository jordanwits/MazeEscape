using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class ZombieAI : MonoBehaviour
{
    const string VoiceAudioChildName = "Zombie_Voice";
    const string FootstepAudioChildName = "Zombie_Footsteps";

    enum ZombieState
    {
        Idle,
        Chase,
        Attack,
        HitReaction,
        Dead
    }

    [Header("References")]
    [SerializeField] Animator animator;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [SerializeField] ZombieHealth zombieHealth;

    [Header("Voice SFX")]
    [SerializeField] AudioSource voiceAudioSource;
    [FormerlySerializedAs("zombieBreathingClip")]
    [SerializeField] AudioClip zombieGroanClip;
    [FormerlySerializedAs("breathingVolume")]
    [SerializeField, Range(0f, 1f)] float groanVolume = 0.3f;
    [SerializeField] AudioClip zombieDeathClip;
    [SerializeField, Range(0f, 1f)] float deathVoiceVolume = 1f;
    [SerializeField, Range(0f, 1f)] float voiceSpatialBlend = 1f;
    [SerializeField, Min(0.01f)] float voice3DMinDistance = 2f;
    [SerializeField, Min(0.01f)] float voice3DMaxDistance = 70f;
    [Tooltip("Delay after each groan before the next (including after the alert groan).")]
    [FormerlySerializedAs("groanRepeatMinSeconds")]
    [SerializeField, Min(0.1f)] float groanRepeatIntervalSeconds = 7f;

    [Header("Footsteps")]
    [SerializeField] AudioSource footstepAudioSource;
    [SerializeField] AudioClip footstepClip1;
    [SerializeField] AudioClip footstepClip2;
    [SerializeField] AudioClip footstepClip3;
    [SerializeField] AudioClip footstepClip4;
    [SerializeField] float walkFootstepInterval = 0.48f;
    [SerializeField] float footstepVolume = 0.6f;
    [SerializeField] float minimumFootstepSpeed = 0.15f;

    [Header("Detection")]
    [SerializeField] LayerMask detectionMask;
    [SerializeField] float detectionRadius = 10f;
    [SerializeField] float loseTargetRadiusMultiplier = 1.5f;
    [SerializeField] float attackRadius = 2f;
    [SerializeField] float targetNavMeshSampleRadius = 3f;
    [Tooltip("Half-angle of the vision cone, from facing. Players outside it are only found by sound/damage — sneaking behind works. 180 restores the old omniscient detection.")]
    [SerializeField, Range(10f, 180f)] float detectionFovHalfAngleDegrees = 100f;
    [Tooltip("Radius at which the zombie HEARS an audibly-sprinting player and shuffles toward them — no vision cone or line-of-sight needed (you're heard from behind and through walls). Walking is silent; only sprinting is heard. 0 disables hearing.")]
    [SerializeField, Min(0f)] float hearingRadius = 14f;
    [Tooltip("If enabled, zombies only become alerted when they can see the player.")]
    [SerializeField] bool requireDetectionLineOfSight = true;
    [Tooltip("Layers considered solid when checking whether detection is blocked.")]
    [SerializeField] LayerMask detectionLineOfSightMask = Physics.DefaultRaycastLayers;
    [Tooltip("Height used for the detection obstruction check so the ray aims roughly at chest level.")]
    [SerializeField] float detectionLineOfSightHeight = 1.1f;
    [Tooltip(
        "Seconds between target-acquisition scans (the OverlapSphere + sight rays that only run while the "
            + "Zombie has NO target). Movement and chasing stay per-frame, so this only adds up to this much "
            + "latency to first spotting a player — imperceptible at <= 0.15s and a real CPU saver. Set 0 to "
            + "scan every frame (original behaviour).")]
    [SerializeField, Min(0f)] float sensingInterval = 0.1f;

    [Header("Movement")]
    [SerializeField] float walkSpeed = 1.5f;
    [SerializeField] float rotationSpeed = 720f;
    [SerializeField] float gravity = -20f;
    [SerializeField] float groundedStickDown = 2f;
    [SerializeField] float pitProbeForwardDistance = 0.75f;
    [SerializeField] float pitProbeDepth = 4f;
    [SerializeField] float pitDropMinHeight = 1f;
    [SerializeField] float pitDropMaxHeight = 12f;
    [SerializeField] float pitDropCommitDuration = 0.45f;

    [Header("Step Rhythm")]
    [Tooltip("Maps walk animation normalized time (0-1) to speed multiplier. Shape this to match footstep timing.")]
    [SerializeField] AnimationCurve walkStepCurve = DefaultWalkStepCurve();
    [Tooltip("How quickly the actual speed blends toward the curve target. Higher = snappier steps.")]
    [SerializeField] float stepSpeedSmoothing = 15f;

    [Header("Combat")]
    [SerializeField] float damage = 25f;
    [SerializeField] float attackRate = 1.5f;
    [Tooltip("Extra distance before Attack starts so the zombie begins the swing before NavMesh settling causes foot jitter.")]
    [SerializeField] float attackStartDistancePadding = 0.35f;
    [Tooltip("Seconds between the attack start and the frame where the swipe should actually deal damage.")]
    [SerializeField] float attackHitDelay = 0.45f;
    [Tooltip("Attack2-only hit timing. Lets the retaliatory swing land later without affecting Attack 1.")]
    [SerializeField] float counterAttackHitDelay = 1f;
    [Tooltip("Extra reach added on top of Attack Radius when the swipe damage is checked.")]
    [SerializeField] float attackHitRangePadding = 0.15f;
    [Tooltip("How wide the committed swipe can hit. Lower values make side-steps dodge more reliably.")]
    [SerializeField, Range(0f, 180f)] float attackHitHalfAngle = 55f;
    [Tooltip("If enabled, the zombie only lands the swipe when nothing solid is between it and the player.")]
    [SerializeField] bool requireAttackLineOfSight = true;
    [Tooltip("Layers considered solid when checking whether the swipe is blocked.")]
    [SerializeField] LayerMask attackLineOfSightMask = Physics.DefaultRaycastLayers;
    [Tooltip("Height used for the swipe obstruction check so the ray aims roughly at chest level.")]
    [SerializeField] float attackLineOfSightHeight = 1.1f;

    [Header("Poise (anti stun-lock)")]
    [Tooltip("Stagger meter. Punches subtract Punch Poise Damage; the full-body stagger only plays when this reaches 0. Hits that don't break poise cause a brief upper-body flinch that never interrupts movement or attacks.")]
    [SerializeField, Min(1f)] float maxPoise = 100f;
    [Tooltip("Poise removed per hit taken. 50 = two quick punches break poise.")]
    [SerializeField, Min(0f)] float punchPoiseDamage = 50f;
    [Tooltip("Seconds after the last hit before poise starts regenerating.")]
    [SerializeField, Min(0f)] float poiseRegenDelay = 1.5f;
    [SerializeField, Min(0f)] float poiseRegenPerSecond = 30f;
    [Tooltip("Length of the full-body stagger played when poise breaks.")]
    [SerializeField, Min(0.1f)] float poiseBreakStaggerSeconds = 1.1f;
    [Tooltip("After a poise break the zombie cannot be staggered again for this long (hits still damage and flinch, and counters are more likely).")]
    [SerializeField, Min(0f)] float staggerImmunitySeconds = 4f;

    [Header("Counter attack")]
    [Tooltip("Chance that a landed punch triggers an immediate retaliatory Attack2 swipe. Rolled per hit — there is no safe punch rhythm to memorize.")]
    [SerializeField, Range(0f, 1f)] float counterChance = 0.35f;
    [Tooltip("Counter chance while stagger-immune after a poise break — punching a zombie that is powering through is extra dangerous.")]
    [SerializeField, Range(0f, 1f)] float counterChanceWhileImmune = 0.65f;
    [Tooltip("Minimum seconds between counter attempts.")]
    [SerializeField, Min(0f)] float counterCooldownSeconds = 2.5f;
    [Tooltip("The attacker must be within this range for a counter to trigger.")]
    [SerializeField, Min(0f)] float counterTriggerRange = 3f;

    [Header("Rage")]
    [Tooltip("At or below this health fraction the zombie permanently enrages: it runs, attacks faster and senses further.")]
    [SerializeField, Range(0f, 1f)] float enrageAtHealthFraction = 0.5f;
    [SerializeField, Min(1f)] float enragedSpeedMultiplier = 1.65f;
    [Tooltip("Multiplies the attack cooldown while enraged (lower = faster attacks).")]
    [SerializeField, Range(0.1f, 1f)] float enragedAttackRateMultiplier = 0.65f;
    [SerializeField, Min(1f)] float enragedDetectionRadiusMultiplier = 1.4f;

    [Header("Animator")]
    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string groundedParameter = "Grounded";
    [SerializeField] string verticalVelocityParameter = "VerticalVelocity";
    [SerializeField] float attackCrossfadeDuration = 0.08f;
    [SerializeField] string attackTrigger = "Attack";
    [SerializeField] string counterAttackStateName = "Attack2";
    [Tooltip("Extra blend time when easing the retaliatory Attack2 back out to the empty upper-body pose.")]
    [SerializeField] float counterAttackExitCrossfadeDuration = 0.22f;
    [SerializeField] string hitReactionTrigger = "HitReaction";
    [Tooltip("Upper-body flinch state played for hits that don't break poise (auto-exits via its transition).")]
    [SerializeField] string flinchStateName = "HitFlinch";
    [Tooltip("Animator bool that switches locomotion to the Run state while enraged.")]
    [SerializeField] string enragedAnimatorBool = "Enraged";
    [Tooltip("Blend time when exiting hit reaction back into locomotion.")]
    [SerializeField] float hitReactionExitCrossfadeDuration = 0.18f;
    [Tooltip("Layer index for upper body attacks. Set to 1 if using Avatar Mask layering.")]
    [SerializeField] int upperBodyLayerIndex = 1;
    [Tooltip("Allow the zombie to move while attacking (uses upper body layer).")]
    [SerializeField] bool allowMoveWhileAttacking = true;

    [Header("Hit reaction wall clamp")]
    [Tooltip("Clamp the hit-reaction stagger so the body can't lean through walls/props — the React clip shoves " +
             "the mesh well past the collision capsule, so it would clip a wall behind it.")]
    [SerializeField] bool clampHitReactionToWalls = true;
    [SerializeField] LayerMask hitReactionWallMask = Physics.DefaultRaycastLayers;
    [Tooltip("Half-thickness of the body used when checking how far it can lean before hitting a wall.")]
    [SerializeField] float hitReactionBodyRadius = 0.32f;
    [SerializeField] float hitReactionCastHeight = 1.1f;

    readonly Collider[] _detectionHits = new Collider[16];
    readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];
    float _nextSenseTime = -1f;

    ZombieState _state;
    Transform _target;
    PlayerHealth _targetHealth;
    float _nextAttackTime;
    float _hitReactionEndTime;
    Coroutine _attackRoutine;
    Vector3 _horizontalVelocity;
    Vector3 _verticalVelocity;
    float _currentStepMultiplier;
    float _intendedMoveSpeed;
    bool _pitDropActive;
    float _pitDropUnlockTime;
    float _poise;
    float _poiseRegenBlockedUntil;
    float _staggerImmuneUntil;
    float _nextCounterRollTime;
    bool _isEnraged;
    float _footstepTimer;
    readonly List<AudioClip> _footstepPool = new List<AudioClip>(4);
    int[] _footstepShuffle;
    int _footstepShuffleIndex;
    float _nextGroanTime = -1f;

    NetworkObject _networkObject;
    Transform _hipsBone;
    bool _clientAudioInitialized;
    Vector3 _clientLastPositionForAudio;
    float _clientFootstepTimer;
    float _clientNextGroanTime = -1f;
    /// <summary>Non-host clients: require motion/groan proxy to stay below threshold this long before resetting — stops NetTransform/anim jitter from stacking SFX.</summary>
    float _clientFootstepBelowSpeedTimer;
    float _clientGroanAggroOffTimer;

    float EffectiveMoveSpeed => _isEnraged ? walkSpeed * enragedSpeedMultiplier : walkSpeed;
    float EffectiveAttackRate => _isEnraged ? attackRate * enragedAttackRateMultiplier : attackRate;
    float EffectiveDetectionRadius => _isEnraged ? detectionRadius * enragedDetectionRadiusMultiplier : detectionRadius;

    /// <summary>
    /// True while this zombie is actively producing audible SFX (groan/footsteps).
    /// Used by other AI (e.g. Jailor) as a simple "heard noise from zombie" signal.
    /// </summary>
    public bool IsMakingNoiseForAi
    {
        get
        {
            if (_state == ZombieState.Dead)
                return false;
            return (voiceAudioSource != null && voiceAudioSource.isPlaying)
                || (footstepAudioSource != null && footstepAudioSource.isPlaying);
        }
    }

    void Reset()
    {
        CacheReferences();
        ConfigureVoiceAudioSource();
        ConfigureFootstepAudioSource();
        RemoveOrphanedRootAudioSources();
        ApplyAgentSettings();
        RebuildFootstepPool();
#if UNITY_EDITOR
        AutoAssignAudioClipsInEditor();
#endif
    }

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
        _poise = maxPoise;
        CacheReferences();
        ConfigureVoiceAudioSource();
        ConfigureFootstepAudioSource();
        RemoveOrphanedRootAudioSources();
        ApplyAgentSettings();
        RebuildFootstepPool();
#if UNITY_EDITOR
        AutoAssignAudioClipsInEditor();
#endif
    }

    void OnEnable()
    {
        TrySnapToNavMesh();
        ZombieAIRegistry.Register(this);
    }

    void OnDisable()
    {
        ZombieAIRegistry.Unregister(this);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        groanRepeatIntervalSeconds = Mathf.Max(0.1f, groanRepeatIntervalSeconds);
        CacheReferences();
        ConfigureVoiceAudioSource(allowCreate: false);
        ConfigureFootstepAudioSource(allowCreate: false);
        RebuildFootstepPool();
        AutoAssignAudioClipsInEditor();
    }
#endif

    void Update()
    {
        bool isNetworkClient = _networkObject != null
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer;

        if (isNetworkClient)
        {
            UpdateNetworkClientAudio();
            return;
        }

        if (zombieHealth != null && zombieHealth.IsDead)
        {
            HandleDeath();
            return;
        }

        if (_state == ZombieState.Dead)
            return;

        if (_poise < maxPoise && Time.time >= _poiseRegenBlockedUntil)
            _poise = Mathf.Min(maxPoise, _poise + poiseRegenPerSecond * Time.deltaTime);

        UpdatePeriodicGroan();
        // Throttle the acquisition scan. RefreshTarget() already no-ops once a target is held, so this only
        // paces the expensive search-phase OverlapSphere/rays; chasing and attacking stay per-frame.
        if (_nextSenseTime < 0f)
            _nextSenseTime = Time.time + Random.Range(0f, Mathf.Max(0f, sensingInterval)); // stagger agents
        if (sensingInterval <= 0f || Time.time >= _nextSenseTime)
        {
            _nextSenseTime = Time.time + Mathf.Max(0f, sensingInterval);
            RefreshTarget();
        }
        if (IsPlayerCarriedByJailor(_targetHealth))
        {
            ClearTarget();
            EnterIdle();
        }

        Vector3 desiredHorizontalVelocity = Vector3.zero;
        bool inHitReaction = _state == ZombieState.HitReaction && Time.time < _hitReactionEndTime;
        if (_targetHealth == null || _targetHealth.IsDead)
        {
            if (!inHitReaction)
            {
                ClearTarget();
                EnterIdle();
            }
        }
        else
        {
            float distanceToTarget = Vector3.Distance(transform.position, _target.position);
            float loseTargetRadius = Mathf.Max(EffectiveDetectionRadius, EffectiveDetectionRadius * loseTargetRadiusMultiplier);
            if (distanceToTarget > loseTargetRadius && !inHitReaction)
            {
                ClearTarget();
                EnterIdle();
            }
            else
            {
                switch (_state)
                {
                    case ZombieState.Idle:
                        TryStartChase();
                        break;
                    case ZombieState.Chase:
                        desiredHorizontalVelocity = UpdateChase(distanceToTarget);
                        break;
                    case ZombieState.Attack:
                        UpdateAttack();
                        break;
                    case ZombieState.HitReaction:
                        UpdateHitReaction();
                        break;
                }
            }
        }

        ApplyMovement(desiredHorizontalVelocity);
        UpdateFootsteps();
        UpdateAnimatorParameters();
    }

    void LateUpdate()
    {
        // The hit-reaction clip leans the mesh far past the collision capsule; clamp the upper body so it can't
        // pass through a wall/prop behind it. Runs on every peer (keyed off the replicated animator state, not the
        // server-only AI state).
        if (!clampHitReactionToWalls || animator == null || _hipsBone == null)
            return;
        if (!IsAnimatorInState(0, "HitReaction"))
            return;

        Vector3 root = transform.position;
        Vector3 hips = _hipsBone.position;
        Vector3 horizontal = hips - root;
        horizontal.y = 0f;
        float distance = horizontal.magnitude;
        if (distance < 0.02f)
            return;

        Vector3 dir = horizontal / distance;
        int mask = MaskExcludingActors(hitReactionWallMask);
        if (mask == 0)
            return;

        Vector3 castOrigin = root + Vector3.up * hitReactionCastHeight;
        if (Physics.SphereCast(castOrigin, hitReactionBodyRadius, dir, out RaycastHit hit, distance, mask, QueryTriggerInteraction.Ignore))
        {
            float allowed = Mathf.Max(0f, hit.distance);
            if (allowed < distance)
                _hipsBone.position += dir * (allowed - distance); // pull the upper body back to the wall surface
        }
    }

    int MaskExcludingActors(LayerMask source)
    {
        int mask = source.value == 0 ? Physics.DefaultRaycastLayers : source.value;
        int playerLayer = LayerMask.NameToLayer("Player");
        int enemyLayer = LayerMask.NameToLayer("Enemy");
        if (playerLayer >= 0) mask &= ~(1 << playerLayer);
        if (enemyLayer >= 0) mask &= ~(1 << enemyLayer);
        return mask;
    }

    public void HandleDeath()
    {
        if (_state == ZombieState.Dead)
            return;

        _state = ZombieState.Dead;

        if (_attackRoutine != null)
        {
            StopCoroutine(_attackRoutine);
            _attackRoutine = null;
        }

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        if (animator != null)
        {
            ResetAllTriggers();
            if (upperBodyLayerIndex > 0)
            {
                animator.Play("Empty", upperBodyLayerIndex, 0f);
                animator.SetLayerWeight(upperBodyLayerIndex, 0f);
            }
        }

        _horizontalVelocity = Vector3.zero;
        _verticalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;
        _pitDropActive = false;
        _staggerImmuneUntil = 0f;
        _nextCounterRollTime = 0f;
        _footstepTimer = 0f;
        _nextGroanTime = -1f;
        _clientFootstepTimer = 0f;
        _clientNextGroanTime = -1f;
        _clientFootstepBelowSpeedTimer = 0f;
        _clientGroanAggroOffTimer = 0f;

        if (voiceAudioSource != null)
            voiceAudioSource.Stop();

        if (footstepAudioSource != null)
            footstepAudioSource.Stop();

        PlayDeathVoice();
    }

    void UpdateNetworkClientAudio()
    {
        if (_state == ZombieState.Dead)
            return;

        // Server zeros velocity during HitReaction in ApplyMovement; match that here or transform jitter stacks footsteps/groans with melee impact.
        if (animator != null && IsAnimatorInState(0, "HitReaction"))
            return;

        if (!_clientAudioInitialized)
        {
            _clientLastPositionForAudio = transform.position;
            _clientAudioInitialized = true;
            return;
        }

        Vector3 pos = transform.position;
        Vector3 horizontalDelta = pos - _clientLastPositionForAudio;
        horizontalDelta.y = 0f;
        _clientLastPositionForAudio = pos;

        float dt = Time.deltaTime;
        float horizontalSpeed = dt > 1e-6f ? horizontalDelta.magnitude / dt : 0f;

        float animSpeed = animator != null ? animator.GetFloat(speedParameter) : 0f;
        bool aggroVoiceProxy =
            horizontalSpeed >= minimumFootstepSpeed * 0.75f
            || animSpeed > 0.03f;

        UpdateClientFootstepsFromMotion(horizontalSpeed);
        UpdateClientPeriodicGroanFromMotion(aggroVoiceProxy);
    }

    void UpdateClientFootstepsFromMotion(float horizontalSpeed)
    {
        if (footstepAudioSource == null || _state == ZombieState.Dead)
            return;

        if (horizontalSpeed < minimumFootstepSpeed)
        {
            _clientFootstepBelowSpeedTimer += Time.deltaTime;
            if (_clientFootstepBelowSpeedTimer >= 0.12f)
                _clientFootstepTimer = 0f;
            return;
        }

        _clientFootstepBelowSpeedTimer = 0f;

        float cadenceScale = Mathf.Clamp(walkSpeed / Mathf.Max(horizontalSpeed, 0.01f), 0.55f, 1f);
        float interval = Mathf.Max(0.05f, walkFootstepInterval * 2f * cadenceScale);
        _clientFootstepTimer -= Time.deltaTime;
        if (_clientFootstepTimer > 0f)
            return;

        PlayFootstepOneShot();
        _clientFootstepTimer = interval;
    }

    void UpdateClientPeriodicGroanFromMotion(bool aggroVoiceProxy)
    {
        if (voiceAudioSource == null || zombieGroanClip == null || _state == ZombieState.Dead)
            return;

        if (!aggroVoiceProxy)
        {
            _clientGroanAggroOffTimer += Time.deltaTime;
            if (_clientGroanAggroOffTimer >= 0.22f)
                _clientNextGroanTime = -1f;
            return;
        }

        _clientGroanAggroOffTimer = 0f;

        if (_clientNextGroanTime < 0f)
        {
            PlayGroanAndScheduleNextClient();
            return;
        }

        if (Time.time < _clientNextGroanTime)
            return;

        PlayGroanAndScheduleNextClient();
    }

    void PlayGroanAndScheduleNextClient()
    {
        voiceAudioSource.PlayOneShot(zombieGroanClip, Mathf.Clamp01(groanVolume));
        _clientNextGroanTime = Time.time + groanRepeatIntervalSeconds;
    }

    /// <summary>
    /// Server-side reaction to surviving a hit (called by <see cref="ZombieHealth"/> after damage applies).
    /// Damage is never blocked any more; this only decides how the zombie reacts:
    ///   • mid-swing — hyper-armor: the committed attack keeps coming (poise chips but cannot break),
    ///   • poise break — the one full-body stagger, followed by a stagger-immunity window,
    ///   • otherwise — a cosmetic upper-body flinch, with a per-hit chance to counter with Attack2.
    /// </summary>
    public void OnDamageTaken(bool fromPlayerMelee, Transform attacker, PlayerHealth attackerHealth)
    {
        if (_state == ZombieState.Dead)
            return;

        AssignAttackerAsTarget(attacker, attackerHealth);
        RefreshRageState();

        _poiseRegenBlockedUntil = Time.time + poiseRegenDelay;

        // Hyper-armor: once a swipe is committed, hits can't interrupt it. Poise still wears down but is
        // floored so the break can never trigger mid-swing (it would cancel the attack and re-open the
        // punch-to-cancel exploit this system removes).
        if (_attackRoutine != null)
        {
            _poise = Mathf.Max(1f, _poise - punchPoiseDamage);
            return;
        }

        bool staggerImmune = Time.time < _staggerImmuneUntil;
        if (!staggerImmune)
        {
            _poise -= punchPoiseDamage;
            if (_poise <= 0f)
            {
                BeginPoiseBreakStagger();
                return;
            }
        }

        if (fromPlayerMelee && TryStartCounterAttack(staggerImmune))
            return;

        PlayHitFlinch();
    }

    /// <summary>The earned full-body stagger: only reachable by depleting poise, and never repeatable back-to-back.</summary>
    void BeginPoiseBreakStagger()
    {
        if (_attackRoutine != null)
        {
            StopCoroutine(_attackRoutine);
            _attackRoutine = null;
        }

        _poise = maxPoise;
        _state = ZombieState.HitReaction;
        _hitReactionEndTime = Time.time + poiseBreakStaggerSeconds;
        _staggerImmuneUntil = _hitReactionEndTime + staggerImmunitySeconds;
        _nextAttackTime = _hitReactionEndTime + EffectiveAttackRate * 0.5f;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        _horizontalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;
        _pitDropActive = false;

        if (animator != null)
        {
            ResetAllTriggers();
            if (upperBodyLayerIndex > 0)
            {
                animator.Play("Empty", upperBodyLayerIndex, 0f);
                animator.SetLayerWeight(upperBodyLayerIndex, 0f);
            }
            animator.CrossFadeInFixedTime("HitReaction", 0.1f, 0, 0f);
        }
    }

    /// <summary>Quick masked recoil on the upper-body layer; the legs (and any chase) keep going.</summary>
    void PlayHitFlinch()
    {
        if (animator == null || upperBodyLayerIndex <= 0 || string.IsNullOrEmpty(flinchStateName))
            return;
        if (_state == ZombieState.HitReaction)
            return;

        animator.SetLayerWeight(upperBodyLayerIndex, 1f);
        animator.CrossFadeInFixedTime(flinchStateName, 0.05f, upperBodyLayerIndex, 0f);
    }

    /// <summary>Per-hit counter roll. Replaces the old fixed 'punched twice within X seconds' window.</summary>
    bool TryStartCounterAttack(bool staggerImmune)
    {
        if (_state == ZombieState.HitReaction && Time.time < _hitReactionEndTime)
            return false;
        if (Time.time < _nextCounterRollTime)
            return false;
        if (_targetHealth == null || _targetHealth.IsDead || _target == null)
            return false;

        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.magnitude > counterTriggerRange)
            return false;

        float chance = staggerImmune ? counterChanceWhileImmune : counterChance;
        if (Random.value > chance)
            return false;

        _nextCounterRollTime = Time.time + counterCooldownSeconds;
        StartCounterAttack();
        return true;
    }

    /// <summary>Permanent low-health rage: run locomotion, faster attacks, wider senses.</summary>
    void RefreshRageState()
    {
        if (_isEnraged || zombieHealth == null || zombieHealth.IsDead)
            return;
        if (zombieHealth.MaxHealth <= 0f
            || zombieHealth.CurrentHealth / zombieHealth.MaxHealth > enrageAtHealthFraction)
            return;

        _isEnraged = true;
        if (animator != null && !string.IsNullOrEmpty(enragedAnimatorBool))
            animator.SetBool(enragedAnimatorBool, true);

        // Rage roar so the speed-up reads as a deliberate turn, not a glitch.
        _nextGroanTime = -1f;
        PlayGroanAndScheduleNext();
    }

    void UpdateHitReaction()
    {
        FaceTarget();

        if (Time.time < _hitReactionEndTime)
            return;

        if (animator != null)
        {
            ResetAllTriggers();
            if (upperBodyLayerIndex > 0)
            {
                animator.Play("Empty", upperBodyLayerIndex, 0f);
                animator.SetLayerWeight(upperBodyLayerIndex, 1f);
            }

            animator.CrossFadeInFixedTime(
                "Walk",
                hitReactionExitCrossfadeDuration,
                0,
                0f);
        }

        _state = _targetHealth != null && !_targetHealth.IsDead ? ZombieState.Chase : ZombieState.Idle;

        if (_state == ZombieState.Chase && _nextGroanTime < 0f)
            PlayGroanAndScheduleNext();
    }

    void AssignAttackerAsTarget(Transform attacker, PlayerHealth attackerHealth)
    {
        if (attackerHealth == null && attacker != null)
            attackerHealth = attacker.GetComponentInParent<PlayerHealth>();

        if (attackerHealth == null || attackerHealth.IsDead || IsPlayerCarriedByJailor(attackerHealth))
            return;

        _targetHealth = attackerHealth;
        _target = attackerHealth.transform;
    }

    void StartCounterAttack()
    {
        if (_attackRoutine != null)
        {
            StopCoroutine(_attackRoutine);
            _attackRoutine = null;
        }

        _attackRoutine = StartCoroutine(AttackRoutine(useCounterAttack: true));
    }

    void ResetAllTriggers()
    {
        animator.ResetTrigger(attackTrigger);
        animator.ResetTrigger(hitReactionTrigger);
    }

    bool IsAnimatorInState(int layer, string stateName)
    {
        if (animator == null) return false;
        int hash = Animator.StringToHash(stateName);
        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
        if (current.shortNameHash == hash) return true;
        if (animator.IsInTransition(layer))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
            if (next.shortNameHash == hash) return true;
        }
        return false;
    }

    void CacheReferences()
    {
        if (animator == null)
            animator = GetComponent<Animator>();

        if (navMeshAgent == null)
            navMeshAgent = GetComponent<NavMeshAgent>();

        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (zombieHealth == null)
            zombieHealth = GetComponent<ZombieHealth>();

        if (voiceAudioSource == null)
            voiceAudioSource = GetOrCreateChildAudioSource(VoiceAudioChildName, allowCreate: false);

        if (footstepAudioSource == null)
            footstepAudioSource = GetOrCreateChildAudioSource(FootstepAudioChildName, allowCreate: false);

        if (_hipsBone == null)
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == "mixamorig:Hips") { _hipsBone = all[i]; break; }
        }
    }

    AudioSource GetOrCreateChildAudioSource(string childName, bool allowCreate)
    {
        Transform child = null;
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform c = transform.GetChild(i);
            if (c.name == childName)
            {
                child = c;
                break;
            }
        }

        if (child == null)
        {
            if (!allowCreate)
                return null;

            var go = new GameObject(childName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            child = go.transform;
        }

        AudioSource audio = child.GetComponent<AudioSource>();
        if (audio == null)
        {
            if (!allowCreate)
                return null;
            audio = child.gameObject.AddComponent<AudioSource>();
        }

        return audio;
    }

    void RemoveOrphanedRootAudioSources()
    {
        AudioSource[] onRoot = GetComponents<AudioSource>();
        for (int i = onRoot.Length - 1; i >= 0; i--)
        {
            AudioSource a = onRoot[i];
            if (a == null)
                continue;
            if (a == voiceAudioSource || a == footstepAudioSource)
                continue;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                DestroyImmediate(a, true);
                continue;
            }
#endif
            Destroy(a);
        }
    }

    void ConfigureVoiceAudioSource(bool allowCreate = true)
    {
        AudioSource resolved = GetOrCreateChildAudioSource(VoiceAudioChildName, allowCreate);
        if (resolved == null)
            return;

        voiceAudioSource = resolved;

        voiceAudioSource.playOnAwake = false;
        voiceAudioSource.loop = false;
        voiceAudioSource.clip = null;
        voiceAudioSource.spatialBlend = voiceSpatialBlend;
        voiceAudioSource.minDistance = voice3DMinDistance;
        voiceAudioSource.maxDistance = voice3DMaxDistance;
        voiceAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        voiceAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(voiceAudioSource);
    }

    void ConfigureFootstepAudioSource(bool allowCreate = true)
    {
        AudioSource resolved = GetOrCreateChildAudioSource(FootstepAudioChildName, allowCreate);
        if (resolved == null)
            return;

        footstepAudioSource = resolved;

        footstepAudioSource.playOnAwake = false;
        footstepAudioSource.loop = false;
        footstepAudioSource.spatialBlend = 1f;
        footstepAudioSource.minDistance = 1.5f;
        footstepAudioSource.maxDistance = 25f;
        footstepAudioSource.rolloffMode = AudioRolloffMode.Linear;
        footstepAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(footstepAudioSource);
    }

    void ApplyAgentSettings()
    {
        if (navMeshAgent == null)
            return;

        navMeshAgent.enabled = true;
        navMeshAgent.speed = walkSpeed;
        navMeshAgent.angularSpeed = rotationSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.1f, attackRadius * 0.9f);
        navMeshAgent.acceleration = Mathf.Max(navMeshAgent.acceleration, walkSpeed * 4f);
        navMeshAgent.updatePosition = false;
        navMeshAgent.updateRotation = false;
        navMeshAgent.baseOffset = 0f;
        // Higher than <see cref="JailorAI"/> (12) so zombies yield in local avoidance and cannot box the jailor in.
        navMeshAgent.avoidancePriority = 48;

        if (characterController != null)
        {
            characterController.skinWidth = 0.02f;
            characterController.minMoveDistance = 0.001f;
        }
    }

    void RefreshTarget()
    {
        if (_targetHealth != null && !_targetHealth.IsDead)
            return;

        int mask = detectionMask.value == 0 ? Physics.DefaultRaycastLayers : detectionMask.value;
        int hitCount = Physics.OverlapSphereNonAlloc(
            transform.position,
            EffectiveDetectionRadius,
            _detectionHits,
            mask,
            QueryTriggerInteraction.Ignore);

        PlayerHealth closestTarget = null;
        float closestDistance = float.MaxValue;

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = _detectionHits[i];
            _detectionHits[i] = null;
            if (hit == null)
                continue;

            PlayerHealth candidate = hit.GetComponentInParent<PlayerHealth>();
            if (candidate == null || candidate.IsDead || IsPlayerCarriedByJailor(candidate))
                continue;

            if (!IsWithinDetectionCone(candidate.transform.position))
                continue;

            if (!HasDetectionLineOfSight(candidate))
                continue;

            float distance = Vector3.Distance(transform.position, candidate.transform.position);
            if (distance >= closestDistance)
                continue;

            closestTarget = candidate;
            closestDistance = distance;
        }

        if (closestTarget == null)
        {
            IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerHealth candidate = players[i];
                if (candidate == null || candidate.IsDead || IsPlayerCarriedByJailor(candidate))
                    continue;

                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance > EffectiveDetectionRadius || distance >= closestDistance)
                    continue;

                if (!IsWithinDetectionCone(candidate.transform.position))
                    continue;

                if (!HasDetectionLineOfSight(candidate))
                    continue;

                closestTarget = candidate;
                closestDistance = distance;
            }
        }

        // Heard-sprint acquisition: no vision cone / line-of-sight gate — a sprinting player is heard from any
        // direction and around corners. Only the audible-sprint flag triggers it, so walking stays silent.
        if (closestTarget == null && hearingRadius > 0f)
        {
            IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerHealth candidate = players[i];
                if (candidate == null || candidate.IsDead || IsPlayerCarriedByJailor(candidate))
                    continue;
                if (!IsPlayerAudiblySprinting(candidate))
                    continue;

                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance > hearingRadius || distance >= closestDistance)
                    continue;

                closestTarget = candidate;
                closestDistance = distance;
            }
        }

        if (closestTarget == null)
            return;

        _targetHealth = closestTarget;
        _target = closestTarget.transform;
    }

    static bool IsPlayerAudiblySprinting(PlayerHealth playerHealth)
    {
        if (playerHealth == null)
            return false;

        NetworkPlayerAvatar avatar = playerHealth.GetComponent<NetworkPlayerAvatar>();
        if (avatar != null && avatar.IsSpawned)
            return avatar.AudiblySprintingForAi;

        PlayerController pc = playerHealth.GetComponent<PlayerController>();
        return pc != null && pc.IsAudiblySprintingForAi;
    }

    static bool IsPlayerCarriedByJailor(PlayerHealth playerHealth)
    {
        if (playerHealth == null)
            return false;

        NetworkPlayerAvatar avatar = playerHealth.GetComponent<NetworkPlayerAvatar>();
        return avatar != null && avatar.IsCarriedByJailor;
    }

    bool HasDetectionLineOfSight(PlayerHealth targetHealth)
    {
        if (!requireDetectionLineOfSight)
            return true;

        return HasLineOfSightToTarget(targetHealth, detectionLineOfSightMask, detectionLineOfSightHeight, Vector3.zero);
    }

    /// <summary>
    /// Vision-cone gate for target ACQUISITION only — hearing and being hit still aggro from any direction,
    /// so players can sneak behind. Points within arm's reach always register (you bumped into it).
    /// </summary>
    bool IsWithinDetectionCone(Vector3 worldPoint)
    {
        if (detectionFovHalfAngleDegrees >= 179.5f)
            return true;

        Vector3 toPoint = worldPoint - transform.position;
        toPoint.y = 0f;
        if (toPoint.sqrMagnitude <= 0.6f * 0.6f)
            return true;

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f)
            return true;

        return Vector3.Angle(forward, toPoint) <= detectionFovHalfAngleDegrees;
    }

    void TryStartChase()
    {
        if (_target == null)
            return;

        _state = ZombieState.Chase;
        PlayGroanAndScheduleNext();
    }

    void PlayGroanAndScheduleNext()
    {
        if (voiceAudioSource == null || zombieGroanClip == null)
            return;

        if (zombieHealth != null && zombieHealth.IsDead)
            return;

        voiceAudioSource.PlayOneShot(zombieGroanClip, Mathf.Clamp01(groanVolume));
        _nextGroanTime = Time.time + groanRepeatIntervalSeconds;
    }

    void UpdatePeriodicGroan()
    {
        if (voiceAudioSource == null || zombieGroanClip == null)
            return;

        if (zombieHealth != null && zombieHealth.IsDead)
            return;

        bool aggroVoice = _targetHealth != null && !_targetHealth.IsDead
            && (_state == ZombieState.Chase || _state == ZombieState.Attack);

        if (!aggroVoice)
            return;

        if (_nextGroanTime < 0f || Time.time < _nextGroanTime)
            return;

        PlayGroanAndScheduleNext();
    }

    void StopZombieVocalAudio()
    {
        if (voiceAudioSource != null)
            voiceAudioSource.Stop();
    }

    Vector3 UpdateChase(float distanceToTarget)
    {
        if (_target == null)
        {
            EnterIdle();
            return Vector3.zero;
        }

        float attackStartDistance = attackRadius + Mathf.Max(0f, attackStartDistancePadding);
        if (distanceToTarget <= attackStartDistance)
        {
            if (Time.time >= _nextAttackTime && _attackRoutine == null)
                _attackRoutine = StartCoroutine(AttackRoutine());

            FaceTarget();

            if (!allowMoveWhileAttacking)
            {
                if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
                {
                    navMeshAgent.isStopped = true;
                    navMeshAgent.ResetPath();
                }

                _intendedMoveSpeed = 0f;
                return Vector3.zero;
            }
        }

        float moveSpeed = EffectiveMoveSpeed;
        float targetMultiplier = _isEnraged ? 1f : SampleStepCurve(); // the run cycle has no shuffle-stop rhythm
        _currentStepMultiplier = Mathf.MoveTowards(
            _currentStepMultiplier,
            targetMultiplier,
            stepSpeedSmoothing * Time.deltaTime);

        _intendedMoveSpeed = moveSpeed;
        bool targetBelowForDrop = IsTargetWithinDropHeightWindow();

        if (ShouldDropIntoPit())
            BeginPitDrop();

        if (_pitDropActive)
            return GetPitDropVelocity(moveSpeed) * _currentStepMultiplier;

        if (!TrySnapToNavMesh())
        {
            if (targetBelowForDrop)
                return GetDirectChaseVelocity(moveSpeed) * _currentStepMultiplier;

            return Vector3.zero;
        }

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = moveSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.1f, attackRadius * 0.9f);
        if (!TryGetTargetDestination(out Vector3 destination))
        {
            if (targetBelowForDrop)
                return GetDirectChaseVelocity(moveSpeed) * _currentStepMultiplier;

            EnterIdle();
            return Vector3.zero;
        }

        if (!navMeshAgent.SetDestination(destination))
        {
            if (targetBelowForDrop)
                return GetDirectChaseVelocity(moveSpeed) * _currentStepMultiplier;

            EnterIdle();
            return Vector3.zero;
        }

        if (!navMeshAgent.pathPending && navMeshAgent.pathStatus != NavMeshPathStatus.PathComplete && targetBelowForDrop)
            return GetDirectChaseVelocity(moveSpeed) * _currentStepMultiplier;

        Vector3 desiredVelocity = navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        if (desiredVelocity.sqrMagnitude > moveSpeed * moveSpeed)
            desiredVelocity = desiredVelocity.normalized * moveSpeed;

        return desiredVelocity * _currentStepMultiplier;
    }

    void UpdateAttack()
    {
        if (!allowMoveWhileAttacking)
        {
            _intendedMoveSpeed = 0f;
            FaceTarget();
        }
    }

    void EnterIdle()
    {
        if (_state == ZombieState.Idle)
            return;

        _state = ZombieState.Idle;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        _horizontalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;
        _pitDropActive = false;
        _footstepTimer = 0f;
        _nextGroanTime = -1f;
        StopZombieVocalAudio();
    }

    void ClearTarget()
    {
        _target = null;
        _targetHealth = null;
        _footstepTimer = 0f;
        _nextGroanTime = -1f;
        StopZombieVocalAudio();
    }

    void UpdateFootsteps()
    {
        if (footstepAudioSource == null || characterController == null || _state == ZombieState.Dead)
            return;

        float horizontalSpeed = _horizontalVelocity.magnitude;
        bool grounded = characterController.isGrounded;
        if (!grounded || horizontalSpeed < minimumFootstepSpeed)
        {
            _footstepTimer = 0f;
            return;
        }

        // Faster stride while enraged: shrink the interval with actual speed above the walk baseline.
        float cadenceScale = Mathf.Clamp(walkSpeed / Mathf.Max(horizontalSpeed, 0.01f), 0.55f, 1f);
        float interval = Mathf.Max(0.05f, walkFootstepInterval * 2f * cadenceScale);
        _footstepTimer -= Time.deltaTime;
        if (_footstepTimer > 0f)
            return;

        PlayFootstepOneShot();
        _footstepTimer = interval;
    }

    void RebuildFootstepPool()
    {
        _footstepPool.Clear();
        if (footstepClip1 != null) _footstepPool.Add(footstepClip1);
        if (footstepClip2 != null) _footstepPool.Add(footstepClip2);
        if (footstepClip3 != null) _footstepPool.Add(footstepClip3);
        if (footstepClip4 != null) _footstepPool.Add(footstepClip4);

        int n = _footstepPool.Count;
        if (n == 0)
        {
            _footstepShuffle = null;
            _footstepShuffleIndex = 0;
            return;
        }

        if (_footstepShuffle == null || _footstepShuffle.Length != n)
            _footstepShuffle = new int[n];

        ReshuffleFootstepOrder();
    }

    void ReshuffleFootstepOrder()
    {
        if (_footstepShuffle == null || _footstepPool.Count == 0)
            return;

        int n = _footstepPool.Count;
        for (int i = 0; i < n; i++)
            _footstepShuffle[i] = i;

        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            int tmp = _footstepShuffle[i];
            _footstepShuffle[i] = _footstepShuffle[j];
            _footstepShuffle[j] = tmp;
        }

        _footstepShuffleIndex = 0;
    }

    void PlayFootstepOneShot()
    {
        if (footstepAudioSource == null)
            return;

        if (_footstepPool.Count == 0)
            RebuildFootstepPool();

        if (_footstepPool.Count == 0)
            return;

        if (_footstepShuffleIndex >= _footstepPool.Count)
            ReshuffleFootstepOrder();

        int poolIndex = _footstepShuffle[_footstepShuffleIndex++];
        AudioClip clipToPlay = _footstepPool[poolIndex];

        if (clipToPlay == null)
            return;

        footstepAudioSource.PlayOneShot(clipToPlay, Mathf.Max(0f, footstepVolume));
    }

    void PlayDeathVoice()
    {
        if (voiceAudioSource == null || zombieDeathClip == null)
            return;

        voiceAudioSource.PlayOneShot(zombieDeathClip, Mathf.Max(0f, deathVoiceVolume));
    }

#if UNITY_EDITOR
    void AutoAssignAudioClipsInEditor()
    {
        if (zombieGroanClip == null)
            zombieGroanClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/ZombieGroan.wav");

        if (zombieDeathClip == null)
            zombieDeathClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/ZombieDeath.wav");

        if (footstepClip1 == null)
            footstepClip1 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/footstep1.mp3");

        if (footstepClip2 == null)
            footstepClip2 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/footstep2.mp3");

        if (footstepClip3 == null)
            footstepClip3 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/footstep3.mp3");

        if (footstepClip4 == null)
            footstepClip4 = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/footstep4.mp3");
    }
#endif

    IEnumerator AttackRoutine(bool useCounterAttack = false)
    {
        bool useUpperBodyAttack = upperBodyLayerIndex > 0 && (allowMoveWhileAttacking || useCounterAttack);
        bool wasMovingDuringAttack = useUpperBodyAttack && _state == ZombieState.Chase && !useCounterAttack;
        Vector3 committedAttackDirection = GetCommittedAttackDirection();
        float hitDelay = useCounterAttack ? counterAttackHitDelay : attackHitDelay;

        if (!wasMovingDuringAttack)
        {
            _state = ZombieState.Attack;

            if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
            {
                navMeshAgent.isStopped = true;
                navMeshAgent.ResetPath();
            }

            _horizontalVelocity = Vector3.zero;
            _intendedMoveSpeed = 0f;
            _pitDropActive = false;
        }

        FaceTarget();

        if (animator != null)
        {
            ResetAllTriggers();
            if (useCounterAttack)
                animator.CrossFadeInFixedTime("Idle", hitReactionExitCrossfadeDuration, 0, 0f);

            string attackStateName = useCounterAttack ? counterAttackStateName : "Attack";
            if (useUpperBodyAttack)
            {
                animator.SetLayerWeight(upperBodyLayerIndex, 1f);
                animator.CrossFadeInFixedTime(attackStateName, attackCrossfadeDuration, upperBodyLayerIndex, 0f);
            }
            else
            {
                animator.CrossFadeInFixedTime(attackStateName, attackCrossfadeDuration, 0, 0f);
            }
        }

        if (hitDelay > 0f)
            yield return new WaitForSeconds(hitDelay);

        if (CanLandCommittedAttack(_targetHealth, committedAttackDirection))
            _targetHealth.TakeDamage(damage); // victim feedback comes from the universal hurt-feedback watcher

        _nextAttackTime = Time.time + EffectiveAttackRate;

        float recoveryTime = Mathf.Max(0f, EffectiveAttackRate - hitDelay);
        if (recoveryTime > 0f)
            yield return new WaitForSeconds(recoveryTime);

        if (animator != null && useUpperBodyAttack)
        {
            float exitCrossfadeDuration = useCounterAttack ? counterAttackExitCrossfadeDuration : 0.1f;
            animator.CrossFadeInFixedTime("Empty", exitCrossfadeDuration, upperBodyLayerIndex, 0f);
        }

        _attackRoutine = null;

        if (_state == ZombieState.Dead)
            yield break;

        if (!wasMovingDuringAttack)
            _state = _targetHealth != null && !_targetHealth.IsDead ? ZombieState.Chase : ZombieState.Idle;
    }

    Vector3 GetCommittedAttackDirection()
    {
        if (_target == null)
            return transform.forward;

        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f)
            return transform.forward;

        return toTarget.normalized;
    }

    bool CanLandCommittedAttack(PlayerHealth targetHealth, Vector3 committedAttackDirection)
    {
        if (targetHealth == null || targetHealth.IsDead)
            return false;

        Vector3 toTarget = targetHealth.transform.position - transform.position;
        Vector3 horizontalToTarget = toTarget;
        horizontalToTarget.y = 0f;
        float horizontalDistanceToTarget = horizontalToTarget.magnitude;
        if (horizontalDistanceToTarget > attackRadius + attackHitRangePadding)
            return false;

        if (horizontalDistanceToTarget > 0.001f)
        {
            float attackAngle = Vector3.Angle(committedAttackDirection, horizontalToTarget / horizontalDistanceToTarget);
            if (attackAngle > attackHitHalfAngle)
                return false;
        }

        if (requireAttackLineOfSight && !HasAttackLineOfSight(targetHealth, committedAttackDirection))
            return false;

        return true;
    }

    bool HasAttackLineOfSight(PlayerHealth targetHealth, Vector3 committedAttackDirection)
    {
        return HasLineOfSightToTarget(targetHealth, attackLineOfSightMask, attackLineOfSightHeight, committedAttackDirection * 0.15f);
    }

    bool HasLineOfSightToTarget(PlayerHealth targetHealth, LayerMask lineOfSightMask, float lineOfSightHeight, Vector3 originOffset)
    {
        if (targetHealth == null)
            return false;

        Vector3 origin = transform.position + Vector3.up * lineOfSightHeight + originOffset;
        Vector3 targetPoint = targetHealth.transform.position + Vector3.up * lineOfSightHeight;
        Vector3 toTarget = targetPoint - origin;
        float distanceToTarget = toTarget.magnitude;
        if (distanceToTarget <= 0.001f)
            return true;

        int mask = lineOfSightMask.value == 0 ? Physics.DefaultRaycastLayers : lineOfSightMask.value;
        int hitCount = Physics.RaycastNonAlloc(
            origin,
            toTarget / distanceToTarget,
            _lineOfSightHits,
            distanceToTarget,
            mask,
            QueryTriggerInteraction.Ignore);
        if (hitCount == 0)
            return true;

        System.Array.Sort(_lineOfSightHits, 0, hitCount, RaycastHitDistanceComparer.Instance);
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = _lineOfSightHits[i];
            _lineOfSightHits[i] = default;

            if (hit.transform == null)
                continue;

            if (hit.transform == transform || hit.transform.IsChildOf(transform))
                continue;

            return hit.transform == targetHealth.transform || hit.transform.IsChildOf(targetHealth.transform);
        }

        return true;
    }

    void ApplyMovement(Vector3 desiredHorizontalVelocity)
    {
        if (characterController == null)
            return;

        bool grounded = characterController.isGrounded;
        if (grounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -groundedStickDown;

        bool frozen = _state == ZombieState.HitReaction || _state == ZombieState.Dead
            || IsAnimatorInState(0, "HitReaction");
        _horizontalVelocity = frozen ? Vector3.zero : desiredHorizontalVelocity;
        _verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 motion = _horizontalVelocity * Time.deltaTime;
        motion.y = _verticalVelocity.y * Time.deltaTime;
        characterController.Move(motion);

        if (_pitDropActive)
            UpdatePitDropState();

        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.nextPosition = transform.position;

        Vector3 horizontalDirection = _horizontalVelocity;
        horizontalDirection.y = 0f;
        if (horizontalDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(horizontalDirection.normalized);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                targetRotation,
                rotationSpeed * Time.deltaTime);
        }
    }

    void FaceTarget()
    {
        if (_target == null)
            return;

        Vector3 lookDirection = _target.position - transform.position;
        lookDirection.y = 0f;
        if (lookDirection.sqrMagnitude < 0.0001f)
            return;

        Quaternion targetRotation = Quaternion.LookRotation(lookDirection.normalized);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRotation,
            rotationSpeed * Time.deltaTime);
    }

    void UpdateAnimatorParameters()
    {
        if (animator == null)
            return;

        if (_state == ZombieState.Dead)
        {
            animator.SetFloat(speedParameter, 0f);
            animator.SetBool(groundedParameter, true);
            animator.SetFloat(verticalVelocityParameter, 0f);
            return;
        }

        float referenceSpeed = EffectiveMoveSpeed;
        float normalizedSpeed = referenceSpeed > 0.001f ? Mathf.Clamp01(_intendedMoveSpeed / referenceSpeed) : 0f;
        animator.SetFloat(speedParameter, normalizedSpeed);
        animator.SetBool(groundedParameter, characterController != null && characterController.isGrounded);
        animator.SetFloat(verticalVelocityParameter, _verticalVelocity.y);
    }

    void BeginPitDrop()
    {
        _pitDropActive = true;
        _pitDropUnlockTime = Time.time + pitDropCommitDuration;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }
    }

    Vector3 GetPitDropVelocity(float moveSpeed)
    {
        Vector3 moveDirection = transform.forward;
        if (_target != null)
        {
            Vector3 targetDirection = _target.position - transform.position;
            targetDirection.y = 0f;
            if (targetDirection.sqrMagnitude > 0.0001f)
                moveDirection = targetDirection.normalized;
        }

        return moveDirection * moveSpeed;
    }

    Vector3 GetDirectChaseVelocity(float moveSpeed)
    {
        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        Vector3 moveDirection = transform.forward;
        if (_target != null)
        {
            Vector3 targetDirection = _target.position - transform.position;
            targetDirection.y = 0f;
            if (targetDirection.sqrMagnitude > 0.0001f)
                moveDirection = targetDirection.normalized;
        }

        return moveDirection * moveSpeed;
    }

    void UpdatePitDropState()
    {
        if (characterController == null)
            return;

        if (!characterController.isGrounded)
            return;

        if (Time.time < _pitDropUnlockTime)
            return;

        if (!TrySnapToNavMesh())
            return;

        _pitDropActive = false;
    }

    bool TrySnapToNavMesh()
    {
        if (navMeshAgent == null || !navMeshAgent.enabled)
            return false;

        if (navMeshAgent.isOnNavMesh)
            return true;

        Vector3 p = transform.position;
        float[] radii = { 2f, 6f, 12f };
        for (int i = 0; i < radii.Length; i++)
        {
            if (!NavMesh.SamplePosition(p, out NavMeshHit hit, radii[i], NavMesh.AllAreas))
                continue;

            return navMeshAgent.Warp(hit.position);
        }

        return false;
    }

    bool ShouldDropIntoPit()
    {
        if (_pitDropActive || _target == null || characterController == null || !characterController.isGrounded)
            return false;

        Vector3 toTarget = _target.position - transform.position;
        if (!IsTargetWithinDropHeightWindow())
            return false;

        Vector3 horizontalToTarget = toTarget;
        horizontalToTarget.y = 0f;
        if (horizontalToTarget.sqrMagnitude < 0.01f)
            return false;

        Vector3 moveDirection = horizontalToTarget.normalized;
        Vector3 rayOrigin = transform.position + Vector3.up * 0.2f + moveDirection * pitProbeForwardDistance;
        bool groundAhead = Physics.Raycast(
            rayOrigin,
            Vector3.down,
            pitProbeDepth,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);

        return !groundAhead;
    }

    bool IsTargetWithinDropHeightWindow()
    {
        if (_target == null)
            return false;

        float targetDrop = transform.position.y - _target.position.y;
        return targetDrop >= pitDropMinHeight && targetDrop <= pitDropMaxHeight;
    }

    bool TryGetTargetDestination(out Vector3 destination)
    {
        destination = Vector3.zero;
        if (_target == null)
            return false;

        if (NavMesh.SamplePosition(_target.position, out NavMeshHit hit, targetNavMeshSampleRadius, NavMesh.AllAreas))
        {
            destination = hit.position;
            return true;
        }

        destination = _target.position;
        return false;
    }

    float SampleStepCurve()
    {
        if (animator == null)
            return 1f;

        AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);
        float normalizedTime = stateInfo.normalizedTime % 1f;

        AnimationCurve curve = walkStepCurve;
        if (curve == null || curve.length == 0)
            return 1f;

        return Mathf.Clamp01(curve.Evaluate(normalizedTime));
    }

    static AnimationCurve DefaultWalkStepCurve()
    {
        return new AnimationCurve(
            new Keyframe(0.00f, 0f),
            new Keyframe(0.10f, 1f),
            new Keyframe(0.30f, 1f),
            new Keyframe(0.40f, 0f),
            new Keyframe(0.50f, 0f),
            new Keyframe(0.60f, 1f),
            new Keyframe(0.80f, 1f),
            new Keyframe(0.90f, 0f),
            new Keyframe(1.00f, 0f)
        );
    }

    sealed class RaycastHitDistanceComparer : System.Collections.Generic.IComparer<RaycastHit>
    {
        public static readonly RaycastHitDistanceComparer Instance = new RaycastHitDistanceComparer();

        public int Compare(RaycastHit x, RaycastHit y)
        {
            return x.distance.CompareTo(y.distance);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.85f, 0.15f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, detectionRadius);

        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, attackRadius);
    }
}
