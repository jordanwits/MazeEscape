using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Stops the first-person camera from seeing through walls / floor / ceiling during ragdoll. Auto-added at
/// runtime by <see cref="PlayerController"/> when first-person look + a ragdoll controller are present.
///
/// Runs after <see cref="FirstPersonViewHeadSync"/> (600) and after <see cref="PlayerController"/> has
/// reparented <c>CameraPitch</c> onto the physics-driven Head bone. Active only during ragdoll / held /
/// get-up, owner-only. Two complementary things keep the view clean:
///   1. While active the camera near-clip plane is shrunk, so the camera can sit right against a surface
///      (e.g. the ragdoll sliding face-down on the floor) and still render it instead of clipping through it.
///      A small near plane also shrinks the clearance the containment below needs, so the view stays near the
///      head/face instead of being yanked away.
///   2. The head-driven eye is then nudged to a safe spot: a spring-arm SphereCast from a guaranteed-inside
///      hips anchor stops it crossing to the far side of a wall, and an OverlapSphere "is it buried?" test
///      lifts it up out of the floor (or pulls it toward the body) until the camera sphere is clear.
///
/// Everything is ray / spherecast / OverlapSphere based on purpose: the maze floor and walls are non-convex
/// MeshColliders, against which Physics.ComputePenetration silently returns false — but rays and overlaps work.
/// When the eye is forced close to the body, the player's own mesh is hidden (shadows-only). Sets only
/// <c>CameraPitch</c> position — never rotation.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(601)]
public class RagdollCameraCollision : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The CameraPitch transform (parent of the camera). If empty, auto-finds.")]
    [SerializeField] Transform cameraPitchTransform;
    [Tooltip("The actual camera transform. If empty, uses the PlayerController look transform / Camera.main.")]
    [SerializeField] Transform cameraTransform;
    [Tooltip("The ragdoll controller. If empty, auto-finds.")]
    [SerializeField] PlayerRagdollController ragdollController;

    [Header("Near clip")]
    [Tooltip("Shrink the camera near-clip plane during ragdoll so the camera can hug a surface without seeing through it.")]
    [SerializeField] bool reduceNearClipDuringRagdoll = true;
    [Tooltip("Near-clip plane used during ragdoll (m). Small = camera can be very close to a surface before it clips.")]
    [SerializeField] float ragdollNearClip = 0.04f;

    [Header("Collision")]
    [Tooltip("Solid geometry to keep the camera inside of. Built in Awake (excludes player/enemy/trigger layers); override only for special setups.")]
    [SerializeField] LayerMask solidLayers;
    [Tooltip("Extra margin added on top of the auto-computed near-plane radius.")]
    [SerializeField] float radiusPadding = 0.04f;
    [Tooltip("Clearance kept between the camera sphere and a wall when the spring-arm clamps.")]
    [SerializeField] float skinWidth = 0.04f;
    [Tooltip("How far the hips anchor is raised toward the chest (m).")]
    [SerializeField] float anchorRaise = 0.25f;
    [Tooltip("How far above the anchor/eye the vertical floor & ceiling probes start (m).")]
    [SerializeField] float verticalProbeRise = 0.3f;
    [Tooltip("Max distance for the floor/ceiling probes (m).")]
    [SerializeField] float maxProbeDistance = 12f;
    [Tooltip("Max steps when lifting the eye up out of the floor while buried.")]
    [SerializeField] int maxEscapeSteps = 10;
    [Tooltip("Steps used to pull the eye horizontally toward the body when it stays buried.")]
    [SerializeField] int horizontalRetreatSteps = 10;

    [Header("Own-body hiding")]
    [Tooltip("Hide the player's own mesh while the camera is forced close to the body, so you never see inside your own model.")]
    [SerializeField] bool hideOwnBodyWhenClose = true;
    [Tooltip("Distance from the camera to the nearest own collider below which the body is hidden (m).")]
    [SerializeField] float bodyHideDistance = 0.35f;

    [Header("Debug")]
    [Tooltip("Log floor-probe / containment values each ragdoll frame (toggle on at runtime to diagnose).")]
    [SerializeField] bool debugLog;

    Camera _cam;
    PlayerController _playerController;
    NetworkPlayerAvatar _avatar;
    Animator _animator;

    Collider[] _playerColliders;
    readonly Collider[] _overlaps = new Collider[16];

    float _radius;
    float _originalNearClip;
    bool _nearClipOverridden;

    // Neutral head-relative pose of CameraPitch, captured when the active window opens. We must reset to it each
    // frame before reading the intended eye, otherwise our previous-frame position override (baked into
    // localPosition because the pitch is parented to the moving head bone) would compound and drift.
    Vector3 _neutralPitchLocalPos;
    Transform _neutralParent;
    bool _isActive;

    SkinnedMeshRenderer[] _bodyRenderers;
    ShadowCastingMode[] _bodyOriginalShadowModes;
    bool _bodyHidden;

    void Awake()
    {
        if (ragdollController == null)
            ragdollController = GetComponent<PlayerRagdollController>();
        _playerController = GetComponent<PlayerController>();
        _avatar = GetComponent<NetworkPlayerAvatar>();
        _animator = GetComponentInChildren<Animator>();

        _playerColliders = GetComponentsInChildren<Collider>(true);

        if (solidLayers.value == 0)
            solidLayers = BuildSolidMask();
    }

    void OnDisable()
    {
        RestoreNearClip();
        if (_bodyHidden)
            SetBodyHidden(false);
        _isActive = false;
    }

    /// <summary>
    /// Everything solid in the world, minus the dynamic characters. We deliberately keep ALL world-geometry
    /// layers (Default and any others the maze floor/walls might use) because excluding the wrong one would make
    /// every query silently miss the floor. The player's own body is filtered separately by the transform.root
    /// check in each query, and triggers by QueryTriggerInteraction.Ignore.
    /// </summary>
    LayerMask BuildSolidMask()
    {
        int mask = ~0;
        string[] excluded = { "Player", "Enemy", "Jailor", "Clown" };
        foreach (string n in excluded)
        {
            int l = LayerMask.NameToLayer(n);
            if (l >= 0)
                mask &= ~(1 << l);
        }
        mask &= ~(1 << gameObject.layer);
        return mask;
    }

    void Start()
    {
        FindCameraReferences();
    }

    void FindCameraReferences()
    {
        // The pitch node PlayerController reparents onto the head during ragdoll — use the exact same one so our
        // neutral-pose capture aligns with it.
        if (cameraPitchTransform == null && _playerController != null)
            cameraPitchTransform = _playerController.CameraPitchNode;

        // The actual Camera under the player. Do NOT rely on Camera.main — the player camera is untagged, so
        // Camera.main returns null and the whole component would silently never run.
        if (_cam == null && cameraPitchTransform != null)
            _cam = cameraPitchTransform.GetComponentInChildren<Camera>(true);
        if (_cam == null)
            _cam = GetComponentInChildren<Camera>(true);
        if (_cam == null && Camera.main != null)
            _cam = Camera.main;

        // Eye = the camera's own transform; pitch = its parent if we still don't have one.
        if (_cam != null)
        {
            cameraTransform = _cam.transform;
            if (cameraPitchTransform == null && cameraTransform.parent != null)
                cameraPitchTransform = cameraTransform.parent;
        }
    }

    /// <summary>Owner-only: covers alive, dead-ragdoll and held cases (HasLocalControl goes false for the latter two).</summary>
    bool IsLocalView()
    {
        if (_avatar == null || !_avatar.IsSpawned)
            return true; // offline / single-player
        return _avatar.IsOwner;
    }

    void LateUpdate()
    {
        if (ragdollController == null)
            return;

        bool active = IsLocalView()
            && (ragdollController.IsRagdolled || ragdollController.IsGettingUp || ragdollController.IsHeld);

        if (!active)
        {
            RestoreNearClip();
            if (_bodyHidden)
                SetBodyHidden(false);
            _isActive = false;
            return;
        }

        if (cameraPitchTransform == null || cameraTransform == null || _cam == null)
        {
            FindCameraReferences();
            if (cameraPitchTransform == null || cameraTransform == null || _cam == null)
                return;
        }

        // Capture the clean head-relative pose once per active window so our overrides don't accumulate.
        if (!_isActive || _neutralParent != cameraPitchTransform.parent)
        {
            _neutralParent = cameraPitchTransform.parent;
            _neutralPitchLocalPos = cameraPitchTransform.localPosition;
            _isActive = true;
        }
        else
        {
            cameraPitchTransform.localPosition = _neutralPitchLocalPos;
        }

        // Shrink the near clip first so the radius below is computed from it (smaller near plane => the camera
        // can hug a surface without seeing through it, and needs far less clearance).
        if (reduceNearClipDuringRagdoll)
        {
            if (!_nearClipOverridden)
            {
                _originalNearClip = _cam.nearClipPlane;
                _nearClipOverridden = true;
            }
            if (!Mathf.Approximately(_cam.nearClipPlane, ragdollNearClip))
                _cam.nearClipPlane = ragdollNearClip;
        }

        _radius = ComputeNearPlaneRadius();

        Vector3 desired = cameraTransform.position; // head-driven eye, freshly posed this frame
        Vector3 safe = ComputeSafePosition(desired);

        // Move the pitch so the rigidly-attached camera eye lands exactly on the safe point.
        cameraPitchTransform.position += safe - desired;

        if (hideOwnBodyWhenClose)
            SetBodyHidden(NearestOwnColliderDistance(safe) < bodyHideDistance);

        if (debugLog && Time.frameCount % 15 == 0)
            Debug.Log($"[RagdollCam] near={_cam.nearClipPlane:F3} r={_radius:F3} desired.y={desired.y:F2} safe.y={safe.y:F2} dy={(safe.y - desired.y):F2} buried={IsBuried(safe)}", this);
    }

    Vector3 ComputeSafePosition(Vector3 desired)
    {
        bool hasAnchor = TryGetAnchor(out Vector3 anchor);
        Vector3 safe = desired;

        // Stage A — spring-arm: never let the eye cross to the far side of a wall between the anchor and head.
        if (hasAnchor)
        {
            Vector3 ray = safe - anchor;
            float dist = ray.magnitude;
            if (dist > 1e-4f)
            {
                Vector3 dir = ray / dist;
                if (Physics.SphereCast(anchor, _radius, dir, out RaycastHit hit, dist, solidLayers, QueryTriggerInteraction.Ignore)
                    && hit.transform.root != transform)
                {
                    safe = anchor + dir * Mathf.Max(0f, hit.distance - skinWidth);
                }
            }
        }

        // Stage B — smooth floor/ceiling clamp via rays (any hit; no normal gate so flipped mesh normals are fine).
        float probeTopY = (hasAnchor ? Mathf.Max(anchor.y, safe.y) : safe.y) + verticalProbeRise;
        ClampVertical(ref safe, probeTopY);

        // Stage C — guaranteed escape. IsBuried (OverlapSphere) works on the maze's non-convex MeshColliders,
        // so this reliably lifts the eye up out of the floor even if the Stage-B ray missed.
        if (IsBuried(safe))
        {
            float step = Mathf.Max(0.03f, _radius * 0.5f);
            for (int i = 0; i < maxEscapeSteps && IsBuried(safe); i++)
                safe.y += step;

            // Still trapped (e.g. wedged under a ceiling or in a wall): pull horizontally toward the body.
            if (hasAnchor && IsBuried(safe))
            {
                Vector3 target = new Vector3(anchor.x, safe.y, anchor.z);
                for (int i = 1; i <= horizontalRetreatSteps && IsBuried(safe); i++)
                    safe = Vector3.Lerp(safe, target, (float)i / horizontalRetreatSteps);
            }

            // Absolute last resort: sit at the anchor, deep inside the body.
            if (hasAnchor && IsBuried(safe))
                safe = anchor;
        }

        return safe;
    }

    /// <summary>Raises <paramref name="pos"/>.y above the nearest floor and lowers it below the nearest ceiling by a radius.</summary>
    void ClampVertical(ref Vector3 pos, float probeTopY)
    {
        Vector3 origin = new Vector3(pos.x, probeTopY, pos.z);

        float minY = float.NegativeInfinity;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit floor, maxProbeDistance, solidLayers, QueryTriggerInteraction.Ignore)
            && floor.transform.root != transform)
        {
            minY = floor.point.y + _radius;
        }

        float maxY = float.PositiveInfinity;
        if (Physics.Raycast(origin, Vector3.up, out RaycastHit ceil, maxProbeDistance, solidLayers, QueryTriggerInteraction.Ignore)
            && ceil.transform.root != transform)
        {
            maxY = ceil.point.y - _radius;
        }

        if (minY > maxY)
        {
            // Space tighter than two radii (very low corridor): centre between floor and ceiling.
            pos.y = 0.5f * (minY + maxY);
            return;
        }
        pos.y = Mathf.Clamp(pos.y, minY, maxY);
    }

    bool TryGetAnchor(out Vector3 anchor)
    {
        Transform hips = ragdollController != null ? ragdollController.HipsTransform : null;
        if (hips == null && _animator != null && _animator.isHuman)
            hips = _animator.GetBoneTransform(HumanBodyBones.Hips);

        if (hips == null)
        {
            anchor = Vector3.zero;
            return false;
        }

        anchor = hips.position + Vector3.up * anchorRaise;
        return true;
    }

    /// <summary>True if the camera sphere overlaps solid geometry (works on the maze's non-convex MeshColliders).</summary>
    bool IsBuried(Vector3 pos)
    {
        int count = Physics.OverlapSphereNonAlloc(pos, _radius * 0.9f, _overlaps, solidLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider col = _overlaps[i];
            if (col != null && !col.isTrigger && col.transform.root != transform)
                return true;
        }
        return false;
    }

    /// <summary>Near-plane bounding radius of the camera frustum, so the near corners never poke through a surface.</summary>
    float ComputeNearPlaneRadius()
    {
        float near = _cam.nearClipPlane;
        float h = near * Mathf.Tan(0.5f * _cam.fieldOfView * Mathf.Deg2Rad);
        float w = h * Mathf.Max(_cam.aspect, 1f);
        return Mathf.Sqrt(h * h + w * w + near * near) + radiusPadding;
    }

    float NearestOwnColliderDistance(Vector3 pos)
    {
        float best = float.MaxValue;
        if (_playerColliders == null)
            return best;

        for (int i = 0; i < _playerColliders.Length; i++)
        {
            Collider col = _playerColliders[i];
            if (col == null || !col.enabled || col.isTrigger)
                continue;

            float d = Vector3.Distance(col.ClosestPoint(pos), pos); // 0 when pos is inside the collider
            if (d < best)
                best = d;
        }
        return best;
    }

    void RestoreNearClip()
    {
        if (_nearClipOverridden && _cam != null)
            _cam.nearClipPlane = _originalNearClip;
        _nearClipOverridden = false;
    }

    void SetBodyHidden(bool hidden)
    {
        if (_bodyHidden == hidden)
            return;

        if (_bodyRenderers == null)
        {
            _bodyRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            _bodyOriginalShadowModes = new ShadowCastingMode[_bodyRenderers.Length];
            for (int i = 0; i < _bodyRenderers.Length; i++)
                _bodyOriginalShadowModes[i] = _bodyRenderers[i].shadowCastingMode;
        }

        for (int i = 0; i < _bodyRenderers.Length; i++)
        {
            if (_bodyRenderers[i] == null)
                continue;
            // ShadowsOnly keeps the body's shadow on the local view but stops drawing the mesh itself.
            _bodyRenderers[i].shadowCastingMode = hidden ? ShadowCastingMode.ShadowsOnly : _bodyOriginalShadowModes[i];
        }
        _bodyHidden = hidden;
    }
}
