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

    Transform _holdPoint;
    Transform _followTransform; // camera-pitch transform, for view-aimed items (flashlight)
    Transform _handBone;
    Transform _pinchSocket;     // where the thumb and index tips meet, for pinched flat items
    Transform _cupSocket;       // axis of the C the hand forms in the cup pose, for cans and rolls
    Transform _ballSocket;      // centre of the sphere the hand drapes over, for the throwable ball
    GrabbableInventoryItem _heldItem;
    int _lastHoldChildCount = -1;

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
    }

    void LateUpdate()
    {
        if (animator == null || !animator.isHuman || !animator.enabled)
            return;

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
                // Negative: rotating the wrist around player-right the other way tips the hand up when looking up.
                float pitch = Mathf.Clamp(-elevationDeg * viewPitchWristFollow, -maxWristPitchDegrees, maxWristPitchDegrees);
                _handBone.rotation = Quaternion.AngleAxis(pitch, transform.right) * _handBone.rotation;
            }
        }

        Transform seat = SeatFor(_heldItem);

        if (forceForwardAim && _heldItem.HeldAimsAlongView && _followTransform != null)
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

        if (_heldItem is FlashlightItem flashlight)
            flashlight.AimHeldLightAlongPitch();
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
