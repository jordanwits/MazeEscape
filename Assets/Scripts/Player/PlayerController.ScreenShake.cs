using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Subtle proximity screen shake driven by the nearest Jailor. Whenever a Jailor is close enough that the
/// player could hear his footsteps (the start distance matches the footstep AudioSource max distance), the
/// local view camera trembles; the tremble grows the closer he gets. Local-control / owner only.
///
/// Every shake source here only accumulates a rotational offset; <see cref="ApplyComposedViewShake"/> resolves
/// the look system's neutral camera pose itself and stamps <c>neutral * offset</c> in a single write per frame.
/// The layer therefore never reads the camera back and never depends on <c>Update</c> having written the look
/// pose this frame — it doesn't on frames where an overlay, the pause menu or a get-up skips that write.
/// Nothing else writes the local player's view camera rotation (<see cref="FirstPersonViewHeadSync"/> moves the
/// pitch node's position, <see cref="MovementViewBob"/> moves the hips bone).
/// </summary>
public partial class PlayerController
{
    [Header("Jailor proximity shake")]
    [Tooltip("Subtle screen shake that ramps up as the Jailor gets close (as long as you could hear his footsteps).")]
    [SerializeField] bool jailorProximityShakeEnabled = true;
    [Tooltip("Distance (m) at/under which the shake begins. Match the Jailor footstep AudioSource max distance (~25) so it kicks in right when you can hear him.")]
    [SerializeField] float jailorShakeStartDistance = 25f;
    [Tooltip("Distance (m) at/under which the shake reaches full strength.")]
    [SerializeField] float jailorShakeFullDistance = 4f;
    [Tooltip("Peak view rotation offset (degrees) at full strength, applied on each of pitch/yaw/roll. Keep small for a subtle tremble.")]
    [SerializeField] float jailorShakeMaxAngleDegrees = 1.2f;
    [Tooltip("Tremble speed. Higher = faster, jitterier shake.")]
    [SerializeField] float jailorShakeFrequency = 11f;
    [Tooltip("Exponent on the 0-1 proximity ramp. >1 keeps the shake very faint until the Jailor is fairly close.")]
    [SerializeField] float jailorShakeFalloffExponent = 2f;
    [Tooltip("How quickly the shake eases toward its target strength (seconds). Prevents pops when the nearest Jailor changes or spawns/despawns.")]
    [SerializeField] float jailorShakeIntensitySmoothTime = 0.25f;

    float _jailorShakeIntensity;
    float _jailorShakeIntensityVelocity;
    float _jailorShakeNoiseTime;
    // Fixed, decorrelated Perlin sample lanes so pitch/yaw/roll don't move in lockstep.
    static readonly Vector3 s_JailorShakeNoiseLanes = new Vector3(11.3f, 47.9f, 83.1f);

    [Header("Scream impulse shake")]
    [Tooltip("Peak view rotation offset (degrees) of a full-strength scream jolt, on each of pitch/yaw/roll. Bigger than the Jailor tremble — this is a jump-scare kick, not an ambient rumble.")]
    [SerializeField] float screamShakeMaxAngleDegrees = 5f;
    [Tooltip("Tremble speed of the scream jolt. Higher = faster, more violent rattle.")]
    [SerializeField] float screamShakeFrequency = 26f;
    [Tooltip("How fast the scream jolt (0-1 trauma) decays back to zero per second once the scream ends. Higher = snappier tail. ~4 settles a full jolt in about a quarter second.")]
    [SerializeField] float screamShakeDecayPerSecond = 4f;
    [Tooltip("Exponent on the 0-1 trauma → angle curve. >1 makes the jolt punch hard then fall off fast (a kick rather than a lingering wobble).")]
    [SerializeField] float screamShakeTraumaExponent = 2f;

    float _screamTrauma;
    float _screamShakeNoiseTime;
    static readonly Vector3 s_ScreamShakeNoiseLanes = new Vector3(19.7f, 61.4f, 97.2f);

    [Header("Melee impact camera kick")]
    [Tooltip("Directional recoil jolt on the local player's view the instant a punch connects. Sells the impact now that most hits only flinch the enemy rather than stunning it. Owner-only, purely cosmetic.")]
    [SerializeField] bool meleeCameraKickEnabled = true;
    [Tooltip("How far the view snaps UP on impact (degrees). This is the main punch recoil.")]
    [SerializeField] float meleeKickUpDegrees = 2.1f;
    [Tooltip("Peak sideways (yaw) jolt on impact (degrees). Randomized left/right each hit so punches don't feel identical.")]
    [SerializeField] float meleeKickYawDegrees = 0.7f;
    [Tooltip("Peak tilt (roll) jolt on impact (degrees). Randomized each hit.")]
    [SerializeField] float meleeKickRollDegrees = 1.1f;
    [Tooltip("Seconds for the kick to spring back to center. Small = snappy recoil.")]
    [SerializeField] float meleeKickRecoverTime = 0.11f;
    [Tooltip("Kick strength multiplier when the punch lands on a Skeleton — the tankier, heavier enemy hits back harder against the camera.")]
    [SerializeField] float meleeKickSkeletonScale = 1.35f;

    [Header("Hurt camera kick")]
    [Tooltip("Directional recoil when THIS player takes damage — bigger and rougher than the punch-landed kick. Fired by the hurt-feedback tick; shares the melee kick spring so the two compose.")]
    [SerializeField] bool hurtCameraKickEnabled = true;
    [SerializeField] float hurtKickPitchDegrees = 3.4f;
    [SerializeField] float hurtKickYawDegrees = 1.6f;
    [SerializeField] float hurtKickRollDegrees = 2.2f;

    [Header("Chase panic chromatic aberration")]
    [Tooltip("Feeds the hunter-proximity ramp (nearest Jailor OR Clown) into MazePostFx's chromatic aberration so the view smears as the hunter closes in. This is the aberration intensity at point-blank; 0 disables.")]
    [SerializeField, Range(0f, 1f)] float chasePanicChromaticAberrationMax = 0.55f;

    // Current applied kick offset (pitch/yaw/roll degrees) and its spring velocity. Decays to zero every frame.
    Vector3 _meleeKickOffsetDeg;
    Vector3 _meleeKickVelocityDeg;

    // This frame's composed shake offset in the camera's own frame, built up by the sources in application
    // order and consumed by ApplyComposedViewShake. The flag distinguishes "no source fired" from "the sources
    // happened to sum to identity", which matters only on the fallback path that has no neutral base to stamp.
    Quaternion _viewShakeOffset = Quaternion.identity;
    bool _viewShakeOffsetActive;

    /// <summary>
    /// Called from <see cref="LateUpdate"/> (before its early-returns) so the shake is computed in every camera
    /// mode, and before <see cref="ApplyComposedViewShake"/> stamps it.
    /// </summary>
    void UpdateJailorProximityShake()
    {
        if (!jailorProximityShakeEnabled || !_hasLocalControl || !ShouldJailorShakeCameraBeActive())
        {
            _jailorShakeIntensity = 0f;
            _jailorShakeIntensityVelocity = 0f;
            PushChasePanicAberration(0f);
            return;
        }

        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return;

        float target = ComputeJailorShakeTarget(cam.position);
        _jailorShakeIntensity = Mathf.SmoothDamp(
            _jailorShakeIntensity, target, ref _jailorShakeIntensityVelocity,
            Mathf.Max(0.0001f, jailorShakeIntensitySmoothTime));

        // Same eased 0-1 ramp drives the screen-smear panic effect — one number, two senses.
        PushChasePanicAberration(_jailorShakeIntensity);

        if (_jailorShakeIntensity <= 0.0005f)
            return;

        AccumulateJailorShakeOffset(_jailorShakeIntensity);
    }

    /// <summary>
    /// Writes the hunter-proximity ramp into the level's chromatic aberration. Gated to the LOCAL PLAYER'S
    /// controller — not to whether it currently has control — so remote-player instances on this machine can
    /// never stomp the value, while the zero-clear above still lands when control is temporarily suspended.
    /// Gating on control instead made the clear a no-op on the two paths that always fire at point-blank range
    /// (death and a Jailor grab), freezing the smear at near-maximum through the ragdoll, the whole respawn wait
    /// and the entire carry — nothing else writes or decays this value.
    /// </summary>
    void PushChasePanicAberration(float ramp01)
    {
        if (!_isLocalAvatar || chasePanicChromaticAberrationMax <= 0f)
            return;

        MazePostFx fx = MazePostFx.Active;
        if (fx != null)
            fx.SetChromaticAberration(ramp01 * chasePanicChromaticAberrationMax);
    }

    /// <summary>
    /// Skip the shake while the ragdoll/death camera path owns the view — that pose is delicate and chaotic
    /// enough on its own, and a layered tremble would fight it.
    /// </summary>
    bool ShouldJailorShakeCameraBeActive()
    {
        if (_playerHealth != null && _playerHealth.IsDead)
            return false;
        if (_ragdollController != null
            && (_ragdollController.IsRagdolled || _ragdollController.IsGettingUp || _ragdollController.IsHeld))
            return false;
        return true;
    }

    float ComputeJailorShakeTarget(Vector3 listenerPosition)
    {
        // "Jailor" proximity historically, but the Clown is the same class of hunter on Level02 (which has
        // no Jailor at all) — the nearest of either drives the ramp. Field names keep the jailor* prefix so
        // existing prefab tuning survives.
        float nearestSqr = float.MaxValue;

        IReadOnlyList<JailorAI> jailors = JailorAIRegistry.All;
        for (int i = 0; i < jailors.Count; i++)
        {
            JailorAI jailor = jailors[i];
            if (jailor == null)
                continue;
            float sqr = (jailor.transform.position - listenerPosition).sqrMagnitude;
            if (sqr < nearestSqr)
                nearestSqr = sqr;
        }

        IReadOnlyList<ClownAI> clowns = ClownAIRegistry.All;
        for (int i = 0; i < clowns.Count; i++)
        {
            ClownAI clown = clowns[i];
            if (clown == null)
                continue;
            float sqr = (clown.transform.position - listenerPosition).sqrMagnitude;
            if (sqr < nearestSqr)
                nearestSqr = sqr;
        }

        if (nearestSqr == float.MaxValue)
            return 0f;

        float start = Mathf.Max(jailorShakeFullDistance + 0.01f, jailorShakeStartDistance);
        float full = Mathf.Max(0f, jailorShakeFullDistance);
        float distance = Mathf.Sqrt(nearestSqr);

        // 1 at/under the full-strength distance, 0 at/beyond the start distance.
        float ramp = Mathf.Clamp01(Mathf.InverseLerp(start, full, distance));
        return Mathf.Pow(ramp, Mathf.Max(0.01f, jailorShakeFalloffExponent));
    }

    /// <summary>
    /// Kick a one-shot scream jolt into the view. <paramref name="strength01"/> is 0-1 trauma; repeated calls
    /// take the max (a fresh loud scream re-arms the jolt rather than stacking). Safe to call on any peer — it
    /// only does something on the local player's own camera. Applied and decayed in <see cref="LateUpdate"/>.
    /// </summary>
    public void AddScreamShake(float strength01)
    {
        _screamTrauma = Mathf.Clamp01(Mathf.Max(_screamTrauma, strength01));
    }

    /// <summary>
    /// Shake THIS peer's local player from a world-space scream/impact. Falls off with distance: full
    /// <paramref name="maxStrength"/> at/under <paramref name="innerRadius"/>, nothing at/beyond
    /// <paramref name="outerRadius"/>. The grabbed victim (right at the source) gets the full jolt; bystanders
    /// feel a lesser rattle. No-op if there's no local player. Call on every peer — each shakes only its own view.
    /// </summary>
    public static void AddScreamShakeToLocalPlayer(Vector3 sourcePosition, float innerRadius, float outerRadius, float maxStrength)
    {
        PlayerController local = ResolveLocalPlayer();
        if (local == null)
            return;

        Transform cam = local.CameraTransformForFacing;
        Vector3 listener = cam != null ? cam.position : local.transform.position;
        float distance = Vector3.Distance(listener, sourcePosition);

        // 1 at/under the inner radius, 0 at/beyond the outer radius.
        float ramp = Mathf.Clamp01(Mathf.InverseLerp(Mathf.Max(innerRadius + 0.01f, outerRadius), innerRadius, distance));
        if (ramp <= 0f)
            return;

        local.AddScreamShake(ramp * maxStrength);
    }

    static PlayerController ResolveLocalPlayer()
    {
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;
            PlayerController controller = player.GetComponent<PlayerController>();
            if (controller != null && controller.HasLocalControl)
                return controller;
        }
        return null;
    }

    /// <summary>
    /// Decays and contributes the one-shot scream jolt. Unlike the Jailor shake this STAYS active while the player
    /// is held (the grabbed victim is exactly who we want to rattle) — during a hold the ragdoll camera path drives
    /// the pitch node's parent/position but never <c>cameraTransform.localRotation</c>, so this offset survives.
    /// Called from <see cref="LateUpdate"/> right after the Jailor shake; the two offsets simply compose.
    /// </summary>
    void UpdateScreamImpulseShake()
    {
        if (_screamTrauma <= 0.0005f)
        {
            _screamTrauma = 0f;
            return;
        }

        // Render at the current strength, then decay — so the trauma always eases to zero even on frames where
        // we can't draw it (no local control, dead), and never lingers when control returns.
        float trauma = _screamTrauma;
        _screamTrauma = Mathf.MoveTowards(_screamTrauma, 0f, Mathf.Max(0.01f, screamShakeDecayPerSecond) * Time.deltaTime);

        if (!_hasLocalControl || (_playerHealth != null && _playerHealth.IsDead))
            return;

        _screamShakeNoiseTime += Time.deltaTime * Mathf.Max(0.01f, screamShakeFrequency);
        float pitch = (Mathf.PerlinNoise(s_ScreamShakeNoiseLanes.x, _screamShakeNoiseTime) - 0.5f) * 2f;
        float yaw = (Mathf.PerlinNoise(s_ScreamShakeNoiseLanes.y, _screamShakeNoiseTime) - 0.5f) * 2f;
        float roll = (Mathf.PerlinNoise(s_ScreamShakeNoiseLanes.z, _screamShakeNoiseTime) - 0.5f) * 2f;

        float shaped = Mathf.Pow(Mathf.Clamp01(trauma), Mathf.Max(0.01f, screamShakeTraumaExponent));
        float angle = screamShakeMaxAngleDegrees * shaped;
        Quaternion shake = Quaternion.Euler(pitch * angle, yaw * angle, roll * angle);

        // Right-multiplied onto the offsets contributed before it (same idiom as the Jailor shake), so a scream
        // during a tremble composes instead of replacing it.
        AddViewShakeOffset(shake);
    }

    /// <summary>
    /// Fire a one-shot directional recoil on THIS peer's local view. Safe to call on any peer / from the shared
    /// hit-SFX methods — it self-gates to the local player (a punch by a remote player, heard as 3D audio on this
    /// machine, must not kick this player's camera). Impulses stack if punches land in quick succession, clamped so
    /// the view can't swing wildly. Applied and sprung back to center in <see cref="UpdateMeleeCameraKick"/>.
    /// </summary>
    void TriggerMeleeCameraKick(float strengthScale)
    {
        if (!meleeCameraKickEnabled || !_hasLocalControl)
            return;

        // Up-kick (negative pitch euler = view rotates up), with a randomized sideways/tilt jolt per hit.
        float sign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
        Vector3 impulse = new Vector3(
            -meleeKickUpDegrees * strengthScale,
            sign * meleeKickYawDegrees * strengthScale,
            -sign * meleeKickRollDegrees * strengthScale);

        _meleeKickOffsetDeg += impulse;
        ClampSharedKickOffset();
    }

    /// <summary>
    /// Rough jolt when THIS player takes a hit — any source (zombie swipe, skeleton bash, clown hammer,
    /// traps). Same spring as the melee kick so simultaneous events compose instead of fighting.
    /// </summary>
    public void TriggerHurtCameraKick()
    {
        if (!hurtCameraKickEnabled || !_hasLocalControl)
            return;

        float sign = UnityEngine.Random.value < 0.5f ? -1f : 1f;
        _meleeKickOffsetDeg += new Vector3(
            -hurtKickPitchDegrees,
            sign * hurtKickYawDegrees,
            -sign * hurtKickRollDegrees);
        ClampSharedKickOffset();
    }

    /// <summary>Clamp the shared kick offset to twice the largest single impulse so rapid hits can't stack into a spin.</summary>
    void ClampSharedKickOffset()
    {
        float maxPitch = Mathf.Max(meleeKickUpDegrees * meleeKickSkeletonScale, hurtKickPitchDegrees) * 2f;
        float maxYaw = Mathf.Max(meleeKickYawDegrees * meleeKickSkeletonScale, hurtKickYawDegrees) * 2f;
        float maxRoll = Mathf.Max(meleeKickRollDegrees * meleeKickSkeletonScale, hurtKickRollDegrees) * 2f;
        _meleeKickOffsetDeg.x = Mathf.Clamp(_meleeKickOffsetDeg.x, -maxPitch, maxPitch);
        _meleeKickOffsetDeg.y = Mathf.Clamp(_meleeKickOffsetDeg.y, -maxYaw, maxYaw);
        _meleeKickOffsetDeg.z = Mathf.Clamp(_meleeKickOffsetDeg.z, -maxRoll, maxRoll);
    }

    /// <summary>
    /// Springs the melee kick offset back to center and contributes it to the view. Called from <see cref="LateUpdate"/>
    /// alongside the other shakes; the offset always decays (even on frames it can't draw) so it never lingers when
    /// control returns. Composes with the Jailor/scream shakes via the same right-multiply idiom.
    /// </summary>
    void UpdateMeleeCameraKick()
    {
        if (_meleeKickOffsetDeg.sqrMagnitude <= 0.0000001f)
        {
            _meleeKickOffsetDeg = Vector3.zero;
            _meleeKickVelocityDeg = Vector3.zero;
            return;
        }

        Vector3 offset = _meleeKickOffsetDeg;
        _meleeKickOffsetDeg = Vector3.SmoothDamp(
            _meleeKickOffsetDeg, Vector3.zero, ref _meleeKickVelocityDeg,
            Mathf.Max(0.0001f, meleeKickRecoverTime));

        if (!_hasLocalControl || (_playerHealth != null && _playerHealth.IsDead))
            return;

        AddViewShakeOffset(Quaternion.Euler(offset));
    }

    void AccumulateJailorShakeOffset(float intensity)
    {
        _jailorShakeNoiseTime += Time.deltaTime * Mathf.Max(0.01f, jailorShakeFrequency);

        // Perlin noise gives a smooth, organic tremble instead of per-frame strobing. Recenter to [-1, 1].
        float pitch = (Mathf.PerlinNoise(s_JailorShakeNoiseLanes.x, _jailorShakeNoiseTime) - 0.5f) * 2f;
        float yaw = (Mathf.PerlinNoise(s_JailorShakeNoiseLanes.y, _jailorShakeNoiseTime) - 0.5f) * 2f;
        float roll = (Mathf.PerlinNoise(s_JailorShakeNoiseLanes.z, _jailorShakeNoiseTime) - 0.5f) * 2f;

        float angle = jailorShakeMaxAngleDegrees * intensity;
        Quaternion shake = Quaternion.Euler(pitch * angle, yaw * angle, roll * angle);

        // Right-multiplied so the tremble stays in the camera's own frame (view-space pitch/yaw/roll) once it is
        // stamped onto the neutral look pose.
        AddViewShakeOffset(shake);
    }

    /// <summary>Adds one source's offset to this frame's composed offset, in the camera's own frame.</summary>
    void AddViewShakeOffset(Quaternion offset)
    {
        _viewShakeOffset = _viewShakeOffset * offset;
        _viewShakeOffsetActive = true;
    }

    /// <summary>
    /// The neutral (shake-free) view-camera localRotation the look system writes when it owns the pose through
    /// the local-rotation path: <see cref="ApplyFirstPersonLook"/>'s child-camera branch and
    /// <see cref="ApplyRagdollFirstPersonLook"/>'s head-parented branch, which both write pitch only (an enemy
    /// hold zeroes the look angles in that branch, so the same formula yields the identity it writes there).
    /// False on the world-space fallback branches, where the camera's local rotation carries no look pose to
    /// rebuild from.
    /// </summary>
    bool TryResolveNeutralViewLocalRotation(Transform cam, out Quaternion neutral)
    {
        neutral = Quaternion.identity;
        if (cam == null || !firstPersonLook)
            return false;

        // Ragdolled / held / getting up, the ragdoll look path owns the view, and it only drives localRotation
        // while the pitch node rides the head; otherwise it writes world rotation.
        if (_ragdollController != null
            && (_ragdollController.IsRagdolled || _ragdollController.IsHeld || _ragdollController.IsGettingUp))
        {
            if (cameraPitchTransform == null || !_cameraPitchParentedToHead)
                return false;
        }
        else if (!cam.IsChildOf(transform))
            return false;

        neutral = Quaternion.Euler(_lookPitchDegrees, 0f, 0f);
        return true;
    }

    /// <summary>
    /// The shake layer's one camera write per frame: neutral look pose stamped with the composed offset. Called
    /// from <see cref="LateUpdate"/> after every source has contributed. The accumulator is consumed before any
    /// gate so a frame that can't draw (remote player, dead) never carries its offset into the next one.
    /// </summary>
    void ApplyComposedViewShake()
    {
        Quaternion offset = _viewShakeOffset;
        bool hasOffset = _viewShakeOffsetActive;
        _viewShakeOffset = Quaternion.identity;
        _viewShakeOffsetActive = false;

        // Remote players' view cameras are driven by NetworkPlayerAvatar (replicated flashlight aim); only the
        // locally-controlled one is ours to write.
        if (!_hasLocalControl || (_playerHealth != null && _playerHealth.IsDead))
            return;

        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return;

        if (TryResolveNeutralViewLocalRotation(cam, out Quaternion neutral))
            cam.localRotation = neutral * offset;
        else if (hasOffset)
            // World-space look branch: there is no local base to rebuild, so the offset can only ride whatever
            // pose currently owns the camera.
            cam.localRotation = cam.localRotation * offset;
    }
}
