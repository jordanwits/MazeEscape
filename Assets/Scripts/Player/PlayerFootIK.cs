using UnityEngine;

/// <summary>
/// Plants the feet on stairs/ramps/uneven floors via built-in humanoid foot IK: raycast under each
/// animated foot goal, snap the sole to the hit while the clip has the foot low ("height heuristic" —
/// no per-clip authoring), tilt to the slope, and lower the pelvis so the downhill foot can reach.
/// Runs on every client for every avatar (2 raycasts/frame when active). Purely visual — never moves
/// the CharacterController. Must live on the same GameObject as the Animator (IK Pass on Base Layer).
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(52)]
public class PlayerFootIK : MonoBehaviour
{
    [Header("References (auto-resolved when empty)")]
    [SerializeField] Animator animator;
    [SerializeField] PlayerRagdollController ragdollController;
    [SerializeField] NetworkPlayerAvatar networkPlayerAvatar;
    [SerializeField] PlayerHealth playerHealth;

    [Header("Ground probing")]
    [Tooltip("Layers feet plant on. When zero, auto-built in Awake: everything (this character's own colliders are filtered out per hit instead).")]
    [SerializeField] LayerMask groundMask;
    [SerializeField] float castUp = 0.45f;
    [SerializeField] float castDown = 0.85f;
    [Tooltip("Ankle-bone height above the sole when standing flat.")]
    [SerializeField] float footSoleHeight = 0.1f;
    [SerializeField] float maxStepUp = 0.35f;
    [SerializeField] float maxStepDown = 0.45f;
    [SerializeField] float maxSlopeDegrees = 40f;

    [Header("Plant weighting")]
    [Tooltip("Animated foot at/below this lift above the root plane: fully planted.")]
    [SerializeField] float footPlantMinLift = 0.04f;
    [Tooltip("Animated foot above this lift: fully animation-driven (mid-step).")]
    [SerializeField] float footPlantMaxLift = 0.18f;
    [SerializeField, Range(0f, 1f)] float rotationWeightScale = 0.7f;
    [SerializeField] float masterWeightSmoothSeconds = 0.1f;
    [SerializeField] float pelvisSmoothTime = 0.12f;

    static readonly int GroundedHash = Animator.StringToHash("Grounded");
    static readonly int SeatedHash = Animator.StringToHash("Seated");
    static readonly int IdleHash = Animator.StringToHash("Idle");
    static readonly int WalkingHash = Animator.StringToHash("Walking");
    static readonly int RunningHash = Animator.StringToHash("Running");
    static readonly int CrouchHash = Animator.StringToHash("Crouch");

    float _masterWeight;
    float _pelvisOffset;
    float _pelvisVelocity;
    Renderer _visibilityProbe;
    readonly RaycastHit[] _groundHits = new RaycastHit[16];

    void Awake()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        if (ragdollController == null)
            ragdollController = GetComponent<PlayerRagdollController>();
        if (networkPlayerAvatar == null)
            networkPlayerAvatar = GetComponent<NetworkPlayerAvatar>();
        if (playerHealth == null)
            playerHealth = GetComponent<PlayerHealth>();
        _visibilityProbe = GetComponentInChildren<SkinnedMeshRenderer>();

        // Everything, including the layers this used to exclude: a downed teammate's ragdoll (player layer),
        // an enemy body (Enemy layer) and another player's blocking proxy capsule (Ignore Raycast) are all
        // surfaces the feet must stand ON. Excluding them let the ray pass straight through and plant on the
        // floor underneath, dropping the pelvis — and with it the first-person camera — by up to maxStepDown
        // for as long as the player stood on the body. A layer mask cannot express "not me" (other players
        // share this layer), so this character's own colliders are filtered per hit in SolveFoot instead.
        if (groundMask.value == 0)
            groundMask = ~0;
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != 0 || animator == null || !animator.isHuman || !animator.enabled)
            return;

        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        bool active = !IsGated();
        _masterWeight = Mathf.MoveTowards(_masterWeight, active ? 1f : 0f, dt / Mathf.Max(0.01f, masterWeightSmoothSeconds));

        if (_masterWeight <= 0.001f)
        {
            animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0f);
            animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot, 0f);
            animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0f);
            animator.SetIKRotationWeight(AvatarIKGoal.RightFoot, 0f);
            _pelvisOffset = Mathf.SmoothDamp(_pelvisOffset, 0f, ref _pelvisVelocity, pelvisSmoothTime, Mathf.Infinity, dt);
            return;
        }

        float leftDelta = SolveFoot(AvatarIKGoal.LeftFoot);
        float rightDelta = SolveFoot(AvatarIKGoal.RightFoot);

        // Lower the pelvis so the downhill foot can reach its plant point; never raise it —
        // CharacterController stepping already lifts the capsule.
        float targetPelvis = Mathf.Clamp(Mathf.Min(leftDelta, rightDelta, 0f), -maxStepDown, 0f) * _masterWeight;
        _pelvisOffset = Mathf.SmoothDamp(_pelvisOffset, targetPelvis, ref _pelvisVelocity, pelvisSmoothTime, Mathf.Infinity, dt);
        animator.bodyPosition += Vector3.up * _pelvisOffset;
    }

    /// <summary>
    /// Plants one foot; returns the signed vertical correction (targetY - animatedY) applied at full
    /// plant weight, or 0 when the foot is mid-step/unsupported (feeds the pelvis drop).
    /// </summary>
    float SolveFoot(AvatarIKGoal goal)
    {
        Vector3 animatedPos = animator.GetIKPosition(goal);
        Quaternion animatedRot = animator.GetIKRotation(goal);

        float liftAboveRoot = animatedPos.y - transform.position.y - footSoleHeight;
        float plant = 1f - Mathf.InverseLerp(footPlantMinLift, footPlantMaxLift, liftAboveRoot);
        plant *= _masterWeight;

        if (plant <= 0.001f
            || !TryCastGroundBelow(animatedPos + Vector3.up * castUp, castUp + castDown, out RaycastHit hit))
        {
            animator.SetIKPositionWeight(goal, 0f);
            animator.SetIKRotationWeight(goal, 0f);
            return 0f;
        }

        float slope = Vector3.Angle(hit.normal, Vector3.up);
        if (slope > maxSlopeDegrees)
        {
            animator.SetIKPositionWeight(goal, 0f);
            animator.SetIKRotationWeight(goal, 0f);
            return 0f;
        }

        float targetY = hit.point.y + footSoleHeight;
        float delta = targetY - animatedPos.y;
        if (delta > maxStepUp || delta < -maxStepDown)
        {
            animator.SetIKPositionWeight(goal, 0f);
            animator.SetIKRotationWeight(goal, 0f);
            return 0f;
        }

        Vector3 targetPos = new Vector3(animatedPos.x, targetY, animatedPos.z);
        animator.SetIKPosition(goal, targetPos);
        animator.SetIKPositionWeight(goal, plant);

        Quaternion slopeTilt = Quaternion.FromToRotation(Vector3.up, hit.normal);
        animator.SetIKRotation(goal, slopeTilt * animatedRot);
        animator.SetIKRotationWeight(goal, plant * rotationWeightScale * Mathf.InverseLerp(0f, maxSlopeDegrees, slope));

        return delta * plant;
    }

    /// <summary>
    /// Nearest downward hit that is not part of this character (its own capsule, ragdoll bones, blocking
    /// proxy or held items). Allocation-free: one reused hit buffer, no sorting.
    /// </summary>
    bool TryCastGroundBelow(Vector3 origin, float distance, out RaycastHit hit)
    {
        hit = default;

        int count = Physics.RaycastNonAlloc(
            origin, Vector3.down, _groundHits, distance, groundMask, QueryTriggerInteraction.Ignore);

        bool found = false;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            Transform hitTransform = _groundHits[i].transform;
            if (hitTransform == null || hitTransform.IsChildOf(transform))
                continue;
            // A near-vertical face is never something a sole rests on: with the mask now admitting other
            // characters, the NEAREST hit under a foot can be the SIDE of a teammate's blocking capsule or an
            // enemy's controller standing beside us, which hitched the pelvis whenever players crowded. Any
            // genuine floor or step passes this; a capsule wall does not.
            if (_groundHits[i].normal.y < 0.5f)
                continue;
            if (_groundHits[i].distance >= nearest)
                continue;

            nearest = _groundHits[i].distance;
            hit = _groundHits[i];
            found = true;
        }

        return found;
    }

    bool IsGated()
    {
        if (!animator.GetBool(GroundedHash) || animator.GetBool(SeatedHash))
            return true;

        if (ragdollController != null
            && (ragdollController.IsRagdolled || ragdollController.IsGettingUp || ragdollController.IsHeld))
        {
            return true;
        }

        if (networkPlayerAvatar != null && networkPlayerAvatar.IsCarriedByJailor)
            return true;

        if (playerHealth != null && playerHealth.IsDead)
            return true;

        // Only locomotion states plant feet (never during get-ups, struggle, sit).
        int state = animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
        if (state != IdleHash && state != WalkingHash && state != RunningHash && state != CrouchHash)
            return true;

        // Off-screen avatars skip the raycasts. Bounds are accurate here because NetworkPlayerAvatar forces
        // updateWhenOffscreen on every player's skinned renderers in Awake; the local owner reads as visible
        // regardless, since in first person the camera sits inside its own body.
        if (_visibilityProbe != null && !_visibilityProbe.isVisible)
            return true;

        return false;
    }
}
