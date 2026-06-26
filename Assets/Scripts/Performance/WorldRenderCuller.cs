using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Runtime render-distance culler for static world geometry (maze walls/floors/props,
/// carnival booths, decorations). Previously every level rendered its entire geometry at
/// all times; in a maze you only ever see a handful of cells at once, so the rest is wasted
/// draw calls. This component buckets all static <see cref="MeshRenderer"/>s into a coarse
/// spatial grid and toggles <c>Renderer.enabled</c> per bucket by distance to the local view
/// camera, so far-away geometry stops drawing (and stops casting shadows) until the player
/// approaches.
///
/// Companion to <see cref="MazeLightCuller"/> (which does the same for lights). Like that
/// one it is:
///   * Non-destructive — only flips the runtime enabled flag, never edits meshes/materials
///     or the scene/prefab assets. Geometry re-appears automatically as the player nears.
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
[DisallowMultipleComponent]
public class WorldRenderCuller : MonoBehaviour
{
    [Header("Distance")]
    [Tooltip("Metres from the local camera at which a bucket of geometry switches off. The bucket's own radius is added on top, so large props still cull at a sane edge.")]
    [SerializeField] float cullDistance = 48f;

    [Tooltip("Extra metres a bucket must move BEYOND its on-distance before it switches back off. Prevents on/off flicker for geometry hovering right at the cull edge.")]
    [SerializeField] float hysteresis = 4f;

    [Header("Bucketing")]
    [Tooltip("Edge length (metres) of each spatial bucket. Renderers are grouped by which bucket their centre falls in and toggled together. ~2 maze cells is a good default: small enough to cull tightly, large enough that the per-bucket loop stays cheap.")]
    [SerializeField] float bucketSize = 12f;

    [Header("Timing")]
    [Tooltip("Seconds between distance evaluations. Walking speed doesn't need an every-frame pass.")]
    [SerializeField] float updateInterval = 0.15f;

    [Tooltip("Seconds between rescans that pick up newly spawned maze geometry. Set to 0 to scan once and never again (use for fully authored static scenes).")]
    [SerializeField] float rescanInterval = 4f;

    // One entry per spatial bucket.
    sealed class Bucket
    {
        public readonly List<Renderer> Renderers = new();
        public Vector3 Center;
        public float OnSqr;   // (cullDistance + radius)^2
        public float OffSqr;  // (cullDistance + radius + hysteresis)^2
        public bool On = true;
    }

    readonly List<Bucket> _buckets = new();
    readonly Dictionary<Vector3Int, Bucket> _bucketByCoord = new();

    Transform _viewpoint;
    float _nextUpdate;
    float _nextRescan;
    int _lastRendererCount = -1;

    void OnEnable()
    {
        _nextUpdate = 0f;
        _nextRescan = 0f;
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
                Rescan();
        }

        if (now < _nextUpdate)
            return;
        _nextUpdate = now + Mathf.Max(0.02f, updateInterval);

        Transform vp = ResolveViewpoint();
        if (vp == null)
            return; // no local camera yet (or headless server) — leave geometry as authored.

        Vector3 eye = vp.position;
        for (int i = 0; i < _buckets.Count; i++)
        {
            Bucket b = _buckets[i];
            float distSqr = (b.Center - eye).sqrMagnitude;

            // Hysteresis: switch on when inside OnSqr, only switch off once past OffSqr.
            bool shouldBeOn = b.On ? distSqr <= b.OffSqr : distSqr <= b.OnSqr;
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
            float radius = combined.extents.magnitude;
            float on = cullDistance + radius;
            float off = on + Mathf.Max(0f, hysteresis);
            b.OnSqr = on * on;
            b.OffSqr = off * off;
            b.On = true; // start visible; the next update pass culls what's out of range.
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

    Transform ResolveViewpoint()
    {
        if (_viewpoint != null && _viewpoint.gameObject.activeInHierarchy)
            return _viewpoint;

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

        _viewpoint = cam != null ? cam.transform : null;
        return _viewpoint;
    }
}
