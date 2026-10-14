using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Glues the currently-held hand-socket item onto the avatar's animated right hand every frame. Runs after
/// MovementViewBob (500) so the item rides the *final* hand pose — animation, IK and bob included — which
/// keeps the grip pixel-perfect while walking and running. The wrist orientation comes purely from the
/// Hold_OneHand pose clip (no rotation IK), so hands always look natural; the flashlight beam alone keeps
/// tracking the camera pitch via <see cref="FlashlightItem.AimHeldLightAlongPitch"/>.
/// Lives on the player root (all avatars — local, remote and offline follow their own animation locally).
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(700)]
public class HeldItemHandSocketFollow : MonoBehaviour
{
    [Header("References (auto-resolved when empty)")]
    [SerializeField] Animator animator;
    [SerializeField] PlayerController playerController;
    [Tooltip("Grip socket child under the right-hand bone. Falls back to the hand bone itself when missing.")]
    [SerializeField] Transform handSocket;

    [Header("Forward aim")]
    [Tooltip("Lock the held item's barrel/forward to a fixed direction (relative to the player) instead of the hand rotation, so a flashlight always points forward while the wrist is posed freely.")]
    [SerializeField] bool forceForwardAim = true;
    [Tooltip("Aim direction in player-local space: +Z forward, small -Y for a slight downward tilt.")]
    [SerializeField] Vector3 forwardAimLocal = new Vector3(0.08f, -0.12f, 1f);

    [Header("View pitch → wrist")]
    [Tooltip("For view-aimed items (flashlight), tip the wrist up/down with the look direction so the hand stays with the barrel. 1 = fully follow, 0 = fixed wrist.")]
    [SerializeField, Range(0f, 1f)] float viewPitchWristFollow = 1f;
    [Tooltip("Clamp on how far the wrist tips up/down (degrees).")]
    [SerializeField] float maxWristPitchDegrees = 55f;

    [Header("View pitch → arms")]
    [Tooltip("Clamp on how far the arms swing up/down at the shoulder (degrees). Per-item amount comes from GrabbableInventoryItem.HeldArmViewPitchFollow.")]
    [SerializeField] float maxArmPitchDegrees = 70f;

    Transform _holdPoint;
    Transform _followTransform; // camera-pitch transform, for view-aimed items (flashlight)
    Transform _handBone;
    Transform _upperArmR;
    Transform _upperArmL;
    Transform _pinchSocket;     // where the thumb and index tips meet, for pinched flat items
    Transform _cupSocket;       // axis of the C the hand forms in the cup pose, for cans and rolls
    Transform _ballSocket;      // centre of the sphere the hand drapes over, for the throwable ball
    GrabbableInventoryItem _heldItem;
    int _lastHoldChildCount = -1;
    PlayerRagdollController _ragdoll;

    // The held item's pose in its seat's space, captured on every animated frame. While the body is limp the item
    // keeps exactly this grip, so it rides the ragdoll (or enemy-held) hand the way it was being held.
    GrabbableInventoryItem _gripCapturedItem;
    Transform _gripCapturedSeat;
    Vector3 _gripLocalPosition;
    Quaternion _gripLocalRotation = Quaternion.identity;

    // Two-hand items (not seated on the hand socket) hidden while the body is limp, shown again at the get-up.
    readonly List<GrabbableInventoryItem> _hiddenWhileLimp = new List<GrabbableInventoryItem>();
    readonly List<Renderer> _rendererScratch = new List<Renderer>();

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
        _ragdoll = GetComponent<PlayerRagdollController>();
    }

    void OnDisable()
    {
        RestoreItemsHiddenWhileLimp();
    }

    void LateUpdate()
    {
        if (animator == null || !animator.isHuman)
            return;

        // Ragdolled, or pinned by a Mutant/RatBot grab: the animator is off and the hand is a physics bone (or the
        // pose stream's puppet on another peer). Bailing out here used to leave the item to GrabbableInventoryItem,
        // which parks it at HoldPoint on the root — and the root stays where the player was hit, so the item hung
        // in mid-air there for the whole knockdown.
        bool limp = !animator.enabled;
        if (limp && !IsBodyLimp())
        {
            RestoreItemsHiddenWhileLimp();
            return;
        }

        if (limp)
        {
            RefreshHeldItem();
            HideTwoHandItemsWhileLimp();
            SeatHeldItemInLimpHand();
            return;
        }

        RestoreItemsHiddenWhileLimp();
        ResolveHandSocket();
        RefreshHeldItem();

        if (_heldItem == null || handSocket == null)
            return;

        // Per-item wrist rotation, applied before anything reads the socket (the socket is a child of the hand,
        // so this carries the grip point with it). The clip's fist points its finger tunnel forward; items that
        // are gripped around an upright axis — a can, a raised glowstick — need the whole hand turned to match.
        Vector3 wristEuler = _heldItem.HeldWristEulerOffset;
        if (wristEuler.sqrMagnitude > 0.0001f)
        {
            if (_handBone == null)
                _handBone = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (_handBone != null)
            {
                Quaternion inPlayerSpace = transform.rotation * Quaternion.Euler(wristEuler) * Quaternion.Inverse(transform.rotation);
                _handBone.rotation = inPlayerSpace * _handBone.rotation;
            }
        }

        // View-aimed items (flashlight): tip the wrist up/down with the look direction BEFORE seating the
        // item, so the hand stays with the barrel instead of the barrel rotating out of a fixed fist.
        if (_heldItem.HeldAimsAlongView && _followTransform != null)
        {
            if (_handBone == null)
                _handBone = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (_handBone != null)
            {
                Vector3 localView = Quaternion.Inverse(transform.rotation) * _followTransform.forward;
                float elevationDeg = Mathf.Asin(Mathf.Clamp(localView.y, -1f, 1f)) * Mathf.Rad2Deg;

                // Weapons swing the whole arm at the shoulder, so looking down lowers the gun instead of
                // leaving it planted across the chest. Applied first: the wrist tip below then works on
                // top of the already-swung arm.
                float armFollow = Mathf.Clamp01(_heldItem.HeldArmViewPitchFollow);
                if (armFollow > 0.001f)
                    SwingArmsWithViewPitch(elevationDeg, armFollow);

                // The hand rides the arm, so a fully-following arm has already pitched it — fade the wrist
                // tip out by the same amount or the hand ends up pitched twice.
                float wristFollow = viewPitchWristFollow * (1f - armFollow);
                if (wristFollow > 0.001f)
                {
                    // Negative: rotating the wrist around player-right the other way tips the hand up when looking up.
                    float pitch = Mathf.Clamp(-elevationDeg * wristFollow, -maxWristPitchDegrees, maxWristPitchDegrees);
                    _handBone.rotation = Quaternion.AngleAxis(pitch, transform.right) * _handBone.rotation;
                }
            }
        }

        Transform seat = SeatFor(_heldItem);

        if (_heldItem.HeldFollowsHandRotation)
        {
            // Swung weapons (sword): rotation comes from the hand bone, so the blade travels through the
            // swing arc. The forced aim below would pin it to a fixed player-space direction for the whole
            // animation, which is correct for a flashlight and nonsense for a weapon.
            _heldItem.ApplyHandSocketHeldPose(seat);
        }
        else if (forceForwardAim && _heldItem.HeldAimsAlongView && _followTransform != null)
        {
            // Flashlight: barrel tilts up/down with the view (camera pitch), matching the beam.
            _heldItem.ApplyHandSocketHeldPoseAim(seat, _followTransform.rotation);
        }
        else if (forceForwardAim)
        {
            Vector3 aim = transform.TransformDirection(forwardAimLocal);
            _heldItem.ApplyHandSocketHeldPoseForwardAim(seat, aim, transform.up);
        }
        else
        {
            _heldItem.ApplyHandSocketHeldPose(seat);
        }

        CaptureGrip(seat);

        if (_heldItem is FlashlightItem flashlight)
            flashlight.AimHeldLightAlongPitch();
    }

    bool IsBodyLimp() => _ragdoll != null && (_ragdoll.IsRagdolled || _ragdoll.IsHeld);

    void CaptureGrip(Transform seat)
    {
        Transform item = _heldItem.transform;
        _gripCapturedItem = _heldItem;
        _gripCapturedSeat = seat;
        _gripLocalPosition = seat.InverseTransformPoint(item.position);
        _gripLocalRotation = Quaternion.Inverse(seat.rotation) * item.rotation;
    }

    /// <summary>
    /// Keeps a hand-socket item in the limp hand: the last animated grip, carried rigidly by the hand bone. No wrist
    /// or arm writes here — those bones belong to the physics (or the pose stream) now. The flashlight beam still
    /// follows the view.
    /// </summary>
    void SeatHeldItemInLimpHand()
    {
        if (_heldItem == null || handSocket == null)
            return;

        Transform seat = SeatFor(_heldItem);
        if (_gripCapturedItem == _heldItem && _gripCapturedSeat == seat)
        {
            _heldItem.transform.SetPositionAndRotation(
                seat.TransformPoint(_gripLocalPosition), seat.rotation * _gripLocalRotation);
        }
        else
        {
            // Never seen animated in this hand (e.g. spawned straight into a knockdown): the plain socket pose.
            _heldItem.ApplyHandSocketHeldPose(seat);
        }

        if (_heldItem is FlashlightItem flashlight)
            flashlight.AimHeldLightAlongPitch();
    }

    /// <summary>
    /// A two-hand item (the StarBall) has no hand to ride, and left at HoldPoint it would float where the player
    /// was hit, so it is hidden until the get-up. An item that leaves the hand meanwhile (dropped on death) is
    /// shown again straight away.
    /// </summary>
    void HideTwoHandItemsWhileLimp()
    {
        for (int i = _hiddenWhileLimp.Count - 1; i >= 0; i--)
        {
            GrabbableInventoryItem hidden = _hiddenWhileLimp[i];
            if (hidden != null && hidden.IsHeld && hidden.transform.parent == _holdPoint)
                continue;

            SetItemRenderersHidden(hidden, false);
            _hiddenWhileLimp.RemoveAt(i);
        }

        if (_holdPoint == null)
            return;

        for (int i = 0; i < _holdPoint.childCount; i++)
        {
            var g = _holdPoint.GetChild(i).GetComponent<GrabbableInventoryItem>();
            if (g == null || !g.IsHeld || g.IsStashed || g.HeldAttachToHandSocket || _hiddenWhileLimp.Contains(g))
                continue;

            SetItemRenderersHidden(g, true);
            _hiddenWhileLimp.Add(g);
        }
    }

    void RestoreItemsHiddenWhileLimp()
    {
        if (_hiddenWhileLimp.Count == 0)
            return;

        for (int i = 0; i < _hiddenWhileLimp.Count; i++)
            SetItemRenderersHidden(_hiddenWhileLimp[i], false);
        _hiddenWhileLimp.Clear();
    }

    void SetItemRenderersHidden(GrabbableInventoryItem item, bool hidden)
    {
        if (item == null)
            return;

        // Shown again by the item's own rule (GrabbableInventoryItem.RefreshInventoryVisibility): hidden only while
        // it sits stashed in a hotbar slot.
        bool hiddenByInventory = item.IsHeld && item.IsStashed;
        item.GetComponentsInChildren(true, _rendererScratch);
        for (int i = 0; i < _rendererScratch.Count; i++)
        {
            if (_rendererScratch[i] != null)
                _rendererScratch[i].forceRenderingOff = hidden || hiddenByInventory;
        }
        _rendererScratch.Clear();
    }

    /// <summary>
    /// Rotates both upper arms about the shoulder line so the whole arm cluster tracks the aim. Each arm is
    /// turned about its own shoulder rather than a shared pivot — that is the same rigid motion here, because
    /// the shoulders are offset along the rotation axis (player-right), and it keeps each shoulder joint
    /// exactly where the animation put it, so nothing stretches at the deltoid.
    /// </summary>
    void SwingArmsWithViewPitch(float elevationDeg, float armFollow)
    {
        if (_upperArmR == null)
            _upperArmR = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (_upperArmL == null)
            _upperArmL = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);

        // Per-item cap wins when set: the arms stop short of vertical at the extremes of the look range
        // while the view itself stays free to keep going.
        float itemMax = _heldItem != null ? _heldItem.HeldArmViewPitchMaxDegrees : 0f;
        float limit = itemMax > 0.01f ? itemMax : maxArmPitchDegrees;

        float armPitch = Mathf.Clamp(-elevationDeg * armFollow, -limit, limit);
        if (Mathf.Abs(armPitch) < 0.01f)
            return;

        Quaternion swing = Quaternion.AngleAxis(armPitch, transform.right);
        if (_upperArmR != null)
            _upperArmR.rotation = swing * _upperArmR.rotation;
        if (_upperArmL != null)
            _upperArmL.rotation = swing * _upperArmL.rotation;
    }

    void ResolveHandSocket()
    {
        if (handSocket != null && _pinchSocket != null && _cupSocket != null && _ballSocket != null)
            return;

        Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand == null)
            return;

        if (handSocket == null)
        {
            Transform authored = hand.Find("GripSocket_R");
            handSocket = authored != null ? authored : hand;
        }
        if (_pinchSocket == null)
            _pinchSocket = hand.Find("PinchSocket_R");
        if (_cupSocket == null)
            _cupSocket = hand.Find("CupSocket_R");
        if (_ballSocket == null)
            _ballSocket = hand.Find("BallSocket_R");
    }

    /// <summary>
    /// Where this item is seated. Each grip pose puts the object somewhere different: the fist centre for a
    /// closed fist, the fingertips for a pinch, and the axis of the C — well clear of the fist — for a cup.
    /// </summary>
    Transform SeatFor(GrabbableInventoryItem item)
    {
        if (item.GripStyle == HeldGripStyle.Pinch && _pinchSocket != null)
            return _pinchSocket;
        if (item.GripStyle == HeldGripStyle.Cup && _cupSocket != null)
            return _cupSocket;
        if (item.GripStyle == HeldGripStyle.Ball && _ballSocket != null)
            return _ballSocket;
        return handSocket;
    }

    void RefreshHeldItem()
    {
        if (_holdPoint == null)
        {
            if (playerController == null
                || !playerController.TryGetFlashlightAttachmentTargets(out _holdPoint, out _followTransform)
                || _holdPoint == null)
            {
                _heldItem = null;
                return;
            }
        }

        int childCount = _holdPoint.childCount;
        bool cachedValid = _heldItem != null && _heldItem.IsHeld && !_heldItem.IsStashed
            && _heldItem.HeldAttachToHandSocket && _heldItem.transform.parent == _holdPoint;
        if (childCount == _lastHoldChildCount && cachedValid)
            return;

        _lastHoldChildCount = childCount;
        _heldItem = null;
        for (int i = 0; i < childCount; i++)
        {
            var g = _holdPoint.GetChild(i).GetComponent<GrabbableInventoryItem>();
            if (g != null && g.IsHeld && !g.IsStashed && g.HeldAttachToHandSocket)
            {
                _heldItem = g;
                break;
            }
        }
    }
}
