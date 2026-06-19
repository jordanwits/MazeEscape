using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Grows the carnival Clown the closer it gets to the player it is chasing and makes it hunch / tuck its
/// limbs so it stays crammed inside tight hallways. Server-authoritative: the server computes a scale
/// multiplier, a spine "hunch" amount and a lateral "squeeze" amount, replicates them as
/// <see cref="NetworkVariable{T}"/>s, and every instance applies the same visual in <see cref="LateUpdate"/>.
///
/// Design notes:
/// - Scaling the ROOT uniformly scales the CharacterController capsule too. Its bottom sits exactly at the
///   mesh soles (center.y - height/2), and <see cref="ClownAI"/>'s gravity/grounding keeps that capsule
///   bottom resting on the floor every frame, so the feet stay planted at any scale for free. The larger
///   collider also makes the big Clown physically fill / collide with the hallway walls.
/// - The hunch and arm tuck are applied as ADDITIVE local-rotation deltas on the Mixamo bones AFTER the
///   Animator has run, so locomotion still plays underneath (at amount 0 the deltas are identity).
/// - Scale is clamped so the head can always be ducked under the detected ceiling, guaranteeing nothing
///   pokes through it even mid-growth.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class ClownDynamicScale : NetworkBehaviour
{
    [Header("References (auto-found if left empty)")]
    [SerializeField] ClownAI clownAI;
    [SerializeField] CharacterController characterController;

    [Header("Bones (auto-found by Mixamo name if left empty)")]
    [SerializeField] Transform spine;   // mixamorig:Spine
    [SerializeField] Transform spine1;  // mixamorig:Spine1
    [SerializeField] Transform spine2;  // mixamorig:Spine2
    [SerializeField] Transform neck;    // mixamorig:Neck
    [SerializeField] Transform head;    // mixamorig:Head
    [SerializeField] Transform leftShoulder;
    [SerializeField] Transform rightShoulder;
    [SerializeField] Transform leftArm;
    [SerializeField] Transform rightArm;

    [Header("Scale vs. distance")]
    [SerializeField, Min(0.1f)] float baseScale = 1f;
    [Tooltip("Scale when the chased player is at (or inside) nearDistance.")]
    [SerializeField, Min(0.1f)] float maxScale = 2.5f;
    [Tooltip("At/inside this distance (m) to the chased player, the Clown is at maxScale.")]
    [SerializeField, Min(0f)] float nearDistance = 1.5f;
    [Tooltip("At/beyond this distance (m), the Clown is at baseScale. Also the cutoff for any growth.")]
    [SerializeField, Min(0.5f)] float farDistance = 10f;
    [SerializeField, Min(0.01f)] float scaleSmoothTime = 0.3f;
    [SerializeField, Min(0.01f)] float bendSmoothTime = 0.18f;

    [Header("Collision")]
    [Tooltip("Cap the CharacterController's EFFECTIVE world radius so the scaled-up body navigates corners "
        + "like the slim base agent. The visual mesh still scales fully; the lateral squeeze handles visual "
        + "width. (Height/feet are unaffected — only the horizontal radius is countered.)")]
    [SerializeField] bool capCollisionRadius = true;
    [Tooltip("Max effective (world) collision radius regardless of scale. The base CharacterController radius is ~0.5.")]
    [SerializeField, Min(0.1f)] float maxCollisionRadius = 0.6f;
    [Tooltip("Cap the EFFECTIVE collision capsule HEIGHT so the full-height physics capsule never rams the "
        + "ceiling as the Clown scales up. The hunch only ducks the VISUAL bones — the physics capsule stays "
        + "full height (~6.7m at 2.8x) — so without this the giant's capsule pokes up THROUGH a normal ceiling "
        + "and CharacterController depenetration cancels its forward motion (it 'runs in place' and can't close "
        + "on the player). The visual mesh still scales fully; only the physics capsule is shortened, and only "
        + "from the TOP so the feet stay planted. Caps only when a ceiling is actually close — full height in "
        + "the open.")]
    [SerializeField] bool capCollisionHeight = true;

    [Header("Ceiling fit")]
    [Tooltip("Layers treated as solid ceiling/walls. Defaults to 'Default' if left as Nothing.")]
    [SerializeField] LayerMask environmentMask;
    [Tooltip("Keep the head this far (m) below the detected ceiling.")]
    [SerializeField, Min(0f)] float headClearanceMargin = 0.15f;
    [Tooltip("Max head drop (m, at scale 1; scales with size) the hunch can absorb. Used to clamp scale so a fit is always possible.")]
    [SerializeField, Min(0.05f)] float maxHunchDrop = 0.9f;
    [SerializeField, Min(1)] int ceilingRayCount = 5;
    [Tooltip("Extra metres scanned above the tallest possible head.")]
    [SerializeField, Min(0.5f)] float ceilingScanExtra = 2f;

    [Header("Hunch pose (degrees at full bend, additive)")]
    [SerializeField] bool enableHunch = true;
    [Tooltip("Local axis each spine/neck/head bone pitches about. Sign is calibrated so the Clown bends FORWARD.")]
    [SerializeField] Vector3 bendAxis = new Vector3(1f, 0f, 0f);
    [SerializeField] float bendSpineDeg = 22f;
    [SerializeField] float bendSpine1Deg = 22f;
    [SerializeField] float bendSpine2Deg = 18f;
    [SerializeField] float bendNeckDeg = 16f;
    [SerializeField] float bendHeadDeg = 10f;
    [Tooltip("Shapes how eagerly the hunch responds. The spine's bend->drop curve is convex (slow to start), "
        + "so an exponent <1 ducks sooner and guarantees the head clears the ceiling instead of skimming it.")]
    [SerializeField, Range(0.3f, 1f)] float bendResponse = 0.6f;

    [Header("Lateral squeeze (degrees at full squeeze, additive)")]
    [SerializeField] bool enableSqueeze = true;
    [Tooltip("Local axis the shoulders/arms tuck about (mirrored for the right side).")]
    [SerializeField] Vector3 tuckAxis = new Vector3(0f, 1f, 0f);
    [SerializeField] float shoulderTuckDeg = 18f;
    [SerializeField] float armTuckDeg = 35f;
    [Tooltip("Extra clearance (m) beyond the scaled body radius before tucking starts.")]
    [SerializeField, Min(0f)] float sideMargin = 0.1f;

    [Header("Head look-at")]
    [SerializeField] bool enableHeadLook = true;
    [Tooltip("Look at the nearest player within this range when not actively chasing one.")]
    [SerializeField, Min(0f)] float headLookRange = 22f;
    [Tooltip("Max angle (deg) the head/neck may swing toward the player so it never breaks the neck.")]
    [SerializeField, Range(0f, 90f)] float maxHeadLookAngle = 72f;
    [Tooltip("Fraction of the turn taken by the neck before the head finishes the aim (0 = head only).")]
    [SerializeField, Range(0f, 1f)] float neckLookShare = 0.3f;
    [SerializeField, Min(1f)] float headLookLerpSpeed = 10f;
    [Tooltip("Aim at the player's position plus this height (m) so the Clown looks at their head, not feet.")]
    [SerializeField] float headLookEyeHeight = 1.5f;
    [Tooltip("Head-bone-local axis that points out of the face. Calibrated in play mode.")]
    [SerializeField] Vector3 headFaceAxisLocal = new Vector3(0f, 0f, 1f);

    [Header("Client smoothing")]
    [SerializeField, Min(1f)] float clientLerpSpeed = 14f;

    readonly NetworkVariable<float> _netScale = new(
        1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> _netBend = new(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> _netSqueeze = new(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // NetworkObjectId of the player the head is fixated on (ulong.MaxValue = none). Clients resolve the
    // live transform locally and aim the head themselves, so we never stream a per-frame rotation.
    readonly NetworkVariable<ulong> _netLookTargetId = new(
        ulong.MaxValue, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    const float NetEpsilon = 0.004f;
    const ulong NoLookTarget = ulong.MaxValue;

    float _scale;
    float _bend;
    float _squeeze;
    float _scaleVel;
    float _bendVel;
    float _squeezeVel;

    float _ccHeight = 2.4893f;   // upright head height above floor (per unit scale)
    float _ccRadius = 0.5f;
    float _feetLocalY = -0.2193f; // capsule bottom = mesh soles, in root-local units at scale 1
    bool _bonesReady;

    Transform _lookTarget;
    float _lookBlend;

    void Awake()
    {
        if (clownAI == null)
            clownAI = GetComponent<ClownAI>();
        if (characterController == null)
            characterController = GetComponent<CharacterController>();

        if (environmentMask == 0)
            environmentMask = DefaultEnvironmentMask();

        CacheCapsuleMetrics();
        EnsureBones();

        _scale = baseScale;
        _bend = 0f;
        _squeeze = 0f;
        transform.localScale = Vector3.one * _scale;
    }

    /// <summary>
    /// Everything solid the body should fit inside, i.e. all layers EXCEPT the dynamic actors
    /// (Clown/Player/Enemy/Jailor). The maze ceiling/walls are spawned procedurally at runtime, so we cannot
    /// know their exact layer at edit time — this catches them whatever layer they end up on.
    /// </summary>
    static int DefaultEnvironmentMask()
    {
        int mask = ~0;
        foreach (string actorLayer in new[] { "Clown", "Player", "Enemy", "Jailor" })
        {
            int layer = LayerMask.NameToLayer(actorLayer);
            if (layer >= 0)
                mask &= ~(1 << layer);
        }
        return mask;
    }

    void CacheCapsuleMetrics()
    {
        if (characterController == null)
            return;
        _ccHeight = Mathf.Max(0.1f, characterController.height);
        _ccRadius = Mathf.Max(0.05f, characterController.radius);
        _feetLocalY = characterController.center.y - characterController.height * 0.5f;
    }

    /// <summary>
    /// Counter-scales the CharacterController's local radius so its EFFECTIVE world radius
    /// (localRadius * uniform scale) is capped — keeping the physical footprint near the slim base agent the
    /// NavMesh path was baked for, even at full size. Height and the capsule bottom (feet) are untouched, so
    /// foot planting is preserved. <see cref="_ccRadius"/> stays the base value and remains the visual-width
    /// reference for the squeeze/ceiling checks.
    /// </summary>
    void ApplyCollisionRadius()
    {
        if (characterController == null)
            return;

        float localRadius = capCollisionRadius
            ? Mathf.Min(_ccRadius, maxCollisionRadius / Mathf.Max(0.01f, _scale))
            : _ccRadius;
        characterController.radius = Mathf.Max(0.05f, localRadius);
    }

    /// <summary>
    /// Shortens the CharacterController capsule from the TOP so it never pokes through a nearby ceiling once
    /// the Clown is scaled up. The visual mesh scales fully and the hunch ducks the visual head, but the
    /// physics capsule (height = ccHeight * scale, ~6.7m at 2.8x) is NOT ducked — left full it jams into the
    /// ceiling and CharacterController.Move's depenetration eats the Clown's horizontal motion, so the giant
    /// "runs in place" several metres short of the player. We cast up to the ceiling and clamp the capsule's
    /// world height to that clearance (minus the head margin), keeping the bottom pinned at the feet so foot
    /// planting and the capped radius are untouched. With no ceiling overhead the capsule stays full height.
    /// </summary>
    void ApplyCollisionHeight()
    {
        if (characterController == null || !capCollisionHeight)
            return;

        float scale = Mathf.Max(0.01f, _scale);
        float floorY = transform.position.y + _feetLocalY * scale;
        float fullWorldHeight = _ccHeight * scale;
        float targetWorldHeight = fullWorldHeight;

        // Nearest ceiling above the body; keep the capsule top a margin below it.
        Vector3 rayOrigin = new Vector3(transform.position.x, floorY + 0.2f, transform.position.z);
        float scan = fullWorldHeight + ceilingScanExtra;
        if (Physics.Raycast(rayOrigin, Vector3.up, out RaycastHit hit, scan, environmentMask, QueryTriggerInteraction.Ignore))
        {
            float clearance = (hit.point.y - floorY) - headClearanceMargin;
            targetWorldHeight = Mathf.Min(targetWorldHeight, clearance);
        }

        // Never degenerate (must still comfortably enclose the capped radius) and never taller than natural.
        float minWorldHeight = characterController.radius * scale * 2.2f;
        targetWorldHeight = Mathf.Clamp(targetWorldHeight, Mathf.Max(0.3f, minWorldHeight), fullWorldHeight);

        float localHeight = Mathf.Min(_ccHeight, targetWorldHeight / scale);
        characterController.height = localHeight;
        Vector3 c = characterController.center;
        characterController.center = new Vector3(c.x, _feetLocalY + localHeight * 0.5f, c.z);
    }

    void EnsureBones()
    {
        if (_bonesReady)
            return;

        Transform hips = transform.Find("mixamorig:Hips");
        if (hips == null)
        {
            // Fall back to a deep search in case the armature is nested differently.
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "mixamorig:Hips")
                {
                    hips = t;
                    break;
                }
            }
        }
        if (hips == null)
        {
            Debug.LogWarning($"[{nameof(ClownDynamicScale)}] Could not find 'mixamorig:Hips'. Disabling.", this);
            enabled = false;
            return;
        }

        if (spine == null) spine = hips.Find("mixamorig:Spine");
        if (spine1 == null && spine != null) spine1 = spine.Find("mixamorig:Spine1");
        if (spine2 == null && spine1 != null) spine2 = spine1.Find("mixamorig:Spine2");
        if (neck == null && spine2 != null) neck = spine2.Find("mixamorig:Neck");
        if (head == null && neck != null) head = neck.Find("mixamorig:Head");
        if (leftShoulder == null && spine2 != null) leftShoulder = spine2.Find("mixamorig:LeftShoulder");
        if (rightShoulder == null && spine2 != null) rightShoulder = spine2.Find("mixamorig:RightShoulder");
        if (leftArm == null && leftShoulder != null) leftArm = leftShoulder.Find("mixamorig:LeftArm");
        if (rightArm == null && rightShoulder != null) rightArm = rightShoulder.Find("mixamorig:RightArm");

        _bonesReady = true;
    }

    bool HasAuthority()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return true;
        if (!IsSpawned)
            return true;
        return IsServer;
    }

    void Update()
    {
        EnsureBones();

        if (HasAuthority())
        {
            ComputeAuthorityTargets(out float targetScale, out float targetBend, out float targetSqueeze);

            _scale = Mathf.SmoothDamp(_scale, targetScale, ref _scaleVel, scaleSmoothTime);
            _bend = Mathf.SmoothDamp(_bend, targetBend, ref _bendVel, bendSmoothTime);
            _squeeze = Mathf.SmoothDamp(_squeeze, targetSqueeze, ref _squeezeVel, bendSmoothTime);

            UpdateAuthorityLookTarget();

            if (IsSpawned && IsServer)
                PushNetworkVars();
        }
        else
        {
            float k = 1f - Mathf.Exp(-clientLerpSpeed * Time.deltaTime);
            _scale = Mathf.Lerp(_scale, _netScale.Value, k);
            _bend = Mathf.Lerp(_bend, _netBend.Value, k);
            _squeeze = Mathf.Lerp(_squeeze, _netSqueeze.Value, k);

            _lookTarget = ResolveNetworkedLookTarget(_netLookTargetId.Value);
        }
    }

    /// <summary>Authority side: pick the player to look at and stash its NetworkObjectId for clients.</summary>
    void UpdateAuthorityLookTarget()
    {
        if (!enableHeadLook)
        {
            _lookTarget = null;
            return;
        }

        _lookTarget = clownAI != null ? clownAI.GetLookAtPlayer(headLookRange) : null;

        ulong id = NoLookTarget;
        if (_lookTarget != null)
        {
            NetworkObject no = _lookTarget.GetComponentInParent<NetworkObject>();
            if (no != null && no.IsSpawned)
                id = no.NetworkObjectId;
        }

        if (IsSpawned && IsServer && _netLookTargetId.Value != id)
            _netLookTargetId.Value = id;
    }

    Transform ResolveNetworkedLookTarget(ulong id)
    {
        if (id == NoLookTarget)
            return null;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.SpawnManager != null
            && nm.SpawnManager.SpawnedObjects.TryGetValue(id, out NetworkObject no) && no != null)
            return no.transform;

        return null;
    }

    void ComputeAuthorityTargets(out float targetScale, out float targetBend, out float targetSqueeze)
    {
        targetScale = baseScale;
        targetBend = 0f;
        targetSqueeze = 0f;

        // 1. Distance-driven raw scale (only while actively chasing a player).
        if (clownAI != null && clownAI.TryGetChaseTargetPosition(out Vector3 targetPos))
        {
            float d = Vector3.Distance(transform.position, targetPos);
            float t = Mathf.InverseLerp(farDistance, nearDistance, d); // d<=near -> 1, d>=far -> 0
            t = Mathf.SmoothStep(0f, 1f, t);
            targetScale = Mathf.Lerp(baseScale, maxScale, t);
        }

        float floorY = transform.position.y + _feetLocalY * _scale;

        // 2. Ceiling clamp + hunch.
        if (enableHunch && TryDetectCeilingAboveFloor(floorY, out float ceilingAboveFloor))
        {
            float allowed = Mathf.Max(0.05f, ceilingAboveFloor - headClearanceMargin);
            float minHeadFactor = Mathf.Max(0.01f, _ccHeight - maxHunchDrop); // lowest head height per unit scale

            float maxFitScale = allowed / minHeadFactor;
            targetScale = Mathf.Min(targetScale, maxFitScale);
            targetScale = Mathf.Clamp(targetScale, baseScale, maxScale);

            float uprightHead = _ccHeight * targetScale;
            float bendCapacity = maxHunchDrop * targetScale;
            float needLower = uprightHead - allowed;
            float linearBend = bendCapacity > 0.0001f ? Mathf.Clamp01(needLower / bendCapacity) : 0f;
            // Counter the spine's convex bend->drop curve so even a small required duck lowers the head enough.
            targetBend = bendResponse < 0.999f ? Mathf.Pow(linearBend, bendResponse) : linearBend;
        }
        else
        {
            targetScale = Mathf.Clamp(targetScale, baseScale, maxScale);
        }

        // 3. Lateral squeeze (arm/shoulder tuck) from side clearance.
        if (enableSqueeze)
            targetSqueeze = ComputeLateralSqueeze(floorY, targetScale);
    }

    bool TryDetectCeilingAboveFloor(float floorY, out float ceilingAboveFloor)
    {
        ceilingAboveFloor = float.MaxValue;
        bool found = false;

        float originY = floorY + _ccHeight * 0.5f * _scale; // mid-body
        float scan = _ccHeight * maxScale + ceilingScanExtra;
        float ringR = _ccRadius * _scale * 0.6f;

        int rays = Mathf.Max(1, ceilingRayCount);
        for (int i = 0; i < rays; i++)
        {
            Vector3 origin;
            if (i == 0)
            {
                origin = new Vector3(transform.position.x, originY, transform.position.z);
            }
            else
            {
                float ang = (i - 1) / (float)Mathf.Max(1, rays - 1) * Mathf.PI * 2f;
                origin = new Vector3(
                    transform.position.x + Mathf.Cos(ang) * ringR,
                    originY,
                    transform.position.z + Mathf.Sin(ang) * ringR);
            }

            if (Physics.Raycast(origin, Vector3.up, out RaycastHit hit, scan, environmentMask, QueryTriggerInteraction.Ignore))
            {
                float aboveFloor = hit.point.y - floorY;
                if (aboveFloor < ceilingAboveFloor)
                    ceilingAboveFloor = aboveFloor;
                found = true;
            }
        }

        return found;
    }

    float ComputeLateralSqueeze(float floorY, float scale)
    {
        float halfWidth = _ccRadius * scale;
        float maxDist = halfWidth + sideMargin;
        Vector3 chest = new Vector3(
            transform.position.x,
            floorY + _ccHeight * 0.55f * scale,
            transform.position.z);

        float worst = 0f;
        Vector3 right = transform.right;
        worst = Mathf.Max(worst, SideSqueeze(chest, right, maxDist, halfWidth));
        worst = Mathf.Max(worst, SideSqueeze(chest, -right, maxDist, halfWidth));
        return Mathf.Clamp01(worst);
    }

    float SideSqueeze(Vector3 origin, Vector3 dir, float maxDist, float halfWidth)
    {
        if (Physics.Raycast(origin, dir, out RaycastHit hit, maxDist, environmentMask, QueryTriggerInteraction.Ignore))
        {
            if (halfWidth > 0.001f)
                return Mathf.Clamp01((halfWidth - hit.distance) / halfWidth);
        }
        return 0f;
    }

    void PushNetworkVars()
    {
        if (Mathf.Abs(_netScale.Value - _scale) > NetEpsilon)
            _netScale.Value = _scale;
        if (Mathf.Abs(_netBend.Value - _bend) > NetEpsilon)
            _netBend.Value = _bend;
        if (Mathf.Abs(_netSqueeze.Value - _squeeze) > NetEpsilon)
            _netSqueeze.Value = _squeeze;
    }

    void LateUpdate()
    {
        // Scale is independent of the Animator; bone deltas must layer on top of the freshly animated pose.
        transform.localScale = Vector3.one * _scale;
        ApplyCollisionRadius();
        ApplyCollisionHeight();

        if (enableHunch && _bend > 0.0001f)
        {
            ApplyBoneBend(spine, bendSpineDeg);
            ApplyBoneBend(spine1, bendSpine1Deg);
            ApplyBoneBend(spine2, bendSpine2Deg);
            ApplyBoneBend(neck, bendNeckDeg);
            ApplyBoneBend(head, bendHeadDeg);
        }

        if (enableSqueeze && _squeeze > 0.0001f)
        {
            ApplyBoneTuck(leftShoulder, shoulderTuckDeg, 1f);
            ApplyBoneTuck(rightShoulder, shoulderTuckDeg, -1f);
            ApplyBoneTuck(leftArm, armTuckDeg, 1f);
            ApplyBoneTuck(rightArm, armTuckDeg, -1f);
        }

        // Head look-at runs LAST so it aims from the final hunched pose.
        UpdateHeadLook();
    }

    void UpdateHeadLook()
    {
        if (!enableHeadLook || head == null)
            return;

        float targetBlend = _lookTarget != null ? 1f : 0f;
        float k = 1f - Mathf.Exp(-headLookLerpSpeed * Time.deltaTime);
        _lookBlend = Mathf.Lerp(_lookBlend, targetBlend, k);
        if (_lookBlend < 0.001f || _lookTarget == null)
            return;

        Vector3 targetPoint = _lookTarget.position + Vector3.up * headLookEyeHeight;

        // Distribute the turn: the neck takes a share first, then the head finishes the aim.
        if (neck != null && neckLookShare > 0.001f)
        {
            Quaternion neckAim = AimDelta(neck, targetPoint, maxHeadLookAngle);
            neck.rotation = Quaternion.Slerp(Quaternion.identity, neckAim, neckLookShare * _lookBlend) * neck.rotation;
        }

        Quaternion headAim = AimDelta(head, targetPoint, maxHeadLookAngle);
        head.rotation = Quaternion.Slerp(Quaternion.identity, headAim, _lookBlend) * head.rotation;
    }

    /// <summary>
    /// World-space rotation that turns <paramref name="bone"/>'s face (its <see cref="headFaceAxisLocal"/>
    /// direction) toward <paramref name="targetPoint"/>, clamped to <paramref name="maxDeg"/>.
    /// </summary>
    Quaternion AimDelta(Transform bone, Vector3 targetPoint, float maxDeg)
    {
        Vector3 worldFace = bone.rotation * headFaceAxisLocal;
        Vector3 desired = targetPoint - bone.position;
        if (worldFace.sqrMagnitude < 1e-6f || desired.sqrMagnitude < 1e-4f)
            return Quaternion.identity;

        Quaternion delta = Quaternion.FromToRotation(worldFace.normalized, desired.normalized);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (axis.sqrMagnitude < 1e-6f || float.IsNaN(axis.x) || float.IsInfinity(axis.x))
            return Quaternion.identity;
        if (angle > 180f)
            angle -= 360f;
        angle = Mathf.Clamp(angle, -maxDeg, maxDeg);
        return Quaternion.AngleAxis(angle, axis);
    }

    void ApplyBoneBend(Transform bone, float degrees)
    {
        if (bone == null || Mathf.Abs(degrees) < 0.001f)
            return;
        bone.localRotation = bone.localRotation * Quaternion.AngleAxis(degrees * _bend, bendAxis);
    }

    void ApplyBoneTuck(Transform bone, float degrees, float sideSign)
    {
        if (bone == null || Mathf.Abs(degrees) < 0.001f)
            return;
        bone.localRotation = bone.localRotation * Quaternion.AngleAxis(degrees * _squeeze * sideSign, tuckAxis);
    }

#if UNITY_EDITOR
    void Reset()
    {
        clownAI = GetComponent<ClownAI>();
        characterController = GetComponent<CharacterController>();
        if (environmentMask == 0)
            environmentMask = DefaultEnvironmentMask();
    }
#endif
}
