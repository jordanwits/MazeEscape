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
    [FormerlySerializedAs("screamSpatialBlend")]
    [SerializeField, Range(0f, 1f)] float voiceSpatialBlend = 1f;
    [FormerlySerializedAs("scream3DMinDistance")]
    [SerializeField, Min(0.01f)] float voice3DMinDistance = 2f;
    [FormerlySerializedAs("scream3DMaxDistance")]
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
    [Tooltip("If enabled, zombies only become alerted when they can see the player.")]
    [SerializeField] bool requireDetectionLineOfSight = true;
    [Tooltip("Layers considered solid when checking whether detection is blocked.")]
    [SerializeField] LayerMask detectionLineOfSightMask = Physics.DefaultRaycastLayers;
    [Tooltip("Height used for the detection obstruction check so the ray aims roughly at chest level.")]
    [SerializeField] float detectionLineOfSightHeight = 1.1f;

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
    [Tooltip("How long the zombie is stunned during a hit reaction.")]
    [SerializeField] float hitReactionDuration = 2.0f;
    [Tooltip("Blend time when exiting hit reaction back into locomotion.")]
    [SerializeField] float hitReactionExitCrossfadeDuration = 0.18f;
    [Tooltip("Layer index for upper body attacks. Set to 1 if using Avatar Mask layering.")]
    [SerializeField] int upperBodyLayerIndex = 1;
    [Tooltip("Allow the zombie to move while attacking (uses upper body layer).")]
    [SerializeField] bool allowMoveWhileAttacking = true;

    readonly Collider[] _detectionHits = new Collider[16];
    readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];

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
    float _rapidPunchCounterWindowEndTime;
    bool _isCounterAttackInvincible;
    float _footstepTimer;
    readonly List<AudioClip> _footstepPool = new List<AudioClip>(4);
    int[] _footstepShuffle;
    int _footstepShuffleIndex;
    float _nextGroanTime = -1f;

    public bool IsInvincible => _isCounterAttackInvincible;

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

        UpdatePeriodicGroan();
        RefreshTarget();
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
        _rapidPunchCounterWindowEndTime = 0f;
        _isCounterAttackInvincible = false;
        _footstepTimer = 0f;
        _nextGroanTime = -1f;

        if (voiceAudioSource != null)
            voiceAudioSource.Stop();

        if (footstepAudioSource != null)
            footstepAudioSource.Stop();

        PlayDeathVoice();
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

        _rapidPunchCounterWindowEndTime = 0f;
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
            if (candidate == null || candidate.IsDead || IsPlayerCarriedByJailor(candidate))
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
            PlayerHealth[] players = FindObjectsByType<PlayerHealth>(FindObjectsInactive.Exclude);
            for (int i = 0; i < players.Length; i++)
            {
                PlayerHealth candidate = players[i];
                if (candidate == null || candidate.IsDead || IsPlayerCarriedByJailor(candidate))
                    continue;

                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance > detectionRadius || distance >= closestDistance)
                    continue;

                if (!HasDetectionLineOfSight(candidate))
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

        float moveSpeed = walkSpeed;
        float targetMultiplier = SampleStepCurve();
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
        _rapidPunchCounterWindowEndTime = 0f;
        _footstepTimer = 0f;
        _nextGroanTime = -1f;
        StopZombieVocalAudio();
    }

    void ClearTarget()
    {
        _target = null;
        _targetHealth = null;
        _rapidPunchCounterWindowEndTime = 0f;
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

        float interval = Mathf.Max(0.05f, walkFootstepInterval * 2f);
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

        float normalizedSpeed = walkSpeed > 0.001f ? Mathf.Clamp01(_intendedMoveSpeed / walkSpeed) : 0f;
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
