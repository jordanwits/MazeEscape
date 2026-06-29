using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Dungeon Skeleton enemy AI. Server-authoritative like <see cref="ZombieAI"/> (clients only run cosmetics): a
/// NavMeshAgent decides the path while a CharacterController does the actual move, and detection is an OverlapSphere
/// + line-of-sight scan.
///
/// Behaviour:
///   • PATROL — with no target it wanders forward-biased NavMesh waypoints (like the Jailor). It cannot cross pits,
///              so a forward ground-probe makes it turn around at any drop instead of walking off.
///   • CHASE  — walk toward a spotted player.
///   • THROW  — within throw range (visible AND with a clear throw lane) it STOPS and lobs the held skull on a
///              cooldown; if a wall/corner blocks the throw it keeps moving to reposition instead of throwing.
///   • BASH   — within melee range it stops throwing and swings the held skull: damage + a non-ragdoll shove.
/// Being meleed plays HitReaction and briefly stuns it. Death (in <see cref="SkeletonHealth"/>/<see cref="NetworkSkeletonAvatar"/>)
/// breaks the body into a bone pile.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class SkeletonAI : MonoBehaviour
{
    enum SkeletonState
    {
        Idle,
        Patrol,
        Chase,
        Throw,
        Bash,
        HitReaction,
        Dead
    }

    [Header("References")]
    [SerializeField] Animator animator;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [SerializeField] SkeletonHealth skeletonHealth;
    [SerializeField] NetworkSkeletonAvatar networkAvatar;
    [Tooltip("Where thrown objects spawn from (the right hand). Falls back to the rig's right-hand bone, then root.")]
    [SerializeField] Transform throwOrigin;

    [Header("Detection")]
    [SerializeField] LayerMask detectionMask;
    [SerializeField] float detectionRadius = 16f;
    [SerializeField] float loseTargetRadiusMultiplier = 1.4f;
    [SerializeField] bool requireDetectionLineOfSight = true;
    [SerializeField] LayerMask detectionLineOfSightMask = Physics.DefaultRaycastLayers;
    [SerializeField] float detectionLineOfSightHeight = 1.1f;
    [Tooltip("Seconds between target-acquisition scans while it has no target. Movement/combat stay per-frame.")]
    [SerializeField, Min(0f)] float sensingInterval = 0.1f;

    [Header("Movement")]
    [SerializeField] float walkSpeed = 1.7f;
    [SerializeField] float rotationSpeed = 720f;
    [SerializeField] float gravity = -20f;
    [SerializeField] float groundedStickDown = 2f;
    [SerializeField] float targetNavMeshSampleRadius = 3f;

    [Header("Patrol")]
    [SerializeField] bool patrolWhenNoTarget = true;
    [SerializeField] float patrolMinWaypointDistance = 5f;
    [SerializeField] float patrolMaxWaypointDistance = 12f;
    [SerializeField] float patrolArrivalDistance = 1.2f;
    [SerializeField] float patrolDestinationRefreshInterval = 0.45f;
    [SerializeField] int patrolSampleAttempts = 12;
    [SerializeField] float patrolStuckSeconds = 2f;
    [SerializeField] float patrolRepathCooldown = 0.9f;

    [Header("Pit avoidance (can't cross pits)")]
    [SerializeField] bool avoidPits = true;
    [Tooltip("Layers that count as solid ground for the forward drop-probe. Actors are excluded automatically.")]
    [SerializeField] LayerMask groundMask = Physics.DefaultRaycastLayers;
    [SerializeField] float pitProbeForwardDistance = 0.65f;
    [SerializeField] float pitProbeUpOffset = 0.4f;
    [Tooltip("If no ground is found within this drop ahead, it's treated as a pit and the skeleton turns away.")]
    [SerializeField] float pitProbeDepth = 2.5f;
    [Tooltip("If a chase makes no progress for this long (e.g. the target is across a pit, so the agent steers at it " +
             "but the pit-probe freezes every step), give up the target and return to patrol instead of freezing.")]
    [SerializeField] float chaseUnreachablePatrolSeconds = 0.5f;
    [Tooltip("After abandoning an unreachable target, ignore that same player for this long so it doesn't instantly " +
             "re-acquire it and freeze again.")]
    [SerializeField] float unreachableTargetForgetSeconds = 3f;

    [Header("Anti-stuck (props / walls)")]
    [Tooltip("Recover when the body is wedged against an un-baked prop/wall: the NavMesh path runs through it so the " +
             "agent pushes forward but the CharacterController makes no actual progress (walking in place). It sidesteps " +
             "to slip around it, and gives up after a few failed nudges so it can never stay stuck.")]
    [SerializeField] bool recoverFromStuck = true;
    [Tooltip("Window (s) over which ACTUAL ground movement is measured to decide whether it's wedged in place.")]
    [SerializeField] float stuckCheckWindow = 0.4f;
    [Tooltip("If it advances less than this (m) over the window while trying to move, it's treated as wedged.")]
    [SerializeField] float stuckProgressEpsilon = 0.06f;
    [Tooltip("How long each sideways unstick nudge lasts.")]
    [SerializeField] float unstickDuration = 0.45f;
    [Tooltip("Consecutive failed nudges before it gives up the chase / repicks a patrol waypoint instead of nudging.")]
    [SerializeField] int stuckStrikesBeforeGiveUp = 4;

    [Header("Throw (ranged)")]
    [Tooltip("The object lobbed at the player (the flaming skull). Server-spawned. May be left empty until the " +
             "object exists — the skeleton will still stop and play the throw, just without a projectile.")]
    [SerializeField] GameObject throwProjectilePrefab;
    [Tooltip("Player must be within this distance (visible, with a clear lane) for the skeleton to stop and throw.")]
    [SerializeField] float throwRange = 13f;
    [SerializeField] bool throwRequiresLineOfSight = true;
    [SerializeField] float throwCooldown = 2.5f;
    [Tooltip("Seconds into the Throw animation before the object is released (the wind-up).")]
    [SerializeField] float throwReleaseDelay = 0.55f;
    [Tooltip("Apex height of the lob arc, in metres above the straight line to the target.")]
    [SerializeField] float projectileArcHeight = 2.5f;
    [Tooltip("How long the lob takes to reach the target point. Lower = faster, harder to dodge.")]
    [SerializeField] float projectileFlightDuration = 1.05f;
    [Tooltip("Aim point height offset on the target (so it's thrown at the torso, not the feet).")]
    [SerializeField] float throwTargetHeightOffset = 1.0f;
    [Tooltip("If a wall is within this distance in the throw direction (from the hand), the lane is blocked and the " +
             "skeleton repositions instead of throwing into a corner.")]
    [SerializeField] float throwClearanceDistance = 1.6f;
    [SerializeField] LayerMask throwClearanceMask = Physics.DefaultRaycastLayers;
    [SerializeField] AudioClip throwSfx;

    [Header("Bash (melee)")]
    [Tooltip("Player must be within this distance for the skeleton to stop throwing and bash.")]
    [SerializeField] float bashRange = 2.4f;
    [SerializeField] float bashDamage = 22f;
    [SerializeField] float bashCooldown = 1.6f;
    [Tooltip("Seconds into the Bash animation before the hit lands.")]
    [SerializeField] float bashHitDelay = 0.45f;
    [SerializeField] float bashHitRangePadding = 0.25f;
    [Tooltip("How wide the bash can hit. Lower lets side-steps dodge it.")]
    [SerializeField, Range(0f, 180f)] float bashHitHalfAngle = 60f;
    [SerializeField] bool bashRequiresLineOfSight = true;
    [SerializeField] float bashPushHorizontalSpeed = 7f;
    [SerializeField] float bashPushUpwardSpeed = 1.5f;
    [SerializeField, Min(0f)] float bashPushControlLockSeconds = 0.25f;
    [SerializeField] AudioClip bashSfx;

    [Header("Hit reaction")]
    [Tooltip("How long the skeleton is stunned when the player melees it.")]
    [SerializeField] float hitReactionDuration = 1.3f;
    [SerializeField] float hitReactionCrossfade = 0.1f;
    [SerializeField] float hitReactionExitCrossfade = 0.18f;
    [Tooltip("Clamp the hit-reaction stagger so the body can't lean through walls/props — the React clip shoves " +
             "the mesh ~1.2m back, far beyond the collision capsule, so it would clip a wall behind it.")]
    [SerializeField] bool clampHitReactionToWalls = true;
    [SerializeField] LayerMask hitReactionWallMask = Physics.DefaultRaycastLayers;
    [Tooltip("Half-thickness of the body used when checking how far it can lean before hitting a wall.")]
    [SerializeField] float hitReactionBodyRadius = 0.28f;
    [SerializeField] float hitReactionCastHeight = 1.1f;

    [Header("Footsteps")]
    [Tooltip("Dedicated 3D AudioSource for footsteps (child 'Skeleton_Footsteps'). Auto-resolved if left empty.")]
    [SerializeField] AudioSource footstepAudioSource;
    [SerializeField] AudioClip footstepClip1;
    [SerializeField] AudioClip footstepClip2;
    [SerializeField, Range(0f, 1f)] float footstepVolume = 0.6f;
    [Tooltip("Footsteps per walk-clip loop (usually 2: left + right). Footsteps are locked to the walk cycle so " +
             "they always match the animation regardless of move/playback speed.")]
    [SerializeField] float footstepsPerCycle = 2f;
    [Tooltip("Shifts footstep timing within the cycle (0..1) to line the sound up with the foot hitting the ground. " +
             "Tuned so the steps land on the clip's actual foot plants (nt ~0.31 / ~0.81).")]
    [SerializeField, Range(0f, 1f)] float footstepPhaseOffset = 0.19f;
    [Tooltip("Normalized Speed (0..1) below which footsteps stop.")]
    [SerializeField] float minFootstepSpeed = 0.12f;

    [Header("Animator")]
    [SerializeField] string speedParameter = "Speed";
    [Tooltip("Float param that scales walk-clip playback so the (in-place) feet match the ground.")]
    [SerializeField] string walkPlaybackParameter = "WalkPlaybackSpeed";
    [Tooltip("Ground speed (m/s) the walk clip's stride represents. Walk plays back at actualSpeed/this, so the " +
             "feet stay planted at any speed. Raise it if the feet still look too fast; lower it if they drag.")]
    [SerializeField] float walkAnimMatchSpeed = 1.9f;
    [SerializeField] string idleStateName = "Idle";
    [SerializeField] string walkStateName = "Walk";
    [SerializeField] string throwStateName = "Throw";
    [SerializeField] string bashStateName = "Bash";
    [SerializeField] string hitReactionStateName = "HitReaction";
    [SerializeField] float actionCrossfade = 0.1f;

    const string RightHandBoneName = "mixamorig:RightHand";
    const string HipsBoneName = "mixamorig:Hips";

    readonly Collider[] _detectionHits = new Collider[16];
    readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];
    float _nextSenseTime = -1f;

    SkeletonState _state;
    Transform _target;
    PlayerHealth _targetHealth;
    float _nextThrowTime;
    float _nextBashTime;
    float _hitReactionEndTime;
    Coroutine _actionRoutine;
    Vector3 _horizontalVelocity;
    Vector3 _verticalVelocity;
    bool _pitBlocked;
    float _chaseUnreachableTime;
    PlayerHealth _unreachableTargetHealth;
    float _unreachableTargetUntil;
    Vector3 _antiStuckSamplePos;
    float _antiStuckSampleTime;
    float _antiStuckNudgeUntil;
    Vector3 _antiStuckNudgeDir;
    int _antiStuckSide;
    int _antiStuckStrikes;
    float _antiStuckStrikeExpiry;

    // Patrol state
    bool _hasPatrolDestination;
    Vector3 _patrolDestination;
    float _nextPatrolRefreshTime;
    float _nextPatrolProgressTime;
    float _patrolPrevRemaining = float.PositiveInfinity;
    float _patrolStuckTime;
    float _nextPatrolRepathTime;
    Vector3 _patrolForwardOverride;
    float _patrolForwardOverrideUntil;
    readonly Queue<Vector3> _recentPatrol = new();

    NetworkObject _networkObject;
    AudioSource _sfxSource;
    Transform _hipsBone;
    int _hitReactionStateHash;
    int _footstepCycleIndex;
    bool _footstepPrimed;
    bool _footstepToggle;
    int _walkStateHash;

    void Reset()
    {
        CacheReferences();
        ApplyAgentSettings();
    }

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
        CacheReferences();
        ResolveBones();
        ApplyAgentSettings();
        EnsureSfxSource();
        ConfigureFootstepSource();
#if UNITY_EDITOR
        AutoAssignFootstepClips();
#endif
        _hitReactionStateHash = Animator.StringToHash(hitReactionStateName);
        _walkStateHash = Animator.StringToHash(walkStateName);
    }

    void LateUpdate()
    {
        // The hit-reaction clip leans the mesh far past the collision capsule; clamp the upper body so it can't
        // pass through a wall/prop behind it. Runs on every peer because the clip plays everywhere (it is keyed
        // off the animator state, which replicates, not the server-only AI state).
        if (!clampHitReactionToWalls || animator == null || _hipsBone == null)
            return;
        if (!IsAnimatorInHitReaction())
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

    bool IsAnimatorInHitReaction()
    {
        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(0);
        if (cur.shortNameHash == _hitReactionStateHash)
            return true;
        if (animator.IsInTransition(0))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
            if (next.shortNameHash == _hitReactionStateHash)
                return true;
        }
        return false;
    }

    void OnEnable()
    {
        TrySnapToNavMesh();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        CacheReferences();
        AutoAssignFootstepClips();
    }
#endif

    void Update()
    {
        // Footsteps run on EVERY peer, keyed off the replicated animator Speed so clients hear them too.
        UpdateFootsteps();

        // Clients never simulate the skeleton; only the server (or fully-offline host) runs the AI.
        bool isNetworkClient = _networkObject != null
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer;
        if (isNetworkClient)
            return;

        if (skeletonHealth != null && skeletonHealth.IsDead)
        {
            HandleDeath();
            return;
        }

        if (_state == SkeletonState.Dead)
            return;

        if (_nextSenseTime < 0f)
            _nextSenseTime = Time.time + Random.Range(0f, Mathf.Max(0f, sensingInterval));
        if (sensingInterval <= 0f || Time.time >= _nextSenseTime)
        {
            _nextSenseTime = Time.time + Mathf.Max(0f, sensingInterval);
            RefreshTarget();
        }

        Vector3 desiredHorizontalVelocity = Vector3.zero;
        bool inHitReaction = _state == SkeletonState.HitReaction && Time.time < _hitReactionEndTime;

        if (_targetHealth == null || _targetHealth.IsDead)
        {
            if (inHitReaction)
                UpdateHitReaction();
            else
            {
                ClearTarget();
                desiredHorizontalVelocity = UpdatePatrol();
            }
        }
        else
        {
            float distanceToTarget = Vector3.Distance(transform.position, _target.position);
            float loseTargetRadius = Mathf.Max(detectionRadius, detectionRadius * loseTargetRadiusMultiplier);
            if (distanceToTarget > loseTargetRadius && !inHitReaction)
            {
                ClearTarget();
                desiredHorizontalVelocity = UpdatePatrol();
            }
            else if (inHitReaction)
            {
                UpdateHitReaction();
            }
            else if (_actionRoutine != null)
            {
                FaceTarget(); // a throw or bash is mid-swing: stay planted, keep facing the target
            }
            else
            {
                desiredHorizontalVelocity = UpdateCombat(distanceToTarget);
            }
        }

        ApplyMovement(desiredHorizontalVelocity);
        TrackChaseStall(); // bail out of a chase that can't make progress (e.g. target across a pit)
        UpdateAntiStuck();  // sidestep / recover when wedged in place against a prop or wall
        UpdateAnimatorParameters();
    }

    /// <summary>Pick throw vs bash vs chase based on range and lane clearance. Returns desired move velocity.</summary>
    Vector3 UpdateCombat(float distanceToTarget)
    {
        if (distanceToTarget <= bashRange)
        {
            FaceTarget();
            StopNavigation();
            if (Time.time >= _nextBashTime)
                _actionRoutine = StartCoroutine(BashRoutine());
            return Vector3.zero;
        }

        bool inThrowRange = distanceToTarget <= throwRange;
        bool throwLos = !throwRequiresLineOfSight || HasLineOfSight(_targetHealth, detectionLineOfSightMask, detectionLineOfSightHeight, Vector3.zero);
        if (inThrowRange && throwLos && HasThrowClearance())
        {
            FaceTarget();
            StopNavigation();
            if (Time.time >= _nextThrowTime)
                _actionRoutine = StartCoroutine(ThrowRoutine());
            return Vector3.zero;
        }

        // Out of range, no line of sight, or a wall/corner blocks the throw lane -> keep moving to reposition.
        return UpdateChase();
    }

    Vector3 UpdateChase()
    {
        _state = SkeletonState.Chase;

        if (!TrySnapToNavMesh())
            return Vector3.zero;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = walkSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.1f, bashRange * 0.9f);

        if (!TryGetTargetDestination(out Vector3 destination) || !navMeshAgent.SetDestination(destination))
            return Vector3.zero;

        Vector3 desiredVelocity = navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        if (desiredVelocity.sqrMagnitude > walkSpeed * walkSpeed)
            desiredVelocity = desiredVelocity.normalized * walkSpeed;

        return desiredVelocity;
    }

    /// <summary>Drop the current (unreachable) target and briefly blacklist it so it isn't instantly re-acquired.</summary>
    void AbandonUnreachableTarget()
    {
        _unreachableTargetHealth = _targetHealth;
        _unreachableTargetUntil = Time.time + Mathf.Max(0f, unreachableTargetForgetSeconds);
        ClearTarget();
    }

    /// <summary>
    /// While chasing, the NavMesh may report a complete path the body can't physically follow — e.g. the target is
    /// across a pit, so the agent steers straight at it but the pit-probe (<see cref="WouldStepIntoPit"/>) zeroes
    /// every step and it freezes at the edge. Detect that — a chase step actively blocked by a pit — and fall back
    /// to patrol. Only counts pit-blocked steps, so a skeleton deliberately stopped in range (throwing or waiting out
    /// a cooldown) is never mistaken for stuck.
    /// </summary>
    void TrackChaseStall()
    {
        if (_state != SkeletonState.Chase || _target == null || !_pitBlocked)
        {
            _chaseUnreachableTime = 0f;
            return;
        }

        _chaseUnreachableTime += Time.deltaTime;
        if (_chaseUnreachableTime >= Mathf.Max(0f, chaseUnreachablePatrolSeconds))
            AbandonUnreachableTarget(); // next Update has no target -> routes to patrol
    }

    /// <summary>
    /// Stops the skeleton ever "walking in place" against a prop/wall the NavMesh path runs through but the body
    /// can't pass. Measures ACTUAL ground progress while it's trying to move; if it isn't advancing it sidesteps to
    /// slip around the obstacle, and after several failed nudges gives up (chase -> patrol, or a fresh patrol
    /// waypoint) so it can never stay wedged. Pit blocks are left to <see cref="TrackChaseStall"/>.
    /// </summary>
    void UpdateAntiStuck()
    {
        if (!recoverFromStuck)
            return;

        bool movingState = _state == SkeletonState.Chase || _state == SkeletonState.Patrol;
        bool wantsToMove = navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh
            && !navMeshAgent.isStopped && navMeshAgent.hasPath
            && navMeshAgent.desiredVelocity.sqrMagnitude > 0.04f;

        // Not trying to move, a pit is the cause (handled elsewhere), or a nudge is mid-play: just (re)start sampling.
        if (!movingState || !wantsToMove || _pitBlocked || Time.time < _antiStuckNudgeUntil)
        {
            _antiStuckSamplePos = transform.position;
            _antiStuckSampleTime = Time.time;
            return;
        }

        if (Time.time - _antiStuckSampleTime < Mathf.Max(0.1f, stuckCheckWindow))
            return;

        // How far did it ACTUALLY travel along the ground over the window?
        Vector3 a = transform.position; a.y = 0f;
        Vector3 b = _antiStuckSamplePos; b.y = 0f;
        float moved = Vector3.Distance(a, b);
        _antiStuckSamplePos = transform.position;
        _antiStuckSampleTime = Time.time;

        if (moved >= stuckProgressEpsilon)
        {
            _antiStuckStrikes = 0; // advancing fine
            return;
        }

        // Wedged in place while wanting to move. Count strikes (decaying) and escalate from nudges to giving up.
        if (Time.time > _antiStuckStrikeExpiry)
            _antiStuckStrikes = 0;
        _antiStuckStrikes++;
        _antiStuckStrikeExpiry = Time.time + 2f;

        if (_antiStuckStrikes >= Mathf.Max(1, stuckStrikesBeforeGiveUp))
        {
            _antiStuckStrikes = 0;
            _antiStuckNudgeUntil = 0f;
            if (_state == SkeletonState.Chase)
            {
                AbandonUnreachableTarget(); // give up this target -> patrols away from the obstacle
            }
            else
            {
                // Patrol: turn around and pick a fresh waypoint away from whatever it's jammed against.
                _patrolForwardOverride = -transform.forward;
                _patrolForwardOverrideUntil = Time.time + 1.5f;
                _hasPatrolDestination = false;
                TrySetNextPatrolDestination();
            }
            return;
        }

        BeginUnstickNudge();
    }

    /// <summary>Steer sideways (alternating sides) for a moment to slide off the corner of whatever is blocking it.</summary>
    void BeginUnstickNudge()
    {
        Vector3 forward = navMeshAgent != null ? navMeshAgent.desiredVelocity : transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
            forward = transform.forward;
        forward.Normalize();

        Vector3 perpendicular = Vector3.Cross(Vector3.up, forward); // to the right of travel
        _antiStuckSide ^= 1;
        if (_antiStuckSide == 1)
            perpendicular = -perpendicular; // alternate sides so a blocked side flips to the other next time

        _antiStuckNudgeDir = (perpendicular * 0.85f + forward * 0.35f).normalized; // mostly sideways, a little forward
        _antiStuckNudgeUntil = Time.time + Mathf.Max(0.1f, unstickDuration);
        _antiStuckSamplePos = transform.position;
        _antiStuckSampleTime = Time.time;
    }

    // ---- Patrol ------------------------------------------------------------

    Vector3 UpdatePatrol()
    {
        if (_state != SkeletonState.Patrol)
            _state = SkeletonState.Patrol;

        // Turned around by a pit last frame? Bias the next waypoint away from it and repath now.
        if (_pitBlocked)
        {
            _pitBlocked = false;
            _patrolForwardOverride = -transform.forward;
            _patrolForwardOverrideUntil = Time.time + 1.5f;
            _hasPatrolDestination = false;
            _nextPatrolRepathTime = Time.time + patrolRepathCooldown;
            TrySetNextPatrolDestination();
        }

        if (!patrolWhenNoTarget)
        {
            StopNavigation();
            return Vector3.zero;
        }

        if (!TrySnapToNavMesh() || navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return Vector3.zero;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = walkSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.2f, patrolArrivalDistance * 0.8f);

        if (!_hasPatrolDestination)
        {
            TrySetNextPatrolDestination();
            return Vector3.zero;
        }

        if (Time.time >= _nextPatrolRefreshTime)
        {
            navMeshAgent.SetDestination(_patrolDestination);
            _nextPatrolRefreshTime = Time.time + Mathf.Max(0.1f, patrolDestinationRefreshInterval);
        }

        if (!navMeshAgent.pathPending)
        {
            if (!navMeshAgent.hasPath
                || navMeshAgent.pathStatus == NavMeshPathStatus.PathInvalid
                || navMeshAgent.pathStatus == NavMeshPathStatus.PathPartial)
            {
                if (Time.time >= _nextPatrolRepathTime)
                {
                    _nextPatrolRepathTime = Time.time + Mathf.Max(0.1f, patrolRepathCooldown);
                    TrySetNextPatrolDestination();
                }
                return Vector3.zero;
            }

            if (navMeshAgent.remainingDistance <= patrolArrivalDistance)
            {
                RememberPatrolDestination(_patrolDestination);
                _hasPatrolDestination = false;
                TrySetNextPatrolDestination();
            }
            else if (Time.time >= _nextPatrolProgressTime)
            {
                float remaining = navMeshAgent.remainingDistance;
                float gained = _patrolPrevRemaining - remaining;
                if (gained < 0.1f)
                    _patrolStuckTime += 0.35f;
                else
                    _patrolStuckTime = 0f;

                _patrolPrevRemaining = remaining;
                _nextPatrolProgressTime = Time.time + 0.35f;

                if (_patrolStuckTime >= patrolStuckSeconds && Time.time >= _nextPatrolRepathTime)
                {
                    _nextPatrolRepathTime = Time.time + Mathf.Max(0.1f, patrolRepathCooldown);
                    _patrolStuckTime = 0f;
                    TrySetNextPatrolDestination();
                }
            }
        }

        Vector3 desired = navMeshAgent.desiredVelocity;
        desired.y = 0f;
        if (desired.sqrMagnitude > walkSpeed * walkSpeed)
            desired = desired.normalized * walkSpeed;
        return desired;
    }

    bool TrySetNextPatrolDestination()
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || !navMeshAgent.isOnNavMesh)
            return false;
        if (!TryPickPatrolDestination(out Vector3 destination))
            return false;
        if (!navMeshAgent.SetDestination(destination))
            return false;

        _patrolDestination = destination;
        _hasPatrolDestination = true;
        _nextPatrolRefreshTime = Time.time + Mathf.Max(0.1f, patrolDestinationRefreshInterval);
        _nextPatrolProgressTime = Time.time + 0.35f;
        _patrolPrevRemaining = float.PositiveInfinity;
        _patrolStuckTime = 0f;
        return true;
    }

    bool TryPickPatrolDestination(out Vector3 destination)
    {
        destination = transform.position;
        Vector3 origin = transform.position;
        Vector3 forward = _patrolForwardOverrideUntil > Time.time ? _patrolForwardOverride : transform.forward;
        forward.y = 0f;
        forward = forward.sqrMagnitude < 0.0001f ? Vector3.forward : forward.normalized;

        float minDistance = Mathf.Max(1f, patrolMinWaypointDistance);
        float maxDistance = Mathf.Max(minDistance + 1f, patrolMaxWaypointDistance);
        int attempts = Mathf.Max(4, patrolSampleAttempts);
        float sampleRadius = Mathf.Max(1.5f, maxDistance * 0.7f);

        Vector3 best = Vector3.zero;
        float bestScore = float.MinValue;
        bool found = false;

        for (int i = 0; i < attempts; i++)
        {
            Vector2 r2 = Random.insideUnitCircle;
            Vector3 randomDir = new Vector3(r2.x, 0f, r2.y);
            if (randomDir.sqrMagnitude < 0.0001f)
                randomDir = forward;
            randomDir.Normalize();

            float bias = Random.Range(0.35f, 0.8f);
            Vector3 dir = (forward * bias + randomDir * (1f - bias)).normalized;
            float distance = Random.Range(minDistance, maxDistance);
            Vector3 raw = origin + dir * distance;

            if (!NavMesh.SamplePosition(raw, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
                continue;

            Vector3 flatTo = hit.position - origin;
            flatTo.y = 0f;
            float flatDistance = flatTo.magnitude;
            if (flatDistance < minDistance * 0.55f)
                continue;

            bool avoidRecent = i < attempts - 3;
            if (avoidRecent && IsNearRecentPatrol(hit.position))
                continue;

            float dirScore = flatDistance > 0.01f ? Vector3.Dot(forward, flatTo / flatDistance) : 0f;
            float score = dirScore + Random.Range(0f, 0.25f);
            if (score > bestScore)
            {
                bestScore = score;
                best = hit.position;
                found = true;
            }
        }

        if (found)
        {
            destination = best;
            return true;
        }
        return false;
    }

    void RememberPatrolDestination(Vector3 p)
    {
        _recentPatrol.Enqueue(p);
        while (_recentPatrol.Count > 6)
            _recentPatrol.Dequeue();
    }

    bool IsNearRecentPatrol(Vector3 p)
    {
        foreach (var q in _recentPatrol)
            if ((q - p).sqrMagnitude < 3.5f * 3.5f)
                return true;
        return false;
    }

    // ---- Throw / bash ------------------------------------------------------

    IEnumerator ThrowRoutine()
    {
        _state = SkeletonState.Throw;
        StopNavigation();
        _horizontalVelocity = Vector3.zero;
        FaceTarget();

        CrossFade(throwStateName, actionCrossfade);
        PlaySfx(throwSfx);

        if (throwReleaseDelay > 0f)
            yield return new WaitForSeconds(throwReleaseDelay);

        if (_state != SkeletonState.Dead && _targetHealth != null && !_targetHealth.IsDead)
        {
            FaceTarget();
            SetHeldSkullHidden(true); // the held skull becomes the projectile
            LaunchProjectile();
        }

        _nextThrowTime = Time.time + throwCooldown;
        _nextBashTime = Mathf.Max(_nextBashTime, Time.time + Mathf.Min(throwCooldown, bashCooldown) * 0.5f);

        float recovery = Mathf.Max(0f, throwCooldown - throwReleaseDelay);
        if (recovery > 0f)
            yield return new WaitForSeconds(Mathf.Min(recovery, 0.6f));

        SetHeldSkullHidden(false); // a fresh skull appears back in the hand
        _actionRoutine = null;
        if (_state != SkeletonState.Dead)
            _state = _targetHealth != null && !_targetHealth.IsDead ? SkeletonState.Chase : SkeletonState.Patrol;
    }

    IEnumerator BashRoutine()
    {
        _state = SkeletonState.Bash;
        StopNavigation();
        _horizontalVelocity = Vector3.zero;
        FaceTarget();

        Vector3 committedDirection = GetFacingToTarget();
        CrossFade(bashStateName, actionCrossfade);
        PlaySfx(bashSfx);

        if (bashHitDelay > 0f)
            yield return new WaitForSeconds(bashHitDelay);

        if (_state != SkeletonState.Dead && CanLandBash(_targetHealth, committedDirection))
            ApplyBashHit(_targetHealth, committedDirection);

        _nextBashTime = Time.time + bashCooldown;

        float recovery = Mathf.Max(0f, bashCooldown - bashHitDelay);
        if (recovery > 0f)
            yield return new WaitForSeconds(Mathf.Min(recovery, 0.6f));

        _actionRoutine = null;
        if (_state != SkeletonState.Dead)
            _state = _targetHealth != null && !_targetHealth.IsDead ? SkeletonState.Chase : SkeletonState.Patrol;
    }

    void LaunchProjectile()
    {
        if (throwProjectilePrefab == null)
            return;

        Vector3 origin = throwOrigin != null ? throwOrigin.position : transform.position + Vector3.up * 1.4f + transform.forward * 0.4f;
        Vector3 target = _target.position + Vector3.up * throwTargetHeightOffset;
        Quaternion rot = Quaternion.LookRotation((target - origin).sqrMagnitude > 1e-4f ? (target - origin).normalized : transform.forward);

        GameObject projectile = Instantiate(throwProjectilePrefab, origin, rot);

        bool networkActive = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        if (networkActive)
        {
            NetworkObject no = projectile.GetComponent<NetworkObject>();
            if (no != null && !no.IsSpawned)
                no.Spawn();
        }

        SkeletonThrownObject thrown = projectile.GetComponent<SkeletonThrownObject>();
        if (thrown != null)
            thrown.Launch(origin, target, projectileArcHeight, projectileFlightDuration);
    }

    bool HasThrowClearance()
    {
        if (_target == null)
            return false;

        Vector3 origin = throwOrigin != null ? throwOrigin.position : transform.position + Vector3.up * 1.4f;
        Vector3 horizontal = _target.position - origin;
        horizontal.y = 0f;
        if (horizontal.sqrMagnitude < 1e-4f)
            return true;
        horizontal.Normalize();

        int mask = MaskExcludingActors(throwClearanceMask);
        if (mask == 0)
            return true;

        return !Physics.Raycast(origin, horizontal, throwClearanceDistance, mask, QueryTriggerInteraction.Ignore);
    }

    bool CanLandBash(PlayerHealth targetHealth, Vector3 committedDirection)
    {
        if (targetHealth == null || targetHealth.IsDead)
            return false;

        Vector3 toTarget = targetHealth.transform.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        if (distance > bashRange + bashHitRangePadding)
            return false;

        if (distance > 0.001f)
        {
            float angle = Vector3.Angle(committedDirection, toTarget / distance);
            if (angle > bashHitHalfAngle)
                return false;
        }

        if (bashRequiresLineOfSight && !HasLineOfSight(targetHealth, detectionLineOfSightMask, detectionLineOfSightHeight, committedDirection * 0.15f))
            return false;

        return true;
    }

    void ApplyBashHit(PlayerHealth targetHealth, Vector3 committedDirection)
    {
        targetHealth.TakeDamage(bashDamage);

        Vector3 pushVel = committedDirection * bashPushHorizontalSpeed;
        bool networkActive = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        if (networkActive && networkAvatar != null)
        {
            NetworkObject playerNo = targetHealth.GetComponent<NetworkObject>();
            if (playerNo != null)
                networkAvatar.ServerRelayPush(playerNo, pushVel, bashPushUpwardSpeed, bashPushControlLockSeconds);
        }
        else
        {
            targetHealth.GetComponent<PlayerController>()?.ApplyExternalPush(pushVel, bashPushUpwardSpeed, bashPushControlLockSeconds);
        }
    }

    void SetHeldSkullHidden(bool hidden)
    {
        if (networkAvatar != null)
            networkAvatar.SetHeldSkullHidden(hidden);
    }

    // ---- Damage hooks (called by SkeletonHealth) ---------------------------

    public void NotifyAttackedBy(Transform attacker, PlayerHealth attackerHealth)
    {
        if (attackerHealth == null && attacker != null)
            attackerHealth = attacker.GetComponentInParent<PlayerHealth>();

        if (attackerHealth == null || attackerHealth.IsDead)
            return;

        _targetHealth = attackerHealth;
        _target = attackerHealth.transform;
    }

    public void TakeHit()
    {
        if (_state == SkeletonState.Dead)
            return;

        if (_actionRoutine != null)
        {
            StopCoroutine(_actionRoutine);
            _actionRoutine = null;
        }

        SetHeldSkullHidden(false); // restore the skull if a throw was interrupted

        _state = SkeletonState.HitReaction;
        _hitReactionEndTime = Time.time + hitReactionDuration;
        _nextBashTime = _hitReactionEndTime + bashCooldown * 0.5f;
        _nextThrowTime = Mathf.Max(_nextThrowTime, _hitReactionEndTime);

        StopNavigation();
        _horizontalVelocity = Vector3.zero;

        CrossFade(hitReactionStateName, hitReactionCrossfade);
    }

    void UpdateHitReaction()
    {
        FaceTarget();
        if (Time.time < _hitReactionEndTime)
            return;

        _state = _targetHealth != null && !_targetHealth.IsDead ? SkeletonState.Chase : SkeletonState.Patrol;
        CrossFade(_state == SkeletonState.Chase ? walkStateName : idleStateName, hitReactionExitCrossfade);
    }

    public void HandleDeath()
    {
        if (_state == SkeletonState.Dead)
            return;

        _state = SkeletonState.Dead;

        if (_actionRoutine != null)
        {
            StopCoroutine(_actionRoutine);
            _actionRoutine = null;
        }

        StopNavigation();
        if (navMeshAgent != null)
            navMeshAgent.enabled = false;

        _horizontalVelocity = Vector3.zero;
        _verticalVelocity = Vector3.zero;
        ClearTarget();
    }

    // ---- Detection ---------------------------------------------------------

    void RefreshTarget()
    {
        if (_targetHealth != null && !_targetHealth.IsDead)
            return;

        int mask = detectionMask.value == 0 ? Physics.DefaultRaycastLayers : detectionMask.value;
        int hitCount = Physics.OverlapSphereNonAlloc(transform.position, detectionRadius, _detectionHits, mask, QueryTriggerInteraction.Ignore);

        PlayerHealth closest = null;
        float closestDistance = float.MaxValue;

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = _detectionHits[i];
            _detectionHits[i] = null;
            if (hit == null)
                continue;

            PlayerHealth candidate = hit.GetComponentInParent<PlayerHealth>();
            if (candidate == null || candidate.IsDead)
                continue;
            if (IsUnreachableSuppressed(candidate))
                continue;
            if (!HasDetectionLineOfSight(candidate))
                continue;

            float distance = Vector3.Distance(transform.position, candidate.transform.position);
            if (distance >= closestDistance)
                continue;

            closest = candidate;
            closestDistance = distance;
        }

        if (closest == null)
        {
            IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerHealth candidate = players[i];
                if (candidate == null || candidate.IsDead)
                    continue;
                if (IsUnreachableSuppressed(candidate))
                    continue;

                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance > detectionRadius || distance >= closestDistance)
                    continue;
                if (!HasDetectionLineOfSight(candidate))
                    continue;

                closest = candidate;
                closestDistance = distance;
            }
        }

        if (closest == null)
            return;

        _targetHealth = closest;
        _target = closest.transform;
    }

    /// <summary>True while a target we just gave up on (because it was unreachable) is still being ignored.</summary>
    bool IsUnreachableSuppressed(PlayerHealth candidate)
    {
        return candidate != null && candidate == _unreachableTargetHealth && Time.time < _unreachableTargetUntil;
    }

    bool HasDetectionLineOfSight(PlayerHealth targetHealth)
    {
        if (!requireDetectionLineOfSight)
            return true;
        return HasLineOfSight(targetHealth, detectionLineOfSightMask, detectionLineOfSightHeight, Vector3.zero);
    }

    bool HasLineOfSight(PlayerHealth targetHealth, LayerMask lineOfSightMask, float lineOfSightHeight, Vector3 originOffset)
    {
        if (targetHealth == null)
            return false;

        Vector3 origin = transform.position + Vector3.up * lineOfSightHeight + originOffset;
        Vector3 targetPoint = targetHealth.transform.position + Vector3.up * lineOfSightHeight;
        Vector3 toTarget = targetPoint - origin;
        float distance = toTarget.magnitude;
        if (distance <= 0.001f)
            return true;

        int mask = lineOfSightMask.value == 0 ? Physics.DefaultRaycastLayers : lineOfSightMask.value;
        int hitCount = Physics.RaycastNonAlloc(origin, toTarget / distance, _lineOfSightHits, distance, mask, QueryTriggerInteraction.Ignore);
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

    // ---- Movement ----------------------------------------------------------

    void ApplyMovement(Vector3 desiredHorizontalVelocity)
    {
        if (characterController == null || !characterController.enabled)
            return;

        bool grounded = characterController.isGrounded;
        if (grounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -groundedStickDown;

        bool frozen = _state == SkeletonState.HitReaction || _state == SkeletonState.Dead
            || _state == SkeletonState.Throw || _state == SkeletonState.Bash;
        _horizontalVelocity = frozen ? Vector3.zero : desiredHorizontalVelocity;

        // Mid-nudge: a prop/wall wedged the body, so steer sideways for a moment to slip around it.
        if (!frozen && recoverFromStuck && Time.time < _antiStuckNudgeUntil)
            _horizontalVelocity = _antiStuckNudgeDir * walkSpeed;

        // Can't cross pits: if the next step has no ground ahead, cancel it (and flag patrol to turn around).
        // Recomputed every frame so it reflects only the current step (a stationary skeleton isn't pit-blocked).
        _pitBlocked = false;
        if (avoidPits && !frozen && _horizontalVelocity.sqrMagnitude > 0.0001f && WouldStepIntoPit(_horizontalVelocity))
        {
            _horizontalVelocity = Vector3.zero;
            _pitBlocked = true;
        }

        _verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 motion = _horizontalVelocity * Time.deltaTime;
        motion.y = _verticalVelocity.y * Time.deltaTime;
        characterController.Move(motion);

        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
            navMeshAgent.nextPosition = transform.position;

        Vector3 horizontalDirection = _horizontalVelocity;
        horizontalDirection.y = 0f;
        if (horizontalDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(horizontalDirection.normalized);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }
    }

    bool WouldStepIntoPit(Vector3 horizontalVelocity)
    {
        Vector3 dir = horizontalVelocity;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
            return false;
        dir.Normalize();

        Vector3 probeOrigin = transform.position + Vector3.up * pitProbeUpOffset + dir * pitProbeForwardDistance;
        int mask = MaskExcludingActors(groundMask);
        if (mask == 0)
            return false;

        bool groundAhead = Physics.Raycast(probeOrigin, Vector3.down, pitProbeDepth, mask, QueryTriggerInteraction.Ignore);
        return !groundAhead;
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

    void FaceTarget()
    {
        if (_target == null)
            return;

        Vector3 lookDirection = _target.position - transform.position;
        lookDirection.y = 0f;
        if (lookDirection.sqrMagnitude < 0.0001f)
            return;

        Quaternion targetRotation = Quaternion.LookRotation(lookDirection.normalized);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
    }

    Vector3 GetFacingToTarget()
    {
        if (_target == null)
            return transform.forward;
        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0f;
        return toTarget.sqrMagnitude < 0.0001f ? transform.forward : toTarget.normalized;
    }

    void StopNavigation()
    {
        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }
    }

    void ClearTarget()
    {
        _target = null;
        _targetHealth = null;
        _chaseUnreachableTime = 0f;
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
        return true;
    }

    // ---- Animator ----------------------------------------------------------

    void UpdateAnimatorParameters()
    {
        if (animator == null)
            return;

        float reference = Mathf.Max(walkSpeed, 0.001f);
        float horizontalSpeed = _horizontalVelocity.magnitude;
        float normalizedSpeed = Mathf.Clamp01(horizontalSpeed / reference);
        animator.SetFloat(speedParameter, normalizedSpeed);

        // Match the in-place walk's foot cadence to actual ground speed so the feet don't skate.
        float playback = Mathf.Clamp(horizontalSpeed / Mathf.Max(0.01f, walkAnimMatchSpeed), 0.3f, 2f);
        animator.SetFloat(walkPlaybackParameter, playback);
    }

    void CrossFade(string stateName, float duration)
    {
        if (animator == null || string.IsNullOrEmpty(stateName))
            return;
        animator.CrossFadeInFixedTime(stateName, duration, 0, 0f);
    }

    // ---- Setup helpers -----------------------------------------------------

    void CacheReferences()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
        if (navMeshAgent == null)
            navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();
        if (skeletonHealth == null)
            skeletonHealth = GetComponent<SkeletonHealth>();
        if (networkAvatar == null)
            networkAvatar = GetComponent<NetworkSkeletonAvatar>();
    }

    void ResolveBones()
    {
        if (throwOrigin != null && _hipsBone != null)
            return;

        Transform[] all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            if (throwOrigin == null && all[i].name == RightHandBoneName)
                throwOrigin = all[i];
            if (_hipsBone == null && all[i].name == HipsBoneName)
                _hipsBone = all[i];
        }
    }

    void ApplyAgentSettings()
    {
        if (navMeshAgent == null)
            return;

        navMeshAgent.speed = walkSpeed;
        navMeshAgent.angularSpeed = rotationSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.1f, bashRange * 0.9f);
        navMeshAgent.acceleration = Mathf.Max(navMeshAgent.acceleration, walkSpeed * 4f);
        navMeshAgent.updatePosition = false;
        navMeshAgent.updateRotation = false;
        navMeshAgent.baseOffset = 0f;
        navMeshAgent.avoidancePriority = 50;

        if (characterController != null)
        {
            characterController.skinWidth = 0.02f;
            characterController.minMoveDistance = 0.001f;
        }
    }

    void EnsureSfxSource()
    {
        _sfxSource = GetComponent<AudioSource>();
        if (_sfxSource == null)
            return;
        _sfxSource.playOnAwake = false;
        _sfxSource.spatialBlend = 1f;
    }

    void PlaySfx(AudioClip clip)
    {
        if (clip == null || _sfxSource == null)
            return;
        _sfxSource.PlayOneShot(clip);
    }

    void UpdateFootsteps()
    {
        if (footstepAudioSource == null || animator == null)
            return;

        // Lock footsteps to the walk animation's cycle so they always match the visible feet, at any playback speed.
        float speed = animator.GetFloat(speedParameter);
        AnimatorStateInfo st = animator.GetCurrentAnimatorStateInfo(0);
        bool walking = speed > minFootstepSpeed && st.shortNameHash == _walkStateHash;
        if (!walking)
        {
            _footstepPrimed = false;
            return;
        }

        // normalizedTime accumulates across loops; one whole-number increment of (time * stepsPerCycle) = one footstep.
        int index = Mathf.FloorToInt((st.normalizedTime + footstepPhaseOffset) * Mathf.Max(1f, footstepsPerCycle));
        if (!_footstepPrimed)
        {
            _footstepCycleIndex = index;
            _footstepPrimed = true;
            return;
        }
        if (index <= _footstepCycleIndex)
            return;
        _footstepCycleIndex = index;

        AudioClip clip = _footstepToggle ? footstepClip2 : footstepClip1;
        _footstepToggle = !_footstepToggle;
        if (clip == null)
            clip = footstepClip1 != null ? footstepClip1 : footstepClip2;
        if (clip != null)
            footstepAudioSource.PlayOneShot(clip, Mathf.Clamp01(footstepVolume));
    }

    void ConfigureFootstepSource()
    {
        if (footstepAudioSource == null)
            footstepAudioSource = FindChildAudioSource("Skeleton_Footsteps");
        if (footstepAudioSource == null)
            return;

        footstepAudioSource.playOnAwake = false;
        footstepAudioSource.loop = false;
        footstepAudioSource.spatialBlend = 1f;
        footstepAudioSource.minDistance = 1.5f;
        footstepAudioSource.maxDistance = 25f;
        footstepAudioSource.rolloffMode = AudioRolloffMode.Linear;
        footstepAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(footstepAudioSource);
    }

    AudioSource FindChildAudioSource(string childName)
    {
        for (int i = 0; i < transform.childCount; i++)
        {
            Transform c = transform.GetChild(i);
            if (c.name == childName)
                return c.GetComponent<AudioSource>();
        }
        return null;
    }

#if UNITY_EDITOR
    void AutoAssignFootstepClips()
    {
        if (footstepClip1 == null)
            footstepClip1 = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Dungeon/SkeletonFootstep1.wav");
        if (footstepClip2 == null)
            footstepClip2 = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Dungeon/SkeletonFootstep2.wav");
    }
#endif

    sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
    {
        public static readonly RaycastHitDistanceComparer Instance = new RaycastHitDistanceComparer();
        public int Compare(RaycastHit x, RaycastHit y) => x.distance.CompareTo(y.distance);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.6f, 0.9f, 1f, 0.7f);
        Gizmos.DrawWireSphere(transform.position, detectionRadius);
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, throwRange);
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, bashRange);
    }
}
