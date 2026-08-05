using UnityEngine;

/// <summary>
/// DEV TOOL. Play-mode authoring aid for the two-handed flare gun hold pose. Spawns draggable handle
/// objects for both wrists and both elbows and solves the arms onto them every LateUpdate — after the
/// animator and view bob, before <see cref="HeldItemHandSocketFollow"/> seats the gun, so the gun rides
/// whatever pose you drag out. Bake the result with <b>Tools/Flare Gun Pose Tuner</b>.
///
/// Nothing here ships enabled: the component is added at runtime by the tuner window and removed again.
/// </summary>
[DefaultExecutionOrder(650)]
public class FlareGunPoseTuner : MonoBehaviour
{
    [Header("Drag these in the Scene view (select them in the Hierarchy)")]
    public Transform wristTargetR;
    public Transform elbowTargetR;
    public Transform wristTargetL;
    public Transform elbowTargetL;

    [Header("Options")]
    [Tooltip("Solve the arms onto the handles. Turn off to see the raw clip pose again.")]
    public bool solveArms = true;
    [Tooltip("While ON the gun stays seated in the right hand. Turn OFF to free the gun so you can drag it.")]
    public bool gunFollowsHand = true;
    [Tooltip("Match the wrist rotation to the handle's rotation (rotate the handle to roll the grip).")]
    public bool applyWristRotation = true;

    Animator _animator;
    HeldItemHandSocketFollow _socketFollow;
    GrabbableInventoryItem _freedItem;
    bool _lastGunFollows = true;

    public Animator TunedAnimator => _animator != null ? _animator : (_animator = GetComponentInChildren<Animator>());

    void OnEnable()
    {
        _animator = GetComponentInChildren<Animator>();
        _socketFollow = GetComponentInChildren<HeldItemHandSocketFollow>(true);
        EnsureHandles();
        _lastGunFollows = gunFollowsHand;
    }

    void OnDisable()
    {
        // never leave the rig's helpers suspended
        SetGunFree(false);
    }

    /// <summary>Creates the four handles at the current bone poses (idempotent).</summary>
    public void EnsureHandles()
    {
        if (TunedAnimator == null || !_animator.isHuman)
            return;

        wristTargetR = EnsureHandle(wristTargetR, "TUNER_WristR", HumanBodyBones.RightHand);
        elbowTargetR = EnsureHandle(elbowTargetR, "TUNER_ElbowR", HumanBodyBones.RightLowerArm);
        wristTargetL = EnsureHandle(wristTargetL, "TUNER_WristL", HumanBodyBones.LeftHand);
        elbowTargetL = EnsureHandle(elbowTargetL, "TUNER_ElbowL", HumanBodyBones.LeftLowerArm);
    }

    Transform EnsureHandle(Transform existing, string handleName, HumanBodyBones seedBone)
    {
        if (existing != null)
            return existing;

        Transform seed = _animator.GetBoneTransform(seedBone);
        var go = new GameObject(handleName);
        go.transform.SetParent(transform, false);
        if (seed != null)
            go.transform.SetPositionAndRotation(seed.position, seed.rotation);
        return go.transform;
    }

    /// <summary>Re-seats every handle back onto the current animated pose (undo your dragging).</summary>
    public void ResetHandlesToPose()
    {
        if (TunedAnimator == null)
            return;
        SnapHandle(wristTargetR, HumanBodyBones.RightHand);
        SnapHandle(elbowTargetR, HumanBodyBones.RightLowerArm);
        SnapHandle(wristTargetL, HumanBodyBones.LeftHand);
        SnapHandle(elbowTargetL, HumanBodyBones.LeftLowerArm);
    }

    void SnapHandle(Transform handle, HumanBodyBones bone)
    {
        Transform t = _animator.GetBoneTransform(bone);
        if (handle != null && t != null)
            handle.SetPositionAndRotation(t.position, t.rotation);
    }

    void LateUpdate()
    {
        if (gunFollowsHand != _lastGunFollows)
        {
            SetGunFree(!gunFollowsHand);
            _lastGunFollows = gunFollowsHand;
        }

        if (!solveArms || TunedAnimator == null || !_animator.isHuman)
            return;

        SolveArm(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            wristTargetR, elbowTargetR);
        SolveArm(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            wristTargetL, elbowTargetL);
    }

    void SolveArm(HumanBodyBones upperBone, HumanBodyBones lowerBone, HumanBodyBones handBone,
        Transform wristTarget, Transform elbowTarget)
    {
        if (wristTarget == null)
            return;

        Transform upper = _animator.GetBoneTransform(upperBone);
        Transform lower = _animator.GetBoneTransform(lowerBone);
        Transform hand = _animator.GetBoneTransform(handBone);
        if (upper == null || lower == null || hand == null)
            return;

        Vector3 shoulder = upper.position;
        float upperLength = (lower.position - shoulder).magnitude;
        float lowerLength = (hand.position - lower.position).magnitude;

        Vector3 toTarget = wristTarget.position - shoulder;
        float reach = Mathf.Min(toTarget.magnitude, (upperLength + lowerLength) * 0.995f);
        if (reach < 1e-4f)
            return;

        Vector3 axis = toTarget.normalized;
        float alongAxis = (upperLength * upperLength - lowerLength * lowerLength + reach * reach) / (2f * reach);
        float offAxis = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - alongAxis * alongAxis));

        // Elbow swivel: aim at the elbow handle, falling back to the current elbow so an untouched
        // handle never changes the pose.
        Vector3 poleSource = elbowTarget != null ? elbowTarget.position : lower.position;
        Vector3 pole = (poleSource - shoulder) - Vector3.Dot(poleSource - shoulder, axis) * axis;
        if (pole.sqrMagnitude < 1e-8f)
        {
            pole = (lower.position - shoulder) - Vector3.Dot(lower.position - shoulder, axis) * axis;
            if (pole.sqrMagnitude < 1e-8f)
                pole = Vector3.Cross(axis, Vector3.up);
        }
        pole.Normalize();

        Vector3 elbow = shoulder + axis * alongAxis + pole * offAxis;
        Vector3 wrist = shoulder + axis * reach;

        upper.rotation = Quaternion.FromToRotation(lower.position - shoulder, elbow - shoulder) * upper.rotation;
        Vector3 elbowNow = lower.position;
        lower.rotation = Quaternion.FromToRotation(hand.position - elbowNow, wrist - elbowNow) * lower.rotation;

        if (applyWristRotation)
            hand.rotation = wristTarget.rotation;
    }

    /// <summary>
    /// Suspends the two things that re-seat the held gun every frame so it can be dragged freely, and
    /// restores them afterwards.
    /// </summary>
    void SetGunFree(bool free)
    {
        if (_socketFollow == null)
            _socketFollow = GetComponentInChildren<HeldItemHandSocketFollow>(true);

        if (free)
        {
            if (_socketFollow != null)
                _socketFollow.enabled = false;
            if (_freedItem == null)
            {
                _freedItem = FindHeldFlareGun();
                if (_freedItem != null)
                    _freedItem.enabled = false;
            }
            return;
        }

        if (_socketFollow != null)
            _socketFollow.enabled = true;
        if (_freedItem != null)
        {
            _freedItem.enabled = true;
            _freedItem = null;
        }
    }

    GrabbableInventoryItem FindHeldFlareGun()
    {
        foreach (GrabbableInventoryItem g in GrabbableInventoryItem.GetRegisteredItems())
        {
            if (g is FlareGunItem && g.IsHeld && !g.IsStashed)
                return g;
        }

        var direct = FindFirstObjectByType<FlareGunItem>();
        return direct;
    }

    void OnDrawGizmos()
    {
        DrawHandle(wristTargetR, new Color(1f, 0.35f, 0.2f), 0.035f);
        DrawHandle(elbowTargetR, new Color(1f, 0.75f, 0.3f), 0.025f);
        DrawHandle(wristTargetL, new Color(0.3f, 0.7f, 1f), 0.035f);
        DrawHandle(elbowTargetL, new Color(0.5f, 0.9f, 1f), 0.025f);
    }

    void DrawHandle(Transform t, Color color, float radius)
    {
        if (t == null)
            return;
        Gizmos.color = color;
        Gizmos.DrawWireSphere(t.position, radius);
        Gizmos.DrawLine(t.position, t.position + t.forward * 0.08f);
    }
}
