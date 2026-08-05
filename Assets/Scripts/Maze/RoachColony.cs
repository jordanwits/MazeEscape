using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A nest of decorative cockroaches scattered across the floor and walls of one maze cell. They crawl
/// about until a player gets close enough to almost step on them, then bolt and vanish.
///
/// <para><b>Not networked, but still consistent between players.</b> Like <see cref="BatSwarmRoost"/>
/// there is no NetworkObject here — every peer builds identical colonies from the maze seed. Unlike the
/// bats, though, the trigger is not per-player: proximity is measured against <em>every</em> player, and
/// player positions are already replicated, so each machine measures the same distances against the same
/// colony and reaches the same verdict. The roaches therefore scatter on everyone's screen at once
/// without a single RPC. Testing only the local camera would leave a nest that had vanished on one
/// screen still sitting there on their team-mate's.</para>
///
/// <para>Placement is done by raycast rather than from hardcoded cell geometry, so the colony works on
/// any maze piece regardless of which décor variant was rolled.</para>
/// </summary>
[DisallowMultipleComponent]
public class RoachColony : MonoBehaviour
{
    [Header("Colony")]
    [Tooltip("Roach prefab (must carry DecorativeRoach). Instanced once at level build and pooled.")]
    [SerializeField] GameObject roachPrefab;
    [Tooltip("How many roaches this nest holds.")]
    [SerializeField, Min(1)] int roachCount = 14;
    [Tooltip("Fraction of the colony placed on walls rather than the floor, 0-1.")]
    [SerializeField, Range(0f, 1f)] float wallShare = 0.45f;
    [Tooltip("Metres out from the colony centre that roaches may be placed. Roughly half a cell.")]
    [SerializeField] float spreadRadius = 2.4f;
    [Tooltip("Height range up a wall where roaches may sit, in metres above the floor.")]
    [SerializeField] Vector2 wallHeightRange = new(0.1f, 2.2f);
    [Tooltip("Surfaces roaches may be placed on and crawl across.")]
    [SerializeField] LayerMask surfaceMask = ~0;

    [Header("Trigger")]
    [Tooltip("Metres from a player at which the nest breaks. Deliberately short — letting the player get "
        + "almost on top of them before they bolt is what makes it a jolt rather than something that "
        + "happens harmlessly across the room.")]
    [SerializeField] float triggerRadius = 2f;
    [Tooltip("Require clear line of sight, so a player on the far side of a wall doesn't set them off.")]
    [SerializeField] bool requireLineOfSight = true;
    [Tooltip("Seconds between trigger checks.")]
    [SerializeField] float pollInterval = 0.1f;

    // Shared across every colony so the scene is searched once per refresh, not once per colony.
    static readonly List<Transform> PlayerTransforms = new();
    static float _nextPlayerRefresh;

    readonly List<DecorativeRoach> _roaches = new();

    Camera _viewer;
    float _nextPoll;
    float _nextCameraCheck;
    bool _spent;

    /// <summary>Local viewer transform, used by the roaches for their grazing-angle tilt.</summary>
    public Transform ViewerTransform
    {
        get
        {
            Camera cam = ResolveViewer();
            return cam != null ? cam.transform : null;
        }
    }

    void Start()
    {
        BuildColony();
    }

    void BuildColony()
    {
        if (roachPrefab == null)
        {
            Debug.LogWarning($"{nameof(RoachColony)} on '{name}' has no roach prefab — colony is inert.", this);
            enabled = false;
            return;
        }

        // This project runs with Physics.autoSyncTransforms disabled, and the maze pieces were
        // Instantiated and positioned only moments ago — without an explicit sync every placement cast
        // below would be tested against stale collider transforms and quietly find nothing.
        Physics.SyncTransforms();

        int wallTarget = Mathf.RoundToInt(roachCount * wallShare);

        for (int i = 0; i < roachCount; i++)
        {
            bool wantWall = i < wallTarget;
            if (!TryFindSurfacePoint(wantWall, out Vector3 point, out Vector3 normal)
                && !TryFindSurfacePoint(!wantWall, out point, out normal))
                continue; // this cell had nowhere to put it; just run a smaller colony

            GameObject instance = Instantiate(roachPrefab, point, Quaternion.identity, transform);
            instance.name = $"Roach{i:00}";

            DecorativeRoach roach = instance.GetComponent<DecorativeRoach>();
            if (roach == null)
            {
                Debug.LogWarning($"{nameof(RoachColony)} prefab '{roachPrefab.name}' is missing "
                    + $"{nameof(DecorativeRoach)} — colony is inert.", this);
                Destroy(instance);
                enabled = false;
                return;
            }

            roach.Place(this, point, normal);
            _roaches.Add(roach);
        }

        if (_roaches.Count == 0)
            enabled = false;
    }

    /// <summary>
    /// Finds a point on a wall or the floor near the colony centre. Everything is discovered by raycast
    /// so this doesn't care which maze piece variant was built or what décor is bolted to it.
    /// </summary>
    bool TryFindSurfacePoint(bool wall, out Vector3 point, out Vector3 normal)
    {
        point = Vector3.zero;
        normal = Vector3.up;

        for (int attempt = 0; attempt < 12; attempt++)
        {
            if (wall)
            {
                float height = Random.Range(wallHeightRange.x, wallHeightRange.y);
                Vector3 origin = transform.position + Vector3.up * height;
                Vector3 direction = Quaternion.AngleAxis(Random.value * 360f, Vector3.up) * Vector3.forward;

                if (!Physics.Raycast(origin, direction, out RaycastHit wallHit, spreadRadius + 1.5f,
                        surfaceMask, QueryTriggerInteraction.Ignore))
                    continue;

                // Reject floors and ceilings that a near-horizontal ray happened to clip.
                if (Mathf.Abs(Vector3.Dot(wallHit.normal, Vector3.up)) > 0.35f)
                    continue;

                point = wallHit.point;
                normal = wallHit.normal;
                return true;
            }

            Vector2 disc = Random.insideUnitCircle * spreadRadius;
            Vector3 from = transform.position + new Vector3(disc.x, 1.5f, disc.y);

            if (!Physics.Raycast(from, Vector3.down, out RaycastHit floorHit, 6f,
                    surfaceMask, QueryTriggerInteraction.Ignore))
                continue;

            if (Vector3.Dot(floorHit.normal, Vector3.up) < 0.7f)
                continue;

            point = floorHit.point;
            normal = floorHit.normal;
            return true;
        }

        return false;
    }

    void Update()
    {
        if (_spent)
            return;

        float now = Time.time;
        if (now < _nextPoll)
            return;
        _nextPoll = now + Mathf.Max(0.02f, pollInterval);

        if (TryGetNearbyPlayer(out Vector3 playerPosition))
            ScatterAll(playerPosition);
    }

    /// <summary>
    /// True if any player is inside <see cref="triggerRadius"/> with line of sight.
    ///
    /// Testing every player rather than just the local one is what keeps peers in agreement: player
    /// positions are already replicated, so each machine measures the same distances against the same
    /// colony and scatters at the same moment. Checking only the local camera would mean a nest that had
    /// vanished on one screen was still sitting there on their team-mate's.
    /// </summary>
    bool TryGetNearbyPlayer(out Vector3 playerPosition)
    {
        playerPosition = Vector3.zero;
        RefreshPlayerCache();

        float bestSqr = triggerRadius * triggerRadius;
        bool found = false;

        for (int i = 0; i < PlayerTransforms.Count; i++)
        {
            Transform player = PlayerTransforms[i];
            if (player == null)
                continue;

            Vector3 position = player.position;
            float sqr = (position - transform.position).sqrMagnitude;
            if (sqr > bestSqr || IsBlocked(position))
                continue;

            bestSqr = sqr;
            playerPosition = position;
            found = true;
        }

        if (found || PlayerTransforms.Count > 0)
            return found;

        // No networked avatars (dev scene, or before the local player spawns) — fall back to the camera.
        Transform viewer = ViewerTransform;
        if (viewer == null)
            return false;

        Vector3 eye = viewer.position;
        if ((eye - transform.position).sqrMagnitude > triggerRadius * triggerRadius || IsBlocked(eye))
            return false;

        playerPosition = eye;
        return true;
    }

    bool IsBlocked(Vector3 target)
    {
        if (!requireLineOfSight)
            return false;

        // Both ends are lifted off the floor: the colony sits flat on it, so a ground-level ray would
        // graze the floor collider and read as blocked from every angle.
        Vector3 from = transform.position + Vector3.up * 0.5f;
        Vector3 to = target + Vector3.up * 0.5f;
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance <= 0.05f)
            return false;

        return Physics.Raycast(from, delta / distance, distance - 0.2f, surfaceMask,
            QueryTriggerInteraction.Ignore);
    }

    static void RefreshPlayerCache()
    {
        float now = Time.unscaledTime;
        if (now < _nextPlayerRefresh)
            return;
        _nextPlayerRefresh = now + 2f;

        PlayerTransforms.Clear();
        NetworkPlayerAvatar[] avatars = FindObjectsByType<NetworkPlayerAvatar>(FindObjectsInactive.Exclude);
        for (int i = 0; i < avatars.Length; i++)
        {
            if (avatars[i] != null)
                PlayerTransforms.Add(avatars[i].transform);
        }
    }

    void ScatterAll(Vector3 threatPoint)
    {
        _spent = true;
        for (int i = 0; i < _roaches.Count; i++)
        {
            if (_roaches[i] != null)
                _roaches[i].Scatter(threatPoint);
        }
    }

    /// <summary>Called by a roach once it has vanished; it is already deactivated.</summary>
    public void NotifyRoachFinished(DecorativeRoach roach)
    {
        // The colony is one-shot, so there is nothing to recycle — this exists so roaches don't need to
        // know whether anyone is listening, and gives a hook if colonies are ever made to re-arm.
    }

    /// <summary>Set by <see cref="ProceduralMazeCoordinator"/> when it places the colony from the maze seed.</summary>
    public void ConfigureColony(int count, float spread)
    {
        roachCount = Mathf.Max(1, count);
        spreadRadius = Mathf.Max(0.25f, spread);
    }

    Camera ResolveViewer()
    {
        if (_viewer != null && _viewer.isActiveAndEnabled && _viewer.gameObject.activeInHierarchy)
            return _viewer;

        float now = Time.unscaledTime;
        if (now < _nextCameraCheck)
            return null;
        _nextCameraCheck = now + 0.5f;

        // Camera.main is null in this project (PlayerView is Untagged).
        Camera cam = Camera.main;
        if (cam == null)
        {
            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].isActiveAndEnabled && cameras[i].cameraType == CameraType.Game)
                {
                    cam = cameras[i];
                    break;
                }
            }
        }

        _viewer = cam;
        return _viewer;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.5f, 0.75f, 0.25f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, spreadRadius);
    }
}
