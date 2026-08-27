using UnityEngine;

/// <summary>
/// Footsteps and the landing thud for a player avatar this peer does NOT own. The owner's
/// <see cref="PlayerController.UpdateFootsteps"/> / <see cref="PlayerController.TryPlayLandFootstep"/> run inside
/// the movement block, which never executes on a puppet — so remote teammates used to move in total silence.
///
/// Deliberately derived locally rather than replicated: a footstep RPC per step, per player, is a lot of traffic
/// for a cosmetic cue. Horizontal speed comes from the (already replicated) transform's own motion and grounded
/// state from the replicated animator, then the owner's cadence and clip alternation are reused verbatim so both
/// sides of the wire sound the same. Everything plays on the 3D body source, so a teammate down the corridor is
/// quiet and wall-occluded like an enemy is.
/// </summary>
public partial class PlayerController
{
    /// <summary>Smoothing time for the transform-derived speed — replicated motion arrives at tick rate and interpolates.</summary>
    const float ObserverSpeedSmoothingSeconds = 0.08f;

    /// <summary>
    /// A horizontal jump larger than this in a single frame is a teleport, not a stride: re-prime instead of
    /// reading it as several hundred m/s and machine-gunning footsteps. Deliberately well under a metre — the
    /// snaps that matter are small (a blackjack seat sit/stand ≈1 m, the Jailor pickup snap, the carry-release
    /// resnap), while a real sprint frame covers ~0.1 m, so this clears genuine motion at any sane frame rate.
    /// </summary>
    const float ObserverTeleportStepThreshold = 0.75f;

    /// <summary>
    /// How fast the puppet must have been falling for a touchdown to thud, measured as the MINIMUM vertical
    /// velocity seen while airborne. The touchdown frame's own value can never reject anything: a grounded
    /// player replicates exactly -2 (the grounded stick) and a grounded-flicker frame reads ≈-2.16, so every
    /// stair seam and slope flicker used to thud. A genuine fall passes this within a couple of frames.
    /// </summary>
    const float ObserverLandingFallSpeed = -4f;

    Vector3 _observerLastFootstepPosition;
    bool _observerFootstepPrimed;
    float _observerSmoothedSpeed;
    float _observerFootstepTimer;
    bool _observerWasGrounded = true;
    float _observerAirMinVerticalVelocity;

    /// <summary>Ticked every LateUpdate on all instances; self-gates to spawned avatars this peer doesn't own.</summary>
    void TickObserverFootsteps()
    {
        if (!IsObserverPuppet)
        {
            _observerFootstepPrimed = false;
            return;
        }

        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        Vector3 position = transform.position;
        if (!_observerFootstepPrimed)
        {
            PrimeObserverFootsteps(position);
            return;
        }

        Vector3 delta = position - _observerLastFootstepPosition;
        _observerLastFootstepPosition = position;
        delta.y = 0f;

        if (delta.sqrMagnitude > ObserverTeleportStepThreshold * ObserverTeleportStepThreshold)
        {
            PrimeObserverFootsteps(position);
            return;
        }

        _observerSmoothedSpeed = Mathf.Lerp(
            _observerSmoothedSpeed,
            delta.magnitude / dt,
            1f - Mathf.Exp(-dt / ObserverSpeedSmoothingSeconds));

        bool grounded = ReadObserverGrounded();
        bool wasGrounded = _observerWasGrounded;
        _observerWasGrounded = grounded;

        // Read the airborne minimum before it is reset, so the landing test below sees how hard this fall was.
        float fallSpeed = _observerAirMinVerticalVelocity;
        _observerAirMinVerticalVelocity = grounded
            ? 0f
            : Mathf.Min(_observerAirMinVerticalVelocity, ReadObserverVerticalVelocity());

        if (ShouldSuppressObserverFootsteps())
        {
            // Zero the speed as well as the cadence: the suppressed states still move the body (a Jailor
            // carrying a sprinting player, a ragdoll sliding), and a speed left integrating through them
            // comes out the far side hot enough to fire a phantom step the instant suppression lifts.
            _observerSmoothedSpeed = 0f;
            _observerFootstepTimer = 0f;
            return;
        }

        // Touchdown after a real fall — the puppet's mirror of TryPlayLandFootstep.
        if (grounded && !wasGrounded && fallSpeed <= ObserverLandingFallSpeed)
        {
            PlayObserverFootstepOneShot();
            _observerFootstepTimer = Mathf.Max(0.05f, walkFootstepInterval);
            return;
        }

        if (!grounded || _observerSmoothedSpeed < minimumFootstepSpeed)
        {
            _observerFootstepTimer = 0f;
            return;
        }

        // No replicated sprint flag, so the cadence is picked off the speed itself: anything past the midpoint
        // of walk and run is a sprint.
        bool sprinting = _observerSmoothedSpeed > (walkSpeed + runSpeed) * 0.5f;
        _observerFootstepTimer -= dt;
        if (_observerFootstepTimer > 0f)
            return;

        PlayObserverFootstepOneShot();
        _observerFootstepTimer = Mathf.Max(0.05f, sprinting ? runFootstepInterval : walkFootstepInterval);
    }

    void PrimeObserverFootsteps(Vector3 position)
    {
        _observerLastFootstepPosition = position;
        _observerFootstepPrimed = true;
        _observerSmoothedSpeed = 0f;
        _observerFootstepTimer = 0f;
        _observerWasGrounded = ReadObserverGrounded();
        _observerAirMinVerticalVelocity = 0f;
    }

    /// <summary>Every state where the owner's own footstep path would not be running.</summary>
    bool ShouldSuppressObserverFootsteps()
    {
        if (_playerHealth != null && _playerHealth.IsDead)
            return true;
        if (_ragdollController != null
            && (_ragdollController.IsRagdolled || _ragdollController.IsGettingUp || _ragdollController.IsHeld))
            return true;
        if (_networkPlayerAvatar != null && _networkPlayerAvatar.IsCarriedByJailor)
            return true;
        return _blackjackSeated;
    }

    bool ReadObserverGrounded()
    {
        return animator == null || animator.GetBool(groundedParameter);
    }

    float ReadObserverVerticalVelocity()
    {
        return animator != null ? animator.GetFloat(verticalVelocityParameter) : 0f;
    }

    /// <summary>Same clip alternation as the owner's <see cref="PlayerController.PlayFootstepOneShot"/>, on the 3D body source.</summary>
    void PlayObserverFootstepOneShot()
    {
        AudioClip clipToPlay = _playFootstep1Next ? footstepClip1 : footstepClip2;
        if (clipToPlay == null)
            clipToPlay = footstepClip1 != null ? footstepClip1 : footstepClip2;

        if (clipToPlay == null)
            return;

        PlayBodyOneShot(clipToPlay, footstepVolume);
        _playFootstep1Next = !_playFootstep1Next;
    }
}
