using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class ZombieAI : MonoBehaviour
{
    enum ZombieState
    {
        Idle,
        Alert,
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
    [SerializeField] AudioSource screamAudioSource;
    [SerializeField] AudioClip screamAudioClip;

    [Header("Detection")]
    [SerializeField] LayerMask detectionMask;
    [SerializeField] float detectionRadius = 10f;
    [SerializeField] float loseTargetRadiusMultiplier = 1.5f;
    [SerializeField] float attackRadius = 2f;
    [SerializeField] float targetNavMeshSampleRadius = 3f;

    [Header("Movement")]
    [SerializeField] float walkSpeed = 1.5f;
    [SerializeField] float runSpeed = 4f;
    [SerializeField] float rotationSpeed = 720f;
    [SerializeField] float gravity = -20f;
    [SerializeField] float groundedStickDown = 2f;
    [SerializeField] float pitProbeForwardDistance = 0.75f;
    [SerializeField] float pitProbeDepth = 4f;
    [SerializeField] float pitDropMinHeight = 1f;
    [SerializeField] float pitDropMaxHeight = 12f;
    [SerializeField] float pitDropCommitDuration = 0.45f;

    [Header("Stamina")]
    [SerializeField] float maxStamina = 8000f;
    [Tooltip("Stamina drained per second while running.")]
    [SerializeField] float staminaDrainRate = 1f;
    [Tooltip("Stamina recovered per second while not running.")]
    [SerializeField] float staminaRegenRate = 20f;
    [Tooltip("Optional UI Image (set to Filled) on a world-space canvas to show the stamina bar.")]
    [SerializeField] UnityEngine.UI.Image staminaBarImage;

    [Header("Step Rhythm")]
    [Tooltip("Maps walk animation normalized time (0-1) to speed multiplier. Shape this to match footstep timing.")]
    [SerializeField] AnimationCurve walkStepCurve = DefaultWalkStepCurve();
    [Tooltip("Maps run animation normalized time (0-1) to speed multiplier. Shape this to match footstep timing.")]
    [SerializeField] AnimationCurve runStepCurve = DefaultRunStepCurve();
    [Tooltip("How quickly the actual speed blends toward the curve target. Higher = snappier steps.")]
    [SerializeField] float stepSpeedSmoothing = 15f;

    [Header("Combat")]
    [SerializeField] float damage = 25f;
    [SerializeField] float attackRate = 1.5f;
    [Tooltip("If the zombie is punched again before this window expires, it immediately counters with Attack2 instead of taking another hit.")]
    [SerializeField] float counterAttackQuickSuccessionWindow = 1f;
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

    [Header("Alert")]
    [SerializeField] bool screamsOnAlert = true;
    [SerializeField] float screamDuration = 1.3f;
    [SerializeField, Range(0f, 1f)] float screamVolume = 1f;
    [Tooltip("Extra multiplier on top of Scream Volume. Use when the clip still feels quiet at volume 1.")]
    [SerializeField, Range(0.5f, 2.5f)] float screamLoudnessMultiplier = 1.35f;
    [Tooltip("0 = 2D (no position). 1 = full 3D: panning and volume follow the listener relative to the zombie.")]
    [SerializeField, Range(0f, 1f)] float screamSpatialBlend = 1f;
    [Tooltip("3D: distance at which the scream is still full volume (Unity rolloff).")]
    [SerializeField, Min(0.01f)] float scream3DMinDistance = 2f;
    [Tooltip("3D: past this distance the scream is inaudible (rolloff).")]
    [SerializeField, Min(0.01f)] float scream3DMaxDistance = 70f;

    [Header("Animator")]
    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string groundedParameter = "Grounded";
    [SerializeField] string verticalVelocityParameter = "VerticalVelocity";
    [SerializeField] float attackCrossfadeDuration = 0.08f;
    [SerializeField] string attackTrigger = "Attack";
    [SerializeField] string counterAttackStateName = "Attack2";
    [Tooltip("Extra blend time when easing the retaliatory Attack2 back out to the empty upper-body pose.")]
    [SerializeField] float counterAttackExitCrossfadeDuration = 0.22f;
    [SerializeField] string screamTrigger = "Scream";
    [SerializeField] string hitReactionTrigger = "HitReaction";
    [Tooltip("How long the zombie is stunned during a hit reaction.")]
    [SerializeField] float hitReactionDuration = 2.0f;
    [Tooltip("Blend time when exiting hit reaction back into locomotion.")]
    [SerializeField] float hitReactionExitCrossfadeDuration = 0.18f;
    [Tooltip("Layer index for upper body attacks. Set to 1 if using Avatar Mask layering.")]
    [SerializeField] int upperBodyLayerIndex = 1;
    [Tooltip("Allow the zombie to move while attacking (uses upper body layer).")]
    [SerializeField] bool allowMoveWhileAttacking = true;

    readonly Collider[] _detectionHits = new Collider[16];

    ZombieState _state;
    Transform _target;
    PlayerHealth _targetHealth;
    float _nextAttackTime;
    float _alertEndTime;
    float _hitReactionEndTime;
    Coroutine _attackRoutine;
    bool _hasAlertedTarget;
    bool _hasPlayedAlertScream;
    Vector3 _horizontalVelocity;
    Vector3 _verticalVelocity;
    float _currentStepMultiplier;
    float _intendedMoveSpeed;
    bool _pitDropActive;
    float _pitDropUnlockTime;
    float _rapidPunchCounterWindowEndTime;
    bool _isCounterAttackInvincible;

    float _currentStamina;
    bool _staminaFull;

    public float StaminaNormalized => maxStamina > 0f ? _currentStamina / maxStamina : 0f;
    public bool IsInvincible => _isCounterAttackInvincible;

    void Reset()
    {
        CacheReferences();
        ConfigureScreamAudioSource();
        ApplyAgentSettings();
    }

    void Awake()
    {
        CacheReferences();
        ConfigureScreamAudioSource();
        ApplyAgentSettings();
        _currentStamina = 0f;
        _staminaFull = false;
    }

    void OnEnable()
    {
        TrySnapToNavMesh();
    }

    void Update()
    {
        NetworkObject networkObject = GetComponent<NetworkObject>();
        if (networkObject != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsServer)
            return;

        if (zombieHealth != null && zombieHealth.IsDead)
        {
            HandleDeath();
            return;
        }

        if (_state == ZombieState.Dead)
            return;

        RefreshTarget();

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
            float loseTargetRadius = Mathf.Max(detectionRadius, detectionRadius * loseTargetRadiusMultiplier);
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
                        StartAlertOrChase();
                        break;
                    case ZombieState.Alert:
                        RegenStamina();
                        UpdateAlert();
                        break;
                    case ZombieState.Chase:
                        desiredHorizontalVelocity = UpdateChase(distanceToTarget);
                        break;
                    case ZombieState.Attack:
                        RegenStamina();
                        UpdateAttack();
                        break;
                    case ZombieState.HitReaction:
                        RegenStamina();
                        UpdateHitReaction();
                        break;
                }
            }
        }

        ApplyMovement(desiredHorizontalVelocity);
        UpdateAnimatorParameters();
        UpdateStaminaBar();
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
        _hasPlayedAlertScream = false;
        _rapidPunchCounterWindowEndTime = 0f;
        _isCounterAttackInvincible = false;
    }

    public bool TryHandleIncomingMeleeHit(Transform attacker, PlayerHealth attackerHealth)
    {
        if (_state == ZombieState.Dead)
            return false;

        if (_isCounterAttackInvincible)
            return false;

        AssignAttackerAsTarget(attacker, attackerHealth);

        bool wasHitTooQuickly = counterAttackQuickSuccessionWindow > 0f
            && Time.time <= _rapidPunchCounterWindowEndTime;
        if (wasHitTooQuickly)
        {
            StartCounterAttack();
            return false;
        }

        _rapidPunchCounterWindowEndTime = Time.time + counterAttackQuickSuccessionWindow;
        return true;
    }

    public void TakeHit()
    {
        if (_state == ZombieState.Dead)
            return;

        if (_attackRoutine != null)
        {
            StopCoroutine(_attackRoutine);
            _attackRoutine = null;
        }

        _state = ZombieState.HitReaction;
        _hitReactionEndTime = Time.time + hitReactionDuration;
        _nextAttackTime = _hitReactionEndTime + attackRate;

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
                GetPostHitReactionState(),
                hitReactionExitCrossfadeDuration,
                0,
                0f);
        }

        _state = _targetHealth != null && !_targetHealth.IsDead ? ZombieState.Chase : ZombieState.Idle;
    }

    void AssignAttackerAsTarget(Transform attacker, PlayerHealth attackerHealth)
    {
        if (attackerHealth == null && attacker != null)
            attackerHealth = attacker.GetComponentInParent<PlayerHealth>();

        if (attackerHealth == null || attackerHealth.IsDead)
            return;

        if (_targetHealth != attackerHealth)
            _hasAlertedTarget = false;

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

        _rapidPunchCounterWindowEndTime = 0f;
        _attackRoutine = StartCoroutine(AttackRoutine(useCounterAttack: true));
    }

    void ResetAllTriggers()
    {
        animator.ResetTrigger(attackTrigger);
        animator.ResetTrigger(hitReactionTrigger);
        animator.ResetTrigger(screamTrigger);
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

    string GetPostHitReactionState()
    {
        if (_targetHealth == null || _targetHealth.IsDead)
            return "Idle";

        return _staminaFull ? "Run" : "Walk";
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

        if (screamAudioSource == null)
            screamAudioSource = GetComponent<AudioSource>();
    }

    void ConfigureScreamAudioSource()
    {
        if (screamAudioSource == null)
            screamAudioSource = gameObject.AddComponent<AudioSource>();

        screamAudioSource.playOnAwake = false;
        screamAudioSource.loop = false;
        screamAudioSource.spatialBlend = screamSpatialBlend;
        screamAudioSource.minDistance = scream3DMinDistance;
        screamAudioSource.maxDistance = scream3DMaxDistance;
        screamAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        screamAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(screamAudioSource);
    }

    void ApplyAgentSettings()
    {
        if (navMeshAgent == null)
            return;

        navMeshAgent.enabled = true;
        navMeshAgent.speed = walkSpeed;
        navMeshAgent.angularSpeed = rotationSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.1f, attackRadius * 0.9f);
        navMeshAgent.acceleration = Mathf.Max(navMeshAgent.acceleration, runSpeed * 4f);
        navMeshAgent.updatePosition = false;
        navMeshAgent.updateRotation = false;
        navMeshAgent.baseOffset = 0f;

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
            detectionRadius,
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
            if (candidate == null || candidate.IsDead)
                continue;

            float distance = Vector3.Distance(transform.position, candidate.transform.position);
            if (distance >= closestDistance)
                continue;

            closestTarget = candidate;
            closestDistance = distance;
        }

        if (closestTarget == null)
        {
            PlayerHealth[] players = FindObjectsByType<PlayerHealth>(FindObjectsInactive.Exclude);
            for (int i = 0; i < players.Length; i++)
            {
                PlayerHealth candidate = players[i];
                if (candidate == null || candidate.IsDead)
                    continue;

                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance > detectionRadius || distance >= closestDistance)
                    continue;

                closestTarget = candidate;
                closestDistance = distance;
            }
        }

        if (closestTarget == null)
            return;

        if (_targetHealth != closestTarget)
            _hasAlertedTarget = false;

        _targetHealth = closestTarget;
        _target = closestTarget.transform;
    }

    void StartAlertOrChase()
    {
        if (_target == null)
            return;

        if (screamsOnAlert && !_hasAlertedTarget)
        {
            _state = ZombieState.Alert;
            _hasAlertedTarget = true;
            _hasPlayedAlertScream = false;
            _alertEndTime = Time.time + screamDuration;

            if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
            {
                navMeshAgent.isStopped = true;
                navMeshAgent.ResetPath();
            }

            if (animator != null)
                animator.SetTrigger(screamTrigger);

            FaceTarget();
            return;
        }

        _state = ZombieState.Chase;
    }

    void UpdateAlert()
    {
        FaceTarget();

        if (Time.time < _alertEndTime)
            return;

        _state = ZombieState.Chase;
    }

    Vector3 UpdateChase(float distanceToTarget)
    {
        if (_target == null)
        {
            EnterIdle();
            return Vector3.zero;
        }

        if (distanceToTarget <= attackRadius)
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

        bool wantsToRun = _staminaFull;
        if (wantsToRun)
        {
            _currentStamina = Mathf.Max(0f, _currentStamina - staminaDrainRate * Time.deltaTime);
            if (_currentStamina <= 0f)
                _staminaFull = false;
        }
        else
        {
            _currentStamina = Mathf.Min(maxStamina, _currentStamina + staminaRegenRate * Time.deltaTime);
            if (_currentStamina >= maxStamina)
                _staminaFull = true;
        }
        float moveSpeed = wantsToRun ? runSpeed : walkSpeed;
        float targetMultiplier = SampleStepCurve(moveSpeed);
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
        _hasPlayedAlertScream = false;
        _rapidPunchCounterWindowEndTime = 0f;
    }

    void ClearTarget()
    {
        _target = null;
        _targetHealth = null;
        _hasAlertedTarget = false;
        _hasPlayedAlertScream = false;
        _rapidPunchCounterWindowEndTime = 0f;
    }

    public void PlayScreamAudio()
    {
        if (_state == ZombieState.Dead || _hasPlayedAlertScream || screamAudioClip == null || screamAudioSource == null)
            return;

        _hasPlayedAlertScream = true;
        screamAudioSource.clip = screamAudioClip;
        // Unity often clamps AudioSource.volume to 0..1, but the multiplier still helps
        // when the serialized scream volume is low, and nudges loudness on engines that allow >1.
        float level = screamVolume * screamLoudnessMultiplier;
        screamAudioSource.volume = Mathf.Clamp(level, 0f, 2f);
        screamAudioSource.Play();
    }

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

        if (useCounterAttack)
        {
            _isCounterAttackInvincible = true;
            _hitReactionEndTime = 0f;
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
        {
            _targetHealth.TakeDamage(damage);
            NotifyZombiePlayerHitSfx(_targetHealth);
        }

        _nextAttackTime = Time.time + attackRate;

        float recoveryTime = Mathf.Max(0f, attackRate - hitDelay);
        if (recoveryTime > 0f)
            yield return new WaitForSeconds(recoveryTime);

        if (animator != null && useUpperBodyAttack)
        {
            float exitCrossfadeDuration = useCounterAttack ? counterAttackExitCrossfadeDuration : 0.1f;
            animator.CrossFadeInFixedTime("Empty", exitCrossfadeDuration, upperBodyLayerIndex, 0f);
        }

        _isCounterAttackInvincible = false;
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

    static void NotifyZombiePlayerHitSfx(PlayerHealth targetHealth)
    {
        if (targetHealth == null)
            return;

        NetworkPlayerCombat combat = targetHealth.GetComponent<NetworkPlayerCombat>();
        if (combat != null && combat.IsSpawned && combat.IsServer)
        {
            combat.NotifyOwnerZombieHitSfx();
            return;
        }

        targetHealth.GetComponent<PlayerController>()?.PlayZombieHitSfx();
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
        Vector3 origin = transform.position + Vector3.up * attackLineOfSightHeight + committedAttackDirection * 0.15f;
        Vector3 targetPoint = targetHealth.transform.position + Vector3.up * attackLineOfSightHeight;
        Vector3 toTarget = targetPoint - origin;
        float distanceToTarget = toTarget.magnitude;
        if (distanceToTarget <= 0.001f)
            return true;

        int mask = attackLineOfSightMask.value == 0 ? Physics.DefaultRaycastLayers : attackLineOfSightMask.value;
        if (!Physics.Raycast(origin, toTarget / distanceToTarget, out RaycastHit hit, distanceToTarget, mask, QueryTriggerInteraction.Ignore))
            return true;

        return hit.transform == targetHealth.transform || hit.transform.IsChildOf(targetHealth.transform);
    }

    void RegenStamina()
    {
        _currentStamina = Mathf.Min(maxStamina, _currentStamina + staminaRegenRate * Time.deltaTime);
        if (_currentStamina >= maxStamina)
            _staminaFull = true;
    }

    void UpdateStaminaBar()
    {
        if (staminaBarImage != null)
            staminaBarImage.fillAmount = StaminaNormalized;
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

        float normalizedSpeed = runSpeed > 0.001f ? Mathf.Clamp01(_intendedMoveSpeed / runSpeed) : 0f;
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

    float SampleStepCurve(float moveSpeed)
    {
        if (animator == null)
            return 1f;

        AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);
        float normalizedTime = stateInfo.normalizedTime % 1f;

        bool isWalking = moveSpeed <= walkSpeed;
        AnimationCurve curve = isWalking ? walkStepCurve : runStepCurve;
        if (curve == null || curve.length == 0)
            return 1f;

        return Mathf.Clamp01(curve.Evaluate(normalizedTime));
    }

    static AnimationCurve DefaultWalkStepCurve()
    {
        // Two-step cycle: move-pause-move-pause per loop
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

    static AnimationCurve DefaultRunStepCurve()
    {
        // Running has shorter pauses
        return new AnimationCurve(
            new Keyframe(0.00f, 0.2f),
            new Keyframe(0.10f, 1f),
            new Keyframe(0.35f, 1f),
            new Keyframe(0.45f, 0.2f),
            new Keyframe(0.55f, 0.2f),
            new Keyframe(0.65f, 1f),
            new Keyframe(0.90f, 1f),
            new Keyframe(1.00f, 0.2f)
        );
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.85f, 0.15f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, detectionRadius);

        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, attackRadius);
    }
}
