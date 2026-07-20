using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Subtle proximity screen shake driven by the nearest Jailor. Whenever a Jailor is close enough that the
/// player could hear his footsteps (the start distance matches the footstep AudioSource max distance), the
/// local view camera trembles; the tremble grows the closer he gets. Local-control / owner only.
///
/// Applied as an additive rotational offset on the view camera in <c>LateUpdate</c>, after the first-person
/// look system writes the camera pose in <c>Update</c>. Nothing else writes the camera's rotation in
/// LateUpdate (<see cref="FirstPersonViewHeadSync"/> moves the pitch node's position, <see cref="MovementViewBob"/>
/// moves the hips bone), so the offset is re-derived each frame and never accumulates or fights another writer.
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

    /// <summary>
    /// Called from <see cref="LateUpdate"/> (before its early-returns) so the shake layers on top of the
    /// pose the look system already wrote this frame, in every camera mode.
    /// </summary>
    void UpdateJailorProximityShake()
    {
        if (!jailorProximityShakeEnabled || !_hasLocalControl || !ShouldJailorShakeCameraBeActive())
        {
            _jailorShakeIntensity = 0f;
            _jailorShakeIntensityVelocity = 0f;
            return;
        }

        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return;

        float target = ComputeJailorShakeTarget(cam.position);
        _jailorShakeIntensity = Mathf.SmoothDamp(
            _jailorShakeIntensity, target, ref _jailorShakeIntensityVelocity,
            Mathf.Max(0.0001f, jailorShakeIntensitySmoothTime));

        if (_jailorShakeIntensity <= 0.0005f)
            return;

        ApplyJailorShakeToCamera(cam, _jailorShakeIntensity);
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
        IReadOnlyList<JailorAI> jailors = JailorAIRegistry.All;
        int count = jailors.Count;
        if (count == 0)
            return 0f;

        float nearestSqr = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            JailorAI jailor = jailors[i];
            if (jailor == null)
                continue;
            float sqr = (jailor.transform.position - listenerPosition).sqrMagnitude;
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
    /// Decays and applies the one-shot scream jolt. Unlike the Jailor shake this STAYS active while the player is
    /// held (the grabbed victim is exactly who we want to rattle) — during a hold the ragdoll camera path drives
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

        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return;

        _screamShakeNoiseTime += Time.deltaTime * Mathf.Max(0.01f, screamShakeFrequency);
        float pitch = (Mathf.PerlinNoise(s_ScreamShakeNoiseLanes.x, _screamShakeNoiseTime) - 0.5f) * 2f;
        float yaw = (Mathf.PerlinNoise(s_ScreamShakeNoiseLanes.y, _screamShakeNoiseTime) - 0.5f) * 2f;
        float roll = (Mathf.PerlinNoise(s_ScreamShakeNoiseLanes.z, _screamShakeNoiseTime) - 0.5f) * 2f;

        float shaped = Mathf.Pow(Mathf.Clamp01(trauma), Mathf.Max(0.01f, screamShakeTraumaExponent));
        float angle = screamShakeMaxAngleDegrees * shaped;
        Quaternion shake = Quaternion.Euler(pitch * angle, yaw * angle, roll * angle);

        // Right-multiply: view-space tremble layered on the pose already written this frame (same idiom as the
        // Jailor shake). If both fire at once they compose.
        cam.localRotation = cam.localRotation * shake;
    }

    void ApplyJailorShakeToCamera(Transform cam, float intensity)
    {
        _jailorShakeNoiseTime += Time.deltaTime * Mathf.Max(0.01f, jailorShakeFrequency);

        // Perlin noise gives a smooth, organic tremble instead of per-frame strobing. Recenter to [-1, 1].
        float pitch = (Mathf.PerlinNoise(s_JailorShakeNoiseLanes.x, _jailorShakeNoiseTime) - 0.5f) * 2f;
        float yaw = (Mathf.PerlinNoise(s_JailorShakeNoiseLanes.y, _jailorShakeNoiseTime) - 0.5f) * 2f;
        float roll = (Mathf.PerlinNoise(s_JailorShakeNoiseLanes.z, _jailorShakeNoiseTime) - 0.5f) * 2f;

        float angle = jailorShakeMaxAngleDegrees * intensity;
        Quaternion shake = Quaternion.Euler(pitch * angle, yaw * angle, roll * angle);

        // Right-multiply so the tremble is in the camera's own frame (view-space pitch/yaw/roll), layered on
        // top of whatever the look system set this frame.
        cam.localRotation = cam.localRotation * shake;
    }
}
