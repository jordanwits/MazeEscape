using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class JailorAI : MonoBehaviour
{
    enum JailorState
    {
        Idle,
        Chase
    }

    [Header("References")]
    [SerializeField] Animator animator;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;

    [Header("Detection")]
    [SerializeField] LayerMask detectionMask;
    [SerializeField] float detectionRadius = 12f;
    [SerializeField] float loseTargetRadiusMultiplier = 1.5f;
    [SerializeField] float hearingRadius = 18f;
    [SerializeField] float voiceHearRadius = 22f;
    [SerializeField] float targetNavMeshSampleRadius = 3f;
    [Tooltip("If enabled, sight checks require a clear ray to the player.")]
    [SerializeField] bool requireDetectionLineOfSight = true;
    [SerializeField] LayerMask detectionLineOfSightMask = Physics.DefaultRaycastLayers;
    [SerializeField] float detectionLineOfSightHeight = 1.1f;

    [Header("Movement")]
    [SerializeField] float walkSpeed = 2.6f;
    [SerializeField] float runSpeed = 3.25f;
    [SerializeField] float rotationSpeed = 360f;
    [SerializeField] float gravity = -20f;
    [SerializeField] float groundedStickDown = 2f;
    [Tooltip("How often chase destination is refreshed. Lower = more reactive, higher = less path jitter.")]
    [SerializeField] float destinationRefreshInterval = 0.1f;
    [Tooltip("Minimum target movement before forcing a path refresh.")]
    [SerializeField] float destinationRefreshMinDistance = 0.2f;
    [Tooltip("When chasing, use run speed (Jailor does not use stamina).")]
    [SerializeField] bool alwaysRunWhenChasing = true;

    [Header("Animator")]
    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string groundedParameter = "Grounded";
    [SerializeField] string verticalVelocityParameter = "VerticalVelocity";
    [Tooltip("Use actual horizontal move speed for the blend tree (matches feet). 0 = instant; higher = less flicker between idle and move.")]
    [SerializeField] float animatorSpeedLerp = 12f;
    [Tooltip("Below this normalized speed, treat movement as idle to avoid idle/walk chatter.")]
    [SerializeField] float idleSpeedDeadZone = 0.08f;

    readonly Collider[] _detectionHits = new Collider[16];
    readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];

    JailorState _state;
    Transform _target;
    PlayerHealth _targetHealth;
    Vector3 _horizontalVelocity;
    Vector3 _verticalVelocity;
    float _intendedMoveSpeed;
    float _smoothedAnimSpeed;
    Vector3 _lastPathDestination;
    float _nextDestinationRefreshTime;
    bool _hasSpeedParameter = true;
    bool _hasGroundedParameter = true;
    bool _hasVerticalVelocityParameter = true;
    bool _loggedMissingAnimatorParams;

    void Reset()
    {
        CacheReferences();
        ApplyAgentSettings();
    }

    void Awake()
    {
        CacheReferences();
        ApplyAgentSettings();
    }

    void OnEnable()
    {
        ServerProximityVoiceNotifications.Register(this);
        TrySnapToNavMesh();
    }

    void OnDisable()
    {
        ServerProximityVoiceNotifications.Unregister(this);
    }

    void CacheReferences()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (navMeshAgent == null)
            navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (animator != null)
        {
            // Jailor gameplay lives on a root with no SkinnedMeshRenderer (mesh is on a child). CullUpdateTransforms
            // uses the wrong bounds and often never updates the rig, so the character stays in one pose.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // If the body comes from a nested model prefab, it may have its own Animator; only the root should drive the rig.
            foreach (Animator other in GetComponentsInChildren<Animator>(true))
            {
                if (other != null && other != animator)
                    other.enabled = false;
            }

            CacheAnimatorParameterAvailability();
        }
    }

    void CacheAnimatorParameterAvailability()
    {
        _hasSpeedParameter = HasAnimatorParameter(speedParameter, AnimatorControllerParameterType.Float);
        _hasGroundedParameter = HasAnimatorParameter(groundedParameter, AnimatorControllerParameterType.Bool);
        _hasVerticalVelocityParameter = HasAnimatorParameter(verticalVelocityParameter, AnimatorControllerParameterType.Float);
    }

    bool HasAnimatorParameter(string parameterName, AnimatorControllerParameterType parameterType)
    {
        if (animator == null || string.IsNullOrWhiteSpace(parameterName))
            return false;

        AnimatorControllerParameter[] parameters = animator.parameters;
        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].type == parameterType && parameters[i].name == parameterName)
                return true;
        }

        return false;
    }

    void ApplyAgentSettings()
    {
        if (navMeshAgent == null)
            return;

        navMeshAgent.enabled = true;
        navMeshAgent.speed = walkSpeed;
        navMeshAgent.angularSpeed = rotationSpeed;
        navMeshAgent.stoppingDistance = 0.5f;
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

    bool ShouldRunSimulation()
    {
        NetworkObject networkObject = GetComponent<NetworkObject>();
        if (networkObject != null
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && !NetworkManager.Singleton.IsServer)
            return false;
        return true;
    }

    void Update()
    {
        if (!ShouldRunSimulation())
            return;

        RefreshTargetFromSightAndHearing();

        float loseRadius = Mathf.Max(detectionRadius, hearingRadius, voiceHearRadius)
            * Mathf.Max(1f, loseTargetRadiusMultiplier);

        if (_targetHealth != null && !_targetHealth.IsDead)
        {
            float d = Vector3.Distance(transform.position, _target.position);
            if (d > loseRadius)
                ClearTarget();
        }

        Vector3 desiredHorizontal = Vector3.zero;
        if (_targetHealth != null && !_targetHealth.IsDead)
        {
            if (_state == JailorState.Idle)
                EnterChase();
            if (_state == JailorState.Chase)
                desiredHorizontal = UpdateChase();
        }
        else
            EnterIdle();

        ApplyMovement(desiredHorizontal);
        UpdateAnimatorParameters();
    }

    public void OnServerHeardVoiceFrame(ulong speakerClientId)
    {
        if (!ShouldRunSimulation())
            return;

        if (!VoiceClientRegistry.TryGet(speakerClientId, out NetworkPlayerVoice voice)
            || voice == null)
            return;

        PlayerHealth health = voice.GetComponentInParent<PlayerHealth>();
        if (health == null || health.IsDead)
            return;

        float d = Vector3.Distance(transform.position, voice.transform.position);
        if (d > voiceHearRadius)
            return;

        AssignTarget(health);
    }

    void RefreshTargetFromSightAndHearing()
    {
        if (_targetHealth != null && !_targetHealth.IsDead)
            return;

        PlayerHealth best = null;
        float bestScore = float.MaxValue;

        int mask = detectionMask.value == 0 ? Physics.DefaultRaycastLayers : detectionMask.value;
        int hitCount = Physics.OverlapSphereNonAlloc(
            transform.position,
            Mathf.Max(detectionRadius, hearingRadius),
            _detectionHits,
            mask,
            QueryTriggerInteraction.Ignore);

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
            bool heard = distance <= hearingRadius && IsPlayerAudiblySprinting(candidate);
            bool seen = distance <= detectionRadius
                && (!requireDetectionLineOfSight || HasDetectionLineOfSight(candidate));

            if (!heard && !seen)
                continue;

            if (distance < bestScore)
            {
                bestScore = distance;
                best = candidate;
            }
        }

        if (best == null)
        {
            foreach (PlayerHealth candidate in FindObjectsByType<PlayerHealth>(FindObjectsInactive.Exclude))
            {
                if (candidate == null || candidate.IsDead)
                    continue;

                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance > Mathf.Max(detectionRadius, hearingRadius))
                    continue;

                bool heard = distance <= hearingRadius && IsPlayerAudiblySprinting(candidate);
                bool seen = distance <= detectionRadius
                    && (!requireDetectionLineOfSight || HasDetectionLineOfSight(candidate));

                if (!heard && !seen)
                    continue;

                if (distance < bestScore)
                {
                    bestScore = distance;
                    best = candidate;
                }
            }
        }

        if (best != null)
            AssignTarget(best);
    }

    void AssignTarget(PlayerHealth health)
    {
        _targetHealth = health;
        _target = health.transform;
    }

    void ClearTarget()
    {
        _target = null;
        _targetHealth = null;
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

    bool HasDetectionLineOfSight(PlayerHealth targetHealth)
    {
        return HasLineOfSightToTarget(targetHealth, detectionLineOfSightMask, detectionLineOfSightHeight, Vector3.zero);
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

        int rayMask = lineOfSightMask.value == 0 ? Physics.DefaultRaycastLayers : lineOfSightMask.value;
        int hitCount = Physics.RaycastNonAlloc(
            origin,
            toTarget / distanceToTarget,
            _lineOfSightHits,
            distanceToTarget,
            rayMask,
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

    Vector3 UpdateChase()
    {
        if (_target == null)
        {
            EnterIdle();
            return Vector3.zero;
        }

        float moveSpeed = alwaysRunWhenChasing ? runSpeed : walkSpeed;
        _intendedMoveSpeed = moveSpeed;

        if (!TrySnapToNavMesh())
            return Vector3.zero;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = moveSpeed;
        navMeshAgent.stoppingDistance = 0.5f;

        if (!TryGetTargetDestination(out Vector3 destination))
            return Vector3.zero;

        bool shouldRefreshDestination =
            Time.time >= _nextDestinationRefreshTime
            || (destination - _lastPathDestination).sqrMagnitude
                >= destinationRefreshMinDistance * destinationRefreshMinDistance;

        if (shouldRefreshDestination)
        {
            if (!navMeshAgent.SetDestination(destination))
            {
                // Path may be pending, or the target moved off-mesh; keep chase state and retry shortly.
                _nextDestinationRefreshTime = Time.time + Mathf.Max(0.02f, destinationRefreshInterval);
                return Vector3.zero;
            }

            _lastPathDestination = destination;
            _nextDestinationRefreshTime = Time.time + Mathf.Max(0.02f, destinationRefreshInterval);
        }

        Vector3 desiredVelocity = navMeshAgent.velocity.sqrMagnitude > 0.0001f
            ? navMeshAgent.velocity
            : navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        if (desiredVelocity.sqrMagnitude > moveSpeed * moveSpeed)
            desiredVelocity = desiredVelocity.normalized * moveSpeed;

        return desiredVelocity;
    }

    void EnterIdle()
    {
        if (_state == JailorState.Idle && _target == null)
            return;

        _state = JailorState.Idle;
        ClearTarget();

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        _horizontalVelocity = Vector3.zero;
        _intendedMoveSpeed = 0f;
        _nextDestinationRefreshTime = 0f;
    }

    void EnterChase()
    {
        _state = JailorState.Chase;
    }

    void ApplyMovement(Vector3 desiredHorizontalVelocity)
    {
        if (characterController == null)
            return;

        bool grounded = characterController.isGrounded;
        if (grounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -groundedStickDown;

        _horizontalVelocity = desiredHorizontalVelocity;
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
                transform.rotation,
                targetRotation,
                rotationSpeed * Time.deltaTime);
        }
    }

    void UpdateAnimatorParameters()
    {
        if (animator == null)
            return;

        if (!_hasSpeedParameter || !_hasGroundedParameter || !_hasVerticalVelocityParameter)
        {
            if (!_loggedMissingAnimatorParams)
            {
                string controllerName = animator.runtimeAnimatorController != null
                    ? animator.runtimeAnimatorController.name
                    : "(none)";
                Debug.LogWarning(
                    $"[JailorAI] Animator controller '{controllerName}' is missing required parameters " +
                    $"('{speedParameter}' float, '{groundedParameter}' bool, '{verticalVelocityParameter}' float). " +
                    "Jailor movement animation sync is disabled until those parameters exist.",
                    this);
                _loggedMissingAnimatorParams = true;
            }
            return;
        }

        // Match clips to what the body is doing: intended speed alone (old behavior) can stay at "run" while
        // desiredVelocity is near zero, which flickers the blend tree between idle, walk, and run.
        float horizontal = new Vector3(_horizontalVelocity.x, 0f, _horizontalVelocity.z).magnitude;
        if (_state == JailorState.Idle)
            horizontal = 0f;

        float targetNormalized = runSpeed > 0.001f ? Mathf.Clamp01(horizontal / runSpeed) : 0f;
        if (targetNormalized < idleSpeedDeadZone)
            targetNormalized = 0f;
        if (alwaysRunWhenChasing
            && _state == JailorState.Chase
            && _targetHealth != null
            && targetNormalized > 0.08f)
        {
            // In chase, once we are actually moving, hold the run clip (blend tree 1) instead of a weak walk.
            targetNormalized = 1f;
        }

        if (animatorSpeedLerp <= 0f)
            _smoothedAnimSpeed = targetNormalized;
        else
        {
            float t = animatorSpeedLerp * Time.deltaTime;
            _smoothedAnimSpeed = Mathf.Lerp(_smoothedAnimSpeed, targetNormalized, 1f - Mathf.Exp(-t));
        }

        animator.SetFloat(speedParameter, _smoothedAnimSpeed);
        animator.SetBool(groundedParameter, characterController != null && characterController.isGrounded);
        animator.SetFloat(verticalVelocityParameter, _verticalVelocity.y);
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

        // Off-navmesh: still try a world point so SetDestination can build a path partway; do not give up the chase.
        destination = _target.position;
        return true;
    }

    sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
    {
        public static readonly RaycastHitDistanceComparer Instance = new();

        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}
