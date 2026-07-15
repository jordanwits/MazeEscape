using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Wind-up cymbal monkey enemy. Server-authoritative, mirrors the Skeleton/Zombie pattern:
/// the server runs the logic, a <see cref="ServerNetworkAnimator"/> replicates the animator, and a
/// NetworkTransform replicates movement. Clients keep this component enabled only so the clap
/// AnimationEvent can play the local SFX.
///
/// Behaviour: stands still (idle pose) until a living player comes within <see cref="activationRadius"/>,
/// then latches on — plays the looping WalkClap animation and walks in a straight line along its spawn
/// facing. Each cymbal clap (driven by AnimationEvents on the clip) plays the MonkeyCymbal SFX for
/// everyone nearby and commands every Clown to run to the monkey's position, regardless of distance.
/// Its only purpose is to attract the Clown.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class WindupMonkeyAI : NetworkBehaviour
{
    [Header("Refs (auto-resolved if left empty)")]
    [SerializeField] Animator animator;
    [SerializeField] CharacterController characterController;
    [SerializeField] AudioSource clapAudioSource;
    [Tooltip("MonkeyCymbal clip played on each clap.")]
    [SerializeField] AudioClip clapClip;

    [Header("Activation")]
    [Tooltip("Stays still until a living player is within this distance (metres), then activates for good.")]
    [SerializeField] float activationRadius = 8f;

    [Header("Movement")]
    [Tooltip("Forward walk speed once activated. Walks straight along its spawn facing (no turning, no pathing).")]
    [SerializeField] float walkSpeed = 1.2f;
    [Tooltip("Downward acceleration applied through the CharacterController so it stays grounded.")]
    [SerializeField] float gravity = 20f;

    [Header("Lurch (wind-up stop-and-go)")]
    [Tooltip("Move in short bursts with brief pauses (like a wind-up toy) instead of gliding smoothly.")]
    [SerializeField] bool lurchWalk = true;
    [Tooltip("Forward-speed multiplier (0..1) sampled across the WalkClap loop phase. Flat 0 sections = brief pauses. " +
             "Kept much shorter than the zombie's pauses.")]
    [SerializeField] AnimationCurve walkStepCurve = DefaultWalkStepCurve();
    [Tooltip("How fast the lurch multiplier ramps between move and pause. Higher = snappier, more mechanical.")]
    [SerializeField] float stepSpeedSmoothing = 20f;

    [Header("Animator")]
    [Tooltip("Bool parameter flipped true when the monkey activates (Idle -> WalkClap).")]
    [SerializeField] string activeBoolParam = "Active";

    [Header("Clown lure")]
    [Tooltip("If true, each cymbal clap commands every Clown to run to this monkey, regardless of distance.")]
    [SerializeField] bool lureClownOnClap = true;

    [Header("Knock-over (crouched player punch)")]
    [Tooltip("When a crouched player punches it over the monkey switches from its kinematic CharacterController " +
             "to a real Rigidbody and topples under gravity. This is its rigid mass.")]
    [SerializeField] float knockOverMass = 3f;
    [Tooltip("Impulse (applied near the top, in the punch direction) that shoves it over. Bigger = harder fall.")]
    [SerializeField] float knockOverImpulse = 14f;
    [Tooltip("Linear/angular drag on the toppled body so it rocks to rest quickly instead of sliding/spinning.")]
    [SerializeField] float knockOverLinearDrag = 0.4f;
    [SerializeField] float knockOverAngularDrag = 0.6f;

    ServerNetworkAnimator _serverNetworkAnimator;
    readonly NetworkVariable<bool> _activated = new(false);
    readonly NetworkVariable<bool> _knockedOver = new(false);
    bool _knockedOverApplied;
    bool _physicsToppleStarted;
    float _verticalVelocity;
    float _stepMultiplier;

    /// <summary>True once a crouched punch has tipped the monkey over. Latched for good across all peers.</summary>
    public bool IsKnockedOver => _knockedOverApplied || _knockedOver.Value;

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>(true);
        if (characterController == null) characterController = GetComponent<CharacterController>();
        if (clapAudioSource == null) clapAudioSource = GetComponent<AudioSource>();
        EnsureAnimationSync();
    }

    void EnsureAnimationSync()
    {
        if (animator == null)
            return;
        _serverNetworkAnimator = animator.GetComponent<ServerNetworkAnimator>();
        if (_serverNetworkAnimator == null)
            _serverNetworkAnimator = animator.gameObject.AddComponent<ServerNetworkAnimator>();
    }

    public override void OnNetworkSpawn()
    {
        _activated.OnValueChanged += HandleActivatedChanged;
        _knockedOver.OnValueChanged += HandleKnockedOverChanged;
        ApplyAuthorityState();
        ApplyActivatedVisual(_activated.Value);
        // Applied after the activated visual so a late-joiner seeing an already-toppled monkey wins (freeze + tip).
        if (_knockedOver.Value)
            ApplyKnockedOverVisual(true);
    }

    public override void OnNetworkDespawn()
    {
        _activated.OnValueChanged -= HandleActivatedChanged;
        _knockedOver.OnValueChanged -= HandleKnockedOverChanged;
    }

    bool ShouldSimulate =>
        NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || IsServer;

    void ApplyAuthorityState()
    {
        // Movement only runs on the server; clients follow via NetworkTransform.
        if (characterController != null)
            characterController.enabled = ShouldSimulate;
    }

    void HandleActivatedChanged(bool previousValue, bool currentValue) => ApplyActivatedVisual(currentValue);

    void ApplyActivatedVisual(bool active)
    {
        // On the server this flips the parameter that ServerNetworkAnimator replicates to clients.
        if (animator != null && !string.IsNullOrEmpty(activeBoolParam))
            animator.SetBool(activeBoolParam, active);
    }

    /// <summary>
    /// Server/offline: knock the monkey over and silence it permanently. A crouched player punch routes here
    /// (via <see cref="PlayerController"/>). It freezes the march/clap animation, stops the wind-up key and the
    /// cymbal SFX, and replaces the kinematic CharacterController with a real Rigidbody so the toy topples under
    /// gravity (shoved in <paramref name="hitDirection"/>) and settles on the floor. NetworkTransform replicates
    /// the server's physics to clients. Any Clown lure already issued by earlier claps still stands — the Clown
    /// keeps investigating the last clap position; we only stop the monkey from making more noise.
    /// </summary>
    public void ServerKnockOver(Vector3 hitDirection)
    {
        if (!ShouldSimulate || IsKnockedOver)
            return;

        if (IsSpawned && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            _knockedOver.Value = true;       // replicates + fires HandleKnockedOverChanged on every peer
        else
            ApplyKnockedOverVisual(true);    // offline / not networked

        // Physics runs on the server (or offline host) only; clients just follow the replicated transform.
        StartPhysicsTopple(hitDirection);
    }

    void HandleKnockedOverChanged(bool previousValue, bool currentValue)
    {
        if (currentValue)
            ApplyKnockedOverVisual(true);
    }

    // Freeze + silence the toy on every peer (driven by the replicated _knockedOver bool). The physical fall
    // itself is server-only (see StartPhysicsTopple) and reaches clients through NetworkTransform.
    void ApplyKnockedOverVisual(bool knocked)
    {
        if (!knocked || _knockedOverApplied)
            return;
        _knockedOverApplied = true;

        // Freeze the toy: the same Active bool drives the march, the cymbal claps and the wind-up key spinner.
        if (animator != null && !string.IsNullOrEmpty(activeBoolParam))
            animator.SetBool(activeBoolParam, false);

        // Silence the cymbal one-shot if it is still ringing.
        if (clapAudioSource != null)
            clapAudioSource.Stop();
    }

    // Server/offline: swap the kinematic CharacterController for a Rigidbody and shove the toy over so it
    // tumbles under real gravity and comes to rest on the ground. Idempotent (latched by _physicsToppleStarted).
    void StartPhysicsTopple(Vector3 hitDirection)
    {
        if (_physicsToppleStarted || !ShouldSimulate || characterController == null)
            return;
        _physicsToppleStarted = true;

        // Capture the CharacterController capsule (local dims) before disabling it — the Rigidbody needs a real
        // collider to rest on the floor (a CharacterController can't act as a dynamic Rigidbody collider).
        float ccHeight = characterController.height;
        float ccRadius = characterController.radius;
        Vector3 ccCenter = characterController.center;
        characterController.enabled = false;

        var box = gameObject.GetComponent<BoxCollider>();
        if (box == null)
            box = gameObject.AddComponent<BoxCollider>();
        box.center = ccCenter;
        box.size = new Vector3(ccRadius * 2f, ccHeight, ccRadius * 2f);

        var rb = gameObject.GetComponent<Rigidbody>();
        if (rb == null)
            rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = knockOverMass;
        rb.useGravity = true;
        rb.isKinematic = false;
        rb.linearDamping = knockOverLinearDrag;
        rb.angularDamping = knockOverAngularDrag;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // Shove near the top of the capsule, horizontally in the punch direction, so it both slides a little and
        // rotates over (torque from the off-centre force) — a natural topple rather than a snap.
        Vector3 dir = new Vector3(hitDirection.x, 0f, hitDirection.z);
        dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : transform.forward;
        Vector3 topLocal = ccCenter + Vector3.up * (ccHeight * 0.45f);
        Vector3 topWorld = transform.TransformPoint(topLocal);
        rb.AddForceAtPosition(dir * knockOverImpulse, topWorld, ForceMode.Impulse);
    }

    void Update()
    {
        if (!ShouldSimulate)
            return;

        if (IsKnockedOver)
        {
            // Toppled for good: the Rigidbody (server-only) now owns the body — never walk, clap or re-activate.
            return;
        }

        bool active = _activated.Value;
        if (!active && AnyLivingPlayerWithin(activationRadius))
        {
            if (IsSpawned && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
                _activated.Value = true;       // replicates + fires HandleActivatedChanged
            else
                ApplyActivatedVisual(true);    // offline / not networked
            ApplyActivatedVisual(true);
            active = true;
        }

        // Gravity is applied every frame (even before activation) so he settles onto the ground while idle;
        // he only moves forward once activated.
        ApplyMotion(active);
    }

    void ApplyMotion(bool walking)
    {
        if (characterController == null || !characterController.enabled)
            return;

        if (characterController.isGrounded && _verticalVelocity < 0f)
            _verticalVelocity = -2f;
        _verticalVelocity -= gravity * Time.deltaTime;

        Vector3 motion = Vector3.up * _verticalVelocity;
        if (walking)
        {
            // Wind-up lurch: gate forward speed by the step curve (sampled off the WalkClap phase) so he
            // moves in short bursts — walk, brief pause, walk — instead of gliding smoothly.
            float target = lurchWalk ? SampleStepCurve() : 1f;
            _stepMultiplier = Mathf.MoveTowards(_stepMultiplier, target, stepSpeedSmoothing * Time.deltaTime);
            motion += transform.forward * (walkSpeed * _stepMultiplier);
        }
        else
        {
            _stepMultiplier = 0f;
        }

        characterController.Move(motion * Time.deltaTime);
    }

    // Sample the lurch multiplier from the current animation phase (server runs the WalkClap animator).
    float SampleStepCurve()
    {
        if (animator == null || walkStepCurve == null || walkStepCurve.length == 0)
            return 1f;
        float phase = animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
        if (phase < 0f)
            phase += 1f;
        return Mathf.Clamp01(walkStepCurve.Evaluate(phase));
    }

    // Mostly moving with two brief stops per WalkClap loop — much shorter pauses than the zombie's curve.
    static AnimationCurve DefaultWalkStepCurve()
    {
        return new AnimationCurve(
            new Keyframe(0.00f, 1f),
            new Keyframe(0.20f, 1f),
            new Keyframe(0.25f, 0f),
            new Keyframe(0.30f, 0f),
            new Keyframe(0.35f, 1f),
            new Keyframe(0.70f, 1f),
            new Keyframe(0.75f, 0f),
            new Keyframe(0.80f, 0f),
            new Keyframe(0.85f, 1f),
            new Keyframe(1.00f, 1f));
    }

    bool AnyLivingPlayerWithin(float radius)
    {
        float r2 = radius * radius;
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth p = players[i];
            if (p == null || p.IsDead)
                continue;
            if ((p.transform.position - transform.position).sqrMagnitude <= r2)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Called by <see cref="WindupMonkeyClapRelay"/> from the WalkClap AnimationEvents — fires on every
    /// peer that plays the clip. Plays the clap SFX locally everywhere; on the server it also lures the Clown.
    /// </summary>
    public void HandleCymbalClap()
    {
        // Toppled monkeys are silent — no SFX, no further Clown lures (belt-and-suspenders; the clip is frozen too).
        if (IsKnockedOver)
            return;

        if (clapAudioSource != null && clapClip != null)
            clapAudioSource.PlayOneShot(clapClip);

        if (lureClownOnClap && ShouldSimulate)
        {
            var clowns = ClownAIRegistry.All;
            for (int i = 0; i < clowns.Count; i++)
            {
                ClownAI clown = clowns[i];
                if (clown != null)
                    clown.LureToPosition(transform.position, this);
            }
        }
    }
}
