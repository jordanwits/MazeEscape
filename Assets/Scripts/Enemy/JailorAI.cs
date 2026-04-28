using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class JailorAI : MonoBehaviour
{
    enum JailorState
    {
        Idle,
        Patrol,
        Investigating,
        Chase,
        Grabbing,
        Carrying,
        JailDelivery
    }

    enum JailDeliveryPhase
    {
        OpeningDoor,
        DroppingPlayer,
    }

    [Header("References")]
    [SerializeField] Animator animator;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [Tooltip("Chest / arms attach point for the carried player (child of Jailor).")]
    [SerializeField] Transform carryAttach;
    [Header("Carried player pose (vs CarryAttach)")]
    [Tooltip("Translation in CarryAttach local axes (meters). Small nudges so the align point (below) lines up with CarryAttach after rotation.")]
    [SerializeField] Vector3 carriedPlayerLocalPositionOffset;
    [Tooltip("Rotation in CarryAttach local space (degrees). Try (180,0,0) or (0,180,0) to flip upside down; adjust per rig.")]
    [SerializeField] Vector3 carriedPlayerLocalEulerOffset;
    [Tooltip(
        "Player-pivot to body point you want on CarryAttach, in the player's local space (Y up the body). "
        + "Character roots are usually at the feet, so the attach sat at the ankles — use ~chest height (e.g. 0,1,0) so the chest lines up with the hands.")]
    [SerializeField] Vector3 playerRootToCarryAlignPointLocal = new Vector3(0f, 1f, 0f);
    [Tooltip("World drop point (jail cell). Assign per scene instance; NavMesh-sampled when moving.")]
    [SerializeField] Transform carryDestination;
    [Tooltip("If Carry Destination is empty: at Start, GameObject.Find(this exact name). Use a unique name. Maze builds usually assign via ProceduralMazeCoordinator instead.")]
    [SerializeField] string carryDestinationObjectName;
    [Header("Jail cell door (optional)")]
    [Tooltip("Hinge door on the jail prefab. If empty, a HingeInteractDoor under the carry destination root is used.")]
    [SerializeField] HingeInteractDoor jailCellDoor;

    /// <summary>Assign drop point at runtime (e.g. from <see cref="ServerNetworkPrefabSpawner"/> or maze build). Null clears to use forward fallback.</summary>
    public void SetCarryDestination(Transform destination)
    {
        carryDestination = destination;
        TryResolveJailCellDoorFromDestination();
    }

    [Header("Detection")]
    [SerializeField] LayerMask detectionMask;
    [SerializeField] float detectionRadius = 12f;
    [SerializeField] float loseTargetRadiusMultiplier = 1.5f;
    [SerializeField] float hearingRadius = 18f;
    [SerializeField] float voiceHearRadius = 22f;
    [SerializeField] float zombieNoiseHearRadius = 22f;
    [SerializeField] float targetNavMeshSampleRadius = 3f;
    [Tooltip("If enabled, sight checks require a clear ray to the player.")]
    [SerializeField] bool requireDetectionLineOfSight = true;
    [SerializeField] LayerMask detectionLineOfSightMask = Physics.DefaultRaycastLayers;
    [SerializeField] float detectionLineOfSightHeight = 1.1f;

    [Header("Grab / carry")]
    [SerializeField] float grabRange = 1.55f;
    [Tooltip("Extra reach added on top of Grab Range when checking grab start (same idea as Zombie attackHitRangePadding).")]
    [SerializeField] float grabHitRangePadding = 0.15f;
    [Tooltip("Half-angle from Jailor forward toward the player — same semantics as Zombie attackHitHalfAngle.")]
    [FormerlySerializedAs("maxGrabAngleDegrees")]
    [SerializeField, Range(0f, 180f)] float grabHitHalfAngle = 55f;
    [SerializeField] float maxGrabVerticalDelta = 1.1f;
    [SerializeField] string grabTriggerParameter = "Grab";
    [SerializeField] float grabClipDurationFallback = 1.93f;
    [SerializeField] float attachFallbackDelay = 0.95f;
    [SerializeField] float carrySpeed = 2.4f;
    [SerializeField] float carryStoppingDistance = 0.65f;
    [SerializeField] float carryArrivalDistance = 1.1f;
    [SerializeField] float carryDestinationSampleRadius = 2f;
    [Tooltip("Forward distance used when carryDestination is not set.")]
    [SerializeField] float carryDestinationForwardFallback = 10f;
    [Tooltip("After dropping the player (non-jail carry), Jailor ignores everyone this long. Jail delivery skips this so other players stay targetable.")]
    [SerializeField] float postDropChaseCooldownSeconds = 6f;
    [Tooltip("Minimum time carrying before a drop is allowed (avoids instant drop spam if arrival distance is tiny).")]
    [SerializeField] float minCarrySecondsBeforeDrop = 0.85f;
    [Tooltip("Max horizontal distance from carry marker to still accept NavMesh snap for pathing (expand if the jailor never reaches the drop).")]
    [SerializeField] float carryDestinationNavSnapMaxDistance = 24f;
    [Tooltip(
        "While carrying to jail, unlock/open the cell door when within this horizontal distance of the door. "
        + "Needed when the door was left closed after an escape so NavMesh can path inside before the drop.")]
    [SerializeField] float jailDoorPremptiveOpenDistance = 16f;
    [Header("Jailor key drop")]
    [Tooltip("Spawned when a player is grabbed. Add JailorKey to Default Network Prefabs if you use ForceSamePrefabs (no runtime AddNetworkPrefab).")]
    [SerializeField] GameObject jailorKeyWorldPrefab;
    [Tooltip("Local offset from the jailor root (Z = forward, Y = up).")]
    [SerializeField] Vector3 jailorKeyDropLocalOffset = new Vector3(0f, 0.12f, 0.55f);

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

    [Header("Patrol")]
    [SerializeField] float patrolSpeed = 2.2f;
    [SerializeField] float patrolMinWaypointDistance = 6f;
    [SerializeField] float patrolMaxWaypointDistance = 14f;
    [SerializeField] float patrolArrivalDistance = 1f;
    [SerializeField] float patrolDestinationRefreshInterval = 0.45f;
    [SerializeField] int patrolSampleAttempts = 14;
    [Tooltip("Avoid recently visited points so the jailor does not bounce in dead-end loops.")]
    [SerializeField] int patrolRecentDestinationMemory = 6;
    [SerializeField] float patrolRecentDestinationRadius = 3.5f;
    [SerializeField] float patrolStuckVelocityThreshold = 0.18f;
    [SerializeField] float patrolProgressCheckInterval = 0.35f;
    [SerializeField] float patrolMinProgressDistance = 0.1f;
    [SerializeField] float patrolStuckSeconds = 2f;
    [SerializeField] float patrolRepathCooldown = 0.9f;
    [SerializeField] float investigationArrivalDistance = 1.2f;
    [SerializeField] float investigationLingerSeconds = 10f;
    [SerializeField] float investigationSearchRadius = 3.5f;
    [SerializeField] float investigationSearchMinWaypointDistance = 1.2f;
    [SerializeField] int investigationSearchSampleAttempts = 10;
    [SerializeField] float chaseLoseLineOfSightSeconds = 2f;

    [Header("Animator")]
    [SerializeField] string speedParameter = "Speed";
    [SerializeField] string groundedParameter = "Grounded";
    [SerializeField] string verticalVelocityParameter = "VerticalVelocity";
    [SerializeField] string grabStateName = "Grab";
    [SerializeField] string jumpTriggerParameter = "Jump";
    [Tooltip("Use actual horizontal move speed for the blend tree (matches feet). 0 = instant; higher = less flicker between idle and move.")]
    [SerializeField] float animatorSpeedLerp = 12f;
    [Tooltip("Below this normalized speed, treat movement as idle to avoid idle/walk chatter.")]
    [SerializeField] float idleSpeedDeadZone = 0.08f;

    [Header("Audio")]
    [Tooltip("Played once when the Jailor successfully picks up a player.")]
    [SerializeField] AudioClip jailorLaughClip;
    [SerializeField, Range(0f, 1f)] float jailorLaughVolume = 0.3f;
    [SerializeField] AudioSource jailorLaughAudioSource;
    [Tooltip("Single jailor footstep clip. Trigger timing still uses the same walk/run animation phases.")]
    [SerializeField] AudioClip jailorFootstepClip;
    [SerializeField, Range(0f, 1f)] float jailorFootstepVolume = 0.45f;
    [SerializeField] AudioSource jailorFootstepAudioSource;
    [SerializeField] float jailorMinFootstepMoveSpeed = 0.2f;
    [Tooltip("Normalized animation times where walk footsteps should fire (x and y in 0-1).")]
    [SerializeField] Vector2 jailorWalkFootstepPhases = new Vector2(0.13f, 0.63f);
    [Tooltip("Normalized animation times where run footsteps should fire (x and y in 0-1).")]
    [SerializeField] Vector2 jailorRunFootstepPhases = new Vector2(0.1f, 0.6f);

    [Header("Off-mesh jump")]
    [Tooltip("Seconds to traverse a pit NavMeshLink.")]
    [SerializeField] float offMeshJumpDuration = 0.72f;
    [Tooltip("Extra vertical arc height applied during the pit jump.")]
    [SerializeField] float offMeshJumpArcHeight = 1.05f;
    [Tooltip("When this close to the end of the link, finish the traversal.")]
    [SerializeField] float offMeshJumpLandingSnapDistance = 0.45f;
    [Tooltip("Small cooldown to avoid instant retrigger on nearby links.")]
    [SerializeField] float offMeshJumpCooldown = 0.12f;

    readonly Collider[] _detectionHits = new Collider[16];
    readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];

    JailorState _state;
    Transform _target;
    PlayerHealth _targetHealth;
    NetworkPlayerAvatar _carriedAvatar;
    Vector3 _horizontalVelocity;
    Vector3 _verticalVelocity;
    float _intendedMoveSpeed;
    float _smoothedAnimSpeed;
    Vector3 _lastPathDestination;
    Vector3 _lastCarryPathDestination;
    float _nextDestinationRefreshTime;
    bool _hasSpeedParameter = true;
    bool _hasGroundedParameter = true;
    bool _hasVerticalVelocityParameter = true;
    bool _hasGrabTriggerParameter = true;
    bool _hasJumpTriggerParameter = true;
    bool _loggedMissingAnimatorParams;
    bool _loggedMissingJumpTriggerParam;
    const string EnemyLayerName = "Enemy";
    const string JailorLayerName = "Jailor";
    static bool s_HasConfiguredEnemyJailorCollision;

    /// <summary>
    /// Register before any Awake so zombies and jailor never rely on spawn order for layer ignores.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RegisterIgnoreEnemyJailorPhysicsCollision()
    {
        int enemyLayer = LayerMask.NameToLayer(EnemyLayerName);
        int jailorLayer = LayerMask.NameToLayer(JailorLayerName);
        if (enemyLayer < 0 || jailorLayer < 0)
            return;
        Physics.IgnoreLayerCollision(enemyLayer, jailorLayer, true);
        s_HasConfiguredEnemyJailorCollision = true;
    }

    NetworkObject _networkObject;
    NetworkAnimator _networkAnimator;
    NetworkJailorAvatar _networkJailorAvatar;
    int _grabStateHash;
    bool _grabAttachCompleted;
    float _grabbingStartedTime;
    bool _enteredCarrying;
    float _suppressChaseUntil;
    float _carryPhaseStartedTime;
    Vector3 _carryPreservedPlayerLossyScale = Vector3.one;
    bool _isTraversingOffMeshJump;
    Vector3 _offMeshJumpStart;
    Vector3 _offMeshJumpEnd;
    float _offMeshJumpStartedTime;
    float _nextOffMeshJumpAllowedTime;
    bool _hasPatrolDestination;
    Vector3 _patrolDestination;
    float _nextPatrolDestinationRefreshTime;
    float _nextPatrolProgressCheckTime;
    float _patrolPreviousRemainingDistance = float.PositiveInfinity;
    float _patrolStuckAccumulatedTime;
    float _nextPatrolRepathAllowedTime;
    readonly Queue<Vector3> _recentPatrolDestinations = new();
    NavMeshPath _patrolPathScratch;
    Vector3 _investigationPoint;
    bool _hasInvestigationPoint;
    bool _isLingerAtInvestigationPoint;
    float _investigationLingerEndTime;
    bool _hasInvestigationSearchDestination;
    Vector3 _investigationSearchDestination;
    float _chaseLineOfSightLostSince = -1f;
    int _lastFootstepAnimStateHash;
    float _lastFootstepAnimNormalizedTime;
    bool _hasFootstepAnimSample;

    JailDeliveryPhase _jailDeliveryPhase;
    HingeInteractDoor _activeJailDoor;
    bool _jailDropApplied;

    void Reset()
    {
        CacheReferences();
        EnsureEnemyAndJailorLayerSetup();
        ApplyAgentSettings();
        EnsurePatrolPathScratch();
        AutoAssignJailorAudioInEditor();
    }

    void Awake()
    {
        AutoAssignJailorAudioInEditor();
        CacheReferences();
        EnsureEnemyAndJailorLayerSetup();
        ApplyAgentSettings();
        EnsurePatrolPathScratch();
        _grabStateHash = Animator.StringToHash(grabStateName);
    }

    void OnValidate()
    {
        AutoAssignJailorAudioInEditor();
    }
    void EnsurePatrolPathScratch()
    {
        if (_patrolPathScratch == null)
            _patrolPathScratch = new NavMeshPath();
    }


    void Start()
    {
        TryResolveCarryDestinationByObjectName();
        TryResolveJailCellDoorFromDestination();
    }

    void TryResolveCarryDestinationByObjectName()
    {
        if (carryDestination != null || string.IsNullOrWhiteSpace(carryDestinationObjectName))
            return;

        GameObject found = GameObject.Find(carryDestinationObjectName.Trim());
        if (found != null)
        {
            carryDestination = found.transform;
            TryResolveJailCellDoorFromDestination();
        }
    }

    void TryResolveJailCellDoorFromDestination()
    {
        if (jailCellDoor != null || carryDestination == null)
            return;

        jailCellDoor = FindNearestHingeDoorInLocalPrefabHierarchy(carryDestination, carryDestination.position);
    }

    /// <summary>
    /// Resolves a door under the same prefab as <paramref name="anchor"/> by walking parents.
    /// Never uses <see cref="Transform.root"/> + GetComponentsInChildren (that can return the first door in the entire scene, e.g. start room).
    /// </summary>
    internal static HingeInteractDoor FindNearestHingeDoorInLocalPrefabHierarchy(Transform anchor, Vector3 referenceWorldPosition)
    {
        if (anchor == null)
            return null;

        const int maxParentSteps = 14;
        Transform t = anchor;
        for (int depth = 0; depth < maxParentSteps && t != null; depth++)
        {
            HingeInteractDoor[] doors = t.GetComponentsInChildren<HingeInteractDoor>(true);
            if (doors != null && doors.Length > 0)
            {
                HingeInteractDoor best = doors[0];
                float bestSqr = (best.transform.position - referenceWorldPosition).sqrMagnitude;
                for (int i = 1; i < doors.Length; i++)
                {
                    if (doors[i] == null)
                        continue;
                    float sqr = (doors[i].transform.position - referenceWorldPosition).sqrMagnitude;
                    if (sqr < bestSqr)
                    {
                        bestSqr = sqr;
                        best = doors[i];
                    }
                }

                return best;
            }

            t = t.parent;
        }

        return null;
    }

    void ClearInvestigationState()
    {
        _hasInvestigationPoint = false;
        _isLingerAtInvestigationPoint = false;
        _investigationLingerEndTime = 0f;
        _hasInvestigationSearchDestination = false;
        _investigationSearchDestination = Vector3.zero;
    }

    /// <summary>Used by <see cref="JailCellDoorTripwire"/> so the wire does not fire while the Jailor brings a prisoner in or is still in the delivery sequence.</summary>
    public bool BlocksJailDoorTripwire =>
        _state == JailorState.Carrying
        || _state == JailorState.Grabbing
        || _state == JailorState.JailDelivery;

    static bool ShouldJailorIgnorePlayer(PlayerHealth health)
    {
        if (health == null || health.IsDead)
            return false;

        NetworkPlayerAvatar avatar = health.GetComponent<NetworkPlayerAvatar>();
        return avatar != null && avatar.IsSealedInJailCell;
    }

    void OnEnable()
    {
        ServerProximityVoiceNotifications.Register(this);
        TrySnapToNavMesh();
    }

    void LateUpdate()
    {
        if (!ShouldRunSimulation())
            return;
        bool useCarryPose =
            _grabAttachCompleted
            && carryAttach != null
            && _targetHealth != null
            && (_state == JailorState.Carrying
                || (_state == JailorState.JailDelivery && _jailDeliveryPhase == JailDeliveryPhase.OpeningDoor));
        if (!useCarryPose)
            return;

        Transform pt = _targetHealth.transform;
        GetCarriedPlayerWorldPose(out Vector3 worldPos, out Quaternion worldRot);
        pt.SetPositionAndRotation(worldPos, worldRot);
        ApplyDesiredLossyScale(pt, _carryPreservedPlayerLossyScale);
    }

    void OnDisable()
    {
        ServerProximityVoiceNotifications.Unregister(this);
        ReleaseCarriedPlayerIfNeeded();
    }

    void CacheReferences()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (navMeshAgent == null)
            navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();
        _networkObject = GetComponent<NetworkObject>();
        _networkAnimator = GetComponent<NetworkAnimator>();
        _networkJailorAvatar = GetComponent<NetworkJailorAvatar>();
        ConfigureJailorLaughAudioSource();
        ConfigureJailorFootstepAudioSource();

        if (animator != null)
        {
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (Animator other in GetComponentsInChildren<Animator>(true))
            {
                if (other != null && other != animator)
                    other.enabled = false;
            }

            CacheAnimatorParameterAvailability();
        }
    }

    void ConfigureJailorLaughAudioSource()
    {
        if (jailorLaughAudioSource == null)
            jailorLaughAudioSource = EnsureNamedChildAudioSource("JailorLaughAudio");
        if (jailorLaughAudioSource == null)
            jailorLaughAudioSource = gameObject.AddComponent<AudioSource>();

        jailorLaughAudioSource.playOnAwake = false;
        jailorLaughAudioSource.loop = false;
        jailorLaughAudioSource.spatialBlend = 1f;
        jailorLaughAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        jailorLaughAudioSource.minDistance = 2f;
        jailorLaughAudioSource.maxDistance = 24f;
        jailorLaughAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(jailorLaughAudioSource);
    }

    void ConfigureJailorFootstepAudioSource()
    {
        if (jailorFootstepAudioSource == null)
            jailorFootstepAudioSource = EnsureNamedChildAudioSource("JailorFootstepAudio");

        jailorFootstepAudioSource.playOnAwake = false;
        jailorFootstepAudioSource.loop = false;
        jailorFootstepAudioSource.spatialBlend = 1f;
        jailorFootstepAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        jailorFootstepAudioSource.minDistance = 1.5f;
        jailorFootstepAudioSource.maxDistance = 25f;
        jailorFootstepAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(jailorFootstepAudioSource);
    }

    AudioSource EnsureNamedChildAudioSource(string childName)
    {
        if (string.IsNullOrWhiteSpace(childName))
            return null;

        Transform child = transform.Find(childName);
        if (child == null)
        {
            GameObject go = new GameObject(childName);
            go.transform.SetParent(transform, false);
            child = go.transform;
        }

        AudioSource source = child.GetComponent<AudioSource>();
        if (source == null)
            source = child.gameObject.AddComponent<AudioSource>();

        return source;
    }

    void AutoAssignJailorAudioInEditor()
    {
#if UNITY_EDITOR
        if (jailorLaughAudioSource == null)
            jailorLaughAudioSource = EnsureNamedChildAudioSource("JailorLaughAudio");
        if (jailorFootstepAudioSource == null)
            jailorFootstepAudioSource = EnsureNamedChildAudioSource("JailorFootstepAudio");

        if (jailorLaughClip == null)
            jailorLaughClip = FindFirstAudioClipByName("Jailor laugh");

        if (jailorFootstepClip == null)
        {
            string[] expected =
            {
                "JailorFootstep1",
                "Jailorfootstep2",
                "Jailorfootstep3",
                "Jailorfootstep4"
            };

            for (int i = 0; i < expected.Length; i++)
            {
                AudioClip resolved = FindFirstAudioClipByName(expected[i]);
                if (resolved == null)
                    continue;

                jailorFootstepClip = resolved;
                break;
            }
        }
#endif
    }

#if UNITY_EDITOR
    static AudioClip FindFirstAudioClipByName(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName))
            return null;

        string[] guids = AssetDatabase.FindAssets($"{clipName} t:AudioClip");
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null)
                return clip;
        }

        return null;
    }
#endif

    void CacheAnimatorParameterAvailability()
    {
        _hasSpeedParameter = HasAnimatorParameter(speedParameter, AnimatorControllerParameterType.Float);
        _hasGroundedParameter = HasAnimatorParameter(groundedParameter, AnimatorControllerParameterType.Bool);
        _hasVerticalVelocityParameter = HasAnimatorParameter(verticalVelocityParameter, AnimatorControllerParameterType.Float);
        _hasGrabTriggerParameter = HasAnimatorParameter(grabTriggerParameter, AnimatorControllerParameterType.Trigger);
        _hasJumpTriggerParameter = HasAnimatorParameter(jumpTriggerParameter, AnimatorControllerParameterType.Trigger);
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
        // Lower value = higher priority in NavMesh local avoidance — prevents deadlocks vs zombies also at default 50.
        navMeshAgent.avoidancePriority = 12;

        if (characterController != null)
        {
            characterController.skinWidth = 0.02f;
            characterController.minMoveDistance = 0.001f;
        }
    }

    bool ShouldRunSimulation()
    {
        if (_networkObject != null
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

        RecoverNavMeshIfOffMesh();

        RefreshTargetFromSightAndHearing();

        float loseRadius = Mathf.Max(detectionRadius, hearingRadius, voiceHearRadius)
            * Mathf.Max(1f, loseTargetRadiusMultiplier);

        bool carryingOrGrabbing = _state == JailorState.Carrying
            || _state == JailorState.Grabbing
            || _state == JailorState.JailDelivery;
        if (_targetHealth != null
            && !_targetHealth.IsDead
            && ShouldJailorIgnorePlayer(_targetHealth)
            && !carryingOrGrabbing)
            ClearTarget();

        if (_targetHealth != null && !_targetHealth.IsDead && !carryingOrGrabbing)
        {
            if (_state == JailorState.Chase && UpdateChaseLostLineOfSight())
            {
                UpdateAnimatorParameters();
                return;
            }

            float d = Vector3.Distance(transform.position, _target.position);
            if (d > loseRadius)
                ClearTarget();
        }

        Vector3 desiredHorizontal = Vector3.zero;
        if (_state == JailorState.JailDelivery)
        {
            desiredHorizontal = UpdateJailDelivery();
        }
        else if (_targetHealth != null && !_targetHealth.IsDead)
        {
            switch (_state)
            {
                case JailorState.Idle:
                case JailorState.Patrol:
                case JailorState.Investigating:
                    EnterChase();
                    desiredHorizontal = TryGrabOrChase();
                    break;
                case JailorState.Chase:
                    desiredHorizontal = TryGrabOrChase();
                    break;
                case JailorState.Grabbing:
                    desiredHorizontal = UpdateGrabbing();
                    break;
                case JailorState.Carrying:
                    desiredHorizontal = UpdateCarrying();
                    break;
            }
        }
        else
        {
            if (_hasInvestigationPoint)
            {
                EnterInvestigating();
                desiredHorizontal = UpdateInvestigating();
            }
            else
            {
                EnterPatrol();
                desiredHorizontal = UpdatePatrol();
            }
        }

        bool handlingOffMeshJump = TryHandleOffMeshJump(ref desiredHorizontal);
        if (!handlingOffMeshJump)
            ApplyMovement(desiredHorizontal);

        HandleJailorFootsteps();
        UpdateAnimatorParameters();
    }

    void HandleJailorFootsteps()
    {
        if (jailorFootstepAudioSource == null || characterController == null || animator == null)
            return;
        if (_state == JailorState.Idle || _state == JailorState.Grabbing || _state == JailorState.JailDelivery)
        {
            _hasFootstepAnimSample = false;
            return;
        }
        if (!characterController.isGrounded || _isTraversingOffMeshJump)
        {
            _hasFootstepAnimSample = false;
            return;
        }

        float horizontalSpeed = new Vector3(_horizontalVelocity.x, 0f, _horizontalVelocity.z).magnitude;
        if (horizontalSpeed < Mathf.Max(0.01f, jailorMinFootstepMoveSpeed))
        {
            _hasFootstepAnimSample = false;
            return;
        }

        AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);
        int stateHash = stateInfo.shortNameHash;
        float normalizedTime = stateInfo.normalizedTime;

        if (!_hasFootstepAnimSample || stateHash != _lastFootstepAnimStateHash)
        {
            _lastFootstepAnimStateHash = stateHash;
            _lastFootstepAnimNormalizedTime = normalizedTime;
            _hasFootstepAnimSample = true;
            return;
        }

        bool isRunningStep = _intendedMoveSpeed > walkSpeed + 0.05f;
        Vector2 phases = isRunningStep ? jailorRunFootstepPhases : jailorWalkFootstepPhases;

        bool hitFirst = DidCrossFootstepPhase(_lastFootstepAnimNormalizedTime, normalizedTime, phases.x);
        bool hitSecond = DidCrossFootstepPhase(_lastFootstepAnimNormalizedTime, normalizedTime, phases.y);
        bool shouldPlay = hitFirst || hitSecond;

        _lastFootstepAnimStateHash = stateHash;
        _lastFootstepAnimNormalizedTime = normalizedTime;
        if (!shouldPlay)
            return;

        NotifyFootstepSfx();
    }

    static bool DidCrossFootstepPhase(float previousNormalizedTime, float currentNormalizedTime, float phase)
    {
        float p = Mathf.Repeat(phase, 1f);
        float prev = Mathf.Repeat(previousNormalizedTime, 1f);
        float curr = Mathf.Repeat(currentNormalizedTime, 1f);

        if (Mathf.Abs(currentNormalizedTime - previousNormalizedTime) > 1f)
            return true;

        if (curr >= prev)
            return p > prev && p <= curr;

        return p > prev || p <= curr;
    }

    void NotifyFootstepSfx()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (_networkJailorAvatar != null
            && nm != null
            && nm.IsListening
            && _networkObject != null
            && _networkObject.IsSpawned)
        {
            _networkJailorAvatar.PlayFootstepSfxForObservers();
            return;
        }

        PlayFootstepSfxLocal();
    }

    public void PlayFootstepSfxLocal()
    {
        if (jailorFootstepAudioSource == null || jailorFootstepClip == null)
            return;

        jailorFootstepAudioSource.PlayOneShot(jailorFootstepClip, Mathf.Clamp01(jailorFootstepVolume));
    }

    bool TryHandleOffMeshJump(ref Vector3 desiredHorizontal)
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || !navMeshAgent.isOnNavMesh)
            return false;

        if (_state != JailorState.Chase && _state != JailorState.Carrying && _state != JailorState.Patrol)
            return false;

        if (_isTraversingOffMeshJump)
        {
            desiredHorizontal = UpdateOffMeshJumpTraversal();
            return true;
        }

        if (Time.time < _nextOffMeshJumpAllowedTime || !navMeshAgent.isOnOffMeshLink)
            return false;

        OffMeshLinkData linkData = navMeshAgent.currentOffMeshLinkData;
        Vector3 start = transform.position;
        Vector3 end = linkData.endPos;
        if ((end - start).sqrMagnitude <= 0.0004f)
        {
            navMeshAgent.CompleteOffMeshLink();
            return false;
        }

        BeginOffMeshJump(start, end);
        desiredHorizontal = UpdateOffMeshJumpTraversal();
        return true;
    }

    void BeginOffMeshJump(Vector3 start, Vector3 end)
    {
        _isTraversingOffMeshJump = true;
        _offMeshJumpStart = start;
        _offMeshJumpEnd = end;
        _offMeshJumpStartedTime = Time.time;
        _verticalVelocity.y = 0f;

        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
            navMeshAgent.isStopped = true;

        TriggerJumpAnimation();
    }

    void TriggerJumpAnimation()
    {
        if (!_hasJumpTriggerParameter || animator == null)
            return;

        if (_networkAnimator != null
            && _networkObject != null
            && _networkObject.IsSpawned)
            _networkAnimator.SetTrigger(jumpTriggerParameter);
        else
            animator.SetTrigger(jumpTriggerParameter);
    }

    Vector3 UpdateOffMeshJumpTraversal()
    {
        if (!_isTraversingOffMeshJump || characterController == null)
            return Vector3.zero;

        float duration = Mathf.Max(0.1f, offMeshJumpDuration);
        float elapsed = Mathf.Max(0f, Time.time - _offMeshJumpStartedTime);
        float t = Mathf.Clamp01(elapsed / duration);

        Vector3 target = Vector3.Lerp(_offMeshJumpStart, _offMeshJumpEnd, t);
        target.y += Mathf.Sin(t * Mathf.PI) * Mathf.Max(0f, offMeshJumpArcHeight);

        Vector3 delta = target - transform.position;
        characterController.Move(delta);

        if (navMeshAgent != null && navMeshAgent.enabled)
            navMeshAgent.nextPosition = transform.position;

        bool reachedByTime = t >= 1f;
        bool reachedByDistance = Vector3.Distance(transform.position, _offMeshJumpEnd)
            <= Mathf.Max(0.05f, offMeshJumpLandingSnapDistance);
        if (reachedByTime || reachedByDistance)
            CompleteOffMeshJumpTraversal();

        Vector3 horizontal = _offMeshJumpEnd - _offMeshJumpStart;
        horizontal.y = 0f;
        if (horizontal.sqrMagnitude <= 0.0001f)
            return Vector3.zero;

        float horizontalSpeed = horizontal.magnitude / duration;
        return horizontal.normalized * horizontalSpeed;
    }

    void CompleteOffMeshJumpTraversal()
    {
        if (!_isTraversingOffMeshJump)
            return;

        _isTraversingOffMeshJump = false;

        Vector3 finalPosition = _offMeshJumpEnd;
        if (NavMesh.SamplePosition(_offMeshJumpEnd, out NavMeshHit hit, Mathf.Max(0.5f, offMeshJumpLandingSnapDistance), NavMesh.AllAreas))
            finalPosition = hit.position;

        transform.position = finalPosition;
        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.Warp(finalPosition);
            if (navMeshAgent.isOnOffMeshLink)
                navMeshAgent.CompleteOffMeshLink();
            navMeshAgent.isStopped = false;
        }

        _nextOffMeshJumpAllowedTime = Time.time + Mathf.Max(0f, offMeshJumpCooldown);
    }

    Vector3 TryGrabOrChase()
    {
        if (_target == null || _targetHealth == null)
            return Vector3.zero;

        if (_state == JailorState.Chase && ShouldStartGrab())
        {
            EnterGrabbing();
            return Vector3.zero;
        }

        return UpdateChase();
    }

    void EnterPatrol()
    {
        if (_state != JailorState.Patrol)
            _state = JailorState.Patrol;

        _intendedMoveSpeed = patrolSpeed;
        if (!TrySnapToNavMesh())
            return;

        if (navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = patrolSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.2f, patrolArrivalDistance * 0.8f);

        if (!_hasPatrolDestination)
            TrySetNextPatrolDestination();
    }

    Vector3 UpdatePatrol()
    {
        _intendedMoveSpeed = patrolSpeed;
        if (!TrySnapToNavMesh() || navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return Vector3.zero;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = patrolSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.2f, patrolArrivalDistance * 0.8f);

        if (!_hasPatrolDestination)
        {
            TrySetNextPatrolDestination();
            return Vector3.zero;
        }

        bool shouldRefreshDestination = Time.time >= _nextPatrolDestinationRefreshTime;
        if (shouldRefreshDestination)
        {
            navMeshAgent.SetDestination(_patrolDestination);
            _nextPatrolDestinationRefreshTime = Time.time + Mathf.Max(0.1f, patrolDestinationRefreshInterval);
        }

        if (!navMeshAgent.pathPending)
        {
            if (!navMeshAgent.hasPath
                || navMeshAgent.pathStatus == NavMeshPathStatus.PathInvalid
                || navMeshAgent.pathStatus == NavMeshPathStatus.PathPartial)
            {
                if (Time.time >= _nextPatrolRepathAllowedTime)
                {
                    _nextPatrolRepathAllowedTime = Time.time + Mathf.Max(0.1f, patrolRepathCooldown);
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
            else if (Time.time >= _nextPatrolProgressCheckTime)
            {
                float remainingDistance = navMeshAgent.remainingDistance;
                float speed = navMeshAgent.velocity.magnitude;
                float gainedDistance = _patrolPreviousRemainingDistance - remainingDistance;
                if (speed <= patrolStuckVelocityThreshold && gainedDistance < patrolMinProgressDistance)
                    _patrolStuckAccumulatedTime += Mathf.Max(0.05f, patrolProgressCheckInterval);
                else
                    _patrolStuckAccumulatedTime = 0f;

                _patrolPreviousRemainingDistance = remainingDistance;
                _nextPatrolProgressCheckTime = Time.time + Mathf.Max(0.05f, patrolProgressCheckInterval);

                if (_patrolStuckAccumulatedTime >= patrolStuckSeconds
                    && Time.time >= _nextPatrolRepathAllowedTime)
                {
                    _nextPatrolRepathAllowedTime = Time.time + Mathf.Max(0.1f, patrolRepathCooldown);
                    _patrolStuckAccumulatedTime = 0f;
                    TrySetNextPatrolDestination();
                }
            }
        }

        Vector3 desiredVelocity = navMeshAgent.velocity.sqrMagnitude > 0.0001f
            ? navMeshAgent.velocity
            : navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        if (desiredVelocity.sqrMagnitude > patrolSpeed * patrolSpeed)
            desiredVelocity = desiredVelocity.normalized * patrolSpeed;

        return desiredVelocity;
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
        _nextPatrolDestinationRefreshTime = Time.time + Mathf.Max(0.1f, patrolDestinationRefreshInterval);
        _nextPatrolProgressCheckTime = Time.time + Mathf.Max(0.05f, patrolProgressCheckInterval);
        _patrolPreviousRemainingDistance = float.PositiveInfinity;
        _patrolStuckAccumulatedTime = 0f;
        return true;
    }

    bool TryPickPatrolDestination(out Vector3 destination)
    {
        destination = transform.position;
        Vector3 origin = transform.position;
        Vector3 flatForward = transform.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.0001f)
            flatForward = Vector3.forward;
        else
            flatForward.Normalize();

        float minDistance = Mathf.Max(1f, patrolMinWaypointDistance);
        float maxDistance = Mathf.Max(minDistance + 1f, patrolMaxWaypointDistance);
        int attempts = Mathf.Max(4, patrolSampleAttempts);
        float sampleRadius = Mathf.Max(1.5f, maxDistance * 0.7f);

        Vector3 bestCandidate = Vector3.zero;
        float bestScore = float.MinValue;
        bool found = false;

        for (int i = 0; i < attempts; i++)
        {
            Vector2 random2 = Random.insideUnitCircle;
            Vector3 randomDir = new Vector3(random2.x, 0f, random2.y);
            if (randomDir.sqrMagnitude < 0.0001f)
                randomDir = flatForward;
            randomDir.Normalize();

            float forwardBias = Random.Range(0.35f, 0.8f);
            Vector3 biasedDir = (flatForward * forwardBias + randomDir * (1f - forwardBias)).normalized;
            float distance = Random.Range(minDistance, maxDistance);
            Vector3 rawCandidate = origin + biasedDir * distance;

            if (!NavMesh.SamplePosition(rawCandidate, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas))
                continue;

            Vector3 flatTo = hit.position - origin;
            flatTo.y = 0f;
            float flatDistance = flatTo.magnitude;
            if (flatDistance < minDistance * 0.55f)
                continue;

            bool shouldAvoidRecent = i < attempts - 3;
            if (shouldAvoidRecent && IsNearRecentPatrolDestination(hit.position))
                continue;

            if (!TryHasReasonablePatrolPath(hit.position))
                continue;

            float directionalScore = flatDistance > 0.01f
                ? Vector3.Dot(flatForward, flatTo / flatDistance)
                : 0f;
            float score = flatDistance + directionalScore * 2.5f;
            if (!found || score > bestScore)
            {
                found = true;
                bestScore = score;
                bestCandidate = hit.position;
            }
        }

        if (!found)
            return false;

        destination = bestCandidate;
        return true;
    }

    bool TryHasReasonablePatrolPath(Vector3 destination)
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || !navMeshAgent.isOnNavMesh)
            return false;
        EnsurePatrolPathScratch();

        if (!NavMesh.CalculatePath(transform.position, destination, NavMesh.AllAreas, _patrolPathScratch))
            return false;

        return _patrolPathScratch.status == NavMeshPathStatus.PathComplete
            && _patrolPathScratch.corners != null
            && _patrolPathScratch.corners.Length >= 2;
    }

    bool IsNearRecentPatrolDestination(Vector3 candidate)
    {
        if (_recentPatrolDestinations.Count == 0)
            return false;

        float radiusSqr = patrolRecentDestinationRadius * patrolRecentDestinationRadius;
        foreach (Vector3 recent in _recentPatrolDestinations)
        {
            Vector3 delta = candidate - recent;
            delta.y = 0f;
            if (delta.sqrMagnitude <= radiusSqr)
                return true;
        }

        return false;
    }

    void RememberPatrolDestination(Vector3 destination)
    {
        int maxMemory = Mathf.Max(1, patrolRecentDestinationMemory);
        while (_recentPatrolDestinations.Count >= maxMemory)
            _recentPatrolDestinations.Dequeue();
        _recentPatrolDestinations.Enqueue(destination);
    }

    bool ShouldStartGrab()
    {
        if (Time.time < _suppressChaseUntil)
            return false;

        if (carryAttach == null)
            return false;

        if (_targetHealth != null && ShouldJailorIgnorePlayer(_targetHealth))
            return false;

        Vector3 to = _target.position - transform.position;
        if (Mathf.Abs(to.y) > Mathf.Max(0.1f, maxGrabVerticalDelta))
            return false;

        Vector3 horizontalToTarget = new Vector3(to.x, 0f, to.z);
        float horizontalDist = horizontalToTarget.magnitude;
        if (horizontalDist > grabRange + Mathf.Max(0f, grabHitRangePadding))
            return false;

        // Forward cone only (matches Zombie swipe angle check vs committed facing).
        if (horizontalDist > 0.001f)
        {
            Vector3 flatFwd = new Vector3(transform.forward.x, 0f, transform.forward.z);
            if (flatFwd.sqrMagnitude < 0.0001f)
                return false;
            flatFwd.Normalize();

            float grabAngle = Vector3.Angle(flatFwd, horizontalToTarget / horizontalDist);
            if (grabAngle > grabHitHalfAngle)
                return false;
        }

        return true;
    }

    void EnterGrabbing()
    {
        _state = JailorState.Grabbing;
        _grabAttachCompleted = false;
        _enteredCarrying = false;
        _grabbingStartedTime = Time.time;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        Vector3 to = _target.position - transform.position;
        to.y = 0f;
        if (to.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(to.normalized);
            transform.rotation = targetRotation;
        }

        if (_hasGrabTriggerParameter && animator != null)
        {
            // NetworkAnimator only allows SetTrigger while the NetworkObject is spawned.
            if (_networkAnimator != null
                && _networkObject != null
                && _networkObject.IsSpawned)
                _networkAnimator.SetTrigger(grabTriggerParameter);
            else
                animator.SetTrigger(grabTriggerParameter);
        }
    }

    Vector3 UpdateGrabbing()
    {
        // Only face the free-standing player before the attach. After TryAttachCarriedPlayer, the
        // target is a child of this jailor, so the vector to _target orbits as we rotate the root
        // and causes a one-frame/short spin. Grab animation can handle upper-body pose.
        if (!_grabAttachCompleted)
        {
            Vector3 to = _target.position - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude > 0.0001f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(to.normalized);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    targetRotation,
                    rotationSpeed * Time.deltaTime);
            }
        }

        if (!_grabAttachCompleted && Time.time >= _grabbingStartedTime + attachFallbackDelay)
            TryAttachCarriedPlayer();

        if (_grabAttachCompleted && ShouldLeaveGrabAnimation())
            EnterCarrying();

        if (!_grabAttachCompleted && Time.time >= _grabbingStartedTime + grabClipDurationFallback + 0.5f)
            TryAttachCarriedPlayer();

        return Vector3.zero;
    }

    bool ShouldLeaveGrabAnimation()
    {
        if (animator == null)
            return Time.time >= _grabbingStartedTime + grabClipDurationFallback;

        AnimatorStateInfo si = animator.GetCurrentAnimatorStateInfo(0);
        if (si.shortNameHash == _grabStateHash)
            return si.normalizedTime >= 0.98f;

        return Time.time >= _grabbingStartedTime + grabClipDurationFallback * 0.85f;
    }

    /// <summary>Animation event from Grab Player clip (hands reach).</summary>
    public void OnGrabAttachPoint()
    {
        TryAttachCarriedPlayer();
    }

    void TryAttachCarriedPlayer()
    {
        if (!ShouldRunSimulation())
            return;
        if (_grabAttachCompleted || _targetHealth == null || _targetHealth.IsDead)
            return;
        if (carryAttach == null)
            return;

        NetworkObject playerNo = _targetHealth.GetComponent<NetworkObject>();
        if (playerNo == null)
            return;

        _carriedAvatar = _targetHealth.GetComponent<NetworkPlayerAvatar>();

        Transform pt = _targetHealth.transform;
        Vector3 preservedLossyScale = pt.lossyScale;
        _carryPreservedPlayerLossyScale = preservedLossyScale;

        GetCarriedPlayerWorldPose(out Vector3 attachWorldPosition, out Quaternion attachWorldRotation);

        // NGO: a spawned NetworkObject must never be parented under a plain Transform (e.g. CarryAttach).
        // That triggers OnTransformParentChanged → invalid parent handling and can throw inside Netcode
        // (e.g. when NetworkManager is null in the same branch) or break replication / local view.
        // Always use TrySetParent(another spawned NetworkObject) for networked players.
        NetworkManager nm = NetworkManager.Singleton;
        bool playerSpawned = playerNo.IsSpawned;
        bool jailorSpawned = _networkObject != null && _networkObject.IsSpawned;

        if (playerSpawned && jailorSpawned && nm != null)
        {
            // Before parenting: replicated carry flag lets OwnerNetworkTransform switch to server authority so the
            // owning client's deltas don't fight TrySetParent / attach pose on observers.
            if (_carriedAvatar != null)
                _carriedAvatar.ServerSetCarriedByJailor(true);

            if (playerNo.TrySetParent(_networkObject, true))
                pt.SetPositionAndRotation(attachWorldPosition, attachWorldRotation);
            else
            {
                pt.SetPositionAndRotation(attachWorldPosition, attachWorldRotation);
                Debug.LogWarning(
                    $"[{nameof(JailorAI)}] Could not NetworkObject.TrySetParent player under Jailor "
                    + "(IsListening, AutoObjectParentSync, or spawn state). World pose snap only; carry may fight OwnerNetworkTransform.",
                    this);
            }

            ApplyDesiredLossyScale(pt, preservedLossyScale);
            _grabAttachCompleted = true;
            NotifyPickupLaughSfx();
            TrySpawnJailorKeyOnPlayerPickup();

            return;
        }

        // Offline / non-network NPC body: no NetworkObject spawn — safe to parent to the attach bone.
        if (!playerSpawned)
        {
            Quaternion localRot = Quaternion.Euler(carriedPlayerLocalEulerOffset);
            pt.SetParent(carryAttach, false);
            pt.localPosition = carriedPlayerLocalPositionOffset - localRot * playerRootToCarryAlignPointLocal;
            pt.localRotation = localRot;
            ApplyDesiredLossyScale(pt, preservedLossyScale);
            _grabAttachCompleted = true;
            NotifyPickupLaughSfx();
            TrySpawnJailorKeyOnPlayerPickup();

            if (_carriedAvatar != null)
                _carriedAvatar.ServerSetCarriedByJailor(true);

            return;
        }

        Debug.LogError(
            $"[{nameof(JailorAI)}] Cannot attach networked player: Jailor must be a spawned NetworkObject "
            + $"(jailorSpawned={jailorSpawned}, nm={(nm != null)}). Do not use an unregistered or non-spawned Jailor in a net session.",
            this);
    }

    void TrySpawnJailorKeyOnPlayerPickup()
    {
        if (jailorKeyWorldPrefab == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool inNetSession = nm != null && nm.IsListening;
        if (inNetSession && !nm.IsServer)
            return;

        Vector3 pos = transform.TransformPoint(jailorKeyDropLocalOffset);
        Quaternion rot = transform.rotation;
        GameObject go = Instantiate(jailorKeyWorldPrefab, pos, rot);
        SceneManager.MoveGameObjectToScene(go, gameObject.scene);

        if (inNetSession && go.TryGetComponent(out NetworkObject no))
            no.Spawn();
    }

    void NotifyPickupLaughSfx()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (_networkJailorAvatar != null
            && nm != null
            && nm.IsListening
            && _networkObject != null
            && _networkObject.IsSpawned)
        {
            _networkJailorAvatar.PlayPickupLaughSfxForObservers();
            return;
        }

        PlayPickupLaughSfxLocal();
    }

    public void PlayPickupLaughSfxLocal()
    {
        if (jailorLaughClip == null || jailorLaughAudioSource == null)
            return;

        jailorLaughAudioSource.PlayOneShot(jailorLaughClip, Mathf.Clamp01(jailorLaughVolume));
    }

    void EnterCarrying()
    {
        if (_enteredCarrying)
            return;
        _enteredCarrying = true;
        _state = JailorState.Carrying;
        _lastCarryPathDestination = Vector3.zero;
        _carryPhaseStartedTime = Time.time;

        if (!TrySnapToNavMesh())
            return;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = false;
            navMeshAgent.speed = carrySpeed;
            navMeshAgent.stoppingDistance = carryStoppingDistance;
        }

        if (!TryGetCarryDestination(out Vector3 dest))
            return;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
            navMeshAgent.SetDestination(dest);

        _lastCarryPathDestination = dest;
        _nextDestinationRefreshTime = Time.time + destinationRefreshInterval;
    }

    Vector3 UpdateCarrying()
    {
        if (_target == null || _targetHealth == null || !_grabAttachCompleted)
            return Vector3.zero;

        _intendedMoveSpeed = carrySpeed;

        if (!TrySnapToNavMesh())
            return Vector3.zero;

        if (!TryGetCarryDestination(out Vector3 destination))
            return Vector3.zero;

        TryResolveJailCellDoorFromDestination();

        Vector3 flatSelf = transform.position;
        flatSelf.y = 0f;
        if (jailCellDoor != null && !jailCellDoor.IsOpen && carryDestination != null)
        {
            Vector3 flatDoor = jailCellDoor.IdentityHintPosition;
            flatDoor.y = 0f;
            if (Vector3.Distance(flatSelf, flatDoor) <= jailDoorPremptiveOpenDistance)
                jailCellDoor.ServerJailorOpenForEntry();
        }

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = carrySpeed;
        navMeshAgent.stoppingDistance = carryStoppingDistance;

        bool shouldRefresh =
            Time.time >= _nextDestinationRefreshTime
            || (destination - _lastCarryPathDestination).sqrMagnitude
                >= destinationRefreshMinDistance * destinationRefreshMinDistance;

        if (shouldRefresh)
        {
            if (navMeshAgent.SetDestination(destination))
            {
                _lastCarryPathDestination = destination;
                _nextDestinationRefreshTime = Time.time + Mathf.Max(0.02f, destinationRefreshInterval);
            }
        }

        Vector3 flatDest = destination;
        flatDest.y = 0f;
        float horizToPathDest = Vector3.Distance(flatSelf, flatDest);
        float horizToMarker = 0f;
        if (carryDestination != null)
        {
            Vector3 fm = carryDestination.position;
            fm.y = 0f;
            horizToMarker = Vector3.Distance(flatSelf, fm);
        }

        bool pathNearlyDone = navMeshAgent.hasPath
            && !navMeshAgent.pathPending
            && (navMeshAgent.remainingDistance <= carryStoppingDistance + 0.35f
                || navMeshAgent.pathStatus == UnityEngine.AI.NavMeshPathStatus.PathComplete
                    && navMeshAgent.remainingDistance <= carryArrivalDistance + 0.5f);

        bool closeEnough = horizToPathDest <= carryArrivalDistance
            || (carryDestination != null && horizToMarker <= carryArrivalDistance * 1.35f)
            || pathNearlyDone;

        if (closeEnough
            && Time.time >= _carryPhaseStartedTime + minCarrySecondsBeforeDrop)
        {
            if (jailCellDoor != null)
                EnterJailDelivery(jailCellDoor);
            else
                CompleteCarryAndDrop();
            return Vector3.zero;
        }

        Vector3 desiredVelocity = navMeshAgent.velocity.sqrMagnitude > 0.0001f
            ? navMeshAgent.velocity
            : navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        if (desiredVelocity.sqrMagnitude > carrySpeed * carrySpeed)
            desiredVelocity = desiredVelocity.normalized * carrySpeed;

        return desiredVelocity;
    }

    bool TryGetCarryDestination(out Vector3 destination)
    {
        destination = Vector3.zero;
        Vector3 raw = carryDestination != null
            ? carryDestination.position
            : transform.position + transform.forward * carryDestinationForwardFallback;

        // Expand search so we don't fall back to raw off-mesh positions (SetDestination then snaps paths to odd corners).
        float[] radii =
        {
            Mathf.Max(0.5f, carryDestinationSampleRadius),
            carryDestinationSampleRadius * 2f,
            Mathf.Min(carryDestinationNavSnapMaxDistance, 8f),
            Mathf.Min(carryDestinationNavSnapMaxDistance, 16f),
            carryDestinationNavSnapMaxDistance
        };

        NavMeshHit bestHit = default;
        bool haveHit = false;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < radii.Length; i++)
        {
            if (!NavMesh.SamplePosition(raw, out NavMeshHit hit, radii[i], NavMesh.AllAreas))
                continue;
            float sqr = (hit.position - raw).sqrMagnitude;
            if (!haveHit || sqr < bestSqr)
            {
                bestSqr = sqr;
                bestHit = hit;
                haveHit = true;
            }
        }

        if (haveHit)
        {
            destination = bestHit.position;
            return true;
        }

        destination = raw;
        return true;
    }

    void CompleteCarryAndDrop()
    {
        ApplyCarryDropRelease(true);
        EnterPatrol();
    }

    /// <summary>Release the carried player at the jail marker without resuming patrol (jail door sequence continues).</summary>
    /// <param name="sealDroppedPlayerAsJailPrisoner">If true, marks the dropped player as sealed in jail immediately so AI does not re-grab before the door tripwire closes.</param>
    void ApplyCarryDropRelease(bool applyPostDropChaseCooldown = true, bool sealDroppedPlayerAsJailPrisoner = false)
    {
        ClearInvestigationState();
        PlayerHealth ph = _targetHealth;
        NetworkObject playerNo = ph != null ? ph.GetComponent<NetworkObject>() : null;
        Transform pt = ph != null ? ph.transform : null;
        Vector3 restoreScale = _carryPreservedPlayerLossyScale.sqrMagnitude > 1e-8f
            ? _carryPreservedPlayerLossyScale
            : (pt != null ? pt.lossyScale : Vector3.one);

        if (ph != null)
        {
            if (TryGetSafeDropPosition(ph, out Vector3 safeDrop))
                ph.transform.position = safeDrop;
            else
                ph.transform.position = carryDestination != null ? carryDestination.position : transform.position;
        }

        if (playerNo != null)
            playerNo.TryRemoveParent(true);

        if (pt != null)
            ApplyDesiredLossyScale(pt, restoreScale);

        if (_carriedAvatar != null)
        {
            _carriedAvatar.ServerSetCarriedByJailor(false);
            _carriedAvatar = null;
        }

        if (sealDroppedPlayerAsJailPrisoner && ph != null)
        {
            NetworkPlayerAvatar sealAvatar = ph.GetComponent<NetworkPlayerAvatar>();
            if (sealAvatar != null)
                sealAvatar.ServerSetSealedInJailCell(true);
        }

        _carryPreservedPlayerLossyScale = Vector3.one;
        if (applyPostDropChaseCooldown)
            _suppressChaseUntil = Time.time + Mathf.Max(0.5f, postDropChaseCooldownSeconds);
        _grabAttachCompleted = false;
        _enteredCarrying = false;
        ClearTarget();
    }

    void EnterJailDelivery(HingeInteractDoor door)
    {
        if (door == null)
        {
            CompleteCarryAndDrop();
            return;
        }

        ClearInvestigationState();
        _state = JailorState.JailDelivery;
        _activeJailDoor = door;
        _jailDeliveryPhase = JailDeliveryPhase.OpeningDoor;
        _jailDropApplied = false;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
        {
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
        }

        Vector3 to = carryDestination != null
            ? carryDestination.position - transform.position
            : Vector3.zero;
        to.y = 0f;
        if (to.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(to.normalized);
            transform.rotation = targetRotation;
        }
    }

    Vector3 UpdateJailDelivery()
    {
        if (_activeJailDoor == null)
        {
            FinishJailDeliveryAndPatrol();
            return Vector3.zero;
        }

        switch (_jailDeliveryPhase)
        {
            case JailDeliveryPhase.OpeningDoor:
                _activeJailDoor.ServerJailorOpenForEntry();
                if (_activeJailDoor.IsOpen)
                    _jailDeliveryPhase = JailDeliveryPhase.DroppingPlayer;
                break;

            case JailDeliveryPhase.DroppingPlayer:
                if (!_jailDropApplied)
                {
                    ApplyCarryDropRelease(false, sealDroppedPlayerAsJailPrisoner: true);
                    _jailDropApplied = true;
                    if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
                    {
                        navMeshAgent.isStopped = false;
                        navMeshAgent.ResetPath();
                    }

                    FinishJailDeliveryAndPatrol();
                }

                break;
        }

        _intendedMoveSpeed = 0f;
        return Vector3.zero;
    }

    void FinishJailDeliveryAndPatrol()
    {
        ClearInvestigationState();
        _activeJailDoor = null;
        _jailDropApplied = false;
        EnterPatrol();
    }

    bool TryGetSafeDropPosition(PlayerHealth playerHealth, out Vector3 dropPosition)
    {
        dropPosition = transform.position;
        if (playerHealth == null)
            return false;

        Vector3 desired = carryDestination != null
            ? carryDestination.position
            : transform.position;

        if (NavMesh.SamplePosition(desired, out NavMeshHit navHit, carryDestinationNavSnapMaxDistance, NavMesh.AllAreas))
            desired = navHit.position;

        CharacterController playerController = playerHealth.GetComponent<CharacterController>();
        float minLift = 0.08f;
        float pivotLift = minLift;
        if (playerController != null)
        {
            float bottomLocalY = playerController.center.y - playerController.height * 0.5f;
            float skin = Mathf.Max(0.01f, playerController.skinWidth);
            pivotLift = Mathf.Max(minLift, -bottomLocalY + skin + 0.02f);
        }

        Vector3 rayStart = desired + Vector3.up * Mathf.Max(2f, pivotLift + 1.5f);
        float rayDistance = Mathf.Max(6f, pivotLift + 4f);
        int rayMask = Physics.DefaultRaycastLayers;
        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit groundHit, rayDistance, rayMask, QueryTriggerInteraction.Ignore))
        {
            dropPosition = new Vector3(desired.x, groundHit.point.y + pivotLift, desired.z);
            return true;
        }

        dropPosition = desired + Vector3.up * pivotLift;
        return true;
    }

    void ReleaseCarriedPlayerIfNeeded()
    {
        if (!ShouldRunSimulation())
            return;
        if (_state != JailorState.Carrying
            && _state != JailorState.Grabbing
            && _state != JailorState.JailDelivery)
            return;

        PlayerHealth ph = _targetHealth;
        if (ph == null)
            return;

        NetworkObject playerNo = ph.GetComponent<NetworkObject>();
        if (playerNo != null)
            playerNo.TryRemoveParent(true);

        Transform pt = ph.transform;
        if (pt != null && _carryPreservedPlayerLossyScale.sqrMagnitude > 1e-8f)
            ApplyDesiredLossyScale(pt, _carryPreservedPlayerLossyScale);

        if (_carriedAvatar != null)
        {
            _carriedAvatar.ServerSetCarriedByJailor(false);
            _carriedAvatar = null;
        }

        _carryPreservedPlayerLossyScale = Vector3.one;
        _suppressChaseUntil = Time.time + Mathf.Max(0.5f, postDropChaseCooldownSeconds);
        _grabAttachCompleted = false;
        _enteredCarrying = false;
        ClearTarget();
        _state = JailorState.Idle;
    }

    void GetCarriedPlayerWorldPose(out Vector3 worldPosition, out Quaternion worldRotation)
    {
        if (carryAttach == null)
        {
            worldPosition = transform.position;
            worldRotation = transform.rotation;
            return;
        }

        Quaternion localOffset = Quaternion.Euler(carriedPlayerLocalEulerOffset);
        worldRotation = carryAttach.rotation * localOffset;
        Vector3 nudge = carryAttach.rotation * carriedPlayerLocalPositionOffset;
        // Root follows feet; without this, feet sit on CarryAttach. Subtract so e.g. chest lands on CarryAttach.
        worldPosition = carryAttach.position + nudge - worldRotation * playerRootToCarryAlignPointLocal;
    }

    /// <summary>
    /// Keeps the same visual size when parenting under a scaled hierarchy (avoids compounding localScale).
    /// </summary>
    static void ApplyDesiredLossyScale(Transform t, Vector3 desiredLossyScale)
    {
        if (t == null)
            return;

        Transform p = t.parent;
        if (p == null)
        {
            t.localScale = desiredLossyScale;
            return;
        }

        Vector3 pl = p.lossyScale;
        t.localScale = new Vector3(
            desiredLossyScale.x / Mathf.Max(Mathf.Abs(pl.x), 1e-6f),
            desiredLossyScale.y / Mathf.Max(Mathf.Abs(pl.y), 1e-6f),
            desiredLossyScale.z / Mathf.Max(Mathf.Abs(pl.z), 1e-6f));
    }

    public void OnServerHeardVoiceFrame(ulong speakerClientId)
    {
        if (!ShouldRunSimulation())
            return;

        if (!VoiceClientRegistry.TryGet(speakerClientId, out NetworkPlayerVoice voice)
            || voice == null)
            return;

        PlayerHealth health = voice.GetComponentInParent<PlayerHealth>();
        if (health == null || health.IsDead || ShouldJailorIgnorePlayer(health))
            return;

        float d = Vector3.Distance(transform.position, voice.transform.position);
        if (d > voiceHearRadius)
            return;

        if (Time.time < _suppressChaseUntil)
            return;

        SetInvestigationPoint(voice.transform.position);
    }

    void RefreshTargetFromSightAndHearing()
    {
        if (_targetHealth != null && !_targetHealth.IsDead)
            return;

        if (Time.time < _suppressChaseUntil)
            return;

        PlayerHealth bestSeen = null;
        float bestSeenScore = float.MaxValue;
        Vector3 bestSoundPoint = Vector3.zero;
        float bestSoundScore = float.MaxValue;
        bool hasSoundPoint = false;

        int mask = detectionMask.value == 0 ? Physics.DefaultRaycastLayers : detectionMask.value;
        int hitCount = Physics.OverlapSphereNonAlloc(
            transform.position,
            Mathf.Max(detectionRadius, Mathf.Max(hearingRadius, Mathf.Max(voiceHearRadius, zombieNoiseHearRadius))),
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
            if (candidate != null && !candidate.IsDead && !ShouldJailorIgnorePlayer(candidate))
            {
                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                bool seen = distance <= detectionRadius
                    && (!requireDetectionLineOfSight || HasDetectionLineOfSight(candidate));
                if (seen && distance < bestSeenScore)
                {
                    bestSeenScore = distance;
                    bestSeen = candidate;
                }

                bool heardSprint = distance <= hearingRadius && IsPlayerAudiblySprinting(candidate);
                if (heardSprint && distance < bestSoundScore)
                {
                    bestSoundScore = distance;
                    bestSoundPoint = candidate.transform.position;
                    hasSoundPoint = true;
                }
            }

            ZombieAI zombie = hit.GetComponentInParent<ZombieAI>();
            if (zombie != null && zombie.IsMakingNoiseForAi)
            {
                float distance = Vector3.Distance(transform.position, zombie.transform.position);
                if (distance <= zombieNoiseHearRadius && distance < bestSoundScore)
                {
                    bestSoundScore = distance;
                    bestSoundPoint = zombie.transform.position;
                    hasSoundPoint = true;
                }
            }
        }

        if (bestSeen == null)
        {
            foreach (PlayerHealth candidate in FindObjectsByType<PlayerHealth>(FindObjectsInactive.Exclude))
            {
                if (candidate == null || candidate.IsDead || ShouldJailorIgnorePlayer(candidate))
                    continue;

                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance > Mathf.Max(detectionRadius, hearingRadius))
                    continue;

                bool seen = distance <= detectionRadius
                    && (!requireDetectionLineOfSight || HasDetectionLineOfSight(candidate));
                if (seen && distance < bestSeenScore)
                {
                    bestSeenScore = distance;
                    bestSeen = candidate;
                }

                bool heardSprint = distance <= hearingRadius && IsPlayerAudiblySprinting(candidate);
                if (heardSprint && distance < bestSoundScore)
                {
                    bestSoundScore = distance;
                    bestSoundPoint = candidate.transform.position;
                    hasSoundPoint = true;
                }
            }
        }

        if (bestSeen == null)
        {
            foreach (ZombieAI zombie in FindObjectsByType<ZombieAI>(FindObjectsInactive.Exclude))
            {
                if (zombie == null || !zombie.IsMakingNoiseForAi)
                    continue;

                float distance = Vector3.Distance(transform.position, zombie.transform.position);
                if (distance > zombieNoiseHearRadius || distance >= bestSoundScore)
                    continue;

                bestSoundScore = distance;
                bestSoundPoint = zombie.transform.position;
                hasSoundPoint = true;
            }
        }

        if (bestSeen != null)
        {
            AssignTarget(bestSeen);
            _hasInvestigationPoint = false;
            return;
        }

        if (hasSoundPoint)
            SetInvestigationPoint(bestSoundPoint);
    }

    void AssignTarget(PlayerHealth health)
    {
        _targetHealth = health;
        _target = health.transform;
        _hasInvestigationPoint = false;
        _chaseLineOfSightLostSince = -1f;
    }

    void ClearTarget()
    {
        _target = null;
        _targetHealth = null;
        _chaseLineOfSightLostSince = -1f;
    }

    bool UpdateChaseLostLineOfSight()
    {
        if (_targetHealth == null || _targetHealth.IsDead)
            return false;

        if (HasDetectionLineOfSight(_targetHealth))
        {
            _chaseLineOfSightLostSince = -1f;
            return false;
        }

        if (_chaseLineOfSightLostSince < 0f)
        {
            _chaseLineOfSightLostSince = Time.time;
            return false;
        }

        if (Time.time - _chaseLineOfSightLostSince < Mathf.Max(0.05f, chaseLoseLineOfSightSeconds))
            return false;

        Vector3 lastKnownPosition = _target.position;
        SetInvestigationPoint(lastKnownPosition);
        ClearTarget();
        EnterInvestigating();
        return true;
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
        _hasPatrolDestination = false;
        _patrolStuckAccumulatedTime = 0f;

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
        _hasPatrolDestination = false;
        _patrolStuckAccumulatedTime = 0f;
        _hasInvestigationPoint = false;
        _state = JailorState.Chase;
    }

    void SetInvestigationPoint(Vector3 worldPoint)
    {
        _investigationPoint = worldPoint;
        _hasInvestigationPoint = true;
        _isLingerAtInvestigationPoint = false;
        _investigationLingerEndTime = 0f;
        _hasInvestigationSearchDestination = false;
        _investigationSearchDestination = Vector3.zero;
    }

    void EnterInvestigating()
    {
        if (_state != JailorState.Investigating)
            _state = JailorState.Investigating;
        _chaseLineOfSightLostSince = -1f;

        _intendedMoveSpeed = patrolSpeed;
        if (!TrySnapToNavMesh())
            return;
        if (navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = patrolSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.2f, investigationArrivalDistance);
        if (_isLingerAtInvestigationPoint)
        {
            navMeshAgent.isStopped = false;
            navMeshAgent.speed = patrolSpeed;
            navMeshAgent.stoppingDistance = Mathf.Max(0.2f, patrolArrivalDistance * 0.7f);
        }
    }

    Vector3 UpdateInvestigating()
    {
        _intendedMoveSpeed = patrolSpeed;
        if (!_hasInvestigationPoint || !TrySnapToNavMesh() || navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return Vector3.zero;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = patrolSpeed;
        navMeshAgent.stoppingDistance = Mathf.Max(0.2f, investigationArrivalDistance);

        if (_isLingerAtInvestigationPoint)
        {
            if (Time.time >= _investigationLingerEndTime)
            {
                _isLingerAtInvestigationPoint = false;
                _hasInvestigationPoint = false;
                _hasInvestigationSearchDestination = false;
                EnterPatrol();
                return Vector3.zero;
            }

            if (!_hasInvestigationSearchDestination)
            {
                if (!TryPickInvestigationSearchDestination(out Vector3 firstSearchPoint))
                    return Vector3.zero;
                _investigationSearchDestination = firstSearchPoint;
                _hasInvestigationSearchDestination = true;
                navMeshAgent.SetDestination(_investigationSearchDestination);
            }
            else if (!navMeshAgent.pathPending
                && (!navMeshAgent.hasPath
                    || navMeshAgent.pathStatus == NavMeshPathStatus.PathInvalid
                    || navMeshAgent.pathStatus == NavMeshPathStatus.PathPartial
                    || navMeshAgent.remainingDistance <= Mathf.Max(0.4f, patrolArrivalDistance * 0.8f)))
            {
                if (TryPickInvestigationSearchDestination(out Vector3 nextSearchPoint))
                {
                    _investigationSearchDestination = nextSearchPoint;
                    navMeshAgent.SetDestination(_investigationSearchDestination);
                }
            }

            Vector3 searchVelocity = navMeshAgent.velocity.sqrMagnitude > 0.0001f
                ? navMeshAgent.velocity
                : navMeshAgent.desiredVelocity;
            searchVelocity.y = 0f;
            if (searchVelocity.sqrMagnitude > patrolSpeed * patrolSpeed)
                searchVelocity = searchVelocity.normalized * patrolSpeed;
            return searchVelocity;
        }

        Vector3 targetPoint = _investigationPoint;
        if (NavMesh.SamplePosition(targetPoint, out NavMeshHit hit, Mathf.Max(0.5f, targetNavMeshSampleRadius), NavMesh.AllAreas))
            targetPoint = hit.position;

        bool shouldRefreshDestination =
            Time.time >= _nextDestinationRefreshTime
            || (targetPoint - _lastPathDestination).sqrMagnitude
                >= destinationRefreshMinDistance * destinationRefreshMinDistance;

        if (shouldRefreshDestination)
        {
            navMeshAgent.SetDestination(targetPoint);
            _lastPathDestination = targetPoint;
            _nextDestinationRefreshTime = Time.time + Mathf.Max(0.05f, destinationRefreshInterval);
        }

        Vector3 flatSelf = transform.position;
        flatSelf.y = 0f;
        Vector3 flatDest = targetPoint;
        flatDest.y = 0f;
        if (Vector3.Distance(flatSelf, flatDest) <= investigationArrivalDistance)
        {
            _isLingerAtInvestigationPoint = true;
            _investigationLingerEndTime = Time.time + Mathf.Max(0f, investigationLingerSeconds);
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
            return Vector3.zero;
        }

        Vector3 desiredVelocity = navMeshAgent.velocity.sqrMagnitude > 0.0001f
            ? navMeshAgent.velocity
            : navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        if (desiredVelocity.sqrMagnitude > patrolSpeed * patrolSpeed)
            desiredVelocity = desiredVelocity.normalized * patrolSpeed;
        return desiredVelocity;
    }

    bool TryPickInvestigationSearchDestination(out Vector3 destination)
    {
        destination = _investigationPoint;
        float radius = Mathf.Max(1f, investigationSearchRadius);
        float minDistance = Mathf.Max(0.2f, investigationSearchMinWaypointDistance);
        int attempts = Mathf.Max(4, investigationSearchSampleAttempts);

        for (int i = 0; i < attempts; i++)
        {
            Vector2 sample2 = Random.insideUnitCircle * radius;
            Vector3 raw = _investigationPoint + new Vector3(sample2.x, 0f, sample2.y);
            if (!NavMesh.SamplePosition(raw, out NavMeshHit hit, Mathf.Max(1f, radius * 0.8f), NavMesh.AllAreas))
                continue;

            Vector3 flatDelta = hit.position - transform.position;
            flatDelta.y = 0f;
            if (flatDelta.magnitude < minDistance)
                continue;

            if (!TryHasReasonablePatrolPath(hit.position))
                continue;

            destination = hit.position;
            return true;
        }

        if (NavMesh.SamplePosition(_investigationPoint, out NavMeshHit centerHit, Mathf.Max(1f, radius), NavMesh.AllAreas))
        {
            destination = centerHit.position;
            return true;
        }

        return false;
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

        if (!_hasJumpTriggerParameter && !_loggedMissingJumpTriggerParam)
        {
            string controllerName = animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.name
                : "(none)";
            Debug.LogWarning(
                $"[JailorAI] Animator controller '{controllerName}' is missing optional trigger parameter '{jumpTriggerParameter}'. " +
                "Pit jump traversal will still move, but the jump animation will not play.",
                this);
            _loggedMissingJumpTriggerParam = true;
        }

        float horizontal = new Vector3(_horizontalVelocity.x, 0f, _horizontalVelocity.z).magnitude;
        if (_state == JailorState.Idle || _state == JailorState.Grabbing || _state == JailorState.JailDelivery)
            horizontal = 0f;

        float targetNormalized = runSpeed > 0.001f ? Mathf.Clamp01(horizontal / runSpeed) : 0f;
        if (targetNormalized < idleSpeedDeadZone)
            targetNormalized = 0f;
        if (alwaysRunWhenChasing
            && _state == JailorState.Chase
            && _targetHealth != null
            && targetNormalized > 0.08f)
            targetNormalized = 1f;

        if (_state == JailorState.Carrying && targetNormalized > 0.08f)
            targetNormalized = Mathf.Max(targetNormalized, 0.85f);

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

    static readonly float[] NavMeshSnapRadiiDefault = { 2f, 6f, 12f };
    static readonly float[] NavMeshSnapRadiiAggressive = { 3f, 8f, 16f, 24f, 48f };

    /// <summary>Pushes agent back onto NavMesh after pits / physics pushes (skipped during scripted off-mesh jumps).</summary>
    void RecoverNavMeshIfOffMesh()
    {
        if (_isTraversingOffMeshJump || navMeshAgent == null || !navMeshAgent.enabled || navMeshAgent.isOnNavMesh)
            return;

        TryWarpToNearestNavMesh(NavMeshSnapRadiiAggressive);
    }

    bool TrySnapToNavMesh()
    {
        return TryWarpToNearestNavMesh(NavMeshSnapRadiiDefault);
    }

    bool TryWarpToNearestNavMesh(float[] radii)
    {
        if (navMeshAgent == null || !navMeshAgent.enabled)
            return false;

        if (navMeshAgent.isOnNavMesh)
            return true;

        if (radii == null || radii.Length == 0)
            return false;

        Vector3 basePos = transform.position;
        Vector3[] verticalOrigins =
        {
            basePos,
            basePos + Vector3.up * 4f,
            basePos + Vector3.up * 10f,
        };

        for (int o = 0; o < verticalOrigins.Length; o++)
        {
            Vector3 origin = verticalOrigins[o];
            for (int i = 0; i < radii.Length; i++)
            {
                if (!NavMesh.SamplePosition(origin, out NavMeshHit hit, radii[i], NavMesh.AllAreas))
                    continue;

                bool ccWasEnabled = characterController != null && characterController.enabled;
                if (characterController != null)
                    characterController.enabled = false;

                navMeshAgent.Warp(hit.position);

                if (characterController != null)
                    characterController.enabled = ccWasEnabled;

                if (navMeshAgent.isOnNavMesh)
                {
                    _verticalVelocity.y = Mathf.Min(_verticalVelocity.y, 0f);
                    navMeshAgent.nextPosition = transform.position;
                    return true;
                }
            }
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

    void EnsureEnemyAndJailorLayerSetup()
    {
        int jailorLayer = LayerMask.NameToLayer(JailorLayerName);
        if (jailorLayer >= 0 && gameObject.layer != jailorLayer)
            gameObject.layer = jailorLayer;

        if (s_HasConfiguredEnemyJailorCollision)
            return;

        int enemyLayer = LayerMask.NameToLayer(EnemyLayerName);
        if (enemyLayer < 0 || jailorLayer < 0)
        {
            Debug.LogWarning(
                $"[{nameof(JailorAI)}] Missing layer setup for '{EnemyLayerName}' or '{JailorLayerName}'. " +
                "Add both layers in Project Settings > Tags and Layers so Jailor and zombies do not collide.",
                this);
            return;
        }

        Physics.IgnoreLayerCollision(enemyLayer, jailorLayer, true);
        s_HasConfiguredEnemyJailorCollision = true;
    }

    sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
    {
        public static readonly RaycastHitDistanceComparer Instance = new();

        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}
