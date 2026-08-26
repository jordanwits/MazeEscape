using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Carnival suicide bomber. A clockwork toy that stands dead still with a stick of dynamite in each fist
/// until something provokes it, then sprints at the nearest player and blows itself up.
///
/// Server-authoritative in the house idiom: the server owns the state machine, the NavMeshAgent computes
/// paths while the CharacterController does the moving, and clients see the result through the
/// NetworkTransform on <see cref="NetworkBomberAvatar"/>. The animator runs a masked two-layer rig — the
/// base layer plays Idle/Run (legs and torso) while the "Dynamite Hold" layer pins the arms in the
/// dynamite-out pose — so the only parameter this AI ever touches is the <c>Chasing</c> bool, which
/// <see cref="ServerNetworkAnimator"/> replicates for free.
///
/// The detonation itself is one shot: damage plus a radial ragdoll launch adjudicated on the server through
/// <see cref="NetworkPlayerRagdoll.RequestTrapHitFromServer"/> (the single sanctioned trap-hit entry point),
/// an FX RPC so every peer sees the same blast, then the NetworkObject despawns. There is no corpse.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class BomberAI : NetworkBehaviour, IBlindableEnemy, ILurableEnemy
{
    enum BomberState : byte
    {
        Idle = 0,     // frozen toy, scanning for a reason to wake up
        Chase = 1,    // committed run at the target
        Priming = 2,  // fuse lit, standing still, about to go off
        Spent = 3,    // detonated; waiting out the despawn
    }

    [Header("Refs (auto-resolved if left empty)")]
    [SerializeField] Animator animator;
    [SerializeField] NavMeshAgent navMeshAgent;
    [SerializeField] CharacterController characterController;
    [Tooltip("Positional source for the fuse sizzle and the run-up giggle. Auto-created if left empty.")]
    [SerializeField] AudioSource voiceAudioSource;

    [Header("Animator")]
    [Tooltip("Bool on the base layer that swaps Idle for Run. The upper-body hold layer is weight-1 always.")]
    [SerializeField] string chasingBoolParam = "Chasing";

    [Header("Provocation (idle until one of these trips)")]
    [Tooltip("Sees a player this far ahead, inside the view cone, with a clear line of sight.")]
    [SerializeField, Min(0f)] float sightRadius = 14f;
    [Tooltip("Full width of the view cone, in degrees.")]
    [SerializeField, Range(10f, 360f)] float sightAngle = 120f;
    [Tooltip("Inside this radius he wakes regardless of facing or walls -- he hears you coming.")]
    [SerializeField, Min(0f)] float noticeRadius = 4f;
    [SerializeField] bool requireLineOfSight = true;
    [SerializeField] LayerMask lineOfSightMask = Physics.DefaultRaycastLayers;
    [Tooltip("Height above the pivot that the sight ray leaves from and aims at.")]
    [SerializeField] float lineOfSightHeight = 0.9f;
    [Tooltip("Seconds between target scans while idle. Cheap, so this can stay short.")]
    [SerializeField, Min(0.02f)] float scanInterval = 0.2f;

    [Header("Chase")]
    [SerializeField, Min(0f)] float chaseSpeed = 4.2f;
    [SerializeField, Min(0f)] float acceleration = 20f;
    [SerializeField, Min(0f)] float turnSpeedDegreesPerSecond = 420f;
    [SerializeField, Min(0f)] float gravity = 20f;
    [Tooltip("Seconds between destination refreshes while chasing.")]
    [SerializeField, Min(0.02f)] float repathInterval = 0.15f;
    [Tooltip("Once provoked he never calms down; he just re-targets whoever is nearest.")]
    [SerializeField] bool relentless = true;

    [Header("Detonation")]
    [Tooltip("Gap to the target that lights the fuse. Measured between body surfaces, so the player's own " +
             "radius is accounted for. Bigger = the sparklers light earlier and warn from further out.")]
    [SerializeField, Min(0f)] float detonateDistance = 3f;
    [Tooltip("Seconds the fuse burns before the blast. This is the player's window to break away, and how " +
             "long the sparklers are on screen.")]
    [SerializeField, Min(0f)] float fuseSeconds = 1.6f;
    [Tooltip("Keep charging while the fuse burns instead of planting his feet. Leave ON: with a fuse that " +
             "lights several metres out, standing still would turn the warning into a free escape.")]
    [SerializeField] bool chaseWhileFuseBurns = true;
    [Tooltip("Anything beyond this from the blast takes nothing.")]
    [SerializeField, Min(0.5f)] float blastRadius = 6f;
    [Tooltip("Inside this fraction of the radius the blast is at full strength before it starts falling off.")]
    [SerializeField, Range(0f, 1f)] float fullDamageRadiusFraction = 0.35f;
    [Tooltip("Damage at the centre of the blast. Falls off to zero at the edge of the radius.")]
    [SerializeField, Min(0f)] float blastDamage = 75f;
    [Tooltip("Outward launch speed at the centre of the blast.")]
    [SerializeField, Min(0f)] float blastLaunchSpeed = 16f;
    [Tooltip("Upward launch speed at the centre of the blast, added to the outward push. This is the one that " +
             "really sets how far a victim travels -- it buys the airtime the outward push then spends.")]
    [SerializeField, Min(0f)] float blastLaunchUpSpeed = 6f;
    [SerializeField] ForceMode blastForceMode = ForceMode.VelocityChange;
    [Tooltip("A wall between the bomber and a victim stops the blast entirely.")]
    [SerializeField] bool blastRequiresLineOfSight = true;
    [Tooltip("Height above each pivot used for the blast line-of-sight check and the launch origin.")]
    [SerializeField] float blastHeight = 0.9f;

    [Header("Fuse presentation")]
    [Tooltip("Both dynamite sticks. Auto-collected from children named *Dynamite if left empty.")]
    [SerializeField] Transform[] dynamiteSticks = new Transform[0];
    [Tooltip("How much the sticks swell while the fuse burns, as a multiplier on their authored scale.")]
    [SerializeField, Min(1f)] float fusePulseScale = 1.18f;
    [Tooltip("Pulses per second while the fuse burns.")]
    [SerializeField, Min(0f)] float fusePulseHz = 7f;
    [Tooltip("Sparkler objects at each fuse tip, inactive on the prefab and switched on when the fuse " +
             "lights. Auto-collected from children named FuseSpark if left empty.")]
    [SerializeField] GameObject[] fuseSparks = new GameObject[0];

    [Header("Blast FX")]
    [Tooltip("The BomberExplosion prefab (fireball + smoke + embers + flash), spawned locally on every peer " +
             "at the detonation point. Leave empty to fall back to a bare light flash.")]
    [SerializeField] GameObject explosionFxPrefab;

    [Header("Audio")]
    [Tooltip("Looping sizzle while the fuse burns.")]
    [SerializeField] AudioClip fuseClip;
    [Tooltip("The blast, played positionally on every peer at the detonation point.")]
    [SerializeField] AudioClip explosionClip;
    [SerializeField, Range(0f, 1f)] float explosionVolume = 1f;

    static readonly RaycastHit[] s_lineOfSightHits = new RaycastHit[12];
    static readonly List<PlayerHealth> s_blastVictims = new List<PlayerHealth>(8);
    /// <summary>Widening snap radii for <see cref="SetDestination"/>; see the note there.</summary>
    static readonly float[] s_destinationSnapRadii = { 2.5f, 6f, 12f };

    readonly NetworkVariable<bool> _fuseLit = new(false);

    BomberState _state = BomberState.Idle;
    PlayerHealth _target;
    EnemyBlindEffect _blindEffect;
    Vector3 _lurePoint;
    bool _hasLurePoint;
    bool _provoked;
    float _verticalVelocity;
    float _nextScanTime;
    float _nextRepathTime;
    float _detonateAtTime;
    Vector3[] _dynamiteBaseScales = new Vector3[0];

    /// <summary>True once anything has woken him. A wound-up bomber never stands down.</summary>
    public bool IsProvoked => _provoked;

    /// <summary>True from the moment the fuse lights. Latched -- nothing can put it out.</summary>
    public bool IsFuseLit => _fuseLit.Value || _state == BomberState.Priming || _state == BomberState.Spent;

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>(true);
        if (navMeshAgent == null) navMeshAgent = GetComponent<NavMeshAgent>();
        if (characterController == null) characterController = GetComponent<CharacterController>();

        if (navMeshAgent != null)
        {
            // Zombie/Clown/RatBot idiom: the agent only computes paths, the CharacterController does the moving.
            navMeshAgent.updatePosition = false;
            navMeshAgent.updateRotation = false;
            navMeshAgent.speed = chaseSpeed;
            navMeshAgent.acceleration = acceleration;
        }

        if (animator != null)
        {
            // He is often the only thing down a dark corridor; a culled animator would freeze the run mid-stride.
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }

        ResolveDynamiteSticks();
        EnsureVoiceSource();
    }

    void ResolveDynamiteSticks()
    {
        if (dynamiteSticks == null || dynamiteSticks.Length == 0)
        {
            var found = new List<Transform>(2);
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name.EndsWith("Dynamite", System.StringComparison.Ordinal))
                    found.Add(t);
            }
            dynamiteSticks = found.ToArray();
        }

        _dynamiteBaseScales = new Vector3[dynamiteSticks.Length];
        for (int i = 0; i < dynamiteSticks.Length; i++)
            _dynamiteBaseScales[i] = dynamiteSticks[i] != null ? dynamiteSticks[i].localScale : Vector3.one;

        if (fuseSparks == null || fuseSparks.Length == 0)
        {
            var found = new List<GameObject>(2);
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith("FuseSpark", System.StringComparison.Ordinal))
                    found.Add(t.gameObject);
            }
            fuseSparks = found.ToArray();
        }
    }

    void EnsureVoiceSource()
    {
        if (voiceAudioSource == null)
            voiceAudioSource = GetComponent<AudioSource>();
        if (voiceAudioSource == null)
            return;

        voiceAudioSource.playOnAwake = false;
        voiceAudioSource.spatialBlend = 1f;
        voiceAudioSource.dopplerLevel = 0f;
        voiceAudioSource.rolloffMode = AudioRolloffMode.Linear;
        if (voiceAudioSource.maxDistance < 1f)
            voiceAudioSource.maxDistance = 22f;
        GameAudioManager.RouteSfxSource(voiceAudioSource);
    }

    public override void OnNetworkSpawn()
    {
        _fuseLit.OnValueChanged += HandleFuseLitChanged;
        if (_fuseLit.Value)
            ApplyFuseVisual(true); // late joiner arriving mid-fuse
    }

    public override void OnNetworkDespawn()
    {
        _fuseLit.OnValueChanged -= HandleFuseLitChanged;
    }

    /// <summary>Server / offline runs the brain; observer clients only render what it decides.</summary>
    bool ShouldSimulate =>
        NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || IsServer;

    // ------------------------------------------------------------------ brain (server)

    void Update()
    {
        // Clients still tick the fuse pulse -- it is driven by the replicated _fuseLit, not by the state machine.
        UpdateFuseVisual();

        if (!ShouldSimulate || _state == BomberState.Spent)
            return;

        // A flashbang stops him mid-run and sends him groping in circles -- but a lit fuse is a lit fuse.
        if (_state != BomberState.Priming && EnemyBlindEffect.IsBlinded(ref _blindEffect, gameObject))
        {
            _target = null;
            SetChasingAnimation(false);
            ApplyMotion(_blindEffect != null ? _blindEffect.TickWanderVelocity(transform, chaseSpeed) : Vector3.zero);
            return;
        }

        switch (_state)
        {
            case BomberState.Idle:    TickIdle();    break;
            case BomberState.Chase:   TickChase();   break;
            case BomberState.Priming: TickPriming(); break;
        }
    }

    void TickIdle()
    {
        SetChasingAnimation(false);
        ApplyMotion(Vector3.zero);   // gravity only, so he settles onto the floor

        if (Time.time < _nextScanTime)
            return;
        _nextScanTime = Time.time + scanInterval;

        if (_provoked)
        {
            // Woken by damage/noise but with nobody to chase yet: walk to the noise, else keep waiting.
            PlayerHealth nearest = FindNearestLivingPlayer(out _);
            if (nearest != null && (relentless || CanSense(nearest, out _)))
            {
                BeginChase(nearest);
                return;
            }
            if (_hasLurePoint)
                EnterState(BomberState.Chase);
            return;
        }

        PlayerHealth spotted = FindProvokingPlayer();
        if (spotted != null)
        {
            _provoked = true;
            BeginChase(spotted);
        }
    }

    void TickChase()
    {
        SetChasingAnimation(true);

        if (_target == null || _target.IsDead)
            _target = relentless ? FindNearestLivingPlayer(out _) : null;

        if (_target == null)
        {
            // Nobody left to blow up. Run down the last noise if there is one, otherwise stand and wait.
            if (_hasLurePoint)
            {
                if (Time.time >= _nextRepathTime)
                {
                    _nextRepathTime = Time.time + repathInterval;
                    SetDestination(_lurePoint);
                }
                if (HorizontalDistance(transform.position, _lurePoint) <= Mathf.Max(1f, detonateDistance))
                {
                    _hasLurePoint = false;
                    EnterState(BomberState.Idle);
                    return;
                }
                MoveAlongPath();
                return;
            }

            EnterState(BomberState.Idle);
            return;
        }

        if (Time.time >= _nextRepathTime)
        {
            _nextRepathTime = Time.time + repathInterval;
            SetDestination(_target.transform.position);
        }

        if (SurfaceGapTo(_target) <= detonateDistance)
        {
            LightFuse();
            return;
        }

        MoveAlongPath();
    }

    void TickPriming()
    {
        bool stillHunting = chaseWhileFuseBurns && _target != null && !_target.IsDead;
        if (stillHunting)
        {
            // Committed final charge: the fuse is lit and he keeps coming. The sparklers are a real warning
            // precisely because they cost him nothing -- planting his feet here would let the player stroll
            // out of a blast he lit several metres early.
            SetChasingAnimation(true);
            if (Time.time >= _nextRepathTime)
            {
                _nextRepathTime = Time.time + repathInterval;
                SetDestination(_target.transform.position);
            }
            MoveAlongPath();
        }
        else
        {
            SetChasingAnimation(false);
            ApplyMotion(Vector3.zero);

            // Nothing left to chase (or the charge is disabled): hold still and face the victim so the
            // blast still reads as deliberate rather than incidental.
            if (_target != null)
            {
                Vector3 toTarget = _target.transform.position - transform.position;
                toTarget.y = 0f;
                RotateToward(toTarget);
            }
        }

        if (Time.time >= _detonateAtTime)
            Detonate();
    }

    // ------------------------------------------------------------------ state transitions (server)

    void BeginChase(PlayerHealth target)
    {
        _target = target;
        _nextRepathTime = 0f;
        EnterState(BomberState.Chase);
    }

    void EnterState(BomberState next)
    {
        if (_state == next)
            return;
        _state = next;

        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
            navMeshAgent.isStopped = next != BomberState.Chase;
    }

    /// <summary>Server: the point of no return. Nothing after this can stop the blast.</summary>
    void LightFuse()
    {
        if (_state == BomberState.Priming || _state == BomberState.Spent)
            return;

        EnterState(BomberState.Priming);
        _detonateAtTime = Time.time + fuseSeconds;

        if (IsSpawned && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            _fuseLit.Value = true;      // replicates the sizzle + swell to every peer
        else
            ApplyFuseVisual(true);      // offline

        if (fuseSeconds <= 0f)
            Detonate();
    }

    // ------------------------------------------------------------------ detonation (server)

    void Detonate()
    {
        if (_state == BomberState.Spent)
            return;
        _state = BomberState.Spent;

        Vector3 point = transform.position + Vector3.up * blastHeight;

        ApplyBlastToPlayers(point);

        if (IsSpawned)
        {
            DetonateFxClientRpc(point);
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                NetworkObject.Despawn(true);
            return;
        }

        // Offline: no RPC to route the FX through, so play it here and tear the body down directly.
        BomberExplosionFx.Play(explosionFxPrefab, point, explosionClip, explosionVolume);
        Destroy(gameObject);
    }

    void ApplyBlastToPlayers(Vector3 point)
    {
        s_blastVictims.Clear();
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || player.IsDead)
                continue;
            s_blastVictims.Add(player);
        }

        for (int i = 0; i < s_blastVictims.Count; i++)
        {
            PlayerHealth victim = s_blastVictims[i];
            Vector3 chest = victim.transform.position + Vector3.up * blastHeight;
            if (!TryGetBlastStrength(point, chest, victim, out float strength))
                continue;

            float damage = blastDamage * strength;
            if (damage <= 0.5f)
                continue;

            // Push straight out from the blast, plus a fixed lift so nobody is simply shoved along the floor.
            Vector3 outward = chest - point;
            outward.y = 0f;
            outward = outward.sqrMagnitude > 0.0001f ? outward.normalized : -transform.forward;
            Vector3 force = outward * (blastLaunchSpeed * strength) + Vector3.up * (blastLaunchUpSpeed * strength);

            var netRagdoll = victim.GetComponent<NetworkPlayerRagdoll>();
            bool inNetSession = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
            if (inNetSession && netRagdoll != null)
            {
                netRagdoll.RequestTrapHitFromServer(force, chest, damage, blastForceMode);
                continue;
            }

            // No networked ragdoll (offline, or a non-networked player rig): damage + ragdoll locally rather
            // than silently whiffing the blast. Mirrors ClownAI's fallback.
            var ragdoll = victim.GetComponent<PlayerRagdollController>();
            if (ragdoll != null)
            {
                victim.TakeDamage(damage);
                ragdoll.ActivateRagdoll(force, chest, blastForceMode, allowAutoRecovery: !victim.IsDead);
            }
            else
            {
                victim.TakeDamage(damage);
            }
        }

        s_blastVictims.Clear();
    }

    /// <summary>
    /// Distance falloff plus the wall check. <paramref name="strength"/> is 1 out to
    /// <see cref="fullDamageRadiusFraction"/> of the radius, then eases to 0 at the edge.
    /// </summary>
    bool TryGetBlastStrength(Vector3 blast, Vector3 victimChest, PlayerHealth victim, out float strength)
    {
        strength = 0f;

        float distance = Vector3.Distance(blast, victimChest);
        if (distance > blastRadius)
            return false;

        if (blastRequiresLineOfSight && !HasLineOfSight(blast, victim))
            return false;

        float fullRadius = blastRadius * fullDamageRadiusFraction;
        strength = distance <= fullRadius
            ? 1f
            : 1f - Mathf.InverseLerp(fullRadius, blastRadius, distance);
        strength = Mathf.Clamp01(strength);
        return strength > 0f;
    }

    // ------------------------------------------------------------------ sensing (server)

    PlayerHealth FindProvokingPlayer()
    {
        PlayerHealth best = null;
        float bestDistance = float.PositiveInfinity;

        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || player.IsDead)
                continue;

            if (!CanSense(player, out float distance) || distance >= bestDistance)
                continue;

            best = player;
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>Close enough to hear, or ahead of him in plain sight.</summary>
    bool CanSense(PlayerHealth player, out float distance)
    {
        distance = Vector3.Distance(transform.position, player.transform.position);

        if (distance <= noticeRadius)
            return true;
        if (distance > sightRadius)
            return false;

        Vector3 toPlayer = player.transform.position - transform.position;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude > 0.0001f
            && Vector3.Angle(transform.forward, toPlayer.normalized) > sightAngle * 0.5f)
            return false;

        return !requireLineOfSight || HasLineOfSight(transform.position + Vector3.up * lineOfSightHeight, player);
    }

    PlayerHealth FindNearestLivingPlayer(out float nearestDistance)
    {
        PlayerHealth best = null;
        nearestDistance = float.PositiveInfinity;

        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || player.IsDead)
                continue;

            float d = Vector3.Distance(transform.position, player.transform.position);
            if (d >= nearestDistance)
                continue;

            best = player;
            nearestDistance = d;
        }

        return best;
    }

    bool HasLineOfSight(Vector3 origin, PlayerHealth target)
    {
        if (target == null)
            return false;

        Vector3 targetPoint = target.transform.position + Vector3.up * lineOfSightHeight;
        Vector3 toTarget = targetPoint - origin;
        float distance = toTarget.magnitude;
        if (distance <= 0.001f)
            return true;

        int mask = lineOfSightMask.value == 0 ? Physics.DefaultRaycastLayers : lineOfSightMask.value;
        int hitCount = Physics.RaycastNonAlloc(
            origin, toTarget / distance, s_lineOfSightHits, distance, mask, QueryTriggerInteraction.Ignore);
        if (hitCount == 0)
            return true;

        System.Array.Sort(s_lineOfSightHits, 0, hitCount, RaycastHitDistanceComparer.Instance);
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit hit = s_lineOfSightHits[i];
            s_lineOfSightHits[i] = default;

            if (hit.transform == null)
                continue;
            if (hit.transform == transform || hit.transform.IsChildOf(transform))
                continue;   // his own body / the dynamite in his fists

            return hit.transform == target.transform || hit.transform.IsChildOf(target.transform);
        }

        return true;
    }

    /// <summary>
    /// Gap between his collision capsule and the target's, rather than pivot-to-pivot. Without this the
    /// detonate distance would have to absorb both bodies' radii and would drift the moment either is rescaled.
    /// </summary>
    float SurfaceGapTo(PlayerHealth target)
    {
        float centreDistance = HorizontalDistance(transform.position, target.transform.position);

        float ownRadius = 0.35f;
        if (characterController != null)
            ownRadius = characterController.radius * Mathf.Max(0.01f, Mathf.Max(transform.lossyScale.x, transform.lossyScale.z));

        float targetRadius = 0.4f;
        CharacterController targetCc = target.GetComponentInParent<CharacterController>();
        if (targetCc == null)
            targetCc = target.GetComponentInChildren<CharacterController>();
        if (targetCc != null)
        {
            float lossy = Mathf.Max(targetCc.transform.lossyScale.x, targetCc.transform.lossyScale.z);
            targetRadius = targetCc.radius * Mathf.Max(0.01f, lossy);
        }

        return Mathf.Max(0f, centreDistance - ownRadius - targetRadius);
    }

    static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    // ------------------------------------------------------------------ movement (server)

    /// <summary>
    /// Snap <paramref name="worldPoint"/> onto the NavMesh and path to it. The search widens in steps because
    /// a player standing on a prop, a stair nose or a table is genuinely off-mesh and a single tight sample
    /// would silently find nothing — leaving the agent with no destination while the run animation played on.
    /// If every radius misses, the previous destination is deliberately left alone rather than cleared.
    /// </summary>
    void SetDestination(Vector3 worldPoint)
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || !navMeshAgent.isOnNavMesh)
            return;

        for (int i = 0; i < s_destinationSnapRadii.Length; i++)
        {
            if (!NavMesh.SamplePosition(worldPoint, out NavMeshHit hit, s_destinationSnapRadii[i], NavMesh.AllAreas))
                continue;
            navMeshAgent.SetDestination(hit.position);
            return;
        }
    }

    void MoveAlongPath()
    {
        if (navMeshAgent == null || !navMeshAgent.enabled || !navMeshAgent.isOnNavMesh)
        {
            ApplyMotion(SteerDirectlyAtTarget());
            return;
        }

        navMeshAgent.isStopped = false;
        navMeshAgent.speed = chaseSpeed;
        navMeshAgent.acceleration = acceleration;

        Vector3 desired = navMeshAgent.desiredVelocity;
        desired.y = 0f;
        if (desired.sqrMagnitude > 0.01f)
        {
            desired = desired.normalized * chaseSpeed;
        }
        else if (navMeshAgent.pathStatus != NavMeshPathStatus.PathComplete)
        {
            // No route exists (the target is on an unreachable island, or has been shoved off the mesh).
            // Charge straight at them and let the CharacterController slide along whatever is in the way —
            // far better than standing still playing the run cycle on the spot.
            desired = SteerDirectlyAtTarget();
        }
        else
        {
            desired = Vector3.zero;   // path complete and nothing left to travel: we have arrived
        }

        if (desired.sqrMagnitude > 0.0001f)
            RotateToward(desired);

        ApplyMotion(desired);
    }

    /// <summary>Path-free beeline at the current target, used only when the NavMesh cannot offer a route.</summary>
    Vector3 SteerDirectlyAtTarget()
    {
        if (_target == null)
            return Vector3.zero;

        Vector3 toTarget = _target.transform.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f)
            return Vector3.zero;

        return toTarget.normalized * chaseSpeed;
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
        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
            navMeshAgent.nextPosition = transform.position;
    }

    void RotateToward(Vector3 horizontalDirection)
    {
        horizontalDirection.y = 0f;
        if (horizontalDirection.sqrMagnitude < 1e-4f)
            return;

        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            Quaternion.LookRotation(horizontalDirection.normalized, Vector3.up),
            turnSpeedDegreesPerSecond * Time.deltaTime);
    }

    // ------------------------------------------------------------------ presentation (every peer)

    void SetChasingAnimation(bool chasing)
    {
        // Server-side writes to a NetworkAnimator-driven bool replicate on their own; only triggers need the
        // explicit ServerNetworkAnimator call, and this rig has none.
        if (animator != null && !string.IsNullOrEmpty(chasingBoolParam))
            animator.SetBool(chasingBoolParam, chasing);
    }

    void HandleFuseLitChanged(bool previousValue, bool currentValue) => ApplyFuseVisual(currentValue);

    void ApplyFuseVisual(bool lit)
    {
        if (!lit)
            return;

        // Sparklers spitting off both fuse tips. They live on the prefab inactive and are switched on here,
        // so every peer lights them off the replicated fuse flag rather than instantiating anything.
        // Nothing ever switches them back off — the fuse cannot go out, and the body despawns on detonation.
        if (fuseSparks != null)
        {
            for (int i = 0; i < fuseSparks.Length; i++)
            {
                if (fuseSparks[i] != null && !fuseSparks[i].activeSelf)
                    fuseSparks[i].SetActive(true);
            }
        }

        if (voiceAudioSource != null && fuseClip != null)
        {
            voiceAudioSource.clip = fuseClip;
            voiceAudioSource.loop = true;
            voiceAudioSource.Play();
        }
    }

    /// <summary>Swells both sticks in time with the sizzle. Runs on every peer off the replicated fuse flag.</summary>
    void UpdateFuseVisual()
    {
        if (!IsFuseLit || dynamiteSticks == null)
            return;

        float pulse = 1f + (fusePulseScale - 1f) * (0.5f + 0.5f * Mathf.Sin(Time.time * fusePulseHz * Mathf.PI * 2f));
        for (int i = 0; i < dynamiteSticks.Length; i++)
        {
            if (dynamiteSticks[i] == null)
                continue;
            // Girth only. The stick's local Z is its length, and swelling that would push it out through
            // the fist it is gripped in -- a fattening stick reads as "about to go off" without the slide.
            Vector3 baseScale = _dynamiteBaseScales[i];
            dynamiteSticks[i].localScale = new Vector3(baseScale.x * pulse, baseScale.y * pulse, baseScale.z);
        }
    }

    [ClientRpc]
    void DetonateFxClientRpc(Vector3 point)
    {
        // The blast itself was adjudicated server-side; this is the fireball and the bang only, so a peer
        // that misses it still took exactly the same damage.
        BomberExplosionFx.Play(explosionFxPrefab, point, explosionClip, explosionVolume);
    }

    // ------------------------------------------------------------------ external hooks

    /// <summary>
    /// Server/offline: wake him up. Anything that should count as provocation routes here -- taking a hit,
    /// a nearby bang, a scripted trigger. Idempotent, and a no-op once the fuse is lit.
    /// </summary>
    public void ServerProvoke()
    {
        if (!ShouldSimulate || _state == BomberState.Priming || _state == BomberState.Spent)
            return;

        _provoked = true;
        _nextScanTime = 0f;
    }

    /// <summary>
    /// Server/offline: <see cref="BomberHealth"/> routes a survived hit here. Being shot wakes him and turns
    /// him on whoever pulled the trigger — but it never interrupts a burning fuse.
    /// </summary>
    public void OnDamageTaken(bool fromPlayerMelee, Transform attacker, PlayerHealth attackerHealth)
    {
        if (!ShouldSimulate || _state == BomberState.Priming || _state == BomberState.Spent)
            return;

        _provoked = true;
        _nextScanTime = 0f;

        if (attackerHealth != null && !attackerHealth.IsDead)
            BeginChase(attackerHealth);
    }

    /// <summary>
    /// Server/offline: <see cref="BomberHealth"/> calls this once the pool hits zero. He has no corpse — the
    /// payload cooks off where he stood (which is why shooting him from across a corridor is the safe play),
    /// and <see cref="Detonate"/> despawns the body itself.
    /// </summary>
    public void HandleDeath(bool detonate)
    {
        if (!ShouldSimulate || _state == BomberState.Spent)
            return;

        if (detonate)
        {
            Detonate();
            return;
        }

        // Non-explosive death: no death clip exists for this rig, so just stop him and take him off the board.
        _state = BomberState.Spent;
        _target = null;
        SetChasingAnimation(false);
        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
            navMeshAgent.isStopped = true;

        if (IsSpawned && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            NetworkObject.Despawn(true);
        else if (!IsSpawned)
            Destroy(gameObject);
    }

    /// <summary>Flashbang caught him: drop the target and grope in circles until it wears off.</summary>
    public void OnFlashbangBlinded(float seconds)
    {
        if (_state == BomberState.Priming || _state == BomberState.Spent)
            return;   // a lit fuse does not care that he cannot see

        _target = null;
        SetChasingAnimation(false);
        if (navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
            navMeshAgent.isStopped = true;
    }

    // ---- ILurableEnemy: a decoy grenade can bait him onto itself instead of onto the squad ----

    /// <summary>Never pull him off a live run-up or a burning fuse.</summary>
    public bool IsPursuingPlayer =>
        _state == BomberState.Priming
        || _state == BomberState.Spent
        || (_state == BomberState.Chase && _target != null);

    public Vector3 LureListenPosition => transform.position;

    public void LureToNoise(Vector3 worldPoint)
    {
        if (!ShouldSimulate || IsPursuingPlayer)
            return;

        _lurePoint = worldPoint;
        _hasLurePoint = true;
        _provoked = true;
        _nextRepathTime = 0f;
        EnterState(BomberState.Chase);
    }

    sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
    {
        public static readonly RaycastHitDistanceComparer Instance = new();

        public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
    }
}
