using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Basic carnival Clown enemy AI: server-authoritative patrol + audio-driven investigation + chase.
/// A trimmed sibling of <see cref="JailorAI"/> — it shares the patrol/detection/investigation/chase
/// locomotion but, instead of the Jailor's grab/carry/jail delivery, its only attack is a hammer swing:
/// when it catches a player it plays the Hammer Swing clip and, at the swing's impact, launches the
/// player into ragdoll (a knockback, like <see cref="RagdollTrap"/>) rather than pinning/carrying them.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(CharacterController))]
public class ClownAI : MonoBehaviour
{
    enum ClownState
    {
        Idle,
        Patrol,
        Investigating,
        Chase,
        Attacking
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
    [SerializeField] float zombieNoiseHearRadius = 22f;
    [SerializeField] float targetNavMeshSampleRadius = 3f;
    [Tooltip("Half-angle of the vision cone, from facing. Players outside it are only found by hearing (sprint/voice/zombie noise/monkey lure) — sneaking behind works. 180 restores the old omniscient detection.")]
    [SerializeField, Range(10f, 180f)] float detectionFovHalfAngleDegrees = 100f;
    [Tooltip("If enabled, sight checks require a clear ray to the player.")]
    [SerializeField] bool requireDetectionLineOfSight = true;
    [SerializeField] LayerMask detectionLineOfSightMask = Physics.DefaultRaycastLayers;
    [SerializeField] float detectionLineOfSightHeight = 1.1f;
    [Tooltip(
        "Seconds between target-acquisition scans (the OverlapSphere + sight rays that only run while the "
            + "Clown has NO target). Movement, chasing and losing a target stay per-frame, so this only adds "
            + "up to this much latency to first spotting a player — imperceptible at <= 0.15s and a real CPU "
            + "saver. Set 0 to scan every frame (original behaviour).")]
    [SerializeField, Min(0f)] float sensingInterval = 0.1f;

    [Header("Movement")]
    [SerializeField] float walkSpeed = 2.6f;
    [SerializeField] float runSpeed = 4f;
    [SerializeField] float rotationSpeed = 360f;
    [SerializeField] float gravity = -20f;
    [SerializeField] float groundedStickDown = 2f;
    [Tooltip("How often chase destination is refreshed. Lower = more reactive, higher = less path jitter.")]
    [SerializeField] float destinationRefreshInterval = 0.1f;
    [Tooltip("Minimum target movement before forcing a path refresh.")]
    [SerializeField] float destinationRefreshMinDistance = 0.2f;
    [Tooltip("When chasing, use run speed (Clown does not use stamina).")]
    [SerializeField] bool alwaysRunWhenChasing = true;
    [Tooltip(
        "Buffer (m) beyond solid contact (Clown radius + player radius, both auto-measured) at which the Clown "
            + "stops chasing and stands/looms. 0 = stop right at contact. Keep >= 0; making it negative would let "
            + "it try to overlap the player and run in place again.")]
    [SerializeField, Min(0f)] float chaseStopPadding = 0.15f;
    [Tooltip(
        "CharacterController can snag on small prop colliders that still sit on the NavMesh. When intent speed stays high but "
            + "actual motion stays near zero, nudge to a nearby NavMesh point and reset the path.")]
    [SerializeField] float propStuckDesiredSpeedThreshold = 0.28f;
    [SerializeField] float propStuckActualSpeedThreshold = 0.06f;
    [SerializeField] float propStuckAccumulateSeconds = 0.55f;
    [SerializeField] float propStuckRecoveryCooldown = 1.1f;
    [SerializeField, Min(3)] int propStuckSampleAttempts = 12;
    [SerializeField, Min(0.05f)] float propStuckNudgeMinRadius = 0.4f;
    [SerializeField, Min(0.05f)] float propStuckNudgeMaxRadius = 2.1f;
    [SerializeField, Min(0.25f)] float propStuckNavSampleRadius = 3.5f;

    [Header("Corner slide assist")]
    [Tooltip(
        "When the body grinds into a wall/corner (common once scaled up, since the NavMesh path was baked for "
            + "the small base radius), redirect movement to slide ALONG the wall at full speed instead of stalling.")]
    [SerializeField] bool enableWallSlide = true;
    [Tooltip("How long (s) a wall contact keeps deflecting movement after the last touch.")]
    [SerializeField, Min(0.02f)] float wallSlideMemorySeconds = 0.15f;
    [Tooltip("Contacts flatter than this |normal.y| count as walls (vs floor/ceiling).")]
    [SerializeField, Range(0f, 1f)] float wallSlideMaxNormalY = 0.7f;

    [Header("Chase corridor centering")]
    [Tooltip(
        "While chasing, steer toward the middle of the hallway instead of letting the baked NavMesh path hug "
            + "the inside corners. The baked path was string-pulled for the small base radius, so the grown body "
            + "clips the walls and hangs on corners. Probing the sides and leaning toward the open one keeps the "
            + "Clown off the walls before it grinds into them.")]
    [SerializeField] bool chaseCenterInCorridor = true;
    [Tooltip("Gap (m, on top of the Clown's own scaled radius) it tries to keep from a side wall while chasing. Also the sideways probe reach.")]
    [SerializeField, Min(0f)] float chaseWallSideClearance = 0.9f;
    [Tooltip("How far ahead (m) to also probe so the Clown widens its line BEFORE it reaches the corner the path is cutting.")]
    [SerializeField, Min(0f)] float chaseWallLookAhead = 1.4f;
    [Tooltip("Max fraction of chase speed redirected sideways toward the open side (1 ≈ a 45° lean toward center).")]
    [SerializeField, Range(0f, 1f)] float chaseCenterSteerStrength = 0.65f;
    [Tooltip("Height (m) above the feet at which the left/right clearance rays are cast.")]
    [SerializeField, Min(0f)] float chaseWallProbeHeight = 1f;

    [Header("Pit recovery")]
    [Tooltip(
        "Watchdog for the case where the Clown lands on a tiny NavMesh patch inside a pit. "
            + "RecoverNavMeshIfOffMesh skips when isOnNavMesh=true, so this fires when his Y sits well below the nearest rim NavMesh.")]
    [SerializeField, Min(0.05f)] float pitStuckCheckInterval = 0.25f;
    [Tooltip("How far (meters) below the nearest sampled NavMesh point he must sit to count as 'in a pit'.")]
    [SerializeField, Min(0.25f)] float pitStuckBelowNavMeshThreshold = 1.5f;
    [Tooltip("Vertical search lift used when sampling NavMesh above the Clown — should cover the deepest pit.")]
    [SerializeField, Min(1f)] float pitStuckVerticalSearchHeight = 12f;
    [Tooltip("XZ sample radii (meters) tried in order when looking for a rim NavMesh point to warp to.")]
    [SerializeField] float[] pitStuckSampleRadii = { 4f, 8f, 16f, 28f };
    [Tooltip("Below this horizontal speed (m/s) the Clown counts as 'not progressing' for pit-stuck accumulation.")]
    [SerializeField, Min(0f)] float pitStuckLowSpeedThreshold = 0.25f;
    [Tooltip("Sustained seconds matching all pit-stuck conditions before a rescue warp fires.")]
    [SerializeField, Min(0.25f)] float pitStuckAccumulateSeconds = 1.75f;
    [Tooltip("Minimum seconds between pit-stuck rescue warps so the watchdog can't loop on a deep arrival landing.")]
    [SerializeField, Min(0.25f)] float pitStuckRescueCooldown = 1.5f;
    [Tooltip("Vertical lift applied after pit-stuck warp so the agent does not immediately re-sample pit-floor NavMesh.")]
    [SerializeField, Min(0f)] float pitStuckRescueLift = 0.1f;
    [Tooltip(
        "Floor-tunnel guard (ApplyMovement): the most the Clown may descend in ONE frame BEYOND its gravity "
            + "step before that drop is treated as a CharacterController depenetration shoving it through the "
            + "non-convex floor (e.g. against the player ragdoll it just clubbed) and is cancelled. Must sit "
            + "above any legitimate single-frame fall (gravity / ramp / step ~= stepOffset) and below an eject "
            + "(which punches the capsule down by a large fraction of its height).")]
    [SerializeField, Min(0.05f)] float floorTunnelMaxExtraDrop = 0.3f;

    [Header("Patrol")]
    [SerializeField] float patrolSpeed = 2.2f;
    [SerializeField] float patrolMinWaypointDistance = 6f;
    [SerializeField] float patrolMaxWaypointDistance = 14f;
    [SerializeField] float patrolArrivalDistance = 1f;
    [SerializeField] float patrolDestinationRefreshInterval = 0.45f;
    [SerializeField] int patrolSampleAttempts = 14;
    [Tooltip("Avoid recently visited points so the clown does not bounce in dead-end loops.")]
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
    [Tooltip("Use actual horizontal move speed for the blend tree (matches feet). 0 = instant; higher = less flicker between idle and move.")]
    [SerializeField] float animatorSpeedLerp = 12f;
    [Tooltip("Below this normalized speed, treat movement as idle to avoid idle/walk chatter.")]
    [SerializeField] float idleSpeedDeadZone = 0.08f;

    [Header("Audio")]
    [Tooltip("Clown footstep clips. One is picked at random per step (no immediate repeat). Trigger timing uses the walk/run animation phases.")]
    [SerializeField] AudioClip[] clownFootstepClips;
    [SerializeField, Range(0f, 1f)] float clownFootstepVolume = 0.45f;
    [SerializeField] AudioSource clownFootstepAudioSource;
    [SerializeField] float clownMinFootstepMoveSpeed = 0.2f;
    [Tooltip("Normalized animation times where walk footsteps should fire (x and y in 0-1).")]
    [SerializeField] Vector2 clownWalkFootstepPhases = new Vector2(0.13f, 0.63f);
    [Tooltip("Normalized animation times where run footsteps should fire (x and y in 0-1).")]
    [SerializeField] Vector2 clownRunFootstepPhases = new Vector2(0.1f, 0.6f);

    /// <summary>Which Clown voice clip a (networked) observer should play — sent over the wire as a byte.</summary>
    public enum ClownVoice : byte { PatrolLaughA, PatrolLaughB, ChaseLaugh }

    [Header("Voice (laughs)")]
    [Tooltip("While NOT chasing, these two alternate every patrolLaughInterval seconds (the 'patrol' ambience). Maps to ClownLaugh2 / ClownLaugh3.")]
    [SerializeField] AudioClip clownPatrolLaughA;
    [SerializeField] AudioClip clownPatrolLaughB;
    [Tooltip("While chasing, ClownLaugh1 loops back-to-back until pursuit ends.")]
    [SerializeField] AudioClip clownChaseLaugh;
    [SerializeField, Range(0f, 1f)] float clownVoiceVolume = 0.7f;
    [SerializeField] AudioSource clownVoiceAudioSource;
    [Tooltip("Seconds between patrol laughs (alternating ClownLaugh2/ClownLaugh3) while not chasing.")]
    [SerializeField, Min(0.1f)] float patrolLaughInterval = 4f;

    [Header("Hammer swing attack")]
    [Tooltip("Animator Trigger that starts the Hammer Swing state.")]
    [SerializeField] string swingTriggerParameter = "Swing";
    [Tooltip("Name of the Hammer Swing state in the animator (used to detect impact timing and when it finishes).")]
    [SerializeField] string swingStateName = "Hammer Swing";
    [Tooltip("Length (s) of the Hammer Swing clip; fallback if the exit can't be read from the animator.")]
    [SerializeField, Min(0.1f)] float swingClipDurationFallback = 3.9f;
    [Tooltip("Hammer reach (m, before scale) to the player to start a swing. Scaled by the Clown's size (the "
        + "arms/hammer grow with it), so the giant commits its swing from where its big hammer can actually "
        + "reach — well before it has physically closed the last few metres.")]
    [SerializeField, Min(0.1f)] float attackRange = 1.7f;
    [SerializeField, Min(0f)] float attackRangePadding = 0.2f;
    [Tooltip("Forward cone half-angle (deg): only swings at a player roughly in front.")]
    [SerializeField, Range(0f, 180f)] float attackHalfAngle = 55f;
    [Tooltip("Max vertical offset (m) to the player to start a swing.")]
    [SerializeField, Min(0.1f)] float maxAttackVerticalDelta = 1.2f;
    [Tooltip("At the impact frame the hammer only connects if the player is still within (attack range + padding) × this multiplier — nothing catches them early, so a clean dodge during the wind-up whiffs. 1 = exactly the start reach; higher = more forgiving.")]
    [SerializeField, Min(1f)] float hammerHitReachMultiplier = 1.5f;
    [Tooltip("Primary trigger: normalized clip time (0-1) at which the hammer connects and the player is launched into ragdoll. Driven off actual animation playback (frame-accurate). TUNE THIS to the frame where the hammer visually contacts.")]
    [SerializeField, Range(0f, 1f)] float hammerHitNormalizedTime = 0.5f;
    [Tooltip("Safety fallback (s) to launch the player if the animator never reaches the swing state. Should be after the normalized-time trigger.")]
    [SerializeField, Min(0f)] float hammerHitFallbackDelay = 2f;
    [Tooltip("Horizontal launch speed of the hammer knockback, along the swing direction. Capped by the player's ragdoll force cap (~16).")]
    [SerializeField, Min(0f)] float knockbackForwardSpeed = 13f;
    [Tooltip("Upward launch speed of the hammer knockback (sends the body flying up and back rather than skidding flat).")]
    [SerializeField, Min(0f)] float knockbackUpwardSpeed = 5f;
    [Header("Knockback direction (avoid walls / throw down hallways)")]
    [Tooltip("If ON, the knockback is redirected to the most open horizontal direction (e.g. down a hallway) so the player isn't launched straight into a nearby wall.")]
    [SerializeField] bool knockbackAvoidWalls = true;
    [Tooltip("Obstacle layers used to probe for walls. Leave as Nothing to auto-use everything except actors (players/enemies).")]
    [SerializeField] LayerMask knockbackObstacleMask;
    [Tooltip("How far to probe each candidate direction for clearance (m).")]
    [SerializeField, Min(1f)] float knockbackProbeRange = 8f;
    [Tooltip("A direction counts as 'open' if it has at least this much clearance (m). The most open direction nearest the swing intent is chosen.")]
    [SerializeField, Min(0.5f)] float knockbackMinClearance = 3f;
    [Tooltip("Probe sphere radius (≈ player width) so narrow gaps don't count as open.")]
    [SerializeField, Min(0.05f)] float knockbackProbeRadius = 0.4f;
    [Tooltip("Height above the impact point to probe from (≈ the launched body's height).")]
    [SerializeField, Min(0f)] float knockbackProbeHeight = 0.5f;
    [Tooltip("Clearance (m) at/above which the knockback uses full forward speed. Below it, the forward launch is scaled down so the ragdoll isn't flung into a nearby wall hard enough to tunnel through it.")]
    [SerializeField, Min(0.5f)] float knockbackForwardFullSpeedClearance = 4f;
    [SerializeField] ForceMode knockbackForceMode = ForceMode.VelocityChange;
    [Tooltip("Damage dealt by the hammer hit. Player auto-recovers unless this kills them.")]
    [SerializeField, Min(0f)] float hammerDamage = 22f;
    [Tooltip("After a swing, the Clown won't attack again (or re-chase) for this long.")]
    [SerializeField, Min(0f)] float postAttackCooldownSeconds = 2.5f;

    readonly Collider[] _detectionHits = new Collider[16];
    readonly RaycastHit[] _lineOfSightHits = new RaycastHit[16];
    float _nextSenseTime = -1f;

    ClownState _state;
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
    bool _hasSwingTriggerParameter;
    int _swingStateHash;

    float _attackStartedTime;
    bool _hammerHitDone;
    Vector3 _attackFrozenPosition;
    PlayerHealth _attackTargetHealth;
    PlayerRagdollController _attackTargetRagdoll;
    NetworkPlayerRagdoll _attackTargetNetRagdoll;
    // Set when the current swing targets a wind-up monkey (a knock-over, not a player ragdoll).
    WindupMonkeyAI _attackTargetMonkey;
    // The wind-up monkey whose clap last lured the Clown; smashed on arrival when no player is around to chase.
    WindupMonkeyAI _lureMonkey;
    float _suppressAttackAndChaseUntil;

    const string EnemyLayerName = "Enemy";
    const string JailorLayerName = "Jailor";
    const string ClownLayerName = "Clown";
    static bool s_HasConfiguredClownCollision;

    /// <summary>
    /// Register before any Awake so the Clown never relies on spawn order for layer ignores against other enemies.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RegisterIgnoreClownPhysicsCollision()
    {
        int clownLayer = LayerMask.NameToLayer(ClownLayerName);
        if (clownLayer < 0)
            return;

        int enemyLayer = LayerMask.NameToLayer(EnemyLayerName);
        int jailorLayer = LayerMask.NameToLayer(JailorLayerName);
        if (enemyLayer >= 0)
            Physics.IgnoreLayerCollision(enemyLayer, clownLayer, true);
        if (jailorLayer >= 0)
            Physics.IgnoreLayerCollision(jailorLayer, clownLayer, true);
        s_HasConfiguredClownCollision = true;
    }

    NetworkObject _networkObject;
    NetworkClownAvatar _networkClownAvatar;

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
    // >0 forces this approach speed during Investigating instead of patrolSpeed (used by the wind-up monkey lure
    // so the Clown RUNS to the monkey). Cleared once he arrives / leaves the investigate state.
    float _investigationSpeedOverride;
    bool _isLingerAtInvestigationPoint;
    float _investigationLingerEndTime;
    bool _hasInvestigationSearchDestination;
    Vector3 _investigationSearchDestination;
    float _chaseLineOfSightLostSince = -1f;
    int _lastFootstepAnimStateHash;
    float _lastFootstepAnimNormalizedTime;
    bool _hasFootstepAnimSample;
    int _lastFootstepClipIndex = -1;
    bool _voiceInitialized;
    bool _wasVoiceChasing;
    float _voiceClipEndTime;
    float _nextPatrolVoiceTime;
    bool _patrolLaughUseB;
    Vector3 _positionBeforeCharacterMove;
    float _propStuckAccumulatedTime;
    float _nextPropStuckRecoveryTime;
    Vector3 _wallSlideNormal;
    float _wallSlideHitTime = -999f;
    int _wallSlideIgnoreLayers;
    float _pitStuckAccumulatedTime;
    float _nextPitStuckCheckTime;
    float _nextPitStuckRescueTime;

    void Reset()
    {
        CacheReferences();
        EnsureEnemyAndClownLayerSetup();
        ApplyAgentSettings();
        EnsurePatrolPathScratch();
        AutoAssignClownAudioInEditor();
    }

    void Awake()
    {
        AutoAssignClownAudioInEditor();
        CacheReferences();
        EnsureEnemyAndClownLayerSetup();
        ApplyAgentSettings();
        EnsurePatrolPathScratch();
        _wallSlideIgnoreLayers = BuildActorLayerMask();
    }

    /// <summary>Layers the wall-slide must ignore — players and other enemies are not "walls" to slide along.</summary>
    static int BuildActorLayerMask()
    {
        int mask = 0;
        string[] actorLayers = { "Player", EnemyLayerName, JailorLayerName, ClownLayerName };
        for (int i = 0; i < actorLayers.Length; i++)
        {
            int layer = LayerMask.NameToLayer(actorLayers[i]);
            if (layer >= 0)
                mask |= 1 << layer;
        }
        return mask;
    }

    void OnValidate()
    {
        AutoAssignClownAudioInEditor();
    }

    void EnsurePatrolPathScratch()
    {
        if (_patrolPathScratch == null)
            _patrolPathScratch = new NavMeshPath();
    }

    void ClearInvestigationState()
    {
        _hasInvestigationPoint = false;
        _isLingerAtInvestigationPoint = false;
        _investigationLingerEndTime = 0f;
        _hasInvestigationSearchDestination = false;
        _investigationSearchDestination = Vector3.zero;
    }

    static bool ShouldIgnorePlayer(PlayerHealth health)
    {
        if (health == null || health.IsDead)
            return false;

        NetworkPlayerAvatar avatar = health.GetComponent<NetworkPlayerAvatar>();
        return avatar != null && avatar.IsSealedInJailCell;
    }

    void OnEnable()
    {
        ServerProximityVoiceNotifications.Register(this);
        ClownAIRegistry.Register(this);
        TrySnapToNavMesh();
    }

    void OnDisable()
    {
        ServerProximityVoiceNotifications.Unregister(this);
        ClownAIRegistry.Unregister(this);

        // The hammer swing never pins the player (it's a one-shot knockback), so there is nothing to release
        // if the Clown is disabled/despawned mid-swing — the player was never attached to it.
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
        _networkClownAvatar = GetComponent<NetworkClownAvatar>();
        ConfigureClownFootstepAudioSource();
        ConfigureClownVoiceAudioSource();

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

    void ConfigureClownFootstepAudioSource()
    {
        if (clownFootstepAudioSource == null)
            clownFootstepAudioSource = EnsureNamedChildAudioSource("ClownFootstepAudio");
        if (clownFootstepAudioSource == null)
            return;

        clownFootstepAudioSource.playOnAwake = false;
        clownFootstepAudioSource.loop = false;
        clownFootstepAudioSource.spatialBlend = 1f;
        clownFootstepAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        clownFootstepAudioSource.minDistance = 1.5f;
        clownFootstepAudioSource.maxDistance = 25f;
        clownFootstepAudioSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(clownFootstepAudioSource);
    }

    void ConfigureClownVoiceAudioSource()
    {
        if (clownVoiceAudioSource == null)
            clownVoiceAudioSource = EnsureNamedChildAudioSource("ClownVoiceAudio");
        if (clownVoiceAudioSource == null)
            return;

        clownVoiceAudioSource.playOnAwake = false;
        clownVoiceAudioSource.loop = false;
        clownVoiceAudioSource.spatialBlend = 1f;
        clownVoiceAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        clownVoiceAudioSource.minDistance = 2f;
        clownVoiceAudioSource.maxDistance = 30f;
        clownVoiceAudioSource.dopplerLevel = 0f;
        clownVoiceAudioSource.volume = Mathf.Clamp01(clownVoiceVolume);
        GameAudioManager.RouteSfxSource(clownVoiceAudioSource);
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

    void AutoAssignClownAudioInEditor()
    {
#if UNITY_EDITOR
        if (clownFootstepAudioSource == null)
            clownFootstepAudioSource = EnsureNamedChildAudioSource("ClownFootstepAudio");

        if (clownFootstepClips == null || clownFootstepClips.Length == 0)
        {
            string[] expected =
            {
                "ClownFootstep1",
                "ClownFootstep2",
                "ClownFootstep3"
            };

            var resolved = new System.Collections.Generic.List<AudioClip>();
            for (int i = 0; i < expected.Length; i++)
            {
                AudioClip clip = FindFirstAudioClipByName(expected[i]);
                if (clip != null)
                    resolved.Add(clip);
            }

            if (resolved.Count > 0)
                clownFootstepClips = resolved.ToArray();
        }

        // Only resolve an EXISTING child here — creating/reparenting a GameObject during OnValidate is
        // disallowed for prefab assets. The runtime ConfigureClownVoiceAudioSource creates it if missing.
        if (clownVoiceAudioSource == null)
        {
            Transform existingVoice = transform.Find("ClownVoiceAudio");
            if (existingVoice != null)
                clownVoiceAudioSource = existingVoice.GetComponent<AudioSource>();
        }

        if (clownPatrolLaughA == null)
            clownPatrolLaughA = FindFirstAudioClipByName("ClownLaugh2");
        if (clownPatrolLaughB == null)
            clownPatrolLaughB = FindFirstAudioClipByName("ClownLaugh3");
        if (clownChaseLaugh == null)
            clownChaseLaugh = FindFirstAudioClipByName("ClownLaugh1");
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
        _hasSwingTriggerParameter = HasAnimatorParameter(swingTriggerParameter, AnimatorControllerParameterType.Trigger);
        _swingStateHash = Animator.StringToHash(swingStateName);
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
        // Lower value = higher priority in NavMesh local avoidance — prevents deadlocks vs other agents also at default 50.
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

        // Laughs/breathing ambience is independent of the locomotion state machine, so drive it before the
        // Attacking early-return below (otherwise the chase loop would stall mid-swing).
        HandleClownVoice();

        // The hammer swing no longer freezes the Clown — it keeps chasing while the swing plays so it can
        // attack on the run and land the hit on a fleeing player instead of stopping dead and whiffing.
        // UpdateAttacking drives the movement, the impact poll and the end-of-swing handoff itself.
        if (_state == ClownState.Attacking)
        {
            UpdateAttacking();
            return;
        }

        RecoverNavMeshIfOffMesh();
        UpdatePitStuckWatchdog();

        // Throttle the acquisition scan. RefreshTargetFromSightAndHearing() already no-ops once a target is
        // held, so this only paces the expensive search-phase OverlapSphere/rays; chasing stays per-frame.
        if (_nextSenseTime < 0f)
            _nextSenseTime = Time.time + Random.Range(0f, Mathf.Max(0f, sensingInterval)); // stagger agents
        if (sensingInterval <= 0f || Time.time >= _nextSenseTime)
        {
            _nextSenseTime = Time.time + Mathf.Max(0f, sensingInterval);
            RefreshTargetFromSightAndHearing();
        }

        float loseRadius = Mathf.Max(detectionRadius, hearingRadius, voiceHearRadius)
            * Mathf.Max(1f, loseTargetRadiusMultiplier);

        if (_targetHealth != null && !_targetHealth.IsDead && ShouldIgnorePlayer(_targetHealth))
            ClearTarget();

        if (_targetHealth != null && !_targetHealth.IsDead)
        {
            if (_state == ClownState.Chase && UpdateChaseLostLineOfSight())
            {
                UpdateAnimatorParameters();
                return;
            }

            float d = Vector3.Distance(transform.position, _target.position);
            if (d > loseRadius)
            {
                if (_state == ClownState.Chase)
                {
                    // Outran, not just out-of-sight: walk to where they last were and search, exactly like
                    // the line-of-sight loss path above — never teleport-forget mid-chase.
                    Vector3 lastKnownPosition = _target.position;
                    SetInvestigationPoint(lastKnownPosition);
                    ClearTarget();
                    EnterInvestigating();
                }
                else
                {
                    ClearTarget();
                }
            }
        }

        Vector3 desiredHorizontal = Vector3.zero;
        if (_targetHealth != null && !_targetHealth.IsDead)
        {
            switch (_state)
            {
                case ClownState.Idle:
                case ClownState.Patrol:
                case ClownState.Investigating:
                    EnterChase();
                    desiredHorizontal = UpdateChase();
                    break;
                case ClownState.Chase:
                    desiredHorizontal = UpdateChase();
                    // Begin the swing the instant it's in range, but keep the chase velocity — the Clown
                    // swings on the run and lunges into the player instead of stopping to wind up.
                    if (ShouldStartAttack())
                        EnterAttacking();
                    break;
            }
        }
        else
        {
            if (_hasInvestigationPoint)
            {
                // Lured to a wind-up monkey with no player to chase: once in hammer reach, smash the monkey
                // (knocking it over/silencing it) instead of just lingering around it.
                if (ShouldSmashMonkey())
                {
                    EnterAttackingMonkey(_lureMonkey);
                    desiredHorizontal = Vector3.zero;
                }
                else
                {
                    EnterInvestigating();
                    desiredHorizontal = UpdateInvestigating();
                }
            }
            else
            {
                EnterPatrol();
                desiredHorizontal = UpdatePatrol();
            }
        }

        ApplyMovement(desiredHorizontal);

        HandleClownFootsteps();
        UpdateAnimatorParameters();
    }

    void HandleClownFootsteps()
    {
        if (clownFootstepAudioSource == null || characterController == null || animator == null)
            return;
        if (_state == ClownState.Attacking)
            return;
        if (_state == ClownState.Idle)
        {
            _hasFootstepAnimSample = false;
            return;
        }
        if (!characterController.isGrounded)
        {
            _hasFootstepAnimSample = false;
            return;
        }

        float horizontalSpeed = new Vector3(_horizontalVelocity.x, 0f, _horizontalVelocity.z).magnitude;
        if (horizontalSpeed < Mathf.Max(0.01f, clownMinFootstepMoveSpeed))
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
        Vector2 phases = isRunningStep ? clownRunFootstepPhases : clownWalkFootstepPhases;

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
        if (_networkClownAvatar != null
            && nm != null
            && nm.IsListening
            && _networkObject != null
            && _networkObject.IsSpawned)
        {
            _networkClownAvatar.PlayFootstepSfxForObservers();
            return;
        }

        PlayFootstepSfxLocal();
    }

    public void PlayFootstepSfxLocal()
    {
        if (clownFootstepAudioSource == null || clownFootstepClips == null || clownFootstepClips.Length == 0)
            return;

        // Pick a random clip, avoiding an immediate repeat so consecutive steps don't sound identical.
        int index;
        if (clownFootstepClips.Length == 1)
            index = 0;
        else
        {
            index = Random.Range(0, clownFootstepClips.Length);
            if (index == _lastFootstepClipIndex)
                index = (index + 1) % clownFootstepClips.Length;
        }
        _lastFootstepClipIndex = index;

        AudioClip clip = clownFootstepClips[index];
        if (clip != null)
            clownFootstepAudioSource.PlayOneShot(clip, Mathf.Clamp01(clownFootstepVolume));
    }

    // ---- Voice (laughs / breathing) -----------------------------------------------------------------

    /// <summary>
    /// Server-side ambience driver (Update is server-gated). While NOT chasing, alternates ClownLaugh2 /
    /// ClownLaugh3 every <see cref="patrolLaughInterval"/> seconds. While chasing, loops ClownLaugh1
    /// back-to-back until pursuit ends, then falls back to the patrol laughs.
    /// Each chosen clip is replicated to nearby observers (mirrors the footstep RPC path).
    /// </summary>
    void HandleClownVoice()
    {
        if (clownVoiceAudioSource == null)
            return;

        bool chasing = _state == ClownState.Chase || _state == ClownState.Attacking;

        // First run, or a pursuit<->patrol transition: restart the relevant cadence.
        if (!_voiceInitialized || chasing != _wasVoiceChasing)
        {
            _voiceInitialized = true;
            _wasVoiceChasing = chasing;
            if (chasing)
            {
                // Pursuit loops the chase laugh back-to-back.
                PlayNextChaseVoice();
            }
            else
            {
                // Fall back to patrol ambience; first laugh after the interval (no instant bark on losing chase).
                _nextPatrolVoiceTime = Time.time + Mathf.Max(0.1f, patrolLaughInterval);
            }
            return;
        }

        if (chasing)
        {
            if (Time.time >= _voiceClipEndTime)
                PlayNextChaseVoice();
        }
        else if (Time.time >= _nextPatrolVoiceTime)
        {
            PlayNextPatrolVoice();
            _nextPatrolVoiceTime = Time.time + Mathf.Max(0.1f, patrolLaughInterval);
        }
    }

    void PlayNextChaseVoice()
    {
        // Schedule the next laugh off this clip's length so they play back-to-back; if the clip is missing,
        // use a short fallback so the loop still advances instead of stalling.
        float length = clownChaseLaugh != null ? clownChaseLaugh.length : 0f;
        _voiceClipEndTime = Time.time + Mathf.Max(0.15f, length);
        NotifyVoiceSfx(ClownVoice.ChaseLaugh);
    }

    void PlayNextPatrolVoice()
    {
        bool useB = _patrolLaughUseB;
        _patrolLaughUseB = !_patrolLaughUseB;
        NotifyVoiceSfx(useB ? ClownVoice.PatrolLaughB : ClownVoice.PatrolLaughA);
    }

    void NotifyVoiceSfx(ClownVoice clip)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (_networkClownAvatar != null
            && nm != null
            && nm.IsListening
            && _networkObject != null
            && _networkObject.IsSpawned)
        {
            _networkClownAvatar.PlayVoiceSfxForObservers((byte)clip);
            return;
        }

        PlayVoiceSfxLocal((byte)clip);
    }

    public void PlayVoiceSfxLocal(byte clipId)
    {
        if (clownVoiceAudioSource == null)
            return;

        AudioClip clip = GetVoiceClip((ClownVoice)clipId);
        if (clip == null)
            return;

        // clip + Play (not PlayOneShot) so a new line cleanly interrupts the previous one — e.g. the chase
        // laugh cutting off a patrol laugh the instant pursuit starts.
        clownVoiceAudioSource.Stop();
        clownVoiceAudioSource.clip = clip;
        clownVoiceAudioSource.volume = Mathf.Clamp01(clownVoiceVolume);
        clownVoiceAudioSource.Play();
    }

    AudioClip GetVoiceClip(ClownVoice clip)
    {
        switch (clip)
        {
            case ClownVoice.PatrolLaughA: return clownPatrolLaughA;
            case ClownVoice.PatrolLaughB: return clownPatrolLaughB;
            case ClownVoice.ChaseLaugh: return clownChaseLaugh;
            default: return null;
        }
    }

    /// <summary>Moves the CharacterController root and syncs <see cref="NavMeshAgent"/> to a NavMesh-safe point.</summary>
    void WarpTransformToNavMeshPoint(Vector3 safeWorldPosition)
    {
        bool ccWasEnabled = characterController != null && characterController.enabled;
        if (characterController != null)
            characterController.enabled = false;

        transform.position = safeWorldPosition;

        if (navMeshAgent != null && navMeshAgent.enabled)
        {
            if (navMeshAgent.isOnNavMesh)
                navMeshAgent.Warp(safeWorldPosition);
            navMeshAgent.isStopped = false;
            navMeshAgent.ResetPath();
            navMeshAgent.nextPosition = transform.position;
        }

        if (characterController != null && ccWasEnabled)
            characterController.enabled = true;

        _verticalVelocity.y = 0f;
    }

    bool TryRecoverFromPropStuck()
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || !navMeshAgent.isOnNavMesh)
            return false;

        Vector3 origin = transform.position;
        float navRadius = Mathf.Max(0.5f, propStuckNavSampleRadius);
        int attempts = Mathf.Max(3, propStuckSampleAttempts);

        Vector3 hintFlat = Vector3.zero;
        if (navMeshAgent.hasPath
            && navMeshAgent.path != null
            && navMeshAgent.path.corners != null
            && navMeshAgent.path.corners.Length >= 2)
        {
            hintFlat = navMeshAgent.path.corners[1] - origin;
            hintFlat.y = 0f;
            if (hintFlat.sqrMagnitude > 0.04f)
                hintFlat.Normalize();
            else
                hintFlat = Vector3.zero;
        }

        if (hintFlat.sqrMagnitude < 0.01f)
        {
            Vector3 dv = navMeshAgent.desiredVelocity;
            dv.y = 0f;
            if (dv.sqrMagnitude > 0.04f)
                hintFlat = dv.normalized;
        }

        float rMin = Mathf.Min(propStuckNudgeMinRadius, propStuckNudgeMaxRadius);
        float rMax = Mathf.Max(propStuckNudgeMinRadius, propStuckNudgeMaxRadius);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Vector3 offset;
            if (attempt == 0 && hintFlat.sqrMagnitude > 0.01f)
                offset = hintFlat * Random.Range(rMin, rMax);
            else
            {
                float ang = Random.Range(0f, Mathf.PI * 2f);
                float rad = Random.Range(rMin, rMax);
                offset = new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad);
            }

            Vector3 samplePoint = origin + offset;
            if (!NavMesh.SamplePosition(samplePoint, out NavMeshHit hit, navRadius, NavMesh.AllAreas))
                continue;

            Vector3 deltaFlat = hit.position - origin;
            deltaFlat.y = 0f;
            if (deltaFlat.sqrMagnitude < 0.007f)
                continue;

            WarpTransformToNavMeshPoint(hit.position);
            _nextDestinationRefreshTime = 0f;
            _nextPatrolDestinationRefreshTime = 0f;
            _patrolStuckAccumulatedTime = 0f;
            return true;
        }

        navMeshAgent.ResetPath();
        _nextDestinationRefreshTime = 0f;
        _nextPatrolDestinationRefreshTime = 0f;
        _patrolStuckAccumulatedTime = 0f;
        return true;
    }

    void EnterPatrol()
    {
        if (_state != ClownState.Patrol)
            _state = ClownState.Patrol;

        _investigationSpeedOverride = 0f;
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

    public void OnServerHeardVoiceFrame(ulong speakerClientId)
    {
        if (!ShouldRunSimulation())
            return;

        if (!VoiceClientRegistry.TryGet(speakerClientId, out NetworkPlayerVoice voice)
            || voice == null)
            return;

        PlayerHealth health = voice.GetComponentInParent<PlayerHealth>();
        if (health == null || health.IsDead || ShouldIgnorePlayer(health))
            return;

        float d = Vector3.Distance(transform.position, voice.transform.position);
        if (d > voiceHearRadius)
            return;

        SetInvestigationPoint(voice.transform.position);
    }

    void RefreshTargetFromSightAndHearing()
    {
        if (_targetHealth != null && !_targetHealth.IsDead)
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
            if (candidate != null && !candidate.IsDead && !ShouldIgnorePlayer(candidate))
            {
                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                bool seen = distance <= detectionRadius
                    && IsWithinDetectionCone(candidate.transform.position)
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
            IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerHealth candidate = players[i];
                if (candidate == null || candidate.IsDead || ShouldIgnorePlayer(candidate))
                    continue;

                float distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance > Mathf.Max(detectionRadius, hearingRadius))
                    continue;

                bool seen = distance <= detectionRadius
                    && IsWithinDetectionCone(candidate.transform.position)
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
            IReadOnlyList<ZombieAI> zombies = ZombieAIRegistry.All;
            for (int i = 0; i < zombies.Count; i++)
            {
                ZombieAI zombie = zombies[i];
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

    /// <summary>
    /// True while the Clown is actively chasing a live, non-sealed player; outputs that player's world
    /// position. <see cref="ClownDynamicScale"/> uses this to grow the Clown the closer it gets to its quarry.
    /// </summary>
    public bool TryGetChaseTargetPosition(out Vector3 position)
    {
        if ((_state == ClownState.Chase || _state == ClownState.Attacking)
            && _target != null
            && _targetHealth != null
            && !_targetHealth.IsDead
            && !ShouldIgnorePlayer(_targetHealth))
        {
            position = _target.position;
            return true;
        }

        position = Vector3.zero;
        return false;
    }

    /// <summary>
    /// The player the Clown should visually fixate on: the active chase target if any, otherwise the
    /// nearest live, non-sealed player within <paramref name="maxRange"/>. Returns null if none.
    /// <see cref="ClownDynamicScale"/> uses this to aim the head.
    /// </summary>
    public Transform GetLookAtPlayer(float maxRange)
    {
        if (_target != null && _targetHealth != null && !_targetHealth.IsDead && !ShouldIgnorePlayer(_targetHealth))
            return _target;

        Transform best = null;
        float bestSqr = maxRange * maxRange;
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth candidate = players[i];
            if (candidate == null || candidate.IsDead || ShouldIgnorePlayer(candidate))
                continue;

            float sqr = (candidate.transform.position - transform.position).sqrMagnitude;
            if (sqr <= bestSqr)
            {
                bestSqr = sqr;
                best = candidate.transform;
            }
        }

        return best;
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

    /// <summary>
    /// Vision-cone gate for SIGHT acquisition only — hearing (sprint/voice/zombie noise/monkey lure) still
    /// works from any direction, so players can sneak behind. Points within arm's reach always register.
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

    /// <summary>
    /// Distance at which the Clown counts as "at" the player while chasing — its current collision radius
    /// (which grows with <see cref="ClownDynamicScale"/>) plus padding for the player's own radius. Keeping
    /// this in sync with the physical body stops the big Clown from perpetually trying to close a gap it can
    /// never reach.
    /// </summary>
    float GetChaseStopDistance()
    {
        float clownRadius = 0.5f;
        if (characterController != null)
        {
            float lossy = Mathf.Max(transform.lossyScale.x, transform.lossyScale.z);
            clownRadius = characterController.radius * Mathf.Max(0.01f, lossy);
        }

        // Auto-account for the player's own radius so the gap matches the real solid-capsule contact distance.
        // (chaseStopPadding is then just a small buffer beyond contact, not a stand-in for the player's size.)
        float playerRadius = 0.4f;
        if (_target != null)
        {
            CharacterController playerCc = _target.GetComponentInParent<CharacterController>();
            if (playerCc == null)
                playerCc = _target.GetComponentInChildren<CharacterController>();
            if (playerCc != null)
            {
                float playerLossy = Mathf.Max(playerCc.transform.lossyScale.x, playerCc.transform.lossyScale.z);
                playerRadius = playerCc.radius * Mathf.Max(0.01f, playerLossy);
            }
        }

        return clownRadius + playerRadius + Mathf.Max(0f, chaseStopPadding);
    }

    /// <summary>
    /// True when the Clown is right next to the player it is chasing. Being held at the player's body is
    /// expected contact, not a prop snag — so the prop-stuck watchdog must NOT warp-nudge here (that caused
    /// the close-range forward/back snapping and, when a nudge landed it overlapping the player, a
    /// depenetration that tunnelled it through the floor).
    /// </summary>
    bool IsBlockedByChaseTarget()
    {
        if ((_state != ClownState.Chase && _state != ClownState.Attacking) || _target == null)
            return false;

        Vector3 flat = _target.position - transform.position;
        flat.y = 0f;
        return flat.magnitude <= GetChaseStopDistance() * 1.5f;
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

        float stopDistance = GetChaseStopDistance();
        navMeshAgent.isStopped = false;
        navMeshAgent.speed = moveSpeed;
        navMeshAgent.stoppingDistance = stopDistance;

        // Arrived at the size-aware contact gap: stand and loom rather than ramming the player and running
        // in place (which also made the prop-stuck watchdog misfire and warp-jitter the Clown).
        Vector3 flatToTarget = _target.position - transform.position;
        flatToTarget.y = 0f;
        if (flatToTarget.magnitude <= stopDistance)
        {
            _intendedMoveSpeed = 0f;
            // Keep facing the player while looming. Root rotation in ApplyMovement is skipped at zero velocity,
            // so without this a player who strafes to the Clown's side/back leaves the attack cone
            // (ShouldStartAttack) and the stationary Clown would never realign or swing.
            if (flatToTarget.sqrMagnitude > 1e-4f)
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    Quaternion.LookRotation(flatToTarget.normalized),
                    rotationSpeed * Time.deltaTime);
            return Vector3.zero;
        }

        if (!TryGetTargetDestination(out Vector3 destination))
            return Vector3.zero;

        // Never re-issue while a path is still computing — SetDestination cancels the async
        // computation, and with the chased player moving more than destinationRefreshMinDistance
        // most frames this refresh otherwise fires every frame, so any route long enough to need
        // more than one frame would never finish and the chase would stall.
        bool shouldRefreshDestination =
            !navMeshAgent.pathPending
            && (Time.time >= _nextDestinationRefreshTime
                || (destination - _lastPathDestination).sqrMagnitude
                    >= destinationRefreshMinDistance * destinationRefreshMinDistance);

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

        // The chase path may not actually reach the player — e.g. they slipped onto an unreachable island or
        // ledge across a gap. The agent then builds a partial/invalid path, drives to its end short of the
        // target and stops; desiredVelocity decays to ~0, and because it's gone the prop-stuck watchdog never
        // fires, so the Clown freezes mid-corridor until the player happens to break line of sight. Detect
        // "path doesn't complete AND the agent has nothing left to follow" and fall back to investigating the
        // last-known position, exactly as losing line of sight does. desiredVelocity stays high while
        // genuinely closing on a reachable target (even through a momentarily-partial path), so a normal chase
        // is never abandoned.
        if (!navMeshAgent.pathPending
            && navMeshAgent.pathStatus != NavMeshPathStatus.PathComplete
            && navMeshAgent.desiredVelocity.sqrMagnitude < 0.04f)
        {
            SetInvestigationPoint(_target.position);
            ClearTarget();
            EnterInvestigating();
            return Vector3.zero;
        }

        Vector3 desiredVelocity = navMeshAgent.velocity.sqrMagnitude > 0.0001f
            ? navMeshAgent.velocity
            : navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        if (desiredVelocity.sqrMagnitude > moveSpeed * moveSpeed)
            desiredVelocity = desiredVelocity.normalized * moveSpeed;

        // Lean the path-following velocity toward the centre of the hall so the body doesn't clip corners.
        desiredVelocity = ApplyChaseCorridorCentering(desiredVelocity);

        return desiredVelocity;
    }

    /// <summary>
    /// Biases the chase velocity toward the centre of the corridor. The baked NavMesh path string-pulls tight
    /// against inside corners; with the Clown's body (especially once <see cref="ClownDynamicScale"/> has grown
    /// it) wider than the baked clearance, that line clips the wall and the CharacterController hangs for a beat.
    /// Probing left/right at the body and a look-ahead point and leaning toward whichever side has more room
    /// keeps speed constant, so the Clown still closes on the target — just down the middle of the hall.
    /// </summary>
    Vector3 ApplyChaseCorridorCentering(Vector3 desiredVelocity)
    {
        if (!chaseCenterInCorridor)
            return desiredVelocity;

        Vector3 flat = desiredVelocity;
        flat.y = 0f;
        float speed = flat.magnitude;
        if (speed < 0.05f)
            return desiredVelocity;

        Vector3 dir = flat / speed;
        Vector3 right = Vector3.Cross(Vector3.up, dir);
        if (right.sqrMagnitude < 1e-4f)
            return desiredVelocity;
        right.Normalize();

        float clownRadius = 0.5f;
        if (characterController != null)
        {
            float lossy = Mathf.Max(transform.lossyScale.x, transform.lossyScale.z);
            clownRadius = characterController.radius * Mathf.Max(0.01f, lossy);
        }

        float probe = clownRadius + Mathf.Max(0f, chaseWallSideClearance);
        // Walls/props only — don't steer away from the player or other actors (we WANT to reach the player).
        int mask = Physics.DefaultRaycastLayers & ~_wallSlideIgnoreLayers;

        float leftClear = probe;
        float rightClear = probe;
        AccumulateSideClearance(transform.position, right, probe, mask, ref leftClear, ref rightClear);
        if (chaseWallLookAhead > 0.01f)
            AccumulateSideClearance(
                transform.position + dir * chaseWallLookAhead, right, probe, mask, ref leftClear, ref rightClear);

        float imbalance = rightClear - leftClear; // + => more room to the right
        if (Mathf.Abs(imbalance) < 0.01f)
            return desiredVelocity;

        float lateral = Mathf.Clamp(imbalance / probe, -1f, 1f) * chaseCenterSteerStrength;
        Vector3 steered = dir + right * lateral;
        if (steered.sqrMagnitude < 1e-4f)
            return desiredVelocity;

        return steered.normalized * speed; // keep full speed, just biased toward the open side
    }

    /// <summary>
    /// Casts a left and right ray from <paramref name="origin"/> (lifted to the probe height) and records the
    /// nearest wall distance on each side, so the closer wall pulls the Clown away from it.
    /// </summary>
    void AccumulateSideClearance(Vector3 origin, Vector3 right, float probe, int mask, ref float leftClear, ref float rightClear)
    {
        origin += Vector3.up * Mathf.Max(0f, chaseWallProbeHeight);

        if (Physics.Raycast(origin, right, out RaycastHit rightHit, probe, mask, QueryTriggerInteraction.Ignore))
            rightClear = Mathf.Min(rightClear, rightHit.distance);

        if (Physics.Raycast(origin, -right, out RaycastHit leftHit, probe, mask, QueryTriggerInteraction.Ignore))
            leftClear = Mathf.Min(leftClear, leftHit.distance);
    }

    void EnterIdle()
    {
        if (_state == ClownState.Idle && _target == null)
            return;

        _state = ClownState.Idle;
        ClearTarget();
        _hasPatrolDestination = false;
        _patrolStuckAccumulatedTime = 0f;
        _propStuckAccumulatedTime = 0f;

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
        _investigationSpeedOverride = 0f;
        _state = ClownState.Chase;
    }

    // ---- Hammer swing attack -------------------------------------------------------------------------

    bool ShouldStartAttack()
    {
        if (Time.time < _suppressAttackAndChaseUntil)
            return false;
        if (_state != ClownState.Chase || _target == null || _targetHealth == null || _targetHealth.IsDead)
            return false;
        if (ShouldIgnorePlayer(_targetHealth))
            return false;

        // Don't swing at a player who is already ragdolled / held (e.g. by another Clown or a trap).
        PlayerRagdollController ragdoll = _targetHealth.GetComponent<PlayerRagdollController>();
        if (ragdoll != null && (ragdoll.IsRagdolled || ragdoll.IsHeld || ragdoll.IsGettingUp))
            return false;

        Vector3 to = _target.position - transform.position;
        if (Mathf.Abs(to.y) > Mathf.Max(0.1f, maxAttackVerticalDelta))
            return false;

        Vector3 flat = new Vector3(to.x, 0f, to.z);
        float dist = flat.magnitude;
        // The swing reach grows with the Clown (ClownDynamicScale enlarges its arms / hammer), so the giant
        // commits its swing from where the hammer can actually reach — typically a few metres out, BEFORE it has
        // had to physically close the last stretch (which the scaled-up body can struggle to do in a tight
        // hallway). The wind-up is now planted (no lunge), so triggering at hammer range no longer drags the
        // planted swing's feet across the floor the way the old "swing on the run" did.
        float reach = (attackRange + Mathf.Max(0f, attackRangePadding)) * Mathf.Max(1f, transform.lossyScale.x);
        if (dist > reach)
            return false;

        if (dist > 0.001f)
        {
            Vector3 fwd = new Vector3(transform.forward.x, 0f, transform.forward.z);
            if (fwd.sqrMagnitude < 1e-4f)
                return false;
            if (Vector3.Angle(fwd.normalized, flat / dist) > attackHalfAngle)
                return false;
        }

        return true;
    }

    void EnterAttacking()
    {
        _state = ClownState.Attacking;
        _hammerHitDone = false;
        _attackStartedTime = Time.time;
        _attackTargetMonkey = null;
        _attackTargetHealth = _targetHealth;
        _attackTargetRagdoll = _targetHealth != null ? _targetHealth.GetComponent<PlayerRagdollController>() : null;
        _attackTargetNetRagdoll = _targetHealth != null ? _targetHealth.GetComponent<NetworkPlayerRagdoll>() : null;

        // Stop the Clown's capsule colliding with this player's ragdoll bone colliders. The hit ragdolls them
        // right at the Clown's feet; depenetrating the scaled-up capsule against them on the non-convex maze
        // floor is what punched the Clown down through it — worst over a DEAD body, which never gets up and
        // which the Clown then walks over while patrolling away. Set here, BEFORE the hit, so it's in place the
        // instant the colliders go live (it persists across their enable/disable in Unity 6). The player's MAIN
        // CharacterController is left colliding, so a standing player is still body-blocked, and the hit is
        // distance-based (no grab), so nothing needs the capsule to touch the limp body.
        IgnorePlayerRagdollCollisions(_attackTargetRagdoll);

        // Face the player smoothly during the planted wind-up (see UpdateAttacking) rather than SNAPPING here:
        // the instant LookRotation was a visible pop on the frame the swing began (the "glitch"). The Clown was
        // already facing the player from the chase, so it only needs to track them from its current heading.

        // Triggers must route through the NetworkAnimator to replicate; fall back to the local animator offline.
        if (_hasSwingTriggerParameter && animator != null)
        {
            bool fired = _networkObject != null
                && _networkObject.IsSpawned
                && _networkClownAvatar != null
                && _networkClownAvatar.TryServerSetAnimatorTrigger(swingTriggerParameter);
            if (!fired)
                animator.SetTrigger(swingTriggerParameter);
        }

        // NetworkAnimator replicates parameter state but NOT one-shot triggers, so a client that joins
        // mid-swing would see an idle Clown. Record the swing animation so the late-join path in
        // NetworkClownAvatar can Play() the right state at the right normalized time.
        if (_networkObject != null && _networkObject.IsSpawned && _networkClownAvatar != null)
            _networkClownAvatar.ServerMarkAttackAnimationStarted(_swingStateHash, swingClipDurationFallback);
    }

    /// <summary>
    /// True when the Clown has been lured to a wind-up monkey, has no player to chase, and the (still-standing)
    /// monkey is within hammer reach — so it should club the toy over instead of merely lingering by it. Uses the
    /// monkey's live position (not the possibly-stale investigation point), so it only fires when genuinely next
    /// to the toy. The no-player condition is guaranteed by the call site (the target-less branch of Update).
    /// </summary>
    bool ShouldSmashMonkey()
    {
        if (Time.time < _suppressAttackAndChaseUntil)
            return false;
        if (_lureMonkey == null || _lureMonkey.IsKnockedOver)
            return false;

        Vector3 to = _lureMonkey.transform.position - transform.position;
        if (Mathf.Abs(to.y) > Mathf.Max(0.1f, maxAttackVerticalDelta))
            return false;

        Vector3 flat = new Vector3(to.x, 0f, to.z);
        float reach = (attackRange + Mathf.Max(0f, attackRangePadding)) * Mathf.Max(1f, transform.lossyScale.x);
        return flat.magnitude <= reach;
    }

    /// <summary>
    /// Begin a hammer swing aimed at a wind-up monkey. Reuses the same Hammer Swing clip/timing as the player
    /// attack; only the impact differs (<see cref="ServerHammerHit"/> knocks the monkey over rather than
    /// ragdolling a player). No player target/ragdoll fields are set.
    /// </summary>
    void EnterAttackingMonkey(WindupMonkeyAI monkey)
    {
        _state = ClownState.Attacking;
        _hammerHitDone = false;
        _attackStartedTime = Time.time;
        _attackTargetMonkey = monkey;
        _attackTargetHealth = null;
        _attackTargetRagdoll = null;
        _attackTargetNetRagdoll = null;

        // Triggers must route through the NetworkAnimator to replicate; fall back to the local animator offline.
        if (_hasSwingTriggerParameter && animator != null)
        {
            bool fired = _networkObject != null
                && _networkObject.IsSpawned
                && _networkClownAvatar != null
                && _networkClownAvatar.TryServerSetAnimatorTrigger(swingTriggerParameter);
            if (!fired)
                animator.SetTrigger(swingTriggerParameter);
        }

        // Record the swing so a client joining mid-swing can Play() the right state at the right time.
        if (_networkObject != null && _networkObject.IsSpawned && _networkClownAvatar != null)
            _networkClownAvatar.ServerMarkAttackAnimationStarted(_swingStateHash, swingClipDurationFallback);
    }

    void UpdateAttacking()
    {
        RecoverNavMeshIfOffMesh();
        UpdatePitStuckWatchdog();

        // PRIMARY trigger: drive the hammer hit off the actual animation playback (frame-accurate), so the
        // launch lands exactly on the swing's impact with no event-dispatch or wall-clock lag.
        float clipNorm = GetSwingStateNormalizedTime();
        if (clipNorm >= 0f && !_hammerHitDone && clipNorm >= hammerHitNormalizedTime)
            ServerHammerHit();

        // SAFETY fallback (only if the animator never reaches the swing state, e.g. missing clip/param).
        if (!_hammerHitDone && Time.time >= _attackStartedTime + hammerHitFallbackDelay)
            ServerHammerHit();

        // AFTER the hit connects: anchor the Clown in place (no CharacterController.Move) for the rest of the
        // swing. The freshly-spawned player ragdoll has its bone colliders enabled right at the Clown's feet,
        // and the scaled-up capsule would otherwise depenetrate against them (or a corner wall) and eject the
        // CLOWN down through the non-convex maze floor — the same wall/floor-eject safety the old frozen swing
        // had, now scoped to just the brief follow-through so the pre-impact lunge stays mobile (swing on run).
        if (_hammerHitDone)
        {
            transform.position = _attackFrozenPosition;
            if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
                navMeshAgent.nextPosition = _attackFrozenPosition;
            _horizontalVelocity = Vector3.zero;
            _verticalVelocity = Vector3.zero;
            _intendedMoveSpeed = 0f;
            UpdateAnimatorParameters();

            // Swing clip essentially done → resume chasing (a short cooldown gates the next swing; the ragdoll
            // check in ShouldStartAttack already stops it clubbing a downed player).
            if (ShouldLeaveSwingAnimation() || Time.time >= _attackStartedTime + swingClipDurationFallback + 0.3f)
                EndSwing();
            return;
        }

        // BEFORE the hit: PLANT the Clown and only track the player's facing. The Hammer Swing clip is a
        // stationary stance with NO root motion (verified by sampling the clip), so translating the body during
        // the wind-up dragged its planted feet across the floor — the "runs in place / glitch" feel. Holding
        // position and only rotating keeps the feet planted (matching the clip) while still aiming the swing at a
        // player who circles or backs off; the wide hammerHitReachMultiplier re-check at impact still lets the
        // giant's hammer connect with someone who gave a little ground. Gravity/grounding still run via
        // ApplyMovement(zero) so the capsule stays on the floor (and the post-hit anchor below is then seamless,
        // since there is no lunge velocity left to kill).
        Vector3 facePoint = Vector3.zero;
        bool hasFacePoint = false;
        if (_attackTargetMonkey != null && !_attackTargetMonkey.IsKnockedOver)
        {
            facePoint = _attackTargetMonkey.transform.position;
            hasFacePoint = true;
        }
        else if (_target != null && _targetHealth != null && !_targetHealth.IsDead
            && !ShouldIgnorePlayer(_targetHealth))
        {
            facePoint = _target.position;
            hasFacePoint = true;
        }
        if (hasFacePoint)
        {
            Vector3 face = facePoint - transform.position;
            face.y = 0f;
            if (face.sqrMagnitude > 1e-4f)
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation,
                    Quaternion.LookRotation(face.normalized),
                    rotationSpeed * Time.deltaTime);
        }

        ApplyMovement(Vector3.zero);
        UpdateAnimatorParameters();
    }

    /// <summary>Normalized time (0-1+) of the Hammer Swing state if it's currently playing (incl. during the
    /// blend-in, where it's the "next" state); -1 if the animator isn't in that state.</summary>
    float GetSwingStateNormalizedTime()
    {
        if (animator == null)
            return -1f;

        AnimatorStateInfo cur = animator.GetCurrentAnimatorStateInfo(0);
        if (cur.shortNameHash == _swingStateHash)
            return cur.normalizedTime;

        if (animator.IsInTransition(0))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
            if (next.shortNameHash == _swingStateHash)
                return next.normalizedTime;
        }

        return -1f;
    }

    bool ShouldLeaveSwingAnimation()
    {
        if (animator == null)
            return Time.time >= _attackStartedTime + swingClipDurationFallback;

        AnimatorStateInfo si = animator.GetCurrentAnimatorStateInfo(0);
        if (si.shortNameHash == _swingStateHash)
            return si.normalizedTime >= 0.95f;

        return Time.time >= _attackStartedTime + swingClipDurationFallback * 0.85f;
    }

    /// <summary>
    /// The hammer connects: launch the target player into ragdoll (a knockback, like <see cref="RagdollTrap"/>)
    /// — away from the Clown, redirected to an open direction so a hallway hit isn't flung into a side wall.
    /// One-shot and idempotent; never pins/holds the player.
    /// </summary>
    void ServerHammerHit()
    {
        if (!ShouldRunSimulation() || _hammerHitDone)
            return;

        _hammerHitDone = true;

        // Anchor the Clown where it connected for the rest of the swing (see UpdateAttacking). The hit spawns
        // the player ragdoll (bone colliders enabled) right at the Clown's feet; without this, the scaled-up
        // CharacterController's next Move() depenetrates against those colliders and ejects the CLOWN down
        // through the non-convex maze floor.
        _attackFrozenPosition = transform.position;

        // Wind-up monkey smash: knock the toy over (silencing the Clown lure) instead of ragdolling a player.
        if (_attackTargetMonkey != null)
        {
            ServerHammerHitMonkey();
            return;
        }

        if (_attackTargetHealth == null || _attackTargetHealth.IsDead)
            return;

        // Nothing catches the player early (unlike the old grab), so they're free to run during the wind-up.
        // Only connect if they're still within the hammer's reach at the impact frame — a clean dodge whiffs.
        float reach = (attackRange + Mathf.Max(0f, attackRangePadding))
            * Mathf.Max(1f, transform.lossyScale.x) * Mathf.Max(1f, hammerHitReachMultiplier);
        Vector3 flatToTarget = _attackTargetHealth.transform.position - transform.position;
        flatToTarget.y = 0f;
        if (flatToTarget.magnitude > reach)
            return; // player got out of the way

        // Knock the player away from the Clown (flattened to horizontal) — the hammer sweeps in front, so this
        // reads as being clubbed off their feet.
        Vector3 knockDir = _attackTargetHealth.transform.position - transform.position;
        knockDir.y = 0f;
        if (knockDir.sqrMagnitude > 1e-4f)
            knockDir.Normalize();
        else
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            knockDir = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
        }

        Vector3 hitPoint = _attackTargetHealth.transform.position + Vector3.up;

        // In tight maze hallways the straight-away direction can point into a wall, so redirect the knockback
        // to the most open direction (down the hallway) nearest the intent, and scale the forward speed down
        // if even the best direction is tight (so the ragdoll can't tunnel through a nearby wall).
        float forwardFactor = 1f;
        if (knockbackAvoidWalls)
        {
            knockDir = ResolveOpenKnockbackDirection(hitPoint, knockDir, out float clearance);
            forwardFactor = Mathf.Clamp01(clearance / Mathf.Max(0.5f, knockbackForwardFullSpeedClearance));
        }

        Vector3 force = (knockDir * knockbackForwardSpeed * forwardFactor) + (Vector3.up * knockbackUpwardSpeed);

        // Re-affirm the ragdoll-collider ignore at the exact moment the bone colliders go live (idempotent).
        IgnorePlayerRagdollCollisions(_attackTargetRagdoll);

        bool inNetSession = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        if (inNetSession && _attackTargetNetRagdoll != null)
        {
            _attackTargetNetRagdoll.RequestTrapHitFromServer(force, hitPoint, hammerDamage, knockbackForceMode);
        }
        // No networked ragdoll on the target (misconfigured/non-networked player prefab): fall through to the
        // local damage + ragdoll path instead of silently whiffing the swing — matches RagdollTrap's behavior.
        else if (_attackTargetRagdoll != null)
        {
            bool survived = true;
            if (hammerDamage > 0f)
            {
                _attackTargetHealth.TakeDamage(hammerDamage);
                survived = !_attackTargetHealth.IsDead;
            }
            _attackTargetRagdoll.ActivateRagdoll(force, hitPoint, knockbackForceMode, allowAutoRecovery: survived);
        }
    }

    /// <summary>
    /// The hammer connects with a wind-up monkey: knock it over — the same effect a crouched player punch has
    /// (freeze/silence + physics topple). One-shot; whiffs cleanly if the toy shuffled out of reach during the
    /// wind-up. The knock-over direction points from the Clown to the monkey (matching the player-punch convention).
    /// </summary>
    void ServerHammerHitMonkey()
    {
        WindupMonkeyAI monkey = _attackTargetMonkey;
        if (monkey == null || monkey.IsKnockedOver)
            return;

        // Only connect if the monkey is still within the hammer's reach at the impact frame (it keeps shuffling
        // forward). Mirrors the player-hit reach re-check.
        float reach = (attackRange + Mathf.Max(0f, attackRangePadding))
            * Mathf.Max(1f, transform.lossyScale.x) * Mathf.Max(1f, hammerHitReachMultiplier);
        Vector3 flatTo = monkey.transform.position - transform.position;
        flatTo.y = 0f;
        if (flatTo.magnitude > reach)
            return; // monkey stepped out of reach

        Vector3 knockDir = flatTo.sqrMagnitude > 1e-4f ? flatTo.normalized : transform.forward;
        monkey.ServerKnockOver(knockDir);
    }

    /// <summary>
    /// Returns the horizontal launch direction nearest <paramref name="intendedDir"/> that is actually open
    /// (so a hallway hit throws the player down the hall instead of into a side wall). If the intended
    /// direction is already clear it's kept unchanged (open rooms behave as before).
    /// </summary>
    Vector3 ResolveOpenKnockbackDirection(Vector3 hitPoint, Vector3 intendedDir, out float chosenClearance)
    {
        intendedDir.y = 0f;
        if (intendedDir.sqrMagnitude < 1e-4f)
        {
            Vector3 f = transform.forward; f.y = 0f;
            intendedDir = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        }
        else
            intendedDir.Normalize();

        // Everything except actors blocks (auto) unless an explicit mask is set.
        int mask = knockbackObstacleMask.value != 0 ? knockbackObstacleMask.value : ~_wallSlideIgnoreLayers;
        Vector3 origin = new Vector3(hitPoint.x, hitPoint.y + knockbackProbeHeight, hitPoint.z);

        // If the intended direction is already open, keep it (open room / aligned hallway).
        float intendedClear = MeasureClearance(origin, intendedDir, mask);
        if (intendedClear >= knockbackMinClearance)
        {
            chosenClearance = intendedClear;
            return intendedDir;
        }

        // Otherwise sweep the circle: among directions that are open enough, take the one most aligned with
        // the knockback intent; if none are open enough, take the single most open direction (best escape).
        const int samples = 24;
        Vector3 bestOpenAligned = intendedDir;
        float bestOpenAlignedClear = intendedClear;
        float bestAlign = float.NegativeInfinity;
        bool anyOpen = false;
        Vector3 mostOpenDir = intendedDir;
        float mostOpenClear = -1f;

        for (int i = 0; i < samples; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, i * (360f / samples), 0f) * Vector3.forward;
            float clear = MeasureClearance(origin, dir, mask);
            if (clear > mostOpenClear)
            {
                mostOpenClear = clear;
                mostOpenDir = dir;
            }
            if (clear >= knockbackMinClearance)
            {
                anyOpen = true;
                float align = Vector3.Dot(dir, intendedDir);
                if (align > bestAlign)
                {
                    bestAlign = align;
                    bestOpenAligned = dir;
                    bestOpenAlignedClear = clear;
                }
            }
        }

        if (anyOpen)
        {
            chosenClearance = bestOpenAlignedClear;
            return bestOpenAligned;
        }
        chosenClearance = mostOpenClear;
        return mostOpenDir;
    }

    float MeasureClearance(Vector3 origin, Vector3 dir, int mask)
    {
        if (Physics.SphereCast(origin, knockbackProbeRadius, dir, out RaycastHit hit,
                knockbackProbeRange, mask, QueryTriggerInteraction.Ignore))
            return hit.distance;
        return knockbackProbeRange;
    }

    /// <summary>
    /// Tells the Clown's CharacterController to pass through the given player's ragdoll bone colliders — the
    /// limbs that go live the instant the hammer ragdolls them, right at the Clown's feet. Without this the
    /// scaled-up capsule depenetrates against them on the non-convex maze floor and can be ejected straight
    /// DOWN through it — most reliably over a DEAD body, which never gets up and which the Clown walks over as
    /// it patrols away. The hit is distance-based (no grab) and the player's MAIN CharacterController is left
    /// colliding, so a standing player is still body-blocked; only the limp ragdoll limbs are ignored. The
    /// ignore persists across the colliders' enable/disable (Unity 6) and is harmless to set repeatedly.
    /// </summary>
    void IgnorePlayerRagdollCollisions(PlayerRagdollController ragdoll)
    {
        if (ragdoll == null || characterController == null)
            return;

        IReadOnlyList<Collider> cols = ragdoll.RagdollColliders;
        if (cols == null)
            return;

        for (int i = 0; i < cols.Count; i++)
        {
            Collider col = cols[i];
            if (col != null)
                Physics.IgnoreCollision(characterController, col, true);
        }
    }

    /// <summary>
    /// Ends the swing and hands control straight back to the chase (no frozen recover state). A short
    /// <see cref="postAttackCooldownSeconds"/> cooldown gates the next swing, but the Clown keeps pursuing in
    /// the meantime — so it stays on the player and clubs them again the moment they recover, instead of
    /// standing still or running in place.
    /// </summary>
    void EndSwing()
    {
        _suppressAttackAndChaseUntil = Time.time + Mathf.Max(0.25f, postAttackCooldownSeconds);
        _attackTargetHealth = null;
        _attackTargetRagdoll = null;
        _attackTargetNetRagdoll = null;
        _attackTargetMonkey = null;
        // If the swing toppled the lured monkey, drop the reference so we don't try to re-smash a dead toy.
        if (_lureMonkey != null && _lureMonkey.IsKnockedOver)
            _lureMonkey = null;

        // Keep chasing the same live target; only drop it if it died / became unreachable so we don't loiter
        // on a stale target (the normal chase flow re-acquires or falls back to patrol next frame).
        if (_targetHealth == null || _targetHealth.IsDead || ShouldIgnorePlayer(_targetHealth))
            ClearTarget();

        _state = ClownState.Chase;

        if (navMeshAgent != null && navMeshAgent.isOnNavMesh)
            navMeshAgent.isStopped = false;

        // Swing clip is finished; clear the late-join snapshot so any subsequent joiner sees the Clown idle.
        if (_networkClownAvatar != null)
            _networkClownAvatar.ServerMarkAttackAnimationEnded();
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

    // Approach speed used while Investigating: the lure override (run) if set, else the normal patrol/search speed.
    float InvestigationMoveSpeed => _investigationSpeedOverride > 0.01f ? _investigationSpeedOverride : patrolSpeed;

    /// <summary>
    /// Public command (used by the wind-up monkey's clap) to make the Clown RUN to a world position, no matter
    /// how far away he is. Won't pull him off a player he's actively chasing/attacking. If he's already lingering
    /// at that spot, just refreshes the linger so he keeps hanging around the monkey.
    /// </summary>
    public void LureToPosition(Vector3 worldPoint, WindupMonkeyAI monkey = null)
    {
        if (_state == ClownState.Chase || _state == ClownState.Attacking)
            return;

        // Track the monkey behind this lure so the Clown can club it over once it arrives (ShouldSmashMonkey).
        _lureMonkey = monkey;

        if (_state == ClownState.Investigating && _isLingerAtInvestigationPoint
            && (worldPoint - _investigationPoint).sqrMagnitude
                <= investigationArrivalDistance * investigationArrivalDistance)
        {
            _investigationLingerEndTime = Time.time + Mathf.Max(0f, investigationLingerSeconds);
            return;
        }

        _investigationSpeedOverride = Mathf.Max(0.01f, runSpeed);
        SetInvestigationPoint(worldPoint);
        EnterInvestigating();
    }

    void EnterInvestigating()
    {
        if (_state != ClownState.Investigating)
            _state = ClownState.Investigating;
        _chaseLineOfSightLostSince = -1f;

        _intendedMoveSpeed = InvestigationMoveSpeed;
        if (!TrySnapToNavMesh())
            return;
        if (navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = InvestigationMoveSpeed;
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
        _intendedMoveSpeed = InvestigationMoveSpeed;
        if (!_hasInvestigationPoint || !TrySnapToNavMesh() || navMeshAgent == null || !navMeshAgent.isOnNavMesh)
            return Vector3.zero;

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = InvestigationMoveSpeed;
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
            !navMeshAgent.pathPending
            && (Time.time >= _nextDestinationRefreshTime
                || (targetPoint - _lastPathDestination).sqrMagnitude
                    >= destinationRefreshMinDistance * destinationRefreshMinDistance);

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
            _investigationSpeedOverride = 0f; // arrived; linger/search at normal patrol speed
            navMeshAgent.isStopped = true;
            navMeshAgent.ResetPath();
            return Vector3.zero;
        }

        Vector3 desiredVelocity = navMeshAgent.velocity.sqrMagnitude > 0.0001f
            ? navMeshAgent.velocity
            : navMeshAgent.desiredVelocity;
        desiredVelocity.y = 0f;
        float approachSpeed = InvestigationMoveSpeed;
        if (desiredVelocity.sqrMagnitude > approachSpeed * approachSpeed)
            desiredVelocity = desiredVelocity.normalized * approachSpeed;
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

        _positionBeforeCharacterMove = transform.position;

        bool grounded = characterController.isGrounded;
        if (grounded && _verticalVelocity.y < 0f)
            _verticalVelocity.y = -groundedStickDown;

        _horizontalVelocity = ApplyWallSlide(desiredHorizontalVelocity);
        _verticalVelocity.y += gravity * Time.deltaTime;

        Vector3 motion = _horizontalVelocity * Time.deltaTime;
        motion.y = _verticalVelocity.y * Time.deltaTime;
        characterController.Move(motion);

        // Floor-tunnel guard. CharacterController.Move() depenetrates the capsule against every collider it
        // overlaps. Right after a hammer hit the just-clubbed player's ragdoll has its bone colliders enabled
        // at the Clown's feet (and a hard lunge can leave the scaled-up capsule pressed into a corner); on the
        // maze's non-convex MeshCollider floor that depenetration can shove the capsule straight DOWN through
        // the floor — the "Clown falls through the floor when he hits me" bug. Gravity is the only thing that
        // should ever lower the body, so any descent this frame BEYOND the intended gravity step is a
        // depenetration shove: cancel just that vertical excess (the horizontal pop-out of the wall/ragdoll is
        // kept) and pin the body back on the floor. A real gravity fall descends by exactly the intended step,
        // so it stays under the threshold and is never blocked — the pit watchdog still handles genuine pits.
        float intendedDrop = Mathf.Max(0f, -motion.y);
        float actualDrop = _positionBeforeCharacterMove.y - transform.position.y;
        if (actualDrop > intendedDrop + Mathf.Max(0.05f, floorTunnelMaxExtraDrop) + characterController.skinWidth)
        {
            Vector3 corrected = transform.position;
            corrected.y = _positionBeforeCharacterMove.y - intendedDrop;
            transform.position = corrected;
            if (_verticalVelocity.y < 0f)
                _verticalVelocity.y = -groundedStickDown;
        }

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

        if (Time.time >= _nextPropStuckRecoveryTime && !IsBlockedByChaseTarget())
        {
            Vector3 desiredFlat = desiredHorizontalVelocity;
            desiredFlat.y = 0f;
            float desiredMag = desiredFlat.magnitude;

            Vector3 movedFlat = transform.position - _positionBeforeCharacterMove;
            movedFlat.y = 0f;
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            float actualMag = movedFlat.magnitude / dt;

            if (desiredMag >= propStuckDesiredSpeedThreshold && actualMag <= propStuckActualSpeedThreshold)
                _propStuckAccumulatedTime += Time.deltaTime;
            else
                _propStuckAccumulatedTime = 0f;

            if (_propStuckAccumulatedTime >= propStuckAccumulateSeconds)
            {
                if (TryRecoverFromPropStuck())
                {
                    _propStuckAccumulatedTime = 0f;
                    _nextPropStuckRecoveryTime = Time.time + Mathf.Max(0.1f, propStuckRecoveryCooldown);
                }
                else
                    _propStuckAccumulatedTime = 0f;
            }
        }
        else
            _propStuckAccumulatedTime = 0f;
    }

    /// <summary>
    /// Records the most recent wall (near-vertical) contact so <see cref="ApplyWallSlide"/> can deflect
    /// movement along it. Without this, the scaled-up body (whose CharacterController radius is larger than
    /// the NavMesh path's baked clearance) grinds head-on into outer corners and stalls for a moment.
    /// </summary>
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (!enableWallSlide || hit.collider == null)
            return;

        if ((_wallSlideIgnoreLayers & (1 << hit.gameObject.layer)) != 0)
            return; // players / other enemies are not walls to slide along

        if (Mathf.Abs(hit.normal.y) >= wallSlideMaxNormalY)
            return; // floor or ceiling, not a wall

        _wallSlideNormal = hit.normal;
        _wallSlideHitTime = Time.time;
    }

    /// <summary>
    /// If the desired velocity is pushing into a freshly-touched wall, project it onto the wall plane and
    /// restore full speed so the Clown slides smoothly around the corner instead of grinding to a crawl.
    /// </summary>
    Vector3 ApplyWallSlide(Vector3 desiredHorizontalVelocity)
    {
        if (!enableWallSlide)
            return desiredHorizontalVelocity;

        Vector3 desired = desiredHorizontalVelocity;
        desired.y = 0f;
        float speed = desired.magnitude;
        if (speed < 0.05f || Time.time - _wallSlideHitTime > wallSlideMemorySeconds)
            return desiredHorizontalVelocity;

        Vector3 normal = _wallSlideNormal;
        normal.y = 0f;
        if (normal.sqrMagnitude < 1e-4f)
            return desiredHorizontalVelocity;
        normal.Normalize();

        float into = Vector3.Dot(desired, normal);
        if (into >= -0.01f)
            return desiredHorizontalVelocity; // already moving away from / parallel to the wall

        Vector3 slide = desired - normal * into; // remove the into-wall component (project onto wall plane)
        if (slide.sqrMagnitude < 1e-4f)
            return desiredHorizontalVelocity; // dead-on into a wall facing the goal — let prop-stuck handle it

        return slide.normalized * speed; // keep full speed, just along the wall
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
                    $"[ClownAI] Animator controller '{controllerName}' is missing required parameters " +
                    $"('{speedParameter}' float, '{groundedParameter}' bool, '{verticalVelocityParameter}' float). " +
                    "Clown movement animation sync is disabled until those parameters exist.",
                    this);
                _loggedMissingAnimatorParams = true;
            }
            return;
        }

        float horizontal = new Vector3(_horizontalVelocity.x, 0f, _horizontalVelocity.z).magnitude;
        if (_state == ClownState.Idle)
            horizontal = 0f;

        float targetNormalized = runSpeed > 0.001f ? Mathf.Clamp01(horizontal / runSpeed) : 0f;
        if (targetNormalized < idleSpeedDeadZone)
            targetNormalized = 0f;
        if (alwaysRunWhenChasing
            && _state == ClownState.Chase
            && _targetHealth != null
            && targetNormalized > 0.08f)
            targetNormalized = 1f;

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

    /// <summary>Pushes agent back onto NavMesh after pits / physics pushes.</summary>
    void RecoverNavMeshIfOffMesh()
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || navMeshAgent.isOnNavMesh)
            return;

        TryWarpToNearestNavMesh(NavMeshSnapRadiiAggressive);
    }

    /// <summary>
    /// Defends against the case where the Clown falls into a pit but lands on a tiny NavMesh island —
    /// <see cref="RecoverNavMeshIfOffMesh"/> skips while <c>isOnNavMesh</c> is true and the pit's KillZone trigger may not reach him.
    /// Compares his Y to the nearest rim NavMesh point; if he stays meaningfully below it without moving, warp him out.
    /// </summary>
    void UpdatePitStuckWatchdog()
    {
        if (navMeshAgent == null || !navMeshAgent.enabled)
        {
            _pitStuckAccumulatedTime = 0f;
            return;
        }

        // Do NOT skip this while IsBlockedByChaseTarget() (looming at the player). The rescue only fires when
        // the Clown sits >= pitStuckBelowNavMeshThreshold BELOW the nearest NavMesh, which never happens during
        // a normal loom on solid floor (drop ~= 0) — but DOES happen if a hammer-hit depenetration punched him
        // through the floor while looming over the player he just clubbed. Gating this on the loom (as it used
        // to) disabled the exact rescue that case needs, so the Clown stayed under the floor; the drop
        // threshold below already keeps a legitimate loom from being warped off its target.
        if (Time.time < _nextPitStuckCheckTime)
            return;
        _nextPitStuckCheckTime = Time.time + Mathf.Max(0.05f, pitStuckCheckInterval);

        Vector3 origin = transform.position + Vector3.up * Mathf.Max(1f, pitStuckVerticalSearchHeight);
        NavMeshHit hit = default;
        bool sampled = false;
        if (pitStuckSampleRadii != null)
        {
            for (int i = 0; i < pitStuckSampleRadii.Length; i++)
            {
                float radius = pitStuckSampleRadii[i];
                if (radius <= 0f)
                    continue;
                if (NavMesh.SamplePosition(origin, out hit, radius, NavMesh.AllAreas))
                {
                    sampled = true;
                    break;
                }
            }
        }

        if (!sampled)
        {
            _pitStuckAccumulatedTime = 0f;
            return;
        }

        float drop = hit.position.y - transform.position.y;
        Vector3 horizontalVelocity = navMeshAgent.velocity;
        horizontalVelocity.y = 0f;
        float horizontalSpeed = horizontalVelocity.magnitude;

        if (drop >= pitStuckBelowNavMeshThreshold && horizontalSpeed <= pitStuckLowSpeedThreshold)
            _pitStuckAccumulatedTime += Mathf.Max(0.05f, pitStuckCheckInterval);
        else
            _pitStuckAccumulatedTime = 0f;

        if (_pitStuckAccumulatedTime < pitStuckAccumulateSeconds || Time.time < _nextPitStuckRescueTime)
            return;

        Vector3 safePosition = hit.position + Vector3.up * Mathf.Max(0f, pitStuckRescueLift);
        WarpTransformToNavMeshPoint(safePosition);
        _pitStuckAccumulatedTime = 0f;
        _nextPitStuckRescueTime = Time.time + Mathf.Max(0.25f, pitStuckRescueCooldown);
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

    void EnsureEnemyAndClownLayerSetup()
    {
        int clownLayer = LayerMask.NameToLayer(ClownLayerName);
        if (clownLayer >= 0 && gameObject.layer != clownLayer)
            gameObject.layer = clownLayer;

        if (s_HasConfiguredClownCollision)
            return;

        if (clownLayer < 0)
        {
            Debug.LogWarning(
                $"[{nameof(ClownAI)}] Missing layer '{ClownLayerName}'. " +
                "Add it in Project Settings > Tags and Layers so the Clown does not shove other enemies.",
                this);
            return;
        }

        int enemyLayer = LayerMask.NameToLayer(EnemyLayerName);
        int jailorLayer = LayerMask.NameToLayer(JailorLayerName);
        if (enemyLayer >= 0)
            Physics.IgnoreLayerCollision(enemyLayer, clownLayer, true);
        if (jailorLayer >= 0)
            Physics.IgnoreLayerCollision(jailorLayer, clownLayer, true);
        s_HasConfiguredClownCollision = true;
    }

    sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
    {
        public static readonly RaycastHitDistanceComparer Instance = new();

        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}
