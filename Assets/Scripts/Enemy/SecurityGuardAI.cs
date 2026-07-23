using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The Severance security guard — Level03's hunter (fills the Jailor/Clown "Enemy 2" slot).
/// A silent, suited martial artist: patrols the porcelain halls at a calm walk, sprints when he spots a
/// player, and fights with a four-move kit (jab, quad-punch flurry, shoving MMA kick, and a ragdolling
/// hurricane-kick gap-closer). He cannot be killed; punching him chips a poise meter — breaking it buys a
/// short stagger escape window, at the risk of an instant counter-kick.
///
/// (An all-fours "running crawl" escalation was prototyped and removed — the guard now escalates
/// purely through his attack pressure, not a movement mode.)
///
/// Server-authoritative like <see cref="ZombieAI"/>: the server simulates movement/combat via
/// CharacterController + NavMeshAgent; clients keep this component enabled purely for cosmetic audio driven
/// by the replicated transform/animator (see <see cref="NetworkSecurityGuardAvatar"/>).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class SecurityGuardAI : MonoBehaviour
{
    const string FootstepAudioChildName = "Guard_Footsteps";
    const string FxAudioChildName = "Guard_Fx";

    enum GuardState
    {
        Patrol,
        Investigate,
        Chase,
        Attack,
        Stagger
    }

    enum GuardAttack
    {
        Punch,
        QuadPunch,
        MmaKick,
        HurricaneKick
    }

    [Header("References")]
    [SerializeField] Animator animator;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;

    [Header("Footsteps")]
    [SerializeField] AudioSource footstepAudioSource;
    [Tooltip("Played the frame the LEFT toe plants.")]
    [SerializeField] AudioClip leftFootstepClip;
    [Tooltip("Played the frame the RIGHT toe plants.")]
    [SerializeField] AudioClip rightFootstepClip;
    [SerializeField, Range(0f, 1f)] float footstepVolume = 0.35f;
    [Tooltip("Random pitch spread per step so a repeated clip doesn't machine-gun.")]
    [SerializeField, Range(0f, 0.3f)] float footstepPitchVariance = 0.06f;

    [Header("Footstep sync")]
    [Tooltip("Steps are fired by the ANIMATION, not a timer: a toe bone dropping below this height " +
             "(metres above the guard's root) counts as planting. Measured foot travel on this rig — " +
             "walk peaks at ~0.2m, run at ~0.8m — so 0.08 catches both cleanly.")]
    [SerializeField] float footContactHeight = 0.08f;
    [Tooltip("The toe must rise back above this before it can register another step. Kills the " +
             "double-tap the walk cycle's heel-toe roll would otherwise produce. Must stay below the " +
             "walk's lowest peak (~0.16m) or that foot can never re-arm.")]
    [SerializeField] float footLiftResetHeight = 0.14f;

    [Header("Attack SFX")]
    [SerializeField] AudioSource fxAudioSource;
    [Tooltip("Whoosh played when an attack animation starts. Keys off the (replicated) animator state, so every peer hears it without extra netcode.")]
    [SerializeField] AudioClip attackWhooshClip;
    [SerializeField, Range(0f, 1f)] float attackWhooshVolume = 0.6f;

    [Header("Detection")]
    [SerializeField] LayerMask detectionMask;
    [SerializeField] float detectionRadius = 16f;
    [Tooltip("Half-angle of the vision cone, from facing. Players outside it are only found by sound/contact — sneaking behind works.")]
    [SerializeField, Range(10f, 180f)] float detectionFovHalfAngleDegrees = 95f;
    [Tooltip("Radius at which he HEARS an audibly-sprinting player — no cone or line-of-sight needed.")]
    [SerializeField, Min(0f)] float hearingRadius = 18f;
    [SerializeField] bool requireDetectionLineOfSight = true;
    [SerializeField] LayerMask detectionLineOfSightMask = Physics.DefaultRaycastLayers;
    [SerializeField] float detectionLineOfSightHeight = 1.35f;
    [Tooltip("Seconds between target-acquisition scans while he has no target.")]
    [SerializeField, Min(0f)] float sensingInterval = 0.1f;
    [Tooltip("Seconds he keeps chasing toward the last-seen position after losing sight before dropping to Investigate.")]
    [SerializeField, Min(0f)] float targetMemorySeconds = 5f;

    [Header("Movement")]
    [SerializeField] float patrolSpeed = 1.8f;
    [Tooltip("Just under the player's 4.8 sprint: sprinting opens only a slow trickle of a gap, and stamina runs out long before he does.")]
    [SerializeField] float chaseSpeed = 4.7f;
    [SerializeField] float rotationSpeed = 620f;
    [SerializeField] float gravity = -20f;
    [SerializeField] float groundedStickDown = 2f;
    [SerializeField] float targetNavMeshSampleRadius = 3f;

    [Header("Patrol")]
    [Tooltip("Random reachable points are picked within this radius of his current position.")]
    [SerializeField] float patrolPointRadius = 26f;
    [SerializeField] float patrolArrivalDistance = 1.2f;
    [Tooltip("Seconds of no progress before he gives up on the current patrol point and picks another.")]
    [SerializeField] float patrolStuckSeconds = 3f;

    [Header("Investigate")]
    [Tooltip("Seconds he lingers and scans at the last-known position before returning to patrol.")]
    [SerializeField] float investigateDwellSeconds = 4f;
    [SerializeField] float investigateArrivalDistance = 1.4f;

    [Header("Combat — shared")]
    [SerializeField] float meleeRange = 1.9f;
    [Tooltip("Extra distance before an attack starts so the swing begins slightly out of range.")]
    [SerializeField] float attackStartDistancePadding = 0.3f;
    [Tooltip("Extra reach on top of Melee Range when a landed hit is validated.")]
    [SerializeField] float attackHitRangePadding = 0.35f;
    [SerializeField, Range(0f, 180f)] float attackHitHalfAngle = 55f;
    [SerializeField] bool requireAttackLineOfSight = true;
    [SerializeField] LayerMask attackLineOfSightMask = Physics.DefaultRaycastLayers;
    [SerializeField] float attackLineOfSightHeight = 1.15f;
    [Tooltip("Minimum seconds between the end of one attack and the start of the next.")]
    [SerializeField] float attackCooldownSeconds = 0.9f;

    [Header("Combat — pressure & attack drive")]
    [Tooltip("Within this range the chase drops from the sprint to a stalking advance — he walks you down between attack strings instead of flip-flopping between run and stand.")]
    [SerializeField] float engagedWalkEnterDistance = 3.4f;
    [Tooltip("Hysteresis: he only breaks back into the run once you open the gap past this.")]
    [SerializeField] float engagedWalkExitDistance = 4.6f;
    [Tooltip("Advance speed while stalking between attacks. At melee range he keeps crowding to point-blank instead of standing still.")]
    [SerializeField] float pressureAdvanceSpeed = 2f;
    [Tooltip("Attacks carry him forward — punching while stepping in — so plain backpedaling no longer makes the whole kit whiff. The drive follows his facing and stops at Attack Drive Min Gap from the target.")]
    [SerializeField] float punchDriveSpeed = 3.4f;
    [SerializeField] float quadPunchDriveSpeed = 3f;
    [SerializeField] float mmaKickDriveSpeed = 3.6f;
    [SerializeField, Min(0f)] float attackDriveMinGap = 0.95f;
    [Tooltip("Turn rate while an attack tracks you during its wind-up. Low enough that a hard SIDE-step still gets outside the hit cone — backpedaling doesn't.")]
    [SerializeField] float attackTrackingTurnSpeed = 210f;
    [Tooltip("Seconds before an attack's impact when the tracking locks. This is the side-step dodge window.")]
    [SerializeField, Min(0f)] float attackCommitLeadSeconds = 0.25f;

    [Header("Combat — decision making")]
    [Tooltip("A landed jab within this window chains into the Quad Punch flurry.")]
    [SerializeField] float comboFollowUpSeconds = 3f;
    [SerializeField, Range(0f, 1f)] float comboFollowUpChance = 0.75f;
    [Tooltip("After this many consecutive whiffed attacks (you keep slipping out of reach) he stops trading jabs and answers with the hurricane lunge.")]
    [SerializeField, Min(1)] int whiffsBeforeHurricanePunish = 4;
    [Tooltip("Inside this range the MMA kick is preferred — you're crowding him, and he boots you off.")]
    [SerializeField] float crowdedKickDistance = 1.7f;

    [Header("Combat — jab (Punching)")]
    [SerializeField] float punchDamage = 12f;
    [SerializeField] float punchHitDelay = 0.5f;
    [SerializeField] float punchRecovery = 0.5f;

    [Header("Combat — flurry (Quad Punch)")]
    [Tooltip("Damage per individual hook. These are quick light punches — the flurry's threat is the " +
             "total if you stand in all four, not any single hit.")]
    [SerializeField] float quadPunchTickDamage = 7f;
    [Tooltip("Seconds from the flurry's start to each punch's impact. Measured off the clip's hand " +
             "extension peaks (left-right-left-right). Every entry is one damage check that " +
             "re-validates range and angle on its own, so backing off or slipping aside mid-flurry " +
             "drops the remaining hits — all four only land on someone who stands still for the whole " +
             "combo. Re-measure if the Quad Punch clip is ever swapped.")]
    [SerializeField] float[] quadPunchHitTimes = { 0.59f, 0.83f, 1.06f, 1.33f };
    [SerializeField] float quadPunchRecovery = 0.9f;
    [SerializeField, Range(0f, 1f)] float quadPunchChance = 0.3f;

    [Header("Combat — shove kick (Mma Kick)")]
    [SerializeField] float mmaKickDamage = 20f;
    [SerializeField] float mmaKickHitDelay = 0.75f;
    [SerializeField] float mmaKickRecovery = 0.7f;
    [SerializeField, Range(0f, 1f)] float mmaKickChance = 0.25f;
    [Tooltip("Horizontal shove speed applied to the kicked player (non-ragdoll, like the skeleton bash).")]
    [SerializeField] float mmaKickPushSpeed = 7f;
    [SerializeField] float mmaKickPushUpwardSpeed = 3f;
    [SerializeField] float mmaKickPushControlLockSeconds = 0.35f;
    [SerializeField] float mmaKickCooldownSeconds = 5f;

    [Header("Combat — hurricane kick (ragdoll launcher)")]
    [Tooltip("Used as a gap-closer when the target is in this band and pulling away — backpedal-spamming out of his combos gets punished. The floor is deliberately well outside melee reach so this stays a rare 'you broke away' punish rather than a routine attack.")]
    [SerializeField] float hurricaneMinRange = 3f;
    [SerializeField] float hurricaneMaxRange = 5.6f;
    [SerializeField] float hurricaneDamage = 26f;
    [SerializeField] float hurricaneHitDelay = 1f;
    [SerializeField] float hurricaneRecovery = 0.8f;
    [Tooltip("Long by design: the launch is his most punishing move, so it should read as an occasional escalation, not part of the rotation. Every trigger path honours this.")]
    [SerializeField] float hurricaneCooldownSeconds = 24f;
    [Tooltip("Scripted forward lunge that carries the spin toward the target (the capsule really moves; walls stop it). Well above chase speed — the spin has to visibly pounce, not drift.")]
    [SerializeField] float hurricaneLungeSpeed = 9.5f;
    [SerializeField] float hurricaneLungeStartTime = 0.3f;
    [SerializeField] float hurricaneLungeEndTime = 1.05f;
    [SerializeField, Min(0f)] float hurricaneKnockbackForwardSpeed = 11f;
    [SerializeField, Min(0f)] float hurricaneKnockbackUpwardSpeed = 4.5f;
    [SerializeField] ForceMode hurricaneKnockbackForceMode = ForceMode.VelocityChange;
    [Tooltip("Redirect the launch toward open space so hallway hits throw the player down the hall, not into a wall.")]
    [SerializeField] bool knockbackAvoidWalls = true;
    [SerializeField, Min(0.5f)] float knockbackMinClearance = 3f;
    [SerializeField, Min(0f)] float knockbackProbeHeight = 0.5f;
    [SerializeField, Min(0.5f)] float knockbackForwardFullSpeedClearance = 4f;
    [SerializeField] LayerMask knockbackObstacleMask;
    [Tooltip("Wall safety: the lunge is only chosen when a spherecast of this radius toward the target is clear, and it aborts once a wall comes within Wall Abort Distance ahead.")]
    [SerializeField] float hurricaneClearanceRadius = 0.4f;
    [SerializeField] float hurricaneWallAbortDistance = 1f;
    [Tooltip("The capsule temporarily widens to this radius during the spin so the body can't hug a wall while kicking.")]
    [SerializeField] float hurricaneCapsuleRadius = 0.45f;

    [Header("Poise (unkillable, but punchable)")]
    [Tooltip("Player punches never damage him — they chip this meter. Breaking it earns the one full-body stagger (your escape window).")]
    [SerializeField, Min(1f)] float maxPoise = 100f;
    [Tooltip("40 = three quick punches break poise, with margin for the regen that trickles back between hits.")]
    [SerializeField, Min(0f)] float punchPoiseDamage = 40f;
    [SerializeField, Min(0f)] float poiseRegenDelay = 2.5f;
    [SerializeField, Min(0f)] float poiseRegenPerSecond = 25f;
    [SerializeField, Min(0.1f)] float poiseBreakStaggerSeconds = 1.2f;
    [Tooltip("After a poise break he cannot be staggered again for this long — hits still land and still draw counters, he simply refuses to go down again.")]
    [SerializeField, Min(0f)] float staggerImmunitySeconds = 6f;

    [Header("Counter attack")]
    [Tooltip("Chance a landed player punch triggers an instant retaliatory kick. Rolled per hit — no safe rhythm.")]
    [SerializeField, Range(0f, 1f)] float counterChance = 0.4f;
    [SerializeField, Range(0f, 1f)] float counterChanceWhileImmune = 0.7f;
    [SerializeField, Min(0f)] float counterCooldownSeconds = 2.5f;

    [Header("Animator")]
    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string runningParameter = "Running";
    [Tooltip("Drives the locomotion states' playback rate from how fast he is ACTUALLY moving, so the feet stay planted instead of skating. Rate = actual speed / the clip's own authored ground speed below.")]
    [SerializeField] string strideRateParameter = "StrideRate";
    [Tooltip("Ground speed each in-place clip was authored for, measured from planted-foot travel on THIS rig: " +
             "Walking 1.70, Running 5.41 m/s. Movement speed divided by these gives the playback rate that " +
             "keeps the feet planted. Re-measure whenever a clip is swapped or the rig's proportions change — " +
             "the walk clip measured 2.47 on the previous, much taller model.")]
    [SerializeField, Min(0.1f)] float walkClipGroundSpeed = 1.7f;
    [SerializeField, Min(0.1f)] float runClipGroundSpeed = 5.41f;
    [SerializeField, Range(0.1f, 1f)] float minStrideRate = 0.5f;
    [SerializeField, Range(1f, 2.5f)] float maxStrideRate = 1.6f;
    [SerializeField] float attackCrossfadeDuration = 0.12f;
    [SerializeField] float attackExitCrossfadeDuration = 0.18f;
    [SerializeField] string staggerStateName = "Stagger";
    [Tooltip("Masked layer carrying the punches (torso/head/arms only) so the legs keep walking or " +
             "running underneath them instead of freezing while the attack drive slides him forward. " +
             "The kicks and the stagger stay full-body on the base layer — they need the legs.")]
    [SerializeField] int upperBodyLayerIndex = 1;
    [SerializeField, Min(0.01f)] float upperBodyBlendSeconds = 0.12f;
    [Tooltip("Attack-drive speed at or above which the legs use the run cycle instead of the walk during a masked punch.")]
    [SerializeField] float attackDriveRunThreshold = 2.8f;

    [Header("Stagger wall clamp")]
    [Tooltip("The borrowed stagger clip leans the mesh past the capsule; clamp the hips so it can't lean through a wall behind him.")]
    [SerializeField] bool clampStaggerToWalls = true;
    [SerializeField] LayerMask staggerWallMask = Physics.DefaultRaycastLayers;
    [SerializeField] float staggerBodyRadius = 0.3f;
    [SerializeField] float staggerCastHeight = 1f;

    static readonly string[] AttackStateNames = { "Punch", "QuadPunch", "MmaKick", "HurricaneKick" };

    readonly Collider[] _detectionHits = new Collider[16];
    readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];

    GuardState _state = GuardState.Patrol;
    Transform _target;
    PlayerHealth _targetHealth;
    Vector3 _lastKnownTargetPosition;
    float _lastPerceivedTargetTime;
    float _nextSenseTime = -1f;

    // Patrol / investigate
    Vector3 _patrolDestination;
    bool _hasPatrolDestination;
    float _patrolNoProgressTimer;
    float _patrolPreviousRemainingDistance;
    Vector3 _investigatePoint;
    float _investigateDwellEndTime = -1f;
    float _investigateScanNextTurnTime;
    Quaternion _investigateScanRotation;
    float _investigateAbortTime;
    float _nextInvestigateRepathTime;
    float _nextChaseRepathTime;
    Vector3 _issuedChaseDestination;
    float _chaseStallTimer;

    // Combat
    Coroutine _attackRoutine;
    float _nextAttackTime;
    float _nextMmaKickTime;
    float _nextHurricaneTime;

    // Attack execution (set by AttackRoutine, consumed by UpdateAttack)
    float _attackDriveSpeed;
    float _attackDriveUntilTime;
    float _attackTrackUntilTime;
    bool _attackDriveIsHurricane;
    bool _currentAttackIsMasked;
    float _restoreCapsuleRadius = -1f;

    // Decision memory
    GuardAttack _lastAttack;
    float _lastAttackEndTime = -999f;
    bool _lastAttackLanded;
    int _consecutiveWhiffs;
    bool _engagedWalk;

    // Poise / stagger
    float _poise;
    float _poiseRegenBlockedUntil;
    float _staggerImmuneUntil;
    float _staggerEndTime;
    float _nextCounterRollTime;

    // Movement
    Vector3 _horizontalVelocity;
    Vector3 _verticalVelocity;
    float _intendedMoveSpeed;

    // Audio
    int _lastBaseStateHash;
    Transform _leftToe;
    Transform _rightToe;
    bool _leftFootArmed;
    bool _rightFootArmed;

    NetworkObject _networkObject;
    NetworkSecurityGuardAvatar _networkAvatar;
    Transform _hipsBone;

    float CurrentModeSpeed => _state == GuardState.Chase
        ? (_engagedWalk ? pressureAdvanceSpeed : chaseSpeed)
        : patrolSpeed;

    void Awake()
    {
        _networkObject = GetComponent<NetworkObject>();
        _networkAvatar = GetComponent<NetworkSecurityGuardAvatar>();
        _poise = maxPoise;
        CacheReferences();
        ConfigureFootstepAudioSource();
        ConfigureFxAudioSource();
        ApplyAgentSettings();
    }

    void Reset()
    {
        CacheReferences();
        ConfigureFootstepAudioSource();
        ConfigureFxAudioSource();
        ApplyAgentSettings();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        CacheReferences();
        ConfigureFootstepAudioSource(allowCreate: false);
        ConfigureFxAudioSource(allowCreate: false);
    }
#endif

    void OnEnable()
    {
        TrySnapToNavMesh();
    }

    void Update()
    {
        bool isNetworkClient = _networkObject != null
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer;

        // Attack whooshes key off the animator state so they play identically on server and clients.
        UpdateAttackWhooshWatcher();
        UpdateUpperBodyLayerWeight();

        if (isNetworkClient)
            return; // footsteps run in LateUpdate off the replicated pose; movement/AI are server-only

        if (_poise < maxPoise && Time.time >= _poiseRegenBlockedUntil)
            _poise = Mathf.Min(maxPoise, _poise + poiseRegenPerSecond * Time.deltaTime);

        if (_nextSenseTime < 0f)
            _nextSenseTime = Time.time + Random.Range(0f, Mathf.Max(0f, sensingInterval));

        RefreshTargetValidity();

        if (_target == null
            && (sensingInterval <= 0f || Time.time >= _nextSenseTime))
        {
            _nextSenseTime = Time.time + Mathf.Max(0f, sensingInterval);
            TryAcquireTarget();
        }

        Vector3 desiredHorizontalVelocity = Vector3.zero;
        switch (_state)
        {
            case GuardState.Patrol:
                desiredHorizontalVelocity = UpdatePatrol();
                break;
            case GuardState.Investigate:
                desiredHorizontalVelocity = UpdateInvestigate();
                break;
            case GuardState.Chase:
                desiredHorizontalVelocity = UpdateChase();
                break;
            case GuardState.Attack:
                desiredHorizontalVelocity = UpdateAttack();
                break;
            case GuardState.Stagger:
                UpdateStagger();
                break;
        }

        ApplyMovement(desiredHorizontalVelocity);
        UpdateAnimatorParameters();
    }

    void LateUpdate()
    {
        // After the animator has posed the rig this frame, so the toes read their true positions.
        UpdateFootstepsFromAnimation();

        // Clamp poses that throw the body past the capsule (the stagger lean and the extended
        // kicks) so the mesh can't pass through a wall. Runs on every peer — keyed off the
        // replicated animator state, not the server-only AI state.
        if (!clampStaggerToWalls || animator == null || _hipsBone == null)
            return;

        bool inHurricane = IsAnimatorInState(0, "HurricaneKick");
        bool clampPose = inHurricane
            || IsAnimatorInState(0, staggerStateName)
            || IsAnimatorInState(0, "MmaKick");
        if (!clampPose)
            return;

        Vector3 root = transform.position;
        Vector3 hips = _hipsBone.position;
        Vector3 horizontal = hips - root;
        horizontal.y = 0f;
        float distance = horizontal.magnitude;
        if (distance < 0.02f)
            return;

        Vector3 dir = horizontal / distance;
        int mask = MaskExcludingActors(staggerWallMask);
        if (mask == 0)
            return;

        float bodyRadius = inHurricane ? Mathf.Max(staggerBodyRadius, hurricaneClearanceRadius) : staggerBodyRadius;
        Vector3 castOrigin = root + Vector3.up * staggerCastHeight;
        if (Physics.SphereCast(castOrigin, bodyRadius, dir, out RaycastHit hit, distance, mask, QueryTriggerInteraction.Ignore))
        {
            float allowed = Mathf.Max(0f, hit.distance);
            if (allowed < distance)
                _hipsBone.position += dir * (allowed - distance);
        }
    }

    // ------------------------------------------------------------------
    // Targeting / senses
    // ------------------------------------------------------------------

    /// <summary>
    /// Drop the target only when it's truly gone (dead, or carried off). A ragdolled target is NOT
    /// dropped — he closes in and waits over them until they get up (see UpdateChase). Losing him
    /// takes breaking his perception, never his patience.
    /// </summary>
    void RefreshTargetValidity()
    {
        if (_targetHealth == null)
            return;

        bool gone = _targetHealth.IsDead || IsPlayerCarriedByJailor(_targetHealth);
        if (!gone)
            return;

        bool wasHunting = _state == GuardState.Chase || _state == GuardState.Attack;
        ClearTarget();

        if (_state == GuardState.Stagger || _attackRoutine != null)
            return;


        if (wasHunting)
            EnterPatrol(); // someone else may be near; sensing re-acquires from patrol immediately
    }

    void TryAcquireTarget()
    {
        PlayerHealth closestTarget = null;
        float closestDistance = float.MaxValue;

        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth candidate = players[i];
            if (!IsValidTargetCandidate(candidate))
                continue;

            float distance = Vector3.Distance(transform.position, candidate.transform.position);
            if (distance >= closestDistance)
                continue;

            bool seen = distance <= detectionRadius
                && IsWithinDetectionCone(candidate.transform.position)
                && HasDetectionLineOfSight(candidate);
            bool heard = hearingRadius > 0f
                && distance <= hearingRadius
                && IsPlayerAudiblySprinting(candidate);

            if (!seen && !heard)
                continue;

            closestTarget = candidate;
            closestDistance = distance;
        }

        // Fallback sweep for players missing from the registry (mirrors ZombieAI's physics scan).
        if (closestTarget == null)
        {
            int mask = detectionMask.value == 0 ? Physics.DefaultRaycastLayers : detectionMask.value;
            int hitCount = Physics.OverlapSphereNonAlloc(
                transform.position, detectionRadius, _detectionHits, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = _detectionHits[i];
                _detectionHits[i] = null;
                if (hit == null)
                    continue;

                PlayerHealth candidate = hit.GetComponentInParent<PlayerHealth>();
                if (!IsValidTargetCandidate(candidate))
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
        }

        if (closestTarget == null)
            return;

        SetTarget(closestTarget);
        EnterChase();
    }

    bool IsValidTargetCandidate(PlayerHealth candidate)
    {
        return candidate != null
            && !candidate.IsDead
            && !IsPlayerCarriedByJailor(candidate)
            && !IsPlayerRagdolled(candidate);
    }

    void SetTarget(PlayerHealth targetHealth)
    {
        _targetHealth = targetHealth;
        _target = targetHealth != null ? targetHealth.transform : null;
        if (_target != null)
        {
            _lastKnownTargetPosition = _target.position;
            _lastPerceivedTargetTime = Time.time;
        }
    }

    void ClearTarget()
    {
        _target = null;
        _targetHealth = null;
    }

    /// <summary>Can he still see (or hear) the current target right now? Refreshes last-known data when true.</summary>
    bool PerceiveCurrentTarget()
    {
        if (_targetHealth == null || _target == null)
            return false;

        float distance = Vector3.Distance(transform.position, _target.position);
        bool perceived =
            (distance <= detectionRadius * 1.25f && HasDetectionLineOfSight(_targetHealth))
            || (hearingRadius > 0f && distance <= hearingRadius && IsPlayerAudiblySprinting(_targetHealth));

        if (perceived)
        {
            _lastKnownTargetPosition = _target.position;
            _lastPerceivedTargetTime = Time.time;
        }

        return perceived;
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

    static bool IsPlayerRagdolled(PlayerHealth playerHealth)
    {
        if (playerHealth == null)
            return false;

        PlayerRagdollController ragdoll = playerHealth.GetComponent<PlayerRagdollController>();
        return ragdoll != null && (ragdoll.IsRagdolled || ragdoll.IsHeld || ragdoll.IsGettingUp);
    }

    bool HasDetectionLineOfSight(PlayerHealth targetHealth)
    {
        if (!requireDetectionLineOfSight)
            return true;

        return HasLineOfSightToTarget(targetHealth, detectionLineOfSightMask, detectionLineOfSightHeight, Vector3.zero);
    }

    bool IsWithinDetectionCone(Vector3 worldPoint)
    {
        if (detectionFovHalfAngleDegrees >= 179.5f)
            return true;

        Vector3 toPoint = worldPoint - transform.position;
        toPoint.y = 0f;
        if (toPoint.sqrMagnitude <= 0.7f * 0.7f)
            return true; // brushed right against him

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 1e-4f)
            return true;

        return Vector3.Angle(forward, toPoint) <= detectionFovHalfAngleDegrees;
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
            origin, toTarget / distanceToTarget, _lineOfSightHits, distanceToTarget, mask, QueryTriggerInteraction.Ignore);
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

    // ------------------------------------------------------------------
    // Patrol / investigate
    // ------------------------------------------------------------------

    void EnterPatrol()
    {
        _state = GuardState.Patrol;
        _hasPatrolDestination = false;
        _patrolNoProgressTimer = 0f;
    }

    Vector3 UpdatePatrol()
    {
        _intendedMoveSpeed = patrolSpeed;
        if (!TrySnapToNavMesh() || navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return Vector3.zero;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = patrolSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.2f, patrolArrivalDistance * 0.8f);

        if (!_hasPatrolDestination && !TryPickPatrolDestination())
            return Vector3.zero;

        if (!navMeshAgent.pathPending)
        {
            bool badPath = !navMeshAgent.hasPath
                || navMeshAgent.pathStatus == NavMeshPathStatus.PathInvalid
                || navMeshAgent.pathStatus == NavMeshPathStatus.PathPartial;
            if (badPath || navMeshAgent.remainingDistance <= patrolArrivalDistance)
            {
                _hasPatrolDestination = false;
                return Vector3.zero;
            }

            float remaining = navMeshAgent.remainingDistance;
            bool noProgress = navMeshAgent.velocity.magnitude < 0.05f
                && _patrolPreviousRemainingDistance - remaining < 0.05f;
            _patrolNoProgressTimer = noProgress ? _patrolNoProgressTimer + Time.deltaTime : 0f;
            _patrolPreviousRemainingDistance = remaining;
            if (_patrolNoProgressTimer >= patrolStuckSeconds)
            {
                _patrolNoProgressTimer = 0f;
                _hasPatrolDestination = false;
                return Vector3.zero;
            }
        }

        Vector3 patrolVelocity = ClampedDesiredVelocity(patrolSpeed);
        _intendedMoveSpeed = patrolVelocity.magnitude;
        return patrolVelocity;
    }

    bool TryPickPatrolDestination()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            Vector2 flat = Random.insideUnitCircle * patrolPointRadius;
            Vector3 candidate = transform.position + new Vector3(flat.x, 0f, flat.y);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 4f, NavMesh.AllAreas))
                continue;
            if (Vector3.Distance(hit.position, transform.position) < patrolArrivalDistance * 2f)
                continue;
            if (!navMeshAgent.SetDestination(hit.position))
                continue;

            _patrolDestination = hit.position;
            _hasPatrolDestination = true;
            _patrolNoProgressTimer = 0f;
            _patrolPreviousRemainingDistance = float.MaxValue;
            return true;
        }

        return false;
    }

    void EnterInvestigate(Vector3 point)
    {
        _state = GuardState.Investigate;
        _investigatePoint = point;
        _investigateDwellEndTime = -1f;
        _investigateAbortTime = Time.time + 15f; // unreachable point → give up and dwell where he is
        _nextInvestigateRepathTime = 0f;
        _hasPatrolDestination = false;
    }

    Vector3 UpdateInvestigate()
    {
        _intendedMoveSpeed = patrolSpeed;
        if (!TrySnapToNavMesh() || navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return Vector3.zero;

        // Walking the point down.
        if (_investigateDwellEndTime < 0f)
        {
            navMeshAgent.isStopped = false;
            navMeshAgent.speed = patrolSpeed;
            navMeshAgent.stoppingDistance = Mathf.Max(0.2f, investigateArrivalDistance * 0.8f);

            // Interval re-path only (see the chase-side comment: per-frame SetDestination stalls
            // any path that needs more than one frame to compute).
            if (!navMeshAgent.pathPending && Time.time >= _nextInvestigateRepathTime)
            {
                navMeshAgent.SetDestination(_investigatePoint);
                _nextInvestigateRepathTime = Time.time + 0.5f;
            }

            Vector3 flatToPoint = _investigatePoint - transform.position;
            flatToPoint.y = 0f;
            bool arrived = flatToPoint.magnitude <= investigateArrivalDistance
                || (!navMeshAgent.pathPending && navMeshAgent.hasPath && navMeshAgent.remainingDistance <= investigateArrivalDistance)
                || Time.time >= _investigateAbortTime;
            if (!arrived)
            {
                Vector3 approachVelocity = ClampedDesiredVelocity(patrolSpeed);
                _intendedMoveSpeed = approachVelocity.magnitude;
                return approachVelocity;
            }

            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
            _investigateDwellEndTime = Time.time + investigateDwellSeconds;
            _investigateScanNextTurnTime = 0f;
            return Vector3.zero;
        }

        // Standing at the point, scanning left/right.
        _intendedMoveSpeed = 0f;
        if (Time.time >= _investigateScanNextTurnTime)
        {
            _investigateScanNextTurnTime = Time.time + Random.Range(0.9f, 1.5f);
            float yaw = transform.eulerAngles.y + Random.Range(70f, 150f) * (Random.value < 0.5f ? -1f : 1f);
            _investigateScanRotation = Quaternion.Euler(0f, yaw, 0f);
        }

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, _investigateScanRotation, rotationSpeed * 0.4f * Time.deltaTime);

        if (Time.time >= _investigateDwellEndTime)
            EnterPatrol();

        return Vector3.zero;
    }

    // ------------------------------------------------------------------
    // Chase
    // ------------------------------------------------------------------

    void EnterChase()
    {
        if (_state == GuardState.Attack && _attackRoutine != null)
            return;

        _state = GuardState.Chase;
        _chaseStallTimer = 0f;
        _nextChaseRepathTime = 0f; // repath immediately on the fresh chase
    }

    Vector3 UpdateChase()
    {
        if (_targetHealth == null || _target == null)
        {
            EnterPatrol();
            return Vector3.zero;
        }

        bool perceived = PerceiveCurrentTarget();
        if (!perceived && Time.time - _lastPerceivedTargetTime > targetMemorySeconds)
        {
            Vector3 lastKnown = _lastKnownTargetPosition;
            ClearTarget();
            _engagedWalk = false;
            EnterInvestigate(lastKnown);
            return Vector3.zero;
        }

        float distanceToTarget = Vector3.Distance(transform.position, _target.position);

        // Sprint/stalk hysteresis: once he's on top of you the chase drops to a steady walking
        // advance; he only breaks back into the run after you open a real gap OR you commit to a
        // sprint escape — no ambling after a fleeing player. Two thresholds so the walk/run choice
        // can't flicker at a single boundary, and a momentary line-of-sight flicker does NOT reset it.
        bool targetFleeingFast = IsTargetMovingAway(pressureAdvanceSpeed + 0.5f);
        if (targetFleeingFast)
            _engagedWalk = false;
        else if (distanceToTarget <= engagedWalkEnterDistance)
            _engagedWalk = true;
        else if (distanceToTarget >= engagedWalkExitDistance)
            _engagedWalk = false;

        // A ragdolled target is kept, not attacked: he walks up and looms at arm's length until
        // they get back on their feet, then the fight resumes. He never wanders off a downed prey.
        bool targetDown = IsPlayerRagdolled(_targetHealth);

        // In range and off cooldown → attack. On cooldown he falls through to the pressure advance
        // below and keeps crowding instead of standing still.
        float attackStartDistance = meleeRange + Mathf.Max(0f, attackStartDistancePadding);
        if (perceived && !targetDown && distanceToTarget <= attackStartDistance)
        {
            FaceTarget();
            if (Time.time >= _nextAttackTime && _attackRoutine == null)
            {
                StartAttack(ChooseEngagedAttack(distanceToTarget));
                return Vector3.zero;
            }
        }
        // Hurricane gap-closer: target in the band with a clear path, and either visibly pulling
        // away or repeatedly slipping out of his reach.
        else if (perceived
            && !targetDown
            && _attackRoutine == null
            && Time.time >= _nextAttackTime
            && Time.time >= _nextHurricaneTime
            && distanceToTarget >= hurricaneMinRange
            && distanceToTarget <= hurricaneMaxRange
            && (IsTargetMovingAway() || _consecutiveWhiffs >= whiffsBeforeHurricanePunish)
            && IsHurricanePathClear())
        {
            StartAttack(GuardAttack.HurricaneKick);
            return Vector3.zero;
        }

        if (targetDown)
        {
            // One beat of eye contact when they rise before the first swing comes — pushed out
            // continuously while they're down, so it expires 0.6s after they're back up.
            _nextAttackTime = Mathf.Max(_nextAttackTime, Time.time + 0.6f);
        }

        float moveSpeed = CurrentModeSpeed;

        if (!TrySnapToNavMesh() || navMeshAgent == null || !navMeshAgent.isOnNavMesh)
        {
            _intendedMoveSpeed = 0f;
            return Vector3.zero;
        }

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = moveSpeed;
        // While stalking he crowds all the way to point-blank; at sprint he brakes at swing range;
        // over a downed target he holds at arm's length, waiting.
        navMeshAgent.stoppingDistance = targetDown
            ? Mathf.Max(0.1f, meleeRange)
            : _engagedWalk ? 0.9f : Mathf.Max(0.1f, meleeRange * 0.75f);

        Vector3 destination = perceived ? _target.position : _lastKnownTargetPosition;
        if (NavMesh.SamplePosition(destination, out NavMeshHit navHit, targetNavMeshSampleRadius, NavMesh.AllAreas))
            destination = navHit.position;

        // Re-path on an interval and never while a path is still computing — issuing SetDestination
        // every frame cancels the async computation each time, which stalls him on any route long
        // enough to need more than one frame (exactly the after-corner chases that matter most).
        if (!navMeshAgent.pathPending
            && (Time.time >= _nextChaseRepathTime
                || (destination - _issuedChaseDestination).sqrMagnitude > 4f))
        {
            navMeshAgent.SetDestination(destination);
            _issuedChaseDestination = destination;
            _nextChaseRepathTime = Time.time + 0.15f;
        }

        // Intended speed reflects what he's actually doing, so the animator can settle into the
        // fight-stance idle when the advance has fully closed instead of walking in place.
        Vector3 chaseVelocity = ClampedDesiredVelocity(moveSpeed);
        _intendedMoveSpeed = chaseVelocity.magnitude;

        // Face the body he's looming over so the wait reads as watching, not standing around.
        if (targetDown && chaseVelocity.sqrMagnitude < 0.01f)
            FaceTarget();

        // Stall watchdog: if the mesh can't actually route to the target (partial path — player on
        // an unreachable spot), the agent parks at the path's dead end with zero desired velocity.
        // Never freeze there: fall back through Investigate so he prowls, scans, and re-acquires
        // instead of standing catatonic mid-chase. Looming over a downed target is legitimate
        // stillness, not a stall.
        if (chaseVelocity.sqrMagnitude < 0.01f && !targetDown && distanceToTarget > attackStartDistance + 0.5f)
        {
            _chaseStallTimer += Time.deltaTime;
            if (_chaseStallTimer >= 1.5f)
            {
                _chaseStallTimer = 0f;
                Vector3 lastKnown = _lastKnownTargetPosition;
                ClearTarget();
                _engagedWalk = false;
                EnterInvestigate(lastKnown);
                return Vector3.zero;
            }
        }
        else
            _chaseStallTimer = 0f;

        return chaseVelocity;
    }

    bool IsTargetMovingAway(float minimumSpeed = 1.5f)
    {
        if (_target == null)
            return false;

        CharacterController targetCc = _target.GetComponent<CharacterController>();
        if (targetCc == null || !targetCc.enabled)
            return false;

        Vector3 away = _target.position - transform.position;
        away.y = 0f;
        if (away.sqrMagnitude < 1e-4f)
            return false;

        Vector3 targetVelocity = targetCc.velocity;
        targetVelocity.y = 0f;
        return targetVelocity.magnitude > minimumSpeed && Vector3.Dot(targetVelocity.normalized, away.normalized) > 0.35f;
    }

    // ------------------------------------------------------------------
    // Attacks
    // ------------------------------------------------------------------

    /// <summary>
    /// Context-aware pick instead of a blind roll: whiff streaks get answered with the hurricane
    /// lunge, crowding gets booted away, a landed jab chains into the flurry, and only the
    /// remainder falls back to weighted variety.
    /// </summary>
    GuardAttack ChooseEngagedAttack(float distanceToTarget)
    {
        bool mmaReady = Time.time >= _nextMmaKickTime;
        bool hurricaneReady = Time.time >= _nextHurricaneTime;

        // You keep slipping out of reach — stop trading jabs and lunge. Needs the same real gap the
        // chase-side gap-closer does, so a target still standing in his face gets kicked, not launched.
        if (_consecutiveWhiffs >= whiffsBeforeHurricanePunish
            && hurricaneReady
            && distanceToTarget >= hurricaneMinRange
            && IsHurricanePathClear())
            return GuardAttack.HurricaneKick;

        // Point-blank crowding gets kicked off.
        if (distanceToTarget <= crowdedKickDistance && mmaReady)
            return GuardAttack.MmaKick;

        // A jab that just landed chains into the flurry while you're still reeling.
        if (_lastAttack == GuardAttack.Punch
            && _lastAttackLanded
            && Time.time - _lastAttackEndTime <= comboFollowUpSeconds
            && Random.value <= comboFollowUpChance)
            return GuardAttack.QuadPunch;

        float roll = Random.value;
        if (mmaReady && roll < mmaKickChance)
            return GuardAttack.MmaKick;
        if (roll < mmaKickChance + quadPunchChance)
            return GuardAttack.QuadPunch;
        return GuardAttack.Punch;
    }

    void StartAttack(GuardAttack kind)
    {
        if (_attackRoutine != null)
        {
            StopCoroutine(_attackRoutine);
            _attackRoutine = null;
        }

        _attackRoutine = StartCoroutine(AttackRoutine(kind));
    }

    IEnumerator AttackRoutine(GuardAttack kind)
    {
        _state = GuardState.Attack;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        _horizontalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;
        EndAttackDrive();

        FaceTarget();

        string stateName;
        switch (kind)
        {
            case GuardAttack.QuadPunch: stateName = "QuadPunch"; break;
            case GuardAttack.MmaKick: stateName = "MmaKick"; break;
            case GuardAttack.HurricaneKick: stateName = "HurricaneKick"; break;
            default: stateName = "Punch"; break;
        }

        // Punches ride the masked upper-body layer so the legs keep striding under them; the kicks
        // are full-body moves and stay on the base layer.
        _currentAttackIsMasked = upperBodyLayerIndex > 0
            && (kind == GuardAttack.Punch || kind == GuardAttack.QuadPunch);

        if (animator != null)
        {
            animator.CrossFadeInFixedTime(
                stateName, attackCrossfadeDuration, _currentAttackIsMasked ? upperBodyLayerIndex : 0, 0f);
        }

        // Every attack steps in behind its swing (UpdateAttack drives the capsule along his facing,
        // tracking the target at a capped turn rate until the commit point). Hits are validated
        // against his forward at impact, so a hard side-step during the commit window still dodges —
        // plain backpedaling no longer does.
        bool landedAny = false;
        float recovery;

        switch (kind)
        {
            case GuardAttack.QuadPunch:
            {
                // One damage check per visible punch. He keeps stepping in through the whole flurry,
                // so a target that only walks backwards still eats most of it — but sprinting away
                // or slipping aside drops every remaining hook.
                int hookCount = quadPunchHitTimes != null ? quadPunchHitTimes.Length : 0;
                _attackDriveSpeed = quadPunchDriveSpeed;
                _attackDriveUntilTime = Time.time + (hookCount > 0 ? quadPunchHitTimes[hookCount - 1] : 0f);

                float elapsed = 0f;
                for (int hook = 0; hook < hookCount; hook++)
                {
                    float wait = Mathf.Max(0f, quadPunchHitTimes[hook] - elapsed);

                    // Track for the first half of the gap, then lock. The hooks are only ~0.24s
                    // apart, so the full commit lead would swallow the gap entirely and leave him
                    // unable to follow the target at all; halving it keeps each punch individually
                    // dodgeable while still letting him track someone drifting sideways.
                    float lead = Mathf.Min(attackCommitLeadSeconds, wait * 0.5f);
                    _attackTrackUntilTime = Time.time + Mathf.Max(0f, wait - lead);

                    if (wait > 0f)
                        yield return new WaitForSeconds(wait);

                    elapsed = quadPunchHitTimes[hook];
                    landedAny |= TryLandMeleeTick(quadPunchTickDamage, transform.forward);
                }

                EndAttackDrive();
                recovery = quadPunchRecovery;
                break;
            }
            case GuardAttack.MmaKick:
            {
                _nextMmaKickTime = Time.time + mmaKickCooldownSeconds;
                _attackTrackUntilTime = Time.time + Mathf.Max(0f, mmaKickHitDelay - attackCommitLeadSeconds);
                _attackDriveSpeed = mmaKickDriveSpeed;
                _attackDriveUntilTime = Time.time + mmaKickHitDelay;

                yield return new WaitForSeconds(mmaKickHitDelay);
                Vector3 kickDirection = transform.forward;
                if (TryLandMeleeTick(mmaKickDamage, kickDirection))
                {
                    landedAny = true;
                    ApplyMmaKickShove(kickDirection);
                }
                EndAttackDrive();
                recovery = mmaKickRecovery;
                break;
            }
            case GuardAttack.HurricaneKick:
            {
                _nextHurricaneTime = Time.time + hurricaneCooldownSeconds;
                BeginHurricaneCapsule();
                _attackTrackUntilTime = Time.time + Mathf.Max(0f, hurricaneHitDelay - attackCommitLeadSeconds);

                float lungeStart = Mathf.Min(hurricaneLungeStartTime, hurricaneHitDelay);
                yield return new WaitForSeconds(lungeStart);
                _attackDriveSpeed = hurricaneLungeSpeed;
                _attackDriveIsHurricane = true;
                _attackDriveUntilTime = Time.time + Mathf.Max(0.05f, hurricaneLungeEndTime - lungeStart);

                float untilHit = Mathf.Max(0f, hurricaneHitDelay - lungeStart);
                yield return new WaitForSeconds(untilHit);
                bool landed = TryLandHurricaneHit(transform.forward);
                landedAny = landed;

                float lungeRemaining = Mathf.Max(0f, hurricaneLungeEndTime - hurricaneHitDelay);
                if (lungeRemaining > 0f && !landed)
                    yield return new WaitForSeconds(lungeRemaining);
                EndAttackDrive();
                EndHurricaneCapsule();
                recovery = hurricaneRecovery;
                // A landed launch is NOT the end of the hunt — recovery runs, then the normal
                // finish keeps the target and he walks up to loom over the ragdoll (UpdateChase).
                break;
            }
            default:
            {
                _attackTrackUntilTime = Time.time + Mathf.Max(0f, punchHitDelay - attackCommitLeadSeconds);
                _attackDriveSpeed = punchDriveSpeed;
                _attackDriveUntilTime = Time.time + punchHitDelay;

                yield return new WaitForSeconds(punchHitDelay);
                landedAny = TryLandMeleeTick(punchDamage, transform.forward);
                EndAttackDrive();
                recovery = punchRecovery;
                break;
            }
        }

        yield return new WaitForSeconds(recovery);
        RecordAttackResult(kind, landedAny);
        FinishAttack();
    }

    void RecordAttackResult(GuardAttack kind, bool landedAny)
    {
        _lastAttack = kind;
        _lastAttackEndTime = Time.time;
        _lastAttackLanded = landedAny;
        if (landedAny)
            _consecutiveWhiffs = 0;
        else
            _consecutiveWhiffs++;
    }

    void EndAttackDrive()
    {
        _attackDriveSpeed = 0f;
        _attackDriveUntilTime = 0f;
        _attackDriveIsHurricane = false;
    }

    void BeginHurricaneCapsule()
    {
        if (characterController == null || !characterController.enabled)
            return;
        if (_restoreCapsuleRadius < 0f)
            _restoreCapsuleRadius = characterController.radius;
        characterController.radius = Mathf.Max(_restoreCapsuleRadius, hurricaneCapsuleRadius);
    }

    void EndHurricaneCapsule()
    {
        if (characterController != null && _restoreCapsuleRadius > 0f)
            characterController.radius = _restoreCapsuleRadius;
        _restoreCapsuleRadius = -1f;
    }

    void FinishAttack()
    {
        EndAttackDrive();
        EndHurricaneCapsule();
        _nextAttackTime = Time.time + attackCooldownSeconds;
        _attackRoutine = null;

        if (animator != null)
        {
            // A masked punch only releases its own layer — yanking the base layer to Idle here
            // would stop the legs mid-stride, which is exactly the slide this layering removes.
            if (_currentAttackIsMasked)
                animator.CrossFadeInFixedTime("Empty", attackExitCrossfadeDuration, upperBodyLayerIndex, 0f);
            else
                animator.CrossFadeInFixedTime("Idle", attackExitCrossfadeDuration, 0, 0f);
        }
        _currentAttackIsMasked = false;

        if (_state != GuardState.Attack)
            return;

        // Ragdolled targets are deliberately kept: the chase loop walks up and waits over them.
        if (_targetHealth != null && !_targetHealth.IsDead && !IsPlayerCarriedByJailor(_targetHealth))
            EnterChase();
        else
        {
            ClearTarget();
            EnterPatrol();
        }
    }

    Vector3 UpdateAttack()
    {
        if (_attackRoutine == null)
        {
            // Routine was interrupted externally (stagger cancelled it); fall back to chase/patrol.
            if (_targetHealth != null && !_targetHealth.IsDead)
                EnterChase();
            else
                EnterPatrol();
            return Vector3.zero;
        }

        // Wind-up tracking at a capped turn rate until the commit point; after that his line is
        // locked. The cap is what keeps a hard side-step alive as a dodge.
        if (Time.time < _attackTrackUntilTime && _target != null)
            RotateTowards(_target.position, attackTrackingTurnSpeed);

        bool driving = _attackDriveSpeed > 0.01f && Time.time < _attackDriveUntilTime;
        if (!driving)
        {
            _intendedMoveSpeed = 0f;
            return Vector3.zero;
        }

        // The step-in halts just short of the target so he strikes at reach instead of shoving
        // through them.
        if (!_attackDriveIsHurricane && _target != null)
        {
            Vector3 gap = _target.position - transform.position;
            gap.y = 0f;
            if (gap.magnitude <= attackDriveMinGap)
            {
                _intendedMoveSpeed = 0f;
                return Vector3.zero;
            }
        }

        // Hurricane wall safety: kill the lunge before the spin reaches a wall.
        if (_attackDriveIsHurricane && IsWallAhead(hurricaneWallAbortDistance))
        {
            EndAttackDrive();
            _intendedMoveSpeed = 0f;
            return Vector3.zero;
        }

        _intendedMoveSpeed = _attackDriveSpeed;
        Vector3 driveForward = transform.forward;
        driveForward.y = 0f;
        return driveForward.normalized * _attackDriveSpeed;
    }

    bool TryLandMeleeTick(float damage, Vector3 committedDirection)
    {
        if (!CanLandCommittedAttack(_targetHealth, committedDirection, meleeRange + attackHitRangePadding))
            return false;

        _targetHealth.TakeDamage(damage); // victim feedback comes from the universal hurt-feedback watcher
        return true;
    }

    void ApplyMmaKickShove(Vector3 committedDirection)
    {
        if (_targetHealth == null)
            return;

        Vector3 pushDir = _targetHealth.transform.position - transform.position;
        pushDir.y = 0f;
        pushDir = pushDir.sqrMagnitude > 1e-4f ? pushDir.normalized : committedDirection;
        Vector3 pushVelocity = pushDir * mmaKickPushSpeed;

        bool inNetSession = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        NetworkObject playerNetworkObject = _targetHealth.GetComponent<NetworkObject>();
        if (inNetSession && _networkAvatar != null && playerNetworkObject != null)
        {
            // The player's CharacterController is owner-authoritative — relay the shove to their owner.
            _networkAvatar.ServerRelayPush(playerNetworkObject, pushVelocity, mmaKickPushUpwardSpeed, mmaKickPushControlLockSeconds);
        }
        else
        {
            _targetHealth.GetComponent<PlayerController>()?.ApplyExternalPush(
                pushVelocity, mmaKickPushUpwardSpeed, mmaKickPushControlLockSeconds);
        }
    }

    bool TryLandHurricaneHit(Vector3 committedDirection)
    {
        float reach = meleeRange + attackHitRangePadding + 0.6f; // the spin sweeps wider than a jab
        if (!CanLandCommittedAttack(_targetHealth, committedDirection, reach))
            return false;

        Vector3 knockDir = _targetHealth.transform.position - transform.position;
        knockDir.y = 0f;
        knockDir = knockDir.sqrMagnitude > 1e-4f ? knockDir.normalized : committedDirection;

        Vector3 hitPoint = _targetHealth.transform.position + Vector3.up;

        // In tight hallways redirect the launch toward open space so the ragdoll flies down the hall,
        // not into the adjacent wall (same trick as the Clown's hammer).
        float forwardFactor = 1f;
        if (knockbackAvoidWalls)
        {
            knockDir = ResolveOpenKnockbackDirection(hitPoint, knockDir, out float clearance);
            forwardFactor = Mathf.Clamp01(clearance / Mathf.Max(0.5f, knockbackForwardFullSpeedClearance));
        }

        Vector3 force = (knockDir * hurricaneKnockbackForwardSpeed * forwardFactor)
            + (Vector3.up * hurricaneKnockbackUpwardSpeed);

        // Keep his capsule from depenetrating against the freshly-enabled ragdoll bone colliders.
        IgnoreCollisionsWithVictim(_targetHealth);

        bool inNetSession = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        NetworkPlayerRagdoll netRagdoll = _targetHealth.GetComponent<NetworkPlayerRagdoll>();
        if (inNetSession && netRagdoll != null)
        {
            netRagdoll.RequestTrapHitFromServer(force, hitPoint, hurricaneDamage, hurricaneKnockbackForceMode);
            return true;
        }

        PlayerRagdollController ragdoll = _targetHealth.GetComponent<PlayerRagdollController>();
        if (ragdoll == null)
        {
            _targetHealth.TakeDamage(hurricaneDamage);
            return true;
        }

        bool survived = true;
        if (hurricaneDamage > 0f)
        {
            _targetHealth.TakeDamage(hurricaneDamage);
            survived = !_targetHealth.IsDead;
        }
        ragdoll.ActivateRagdoll(force, hitPoint, hurricaneKnockbackForceMode, allowAutoRecovery: survived);
        return true;
    }

    void IgnoreCollisionsWithVictim(PlayerHealth victim)
    {
        if (victim == null || characterController == null)
            return;

        Collider[] victimColliders = victim.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < victimColliders.Length; i++)
        {
            Collider c = victimColliders[i];
            if (c != null && c != characterController)
                Physics.IgnoreCollision(characterController, c, true);
        }
    }

    bool CanLandCommittedAttack(PlayerHealth targetHealth, Vector3 committedDirection, float reach)
    {
        if (targetHealth == null || targetHealth.IsDead)
            return false;
        if (IsPlayerRagdolled(targetHealth))
            return false; // already down — he doesn't hit downed players

        Vector3 toTarget = targetHealth.transform.position - transform.position;
        Vector3 horizontalToTarget = toTarget;
        horizontalToTarget.y = 0f;
        float horizontalDistance = horizontalToTarget.magnitude;
        if (horizontalDistance > reach)
            return false;

        if (horizontalDistance > 0.001f)
        {
            float attackAngle = Vector3.Angle(committedDirection, horizontalToTarget / horizontalDistance);
            if (attackAngle > attackHitHalfAngle)
                return false;
        }

        if (requireAttackLineOfSight
            && !HasLineOfSightToTarget(targetHealth, attackLineOfSightMask, attackLineOfSightHeight, committedDirection * 0.15f))
            return false;

        return true;
    }

    /// <summary>Clown-style clearance sweep: nearest open horizontal direction to the intended launch.</summary>
    Vector3 ResolveOpenKnockbackDirection(Vector3 hitPoint, Vector3 intendedDir, out float chosenClearance)
    {
        intendedDir.y = 0f;
        if (intendedDir.sqrMagnitude < 1e-4f)
        {
            Vector3 f = transform.forward;
            f.y = 0f;
            intendedDir = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        }
        else
            intendedDir.Normalize();

        int mask = knockbackObstacleMask.value != 0
            ? knockbackObstacleMask.value
            : MaskExcludingActors(Physics.DefaultRaycastLayers);
        Vector3 origin = new Vector3(hitPoint.x, hitPoint.y + knockbackProbeHeight, hitPoint.z);

        float intendedClear = MeasureClearance(origin, intendedDir, mask);
        if (intendedClear >= knockbackMinClearance)
        {
            chosenClearance = intendedClear;
            return intendedDir;
        }

        const int samples = 16;
        Vector3 bestDir = intendedDir;
        float bestClear = intendedClear;
        float bestAlign = float.NegativeInfinity;
        bool anyOpen = false;
        for (int i = 0; i < samples; i++)
        {
            float angle = 360f * i / samples;
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            float clear = MeasureClearance(origin, dir, mask);
            float align = Vector3.Dot(dir, intendedDir);
            if (clear >= knockbackMinClearance)
            {
                if (!anyOpen || align > bestAlign)
                {
                    anyOpen = true;
                    bestAlign = align;
                    bestDir = dir;
                    bestClear = clear;
                }
            }
            else if (!anyOpen && clear > bestClear)
            {
                bestDir = dir;
                bestClear = clear;
            }
        }

        chosenClearance = bestClear;
        return bestDir;
    }

    float MeasureClearance(Vector3 origin, Vector3 dir, int mask)
    {
        const float probeDistance = 12f;
        if (Physics.Raycast(origin, dir, out RaycastHit hit, probeDistance, mask, QueryTriggerInteraction.Ignore))
            return hit.distance;
        return probeDistance;
    }

    // ------------------------------------------------------------------
    // Poise / counters (the player-facing "combat system")
    // ------------------------------------------------------------------

    /// <summary>
    /// Player melee entry point (called from <c>PlayerController.ApplyMeleeDamageLocally</c>, which runs on the
    /// server in online sessions). Punches never damage him — they chip poise and roll for a counter:
    ///   • mid-attack — hyper-armor: poise chips but the committed move keeps coming,
    ///   • poise break — the ONE full-body stagger (your escape window), then a stagger-immunity window,
    ///   • otherwise — a per-hit chance he answers instantly with a kick.
    /// Returns true when the hit registered (drives the puncher's impact SFX + camera kick).
    /// </summary>
    public bool TakeMeleeHit(Transform attacker, PlayerHealth attackerHealth)
    {
        if (_networkObject != null
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer)
            return false;

        // Getting punched always gets his attention (even from behind, even mid-patrol).
        if (attackerHealth != null && !attackerHealth.IsDead && !IsPlayerCarriedByJailor(attackerHealth))
        {
            SetTarget(attackerHealth);
            if (_state == GuardState.Patrol || _state == GuardState.Investigate)
                EnterChase();
        }

        _poiseRegenBlockedUntil = Time.time + poiseRegenDelay;

        // Hyper-armor: a committed attack cannot be interrupted; poise floors at 1 so the break can't fire mid-move.
        if (_attackRoutine != null)
        {
            _poise = Mathf.Max(1f, _poise - punchPoiseDamage);
            return true;
        }

        if (_state == GuardState.Stagger)
            return true; // already down — free hits, but no re-stagger while he's recovering

        bool staggerImmune = Time.time < _staggerImmuneUntil;
        if (!staggerImmune)
        {
            _poise -= punchPoiseDamage;
            if (_poise <= 0f)
            {
                BeginPoiseBreakStagger();
                return true;
            }
        }

        TryStartCounterAttack(staggerImmune);
        return true;
    }

    void BeginPoiseBreakStagger()
    {
        if (_attackRoutine != null)
        {
            StopCoroutine(_attackRoutine);
            _attackRoutine = null;
        }

        _poise = maxPoise;
        _state = GuardState.Stagger;
        _staggerEndTime = Time.time + poiseBreakStaggerSeconds;
        _staggerImmuneUntil = _staggerEndTime + staggerImmunitySeconds;
        _nextAttackTime = _staggerEndTime + attackCooldownSeconds * 0.5f;
        EndAttackDrive();
        EndHurricaneCapsule();

        // The stagger is a full-body reaction: drop any punch still playing on the masked layer,
        // or it would keep driving the arms over the recoil.
        if (animator != null && upperBodyLayerIndex > 0 && upperBodyLayerIndex < animator.layerCount)
        {
            animator.Play("Empty", upperBodyLayerIndex, 0f);
            animator.SetLayerWeight(upperBodyLayerIndex, 0f);
        }
        _currentAttackIsMasked = false;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        _horizontalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;

        if (animator != null)
            animator.CrossFadeInFixedTime(staggerStateName, 0.1f, 0, 0f);
    }

    void UpdateStagger()
    {
        _intendedMoveSpeed = 0f;
        if (Time.time < _staggerEndTime)
            return;

        if (animator != null)
            animator.CrossFadeInFixedTime("Idle", 0.2f, 0, 0f);

        if (_targetHealth != null && !_targetHealth.IsDead && !IsPlayerCarriedByJailor(_targetHealth))
            EnterChase();
        else
        {
            ClearTarget();
            EnterPatrol();
        }
    }

    void TryStartCounterAttack(bool staggerImmune)
    {
        if (Time.time < _nextCounterRollTime)
            return;
        if (_targetHealth == null || _targetHealth.IsDead || _target == null)
            return;
        if (IsPlayerRagdolled(_targetHealth))
            return;

        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        if (distance > hurricaneMaxRange)
            return;

        float chance = staggerImmune ? counterChanceWhileImmune : counterChance;
        if (Random.value > chance)
            return;

        _nextCounterRollTime = Time.time + counterCooldownSeconds;

        // Within arm's reach he answers with the shove kick.
        bool inKickReach = distance <= meleeRange + attackHitRangePadding;
        if (inKickReach)
        {
            StartAttack(GuardAttack.MmaKick);
            return;
        }

        // Out of reach the only answer is the hurricane lunge — and it must honour its own
        // cooldown like every other use. (It previously bypassed the cooldown here, which let a
        // player trading punches at range pull the lunge every counter roll: by far the biggest
        // source of hurricane spam.) On cooldown or facing a wall he simply eats the hit.
        if (Time.time < _nextHurricaneTime || !IsHurricanePathClear())
            return;

        StartAttack(GuardAttack.HurricaneKick);
    }

    // ------------------------------------------------------------------
    // Movement / animator plumbing (mirrors ZombieAI)
    // ------------------------------------------------------------------

    void ApplyMovement(Vector3 desiredHorizontalVelocity)
    {
        if (characterController == null || !characterController.enabled)
            return;

        bool grounded = characterController.isGrounded;
        if (grounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -groundedStickDown;

        bool frozen = _state == GuardState.Stagger || IsAnimatorInState(0, staggerStateName);
        _horizontalVelocity = frozen ? Vector3.zero : desiredHorizontalVelocity;
        _verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 motion = _horizontalVelocity * Time.deltaTime;
        motion.y = _verticalVelocity.y * Time.deltaTime;
        characterController.Move(motion);

        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.nextPosition = transform.position;

        Vector3 horizontalDirection = _horizontalVelocity;
        horizontalDirection.y = 0f;
        if (horizontalDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(horizontalDirection.normalized);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }
    }

    void FaceTarget()
    {
        if (_target == null)
            return;

        RotateTowards(_target.position, rotationSpeed);
    }

    void RotateTowards(Vector3 worldPoint, float degreesPerSecond)
    {
        Vector3 lookDirection = worldPoint - transform.position;
        lookDirection.y = 0f;
        if (lookDirection.sqrMagnitude < 0.0001f)
            return;

        Quaternion targetRotation = Quaternion.LookRotation(lookDirection.normalized);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, targetRotation, degreesPerSecond * Time.deltaTime);
    }

    /// <summary>Wall probe straight ahead at chest height — used to abort the hurricane lunge in time.</summary>
    bool IsWallAhead(float distance)
    {
        int mask = MaskExcludingActors(attackLineOfSightMask);
        if (mask == 0)
            return false;

        Vector3 origin = transform.position + Vector3.up * 1.1f;
        return Physics.SphereCast(origin, hurricaneClearanceRadius, transform.forward, out _, distance, mask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>The lunge is only ever chosen when the corridor toward the target is actually open.</summary>
    bool IsHurricanePathClear()
    {
        if (_target == null)
            return false;

        Vector3 toTarget = _target.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        if (distance < 0.5f)
            return true;

        int mask = MaskExcludingActors(attackLineOfSightMask);
        if (mask == 0)
            return true;

        Vector3 origin = transform.position + Vector3.up * 1.1f;
        return !Physics.SphereCast(origin, hurricaneClearanceRadius, toTarget / distance, out _, distance * 0.85f, mask, QueryTriggerInteraction.Ignore);
    }

    void UpdateAnimatorParameters()
    {
        if (animator == null)
            return;

        // While a masked punch plays, the legs are driven by the attack's step-in rather than by the
        // chase mode — otherwise they'd fall back to a patrol-speed reference (or Idle) and skate
        // under a body that is visibly moving.
        bool inMaskedAttack = _state == GuardState.Attack && _currentAttackIsMasked;
        float referenceSpeed;
        bool running;
        if (inMaskedAttack)
        {
            running = _intendedMoveSpeed >= attackDriveRunThreshold;
            referenceSpeed = running ? chaseSpeed : pressureAdvanceSpeed;
        }
        else
        {
            referenceSpeed = CurrentModeSpeed;
            running = _state == GuardState.Chase && !_engagedWalk;
        }

        float normalizedSpeed = referenceSpeed > 0.001f ? Mathf.Clamp01(_intendedMoveSpeed / referenceSpeed) : 0f;
        animator.SetFloat(speedParameter, normalizedSpeed);
        animator.SetBool(runningParameter, running);

        // Stride rate from ACTUAL movement, divided by the ground speed the ACTIVE clip was
        // authored for. Per-clip (not per-mode) is what matters: the walk cycle is shared by the
        // patrol and the faster stalking advance, and each needs its own cadence to keep contact.
        // Rounding a corner or yielding in local avoidance slows the legs too. Replicates as a
        // normal animator float, so observers get the identical cadence.
        if (!string.IsNullOrEmpty(strideRateParameter))
        {
            float clipGroundSpeed = running ? runClipGroundSpeed : walkClipGroundSpeed;
            float actualSpeed = _horizontalVelocity.magnitude;
            float rate = clipGroundSpeed > 0.001f
                ? Mathf.Clamp(actualSpeed / clipGroundSpeed, minStrideRate, maxStrideRate)
                : 1f;
            animator.SetFloat(strideRateParameter, rate);
        }
    }

    Vector3 ClampedDesiredVelocity(float maxSpeed)
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || !navMeshAgent.isOnNavMesh)
            return Vector3.zero;

        Vector3 desiredVelocity = navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        if (desiredVelocity.sqrMagnitude > maxSpeed * maxSpeed)
            desiredVelocity = desiredVelocity.normalized * maxSpeed;
        return desiredVelocity;
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

    void ApplyAgentSettings()
    {
        if (navMeshAgent == null)
            return;

        navMeshAgent.enabled = true;
        navMeshAgent.speed = patrolSpeed;
        navMeshAgent.angularSpeed = rotationSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.1f, meleeRange * 0.75f);
        navMeshAgent.acceleration = Mathf.Max(navMeshAgent.acceleration, chaseSpeed * 4f);
        navMeshAgent.updatePosition = false;
        navMeshAgent.updateRotation = false;
        navMeshAgent.baseOffset = 0f;
        // The hunter has right of way in local avoidance (same tier as the Jailor; zombies are 48).
        navMeshAgent.avoidancePriority = 12;

        if (characterController != null)
        {
            characterController.skinWidth = 0.02f;
            characterController.minMoveDistance = 0.001f;
        }
    }

    bool IsAnimatorInState(int layer, string stateName)
    {
        if (animator == null)
            return false;
        int hash = Animator.StringToHash(stateName);
        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
        if (current.shortNameHash == hash)
            return true;
        if (animator.IsInTransition(layer))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
            if (next.shortNameHash == hash)
                return true;
        }
        return false;
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

    void CacheReferences()
    {
        if (animator == null)
            animator = GetComponent<Animator>();

        if (navMeshAgent == null)
            navMeshAgent = GetComponent<NavMeshAgent>();

        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (footstepAudioSource == null)
            footstepAudioSource = GetOrCreateChildAudioSource(FootstepAudioChildName, allowCreate: false);

        if (fxAudioSource == null)
            fxAudioSource = GetOrCreateChildAudioSource(FxAudioChildName, allowCreate: false);

        // Bone lookups are name-based and tolerate both the Mixamo and the bodyguard rigs.
        if (_hipsBone == null || _leftToe == null || _rightToe == null)
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                string n = all[i].name;
                if (_hipsBone == null && (n == "Hips" || n == "mixamorig:Hips"))
                    _hipsBone = all[i];
                else if (_leftToe == null && (n == "LeftToeBase" || n == "mixamorig:LeftToeBase"))
                    _leftToe = all[i];
                else if (_rightToe == null && (n == "RightToeBase" || n == "mixamorig:RightToeBase"))
                    _rightToe = all[i];
            }

            // Fall back to the ankles if the rig has no toe bones.
            for (int i = 0; i < all.Length && (_leftToe == null || _rightToe == null); i++)
            {
                string n = all[i].name;
                if (_leftToe == null && (n == "LeftFoot" || n == "mixamorig:LeftFoot")) _leftToe = all[i];
                if (_rightToe == null && (n == "RightFoot" || n == "mixamorig:RightFoot")) _rightToe = all[i];
            }
        }
    }

    // ------------------------------------------------------------------
    // Audio
    // ------------------------------------------------------------------

    /// <summary>
    /// Plays the attack whoosh the frame the animator enters any attack state. The animator replicates
    /// (ServerNetworkAnimator), so this single watcher gives identical audio on host and clients.
    /// </summary>
    void UpdateAttackWhooshWatcher()
    {
        if (animator == null || fxAudioSource == null || attackWhooshClip == null)
            return;

        // Watch both layers: the kicks live on the base layer, the punches on the masked one.
        int hash = animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
        if (upperBodyLayerIndex > 0 && upperBodyLayerIndex < animator.layerCount)
        {
            int upperHash = animator.GetCurrentAnimatorStateInfo(upperBodyLayerIndex).shortNameHash;
            if (upperHash != Animator.StringToHash("Empty"))
                hash = upperHash;
        }

        if (hash == _lastBaseStateHash)
            return;

        _lastBaseStateHash = hash;
        for (int i = 0; i < AttackStateNames.Length; i++)
        {
            if (hash == Animator.StringToHash(AttackStateNames[i]))
            {
                fxAudioSource.PlayOneShot(attackWhooshClip, Mathf.Clamp01(attackWhooshVolume));
                return;
            }
        }
    }

    /// <summary>
    /// Blends the masked punch layer in and out. Driven from the layer's own (replicated) state
    /// rather than set imperatively by the server, so observers get the identical blend without
    /// relying on layer-weight synchronisation.
    /// </summary>
    void UpdateUpperBodyLayerWeight()
    {
        if (animator == null || upperBodyLayerIndex <= 0 || upperBodyLayerIndex >= animator.layerCount)
            return;

        bool punching = IsAnimatorInState(upperBodyLayerIndex, "Punch")
            || IsAnimatorInState(upperBodyLayerIndex, "QuadPunch");

        float current = animator.GetLayerWeight(upperBodyLayerIndex);
        float target = punching ? 1f : 0f;
        if (Mathf.Approximately(current, target))
            return;

        animator.SetLayerWeight(upperBodyLayerIndex, Mathf.MoveTowards(
            current, target, Time.deltaTime / Mathf.Max(0.01f, upperBodyBlendSeconds)));
    }

    /// <summary>
    /// Footsteps are driven by the ANIMATION itself: a step fires on the frame a toe bone drops to
    /// the floor. The rhythm therefore matches the visible stride exactly at any playback rate —
    /// whether he's patrolling, stalking, sprinting or slowing round a corner — with no interval
    /// left to drift out of sync with the speed.
    ///
    /// Runs on every peer straight off the replicated animator pose, so observers hear precisely the
    /// cadence they see and no footstep netcode is needed. Only the locomotion states step; idle,
    /// attack and stagger foot movement stays silent.
    /// </summary>
    void UpdateFootstepsFromAnimation()
    {
        if (footstepAudioSource == null || animator == null || _leftToe == null || _rightToe == null)
            return;

        if (!IsAnimatorInState(0, "Walk") && !IsAnimatorInState(0, "Run"))
        {
            // Disarm while not walking/running so re-entering locomotion can't fire an instant
            // step from a foot that merely happened to be resting low.
            _leftFootArmed = false;
            _rightFootArmed = false;
            return;
        }

        float leftHeight = transform.InverseTransformPoint(_leftToe.position).y;
        float rightHeight = transform.InverseTransformPoint(_rightToe.position).y;

        if (_leftFootArmed && leftHeight <= footContactHeight)
        {
            PlayFootstep(leftFootstepClip);
            _leftFootArmed = false;
        }
        else if (!_leftFootArmed && leftHeight >= footLiftResetHeight)
        {
            _leftFootArmed = true;
        }

        if (_rightFootArmed && rightHeight <= footContactHeight)
        {
            PlayFootstep(rightFootstepClip);
            _rightFootArmed = false;
        }
        else if (!_rightFootArmed && rightHeight >= footLiftResetHeight)
        {
            _rightFootArmed = true;
        }
    }

    void PlayFootstep(AudioClip clip)
    {
        if (clip == null || footstepAudioSource == null)
            return;

        footstepAudioSource.pitch = 1f + Random.Range(-footstepPitchVariance, footstepPitchVariance);
        footstepAudioSource.PlayOneShot(clip, Mathf.Max(0f, footstepVolume));
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
        footstepAudioSource.maxDistance = 28f;
        footstepAudioSource.rolloffMode = AudioRolloffMode.Linear;
        footstepAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(footstepAudioSource);
    }

    void ConfigureFxAudioSource(bool allowCreate = true)
    {
        AudioSource resolved = GetOrCreateChildAudioSource(FxAudioChildName, allowCreate);
        if (resolved == null)
            return;

        fxAudioSource = resolved;
        fxAudioSource.playOnAwake = false;
        fxAudioSource.loop = false;
        fxAudioSource.spatialBlend = 1f;
        fxAudioSource.minDistance = 2f;
        fxAudioSource.maxDistance = 32f;
        fxAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        fxAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(fxAudioSource);
    }

    sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
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

        Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, hearingRadius);

        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, meleeRange);

        Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, hurricaneMaxRange);
    }
}
