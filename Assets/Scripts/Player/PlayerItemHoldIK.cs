using UnityEngine;

/// <summary>
/// Pins the avatar's hands onto the held item's grip points via built-in humanoid IK (IK Pass on Base Layer),
/// and renders the gated-pickup reach toward a world item. The held item itself stays parented to HoldPoint
/// and driven by camera pitch (replicated), so this runs identically for the local owner, remote proxies and
/// offline play. Grip targets are recomputed from current-frame inputs
/// (<see cref="GrabbableInventoryItem.TryComputeHeldGripWorldPose"/>) because the item transform is written in
/// LateUpdate and is one frame stale during the IK pass. Must live on the same GameObject as the Animator.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(55)]
public class PlayerItemHoldIK : MonoBehaviour
{
    [Header("References (auto-resolved when empty)")]
    [SerializeField] Animator animator;
    [SerializeField] PlayerController playerController;
    [SerializeField] NetworkPlayerAvatar networkPlayerAvatar;
    [SerializeField] PlayerRagdollController ragdollController;
    [SerializeField] MovementViewBob movementViewBob;
    [SerializeField] PlayerHealth playerHealth;

    [Header("Hold IK")]
    [SerializeField, Range(0f, 1f)] float handPositionWeight = 1f;
    [SerializeField, Range(0f, 1f)] float oneHandRotationWeight = 0.85f;
    [SerializeField, Range(0f, 1f)] float twoHandRotationWeight = 0.45f;
    [SerializeField, Range(0f, 1f)] float elbowHintWeight = 0.6f;
    [SerializeField] float weightInSeconds = 0.15f;
    [SerializeField] float weightOutSeconds = 0.12f;
    [Tooltip("IK goals are pulled to this fraction of the measured arm length so the elbow never locks straight (the HoldPoint sits beyond full arm reach).")]
    [SerializeField, Range(0.5f, 1f)] float maxReachFraction = 0.94f;
    [Tooltip("Extra rotation applied on top of the grip rotation to align this rig's palm axes. Tune visually once.")]
    [SerializeField] Vector3 rightHandRotationOffsetEuler;
    [SerializeField] Vector3 leftHandRotationOffsetEuler;
    [Tooltip("Curl hollow of the posed hand, in the hand bone's local space (measured on this rig). IK places the wrist so this point — not the wrist — lands on the grip, i.e. items sit in the palm.")]
    [SerializeField] Vector3 rightPalmCenterInHandSpace = new Vector3(0.111f, -0.025f, 0.01f);
    [SerializeField] Vector3 leftPalmCenterInHandSpace = new Vector3(0.111f, -0.025f, 0.01f);
    [Tooltip("Subtract MovementViewBob's pending hips lift from IK targets so hands stay on the item while bobbing.")]
    [SerializeField] bool compensateViewBob = true;

    [Header("Pickup reach presentation")]
    [SerializeField] float reachInSeconds = 0.24f;
    [SerializeField] float reachHoldSeconds = 0.12f;
    [SerializeField] float reachOutSeconds = 0.2f;

    [Header("Melee gate")]
    [Tooltip("Hold/reach IK releases while the masked punch plays — humanoid IK applies after ALL layers, so an un-gated hold would pin the punching fist back onto the item.")]
    [SerializeField] string upperBodyLayerName = "Upper Body";
    [SerializeField] int upperBodyLayerIndexFallback = 2;
    [SerializeField] string meleeStateNameOnUpperLayer = "RightHook";

    static readonly int HoldPoseHash = Animator.StringToHash("HoldPose");
    static readonly int SeatedHash = Animator.StringToHash("Seated");

    Transform _holdPoint;
    GrabbableInventoryItem _heldItem;
    int _lastHoldChildCount = -1;
    int _meleeLayerIndexCache = int.MinValue;
    float _rightWeight;
    float _leftWeight;
    float _armLength;

    // Reach envelope: timeline runs locally, keyed off the published target id (owner: PlayerController
    // property; remotes: NetworkPlayerAvatar netvar). A whiffed/stale id just extends and retracts.
    ulong _reachItemId;
    ulong _lastConsumedReachSourceId;
    float _reachTimer;
    bool _reachWindingOut;
    Vector3 _reachWorldPoint;
    bool _reachHasPoint;

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
        if (networkPlayerAvatar == null)
            networkPlayerAvatar = GetComponent<NetworkPlayerAvatar>();
        if (ragdollController == null)
            ragdollController = GetComponent<PlayerRagdollController>();
        if (movementViewBob == null)
            movementViewBob = GetComponent<MovementViewBob>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
    }

    void Start()
    {
        if (animator == null || !animator.isHuman)
            return;
        Transform upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform lower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (upper != null && lower != null && hand != null)
            _armLength = (lower.position - upper.position).magnitude + (hand.position - lower.position).magnitude;
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0 || animator == null || !animator.isHuman || !animator.enabled)
            return;

        RefreshHeldItem();
        float reachWeight = TickReachEnvelope();

        bool gated = IsGated();
        int holdPose = gated ? 0 : animator.GetInteger(HoldPoseHash);
        bool reaching = !gated && reachWeight > 0.001f && _reachHasPoint;
        // Hand-socket items ride the animated hand (HeldItemHandSocketFollow) — IK-ing the hand onto an
        // item that is glued to the hand would chase its own tail; the pose clip owns that arm instead.
        bool socketHeld = _heldItem != null && _heldItem.HeldAttachToHandSocket;
        bool wantRight = !gated && ((holdPose >= 1 && _heldItem != null && !socketHeld) || reaching);
        bool wantLeft = !gated && holdPose == 2 && _heldItem != null && !socketHeld;

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        _rightWeight = MoveWeight(_rightWeight, wantRight ? 1f : 0f, dt);
        _leftWeight = MoveWeight(_leftWeight, wantLeft ? 1f : 0f, dt);

        Vector3 bob = compensateViewBob && movementViewBob != null
            ? movementViewBob.ComputePendingBobWorldOffset()
            : Vector3.zero;

        ApplyHandIK(AvatarIKGoal.RightHand, AvatarIKHint.RightElbow, HumanBodyBones.RightUpperArm, _rightWeight,
            isLeft: false, holdPose, reaching, reachWeight, bob);
        ApplyHandIK(AvatarIKGoal.LeftHand, AvatarIKHint.LeftElbow, HumanBodyBones.LeftUpperArm, _leftWeight,
            isLeft: true, holdPose, reaching: false, reachWeight: 0f, bob);
    }

    void ApplyHandIK(AvatarIKGoal goal, AvatarIKHint hint, HumanBodyBones shoulderBone, float weight,
        bool isLeft, int holdPose, bool reaching, float reachWeight, Vector3 bob)
    {
        if (weight <= 0.001f)
        {
            animator.SetIKPositionWeight(goal, 0f);
            animator.SetIKRotationWeight(goal, 0f);
            animator.SetIKHintPositionWeight(hint, 0f);
            return;
        }

        Vector3 holdPos = default;
        Quaternion holdRot = Quaternion.identity;
        bool hasHold = _heldItem != null && !_heldItem.HeldAttachToHandSocket
            && _heldItem.TryComputeHeldGripWorldPose(isLeft, out holdPos, out holdRot);

        Vector3 targetPos;
        float rotationWeight = 0f;
        Quaternion targetRot = Quaternion.identity;

        if (reaching && hasHold)
        {
            targetPos = Vector3.Lerp(holdPos, _reachWorldPoint, reachWeight);
        }
        else if (reaching)
        {
            targetPos = _reachWorldPoint;
        }
        else if (hasHold)
        {
            targetPos = holdPos;
        }
        else
        {
            animator.SetIKPositionWeight(goal, 0f);
            animator.SetIKRotationWeight(goal, 0f);
            animator.SetIKHintPositionWeight(hint, 0f);
            return;
        }

        if (hasHold && !reaching)
        {
            Vector3 offset = isLeft ? leftHandRotationOffsetEuler : rightHandRotationOffsetEuler;
            targetRot = holdRot * Quaternion.Euler(offset);
            rotationWeight = (holdPose == 2 ? twoHandRotationWeight : oneHandRotationWeight) * weight;
            // Pull the wrist back so the palm's curl hollow — not the wrist bone — lands on the grip.
            targetPos -= targetRot * (isLeft ? leftPalmCenterInHandSpace : rightPalmCenterInHandSpace);
        }

        targetPos -= bob;

        Transform shoulder = animator.GetBoneTransform(shoulderBone);
        if (shoulder != null && _armLength > 0.01f)
        {
            Vector3 fromShoulder = targetPos - shoulder.position;
            float maxLen = _armLength * maxReachFraction;
            if (fromShoulder.magnitude > maxLen)
                targetPos = shoulder.position + fromShoulder.normalized * maxLen;
        }

        float positionWeight = handPositionWeight * weight * (reaching && !hasHold ? reachWeight : 1f);
        animator.SetIKPosition(goal, targetPos);
        animator.SetIKPositionWeight(goal, positionWeight);
        animator.SetIKRotation(goal, targetRot);
        animator.SetIKRotationWeight(goal, rotationWeight);

        if (shoulder != null)
        {
            Vector3 mid = (shoulder.position + targetPos) * 0.5f;
            Vector3 side = transform.right * (isLeft ? -1f : 1f);
            animator.SetIKHintPosition(hint, mid + side * 0.35f - transform.up * 0.2f);
            animator.SetIKHintPositionWeight(hint, elbowHintWeight * weight);
        }
    }

    float MoveWeight(float current, float target, float dt)
    {
        float rate = target > current
            ? dt / Mathf.Max(0.01f, weightInSeconds)
            : dt / Mathf.Max(0.01f, weightOutSeconds);
        return Mathf.MoveTowards(current, target, rate);
    }

    void RefreshHeldItem()
    {
        if (_holdPoint == null)
        {
            if (playerController == null
                || !playerController.TryGetFlashlightAttachmentTargets(out _holdPoint, out _)
                || _holdPoint == null)
            {
                _heldItem = null;
                return;
            }
        }

        int childCount = _holdPoint.childCount;
        bool cachedValid = _heldItem != null && _heldItem.IsHeld && !_heldItem.IsStashed
            && _heldItem.transform.parent == _holdPoint;
        if (childCount == _lastHoldChildCount && cachedValid)
            return;

        _lastHoldChildCount = childCount;
        _heldItem = null;
        for (int i = 0; i < childCount; i++)
        {
            var g = _holdPoint.GetChild(i).GetComponent<GrabbableInventoryItem>();
            if (g != null && g.IsHeld && !g.IsStashed)
            {
                _heldItem = g;
                break;
            }
        }
    }

    /// <summary>Advances the reach envelope and returns its current 0..1 weight.</summary>
    float TickReachEnvelope()
    {
        ulong sourceId = ResolveReachSourceId();

        if (sourceId == 0UL)
            _lastConsumedReachSourceId = 0UL;

        if (sourceId != 0UL && sourceId != _reachItemId && sourceId != _lastConsumedReachSourceId)
        {
            _reachItemId = sourceId;
            _lastConsumedReachSourceId = sourceId;
            _reachTimer = 0f;
            _reachWindingOut = false;
            _reachHasPoint = false;
        }

        if (_reachItemId == 0UL)
            return 0f;

        // Resolve the live target while extending; keep the last point once the item is taken/held.
        if (!_reachWindingOut
            && GrabbableInventoryItem.TryGetRegistered(_reachItemId, out GrabbableInventoryItem item)
            && item != null && !item.IsHeld && item.gameObject.activeInHierarchy)
        {
            Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            _reachWorldPoint = item.GetInteractAimPointClosestTo(hand != null ? hand.position : transform.position);
            _reachHasPoint = true;
        }

        // The published id dropping (cancel/settle) or the item landing in *our* hand winds the reach out early.
        bool itemNowHeldByUs = _heldItem != null && _heldItem.ItemId == _reachItemId;
        if (!_reachWindingOut && (sourceId == 0UL || itemNowHeldByUs))
        {
            float currentWeight = EvaluateReachWeight(_reachTimer);
            _reachWindingOut = true;
            _reachTimer = reachInSeconds + reachHoldSeconds + (1f - currentWeight) * reachOutSeconds;
        }

        _reachTimer += Mathf.Max(Time.deltaTime, 0.0001f);
        float weight = EvaluateReachWeight(_reachTimer);

        if (_reachTimer >= reachInSeconds + reachHoldSeconds + reachOutSeconds)
        {
            _reachItemId = 0UL;
            _reachWindingOut = false;
            _reachHasPoint = false;
            return 0f;
        }

        return weight;
    }

    float EvaluateReachWeight(float t)
    {
        if (t < reachInSeconds)
            return Mathf.Clamp01(t / Mathf.Max(0.01f, reachInSeconds));
        if (t < reachInSeconds + reachHoldSeconds)
            return 1f;
        return Mathf.Clamp01(1f - (t - reachInSeconds - reachHoldSeconds) / Mathf.Max(0.01f, reachOutSeconds));
    }

    ulong ResolveReachSourceId()
    {
        bool ownerLike = networkPlayerAvatar == null || !networkPlayerAvatar.IsSpawned || networkPlayerAvatar.IsOwner;
        if (ownerLike)
            return playerController != null ? playerController.PickupReachTargetItemId : 0UL;
        return networkPlayerAvatar.ReachTargetItemId;
    }

    bool IsGated()
    {
        if (ragdollController != null
            && (ragdollController.IsRagdolled || ragdollController.IsGettingUp || ragdollController.IsHeld))
        {
            return true;
        }

        if (networkPlayerAvatar != null && networkPlayerAvatar.IsCarriedByJailor)
            return true;

        if (playerHealth != null && playerHealth.IsDead)
            return true;

        if (animator.GetBool(SeatedHash))
            return true;

        return IsPlayingUpperBodyMeleeState();
    }

    bool IsPlayingUpperBodyMeleeState()
    {
        int layer = ResolveMeleeLayerIndex();
        if (layer < 0 || layer >= animator.layerCount || string.IsNullOrEmpty(meleeStateNameOnUpperLayer))
            return false;

        if (animator.IsInTransition(layer))
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
            if (next.IsName(meleeStateNameOnUpperLayer))
                return true;
        }

        return animator.GetCurrentAnimatorStateInfo(layer).IsName(meleeStateNameOnUpperLayer);
    }

    int ResolveMeleeLayerIndex()
    {
        if (_meleeLayerIndexCache != int.MinValue)
            return _meleeLayerIndexCache;

        if (string.IsNullOrEmpty(upperBodyLayerName))
        {
            _meleeLayerIndexCache = upperBodyLayerIndexFallback;
            return _meleeLayerIndexCache;
        }

        for (int i = 0; i < animator.layerCount; i++)
        {
            if (animator.GetLayerName(i) == upperBodyLayerName)
            {
                _meleeLayerIndexCache = i;
                return i;
            }
        }

        _meleeLayerIndexCache = upperBodyLayerIndexFallback;
        return _meleeLayerIndexCache;
    }
}
