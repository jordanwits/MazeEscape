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

        // Harmonica being played: lift it from the carry pose just applied up to the mouth, arm and all.
        if (_heldItem is HarmonicaItem harmonica && harmonica.PlayPoseWeight > 0.001f)
            ApplyHarmonicaPlayPose(harmonica, seat);

        CaptureGrip(seat);

        if (_heldItem is FlashlightItem flashlight)
            flashlight.AimHeldLightAlongPitch();
    }

    bool IsBodyLimp() => _ragdoll != null && (_ragdoll.IsRagdolled || _ragdoll.IsHeld);

    // ---------- harmonica: raised to the mouth while playing ----------

    Transform _headBone;
    Transform _lowerArmR;
    bool _fistAxesCached;
    Vector3 _fistTunnelInHand;   // little-finger knuckle -> index knuckle, hand-local
    Vector3 _forearmInHand;      // wrist -> back along the forearm, hand-local (perpendicular to the tunnel)

    /// <summary>
    /// Playing pose, procedural so it holds on every head angle and every peer: the harmonica is placed on the
    /// lips in the head's frame (<see cref="HarmonicaItem.TryGetPlayPose"/>), the fist is turned so its tunnel
    /// runs along the harmonica toward the mouth with the back of the hand facing out, and a two-bone IK on the
    /// right arm brings the fist to the harmonica's right end. Blended from the carry pose by
    /// <see cref="HarmonicaItem.PlayPoseWeight"/>, with the elbow hint starting at the animated elbow so the
    /// lift starts exactly where the carry pose is. The item is finally re-seated on the actual hand, so it can
    /// never float off the fist if the mouth is out of reach.
    /// </summary>
    void ApplyHarmonicaPlayPose(HarmonicaItem harmonica, Transform seat)
    {
        if (_headBone == null)
            _headBone = animator.GetBoneTransform(HumanBodyBones.Head);
        if (_upperArmR == null)
            _upperArmR = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (_lowerArmR == null)
            _lowerArmR = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        if (_handBone == null)
            _handBone = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (_headBone == null || _upperArmR == null || _lowerArmR == null || _handBone == null || seat == null)
            return;
        if (!CacheFistAxes(seat))
            return;

        bool firstPersonView = playerController != null && playerController.IsFirstPersonViewOwner;
        if (!harmonica.TryGetPlayPose(_headBone, firstPersonView, out Vector3 playPos, out Quaternion playRot))
            return;

        float w = harmonica.PlayPoseWeight;
        Transform item = harmonica.transform;
        Vector3 itemPos = Vector3.Lerp(item.position, playPos, w);
        Quaternion itemRot = Quaternion.Slerp(item.rotation, playRot, w);
        Vector3 gripLocal = harmonica.HeldGripLocalOffset;

        // Fist orientation at the mouth: the tunnel runs along the harmonica with the index side OUTWARD (toward its
        // right end), which with a straight wrist and the forearm hanging down turns the palm toward the face —
        // the right hand cupping the end. Index side toward the mouth would face the palm out instead.
        Vector3 along = playRot * Vector3.back;
        Vector3 forearm = harmonica.PlayForearmDirection(playRot);
        Vector3 forearmPerp = forearm - Vector3.Dot(forearm, along) * along;
        if (forearmPerp.sqrMagnitude < 1e-6f)
            forearmPerp = -(playRot * Vector3.up);
        forearmPerp.Normalize();
        Quaternion handPlay = Quaternion.LookRotation(along, forearmPerp)
            * Quaternion.Inverse(Quaternion.LookRotation(_fistTunnelInHand, _forearmInHand));

        Quaternion handRot = Quaternion.Slerp(_handBone.rotation, handPlay, w);
        Vector3 seatFromWrist = Quaternion.Inverse(_handBone.rotation) * (seat.position - _handBone.position);
        Vector3 wristTarget = itemPos + itemRot * gripLocal - handRot * seatFromWrist;

        // Bend the elbow down the forearm line, starting from the animated elbow so the lift has no jump.
        Vector3 elbowPlay = wristTarget + forearm.normalized * 0.25f;
        Vector3 elbowHint = Vector3.Lerp(_lowerArmR.position, elbowPlay, w);

        SolveTwoBoneIK(_upperArmR, _lowerArmR, _handBone, wristTarget, elbowHint);
        _handBone.rotation = handRot;

        // Seat on the real hand: identical to the target when it was in reach, still in the fist when it was not.
        item.SetPositionAndRotation(seat.position - itemRot * gripLocal, itemRot);
    }

    bool CacheFistAxes(Transform seat)
    {
        if (_fistAxesCached)
            return true;

        Transform index = animator.GetBoneTransform(HumanBodyBones.RightIndexProximal);
        Transform little = animator.GetBoneTransform(HumanBodyBones.RightLittleProximal);
        Transform middle = animator.GetBoneTransform(HumanBodyBones.RightMiddleProximal);
        if (index == null || little == null || middle == null)
            return false;

        // Knuckle joints are fixed in the hand bone's frame (finger muscles only rotate bones about them), so
        // these are constants of the rig, whatever the fingers are doing.
        Quaternion toHand = Quaternion.Inverse(_handBone.rotation);
        Vector3 tunnel = (toHand * (index.position - little.position)).normalized;
        // A straight wrist continues the hand's own wrist->knuckles line back down the forearm.
        Vector3 forearm = toHand * (_handBone.position - middle.position);
        forearm = (forearm - Vector3.Dot(forearm, tunnel) * tunnel).normalized;
        if (tunnel.sqrMagnitude < 0.5f || forearm.sqrMagnitude < 0.5f)
            return false;

        _fistTunnelInHand = tunnel;
        _forearmInHand = forearm;
        _fistAxesCached = true;
        return true;
    }

    /// <summary>Analytic two-bone IK: puts <paramref name="end"/> on the target, elbow bent toward the hint.</summary>
    static void SolveTwoBoneIK(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 hint)
    {
        Vector3 a = upper.position;
        Vector3 b = lower.position;
        Vector3 c = end.position;
        float ab = (b - a).magnitude;
        float bc = (c - b).magnitude;
        Vector3 toTarget = target - a;
        float len = toTarget.magnitude;
        if (ab < 1e-4f || bc < 1e-4f || len < 1e-4f)
            return;

        len = Mathf.Clamp(len, Mathf.Abs(ab - bc) + 1e-4f, ab + bc - 1e-4f);
        Vector3 dir = toTarget / toTarget.magnitude;

        Vector3 toHint = hint - a;
        Vector3 bend = toHint - Vector3.Dot(toHint, dir) * dir;
        if (bend.sqrMagnitude < 1e-8f)
        {
            Vector3 toElbow = b - a;
            bend = toElbow - Vector3.Dot(toElbow, dir) * dir;
        }
        if (bend.sqrMagnitude < 1e-8f)
            return;
        bend.Normalize();

        float along = (ab * ab - bc * bc + len * len) / (2f * len);
        float height = Mathf.Sqrt(Mathf.Max(0f, ab * ab - along * along));
        Vector3 elbow = a + dir * along + bend * height;

        upper.rotation = Quaternion.FromToRotation(b - a, elbow - a) * upper.rotation;
        Vector3 b2 = lower.position;
        lower.rotation = Quaternion.FromToRotation(end.position - b2, a + dir * len - b2) * lower.rotation;
    }

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
