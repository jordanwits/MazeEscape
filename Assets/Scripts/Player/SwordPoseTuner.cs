using UnityEngine;

/// <summary>
/// DEV TOOL. Play-mode authoring aid for how the sword is carried. Drives three things:
///
///   <b>Elbow</b> — a draggable pole handle that swivels the arm without moving the hand.
///   <b>Wrist</b> — a draggable hand handle, plus three euler sliders applied in the FOREARM's frame
///                  (so "tilt the wrist up" means the same thing regardless of where the arm is).
///   <b>Carry</b> — the sword's seat in the hand: grip offset + rotation offset, live-editable, or
///                  captured from the sword after dragging it freely in the Scene view.
///
/// Runs at 650: after the animator and view bob, before <see cref="HeldItemHandSocketFollow"/> (700) seats
/// the blade, so the sword rides whatever pose is dragged out. Bake with <b>Tools/Sword Pose Tuner</b>.
///
/// Baking from PLAY MODE is deliberate: arm muscles are relative to the chest, which locomotion drives, so a
/// pose measured in an edit-mode preview lands somewhere else in the running game.
///
/// Nothing here ships enabled — the tuner window adds the component at runtime and removes it again.
/// </summary>
[DefaultExecutionOrder(650)]
public class SwordPoseTuner : MonoBehaviour
{
    [Header("Drag these in the Scene view (select them in the Hierarchy)")]
    public Transform wristTargetR;
    public Transform elbowTargetR;

    [Header("Arm")]
    [Tooltip("Solve the right arm onto the handles. Turn off to see the raw clip pose again.")]
    public bool solveArm = true;
    [Tooltip("Match the wrist rotation to the handle's rotation (rotate TUNER_WristR to roll the grip).")]
    public bool wristFromHandle = true;

    [Header("Wrist trim (degrees, in the forearm's frame)")]
    [Tooltip("X tilts the hand up/down, Y turns it left/right, Z rolls the blade. Applied on top of the base wrist rotation.")]
    public Vector3 wristEuler;

    [Header("Sword carry")]
    [Tooltip("Push the values below onto the held sword every frame so the carry can be tuned live.")]
    public bool applyCarryOverrides = true;
    [Tooltip("Where the hand grips the blade, in the sword's local space (its GripPoint_R).")]
    public Vector3 carryGripLocalPosition;
    [Tooltip("How the blade is angled in the fist. Captured, not guessed — use 'Capture Sword From Scene'.")]
    public Vector3 carryRotationEuler;
    [Tooltip("While OFF the sword is released from the hand so it can be dragged freely in the Scene view. "
        + "Turn it back ON (or hit Capture) once it is where you want it.")]
    public bool swordFollowsHand = true;

    Animator _animator;
    HeldItemHandSocketFollow _socketFollow;
    GrabbableInventoryItem _freedItem;
    bool _lastSwordFollows = true;
    bool _capturedInitialCarry;

    public Animator TunedAnimator => _animator != null ? _animator : (_animator = GetComponentInChildren<Animator>());

    void OnEnable()
    {
        _animator = GetComponentInChildren<Animator>();
        _socketFollow = GetComponentInChildren<HeldItemHandSocketFollow>(true);
        EnsureHandles();
        SeedCarryFromHeldSword();
        _lastSwordFollows = swordFollowsHand;
    }

    void OnDisable()
    {
        // never leave the rig's helpers suspended
        SetSwordFree(false);
    }

    /// <summary>Reads the sword's authored carry values in, so opening the tuner never moves the blade.</summary>
    public void SeedCarryFromHeldSword()
    {
        if (_capturedInitialCarry)
            return;

        GrabbableInventoryItem sword = FindHeldSword();
        if (sword == null)
            return;

        Transform grip = sword.transform.Find("GripPoint_R");
        carryGripLocalPosition = grip != null ? grip.localPosition : Vector3.zero;
        carryRotationEuler = sword.HeldRotationOffsetEuler;
        _capturedInitialCarry = true;
    }

    /// <summary>Creates the two handles at the current bone poses (idempotent).</summary>
    public void EnsureHandles()
    {
        if (TunedAnimator == null || !_animator.isHuman)
            return;

        wristTargetR = EnsureHandle(wristTargetR, "TUNER_WristR", HumanBodyBones.RightHand);
        elbowTargetR = EnsureHandle(elbowTargetR, "TUNER_ElbowR", HumanBodyBones.RightLowerArm);
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

    /// <summary>Re-seats both handles onto the current animated pose (undo your dragging).</summary>
    public void ResetHandlesToPose()
    {
        if (TunedAnimator == null)
            return;
        SnapHandle(wristTargetR, HumanBodyBones.RightHand);
        SnapHandle(elbowTargetR, HumanBodyBones.RightLowerArm);
    }

    void SnapHandle(Transform handle, HumanBodyBones bone)
    {
        Transform t = _animator.GetBoneTransform(bone);
        if (handle != null && t != null)
            handle.SetPositionAndRotation(t.position, t.rotation);
    }

    /// <summary>
    /// Reads the freed sword's current world transform back into <see cref="carryGripLocalPosition"/> and
    /// <see cref="carryRotationEuler"/> by inverting <see cref="GrabbableInventoryItem.ApplyHandSocketHeldPose"/>,
    /// so re-seating it puts the blade exactly where it was dragged. Returns false when there is nothing to read.
    /// </summary>
    public bool CaptureCarryFromScene(out string message)
    {
        message = string.Empty;
        GrabbableInventoryItem sword = FindHeldSword();
        Transform socket = ResolveGripSocket();
        if (sword == null || socket == null)
        {
            message = "No held sword (or no GripSocket_R) to capture from.";
            return false;
        }

        Quaternion itemRotation = sword.transform.rotation;

        // itemRotation = socket.rotation * Inverse(gripLocalRotation) * Euler(offset), with GripPoint_R kept
        // at identity rotation so the angle stays owned by the euler alone.
        carryRotationEuler = (Quaternion.Inverse(socket.rotation) * itemRotation).eulerAngles;

        // itemPosition = socket.position - itemRotation * Scale(gripLocalPosition, itemScale)
        Vector3 local = Quaternion.Inverse(itemRotation) * (socket.position - sword.transform.position);
        Vector3 scale = sword.transform.localScale;
        carryGripLocalPosition = new Vector3(
            local.x / Mathf.Max(1e-6f, scale.x),
            local.y / Mathf.Max(1e-6f, scale.y),
            local.z / Mathf.Max(1e-6f, scale.z));

        swordFollowsHand = true;
        applyCarryOverrides = true;

        // Push straight away rather than waiting for the next LateUpdate: re-seating with stale values would
        // snap the blade away from where it was just placed for a frame, which reads as the capture failing.
        PushCarryOverrides();

        message = $"Captured grip {carryGripLocalPosition:F4}, rotation {carryRotationEuler:F2}.";
        return true;
    }

    void LateUpdate()
    {
        if (swordFollowsHand != _lastSwordFollows)
        {
            SetSwordFree(!swordFollowsHand);
            _lastSwordFollows = swordFollowsHand;
        }

        if (TunedAnimator == null || !_animator.isHuman)
            return;

        if (solveArm)
        {
            SolveArm(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                wristTargetR, elbowTargetR);
        }

        if (applyCarryOverrides && swordFollowsHand)
            PushCarryOverrides();
    }

    /// <summary>
    /// Writes the tuner's carry values onto the live sword before the socket follower reads them. Only the
    /// running instance changes; the prefab is written by the Bake button.
    /// </summary>
    public void PushCarryOverrides()
    {
        GrabbableInventoryItem sword = FindHeldSword();
        if (sword == null)
            return;

        Transform grip = sword.transform.Find("GripPoint_R");
        if (grip != null)
        {
            grip.localPosition = carryGripLocalPosition;
            grip.localRotation = Quaternion.identity;
        }

        sword.SetHeldRotationOffsetEulerForTuning(carryRotationEuler);
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

        // Elbow swivel: aim at the elbow handle, falling back to the current elbow so an untouched handle
        // never changes the pose.
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

        if (wristFromHandle)
            hand.rotation = wristTarget.rotation;

        // Wrist trim, applied about the FOREARM's axes so the sliders mean the same thing at any arm position.
        if (wristEuler.sqrMagnitude > 0.0001f)
        {
            Quaternion inForearm = lower.rotation * Quaternion.Euler(wristEuler) * Quaternion.Inverse(lower.rotation);
            hand.rotation = inForearm * hand.rotation;
        }
    }

    /// <summary>
    /// Suspends the two things that re-seat the held sword every frame so it can be dragged freely, and
    /// restores them afterwards. The animator still moves the hand out from under a freed sword — freeze it
    /// with the tuner window's "Freeze animation" button while placing.
    /// </summary>
    void SetSwordFree(bool free)
    {
        if (_socketFollow == null)
            _socketFollow = GetComponentInChildren<HeldItemHandSocketFollow>(true);

        if (free)
        {
            if (_socketFollow != null)
                _socketFollow.enabled = false;
            if (_freedItem == null)
            {
                _freedItem = FindHeldSword();
                if (_freedItem != null)
                    _freedItem.enabled = false;   // its LateUpdate re-snaps to the HoldPoint too
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

    public Transform ResolveGripSocket()
    {
        if (TunedAnimator == null || !_animator.isHuman)
            return null;
        Transform hand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand == null)
            return null;
        Transform socket = hand.Find("GripSocket_R");
        return socket != null ? socket : hand;
    }

    /// <summary>The sword in hand; falls back to any sword in the scene so the tool still works if it was dropped.</summary>
    public GrabbableInventoryItem FindHeldSword()
    {
        foreach (GrabbableInventoryItem g in GrabbableInventoryItem.GetRegisteredItems())
        {
            if (g is SwordItem && g.IsHeld && !g.IsStashed)
                return g;
        }

        return FindFirstObjectByType<SwordItem>();
    }

    void OnDrawGizmos()
    {
        DrawHandle(wristTargetR, new Color(1f, 0.35f, 0.2f), 0.035f);
        DrawHandle(elbowTargetR, new Color(1f, 0.75f, 0.3f), 0.025f);
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
