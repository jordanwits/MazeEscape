using UnityEngine;

/// <summary>
/// Dev tool: poses the right arm + wrist live so you can visually set the one-hand hold. Drag the Hand and
/// Elbow handles in the Scene view (via the custom editor) and use the wrist sliders; the arm follows and
/// the flashlight (locked forward) sits in the fist. Then "Bake" writes the pose into Hold_OneHand_Pose.anim.
/// Runs after the animator + hold clip (so it overrides them) and before HeldItemHandSocketFollow (700).
/// Leave livePreview OFF for normal play — it only poses while on.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(600)]
public class HandPoseTuner : MonoBehaviour
{
    [Tooltip("Pose the right arm live from the values below. Turn OFF to return to the baked animation.")]
    public bool livePreview = true;

    [Header("Arm — drag the Scene-view handles")]
    [Tooltip("Where the right hand goes, in player-local space.")]
    public Vector3 handTargetLocal = new Vector3(0.16f, 1.22f, 0.34f);
    [Tooltip("Elbow pole hint, in player-local space (points the elbow).")]
    public Vector3 elbowHintLocal = new Vector3(0.5f, 0.95f, 0.02f);

    [Header("Wrist (degrees, relative to the forearm)")]
    public float wristPitch;
    public float wristYaw;
    public float wristRoll;

    Animator _animator;
    Transform _upper, _lower, _hand;
    Quaternion _handBaseLocal;
    bool _captured;

    public Animator TunerAnimator => _animator;

    /// <summary>Snap the fields to the current animated arm (so preview starts from the live hold pose).</summary>
    public void InitFromCurrentPose()
    {
        Resolve();
        if (_hand == null) return;
        handTargetLocal = transform.InverseTransformPoint(_hand.position);
        if (_lower != null)
            elbowHintLocal = transform.InverseTransformPoint(_lower.position + transform.right * 0.35f - transform.up * 0.1f);
        wristPitch = wristYaw = wristRoll = 0f;
        _captured = false;
    }

    void OnEnable() => Resolve();

    void Resolve()
    {
        if (_animator == null) _animator = GetComponent<Animator>();
        if (_animator != null && _animator.isHuman)
        {
            _upper = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _lower = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _hand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
        }
    }

    void LateUpdate()
    {
        if (!livePreview) { _captured = false; return; }
        ApplyNow();
    }

    /// <summary>Poses the arm from the current field values right now (also used by the bake button).</summary>
    public void ApplyNow()
    {
        Resolve();
        if (_upper == null || _lower == null || _hand == null)
            return;
        if (!_captured) { _handBaseLocal = _hand.localRotation; _captured = true; }

        Vector3 target = transform.TransformPoint(handTargetLocal);
        Vector3 pole = transform.TransformPoint(elbowHintLocal);
        SolveTwoBone(_upper, _lower, _hand, target, pole);
        _hand.localRotation = _handBaseLocal * Quaternion.Euler(wristPitch, wristYaw, wristRoll);
    }

    /// <summary>Analytic 2-bone IK: rotate upper + lower so the hand reaches target, elbow toward pole.</summary>
    public static void SolveTwoBone(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole)
    {
        for (int iter = 0; iter < 6; iter++)
        {
            Vector3 S = upper.position, E = lower.position, W = hand.position;
            float a = (E - S).magnitude, b = (W - E).magnitude;
            float t = Mathf.Clamp((target - S).magnitude, 0.05f, a + b - 0.002f);
            float cosInt = Mathf.Clamp(((a * a) + (b * b) - (t * t)) / (2f * a * b), -1f, 1f);
            float desired = Mathf.Acos(cosInt) * Mathf.Rad2Deg;
            Vector3 es = (S - E).normalized, ew = (W - E).normalized;
            float cur = Vector3.Angle(es, ew);
            Vector3 axis = Vector3.Cross(es, ew);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.Cross(es, (pole - E).normalized);
            axis.Normalize();
            float dA = desired - cur;
            lower.rotation = Quaternion.AngleAxis(dA, axis) * lower.rotation;
            float after = Vector3.Angle((S - lower.position).normalized, (hand.position - lower.position).normalized);
            if (Mathf.Abs(after - desired) > Mathf.Abs(cur - desired))
                lower.rotation = Quaternion.AngleAxis(-2f * dA, axis) * lower.rotation;
            upper.rotation = Quaternion.FromToRotation(hand.position - S, target - S) * upper.rotation;
            Vector3 ax2 = (target - S).normalized;
            Vector3 eP = Vector3.ProjectOnPlane(lower.position - S, ax2);
            Vector3 pP = Vector3.ProjectOnPlane(pole - S, ax2);
            if (eP.sqrMagnitude > 1e-8f && pP.sqrMagnitude > 1e-8f)
                upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(eP, pP, ax2) * 0.9f, ax2) * upper.rotation;
        }
    }
}
