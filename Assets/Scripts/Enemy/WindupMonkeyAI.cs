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

    ServerNetworkAnimator _serverNetworkAnimator;
    readonly NetworkVariable<bool> _activated = new(false);
    float _verticalVelocity;
    float _stepMultiplier;

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
        ApplyAuthorityState();
        ApplyActivatedVisual(_activated.Value);
    }

    public override void OnNetworkDespawn()
    {
        _activated.OnValueChanged -= HandleActivatedChanged;
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

    void Update()
    {
        if (!ShouldSimulate)
            return;

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
        if (clapAudioSource != null && clapClip != null)
            clapAudioSource.PlayOneShot(clapClip);

        if (lureClownOnClap && ShouldSimulate)
        {
            var clowns = ClownAIRegistry.All;
            for (int i = 0; i < clowns.Count; i++)
            {
                ClownAI clown = clowns[i];
                if (clown != null)
                    clown.LureToPosition(transform.position);
            }
        }
    }
}
