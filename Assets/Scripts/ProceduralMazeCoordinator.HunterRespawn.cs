using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Replaces a killed maze hunter (Enemy slot 2 — Level01's Jailor, <b>Level02's Clown</b>, Level03's
/// SecurityGuard) without regenerating the level.
///
/// The maze is built once per section and then thrown away as data, so this keeps the small slice of that
/// build a respawn actually needs: the hunter prefab, every cell the build considered spawnable, and the
/// cell-size / interior-room / trap context <see cref="ResolveSpawnPositionWithSeparation"/> needs to turn a
/// cell into a NavMesh-confirmed floor position. Captured at the end of <see cref="TrySpawnMazeEnemies"/>,
/// dropped the moment the next maze is built.
///
/// The point of the feature is that nobody ever <i>watches</i> the replacement arrive, so the picker grades
/// every cached cell against every player on two axes — distance and line of sight — takes a random cell from
/// the best grade available, and then re-checks the jittered spawn point it actually resolved rather than
/// trusting the cell it came from. Only a maze with nowhere hidden to put him falls back to a visible cell,
/// and that warns.
///
/// Server-only, like every other spawn path here: the replacement is instantiated and
/// <see cref="NetworkObject.Spawn"/>ed by the server and reaches clients through normal NGO replication.
/// </summary>
public partial class ProceduralMazeCoordinator
{
    /// <summary>Eye height used for the "can a player see this" line-of-sight probe.</summary>
    const float HunterRespawnViewerEyeHeight = 1.5f;

    /// <summary>Chest height on the candidate point — roughly where the hunter's mass would appear.</summary>
    const float HunterRespawnProbeHeight = 1.1f;

    /// <summary>
    /// Past this the line-of-sight probe is skipped and the point counts as hidden: <see cref="WorldRenderCuller"/>
    /// stops drawing static geometry at 48m and <see cref="MazeDistanceFog"/> closes in before that, so a
    /// spawn this far away is not on screen even down a straight corridor.
    /// </summary>
    const float HunterRespawnAlwaysHiddenDistance = 60f;

    /// <summary>
    /// How many shortlisted cells to actually resolve a spawn point in before settling. A cell is graded by
    /// its anchor, but the spawn is jittered up to half a cell off it, so the resolved point is re-checked and
    /// a visible one costs another try rather than the whole pick.
    /// </summary>
    const int HunterRespawnPlacementAttempts = 6;

    /// <summary>
    /// Bumped by <see cref="ClearHunterRespawnContext"/> on every maze build. A scheduled respawn captures the
    /// value it was queued under and aborts if it no longer matches — otherwise a Clown killed seconds before
    /// the elevator would follow the party into the next section.
    /// </summary>
    int _mazeBuildGeneration;

    GameObject _hunterRespawnPrefab;
    /// <summary>Every cell the build considered spawnable — the pool the picker grades against the players.</summary>
    readonly List<Vector2Int> _hunterRespawnCells = new();
    readonly List<Transform> _hunterRespawnTrapRoots = new();
    InteriorRoomBuildPlan _hunterRespawnInteriorPlan;
    Transform _hunterRespawnEnemiesRoot;
    Transform _hunterRespawnMazeRoot;
    Vector2Int _hunterRespawnStartCell;
    Vector2Int _hunterRespawnExitCell;
    float _hunterRespawnCellSize;
    float _hunterRespawnYOffset;
    int _hunterRespawnSeed;
    int _hunterRespawnCounter;

    // Scratch buffers. The picker runs once per hunter death, but it walks a few hundred cells when it does.
    readonly List<Vector3> _hunterRespawnViewerEyes = new();
    readonly List<Vector2Int> _hunterRespawnPreferredCells = new();
    readonly List<Vector2Int> _hunterRespawnAcceptedCells = new();
    readonly List<Vector2Int> _hunterRespawnShortlist = new();
    readonly List<Vector3> _hunterRespawnPlacedScratch = new();
    // Deep enough that a ragdolled player's bone colliders in front of the eye cannot fill it before a wall
    // shows up. Overflowing it errs toward "visible", which only ever costs a candidate.
    readonly RaycastHit[] _hunterRespawnSightHits = new RaycastHit[32];

    /// <summary>
    /// True once a build has captured a hunter prefab and at least one cell to put it in, and that build's
    /// maze is still standing. The maze-root check is what stops a respawn queued in Level02 from firing
    /// after a quit to the menu — that path never rebuilds a maze, so the build generation alone would
    /// still match and a Clown would appear in the lobby.
    /// </summary>
    public bool HasMazeHunterRespawnContext =>
        _hunterRespawnPrefab != null && _hunterRespawnCells.Count > 0 && _hunterRespawnMazeRoot != null;

    /// <summary>
    /// Queues a hunter respawn on the coordinator (which is on the DontDestroyOnLoad NetworkManager object, so
    /// the dying hunter's own corpse despawn cannot cancel the wait).
    ///
    /// <typeparamref name="T"/> guards the enemy slot: the caller names the AI component it expects on the
    /// level's hunter prefab, so <see cref="ClownHealth"/> asking for a replacement on a level whose slot 2
    /// holds a Jailor is a no-op rather than a surprise Jailor.
    /// </summary>
    /// <returns>True if a respawn was queued (not that it will succeed — the cell search runs when it fires).</returns>
    public static bool TryServerScheduleMazeHunterRespawn<T>(
        float delaySeconds,
        float minPlayerDistance,
        float preferredPlayerDistance) where T : Component
    {
        ProceduralMazeCoordinator coordinator = ResolveCoordinatorForRespawn();
        if (coordinator == null)
            return false;

        return coordinator.ServerScheduleMazeHunterRespawn<T>(delaySeconds, minPlayerDistance, preferredPlayerDistance);
    }

    static ProceduralMazeCoordinator ResolveCoordinatorForRespawn()
    {
        // Same lookup as TryApplyMazeSeedAsClientFromRpc: the coordinator is added to the NetworkManager
        // object by MultiplayerBootstrap. The scene search only matters for dev scenes entered directly.
        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.TryGetComponent(out ProceduralMazeCoordinator coordinator)
            && coordinator != null)
        {
            return coordinator;
        }

        return FindAnyObjectByType<ProceduralMazeCoordinator>();
    }

    public bool ServerScheduleMazeHunterRespawn<T>(
        float delaySeconds,
        float minPlayerDistance,
        float preferredPlayerDistance) where T : Component
    {
        if (IsPureNetworkClientForRespawn())
            return false;

        if (!HasMazeHunterRespawnContext)
            return false;

        if (_hunterRespawnPrefab.GetComponent<T>() == null)
            return false;

        StartCoroutine(HunterRespawnAfterDelayRoutine(
            Mathf.Max(0f, delaySeconds), _mazeBuildGeneration, minPlayerDistance, preferredPlayerDistance));
        return true;
    }

    IEnumerator HunterRespawnAfterDelayRoutine(
        float delaySeconds,
        int buildGeneration,
        float minPlayerDistance,
        float preferredPlayerDistance)
    {
        if (delaySeconds > 0f)
            yield return new WaitForSeconds(delaySeconds);

        // The maze was rebuilt (next section, or a re-host) while we waited: that hunter belongs to a level
        // that no longer exists.
        if (buildGeneration != _mazeBuildGeneration)
            yield break;

        TryServerRespawnMazeHunter(minPlayerDistance, preferredPlayerDistance, out GameObject _);
    }

    /// <summary>
    /// Places a fresh hunter immediately. Public so an encounter script can call it directly; the normal caller
    /// is <see cref="HunterRespawnAfterDelayRoutine"/>.
    /// </summary>
    public bool TryServerRespawnMazeHunter(
        float minPlayerDistance,
        float preferredPlayerDistance,
        out GameObject spawned)
    {
        spawned = null;

        if (IsPureNetworkClientForRespawn() || !HasMazeHunterRespawnContext)
            return false;

        if (!TryBuildHunterRespawnShortlist(minPlayerDistance, preferredPlayerDistance, out bool shortlistIsHidden))
            return false;

        if (!TryResolveHunterRespawnPosition(shortlistIsHidden, out Vector3 position, out Vector2Int cell))
            return false;

        Transform parent = _hunterRespawnEnemiesRoot != null ? _hunterRespawnEnemiesRoot : null;
        spawned = Instantiate(_hunterRespawnPrefab, position, Quaternion.identity, parent);

        if (_networkManager != null && _networkManager.IsListening)
        {
            NetworkObject networkObject = spawned.GetComponent<NetworkObject>();
            if (networkObject != null)
                networkObject.Spawn();
        }

        // A respawned Jailor needs the carry destination the build handed the original. No-ops on any config
        // that does not use it (Level02 included).
        TryAssignJailorCarryDestinationFromMaze(
            _hunterRespawnMazeRoot, _hunterRespawnStartCell, _hunterRespawnExitCell, _hunterRespawnCellSize);

        Debug.Log(
            $"[Maze] Respawned maze hunter \"{_hunterRespawnPrefab.name}\" at cell {cell}, "
            + $"{HorizontalDistanceToNearestViewer(position):F1}m from the nearest player.",
            this);
        return true;
    }

    /// <summary>
    /// Turns a shortlisted cell into the exact spawn point, re-checking line of sight on the point it resolved:
    /// cells are graded by their anchor, but <see cref="ResolveSpawnPositionWithSeparation"/> jitters up to half
    /// a cell off it, which at a junction is enough to slide into view.
    /// </summary>
    bool TryResolveHunterRespawnPosition(bool shortlistIsHidden, out Vector3 position, out Vector2Int cell)
    {
        position = default;
        cell = default;

        float minSeparation = ResolveMazeEnemyMinSeparationXZ(_hunterRespawnCellSize);
        int attempts = Mathf.Min(HunterRespawnPlacementAttempts, _hunterRespawnShortlist.Count);
        bool hasFallback = false;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Vector2Int candidate = TakeRandomFromShortlist();

            // Jitter each attempt — and each respawn over the level's life — away from the last.
            _hunterRespawnCounter++;
            int spawnKey = MixSeed(_hunterRespawnSeed, MixSeed(_hunterRespawnCounter, unchecked((int)0x2C10C0DE)));

            _hunterRespawnPlacedScratch.Clear();
            Vector3 candidatePosition = ResolveSpawnPositionWithSeparation(
                candidate,
                _hunterRespawnCellSize,
                _hunterRespawnYOffset,
                _hunterRespawnSeed,
                spawnKey,
                _hunterRespawnPlacedScratch,
                minSeparation,
                _hunterRespawnInteriorPlan,
                _hunterRespawnTrapRoots);

            if (!hasFallback)
            {
                position = candidatePosition;
                cell = candidate;
                hasFallback = true;
            }

            // The shortlist is already the last-resort visible tier: nothing left to verify, and the warning
            // explaining it has been logged.
            if (!shortlistIsHidden)
                return true;

            if (IsPointVisibleToAnyViewer(candidatePosition + Vector3.up * HunterRespawnProbeHeight))
                continue;

            position = candidatePosition;
            cell = candidate;
            return true;
        }

        if (hasFallback)
        {
            LogMazeWarningOnce(
                "hunter-respawn-jitter-visible",
                $"[Maze] All {attempts} hidden hunter respawn cell(s) tried jittered into a player's line of "
                + "sight; using the last. The respawn may be visible.",
                this);
        }

        return hasFallback;
    }

    Vector2Int TakeRandomFromShortlist()
    {
        int index = Random.Range(0, _hunterRespawnShortlist.Count);
        Vector2Int cell = _hunterRespawnShortlist[index];
        _hunterRespawnShortlist.RemoveAt(index);
        return cell;
    }

    /// <summary>
    /// Fills <see cref="_hunterRespawnShortlist"/> with the best grade of cell the maze currently offers.
    /// Hidden-or-not is graded ahead of far-or-not: a hunter walking back from the far side of the maze is a
    /// much smaller problem than one seen materialising.
    /// </summary>
    /// <param name="shortlistIsHidden">False only in the last-resort grade, where every cell can be seen.</param>
    bool TryBuildHunterRespawnShortlist(
        float minPlayerDistance,
        float preferredPlayerDistance,
        out bool shortlistIsHidden)
    {
        shortlistIsHidden = true;

        CollectHunterRespawnViewerEyes();

        float minSqr = Mathf.Max(0f, minPlayerDistance);
        minSqr *= minSqr;
        float preferredSqr = Mathf.Max(minPlayerDistance, preferredPlayerDistance);
        preferredSqr *= preferredSqr;

        _hunterRespawnShortlist.Clear();
        _hunterRespawnPreferredCells.Clear();
        _hunterRespawnAcceptedCells.Clear();

        Vector2Int farthestHiddenCell = default;
        float farthestHiddenSqr = -1f;
        Vector2Int farthestAnyCell = default;
        float farthestAnySqr = -1f;

        for (int i = 0; i < _hunterRespawnCells.Count; i++)
        {
            Vector2Int candidate = _hunterRespawnCells[i];
            Vector3 anchor = ResolveMazeEnemySpawnHorizontalCellOrigin(
                candidate, _hunterRespawnCellSize, _hunterRespawnInteriorPlan)
                + Vector3.up * HunterRespawnProbeHeight;

            float nearestSqr = HorizontalSqrDistanceToNearestViewer(anchor);
            if (nearestSqr > farthestAnySqr)
            {
                farthestAnySqr = nearestSqr;
                farthestAnyCell = candidate;
            }

            if (IsPointVisibleToAnyViewer(anchor))
                continue;

            if (nearestSqr > farthestHiddenSqr)
            {
                farthestHiddenSqr = nearestSqr;
                farthestHiddenCell = candidate;
            }

            if (nearestSqr >= preferredSqr)
                _hunterRespawnPreferredCells.Add(candidate);
            else if (nearestSqr >= minSqr)
                _hunterRespawnAcceptedCells.Add(candidate);
        }

        if (_hunterRespawnPreferredCells.Count > 0)
        {
            _hunterRespawnShortlist.AddRange(_hunterRespawnPreferredCells);
            return true;
        }

        if (_hunterRespawnAcceptedCells.Count > 0)
        {
            _hunterRespawnShortlist.AddRange(_hunterRespawnAcceptedCells);
            return true;
        }

        if (farthestHiddenSqr >= 0f)
        {
            // Nothing clears the distance rule, but this one is at least out of sight.
            _hunterRespawnShortlist.Add(farthestHiddenCell);
            LogMazeWarningOnce(
                "hunter-respawn-close",
                $"[Maze] No hunter respawn cell is {minPlayerDistance:F0}m from the players; using the farthest "
                + "out-of-sight cell instead. Enlarge the maze or lower the respawn min distance.",
                this);
            return true;
        }

        if (farthestAnySqr >= 0f)
        {
            // Every cell has a sight line to somebody: a tiny maze, or the party spread right across it.
            _hunterRespawnShortlist.Add(farthestAnyCell);
            shortlistIsHidden = false;
            LogMazeWarningOnce(
                "hunter-respawn-visible",
                "[Maze] Every hunter respawn cell is in a player's line of sight; using the farthest one. "
                + "The respawn may be visible.",
                this);
            return true;
        }

        return false;
    }

    void CollectHunterRespawnViewerEyes()
    {
        _hunterRespawnViewerEyes.Clear();

        // Every registered player counts as a viewer, dead ones included — a downed player's camera is still
        // pointed at the maze.
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;
            _hunterRespawnViewerEyes.Add(player.transform.position + Vector3.up * HunterRespawnViewerEyeHeight);
        }
    }

    /// <summary>Squared horizontal distance to the closest player, or <see cref="float.MaxValue"/> with none.</summary>
    float HorizontalSqrDistanceToNearestViewer(Vector3 point)
    {
        float nearestSqr = float.MaxValue;
        for (int i = 0; i < _hunterRespawnViewerEyes.Count; i++)
        {
            Vector3 eye = _hunterRespawnViewerEyes[i];
            float dx = point.x - eye.x;
            float dz = point.z - eye.z;
            float sqr = dx * dx + dz * dz;
            if (sqr < nearestSqr)
                nearestSqr = sqr;
        }

        return nearestSqr;
    }

    float HorizontalDistanceToNearestViewer(Vector3 point)
    {
        float sqr = HorizontalSqrDistanceToNearestViewer(point);
        return sqr >= float.MaxValue * 0.5f ? 0f : Mathf.Sqrt(sqr);
    }

    bool IsPointVisibleToAnyViewer(Vector3 point)
    {
        for (int i = 0; i < _hunterRespawnViewerEyes.Count; i++)
        {
            Vector3 eye = _hunterRespawnViewerEyes[i];
            float dx = point.x - eye.x;
            float dz = point.z - eye.z;
            if (dx * dx + dz * dz >= HunterRespawnAlwaysHiddenDistance * HunterRespawnAlwaysHiddenDistance)
                continue;
            if (HasClearSightLine(eye, point))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True if nothing solid stands between a player's eye and the point. Modelled on
    /// <c>ClownAI.HasLineOfSightToTarget</c>: the ray starts inside the player's own capsule, so hits on the
    /// player themselves are skipped rather than counted as cover.
    /// </summary>
    bool HasClearSightLine(Vector3 eye, Vector3 point)
    {
        Vector3 toPoint = point - eye;
        float distance = toPoint.magnitude;
        if (distance <= 0.001f)
            return true;

        int hitCount = Physics.RaycastNonAlloc(
            eye,
            toPoint / distance,
            _hunterRespawnSightHits,
            distance,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            Transform hitTransform = _hunterRespawnSightHits[i].transform;
            _hunterRespawnSightHits[i] = default;

            if (hitTransform == null)
                continue;

            // The players themselves are not cover.
            if (hitTransform.GetComponentInParent<PlayerHealth>() != null)
                continue;

            return false;
        }

        return true;
    }

    bool IsPureNetworkClientForRespawn()
    {
        return _networkManager != null && _networkManager.IsListening && !_networkManager.IsServer;
    }

    /// <summary>
    /// Snapshots what a respawn needs out of the build that just ran. The two cell lists are the build's own
    /// split of the same candidate pool (everything the hunter did not reserve, plus what it did) — together
    /// they are every cell the maze deemed spawnable.
    /// </summary>
    void CacheHunterRespawnContext(
        GameObject hunterPrefab,
        List<Vector2Int> unreservedCells,
        List<Vector2Int> reservedHunterCells,
        Transform enemiesRoot,
        Transform mazeRoot,
        Vector2Int startCell,
        Vector2Int exitCell,
        float cellSize,
        float yOffset,
        int seed,
        InteriorRoomBuildPlan interiorPlan,
        List<Transform> mazeTrapRoots)
    {
        _hunterRespawnPrefab = hunterPrefab;

        _hunterRespawnCells.Clear();
        if (unreservedCells != null)
            _hunterRespawnCells.AddRange(unreservedCells);
        if (reservedHunterCells != null)
            _hunterRespawnCells.AddRange(reservedHunterCells);

        _hunterRespawnTrapRoots.Clear();
        if (mazeTrapRoots != null)
            _hunterRespawnTrapRoots.AddRange(mazeTrapRoots);

        _hunterRespawnInteriorPlan = interiorPlan;
        _hunterRespawnEnemiesRoot = enemiesRoot;
        _hunterRespawnMazeRoot = mazeRoot;
        _hunterRespawnStartCell = startCell;
        _hunterRespawnExitCell = exitCell;
        _hunterRespawnCellSize = cellSize;
        _hunterRespawnYOffset = yOffset;
        _hunterRespawnSeed = seed;
        _hunterRespawnCounter = 0;
    }

    /// <summary>Called at the top of every maze build: the previous level's cells and prefab are dead data.</summary>
    void ClearHunterRespawnContext()
    {
        _mazeBuildGeneration++;
        _hunterRespawnPrefab = null;
        _hunterRespawnCells.Clear();
        _hunterRespawnTrapRoots.Clear();
        _hunterRespawnShortlist.Clear();
        _hunterRespawnInteriorPlan = default;
        _hunterRespawnEnemiesRoot = null;
        _hunterRespawnMazeRoot = null;
        _hunterRespawnCounter = 0;
    }
}
