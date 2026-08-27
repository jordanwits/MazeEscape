using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Runtime render-distance culler for static world geometry (maze walls/floors/props,
/// carnival booths, decorations). Previously every level rendered its entire geometry at
/// all times; in a maze you only ever see a handful of cells at once, so the rest is wasted
/// draw calls. This component buckets all static <see cref="MeshRenderer"/>s into a coarse
/// spatial grid and toggles <c>Renderer.enabled</c> per bucket, so geometry the player can't
/// see stops drawing (and stops casting shadows) until it comes into view.
///
/// Visibility is a <b>view cone</b>, not a plain radius: a bucket renders only when it is
/// within the cull distance AND inside the camera's field of view (with a margin), so
/// geometry behind and to the sides of the player is culled even when it's close by. A short
/// <see cref="nearRadius"/> around the camera stays visible regardless of facing so walls
/// right beside/behind you don't pop as you turn on the spot. The cone is sized to fully
/// contain the camera frustum (out to its corners) before the margin is added, so nothing
/// actually on screen is culled.
///
/// Companion to <see cref="MazeLightCuller"/> (which culls lights by distance — lights stay
/// radius-based because a light just off-screen still illuminates surfaces that ARE on screen).
/// Like that one it is:
///   * Non-destructive — only flips the runtime enabled flag, never edits meshes/materials
///     or the scene/prefab assets. Geometry re-appears automatically as it comes into view.
///   * Self-discovering — rescans on an interval so it works with the procedural maze that
///     spawns its cells in at runtime.
///   * Client-side visual only — toggling a renderer has no effect on Netcode replication.
///
/// It only ever looks at <see cref="MeshRenderer"/>s, so character bodies (SkinnedMeshRenderers)
/// are never touched. Static networked geometry IS culled — the carnival minigame booths
/// (blackjack table, ring toss, basketball, etc.) are scene-placed NetworkObjects with no
/// moving parts, so they cull with the rest of the world.
///
/// Excluded automatically:
///   * Anything under a <see cref="PlayerController"/> (the local rig / held items).
///   * Anything under a <see cref="Rigidbody"/> — moving physics props (thrown balls, bottles,
///     rings, ragdolls) travel away from where they were bucketed, so culling them by bucket
///     would flicker them on/off.
///   * Character rigs that roam — detected as a <see cref="NetworkObject"/> whose hierarchy
///     contains a <see cref="SkinnedMeshRenderer"/> (players + enemies), which also covers
///     mesh props parented into their hands (e.g. the Clown's hammer). Static booths have no
///     skinned mesh, so this does NOT exclude them.
///   * Anything carrying a <see cref="WorldRenderCullIgnore"/> marker.
/// </summary>
// Run the visibility pass AFTER every script that moves the view camera this frame, so we cull
// against the camera's FINAL pose rather than last frame's. The FP view is driven late in LateUpdate:
// RagdollCameraDamper (550) -> FirstPersonViewHeadSync (600) -> RagdollCameraCollision (601). If we
// evaluated at the default order (0) we'd read the previous frame's pose, and a fast pose change —
// most visibly being whipped around a corner in the Jailor's grip — would outrun the cull by a frame
// and briefly reveal the skybox. Ordering past 601 closes that one-frame lag.
[DefaultExecutionOrder(700)]
[DisallowMultipleComponent]
public class WorldRenderCuller : MonoBehaviour
{
    [Header("View cone")]
    [Tooltip("Length of the view cone in metres. Buckets beyond this distance switch off. The bucket's own radius is added on top, so large props still cull at a sane edge.")]
    [SerializeField] float cullDistance = 60f;

    [Tooltip("Extra degrees added on top of the camera's field of view when building the cull cone. The cone is first sized to fully contain the camera frustum (to its corners), THEN this margin is added. Larger = geometry at the screen edge fades in earlier and survives faster turns between updates (less edge pop-in), but more stays drawn.")]
    [SerializeField] float coneMarginDegrees = 15f;

    [Tooltip("Geometry within this radius of the camera stays visible regardless of facing, so walls right beside/behind you don't pop as you spin on the spot. Keep it around 1-2 maze cells.")]
    [SerializeField] float nearRadius = 10f;

    [Tooltip("Extra metres a bucket must move BEYOND its on-distance, and extra degrees it must swing BEYOND the cone edge, before it switches back off. Prevents on/off flicker for geometry hovering right at the cull edge.")]
    [SerializeField] float hysteresis = 4f;

    [Tooltip("Extra degrees of angular hysteresis at the cone edge (paired with the metres of distance hysteresis above).")]
    [SerializeField] float coneEdgeHysteresisDegrees = 5f;

    [Header("Bucketing")]
    [Tooltip("Edge length (metres) of each spatial bucket. Renderers are grouped by which bucket their centre falls in and toggled together. ~2 maze cells is a good default: small enough to cull tightly, large enough that the per-bucket loop stays cheap.")]
    [SerializeField] float bucketSize = 12f;

    [Header("Timing")]
    [Tooltip("Seconds between visibility evaluations while the view is steady. A fast turn or dash forces an immediate pass regardless (see the re-evaluate triggers below), so this only governs the idle/walking cadence.")]
    [SerializeField] float updateInterval = 0.15f;

    [Tooltip("If the camera rotates more than this many degrees since the last visibility pass, re-evaluate immediately instead of waiting for the update interval. This is what stops a fast spin from briefly revealing the skybox — during a quick turn it fires every frame, so geometry is enabled the same frame it swings into view. Keep it well below the cone margin.")]
    [SerializeField] float reevaluateOnTurnDegrees = 5f;

    [Tooltip("If the camera moves more than this many metres since the last visibility pass, re-evaluate immediately (covers sprinting/teleporting toward the far cull edge).")]
    [SerializeField] float reevaluateOnMoveMetres = 3f;

    [Tooltip("Seconds between rescans that pick up newly spawned maze geometry. Set to 0 to scan once and never again (use for fully authored static scenes).")]
    [SerializeField] float rescanInterval = 4f;

    // One entry per spatial bucket.
    sealed class Bucket
    {
        public readonly List<Renderer> Renderers = new();
        public Vector3 Center;
        public float Radius;     // reach from Center to the furthest managed geometry.
        public float OnSqr;      // (cullDistance + radius)^2                — inside this, distance passes.
        public float OffSqr;     // (cullDistance + radius + hysteresis)^2   — past this, distance fails.
        public float NearOnSqr;  // (nearRadius + radius)^2                  — inside this, facing is ignored.
        public float NearOffSqr; // (nearRadius + radius + hysteresis)^2     — near-field with hysteresis.
        public bool On = true;
    }

    /// <summary>View-cone length in metres — geometry beyond this (plus its bucket radius) stops drawing.
    /// Exposed so <see cref="MazeDistanceFog"/> can end its fog just before this edge.</summary>
    public float CullDistance => cullDistance;

    readonly List<Bucket> _buckets = new();
    readonly Dictionary<Vector3Int, Bucket> _bucketByCoord = new();

    Camera _viewCamera;
    float _nextUpdate;
    float _nextRescan;
    int _lastRendererCount = -1;

    // Frame index through which the throttle is bypassed (a visibility pass runs every frame).
    // Set by RequestContinuousEvaluation while the local view is being moved by something other than
    // the player's own look input — being carried/ragdolled by the Jailor. In that state the camera
    // rides a smoothed proxy whose per-frame rotation/translation can sit just under the turn/move
    // re-evaluate thresholds, so the pass would otherwise fall back to the idle updateInterval cadence
    // and briefly reveal the skybox as geometry swings into view between ticks. The owner pulses this
    // each frame it is disrupted; a single-frame horizon means it self-expires the moment it stops.
    static int _forceEvalThroughFrame = -1;

    /// <summary>
    /// Ask the culler to skip its update-interval throttle and run a visibility pass this frame and the
    /// next. Call every frame while the local viewpoint is being driven involuntarily (Jailor carry,
    /// ragdoll) so geometry is enabled the same frame it comes into view instead of on the next tick.
    /// Cheap and idempotent; static so callers don't need a reference to the instance.
    /// </summary>
    public static void RequestContinuousEvaluation()
    {
        // +1 so a pulse set in an earlier LateUpdate (PlayerController, order 100) still counts when the
        // culler reads it later this frame (order 700), and also covers the immediately following frame.
        _forceEvalThroughFrame = Time.frameCount + 1;
    }

    // View state at the last visibility pass, so a fast turn/dash can force an early re-evaluation.
    Vector3 _lastEvalForward;
    Vector3 _lastEvalPosition;
    bool _hasEvaluated;

    void OnEnable()
    {
        _nextUpdate = 0f;
        _nextRescan = 0f;
        _hasEvaluated = false;
        Rescan();
    }

    void LateUpdate()
    {
        float now = Time.unscaledTime;

        if (rescanInterval > 0f && now >= _nextRescan)
        {
            _nextRescan = now + Mathf.Max(0.5f, rescanInterval);
            // Only pay the rebuild cost when the world actually changed (maze rebuilt, props
            // spawned/despawned). FindObjectsByType is the expensive bit, but it lets us skip
            // re-bucketing a stable scene every interval.
            int count = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
            if (count != _lastRendererCount)
            {
                Rescan();
                // Rescan re-enables every managed renderer and marks every bucket On, so the level is fully
                // drawn right now. Force the visibility pass to run this same frame — OnEnable already does
                // this before its own Rescan. Without it a stationary player (neither throttle nor the
                // turn/move triggers due) presents the whole maze, shadow casters included, for up to
                // updateInterval, which reads as a periodic hitch whenever the renderer count shifts.
                _nextUpdate = 0f;
            }
        }

        Camera cam = ResolveViewpoint();
        if (cam == null)
            return; // no local camera yet (or headless server) — leave geometry as authored.

        Vector3 eye = cam.transform.position;
        Vector3 forward = cam.transform.forward;

        // Decide whether to run the (cheap) visibility pass this frame. It's normally throttled to
        // updateInterval, but a fast turn or dash must re-evaluate immediately — otherwise geometry
        // that swings into view stays disabled until the next tick and the player sees the skybox.
        // During a quick turn the rotation test trips every frame, so what you're looking at is
        // enabled the same frame it becomes visible (LateUpdate runs before rendering).
        bool due = now >= _nextUpdate || Time.frameCount <= _forceEvalThroughFrame;
        if (!due && _hasEvaluated)
        {
            float turnCos = Mathf.Cos(Mathf.Max(0f, reevaluateOnTurnDegrees) * Mathf.Deg2Rad);
            float moveSqr = reevaluateOnMoveMetres * reevaluateOnMoveMetres;
            if (Vector3.Dot(forward, _lastEvalForward) < turnCos ||
                (eye - _lastEvalPosition).sqrMagnitude > moveSqr)
                due = true;
        }
        if (!due)
            return;

        _nextUpdate = now + Mathf.Max(0.02f, updateInterval);
        _lastEvalForward = forward;
        _lastEvalPosition = eye;
        _hasEvaluated = true;

        // Half-angle of the cull cone, sized to fully contain the camera frustum plus the margin.
        float coneHalf = ComputeConeHalfAngle(cam);
        float angleHysteresis = Mathf.Max(0f, coneEdgeHysteresisDegrees) * Mathf.Deg2Rad;

        for (int i = 0; i < _buckets.Count; i++)
        {
            Bucket b = _buckets[i];
            Vector3 toBucket = b.Center - eye;
            float distSqr = toBucket.sqrMagnitude;

            bool shouldBeOn;

            // 1) Distance gate — past the cone's length, nothing else matters. Hysteresis: use the
            //    looser OffSqr while already on, the tighter OnSqr while off.
            float maxDistSqr = b.On ? b.OffSqr : b.OnSqr;
            if (distSqr > maxDistSqr)
            {
                shouldBeOn = false;
            }
            // 2) Near field — close enough to stay visible no matter which way we're facing, so
            //    turning on the spot never pops the walls immediately around the player.
            else if (distSqr <= (b.On ? b.NearOffSqr : b.NearOnSqr))
            {
                shouldBeOn = true;
            }
            // 3) Cone test — is the bucket inside the field of view? Widen the cone by the bucket's
            //    own angular size (so a bucket only partly on screen still renders) plus the angular
            //    hysteresis while already on.
            else
            {
                float dist = Mathf.Sqrt(distSqr);
                Vector3 dir = toBucket / dist;
                float cosAngle = Vector3.Dot(dir, forward);

                float angularRadius = Mathf.Asin(Mathf.Clamp01(b.Radius / dist));
                float allowed = coneHalf + angularRadius + (b.On ? angleHysteresis : 0f);

                // allowed >= 180° means the whole sphere is inside the widened cone — always on.
                shouldBeOn = allowed >= Mathf.PI || cosAngle >= Mathf.Cos(allowed);
            }

            if (shouldBeOn == b.On)
                continue;

            b.On = shouldBeOn;
            List<Renderer> renderers = b.Renderers;
            for (int r = 0; r < renderers.Count; r++)
            {
                Renderer rend = renderers[r];
                if (rend != null && rend.enabled != shouldBeOn)
                    rend.enabled = shouldBeOn;
            }
        }
    }

    /// <summary>
    /// Half-angle (radians) of the cull cone. Sized to the frustum corner — the widest point on
    /// screen — so everything actually visible is inside the cone before <see cref="coneMarginDegrees"/>
    /// is added on top.
    /// </summary>
    float ComputeConeHalfAngle(Camera cam)
    {
        float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float aspect = cam.aspect > 0f ? cam.aspect : 16f / 9f;
        float tanH = tanV * aspect;
        // atan of the diagonal reach gives the angle from forward to the frustum corner.
        float diagonalHalf = Mathf.Atan(Mathf.Sqrt(tanV * tanV + tanH * tanH));
        float half = diagonalHalf + Mathf.Max(0f, coneMarginDegrees) * Mathf.Deg2Rad;
        return Mathf.Clamp(half, 1f * Mathf.Deg2Rad, 89f * Mathf.Deg2Rad);
    }

    /// <summary>Rebuilds the bucket grid from the current scene's static renderers.</summary>
    public void Rescan()
    {
        _buckets.Clear();
        _bucketByCoord.Clear();

        MeshRenderer[] all = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        _lastRendererCount = all.Length;

        float inv = 1f / Mathf.Max(0.01f, bucketSize);
        for (int i = 0; i < all.Length; i++)
        {
            MeshRenderer rend = all[i];
            if (rend == null || !ShouldManage(rend))
                continue;

            Bounds bounds = rend.bounds;
            Vector3 c = bounds.center;
            Vector3Int coord = new(
                Mathf.FloorToInt(c.x * inv),
                Mathf.FloorToInt(c.y * inv),
                Mathf.FloorToInt(c.z * inv));

            if (!_bucketByCoord.TryGetValue(coord, out Bucket bucket))
            {
                bucket = new Bucket();
                _bucketByCoord.Add(coord, bucket);
                _buckets.Add(bucket);
            }

            bucket.Renderers.Add(rend);

            // Reset to visible on (re)scan so the next update pass culls from a known state.
            // Without this a renderer culled-off before a rescan (e.g. maze rebuild) could stay
            // hidden even once it's back in range, since the fresh bucket starts On = true.
            if (!rend.enabled)
                rend.enabled = true;
        }

        // Finalise each bucket: centre = encapsulated centre, radius = reach to furthest geometry.
        for (int i = 0; i < _buckets.Count; i++)
        {
            Bucket b = _buckets[i];
            if (b.Renderers.Count == 0)
                continue;

            Bounds combined = b.Renderers[0].bounds;
            for (int r = 1; r < b.Renderers.Count; r++)
                combined.Encapsulate(b.Renderers[r].bounds);

            b.Center = combined.center;
            b.Radius = combined.extents.magnitude;

            float on = cullDistance + b.Radius;
            float off = on + Mathf.Max(0f, hysteresis);
            b.OnSqr = on * on;
            b.OffSqr = off * off;

            float nearOn = nearRadius + b.Radius;
            float nearOff = nearOn + Mathf.Max(0f, hysteresis);
            b.NearOnSqr = nearOn * nearOn;
            b.NearOffSqr = nearOff * nearOff;

            b.On = true; // start visible; the next update pass culls what's out of view.
        }
    }

    bool ShouldManage(Renderer rend)
    {
        if (rend.GetComponentInParent<PlayerController>() != null)
            return false;
        if (rend.GetComponentInParent<WorldRenderCullIgnore>() != null)
            return false;

        // Moving physics objects (thrown balls/bottles/rings, ragdolls) travel far from the
        // position they were bucketed at, so a spatial-bucket cull would hide/show them wrongly.
        if (rend.GetComponentInParent<Rigidbody>() != null)
            return false;

        // Character rigs — players and enemies, plus props held in their hands (e.g. the Clown's
        // hammer) — roam the level and some manage their own visibility (avatar dormancy). Detect
        // them as a NetworkObject whose hierarchy contains a SkinnedMeshRenderer. Static networked
        // booths (blackjack table, ring toss, etc.) have NO skinned mesh, so they stay cullable —
        // which is the whole point: the carnival games now cull with the rest of the world.
        NetworkObject networkObject = rend.GetComponentInParent<NetworkObject>();
        if (networkObject != null && networkObject.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
            return false;

        // Degenerate bounds (skybox quads, empty meshes) have nothing meaningful to cull by.
        if (rend.bounds.extents.sqrMagnitude <= 0.0001f)
            return false;

        return true;
    }

    Camera ResolveViewpoint()
    {
        if (_viewCamera != null && _viewCamera.isActiveAndEnabled && _viewCamera.gameObject.activeInHierarchy)
            return _viewCamera;

        // Camera.main is null in this project (PlayerView is Untagged), so fall back to the
        // enabled Game camera — on a client that's the local player's view camera.
        Camera cam = Camera.main;
        if (cam == null)
        {
            Camera[] cams = Camera.allCameras; // enabled cameras only
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null && cams[i].isActiveAndEnabled && cams[i].cameraType == CameraType.Game)
                {
                    cam = cams[i];
                    break;
                }
            }
        }

        _viewCamera = cam;
        return _viewCamera;
    }
}
