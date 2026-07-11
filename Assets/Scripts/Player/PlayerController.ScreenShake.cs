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
