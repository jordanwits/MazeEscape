using System;
using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class ProceduralMazeCoordinator : MonoBehaviour
{
    struct MazeCell
    {
        public MazeFaceMask Openings;
    }

    struct MazeLayout
    {
        public MazeLayout(MazeCell[,] cells, Vector2Int start)
        {
            Cells = cells;
            Start = start;
        }

        public MazeCell[,] Cells { get; }
        public Vector2Int Start { get; }
    }

    readonly struct FrontierNode
    {
        public FrontierNode(Vector2Int cell, MazeFaceMask incomingDirection, int straightRunLength)
        {
            Cell = cell;
            IncomingDirection = incomingDirection;
            StraightRunLength = straightRunLength;
        }

        public Vector2Int Cell { get; }
        public MazeFaceMask IncomingDirection { get; }
        public int StraightRunLength { get; }
    }

    readonly struct DirectionStep
    {
        public DirectionStep(int dx, int dy, MazeFaceMask direction)
        {
            Dx = dx;
            Dy = dy;
            Direction = direction;
        }

        public int Dx { get; }
        public int Dy { get; }
        public MazeFaceMask Direction { get; }
    }

    readonly struct InteriorRoomPlacementEntry
    {
        public InteriorRoomPlacementEntry(MazePieceMatch match, Vector2Int footprint)
        {
            Match = match;
            Footprint = footprint;
        }

        public MazePieceMatch Match { get; }
        public Vector2Int Footprint { get; }
    }

    readonly struct InteriorRoomBuildPlan
    {
        public InteriorRoomBuildPlan(
            Dictionary<Vector2Int, InteriorRoomPlacementEntry> anchors,
            HashSet<Vector2Int> skipCells)
        {
            Anchors = anchors;
            SkipCells = skipCells;
        }

        public Dictionary<Vector2Int, InteriorRoomPlacementEntry> Anchors { get; }
        public HashSet<Vector2Int> SkipCells { get; }
    }

    readonly struct TrapAnchorCandidate
    {
        public TrapAnchorCandidate(Vector2Int cell, Transform anchor)
        {
            Cell = cell;
            Anchor = anchor;
        }

        public Vector2Int Cell { get; }
        public Transform Anchor { get; }
    }

    const string ConfigResourceName = "ProceduralMazeConfig";
    /// <summary>Subfolder under a Resources folder: <c>Assets/Resources/MazeConfigs/*.asset</c>.
    /// Each asset's <see cref="ProceduralMazeConfig.TargetSceneName"/> selects it when that scene loads.</summary>
    const string SectionConfigsResourcesPath = "MazeConfigs";

    /// <summary>
    /// False while a <b>client</b> (not host) has loaded the target maze scene but the procedural
    /// floor/colliders are not built yet. Steam/P2P latency makes this window longer than local IP,
    /// so the local <see cref="CharacterController"/> must not simulate until this becomes true.
    /// </summary>
    public static bool IsLocalMazeCollidersReady { get; private set; } = true;
    const string TrapAnchorName = "TrapAnchor";
    const string TrapAnchor2Name = "TrapAnchor2";
    const string TrapMountPointName = "MountPoint";
    const string ChestAnchorName = "ChestAnchor";
    const string ChestMountPointName = "ChestMount";
    const string TeleportOrbAnchorName = "TeleportOrbAnchor";
    const string RatBotSpawnMarkerName = "RatSpawn";
    const string PosterSpawnName = "PosterSpawn";
    const string ItemSpawnNamePrefix = "ItemSpawn";
    /// <summary>Stands in for a pool index in item ids spawned from a <see cref="MazeItemSpawnPoint"/> override.</summary>
    const int OverrideItemPrefabIndex = -1;
    const string LightSpawnNamePrefix = "LightSpawn";

    static readonly DirectionStep[] Steps =
    {
        new(0, 1, MazeFaceMask.North),
        new(1, 0, MazeFaceMask.East),
        new(0, -1, MazeFaceMask.South),
        new(-1, 0, MazeFaceMask.West)
    };

    [Header("Config")]
    [Tooltip("If set, always uses this config (ignores Resources/MazeConfigs). Leave empty for normal play: one ProceduralMazeConfig per level under Resources/MazeConfigs, matched by Target Scene Name.")]
    [SerializeField] ProceduralMazeConfig configOverride;
    [Tooltip("If set, used instead of the config's Enemy Slot 1 (zombie horde) prefab (same maze asset, different enemy per scene).")]
    [SerializeField] GameObject mazeEnemyPrefabOverride;

    [Header("Navigation (runtime)")]
    [Tooltip("After the maze is built, bake a NavMesh from geometry under the maze root so NavMeshAgents (e.g. zombies) can path. Uses NavMeshSurface from the AI Navigation package.")]
    [SerializeField] bool rebuildNavMeshAfterMaze = true;
    [Tooltip("Physics = MeshCollider/BoxCollider on floor pieces. Render Meshes = Renderer geometry (use if walkable meshes have no colliders).")]
    [SerializeField] NavMeshCollectGeometry navMeshBakeGeometry = NavMeshCollectGeometry.PhysicsColliders;
    [Tooltip("Runtime navmesh surfaces above this height (relative to the maze layout floor, see Origin on ProceduralMazeConfig) are marked Not Walkable so ceilings and wall tops do not bake. Uses max(collider min Y, Origin.y) so pit/deep colliders do not lower this band onto the real floor.")]
    [SerializeField] float navMeshCeilingExcludeHeightAboveFloor = 1.4f;

    ProceduralMazeConfig _config;
    NetworkManager _networkManager;
    bool _hasCurrentSeed;
    int _currentSeed;
    Coroutine _sceneRoutine;
    readonly HashSet<string> _loggedMazeWarnings = new();
    // Prefab actually placed at each built cell this build, so BuildCell can steer a cell away from
    // repeating an orthogonal neighbor's piece (see AvoidAdjacentDuplicatePieces). Cleared per build.
    readonly Dictionary<Vector2Int, GameObject> _placedCellPrefabs = new();
    // Every cell the multi-cell exit hall claimed this build (see TryAttachExitHall). The hall IS the
    // finish, so nothing else — enemies especially — gets to spawn inside the approach. Cleared per build.
    readonly HashSet<Vector2Int> _exitHallCells = new();
    string _lastServerMazeBuildSceneName;
    int _lastServerMazeBuildSeed = int.MinValue;
    Dictionary<string, ProceduralMazeConfig> _sectionConfigsByTargetScene;
    bool _sectionConfigsIndexed;

    void Awake()
    {
        _networkManager = GetComponent<NetworkManager>();
        EnsureConfigForScene(SceneManager.GetActiveScene().name);

        if (_config == null)
            Debug.LogWarning("[Maze] Assign config Override on ProceduralMazeCoordinator or add ProceduralMazeConfig to Resources.", this);
        else if (!_config.HasMinimumStarterSet)
            Debug.LogWarning("[Maze] ProceduralMazeConfig needs cross, straight, dead-end, corner, and tee prefabs assigned.", this);
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
        HookNetworkEvents();
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        UnhookNetworkEvents();
    }

    void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureConfigForScene(scene.name);

        if (!ShouldManageScene(scene))
            return;

        // Before any other Update/FixedUpdate: pure clients have no walkable colliders until the
        // seed round-trip finishes and BuildMazeInScene runs (host already has a synchronous build).
        // A scene transition destroys the old generated maze, so the next seed RPC must rebuild even
        // when the host reuses the same seed for the next section.
        if (IsPureNetworkClient())
        {
            _hasCurrentSeed = false;
            IsLocalMazeCollidersReady = false;
        }

        RestartSceneRoutine(HandleSceneLoadedRoutine(scene));
    }

    IEnumerator HandleSceneLoadedRoutine(Scene scene)
    {
        yield return null;

        if (!ShouldManageScene(scene))
            yield break;

        if (IsServerListening())
        {
            if (_hasCurrentSeed)
                BuildMazeInScene(scene, _currentSeed);
            yield break;
        }

        if (IsClientConnected())
        {
            // Maze seed is sent via NetworkPlayerAvatar ServerRpc/ClientRpc (reliable on Steam; custom
            // named messages to the host were not being received in practice).
            yield break;
        }

        if (!_config.BuildOfflineInPlayMode)
            yield break;

        _currentSeed = _config.RandomizeOfflineSeed ? UnityEngine.Random.Range(int.MinValue, int.MaxValue) : _config.OfflineSeed;
        _hasCurrentSeed = true;
        BuildMazeInScene(scene, _currentSeed);
    }

    void HookNetworkEvents()
    {
        if (_networkManager == null)
            _networkManager = GetComponent<NetworkManager>();

        if (_networkManager == null)
            return;

        _networkManager.OnServerStarted -= HandleServerStarted;
        _networkManager.OnServerStarted += HandleServerStarted;
        _networkManager.OnClientConnectedCallback -= HandleClientConnected;
        _networkManager.OnClientConnectedCallback += HandleClientConnected;
        _networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
        _networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
    }

    void UnhookNetworkEvents()
    {
        if (_networkManager == null)
            return;

        _networkManager.OnServerStarted -= HandleServerStarted;
        _networkManager.OnClientConnectedCallback -= HandleClientConnected;
        _networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    void HandleServerStarted()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        EnsureConfigForScene(activeScene.name);

        if (_config == null || !_config.EnableGeneration)
            return;

        _currentSeed = _config.RandomizeHostSeed ? UnityEngine.Random.Range(int.MinValue, int.MaxValue) : _config.OfflineSeed;
        _hasCurrentSeed = true;

        if (ShouldManageScene(activeScene))
            BuildMazeInScene(activeScene, _currentSeed);
    }

    void HandleClientConnected(ulong clientId)
    {
        if (_networkManager == null || !_networkManager.IsListening)
            return;

        if (_networkManager.IsServer)
        {
            if (clientId != _networkManager.LocalClientId && _hasCurrentSeed)
                TryDeliverMazeSeedToClientPlayer(clientId);

            return;
        }
    }

    void HandleClientDisconnected(ulong clientId)
    {
        if (_networkManager == null)
            return;

        if (!_networkManager.IsServer && clientId == _networkManager.LocalClientId)
        {
            _hasCurrentSeed = false;
            IsLocalMazeCollidersReady = true;
        }
    }

    public bool TryGetServerMazeSeed(out int seed)
    {
        seed = 0;
        if (!IsServerListening() || _networkManager == null || !_hasCurrentSeed)
            return false;
        seed = _currentSeed;
        return true;
    }

    public static void TryApplyMazeSeedAsClientFromRpc(int seed)
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer)
            return;
        if (!NetworkManager.Singleton.TryGetComponent(out ProceduralMazeCoordinator coordinator) || coordinator == null)
            return;
        coordinator.ApplyMazeSeedFromNetworkOnClient(seed);
    }

    void ApplyMazeSeedFromNetworkOnClient(int seed)
    {
        if (_networkManager == null)
            return;

        if (_hasCurrentSeed && _currentSeed == seed)
            return;

        _currentSeed = seed;
        _hasCurrentSeed = true;

        Scene activeScene = SceneManager.GetActiveScene();
        EnsureConfigForScene(activeScene.name);
        if (ShouldManageScene(activeScene))
            BuildMazeInScene(activeScene, seed);
    }

    void TryDeliverMazeSeedToClientPlayer(ulong clientId)
    {
        if (_networkManager == null || !IsServerListening() || !_hasCurrentSeed)
            return;
        if (!_networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client == null)
            return;
        if (client.PlayerObject == null
            || !client.PlayerObject.TryGetComponent(out NetworkPlayerAvatar playerAvatar)
            || playerAvatar == null)
        {
            return;
        }

        playerAvatar.DeliverMazeSeedToOwnerFromServer(_currentSeed);
    }

    void BroadcastMazeSeedToRemotePlayerAvatars(int seed)
    {
        if (_networkManager == null || !IsServerListening() || !_hasCurrentSeed)
            return;

        foreach (KeyValuePair<ulong, NetworkClient> pair in _networkManager.ConnectedClients)
        {
            if (pair.Key == _networkManager.LocalClientId)
                continue;
            if (pair.Value == null)
                continue;
            if (pair.Value.PlayerObject == null
                || !pair.Value.PlayerObject.TryGetComponent(out NetworkPlayerAvatar avatar)
                || avatar == null)
            {
                continue;
            }

            avatar.DeliverMazeSeedToOwnerFromServer(seed);
        }
    }

    void EnsureSectionConfigsIndexed()
    {
        if (_sectionConfigsIndexed)
            return;

        _sectionConfigsIndexed = true;
        _sectionConfigsByTargetScene = new Dictionary<string, ProceduralMazeConfig>(StringComparer.OrdinalIgnoreCase);
        ProceduralMazeConfig[] sectionAssets = Resources.LoadAll<ProceduralMazeConfig>(SectionConfigsResourcesPath);
        foreach (ProceduralMazeConfig candidate in sectionAssets)
        {
            if (candidate == null)
                continue;

            string target = candidate.TargetSceneName;
            if (string.IsNullOrWhiteSpace(target))
                continue;

            target = target.Trim();
            if (_sectionConfigsByTargetScene.TryGetValue(target, out ProceduralMazeConfig existing))
            {
                LogMazeWarningOnce(
                    $"dup-maze-section-config:{target}",
                    $"[Maze] Multiple ProceduralMazeConfig assets in Resources/{SectionConfigsResourcesPath} use Target Scene Name \"{target}\". " +
                    $"Using \"{existing.name}\"; ignoring \"{candidate.name}\".",
                    this);
                continue;
            }

            _sectionConfigsByTargetScene[target] = candidate;
        }
    }

    bool TryGetSectionConfigForScene(string sceneName, out ProceduralMazeConfig config)
    {
        config = null;
        if (string.IsNullOrEmpty(sceneName))
            return false;

        EnsureSectionConfigsIndexed();
        return _sectionConfigsByTargetScene.TryGetValue(sceneName, out config);
    }

    void EnsureConfigForScene(string sceneName)
    {
        if (configOverride != null)
            _config = configOverride;
        else if (TryGetSectionConfigForScene(sceneName, out ProceduralMazeConfig sectionConfig))
            _config = sectionConfig;
        else
        {
            _config = Resources.Load<ProceduralMazeConfig>(ConfigResourceName);
            WarnIfFallbackMismatchesScene(sceneName);
        }
    }

    void WarnIfFallbackMismatchesScene(string sceneName)
    {
        if (_config == null || string.IsNullOrEmpty(sceneName))
            return;
        if (string.Equals(_config.TargetSceneName, sceneName, StringComparison.OrdinalIgnoreCase))
            return;
        if (!IsLikelyProceduralMazeSceneName(sceneName))
            return;

        LogMazeWarningOnce(
            $"maze-config-missing:{sceneName}",
            $"[Maze] No ProceduralMazeConfig under Resources/{SectionConfigsResourcesPath} with Target Scene Name \"{sceneName}\". " +
            $"Using fallback \"{_config.name}\" (Target Scene \"{_config.TargetSceneName}\"). Add a config asset there or assign config Override.",
            this);
    }

    static bool IsLikelyProceduralMazeSceneName(string sceneName)
        => sceneName.StartsWith("Level", StringComparison.OrdinalIgnoreCase);

    bool ShouldManageScene(Scene scene)
    {
        return _config != null
            && _config.EnableGeneration
            && _config.HasMinimumStarterSet
            && scene.IsValid()
            && scene.isLoaded
            && string.Equals(scene.name, _config.TargetSceneName, StringComparison.OrdinalIgnoreCase);
    }

    bool IsServerListening()
    {
        return _networkManager != null && _networkManager.IsListening && _networkManager.IsServer;
    }

    bool IsClientConnected()
    {
        return _networkManager != null && _networkManager.IsListening && !_networkManager.IsServer;
    }

    void RestartSceneRoutine(IEnumerator routine)
    {
        if (_sceneRoutine != null)
            StopCoroutine(_sceneRoutine);

        _sceneRoutine = StartCoroutine(routine);
    }

    void BuildMazeInScene(Scene scene, int seed)
    {
        EnsureConfigForScene(scene.name);

        if (!ShouldManageScene(scene))
            return;

        if (IsDuplicateServerMazeBuild(scene, seed))
        {
            Debug.Log(
                $"[Maze] Skipping duplicate maze build for scene \"{scene.name}\" seed {seed}. " +
                "(Host/server already built this maze; avoids doubling enemies and stacked spawns.)",
                this);
            return;
        }

        if (IsServerListening())
        {
            _lastServerMazeBuildSceneName = scene.name;
            _lastServerMazeBuildSeed = seed;
        }

        _loggedMazeWarnings.Clear();
        _placedCellPrefabs.Clear();
        _exitHallCells.Clear();
        ValidateConfiguredPieceSetup();

        ServerDespawnAllLevelNetworkObjects();
        EnsureDoorStateStoreSpawnedAndCleared();

        GameObject root = GetOrCreateRoot(scene);
        ClearRoot(root.transform);
        root.transform.position = Vector3.zero;

        Vector2Int logicalSize = _config.MazeSize;
        MazeLayout layout = GenerateMazeLayout(seed, logicalSize.x, logicalSize.y);
        MazeCell[,] grid = layout.Cells;
        Vector2Int start = layout.Start;
        Vector2Int exit = FindFarthestCell(grid, start);
        int width = grid.GetLength(0);
        int height = grid.GetLength(1);
        bool multiStartWanted = TryPrepareMultiCellForcedStart(
            grid, start, exit, width, height, seed,
            out HashSet<Vector2Int> reservedForForcedStart,
            out Vector2Int multiStartFootprint);

        if (!multiStartWanted)
        {
            reservedForForcedStart = null;
            multiStartFootprint = Vector2Int.one;
        }

        // Apply forced multi-cell start before interior room stamping mutates the grid; otherwise
        // TryResolve can fail vs TryPrepare, SkipCells never get added, and pieces at (1,0)… overlap MG_Start.
        Dictionary<Vector2Int, InteriorRoomPlacementEntry> forcedStartAnchors = new();
        HashSet<Vector2Int> forcedStartSkips = new();
        bool startAttached = false;
        if (multiStartWanted)
        {
            InteriorRoomBuildPlan forcedStartPlan = new(forcedStartAnchors, forcedStartSkips);
            if (TryAttachForcedStartAsInteriorRoom(
                    forcedStartPlan, grid, start, exit, seed, multiStartFootprint))
                startAttached = true;
            else
                multiStartFootprint = Vector2Int.one;
        }

        // Exit hall: a multi-cell finish piece stamped over a straight run of cells that contains the
        // exit, so the run ends in a long approach corridor. Runs before interior rooms so they can be
        // told to keep clear of it; falls back to the single-cell Special piece when nothing fits.
        Dictionary<Vector2Int, InteriorRoomPlacementEntry> exitHallAnchors = new();
        HashSet<Vector2Int> exitHallSkips = new();
        HashSet<Vector2Int> reservedCells = reservedForForcedStart;
        if (TryAttachExitHall(
                exitHallAnchors, exitHallSkips, grid, start, exit, width, height, seed, reservedCells))
        {
            reservedCells ??= new HashSet<Vector2Int>();
            foreach (Vector2Int cell in _exitHallCells)
                reservedCells.Add(cell);
        }

        InteriorRoomBuildPlan interiorPlan = SelectInteriorRoomPlacements(
            grid, start, exit, seed, width, height, reservedCells);
        if (startAttached)
        {
            foreach (KeyValuePair<Vector2Int, InteriorRoomPlacementEntry> a in forcedStartAnchors)
                interiorPlan.Anchors[a.Key] = a.Value;
            foreach (Vector2Int skip in forcedStartSkips)
                interiorPlan.SkipCells.Add(skip);
        }

        foreach (KeyValuePair<Vector2Int, InteriorRoomPlacementEntry> a in exitHallAnchors)
            interiorPlan.Anchors[a.Key] = a.Value;
        foreach (Vector2Int skip in exitHallSkips)
            interiorPlan.SkipCells.Add(skip);

        Vector2Int? jailDeadEndCell = SelectJailDeadEndCell(
            _config.JailDeadEndPrefab, grid, start, exit, interiorPlan, width, height, seed);
        if (_config.JailDeadEndPrefab != null && !jailDeadEndCell.HasValue)
        {
            LogMazeWarningOnce(
                "jail-dead-end-no-candidate",
                "[Maze] Jail Dead End Prefab is set but no valid dead-end cell was found (need a non-start, non-exit, non-interior one-opening cell). No jail piece spawned.",
                this);
        }

        Transform cellsRoot = CreateChild(root.transform, "Cells");
        Dictionary<Vector2Int, Transform> builtCellRoots = new();
        float cellSize = _config.CellSize;
        float blockerOffset = _config.BlockerOffset > 0f ? _config.BlockerOffset : cellSize * 0.5f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (grid[x, y].Openings == MazeFaceMask.None)
                    continue;

                if (interiorPlan.SkipCells.Contains(new Vector2Int(x, y)))
                    continue;

                Transform cellRoot = CreateChild(cellsRoot, $"Cell_{x}_{y}");
                Vector2Int cell = new(x, y);
                builtCellRoots[cell] = cellRoot;
                bool isInteriorAnchor = interiorPlan.Anchors.TryGetValue(cell, out InteriorRoomPlacementEntry interiorEntry);
                cellRoot.position = isInteriorAnchor
                    ? CellRectCenterWorld(x, y, interiorEntry.Footprint.x, interiorEntry.Footprint.y, cellSize)
                    : CellToWorld(x, y, cellSize);

                bool isStart = x == start.x && y == start.y;
                bool isExit = x == exit.x && y == exit.y;
                BuildCell(
                    cellRoot,
                    grid[x, y].Openings,
                    blockerOffset,
                    isStart,
                    isExit,
                    seed,
                    cell,
                    isInteriorAnchor,
                    interiorEntry,
                    jailDeadEndCell);
            }
        }

        (HashSet<Vector2Int> mazeTrapCells, List<Transform> mazeTrapRoots) =
            TrySpawnMazeTraps(root.transform, grid, builtCellRoots, start, exit, seed, cellSize);
        TrySpawnMazeChests(root.transform, builtCellRoots, seed);
        TrySpawnMazeTeleportOrbs(root.transform, builtCellRoots, seed);
        TrySpawnMazePosters(root.transform, builtCellRoots, seed);
        TrySpawnMazeBatRoosts(root.transform, grid, builtCellRoots, start, exit, jailDeadEndCell, interiorPlan, seed, cellSize);
        TrySpawnMazeRoachColonies(root.transform, grid, builtCellRoots, start, exit, jailDeadEndCell, interiorPlan, seed, cellSize);
        TrySpawnMazeStartFlashlights(root.transform);
        CreateSpawnPoints(root.transform, start, cellSize, multiStartFootprint);
        if (MultiplayerSpawnRegistry.Instance != null)
        {
            MultiplayerSpawnRegistry.Instance.RefreshSpawnPoints();
            MultiplayerSpawnRegistry.Instance.ResetInitialJoinRoundRobin();
        }
        TryRebuildRuntimeNavMesh(root);
        TrySpawnMazeItemPickups(root.transform, builtCellRoots, seed);
        TrySpawnMazeEnemies(root.transform, grid, start, exit, seed, cellSize, interiorPlan, mazeTrapCells, mazeTrapRoots);
        TrySpawnMazeRatBots(root.transform, seed);
        Debug.Log($"[Maze] Built seeded maze {seed} from logical size {logicalSize.x}x{logicalSize.y} into {width}x{height} cells in scene \"{scene.name}\".", this);

        MarkLocalMazeCollidersReadyAndResyncClientPlayer();
        if (IsServerListening())
            BroadcastMazeSeedToRemotePlayerAvatars(seed);
    }

    static bool IsPureNetworkClient() =>
        NetworkManager.Singleton != null
        && NetworkManager.Singleton.IsListening
        && !NetworkManager.Singleton.IsServer;

    public static bool ShouldBlockLocalPlayerUntilMazeReady()
    {
        if (IsLocalMazeCollidersReady)
            return false;
        return IsPureNetworkClient();
    }

    void MarkLocalMazeCollidersReadyAndResyncClientPlayer()
    {
        IsLocalMazeCollidersReady = true;

        if (!IsPureNetworkClient())
            return;

        if (_networkManager != null
            && _networkManager.ConnectedClients.TryGetValue(
                _networkManager.LocalClientId, out NetworkClient client)
            && client.PlayerObject != null)
        {
            CharacterController characterController = client.PlayerObject.GetComponent<CharacterController>();
            if (characterController != null)
            {
                Vector3 p = client.PlayerObject.transform.position;
                Quaternion r = client.PlayerObject.transform.rotation;
                characterController.enabled = false;
                client.PlayerObject.transform.SetPositionAndRotation(p, r);
                Physics.SyncTransforms();
                characterController.enabled = true;
            }

            PlayerController playerController = client.PlayerObject.GetComponent<PlayerController>();
            playerController?.OnClientMazeCollidersBecameReady();
        }

        foreach (OwnerNetworkTransform ownerNetTransform in FindObjectsByType<OwnerNetworkTransform>())
        {
            if (ownerNetTransform != null)
                ownerNetTransform.SnapObserverToLatestNetworkState();
        }

        Physics.SyncTransforms();
    }

    /// <summary>
    /// When the forced start prefab has a multi-cell <see cref="MazePieceDefinition.interiorGridFootprint"/>,
    /// reserves the rectangle and blocks other interior rooms from overlapping it, then
    /// <see cref="TryAttachForcedStartAsInteriorRoom"/> places it as an interior room (centered + skip cells).
    /// </summary>
    bool TryPrepareMultiCellForcedStart(
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        int width,
        int height,
        int seed,
        out HashSet<Vector2Int> reserved,
        out Vector2Int footprint)
    {
        reserved = null;
        footprint = Vector2Int.one;

        if (_config == null || _config.ForcedStartPiecePrefab == null)
            return false;

        if (!MazePieceDefinition.TryGetDefinitionForResolution(_config.ForcedStartPiecePrefab, out MazePieceDefinition def)
            || def.OpenFaces == MazeFaceMask.None)
        {
            return false;
        }

        footprint = def.ResolveInteriorGridFootprint(_config.InteriorRoomGridFootprint);
        if (footprint.x <= 1 && footprint.y <= 1)
            return false;

        if (start.x + footprint.x > width || start.y + footprint.y > height)
        {
            LogMazeWarningOnce(
                "forced-start-footprint-bounds",
                $"[Maze] Forced start footprint {footprint.x}x{footprint.y} does not fit the maze; using single-cell placement.",
                this);
            return false;
        }

        if (ExitLiesInFootprint(exit, start, footprint))
        {
            LogMazeWarningOnce(
                "forced-start-footprint-contains-exit",
                "[Maze] Forced start multi-cell footprint contains the exit cell; other interior room rules apply. Using single-cell start placement for this build.",
                this);
            return false;
        }

        for (int ly = 0; ly < footprint.y; ly++)
        {
            for (int lx = 0; lx < footprint.x; lx++)
            {
                int x = start.x + lx;
                int y = start.y + ly;
                if (grid[x, y].Openings == MazeFaceMask.None)
                {
                    LogMazeWarningOnce(
                        "forced-start-footprint-has-void",
                        "[Maze] Forced start multi-cell footprint includes a void cell; using single-cell start placement.",
                        this);
                    return false;
                }
            }
        }

        bool isExitCell = start == exit;
        if (!MazePieceResolver.TryResolve(
                _config,
                grid[start.x, start.y].Openings,
                isStart: true,
                isExit: isExitCell,
                seed,
                start,
                out _,
                out string resolveReason))
        {
            LogMazeWarningOnce(
                "forced-start-multicell-resolve",
                $"[Maze] Forced start multi-cell: could not resolve piece ({resolveReason ?? "unknown"}); using single-cell start placement.",
                this);
            return false;
        }

        reserved = new HashSet<Vector2Int>(footprint.x * footprint.y);
        for (int ly = 0; ly < footprint.y; ly++)
        {
            for (int lx = 0; lx < footprint.x; lx++)
                reserved.Add(new Vector2Int(start.x + lx, start.y + ly));
        }

        return true;
    }

    static bool ExitLiesInFootprint(Vector2Int exit, Vector2Int anchor, Vector2Int fp)
    {
        return exit.x >= anchor.x && exit.x < anchor.x + fp.x
            && exit.y >= anchor.y && exit.y < anchor.y + fp.y;
    }

    bool TryAttachForcedStartAsInteriorRoom(
        InteriorRoomBuildPlan plan,
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        int seed,
        Vector2Int footprint)
    {
        GameObject prefab = _config != null ? _config.ForcedStartPiecePrefab : null;
        if (prefab == null
            || !MazePieceDefinition.TryGetDefinitionForResolution(prefab, out MazePieceDefinition definition))
        {
            LogMazeWarningOnce(
                "forced-start-attach-failed",
                "[Maze] Forced start interior anchor: prefab is missing a MazePieceDefinition.",
                this);
            return false;
        }

        int width = grid.GetLength(0);
        int height = grid.GetLength(1);
        if (!TryStampInteriorRoomWithPrefab(
                grid,
                start,
                exit,
                start,
                footprint,
                prefab,
                definition,
                width,
                height,
                out InteriorRoomPlacementEntry entry,
                out Dictionary<Vector2Int, MazeFaceMask> gridChanges,
                out _,
                out string failureReason))
        {
            LogMazeWarningOnce(
                "forced-start-attach-failed",
                $"[Maze] Forced start interior anchor: stamp failed ({failureReason ?? "unknown"}).",
                this);
            return false;
        }

        foreach (KeyValuePair<Vector2Int, MazeFaceMask> kvp in gridChanges)
            grid[kvp.Key.x, kvp.Key.y].Openings = kvp.Value;

        for (int ly = 0; ly < footprint.y; ly++)
        {
            for (int lx = 0; lx < footprint.x; lx++)
            {
                if (lx == 0 && ly == 0)
                    continue;

                plan.SkipCells.Add(new Vector2Int(start.x + lx, start.y + ly));
            }
        }

        plan.Anchors[start] = entry;
        return true;
    }

    /// <summary>
    /// Stamps <see cref="ProceduralMazeConfig.ExitHallPiecePrefab"/> — a 1×N straight finish piece —
    /// over a run of cells containing the exit cell, so the maze ends in a long approach hallway rather
    /// than a single 6m room. The prefab is authored running +Z with its mouth (its one open face) on
    /// the −Z end and its pivot at the centre of the footprint rect, exactly like an interior room.
    ///
    /// Every anchor offset along both axes and both mouth ends is tried; the candidate that orphans the
    /// fewest maze cells wins, with a seeded shuffle breaking ties so a level does not always favour the
    /// same orientation. Returns false (and leaves the grid untouched) when the prefab is unset,
    /// mis-authored, or no run fits — the exit cell then builds from the normal Special pool.
    /// </summary>
    bool TryAttachExitHall(
        Dictionary<Vector2Int, InteriorRoomPlacementEntry> anchors,
        HashSet<Vector2Int> skipCells,
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        int width,
        int height,
        int seed,
        HashSet<Vector2Int> reservedCells)
    {
        GameObject prefab = _config != null ? _config.ExitHallPiecePrefab : null;
        if (prefab == null)
            return false;

        if (!MazePieceDefinition.TryGetDefinitionForResolution(prefab, out MazePieceDefinition definition))
        {
            LogMazeWarningOnce(
                "exit-hall-no-definition",
                $"[Maze] Exit Hall Piece Prefab \"{prefab.name}\" has no MazePieceDefinition; using the single-cell finish piece.",
                this);
            return false;
        }

        Vector2Int authored = definition.ResolveInteriorGridFootprint(_config.InteriorRoomGridFootprint);
        int length = Mathf.Max(authored.x, authored.y);
        if (Mathf.Min(authored.x, authored.y) != 1 || length < 2)
        {
            LogMazeWarningOnce(
                "exit-hall-footprint",
                $"[Maze] Exit Hall Piece Prefab \"{prefab.name}\" needs an Interior Grid Footprint of 1×N or N×1 "
                + $"(it is {authored.x}×{authored.y}); using the single-cell finish piece.",
                this);
            return false;
        }

        // One entry per (orientation, offset-of-the-exit-cell-along-the-hall). quarterTurns follows the
        // same clockwise convention as MazeFaceMaskUtility.Rotate, so 1 turn sends the +Z hall to +X.
        List<ExitHallCandidate> candidates = new(4 * length);
        for (int offset = 0; offset < length; offset++)
        {
            Vector2Int alongZ = new(exit.x, exit.y - offset);
            Vector2Int alongX = new(exit.x - offset, exit.y);
            candidates.Add(new ExitHallCandidate(0, alongZ, new Vector2Int(1, length), new Vector2Int(0, 0), MazeFaceMask.South));
            candidates.Add(new ExitHallCandidate(2, alongZ, new Vector2Int(1, length), new Vector2Int(0, length - 1), MazeFaceMask.North));
            candidates.Add(new ExitHallCandidate(1, alongX, new Vector2Int(length, 1), new Vector2Int(0, 0), MazeFaceMask.West));
            candidates.Add(new ExitHallCandidate(3, alongX, new Vector2Int(length, 1), new Vector2Int(length - 1, 0), MazeFaceMask.East));
        }

        ShuffleList(candidates, new System.Random(MixSeed(seed, unchecked((int)0xEE817A11))));

        ExitHallCandidate best = default;
        Dictionary<Vector2Int, MazeFaceMask> bestChanges = null;
        MazeFaceMask bestExteriorMask = MazeFaceMask.None;
        int bestOrphans = int.MaxValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            ExitHallCandidate candidate = candidates[i];
            if (!IsExitHallRectUsable(candidate, start, width, height, reservedCells))
                continue;

            HashSet<InteriorDoorwaySpec> doorways = new()
            {
                new InteriorDoorwaySpec(candidate.MouthCell, candidate.MouthDirection),
            };

            if (!TryComputeStampPlan(
                    grid,
                    start,
                    exit,
                    candidate.Anchor,
                    candidate.Footprint,
                    doorways,
                    width,
                    height,
                    out Dictionary<Vector2Int, MazeFaceMask> proposed,
                    out MazeFaceMask exteriorMask,
                    out int orphans))
            {
                continue;
            }

            if (orphans >= bestOrphans)
                continue;

            best = candidate;
            bestChanges = proposed;
            bestExteriorMask = exteriorMask;
            bestOrphans = orphans;

            if (bestOrphans == 0)
                break;
        }

        if (bestChanges == null)
        {
            LogMazeWarningOnce(
                "exit-hall-no-placement",
                $"[Maze] No straight run of {length} cells through the exit cell could host \"{prefab.name}\" "
                + "(out of bounds, over the start cell, or it would have cut the maze). Using the single-cell finish piece.",
                this);
            return false;
        }

        foreach (KeyValuePair<Vector2Int, MazeFaceMask> change in bestChanges)
            grid[change.Key.x, change.Key.y].Openings = change.Value;

        for (int ly = 0; ly < best.Footprint.y; ly++)
        {
            for (int lx = 0; lx < best.Footprint.x; lx++)
            {
                Vector2Int cell = new(best.Anchor.x + lx, best.Anchor.y + ly);
                _exitHallCells.Add(cell);
                if (lx != 0 || ly != 0)
                    skipCells.Add(cell);
            }
        }

        Quaternion rotation = Quaternion.Euler(0f, best.QuarterTurns * 90f + definition.YawOffset, 0f);
        anchors[best.Anchor] = new InteriorRoomPlacementEntry(
            new MazePieceMatch(prefab, definition, rotation, bestExteriorMask, definition.UseClosedFaceCaps),
            best.Footprint);

        if (bestOrphans > 0)
        {
            Debug.Log(
                $"[Maze] Exit hall \"{prefab.name}\" placed at ({best.Anchor.x}, {best.Anchor.y}) "
                + $"{best.Footprint.x}×{best.Footprint.y}; {bestOrphans} maze cell(s) it cut off were pruned.",
                this);
        }

        return true;
    }

    /// <summary>
    /// Bounds/overlap test for an exit-hall rect. Unlike <see cref="IsRectangleStampable"/> this one
    /// WANTS to cover the exit cell — the hall replaces it — but still refuses the start cell and any
    /// cell already reserved by the forced start room.
    /// </summary>
    static bool IsExitHallRectUsable(
        ExitHallCandidate candidate,
        Vector2Int start,
        int width,
        int height,
        HashSet<Vector2Int> reservedCells)
    {
        Vector2Int anchor = candidate.Anchor;
        Vector2Int footprint = candidate.Footprint;
        if (anchor.x < 0 || anchor.y < 0 || anchor.x + footprint.x > width || anchor.y + footprint.y > height)
            return false;

        for (int ly = 0; ly < footprint.y; ly++)
        {
            for (int lx = 0; lx < footprint.x; lx++)
            {
                Vector2Int cell = new(anchor.x + lx, anchor.y + ly);
                if (cell == start)
                    return false;
                if (reservedCells != null && reservedCells.Contains(cell))
                    return false;
            }
        }

        return true;
    }

    readonly struct ExitHallCandidate
    {
        public ExitHallCandidate(
            int quarterTurns,
            Vector2Int anchor,
            Vector2Int footprint,
            Vector2Int mouthCell,
            MazeFaceMask mouthDirection)
        {
            QuarterTurns = quarterTurns;
            Anchor = anchor;
            Footprint = footprint;
            MouthCell = mouthCell;
            MouthDirection = mouthDirection;
        }

        public int QuarterTurns { get; }
        public Vector2Int Anchor { get; }
        public Vector2Int Footprint { get; }
        /// <summary>Footprint-local cell that holds the hall's one open face.</summary>
        public Vector2Int MouthCell { get; }
        public MazeFaceMask MouthDirection { get; }
    }

    bool IsDuplicateServerMazeBuild(Scene scene, int seed)
    {
        if (!IsServerListening())
            return false;

        return _lastServerMazeBuildSeed == seed
            && !string.IsNullOrEmpty(_lastServerMazeBuildSceneName)
            && string.Equals(_lastServerMazeBuildSceneName, scene.name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Server-only. Ensures the singleton <see cref="DoorNetworkStateStore"/> (which replicates procedural door
    /// open/locked state to clients, including late joiners) is spawned, then clears its entries so door state is
    /// scoped per level. The store persists across the NGO scene switch as infrastructure; clearing on each build
    /// prevents a previous level's door states from bleeding into the next.
    /// </summary>
    void EnsureDoorStateStoreSpawnedAndCleared()
    {
        if (!IsServerListening())
            return;

        if (DoorNetworkStateStore.Instance == null)
        {
            GameObject prefab = FindRegisteredNetworkPrefabWithComponent<DoorNetworkStateStore>();
            if (prefab == null)
            {
                LogMazeWarningOnce(
                    "door-state-store-prefab-missing",
                    "[Maze] DoorStateStore network prefab not found in the NetworkManager prefab list. Procedural jail/key door open state will not replicate to clients. Add DoorStateStore.prefab (with DoorNetworkStateStore + NetworkObject) to Resources/DefaultNetworkPrefabs.",
                    this);
                return;
            }

            GameObject instance = Instantiate(prefab);
            if (instance.TryGetComponent(out NetworkObject storeNetObj))
                storeNetObj.Spawn();
            else
                Destroy(instance);
        }

        DoorNetworkStateStore.ServerClear();
        // ConsumedItemNetworkStore and CarnivalRadioNetworkStore ride the same infrastructure NetworkObject as the
        // door store, so they are already spawned by the block above; just scope their per-level state to this level.
        ConsumedItemNetworkStore.ServerClear();
        CarnivalRadioNetworkStore.ServerClear();
    }

    GameObject FindRegisteredNetworkPrefabWithComponent<T>() where T : Component
    {
        NetworkConfig config = _networkManager != null ? _networkManager.NetworkConfig : null;
        if (config == null || config.Prefabs == null || config.Prefabs.NetworkPrefabsLists == null)
            return null;

        foreach (NetworkPrefabsList list in config.Prefabs.NetworkPrefabsLists)
        {
            if (list == null || list.PrefabList == null)
                continue;
            foreach (NetworkPrefab entry in list.PrefabList)
            {
                if (entry != null && entry.Prefab != null && entry.Prefab.GetComponent<T>() != null)
                    return entry.Prefab;
            }
        }

        return null;
    }

    /// <summary>
    /// Server-only. Despawns EVERY runtime-spawned level NetworkObject — enemies (incl. the Jailor), traps, chests,
    /// carnival props, keyed-door objects, thrown/held items, etc. — so nothing from the previous section bleeds into
    /// the next. NGO does NOT destroy dynamically-spawned NetworkObjects on a <see cref="LoadSceneMode.Single"/>
    /// switch when they were spawned with the default <c>destroyWithScene=false</c>; it migrates them into the new
    /// scene, so level content must be despawned explicitly by the server. Player objects (kept across the
    /// connection) and the <see cref="DoorNetworkStateStore"/> singleton (infrastructure) are preserved.
    ///
    /// Enumerating the live spawn set (rather than a hand-maintained type list) makes this exhaustive and
    /// automatically correct as new enemy/trap/prop types are added across all four maze sections. Running it at the
    /// start of every server maze build also guarantees <see cref="MazePieceNetworkRigidbodySpawn"/>'s dedup runs
    /// against a genuinely clean scene.
    /// </summary>
    public void ServerDespawnAllLevelNetworkObjects()
    {
        if (_networkManager == null || !_networkManager.IsListening || !_networkManager.IsServer)
            return;

        var spawnManager = _networkManager.SpawnManager;
        if (spawnManager == null || spawnManager.SpawnedObjects == null)
            return;

        List<NetworkObject> toDespawn = new();
        foreach (KeyValuePair<ulong, NetworkObject> pair in spawnManager.SpawnedObjects)
        {
            NetworkObject networkObject = pair.Value;
            if (networkObject == null || !networkObject.IsSpawned)
                continue;

            // Preserve the per-connection player objects and the door-state infrastructure singleton.
            if (networkObject.IsPlayerObject)
                continue;
            if (networkObject.GetComponent<DoorNetworkStateStore>() != null)
                continue;

            toDespawn.Add(networkObject);
        }

        for (int i = 0; i < toDespawn.Count; i++)
        {
            NetworkObject networkObject = toDespawn[i];
            if (networkObject != null && networkObject.IsSpawned)
                networkObject.Despawn(true);
        }
    }

    GameObject GetOrCreateRoot(Scene scene)
    {
        foreach (GameObject rootObject in scene.GetRootGameObjects())
        {
            if (rootObject.name == _config.GeneratedRootName)
                return rootObject;
        }

        GameObject root = new(_config.GeneratedRootName);
        SceneManager.MoveGameObjectToScene(root, scene);
        return root;
    }

    void ClearRoot(Transform root)
    {
        for (int i = root.childCount - 1; i >= 0; i--)
        {
            Transform child = root.GetChild(i);
            if (Application.isPlaying)
            {
                DespawnNetworkObjectsUnder(child.gameObject);
                Destroy(child.gameObject);
            }
            else
                DestroyImmediate(child.gameObject);
        }
    }

    static void DespawnNetworkObjectsUnder(GameObject root)
    {
        if (root == null)
            return;

        NetworkObject[] networkObjects = root.GetComponentsInChildren<NetworkObject>(true);
        for (int i = 0; i < networkObjects.Length; i++)
        {
            NetworkObject networkObject = networkObjects[i];
            if (networkObject != null && networkObject.IsSpawned)
                networkObject.Despawn(true);
        }
    }

    Transform CreateChild(Transform parent, string childName)
    {
        GameObject child = new(childName);
        child.transform.SetParent(parent, false);
        return child.transform;
    }

    Vector3 CellToWorld(int x, int y, float cellSize)
    {
        return _config.Origin + new Vector3(x * cellSize, 0f, y * cellSize);
    }

    Vector3 CellRectCenterWorld(int minGridX, int minGridY, int footprintCellsX, int footprintCellsZ, float cellSize)
    {
        float cx = minGridX + (footprintCellsX - 1) * 0.5f;
        float cz = minGridY + (footprintCellsZ - 1) * 0.5f;
        return _config.Origin + new Vector3(cx * cellSize, 0f, cz * cellSize);
    }

    /// <summary>
    /// Horizontal origin for enemy/hunter spawn probes: interior anchor cells use the footprint center (matching built geometry).
    /// Skip cells are excluded from candidates beforehand.
    /// </summary>
    Vector3 ResolveMazeEnemySpawnHorizontalCellOrigin(
        Vector2Int cell,
        float cellSize,
        InteriorRoomBuildPlan interiorPlan)
    {
        if (interiorPlan.Anchors.TryGetValue(cell, out InteriorRoomPlacementEntry entry))
            return CellRectCenterWorld(cell.x, cell.y, entry.Footprint.x, entry.Footprint.y, cellSize);

        return CellToWorld(cell.x, cell.y, cellSize);
    }

    MazeLayout GenerateMazeLayout(int seed, int width, int height)
    {
        if (!_config.ForceStartCellSingleOpening)
            return GenerateMazeLayoutOnce(seed, width, height);

        const int maxAttempts = 128;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int trySeed = MixSeed(seed, attempt);
            System.Random random = new(trySeed);
            MazeCell[,] coreMaze = GenerateCoreMaze(random, width, height);
            if (MazeFaceMaskUtility.CountOpenFaces(coreMaze[0, 0].Openings) != 1)
                continue;

            int[] horizontalRuns = GenerateCorridorRuns(random, Mathf.Max(0, width - 1));
            int[] verticalRuns = GenerateCorridorRuns(random, Mathf.Max(0, height - 1));
            ClampCorridorRunChains(coreMaze, horizontalRuns, verticalRuns);
            MazeCell[,] expandedMaze = ExpandMaze(coreMaze, horizontalRuns, verticalRuns);
            return new MazeLayout(expandedMaze, Vector2Int.zero);
        }

        Debug.LogWarning(
            $"[Maze] Force Start Cell Single Opening: no single-opening start at (0,0) after {maxAttempts} tries for seed {seed}. Using default layout (forced one-way start prefab may fall back).",
            this);

        return GenerateMazeLayoutOnce(seed, width, height);
    }

    MazeLayout GenerateMazeLayoutOnce(int seed, int width, int height)
    {
        System.Random random = new(seed);
        MazeCell[,] coreMaze = GenerateCoreMaze(random, width, height);
        int[] horizontalRuns = GenerateCorridorRuns(random, Mathf.Max(0, width - 1));
        int[] verticalRuns = GenerateCorridorRuns(random, Mathf.Max(0, height - 1));
        ClampCorridorRunChains(coreMaze, horizontalRuns, verticalRuns);
        MazeCell[,] expandedMaze = ExpandMaze(coreMaze, horizontalRuns, verticalRuns);
        return new MazeLayout(expandedMaze, Vector2Int.zero);
    }

    void ClampCorridorRunChains(MazeCell[,] coreMaze, int[] horizontalRuns, int[] verticalRuns)
    {
        int cap = _config.MaxStraightCellsPerCorridorChain;
        if (cap <= 0)
            return;

        int coreWidth = coreMaze.GetLength(0);
        int coreHeight = coreMaze.GetLength(1);

        for (int y = 0; y < coreHeight; y++)
        {
            int x = 0;
            while (x < coreWidth - 1)
            {
                if ((coreMaze[x, y].Openings & MazeFaceMask.East) == 0)
                {
                    x++;
                    continue;
                }

                List<int> chain = new();
                int cx = x;
                while (cx < coreWidth - 1 && (coreMaze[cx, y].Openings & MazeFaceMask.East) != 0)
                {
                    chain.Add(cx);
                    cx++;
                }

                ReduceCorridorChainSum(horizontalRuns, chain, cap);
                x = cx;
            }
        }

        for (int x = 0; x < coreWidth; x++)
        {
            int y = 0;
            while (y < coreHeight - 1)
            {
                if ((coreMaze[x, y].Openings & MazeFaceMask.North) == 0)
                {
                    y++;
                    continue;
                }

                List<int> chain = new();
                int cy = y;
                while (cy < coreHeight - 1 && (coreMaze[x, cy].Openings & MazeFaceMask.North) != 0)
                {
                    chain.Add(cy);
                    cy++;
                }

                ReduceCorridorChainSum(verticalRuns, chain, cap);
                y = cy;
            }
        }
    }

    void ReduceCorridorChainSum(int[] runs, List<int> edgeIndices, int maxSum)
    {
        if (edgeIndices == null || edgeIndices.Count == 0)
            return;

        int floor = _config.MinStraightRun;
        while (true)
        {
            int sum = 0;
            int bestIdx = -1;
            int bestVal = -1;
            for (int i = 0; i < edgeIndices.Count; i++)
            {
                int ei = edgeIndices[i];
                int v = runs[ei];
                sum += v;
                if (v > bestVal)
                {
                    bestVal = v;
                    bestIdx = ei;
                }
            }

            if (sum <= maxSum || bestIdx < 0 || bestVal <= floor)
                return;

            runs[bestIdx]--;
        }
    }

    static int MixSeed(int seed, int attempt)
    {
        unchecked
        {
            uint x = (uint)seed;
            x ^= (uint)attempt * 0x9e3779b9u;
            x ^= x >> 16;
            x *= 0x85ebca6bu;
            x ^= x >> 13;
            return (int)x;
        }
    }

    MazeCell[,] GenerateCoreMaze(System.Random random, int width, int height)
    {
        MazeCell[,] cells = new MazeCell[width, height];
        bool[,] visited = new bool[width, height];
        List<FrontierNode> frontier = new();

        Vector2Int start = Vector2Int.zero;
        visited[start.x, start.y] = true;
        frontier.Add(new FrontierNode(start, MazeFaceMask.None, 0));

        int maxConsecutive = _config.MaxConsecutiveSameDirection;
        float randomFrontierChance = _config.RandomFrontierSelectionChance;

        while (frontier.Count > 0)
        {
            int frontierIndex = random.NextDouble() < randomFrontierChance
                ? random.Next(frontier.Count)
                : frontier.Count - 1;

            FrontierNode currentNode = frontier[frontierIndex];
            Vector2Int current = currentNode.Cell;
            List<DirectionStep> available = GetAvailableSteps(current, visited, width, height);

            if (maxConsecutive > 0 && currentNode.StraightRunLength >= maxConsecutive && available.Count > 1)
                available.RemoveAll(s => s.Direction == currentNode.IncomingDirection);

            if (available.Count == 0)
            {
                frontier.RemoveAt(frontierIndex);
                continue;
            }

            DirectionStep choice = available[random.Next(available.Count)];
            Vector2Int next = new(current.x + choice.Dx, current.y + choice.Dy);

            cells[current.x, current.y].Openings |= choice.Direction;
            cells[next.x, next.y].Openings |= MazeFaceMaskUtility.Opposite(choice.Direction);
            visited[next.x, next.y] = true;

            int nextStraightRunLength = choice.Direction == currentNode.IncomingDirection
                ? currentNode.StraightRunLength + 1
                : 1;
            frontier.Add(new FrontierNode(next, choice.Direction, nextStraightRunLength));
        }

        return cells;
    }

    int[] GenerateCorridorRuns(System.Random random, int count)
    {
        if (count <= 0)
            return Array.Empty<int>();

        int[] runs = new int[count];
        for (int i = 0; i < count; i++)
            runs[i] = random.Next(_config.MinStraightRun, _config.MaxStraightRun + 1);

        return runs;
    }

    MazeCell[,] ExpandMaze(MazeCell[,] coreMaze, int[] horizontalRuns, int[] verticalRuns)
    {
        int coreWidth = coreMaze.GetLength(0);
        int coreHeight = coreMaze.GetLength(1);
        int[] xPositions = GetNodePositions(coreWidth, horizontalRuns);
        int[] yPositions = GetNodePositions(coreHeight, verticalRuns);
        int expandedWidth = xPositions[coreWidth - 1] + 1;
        int expandedHeight = yPositions[coreHeight - 1] + 1;
        MazeCell[,] expanded = new MazeCell[expandedWidth, expandedHeight];

        for (int y = 0; y < coreHeight; y++)
        {
            for (int x = 0; x < coreWidth; x++)
            {
                int expandedX = xPositions[x];
                int expandedY = yPositions[y];
                MazeCell cell = coreMaze[x, y];
                expanded[expandedX, expandedY].Openings |= cell.Openings;

                if (x < coreWidth - 1 && (cell.Openings & MazeFaceMask.East) != 0)
                {
                    int runLength = horizontalRuns[x];
                    for (int step = 1; step <= runLength; step++)
                        expanded[expandedX + step, expandedY].Openings |= MazeFaceMask.East | MazeFaceMask.West;
                }

                if (y < coreHeight - 1 && (cell.Openings & MazeFaceMask.North) != 0)
                {
                    int runLength = verticalRuns[y];
                    for (int step = 1; step <= runLength; step++)
                        expanded[expandedX, expandedY + step].Openings |= MazeFaceMask.North | MazeFaceMask.South;
                }
            }
        }

        return expanded;
    }

    static int[] GetNodePositions(int nodeCount, int[] corridorRuns)
    {
        int[] positions = new int[nodeCount];
        int current = 0;
        for (int i = 0; i < nodeCount; i++)
        {
            positions[i] = current;
            if (i < corridorRuns.Length)
                current += 1 + corridorRuns[i];
        }

        return positions;
    }

    List<DirectionStep> GetAvailableSteps(Vector2Int current, bool[,] visited, int width, int height)
    {
        List<DirectionStep> available = new();
        for (int i = 0; i < Steps.Length; i++)
        {
            DirectionStep step = Steps[i];
            int nextX = current.x + step.Dx;
            int nextY = current.y + step.Dy;

            if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height)
                continue;

            if (visited[nextX, nextY])
                continue;

            available.Add(step);
        }

        return available;
    }

    Vector2Int FindFarthestCell(MazeCell[,] cells, Vector2Int start)
    {
        int width = cells.GetLength(0);
        int height = cells.GetLength(1);
        bool[,] visited = new bool[width, height];
        Queue<Vector2Int> queue = new();
        queue.Enqueue(start);
        visited[start.x, start.y] = true;
        Vector2Int farthest = start;

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            farthest = current;

            foreach (DirectionStep step in Steps)
            {
                if ((cells[current.x, current.y].Openings & step.Direction) == 0)
                    continue;

                int nextX = current.x + step.Dx;
                int nextY = current.y + step.Dy;
                if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height || visited[nextX, nextY])
                    continue;

                visited[nextX, nextY] = true;
                queue.Enqueue(new Vector2Int(nextX, nextY));
            }
        }

        return farthest;
    }

    InteriorRoomBuildPlan SelectInteriorRoomPlacements(
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        int seed,
        int width,
        int height,
        HashSet<Vector2Int> cellsReservedByForcedStart)
    {
        Dictionary<Vector2Int, InteriorRoomPlacementEntry> anchors = new();
        HashSet<Vector2Int> skipCells = new();
        GameObject[] pool = _config.InteriorRoomPrefabs;
        int want = _config.InteriorRoomCount;
        int minApart = _config.InteriorRoomMinChebyshevSeparation;
        Vector2Int configFootprint = _config.InteriorRoomGridFootprint;

        if (want <= 0 || pool == null)
            return new InteriorRoomBuildPlan(anchors, skipCells);

        bool hasAnyPrefab = false;
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i] != null)
            {
                hasAnyPrefab = true;
                break;
            }
        }

        if (!hasAnyPrefab)
            return new InteriorRoomBuildPlan(anchors, skipCells);

        List<Vector2Int> candidateAnchors = new();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (grid[x, y].Openings == MazeFaceMask.None)
                    continue;

                if (x == start.x && y == start.y)
                    continue;

                if (x == exit.x && y == exit.y)
                    continue;

                if (!HasAnchorWithFittingPrefab(
                        grid, start, exit, x, y, pool, configFootprint, width, height, cellsReservedByForcedStart))
                    continue;

                candidateAnchors.Add(new Vector2Int(x, y));
            }
        }

        if (candidateAnchors.Count == 0)
        {
            LogMazeWarningOnce(
                "interior-room-no-candidates",
                "[Maze] No candidate anchors found for interior rooms. " +
                "Every potential anchor either overlapped the start/exit cell or could not fit any prefab footprint inside the maze bounds.",
                this);
            return new InteriorRoomBuildPlan(anchors, skipCells);
        }

        ShuffleList(candidateAnchors, new System.Random(MixSeed(seed, unchecked((int)0x4C7552D3))));

        List<(Vector2Int min, Vector2Int size)> placedRects = new();
        int[] placedPerPrefab = new int[pool.Length];
        bool[] prefabFailedEverywhere = new bool[pool.Length];

        // Iteratively pick the anchor with the fewest orphaned cells from the remaining
        // candidates. Re-scoring after each placement is necessary because placing a room
        // mutates the grid (severs/prunes other corridors) and changes how every other
        // anchor would orphan cells.
        while (anchors.Count < want)
        {
            List<int> eligiblePrefabIndices = SelectEligibleInteriorRoomPrefabIndices(
                pool, placedPerPrefab, prefabFailedEverywhere, out bool eligibleIsSingleCapped);
            if (eligiblePrefabIndices.Count == 0)
                break;

            Vector2Int? bestAnchor = null;
            InteriorRoomPlacementEntry bestEntry = default;
            int bestPoolIndex = -1;
            Vector2Int bestFootprint = default;
            Dictionary<Vector2Int, MazeFaceMask> bestGridChanges = null;
            int bestOrphans = int.MaxValue;

            foreach (Vector2Int anchor in candidateAnchors)
            {
                if (anchors.ContainsKey(anchor))
                    continue;

                bool tooCloseEarly = false;
                for (int i = 0; i < placedRects.Count; i++)
                {
                    (Vector2Int pMin, Vector2Int pSize) = placedRects[i];
                    if (MinChebyshevBetweenAxisAlignedRects(anchor, configFootprint, pMin, pSize) < minApart)
                    {
                        tooCloseEarly = true;
                        break;
                    }
                }

                if (tooCloseEarly)
                    continue;

                if (!TryStampInteriorRoomAtAnchor(
                        grid,
                        start,
                        exit,
                        anchor,
                        pool,
                        eligiblePrefabIndices,
                        configFootprint,
                        seed,
                        width,
                        height,
                        cellsReservedByForcedStart,
                        out InteriorRoomPlacementEntry entry,
                        out int entryPoolIndex,
                        out Vector2Int footprint,
                        out Dictionary<Vector2Int, MazeFaceMask> gridChanges,
                        out int orphans,
                        out _))
                    continue;

                // Reject candidates whose stamp would orphan an already-placed interior room's
                // anchor: the connectivity pass prunes that cell to None, so the earlier room's
                // piece silently drops at build time and leaves its footprint as an empty gap.
                // (The main room is placed first, so it is the usual victim of a later small room.)
                bool orphansPlacedRoom = false;
                foreach (Vector2Int placedAnchor in anchors.Keys)
                {
                    if (gridChanges.TryGetValue(placedAnchor, out MazeFaceMask placedMask)
                        && placedMask == MazeFaceMask.None)
                    {
                        orphansPlacedRoom = true;
                        break;
                    }
                }

                if (orphansPlacedRoom)
                    continue;

                bool tooClose = false;
                for (int i = 0; i < placedRects.Count; i++)
                {
                    (Vector2Int pMin, Vector2Int pSize) = placedRects[i];
                    if (MinChebyshevBetweenAxisAlignedRects(anchor, footprint, pMin, pSize) < minApart)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (tooClose)
                    continue;

                if (orphans < bestOrphans)
                {
                    bestOrphans = orphans;
                    bestAnchor = anchor;
                    bestEntry = entry;
                    bestPoolIndex = entryPoolIndex;
                    bestFootprint = footprint;
                    bestGridChanges = gridChanges;

                    if (bestOrphans == 0)
                        break;
                }
            }

            if (bestAnchor == null)
            {
                // A capped prefab that fits nowhere must not dead-lock the rest of the pool
                // (e.g. the big main room failing on a cramped seed still lets small rooms try).
                if (eligibleIsSingleCapped)
                {
                    int failedIndex = eligiblePrefabIndices[0];
                    LogMazeWarningOnce(
                        $"interior-room-capped-unplaceable:{failedIndex}",
                        $"[Maze] Capped interior room prefab \"{pool[failedIndex].name}\" could not be placed at any candidate anchor this build; continuing with the rest of the pool.",
                        this);
                    prefabFailedEverywhere[failedIndex] = true;
                    continue;
                }

                break;
            }

            Vector2Int chosen = bestAnchor.Value;
            foreach (KeyValuePair<Vector2Int, MazeFaceMask> kvp in bestGridChanges)
                grid[kvp.Key.x, kvp.Key.y].Openings = kvp.Value;

            for (int ly = 0; ly < bestFootprint.y; ly++)
            {
                for (int lx = 0; lx < bestFootprint.x; lx++)
                {
                    if (lx == 0 && ly == 0)
                        continue;

                    skipCells.Add(new Vector2Int(chosen.x + lx, chosen.y + ly));
                }
            }

            anchors[chosen] = bestEntry;
            placedRects.Add((chosen, bestFootprint));
            if (bestPoolIndex >= 0)
                placedPerPrefab[bestPoolIndex]++;
        }

        if (anchors.Count < want)
        {
            LogMazeWarningOnce(
                $"interior-room-undershoot:{want}:{anchors.Count}",
                $"[Maze] Requested {want} interior room(s) but only placed {anchors.Count}. " +
                $"Tried {candidateAnchors.Count} candidate anchor(s). " +
                "Consider adding more doorway markers, lowering Interior Room Count, lowering Min Chebyshev Separation, or raising Interior Room Prefab Counts caps.",
                this);
        }

        if (anchors.Count > 0)
        {
            string anchorList = string.Join(", ", anchors.Keys);
            Debug.Log($"[Maze] Interior rooms placed: {anchors.Count}/{want} at anchors [{anchorList}].", this);
        }

        return new InteriorRoomBuildPlan(anchors, skipCells);
    }

    /// <summary>
    /// Pool indices allowed for the next interior room placement. Prefabs with a positive
    /// <see cref="ProceduralMazeConfig.ResolveInteriorRoomPrefabCap"/> place first, one pool slot at a
    /// time in list order, until each cap is met; afterwards every uncapped prefab competes as before.
    /// <paramref name="singleCapped"/> reports that mode so a nowhere-fitting capped prefab can be
    /// skipped instead of ending placement for the whole pool.
    /// </summary>
    List<int> SelectEligibleInteriorRoomPrefabIndices(
        GameObject[] pool,
        int[] placedPerPrefab,
        bool[] prefabFailedEverywhere,
        out bool singleCapped)
    {
        singleCapped = false;
        List<int> eligible = new();
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i] == null || prefabFailedEverywhere[i])
                continue;

            int cap = _config.ResolveInteriorRoomPrefabCap(i);
            if (cap <= 0)
                continue;

            if (placedPerPrefab[i] < cap)
            {
                eligible.Add(i);
                singleCapped = true;
                return eligible;
            }
        }

        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i] == null || prefabFailedEverywhere[i])
                continue;

            if (_config.ResolveInteriorRoomPrefabCap(i) <= 0)
                eligible.Add(i);
        }

        return eligible;
    }

    bool HasAnchorWithFittingPrefab(
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        int anchorX,
        int anchorY,
        GameObject[] pool,
        Vector2Int configFootprint,
        int width,
        int height,
        HashSet<Vector2Int> cellsReservedByForcedStart)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            GameObject prefab = pool[i];
            if (prefab == null)
                continue;

            if (!MazePieceDefinition.TryGetDefinitionForResolution(prefab, out MazePieceDefinition definition))
                continue;

            Vector2Int fp = definition.ResolveInteriorGridFootprint(configFootprint);
            if (IsRectangleStampable(
                    grid, anchorX, anchorY, fp.x, fp.y, width, height, start, exit, cellsReservedByForcedStart))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A rectangle is stampable if every cell is in bounds and the footprint does not overlap
    /// the start or exit cell. Holes (Openings == None) inside the footprint are allowed:
    /// <see cref="TryStampInteriorRoomAtAnchor"/> carves the room on top of them and the
    /// connectivity check guarantees the start↔exit path still works (orphaned cells are pruned).
    /// </summary>
    static bool IsRectangleStampable(
        MazeCell[,] grid,
        int minX,
        int minY,
        int fw,
        int fh,
        int width,
        int height,
        Vector2Int start,
        Vector2Int exit,
        HashSet<Vector2Int> cellsReservedByForcedStart)
    {
        if (fw < 1 || fh < 1 || minX < 0 || minY < 0 || minX + fw > width || minY + fh > height)
            return false;

        for (int ly = 0; ly < fh; ly++)
        {
            for (int lx = 0; lx < fw; lx++)
            {
                int x = minX + lx;
                int y = minY + ly;

                if (x == start.x && y == start.y)
                    return false;

                if (x == exit.x && y == exit.y)
                    return false;

                if (cellsReservedByForcedStart != null && cellsReservedByForcedStart.Contains(new Vector2Int(x, y)))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Picks the first eligible prefab/rotation that can be stamped at <paramref name="anchor"/> while
    /// keeping every visited cell reachable from <paramref name="start"/>. Returns the proposed
    /// grid changes in <paramref name="gridChanges"/> so the caller can apply them after the
    /// neighbor-separation check passes. <paramref name="eligiblePoolIndices"/> restricts which pool
    /// slots may be used (per-prefab cap budgets); <paramref name="poolIndexUsed"/> reports the winner.
    /// </summary>
    bool TryStampInteriorRoomAtAnchor(
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        Vector2Int anchor,
        GameObject[] pool,
        IReadOnlyList<int> eligiblePoolIndices,
        Vector2Int configFootprint,
        int seed,
        int width,
        int height,
        HashSet<Vector2Int> cellsReservedByForcedStart,
        out InteriorRoomPlacementEntry entry,
        out int poolIndexUsed,
        out Vector2Int footprintUsed,
        out Dictionary<Vector2Int, MazeFaceMask> gridChanges,
        out int orphanedCells,
        out string rejectionReason)
    {
        entry = default;
        poolIndexUsed = -1;
        footprintUsed = default;
        gridChanges = null;
        orphanedCells = int.MaxValue;
        rejectionReason = "no interior room prefabs were considered";

        List<int> order = new(eligiblePoolIndices);
        ShuffleList(order, new System.Random(MixSeed(seed, MixSeed(anchor.x, anchor.y))));

        for (int o = 0; o < order.Count; o++)
        {
            int poolIndex = order[o];
            GameObject prefab = pool[poolIndex];
            if (prefab == null)
                continue;

            if (!MazePieceDefinition.TryGetDefinitionForResolution(prefab, out MazePieceDefinition definition))
            {
                rejectionReason = $"prefab \"{prefab.name}\" has no MazePieceDefinition";
                continue;
            }

            if (!definition.AllowsContext(false, false))
            {
                rejectionReason = $"prefab \"{prefab.name}\" is Start Only or Exit Only and cannot be used as an interior room";
                continue;
            }

            Vector2Int fp = definition.ResolveInteriorGridFootprint(configFootprint);
            if (!IsRectangleStampable(
                    grid, anchor.x, anchor.y, fp.x, fp.y, width, height, start, exit, cellsReservedByForcedStart))
            {
                rejectionReason = $"footprint {fp.x}x{fp.y} for prefab \"{prefab.name}\" runs out of bounds or overlaps the start/exit cell";
                continue;
            }

            if (TryStampInteriorRoomWithPrefab(
                    grid,
                    start,
                    exit,
                    anchor,
                    fp,
                    prefab,
                    definition,
                    width,
                    height,
                    out entry,
                    out gridChanges,
                    out orphanedCells,
                    out string prefabReason))
            {
                poolIndexUsed = poolIndex;
                footprintUsed = fp;
                rejectionReason = null;
                return true;
            }

            rejectionReason = $"prefab \"{prefab.name}\" rejected: {prefabReason}";
        }

        return false;
    }

    static bool TryStampInteriorRoomWithPrefab(
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        Vector2Int anchor,
        Vector2Int footprint,
        GameObject prefab,
        MazePieceDefinition definition,
        int width,
        int height,
        out InteriorRoomPlacementEntry entry,
        out Dictionary<Vector2Int, MazeFaceMask> gridChanges,
        out int orphanedCells,
        out string rejectionReason)
    {
        entry = default;
        gridChanges = null;
        orphanedCells = int.MaxValue;
        rejectionReason = "prefab could not rotate to fit the maze";

        bool hasAuthoredDoorways = MazePieceDefinition.TryGetExactInteriorDoorwaySpecs(
            prefab,
            footprint,
            definition.FootprintSize,
            out List<InteriorDoorwaySpec> authoredDoorwaySpecs);

        int turnCount = definition.CanRotate ? 4 : 1;
        bool anyDoorwaysConsidered = false;
        bool anyConnectivityChecked = false;

        Dictionary<Vector2Int, MazeFaceMask> bestProposed = null;
        MazeFaceMask bestExteriorMask = MazeFaceMask.None;
        int bestQuarterTurns = 0;
        int bestOrphanCount = int.MaxValue;

        for (int quarterTurns = 0; quarterTurns < turnCount; quarterTurns++)
        {
            HashSet<InteriorDoorwaySpec> rotatedDoorways;
            if (hasAuthoredDoorways)
            {
                if (!TryRotateDoorwaySpecs(authoredDoorwaySpecs, footprint, quarterTurns, out rotatedDoorways))
                    continue;
            }
            else
            {
                rotatedDoorways = ComputeFallbackDoorwaysFromMaze(grid, anchor, footprint, width, height);
                if (rotatedDoorways.Count == 0)
                    continue;
            }

            anyDoorwaysConsidered = true;
            anyConnectivityChecked = true;

            if (!TryComputeStampPlan(
                    grid,
                    start,
                    exit,
                    anchor,
                    footprint,
                    rotatedDoorways,
                    width,
                    height,
                    out Dictionary<Vector2Int, MazeFaceMask> proposed,
                    out MazeFaceMask exteriorMask,
                    out int rotationOrphans))
                continue;

            if (rotationOrphans < bestOrphanCount)
            {
                bestOrphanCount = rotationOrphans;
                bestProposed = proposed;
                bestExteriorMask = exteriorMask;
                bestQuarterTurns = quarterTurns;

                if (bestOrphanCount == 0)
                    break;
            }
        }

        if (bestProposed != null)
        {
            Quaternion rotation = Quaternion.Euler(0f, bestQuarterTurns * 90f + definition.YawOffset, 0f);
            entry = new InteriorRoomPlacementEntry(
                new MazePieceMatch(prefab, definition, rotation, bestExteriorMask, definition.UseClosedFaceCaps),
                footprint);
            gridChanges = bestProposed;
            orphanedCells = bestOrphanCount;
            rejectionReason = null;
            return true;
        }

        if (!anyDoorwaysConsidered)
        {
            rejectionReason = hasAuthoredDoorways
                ? "no rotation produced a doorway pattern that fit the footprint"
                : "no maze openings border this footprint to derive a doorway pattern from";
        }
        else if (anyConnectivityChecked)
        {
            rejectionReason = "every rotation either pointed a doorway into a maze hole or " +
                "would have severed the start↔exit path";
        }

        return false;
    }

    static HashSet<InteriorDoorwaySpec> ComputeFallbackDoorwaysFromMaze(
        MazeCell[,] grid,
        Vector2Int anchor,
        Vector2Int footprint,
        int width,
        int height)
    {
        HashSet<InteriorDoorwaySpec> doorways = new();
        for (int ly = 0; ly < footprint.y; ly++)
        {
            for (int lx = 0; lx < footprint.x; lx++)
            {
                int x = anchor.x + lx;
                int y = anchor.y + ly;
                MazeFaceMask m = grid[x, y].Openings;

                for (int s = 0; s < Steps.Length; s++)
                {
                    DirectionStep step = Steps[s];
                    int nx = x + step.Dx;
                    int ny = y + step.Dy;
                    bool inside = nx >= anchor.x && nx < anchor.x + footprint.x && ny >= anchor.y && ny < anchor.y + footprint.y;
                    if (inside)
                        continue;

                    if ((m & step.Direction) == 0)
                        continue;

                    if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                        continue;

                    if (grid[nx, ny].Openings == MazeFaceMask.None)
                        continue;

                    doorways.Add(new InteriorDoorwaySpec(new Vector2Int(lx, ly), step.Direction));
                }
            }
        }

        return doorways;
    }

    static bool TryComputeStampPlan(
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        Vector2Int anchor,
        Vector2Int footprint,
        HashSet<InteriorDoorwaySpec> doorways,
        int width,
        int height,
        out Dictionary<Vector2Int, MazeFaceMask> proposed,
        out MazeFaceMask exteriorMask,
        out int orphanedCells)
    {
        orphanedCells = 0;
        proposed = new Dictionary<Vector2Int, MazeFaceMask>();
        exteriorMask = MazeFaceMask.None;

        for (int ly = 0; ly < footprint.y; ly++)
        {
            for (int lx = 0; lx < footprint.x; lx++)
            {
                Vector2Int cell = new(anchor.x + lx, anchor.y + ly);
                MazeFaceMask m = MazeFaceMask.None;

                if (lx > 0)
                    m |= MazeFaceMask.West;
                if (lx + 1 < footprint.x)
                    m |= MazeFaceMask.East;
                if (ly > 0)
                    m |= MazeFaceMask.South;
                if (ly + 1 < footprint.y)
                    m |= MazeFaceMask.North;

                // Authored doorways describe a physical opening in the prefab art. If the maze
                // cell on the other side is out of bounds or empty, the room would spawn with a
                // visible doorway attached to nothing. Reject this rotation/placement so the
                // caller can try another rotation, prefab, or anchor.
                if (lx == 0 && doorways.Contains(new InteriorDoorwaySpec(new Vector2Int(lx, ly), MazeFaceMask.West)))
                {
                    if (!TryAddAuthoredDoorway(grid, anchor.x + lx - 1, anchor.y + ly, width, height, MazeFaceMask.West, ref m))
                        return false;
                }
                if (lx == footprint.x - 1 && doorways.Contains(new InteriorDoorwaySpec(new Vector2Int(lx, ly), MazeFaceMask.East)))
                {
                    if (!TryAddAuthoredDoorway(grid, anchor.x + lx + 1, anchor.y + ly, width, height, MazeFaceMask.East, ref m))
                        return false;
                }
                if (ly == 0 && doorways.Contains(new InteriorDoorwaySpec(new Vector2Int(lx, ly), MazeFaceMask.South)))
                {
                    if (!TryAddAuthoredDoorway(grid, anchor.x + lx, anchor.y + ly - 1, width, height, MazeFaceMask.South, ref m))
                        return false;
                }
                if (ly == footprint.y - 1 && doorways.Contains(new InteriorDoorwaySpec(new Vector2Int(lx, ly), MazeFaceMask.North)))
                {
                    if (!TryAddAuthoredDoorway(grid, anchor.x + lx, anchor.y + ly + 1, width, height, MazeFaceMask.North, ref m))
                        return false;
                }

                proposed[cell] = m;
                exteriorMask |= m & GetExteriorFaceMask(lx, ly, footprint);
            }
        }

        for (int ly = 0; ly < footprint.y; ly++)
        {
            for (int lx = 0; lx < footprint.x; lx++)
            {
                Vector2Int cell = new(anchor.x + lx, anchor.y + ly);
                MazeFaceMask roomOpenings = proposed[cell];

                for (int s = 0; s < Steps.Length; s++)
                {
                    DirectionStep step = Steps[s];
                    int nx = cell.x + step.Dx;
                    int ny = cell.y + step.Dy;
                    bool inside = nx >= anchor.x && nx < anchor.x + footprint.x && ny >= anchor.y && ny < anchor.y + footprint.y;
                    if (inside)
                        continue;

                    if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                        continue;

                    Vector2Int neighbor = new(nx, ny);
                    MazeFaceMask currentNeighborOpenings = proposed.TryGetValue(neighbor, out MazeFaceMask cached)
                        ? cached
                        : grid[nx, ny].Openings;

                    MazeFaceMask oppositeDir = MazeFaceMaskUtility.Opposite(step.Direction);
                    bool roomWantsOpen = (roomOpenings & step.Direction) != 0;
                    bool neighborOpens = (currentNeighborOpenings & oppositeDir) != 0;

                    if (roomWantsOpen && !neighborOpens)
                    {
                        if (currentNeighborOpenings == MazeFaceMask.None)
                            return false;

                        proposed[neighbor] = currentNeighborOpenings | oppositeDir;
                    }
                    else if (!roomWantsOpen && neighborOpens)
                    {
                        proposed[neighbor] = currentNeighborOpenings & ~oppositeDir;
                    }
                }
            }
        }

        if (!IsConnectivityPreservedAfterStamp(grid, proposed, start, exit, width, height, out orphanedCells))
            return false;

        // If stamping the room orphaned its own footprint, IsConnectivityPreservedAfterStamp pruned
        // those cells to None. The cell-build loop skips any cell with Openings==None *before* it
        // checks for an interior-room anchor, so the room piece would be silently dropped while its
        // footprint stays reserved as an empty gap (and would be unreachable even if drawn). Reject
        // such placements so the room lands at a reachable anchor instead of vanishing.
        if (!proposed.TryGetValue(anchor, out MazeFaceMask anchorOpenings) || anchorOpenings == MazeFaceMask.None)
            return false;

        return true;
    }

    static bool TryAddAuthoredDoorway(
        MazeCell[,] grid,
        int nx,
        int ny,
        int width,
        int height,
        MazeFaceMask direction,
        ref MazeFaceMask roomOpenings)
    {
        if (nx < 0 || nx >= width || ny < 0 || ny >= height)
            return false;

        if (grid[nx, ny].Openings == MazeFaceMask.None)
            return false;

        roomOpenings |= direction;
        return true;
    }

    static MazeFaceMask GetExteriorFaceMask(int lx, int ly, Vector2Int footprint)
    {
        MazeFaceMask mask = MazeFaceMask.None;
        if (lx == 0)
            mask |= MazeFaceMask.West;
        if (lx == footprint.x - 1)
            mask |= MazeFaceMask.East;
        if (ly == 0)
            mask |= MazeFaceMask.South;
        if (ly == footprint.y - 1)
            mask |= MazeFaceMask.North;

        return mask;
    }

    /// <summary>
    /// Returns true if the proposed stamp keeps <paramref name="start"/> and <paramref name="exit"/>
    /// connected, then prunes any maze cells the stamp orphaned (sets them to <see cref="MazeFaceMask.None"/>
    /// in <paramref name="proposed"/> and clears the matching face on neighbors). Reports the
    /// orphan count via <paramref name="orphanedCells"/> so the caller can rank candidate placements.
    /// </summary>
    static bool IsConnectivityPreservedAfterStamp(
        MazeCell[,] grid,
        Dictionary<Vector2Int, MazeFaceMask> proposed,
        Vector2Int start,
        Vector2Int exit,
        int width,
        int height,
        out int orphanedCells)
    {
        orphanedCells = 0;

        MazeFaceMask EffectiveOpenings(int x, int y)
        {
            return proposed.TryGetValue(new Vector2Int(x, y), out MazeFaceMask m) ? m : grid[x, y].Openings;
        }

        if (EffectiveOpenings(start.x, start.y) == MazeFaceMask.None)
            return false;
        if (EffectiveOpenings(exit.x, exit.y) == MazeFaceMask.None)
            return false;

        bool[,] visited = new bool[width, height];
        Queue<Vector2Int> queue = new();
        queue.Enqueue(start);
        visited[start.x, start.y] = true;
        bool exitReached = start == exit;

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            MazeFaceMask m = EffectiveOpenings(current.x, current.y);

            for (int s = 0; s < Steps.Length; s++)
            {
                DirectionStep step = Steps[s];
                if ((m & step.Direction) == 0)
                    continue;

                int nx = current.x + step.Dx;
                int ny = current.y + step.Dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                    continue;

                if (visited[nx, ny])
                    continue;

                MazeFaceMask nm = EffectiveOpenings(nx, ny);
                if ((nm & MazeFaceMaskUtility.Opposite(step.Direction)) == 0)
                    continue;

                visited[nx, ny] = true;
                if (nx == exit.x && ny == exit.y)
                    exitReached = true;
                queue.Enqueue(new Vector2Int(nx, ny));
            }
        }

        if (!exitReached)
            return false;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (visited[x, y])
                    continue;
                if (EffectiveOpenings(x, y) == MazeFaceMask.None)
                    continue;

                Vector2Int cell = new Vector2Int(x, y);
                proposed[cell] = MazeFaceMask.None;
                orphanedCells++;

                for (int s = 0; s < Steps.Length; s++)
                {
                    DirectionStep step = Steps[s];
                    int nx = x + step.Dx;
                    int ny = y + step.Dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                        continue;

                    Vector2Int neighbor = new Vector2Int(nx, ny);
                    MazeFaceMask neighborOpenings = proposed.TryGetValue(neighbor, out MazeFaceMask existing)
                        ? existing
                        : grid[nx, ny].Openings;
                    MazeFaceMask opposite = MazeFaceMaskUtility.Opposite(step.Direction);
                    if ((neighborOpenings & opposite) != 0)
                        proposed[neighbor] = neighborOpenings & ~opposite;
                }
            }
        }

        return true;
    }

    static bool TryRotateDoorwaySpecs(
        List<InteriorDoorwaySpec> source,
        Vector2Int footprint,
        int quarterTurns,
        out HashSet<InteriorDoorwaySpec> rotated)
    {
        rotated = new HashSet<InteriorDoorwaySpec>();
        if (source == null)
            return true;

        quarterTurns = ((quarterTurns % 4) + 4) % 4;
        int width = footprint.x;
        int height = footprint.y;
        int finalWidth = quarterTurns % 2 == 0 ? footprint.x : footprint.y;
        int finalHeight = quarterTurns % 2 == 0 ? footprint.y : footprint.x;
        if (finalWidth != footprint.x || finalHeight != footprint.y)
            return false;

        for (int i = 0; i < source.Count; i++)
        {
            Vector2Int cell = source[i].Cell;
            MazeFaceMask direction = source[i].Direction;
            int currentWidth = width;
            int currentHeight = height;

            for (int turn = 0; turn < quarterTurns; turn++)
            {
                cell = new Vector2Int(currentHeight - 1 - cell.y, cell.x);
                direction = MazeFaceMaskUtility.Rotate(direction, 1);
                int nextWidth = currentHeight;
                currentHeight = currentWidth;
                currentWidth = nextWidth;
            }

            rotated.Add(new InteriorDoorwaySpec(cell, direction));
        }

        return true;
    }

    static int MinChebyshevBetweenAxisAlignedRects(Vector2Int aMin, Vector2Int aSize, Vector2Int bMin, Vector2Int bSize)
    {
        int ax0 = aMin.x;
        int ax1 = aMin.x + aSize.x - 1;
        int ay0 = aMin.y;
        int ay1 = aMin.y + aSize.y - 1;
        int bx0 = bMin.x;
        int bx1 = bMin.x + bSize.x - 1;
        int by0 = bMin.y;
        int by1 = bMin.y + bSize.y - 1;

        int best = int.MaxValue;
        for (int ay = ay0; ay <= ay1; ay++)
        {
            for (int ax = ax0; ax <= ax1; ax++)
            {
                for (int by = by0; by <= by1; by++)
                {
                    for (int bx = bx0; bx <= bx1; bx++)
                    {
                        int d = ChebyshevDistance(new Vector2Int(ax, ay), new Vector2Int(bx, by));
                        if (d < best)
                            best = d;
                    }
                }
            }
        }

        return best;
    }

    static int ChebyshevDistance(Vector2Int a, Vector2Int b)
    {
        return Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));
    }

    /// <summary>
    /// Picks one grid cell to place <see cref="ProceduralMazeConfig.JailDeadEndPrefab"/>, or null if the prefab is unset or no cell qualifies.
    /// </summary>
    static Vector2Int? SelectJailDeadEndCell(
        GameObject jailPrefab,
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        InteriorRoomBuildPlan interiorPlan,
        int width,
        int height,
        int seed)
    {
        if (jailPrefab == null)
            return null;

        List<Vector2Int> candidates = new();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                MazeFaceMask openings = grid[x, y].Openings;
                if (openings == MazeFaceMask.None)
                    continue;

                Vector2Int c = new(x, y);
                if (interiorPlan.SkipCells.Contains(c))
                    continue;
                if (interiorPlan.Anchors.ContainsKey(c))
                    continue;
                if (MazeFaceMaskUtility.CountOpenFaces(openings) != 1)
                    continue;
                if (c == start || c == exit)
                    continue;

                candidates.Add(c);
            }
        }

        if (candidates.Count == 0)
            return null;

        candidates.Sort((a, b) =>
        {
            int cmp = a.y.CompareTo(b.y);
            return cmp != 0 ? cmp : a.x.CompareTo(b.x);
        });

        int idx = (int)((uint)MixSeed(MixSeed(seed, unchecked((int)0x1A1E0DE0)), unchecked((int)0xBEE51A1E)) % (uint)candidates.Count);
        return candidates[idx];
    }

    void BuildCell(
        Transform parent,
        MazeFaceMask gridCellOpenings,
        float blockerOffset,
        bool isStart,
        bool isExit,
        int seed,
        Vector2Int cellCoordinates,
        bool hasInteriorRoomEntry,
        InteriorRoomPlacementEntry interiorRoomEntry,
        Vector2Int? jailDeadEndCell)
    {
        if (gridCellOpenings == MazeFaceMask.None)
            return;

        if (hasInteriorRoomEntry)
        {
            MazePieceMatch forcedMatch = interiorRoomEntry.Match;
            GameObject forcedPiece = Instantiate(forcedMatch.Prefab, parent.position, forcedMatch.Rotation, parent);
            _placedCellPrefabs[cellCoordinates] = forcedMatch.Prefab;
            TrySpawnElevatorFinishSyncIfPresent(forcedPiece);
            TrySpawnUseKeyHingeNetworkObjectsIfPresent(forcedPiece);
            TrySpawnMazeNetworkRigidbodyPropsIfPresent(forcedPiece);

            if (!forcedMatch.UseClosedFaceCaps || _config.EndCapPrefab == null)
                return;

            foreach (DirectionStep step in Steps)
            {
                if ((forcedMatch.FinalOpenFaces & step.Direction) != 0)
                    continue;

                Vector3 localOffset = MazeFaceMaskUtility.ToVector3(step.Direction) * blockerOffset;
                Quaternion endCapRotation = Quaternion.LookRotation(MazeFaceMaskUtility.ToVector3(step.Direction), Vector3.up)
                    * Quaternion.Euler(0f, _config.EndCapYawOffset, 0f);
                Instantiate(_config.EndCapPrefab, parent.position + localOffset, endCapRotation, parent);
            }

            return;
        }

        if (jailDeadEndCell.HasValue
            && cellCoordinates == jailDeadEndCell.Value
            && _config.JailDeadEndPrefab != null)
        {
            if (MazePieceResolver.TryResolveFromPrefabPool(
                    new[] { _config.JailDeadEndPrefab },
                    gridCellOpenings,
                    isStart,
                    isExit,
                    seed,
                    cellCoordinates,
                    0xA11E5A1Du,
                    out MazePieceMatch jailMatch))
            {
                GameObject jailPiece = Instantiate(jailMatch.Prefab, parent.position, jailMatch.Rotation, parent);
                _placedCellPrefabs[cellCoordinates] = jailMatch.Prefab;
                TrySpawnElevatorFinishSyncIfPresent(jailPiece);
                TrySpawnUseKeyHingeNetworkObjectsIfPresent(jailPiece);
                TrySpawnMazeNetworkRigidbodyPropsIfPresent(jailPiece);

                if (!jailMatch.UseClosedFaceCaps || _config.EndCapPrefab == null)
                    return;

                foreach (DirectionStep step in Steps)
                {
                    if ((jailMatch.FinalOpenFaces & step.Direction) != 0)
                        continue;

                    Vector3 localOffset = MazeFaceMaskUtility.ToVector3(step.Direction) * blockerOffset;
                    Quaternion endCapRotation = Quaternion.LookRotation(MazeFaceMaskUtility.ToVector3(step.Direction), Vector3.up)
                        * Quaternion.Euler(0f, _config.EndCapYawOffset, 0f);
                    Instantiate(_config.EndCapPrefab, parent.position + localOffset, endCapRotation, parent);
                }

                return;
            }

            LogMazeWarningOnce(
                "jail-dead-end-resolve-fail",
                $"[Maze] Jail dead-end prefab did not match openings at ({cellCoordinates.x}, {cellCoordinates.y}); using normal piece pool for that cell.",
                this);
        }

        HashSet<GameObject> neighborPrefabsToAvoid = _config.AvoidAdjacentDuplicatePieces
            ? CollectAdjacentPlacedPrefabs(cellCoordinates)
            : null;

        if (!MazePieceResolver.TryResolve(_config, gridCellOpenings, isStart, isExit, seed, cellCoordinates, out MazePieceMatch match, out string failureReason, neighborPrefabsToAvoid))
        {
            LogMazeWarningOnce(
                $"resolve:{gridCellOpenings}",
                $"[Maze] {failureReason} Cell ({cellCoordinates.x}, {cellCoordinates.y}) could not be built.",
                this);
            return;
        }

        GameObject matchPiece = Instantiate(match.Prefab, parent.position, match.Rotation, parent);
        _placedCellPrefabs[cellCoordinates] = match.Prefab;
        TrySpawnElevatorFinishSyncIfPresent(matchPiece);
        TrySpawnUseKeyHingeNetworkObjectsIfPresent(matchPiece);
        TrySpawnMazeNetworkRigidbodyPropsIfPresent(matchPiece);

        if (!match.UseClosedFaceCaps || _config.EndCapPrefab == null)
            return;

        foreach (DirectionStep step in Steps)
        {
            if ((match.FinalOpenFaces & step.Direction) != 0)
                continue;

            Vector3 localOffset = MazeFaceMaskUtility.ToVector3(step.Direction) * blockerOffset;
            Quaternion endCapRotation = Quaternion.LookRotation(MazeFaceMaskUtility.ToVector3(step.Direction), Vector3.up)
                * Quaternion.Euler(0f, _config.EndCapYawOffset, 0f);
            Instantiate(_config.EndCapPrefab, parent.position + localOffset, endCapRotation, parent);
        }
    }

    /// <summary>
    /// Gathers the prefabs already placed in the four orthogonal neighbors of <paramref name="cell"/>.
    /// The build loop runs in a fixed (y, x) order, so only the west/south neighbors are populated when a
    /// cell resolves — but adjacency is symmetric, so steering each cell away from its already-built
    /// neighbors is enough to guarantee no two identical pieces ever end up side by side. Returns null when
    /// nothing is built yet so <see cref="MazePieceResolver.TryResolve"/> treats it as no constraint.
    /// </summary>
    HashSet<GameObject> CollectAdjacentPlacedPrefabs(Vector2Int cell)
    {
        if (_placedCellPrefabs.Count == 0)
            return null;

        HashSet<GameObject> result = null;
        for (int i = 0; i < Steps.Length; i++)
        {
            Vector2Int neighbor = new(cell.x + Steps[i].Dx, cell.y + Steps[i].Dy);
            if (_placedCellPrefabs.TryGetValue(neighbor, out GameObject prefab) && prefab != null)
            {
                result ??= new HashSet<GameObject>();
                result.Add(prefab);
            }
        }

        return result;
    }

    void TrySpawnElevatorFinishSyncIfPresent(GameObject pieceRoot)
    {
        if (pieceRoot == null || !IsServerListening() || _networkManager == null)
            return;

        ElevatorFinishSpawnMarker marker = pieceRoot.GetComponentInChildren<ElevatorFinishSpawnMarker>(true);
        if (marker == null)
            return;

        ElevatorFinishController finish = pieceRoot.GetComponentInChildren<ElevatorFinishController>(true);
        if (finish == null)
        {
            LogMazeWarningOnce(
                "elevator-finish-embedded-missing",
                "[Maze] This finish piece has ElevatorFinishSpawnMarker but no ElevatorFinishController child. Add the ElevatorFinishSync prefab under the chunk (see MG_Finish).",
                marker);
            return;
        }

        NetworkObject networkObject = finish.GetComponent<NetworkObject>();
        if (networkObject == null)
        {
            LogMazeWarningOnce(
                "elevator-finish-sync-no-netobj",
                "[Maze] ElevatorFinishController must be on a GameObject with a NetworkObject.",
                finish);
            return;
        }

        if (networkObject.IsSpawned)
            return;

        if (finish.gameObject.scene != pieceRoot.scene)
            SceneManager.MoveGameObjectToScene(finish.gameObject, pieceRoot.scene);

        networkObject.Spawn();
    }

    void TrySpawnMazeNetworkRigidbodyPropsIfPresent(GameObject pieceRoot)
    {
        if (pieceRoot == null || !IsServerListening() || _networkManager == null)
            return;

        MazePieceNetworkRigidbodySpawn[] markers =
            pieceRoot.GetComponentsInChildren<MazePieceNetworkRigidbodySpawn>(true);
        for (int i = 0; i < markers.Length; i++)
        {
            MazePieceNetworkRigidbodySpawn marker = markers[i];
            if (marker == null)
                continue;

            NetworkObject netObj = marker.GetComponent<NetworkObject>();
            if (netObj == null || netObj.IsSpawned)
                continue;

            if (netObj.gameObject.scene != pieceRoot.scene)
                SceneManager.MoveGameObjectToScene(netObj.gameObject, pieceRoot.scene);

            netObj.Spawn();
        }
    }

    /// <summary>
    /// Nested <see cref="HingeInteractDoor"/> with <c>Use Key To Unlock</c> may carry a <see cref="NetworkObject"/> (e.g. Door_A in Jail).
    /// Server-spawn those objects so lock/open state replicates; doors without <see cref="NetworkObject"/> stay on the procedural Rpc path.
    /// Double doors: every leaf in the <see cref="HingeInteractDoor.PairedLeaf"/> loop for a keyed door is included so both
    /// NetworkObjects spawn when the prefab has them (purely keyed mates do not need their own Use Key flag).
    /// </summary>
    void TrySpawnUseKeyHingeNetworkObjectsIfPresent(GameObject pieceRoot)
    {
        if (pieceRoot == null || !IsServerListening() || _networkManager == null)
            return;

        HingeInteractDoor[] hinges = pieceRoot.GetComponentsInChildren<HingeInteractDoor>(true);
        HashSet<HingeInteractDoor> keyedDoorCluster = new HashSet<HingeInteractDoor>();
        foreach (HingeInteractDoor door in hinges)
        {
            if (door != null && door.UseKeyToUnlock)
                AddKeyedHingePairChainToSet(door, keyedDoorCluster);
        }

        foreach (HingeInteractDoor door in keyedDoorCluster)
            TrySpawnHingeDoorNetworkObjectInPieceScene(door, pieceRoot);
    }

    static void AddKeyedHingePairChainToSet(HingeInteractDoor seed, HashSet<HingeInteractDoor> bucket)
    {
        for (HingeInteractDoor step = seed; step != null; step = step.PairedLeaf)
        {
            if (!bucket.Add(step))
                break;
        }
    }

    void TrySpawnHingeDoorNetworkObjectInPieceScene(HingeInteractDoor door, GameObject pieceRoot)
    {
        if (door == null || !door.TryGetComponent(out NetworkObject netObj) || netObj == null)
            return;
        if (netObj.IsSpawned)
            return;

        if (netObj.gameObject.scene != pieceRoot.scene)
            SceneManager.MoveGameObjectToScene(netObj.gameObject, pieceRoot.scene);

        netObj.Spawn();
    }

    void ValidateConfiguredPieceSetup()
    {
        ValidatePiecePool("dead-end", MazePieceCategory.DeadEnd, _config.EnumerateTopologyPrefabs(MazePieceCategory.DeadEnd), true);
        ValidatePiecePool("straight", MazePieceCategory.Straight, _config.EnumerateTopologyPrefabs(MazePieceCategory.Straight), true);
        ValidatePiecePool("corner", MazePieceCategory.Corner, _config.EnumerateTopologyPrefabs(MazePieceCategory.Corner), true);
        ValidatePiecePool("tee", MazePieceCategory.Tee, _config.EnumerateTopologyPrefabs(MazePieceCategory.Tee), true);
        ValidatePiecePool("cross", MazePieceCategory.Cross, _config.EnumerateTopologyPrefabs(MazePieceCategory.Cross), true);
        ValidatePiecePool("special", null, _config.SpecialPrefabs, false);
        ValidatePiecePool("interior room", null, _config.InteriorRoomPrefabs, false);

        if (_config.JailDeadEndPrefab != null)
        {
            if (!MazePieceDefinition.TryGetDefinitionForResolution(_config.JailDeadEndPrefab, out MazePieceDefinition jailDef))
            {
                LogMazeWarningOnce(
                    "jail-dead-end-no-definition",
                    "[Maze] Jail Dead End Prefab needs a MazePieceDefinition with dead-end topology matching your corridor openings.",
                    this);
            }
            else if (jailDef.Category != MazePieceCategory.DeadEnd)
            {
                LogMazeWarningOnce(
                    "jail-dead-end-wrong-category",
                    $"[Maze] Jail Dead End Prefab \"{_config.JailDeadEndPrefab.name}\" should use MazePieceCategory DeadEnd.",
                    this);
            }
        }

        if (_config.InteriorRoomCount > 0)
        {
            bool anyInteriorPrefab = false;
            foreach (GameObject prefab in _config.InteriorRoomPrefabs)
            {
                if (prefab == null)
                    continue;

                anyInteriorPrefab = true;
                if (MazePieceDefinition.TryGetDefinitionForResolution(prefab, out MazePieceDefinition definition)
                    && (definition.StartOnly || definition.ExitOnly))
                {
                    LogMazeWarningOnce(
                        $"interior-room-start-exit-only:{prefab.name}",
                        $"[Maze] Interior room prefab \"{prefab.name}\" is Start Only or Exit Only; interior placement skips start/exit cells, so it will never spawn as an interior room.",
                        this);
                }
            }

            if (!anyInteriorPrefab)
            {
                LogMazeWarningOnce(
                    "interior-room-count-no-prefabs",
                    "[Maze] Interior Room Count is greater than zero but Interior Room Prefabs has no assigned prefabs.",
                    this);
            }
        }

        if (_config.ForcedStartPiecePrefab != null)
        {
            MazePieceDefinition forcedDef = _config.ForcedStartPiecePrefab.GetComponent<MazePieceDefinition>();
            if (forcedDef == null)
            {
                LogMazeWarningOnce(
                    "forced-start-no-definition",
                    $"[Maze] Forced Start Piece Prefab \"{_config.ForcedStartPiecePrefab.name}\" needs a MazePieceDefinition (open faces must cover every possible start opening pattern, e.g. North|East for corner starts).",
                    this);
            }
            else if (forcedDef.OpenFaces == MazeFaceMask.None)
            {
                LogMazeWarningOnce(
                    "forced-start-no-faces",
                    $"[Maze] Forced Start Piece Prefab \"{_config.ForcedStartPiecePrefab.name}\" has no open faces on MazePieceDefinition.",
                    this);
            }
        }

        bool hasConfiguredSpecialPrefabs = false;
        foreach (GameObject specialPrefab in _config.SpecialPrefabs)
        {
            if (specialPrefab == null)
                continue;

            hasConfiguredSpecialPrefabs = true;
            break;
        }

        if (_config.EffectiveSpecialRoomPrefab != null && !hasConfiguredSpecialPrefabs)
        {
            LogMazeWarningOnce(
                "legacy-room-fallback",
                "[Maze] Room Prefab / Alternate Room Prefab is still using legacy start/exit fallback behavior. Add a MazePieceDefinition and move it into Special Prefabs for exact face matching.",
                this);
        }

        if (_config.SpecialRoomVariant == MazeSpecialRoomVariant.Alternate
            && _config.AlternateRoomPrefab == null
            && _config.RoomPrefab != null)
        {
            LogMazeWarningOnce(
                "special-room-variant-alternate-missing",
                "[Maze] Special Room Variant is Alternate but Alternate Room Prefab is empty; using Room Prefab as fallback.",
                this);
        }

        if (_config.MazeTrapCount > 0)
        {
            if (_config.MazeTrapPrefab == null)
            {
                LogMazeWarningOnce(
                    "maze-trap-count-no-prefab",
                    "[Maze] Maze Trap Count is greater than zero but Maze Trap Prefab is empty.",
                    this);
            }
            else if (_config.MazeTrapPrefab.GetComponent<NetworkObject>() == null)
            {
                LogMazeWarningOnce(
                    $"maze-trap-no-networkobject:{_config.MazeTrapPrefab.name}",
                    $"[Maze] Maze Trap Prefab \"{_config.MazeTrapPrefab.name}\" has no NetworkObject. It will not replicate to clients in multiplayer.",
                    this);
            }
        }
    }

    void ValidatePiecePool(string poolName, MazePieceCategory? expectedCategory, IEnumerable<GameObject> prefabs, bool required)
    {
        bool hasAssignedPrefab = false;
        bool hasValidDefinition = false;
        HashSet<GameObject> seen = new();

        foreach (GameObject prefab in prefabs)
        {
            if (prefab == null)
                continue;

            hasAssignedPrefab = true;
            if (!seen.Add(prefab))
            {
                LogMazeWarningOnce(
                    $"duplicate:{poolName}:{prefab.name}",
                    $"[Maze] Prefab \"{prefab.name}\" is listed more than once in the {poolName} pool.",
                    this);
                continue;
            }

            if (!MazePieceDefinition.TryGetDefinitionForResolution(prefab, out MazePieceDefinition definition))
            {
                LogMazeWarningOnce(
                    $"missing-definition:{poolName}:{prefab.name}",
                    $"[Maze] Prefab \"{prefab.name}\" in the {poolName} pool is missing a MazePieceDefinition (root or child).",
                    this);
                continue;
            }

            bool hasExactInteriorDoorways = MazePieceDefinition.TryGetExactInteriorDoorwaySpecs(
                prefab,
                definition.ResolveInteriorGridFootprint(_config.InteriorRoomGridFootprint),
                definition.FootprintSize,
                out _);

            if (definition.OpenFaces == MazeFaceMask.None && !hasExactInteriorDoorways)
            {
                LogMazeWarningOnce(
                    $"no-open-faces:{poolName}:{prefab.name}",
                    $"[Maze] Prefab \"{prefab.name}\" in the {poolName} pool has no open faces configured.",
                    this);
                continue;
            }

            if (expectedCategory.HasValue && definition.Category != expectedCategory.Value)
            {
                LogMazeWarningOnce(
                    $"category-mismatch:{poolName}:{prefab.name}",
                    $"[Maze] Prefab \"{prefab.name}\" is in the {poolName} pool but its MazePieceDefinition category is {definition.Category}.",
                    this);
                continue;
            }

            hasValidDefinition = true;
        }

        if (required && !hasAssignedPrefab)
        {
            LogMazeWarningOnce(
                $"empty-pool:{poolName}",
                $"[Maze] The {poolName} pool has no prefabs assigned.",
                this);
        }

        if (required && !hasValidDefinition)
        {
            LogMazeWarningOnce(
                $"no-valid-pool:{poolName}",
                $"[Maze] The {poolName} pool does not contain any valid maze piece definitions.",
                this);
        }
    }

    void LogMazeWarningOnce(string key, string message, UnityEngine.Object context)
    {
        if (!_loggedMazeWarnings.Add(key))
            return;

        Debug.LogWarning(message, context);
    }

    void CreateSpawnPoints(Transform root, Vector2Int startCell, float cellSize, Vector2Int startAreaFootprint)
    {
        if (_config.SpawnPointCount <= 0)
            return;

        Transform spawnRoot = CreateChild(root, "GeneratedSpawnPoints");
        bool useStartRect = startAreaFootprint.x > 1 || startAreaFootprint.y > 1;
        Vector3 center = (useStartRect
                ? CellRectCenterWorld(startCell.x, startCell.y, startAreaFootprint.x, startAreaFootprint.y, cellSize)
                : CellToWorld(startCell.x, startCell.y, cellSize))
            + Vector3.up * _config.SpawnHeight;

        for (int i = 0; i < _config.SpawnPointCount; i++)
        {
            Vector3 offset = GetSpawnOffset(i);
            GameObject spawnObject = new($"Spawn_{i}");
            spawnObject.transform.SetParent(spawnRoot, false);
            spawnObject.transform.position = center + offset;
            spawnObject.transform.rotation = Quaternion.identity;

            MultiplayerSpawnPoint spawnPoint = spawnObject.AddComponent<MultiplayerSpawnPoint>();
            spawnPoint.SetPriority(i);
        }
    }

    Vector3 GetSpawnOffset(int index)
    {
        float spacing = _config.SpawnSpacing;
        return index switch
        {
            0 => Vector3.zero,
            1 => new Vector3(spacing, 0f, 0f),
            2 => new Vector3(-spacing, 0f, 0f),
            3 => new Vector3(0f, 0f, spacing),
            4 => new Vector3(0f, 0f, -spacing),
            _ => new Vector3((index - 2) * spacing * 0.5f, 0f, 0f)
        };
    }

    void TryRebuildRuntimeNavMesh(GameObject mazeRoot)
    {
        if (!rebuildNavMeshAfterMaze || mazeRoot == null || !Application.isPlaying)
            return;

        // Clients build the same maze from the synced seed, so they have the same colliders. Bake here too:
        // replicated enemies use NavMeshAgent; the agent briefly needs a valid NavMesh on OnEnable (before
        // NetworkZombieAvatar disables the agent for non-server), and this avoids "no valid NavMesh" spam.
        NavMeshSurface surface = mazeRoot.GetComponent<NavMeshSurface>();
        if (surface == null)
            surface = mazeRoot.AddComponent<NavMeshSurface>();

        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = navMeshBakeGeometry;
        surface.layerMask = ~0;
        ConfigureCeilingExclusionVolume(mazeRoot.transform);

        LogUnreadableNavMeshSourceMeshesOnce(mazeRoot.transform);
        surface.BuildNavMesh();
    }

    /// <summary>
    /// Runtime NavMesh baking reads mesh data from MeshColliders (physics mode) or MeshFilters (render mode).
    /// Imported meshes must have Read/Write enabled or player builds cannot bake; the editor often still works.
    /// </summary>
    void LogUnreadableNavMeshSourceMeshesOnce(Transform mazeRoot)
    {
        if (mazeRoot == null)
            return;

        if (navMeshBakeGeometry == NavMeshCollectGeometry.PhysicsColliders)
        {
            foreach (MeshCollider col in mazeRoot.GetComponentsInChildren<MeshCollider>(true))
                TryLogUnreadableNavMeshMeshOnce(col.sharedMesh, col.transform, col);
        }
        else
        {
            foreach (MeshFilter mf in mazeRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.GetComponent<MeshRenderer>() == null)
                    continue;
                TryLogUnreadableNavMeshMeshOnce(mf.sharedMesh, mf.transform, mf);
            }
        }
    }

    void TryLogUnreadableNavMeshMeshOnce(Mesh mesh, Transform sourceTransform, Component context)
    {
        if (mesh == null || mesh.isReadable)
            return;

        string key = $"navmesh_cpu_unreadable::{mesh.GetEntityId()}";
        if (!_loggedMazeWarnings.Add(key))
            return;

        Debug.LogError(
            "[Maze] NavMesh runtime bake: mesh asset '" + mesh.name + "' is not CPU-readable (see e.g. '" +
            BuildTransformHierarchyPath(sourceTransform) + "'). " +
            "Fix: Project window → select the FBX/model for this mesh → Inspector Model tab → enable Read/Write Enabled → Apply. " +
            "Without this, runtime baking works in the Editor but fails in standalone players.",
            context);
    }

    static string BuildTransformHierarchyPath(Transform t)
    {
        if (t == null)
            return "";

        List<string> names = new(8);
        for (Transform cur = t; cur != null; cur = cur.parent)
            names.Add(cur.name);
        names.Reverse();
        return string.Join("/", names);
    }

    void ConfigureCeilingExclusionVolume(Transform mazeRoot)
    {
        if (mazeRoot == null)
            return;

        const string exclusionName = "__RuntimeNavMesh_CeilingExclude";
        Transform exclusionTransform = mazeRoot.Find(exclusionName);
        if (exclusionTransform == null)
        {
            GameObject exclusionObject = new(exclusionName);
            exclusionObject.transform.SetParent(mazeRoot, false);
            exclusionTransform = exclusionObject.transform;
        }

        if (!TryGetMazeBounds(mazeRoot, out Bounds mazeBounds))
            return;

        // Never key off the raw axis-aligned min Y alone: tall pits / large downward colliders drag min.y
        // far below the real corridor floor and the Not Walkable box would cover the walkable level.
        float layoutFloorY = _config != null
            ? Mathf.Max(mazeBounds.min.y, _config.Origin.y)
            : mazeBounds.min.y;
        float excludeBottomY = layoutFloorY + Mathf.Max(0.5f, navMeshCeilingExcludeHeightAboveFloor);
        float slabThickness = Mathf.Max(0.5f, mazeBounds.max.y - excludeBottomY + 0.5f);
        float margin = 0.35f;

        exclusionTransform.position = new Vector3(
            mazeBounds.center.x,
            excludeBottomY + slabThickness * 0.5f,
            mazeBounds.center.z);
        exclusionTransform.rotation = Quaternion.identity;
        exclusionTransform.localScale = Vector3.one;

        NavMeshModifierVolume volume = exclusionTransform.GetComponent<NavMeshModifierVolume>();
        if (volume == null)
            volume = exclusionTransform.gameObject.AddComponent<NavMeshModifierVolume>();

        volume.center = Vector3.zero;
        volume.size = new Vector3(
            Mathf.Max(0.5f, mazeBounds.size.x + margin * 2f),
            Mathf.Max(0.5f, slabThickness),
            Mathf.Max(0.5f, mazeBounds.size.z + margin * 2f));
        volume.area = 1; // Not Walkable
    }

    bool TryGetMazeBounds(Transform mazeRoot, out Bounds bounds)
    {
        bounds = default;
        bool hasBounds = false;

        Collider[] colliders = mazeRoot.GetComponentsInChildren<Collider>(includeInactive: true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider c = colliders[i];
            if (c == null || !c.enabled || c.isTrigger)
                continue;

            if (!hasBounds)
            {
                bounds = c.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(c.bounds);
            }
        }

        if (hasBounds)
            return true;

        Renderer[] renderers = mazeRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null || !r.enabled)
                continue;

            if (!hasBounds)
            {
                bounds = r.bounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }

        return hasBounds;
    }

    void TrySpawnMazeEnemies(
        Transform mazeRoot,
        MazeCell[,] grid,
        Vector2Int start,
        Vector2Int exit,
        int seed,
        float cellSize,
        InteriorRoomBuildPlan interiorPlan,
        HashSet<Vector2Int> mazeTrapCells,
        List<Transform> mazeTrapRoots)
    {
        mazeTrapCells ??= new HashSet<Vector2Int>();
        mazeTrapRoots ??= new List<Transform>();
        GameObject zombiePrefab = mazeEnemyPrefabOverride != null ? mazeEnemyPrefabOverride : _config.MazeEnemyPrefab;
        int zombieCountRequested = _config.MazeEnemyCount;
        GameObject hunterPrefab = _config.MazeHunterPrefab;
        int hunterCountRequested = _config.MazeHunterCount;
        GameObject skeletonPrefab = _config.MazeSkeletonPrefab;
        int skeletonCountRequested = _config.MazeSkeletonCount;
        GameObject monkeyPrefab = _config.MazeWindupMonkeyPrefab;
        int monkeyCountRequested = _config.MazeWindupMonkeyCount;

        bool wantZombies = zombiePrefab != null && zombieCountRequested > 0;
        bool wantHunters = hunterPrefab != null && hunterCountRequested > 0;
        bool wantSkeletons = skeletonPrefab != null && skeletonCountRequested > 0;
        bool wantMonkeys = monkeyPrefab != null && monkeyCountRequested > 0;
        if (!wantZombies && !wantHunters && !wantSkeletons && !wantMonkeys)
            return;

        if (_networkManager != null && _networkManager.IsListening && !_networkManager.IsServer)
            return;

        int?[,] distances = ComputeMazeCellDistances(grid, start);
        int width = grid.GetLength(0);
        int height = grid.GetLength(1);
        List<Vector2Int> candidates = new();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (grid[x, y].Openings == MazeFaceMask.None)
                    continue;

                Vector2Int cellKey = new(x, y);
                if (interiorPlan.SkipCells.Contains(cellKey))
                    continue;

                if (x == start.x && y == start.y)
                    continue;

                if (_config.MazeEnemyExcludeExitCell && x == exit.x && y == exit.y)
                    continue;

                // The exit hall is one long finish piece; its anchor cell is a normal built cell, so
                // without this an enemy could spawn in the approach — or inside the elevator alcove.
                if (_exitHallCells.Contains(cellKey))
                    continue;

                int? d = distances[x, y];
                if (!d.HasValue || d.Value < _config.MazeEnemyMinCellsFromStart)
                    continue;

                if (mazeTrapCells.Count > 0 && mazeTrapCells.Contains(cellKey))
                    continue;

                candidates.Add(cellKey);
            }
        }

        if (candidates.Count == 0)
        {
            LogMazeWarningOnce("enemies-no-cells", "[Maze] No valid cells for maze enemies (check min distance / maze size).", this);
            return;
        }

        ShuffleList(candidates, new System.Random(MixSeed(seed, unchecked((int)0x39FEA0B9))));

        // Reserve far-from-start cells for the hunter slot (Jailor/Clown) FIRST, so that dangerous chaser never
        // spawns right on top of the start room. The hunter uses its own (usually larger) minimum
        // distance; everything else keeps the shared maze-enemy minimum baked into the candidate list.
        int hunterMinCells = _config.MazeHunterMinCellsFromStart;
        List<Vector2Int> hunterCells = new();
        if (wantHunters && hunterCountRequested > 0)
        {
            HashSet<Vector2Int> picked = new();
            foreach (Vector2Int c in candidates)
            {
                if (hunterCells.Count >= hunterCountRequested)
                    break;
                if ((distances[c.x, c.y] ?? 0) >= hunterMinCells)
                {
                    hunterCells.Add(c);
                    picked.Add(c);
                }
            }

            // Small maze fallback: if no cell is far enough, still spawn the hunter at the
            // farthest cells available (never near the start) rather than dropping it entirely.
            if (hunterCells.Count < hunterCountRequested)
            {
                List<Vector2Int> byDistanceDesc = new(candidates);
                byDistanceDesc.Sort((a, b) => (distances[b.x, b.y] ?? 0).CompareTo(distances[a.x, a.y] ?? 0));
                foreach (Vector2Int c in byDistanceDesc)
                {
                    if (hunterCells.Count >= hunterCountRequested)
                        break;
                    if (picked.Add(c))
                        hunterCells.Add(c);
                }

                LogMazeWarningOnce(
                    "maze-hunter-trimmed",
                    $"[Maze] No cell is at least {hunterMinCells} cell(s) from start for the hunter (Jailor/Clown); "
                    + "placing it at the farthest available cell(s) instead. Lower the hunter min distance or enlarge the maze.",
                    this);
            }
        }

        int huntersToSpawn = hunterCells.Count;

        HashSet<Vector2Int> reservedHunterCells = new(hunterCells);

        // Remaining cells (shuffled order preserved) feed zombies, skeletons and monkeys.
        List<Vector2Int> otherCells = new(candidates.Count - huntersToSpawn);
        foreach (Vector2Int c in candidates)
            if (!reservedHunterCells.Contains(c))
                otherCells.Add(c);

        int zombiesToSpawn = wantZombies ? Mathf.Min(zombieCountRequested, otherCells.Count) : 0;
        if (wantZombies && zombieCountRequested > otherCells.Count)
        {
            LogMazeWarningOnce(
                "enemies-trimmed",
                $"[Maze] Requested {_config.MazeEnemyCount} maze enemies but only {otherCells.Count} cells match rules; spawning {otherCells.Count}.",
                this);
        }

        int freeAfterZombies = otherCells.Count - zombiesToSpawn;
        int skeletonsToSpawn = wantSkeletons ? Mathf.Min(skeletonCountRequested, freeAfterZombies) : 0;
        if (wantSkeletons && skeletonsToSpawn < skeletonCountRequested)
        {
            LogMazeWarningOnce(
                "maze-skeleton-trimmed",
                $"[Maze] Requested {skeletonCountRequested} maze skeleton(s) but only {freeAfterZombies} cell(s) remain after zombies and hunters; spawning {skeletonsToSpawn}. "
                + "Reduce maze enemy count or maze size.",
                this);
        }

        int freeAfterSkeletons = freeAfterZombies - skeletonsToSpawn;
        int monkeysToSpawn = wantMonkeys ? Mathf.Min(monkeyCountRequested, freeAfterSkeletons) : 0;
        if (wantMonkeys && monkeysToSpawn < monkeyCountRequested)
        {
            LogMazeWarningOnce(
                "maze-monkey-trimmed",
                $"[Maze] Requested {monkeyCountRequested} maze wind-up monkey(s) but only {freeAfterSkeletons} cell(s) remain after zombies, hunters and skeletons; spawning {monkeysToSpawn}. "
                + "Reduce maze enemy count or maze size.",
                this);
        }

        int totalSpawns = zombiesToSpawn + huntersToSpawn + skeletonsToSpawn + monkeysToSpawn;
        if (totalSpawns == 0)
            return;

        // Rebuild the flat candidate list in the exact layout the spawn loops below index into:
        // [zombies][hunters][skeletons][monkeys].
        candidates = new List<Vector2Int>(totalSpawns);
        candidates.AddRange(otherCells.GetRange(0, zombiesToSpawn));
        candidates.AddRange(hunterCells);
        candidates.AddRange(otherCells.GetRange(zombiesToSpawn, skeletonsToSpawn));
        candidates.AddRange(otherCells.GetRange(zombiesToSpawn + skeletonsToSpawn, monkeysToSpawn));

        if (wantZombies)
        {
            Debug.Log(
                $"[Maze] Spawning {zombiesToSpawn} maze enemy/enemies from config \"{_config.name}\" "
                + $"(prefab \"{zombiePrefab.name}\"). "
                + $"{(mazeEnemyPrefabOverride != null ? $"Override on coordinator: \"{mazeEnemyPrefabOverride.name}\"." : "")}",
                this);
        }

        if (wantHunters && huntersToSpawn > 0)
        {
            Debug.Log(
                $"[Maze] Spawning {huntersToSpawn} maze hunter(s) (Jailor/Clown) from config \"{_config.name}\" (prefab \"{hunterPrefab.name}\").",
                this);
        }

        Transform enemiesRoot = CreateChild(mazeRoot, "GeneratedEnemies");
        float yOffset = _config.MazeEnemySpawnHeight;
        bool spawnWithNetcode = _networkManager != null && _networkManager.IsListening;
        List<Vector3> placedEnemyPositions = new(totalSpawns);
        float minSeparationXZ = ResolveMazeEnemyMinSeparationXZ(cellSize);

        for (int i = 0; i < zombiesToSpawn; i++)
        {
            Vector2Int cell = candidates[i];
            Vector3 position = ResolveSpawnPositionWithSeparation(
                cell,
                cellSize,
                yOffset,
                seed,
                i,
                placedEnemyPositions,
                minSeparationXZ,
                interiorPlan,
                mazeTrapRoots);

            placedEnemyPositions.Add(position);
            GameObject instance = Instantiate(zombiePrefab, position, Quaternion.identity, enemiesRoot);

            if (spawnWithNetcode)
            {
                NetworkObject networkObject = instance.GetComponent<NetworkObject>();
                if (networkObject != null)
                    networkObject.Spawn();
            }
        }

        const int hunterSpawnSalt = unchecked((int)0x7E11CA33);
        for (int j = 0; j < huntersToSpawn; j++)
        {
            int candidateIndex = zombiesToSpawn + j;
            Vector2Int cell = candidates[candidateIndex];
            int spawnKey = zombiesToSpawn + MixSeed(seed, MixSeed(j, hunterSpawnSalt));
            Vector3 position = ResolveSpawnPositionWithSeparation(
                cell,
                cellSize,
                yOffset,
                seed,
                spawnKey,
                placedEnemyPositions,
                minSeparationXZ,
                interiorPlan,
                mazeTrapRoots);

            placedEnemyPositions.Add(position);
            GameObject instance = Instantiate(hunterPrefab, position, Quaternion.identity, enemiesRoot);

            if (spawnWithNetcode)
            {
                NetworkObject networkObject = instance.GetComponent<NetworkObject>();
                if (networkObject != null)
                    networkObject.Spawn();
            }
        }

        if (skeletonsToSpawn > 0)
        {
            Debug.Log(
                $"[Maze] Spawning {skeletonsToSpawn} maze skeleton(s) from config \"{_config.name}\" (prefab \"{skeletonPrefab.name}\").",
                this);
        }

        const int skeletonSpawnSalt = unchecked((int)0x5CE1E708);
        for (int s = 0; s < skeletonsToSpawn; s++)
        {
            int candidateIndex = zombiesToSpawn + huntersToSpawn + s;
            Vector2Int cell = candidates[candidateIndex];
            int spawnKey = zombiesToSpawn + huntersToSpawn + MixSeed(seed, MixSeed(s, skeletonSpawnSalt));
            Vector3 position = ResolveSpawnPositionWithSeparation(
                cell,
                cellSize,
                yOffset,
                seed,
                spawnKey,
                placedEnemyPositions,
                minSeparationXZ,
                interiorPlan,
                mazeTrapRoots);

            placedEnemyPositions.Add(position);
            GameObject instance = Instantiate(skeletonPrefab, position, Quaternion.identity, enemiesRoot);

            if (spawnWithNetcode)
            {
                NetworkObject networkObject = instance.GetComponent<NetworkObject>();
                if (networkObject != null)
                    networkObject.Spawn();
            }
        }

        if (monkeysToSpawn > 0)
        {
            Debug.Log(
                $"[Maze] Spawning {monkeysToSpawn} maze wind-up monkey(s) from config \"{_config.name}\" (prefab \"{monkeyPrefab.name}\").",
                this);
        }

        const int monkeySpawnSalt = unchecked((int)0x4D0E1A55);
        for (int m = 0; m < monkeysToSpawn; m++)
        {
            int candidateIndex = zombiesToSpawn + huntersToSpawn + skeletonsToSpawn + m;
            Vector2Int cell = candidates[candidateIndex];
            int spawnKey = zombiesToSpawn + huntersToSpawn + skeletonsToSpawn + MixSeed(seed, MixSeed(m, monkeySpawnSalt));
            Vector3 position = ResolveSpawnPositionWithSeparation(
                cell,
                cellSize,
                yOffset,
                seed,
                spawnKey,
                placedEnemyPositions,
                minSeparationXZ,
                interiorPlan,
                mazeTrapRoots);

            placedEnemyPositions.Add(position);
            GameObject instance = Instantiate(monkeyPrefab, position, Quaternion.identity, enemiesRoot);

            if (spawnWithNetcode)
            {
                NetworkObject networkObject = instance.GetComponent<NetworkObject>();
                if (networkObject != null)
                    networkObject.Spawn();
            }
        }

        TryAssignJailorCarryDestinationFromMaze(mazeRoot, start, exit, cellSize);
    }

    void TryAssignJailorCarryDestinationFromMaze(
        Transform mazeRoot,
        Vector2Int start,
        Vector2Int exit,
        float cellSize)
    {
        if (!_config.AssignJailorCarryDestinationAfterSpawn || mazeRoot == null)
            return;

        // Maze enemy prefab may be a zombie; Jailor can be spawned elsewhere in the same scene.
        // Assign carry target to every JailorAI in this scene, not only under GeneratedEnemies.
        List<JailorAI> jailors = FindJailorsInSameSceneAsMaze(mazeRoot);
        if (jailors.Count == 0)
            return;

        if (_config.PreferJailorCarryAnchorFromMazePrefab)
        {
            Transform anchor = FindFirstTransformByExactName(
                mazeRoot,
                _config.JailorCarryAnchorTransformName,
                out int anchorMatches);
            if (anchor != null)
            {
                if (anchorMatches > 1)
                {
                    LogMazeWarningOnce(
                        "jailor-carry-anchor-multiple",
                        $"[Maze] Found {anchorMatches} transforms named \"{_config.JailorCarryAnchorTransformName}\" under the maze; "
                        + "using the first. Use a unique name or only one piece with this anchor.",
                        this);
                }

                DestroyChildByName(mazeRoot, _config.JailorCarryDestinationMarkerName);
                ApplyCarryDestinationToJailors(jailors, anchor);
                return;
            }

            LogMazeWarningOnce(
                "jailor-carry-anchor-missing",
                $"[Maze] Prefer maze-piece carry anchor is on but no Transform named \"{_config.JailorCarryAnchorTransformName}\" was found. "
                + "Falling back to generated exit/start marker. Add a child with that exact name to your room prefab.",
                this);
        }

        Vector2Int cell = _config.JailorCarryDestinationMazeAnchor == JailorCarryDestinationMazeAnchor.StartCell
            ? start
            : exit;

        // Same floor + NavMesh snap strategy as maze enemies so the marker isn't projected to a random
        // corridor corner when the cell center sits off-mesh (common in large interior cells).
        Vector3 cellCenter = CellToWorld(cell.x, cell.y, cellSize);
        if (!TryFindMazeEnemySpawnFloor(cellCenter, cellSize, out Vector3 floorPoint, null))
            floorPoint = cellCenter;

        Vector3 sampleFrom = floorPoint + Vector3.up * (0.35f + _config.JailorCarryDestinationYOffset);
        float[] snapRadii =
        {
            Mathf.Clamp(cellSize * 0.22f, 0.75f, 3f),
            Mathf.Clamp(cellSize * 0.5f, 2.5f, 8f),
            Mathf.Max(12f, _config.JailorCarryDestinationNavMeshSearchRadius)
        };

        Vector3 raw = floorPoint + Vector3.up * _config.JailorCarryDestinationYOffset;
        for (int i = 0; i < snapRadii.Length; i++)
        {
            if (!NavMesh.SamplePosition(sampleFrom, out NavMeshHit navHit, snapRadii[i], NavMesh.AllAreas))
                continue;
            if (navHit.position.y <= floorPoint.y + 2.5f)
            {
                raw = navHit.position + Vector3.up * _config.JailorCarryDestinationYOffset;
                break;
            }
        }

        string markerName = _config.JailorCarryDestinationMarkerName;
        DestroyChildByName(mazeRoot, markerName);

        GameObject marker = new(markerName);
        marker.transform.SetParent(mazeRoot, false);
        marker.transform.position = raw;

        ApplyCarryDestinationToJailors(jailors, marker.transform);
    }

    static void ApplyCarryDestinationToJailors(List<JailorAI> jailors, Transform destination)
    {
        if (jailors == null || destination == null)
            return;

        for (int i = 0; i < jailors.Count; i++)
        {
            if (jailors[i] != null)
                jailors[i].SetCarryDestination(destination);
        }
    }

    static List<JailorAI> FindJailorsInSameSceneAsMaze(Transform mazeRoot)
    {
        List<JailorAI> list = new();
        if (mazeRoot == null)
            return list;

        Scene mazeScene = mazeRoot.gameObject.scene;
        foreach (JailorAI j in UnityEngine.Object.FindObjectsByType<JailorAI>(
                     FindObjectsInactive.Include))
        {
            if (j != null && j.gameObject.scene == mazeScene)
                list.Add(j);
        }

        return list;
    }

    static void DestroyChildByName(Transform parent, string childName)
    {
        if (parent == null || string.IsNullOrEmpty(childName))
            return;

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Transform c = parent.GetChild(i);
            if (c != null && c.name == childName)
                UnityEngine.Object.Destroy(c.gameObject);
        }
    }

    static Transform FindFirstTransformByExactName(Transform root, string exactName, out int matchCount)
    {
        matchCount = 0;
        if (root == null || string.IsNullOrEmpty(exactName))
            return null;

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        Transform first = null;
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform t = transforms[i];
            if (t == null || t.name != exactName)
                continue;

            matchCount++;
            if (first == null)
                first = t;
        }

        return first;
    }

    /// <summary>
    /// Grid cells with a spawned trap and root transforms of each trap instance (roof/cap colliders may not be under <see cref="PitKillZone"/>).
    /// </summary>
    (HashSet<Vector2Int> cells, List<Transform> roots) TrySpawnMazeTraps(
        Transform mazeRoot,
        MazeCell[,] grid,
        IReadOnlyDictionary<Vector2Int, Transform> cellRoots,
        Vector2Int start,
        Vector2Int exit,
        int seed,
        float cellSize)
    {
        HashSet<Vector2Int> spawnedTrapCells = new();
        List<Transform> spawnedTrapRoots = new();
        GameObject prefab = _config.MazeTrapPrefab;
        int requestedCount = _config.MazeTrapCount;
        if (prefab == null || requestedCount <= 0)
            return (spawnedTrapCells, spawnedTrapRoots);

        if (_networkManager != null && _networkManager.IsListening && !_networkManager.IsServer)
            return (spawnedTrapCells, spawnedTrapRoots);

        int?[,] distances = ComputeMazeCellDistances(grid, start);
        List<TrapAnchorCandidate> candidates = new();
        List<Transform> cellTrapAnchors = new(4);

        foreach (KeyValuePair<Vector2Int, Transform> pair in cellRoots)
        {
            Vector2Int cell = pair.Key;
            Transform cellRoot = pair.Value;
            if (cellRoot == null)
                continue;

            if (cell == start)
                continue;

            if (_config.MazeTrapExcludeExitCell && cell == exit)
                continue;

            int? distance = distances[cell.x, cell.y];
            if (!distance.HasValue || distance.Value < _config.MazeTrapMinCellsFromStart)
                continue;

            cellTrapAnchors.Clear();
            CollectTrapAnchors(cellRoot, cellTrapAnchors);
            if (cellTrapAnchors.Count == 0)
                continue;

            for (int a = 0; a < cellTrapAnchors.Count; a++)
                candidates.Add(new TrapAnchorCandidate(cell, cellTrapAnchors[a]));
        }

        if (candidates.Count == 0)
        {
            LogMazeWarningOnce(
                "maze-traps-no-anchors",
                "[Maze] No valid TrapAnchor or TrapAnchor2 locations were found for maze traps. Add those children to generated maze prefabs and check min distance / exit exclusion settings.",
                this);
            return (spawnedTrapCells, spawnedTrapRoots);
        }

        ShuffleList(candidates, new System.Random(MixSeed(seed, unchecked((int)0x2D6E7A11))));

        Transform trapsRoot = CreateChild(mazeRoot, "GeneratedTraps");
        bool spawnWithNetcode = _networkManager != null && _networkManager.IsListening;
        float minSeparationXZ = ResolveMazeTrapMinSeparationXZ(cellSize);
        float minSqr = minSeparationXZ * minSeparationXZ;
        List<Vector3> placedTrapPositions = new(requestedCount);
        int spawnedCount = 0;

        for (int i = 0; i < candidates.Count && spawnedCount < requestedCount; i++)
        {
            TrapAnchorCandidate candidate = candidates[i];
            Vector3 position = candidate.Anchor.position;
            if (spawnedCount > 0 && minSeparationXZ > 0f && !IsFarEnoughXZ(position, placedTrapPositions, minSqr))
                continue;

            placedTrapPositions.Add(position);
            GameObject instance = Instantiate(prefab, Vector3.zero, Quaternion.identity, trapsRoot);
            AlignTrapInstanceToAnchor(instance.transform, candidate.Anchor);
            spawnedCount++;
            spawnedTrapCells.Add(candidate.Cell);
            spawnedTrapRoots.Add(instance.transform);

            if (!spawnWithNetcode)
                continue;

            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            if (networkObject != null)
                networkObject.Spawn();
        }

        if (spawnedCount <= 0)
        {
            LogMazeWarningOnce(
                "maze-traps-filtered-out",
                "[Maze] Maze traps were configured but no anchors survived the spacing rules. Lower Maze Trap Min Separation or increase anchor coverage.",
                this);
            return (spawnedTrapCells, spawnedTrapRoots);
        }

        if (spawnedCount < requestedCount)
        {
            LogMazeWarningOnce(
                "maze-traps-trimmed",
                $"[Maze] Requested {requestedCount} maze traps but only spawned {spawnedCount} from {candidates.Count} TrapAnchor candidate(s).",
                this);
        }

        return (spawnedTrapCells, spawnedTrapRoots);
    }

    void TrySpawnMazeStartFlashlights(Transform mazeRoot)
    {
        GameObject prefab = _config.MazeStartFlashlightPrefab;
        if (prefab == null || mazeRoot == null)
            return;

        if (_networkManager != null && _networkManager.IsListening && !_networkManager.IsServer)
            return;

        List<Transform> spawns = new(8);
        CollectLightSpawnTransforms(mazeRoot, spawns);
        if (spawns.Count == 0)
            return;

        int want = Mathf.Min(ResolveMazeBuildPlayerCount(), spawns.Count);
        if (want <= 0)
            return;

        // World-space only: parenting under GeneratedMaze would require a NetworkObject on the parent, and
        // Netcode also rejects reparenting under non-NO hand bones if AutoObjectParentSync is on the item.
        bool spawnWithNetcode = _networkManager != null && _networkManager.IsListening;
        for (int i = 0; i < want; i++)
        {
            Transform t = spawns[i];
            GameObject instance = Instantiate(prefab, t.position, t.rotation);
            SceneManager.MoveGameObjectToScene(instance, mazeRoot.gameObject.scene);

            if (spawnWithNetcode)
            {
                if (instance.TryGetComponent(out NetworkObject networkObject) && networkObject != null)
                    networkObject.Spawn();
                else
                {
                    LogMazeWarningOnce(
                        "maze-start-flashlight-no-network-object",
                        "[Maze] Maze start flashlight prefab has no NetworkObject. Flashlights will not appear on clients; add NetworkObject to the pickup prefab.",
                        this);
                }
            }
        }
    }

    int ResolveMazeBuildPlayerCount()
    {
        if (IsServerListening() && _networkManager != null)
            return Mathf.Max(1, _networkManager.ConnectedClients.Count);
        return 1;
    }

    static void CollectLightSpawnTransforms(Transform mazeRoot, List<Transform> into)
    {
        Transform[] all = mazeRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null)
                continue;
            if (t.name.StartsWith(LightSpawnNamePrefix, StringComparison.Ordinal))
                into.Add(t);
        }

        into.Sort(CompareLightSpawnTransforms);
    }

    static int CompareLightSpawnTransforms(Transform a, Transform b)
    {
        int ka = GetLightSpawnSortKey(a != null ? a.name : null);
        int kb = GetLightSpawnSortKey(b != null ? b.name : null);
        if (ka != kb)
            return ka.CompareTo(kb);
        if (a == null || b == null)
            return 0;
        return a.GetSiblingIndex().CompareTo(b.GetSiblingIndex());
    }

    static int GetLightSpawnSortKey(string transformName)
    {
        if (string.IsNullOrEmpty(transformName)
            || !transformName.StartsWith(LightSpawnNamePrefix, StringComparison.Ordinal))
        {
            return 9999;
        }

        string suffix = transformName.Substring(LightSpawnNamePrefix.Length);
        if (string.IsNullOrEmpty(suffix))
            return 0;
        if (int.TryParse(suffix, out int n))
            return n;
        return 5000 + (Mathf.Abs(transformName.GetHashCode()) & 0x3fff);
    }

    void TrySpawnMazePosters(Transform mazeRoot, IReadOnlyDictionary<Vector2Int, Transform> cellRoots, int mazeSeed)
    {
        GameObject[] posterPrefabs = _config.MazePosterPrefabs;
        if (posterPrefabs == null || posterPrefabs.Length == 0)
            return;

        int nonNullPosters = 0;
        for (int i = 0; i < posterPrefabs.Length; i++)
        {
            if (posterPrefabs[i] != null)
                nonNullPosters++;
        }

        if (nonNullPosters == 0)
            return;

        float chance = _config.MazePosterSpawnChance;
        if (chance <= 0f)
            return;

        Transform postersRoot = CreateChild(mazeRoot, "GeneratedPosters");
        List<Transform> cellPosterSpawns = new(8);

        foreach (KeyValuePair<Vector2Int, Transform> pair in cellRoots)
        {
            Vector2Int cell = pair.Key;
            Transform cellRoot = pair.Value;
            if (cellRoot == null)
                continue;

            cellPosterSpawns.Clear();
            CollectPosterSpawns(cellRoot, cellPosterSpawns);

            for (int a = 0; a < cellPosterSpawns.Count; a++)
            {
                Transform anchor = cellPosterSpawns[a];
                if (anchor == null)
                    continue;

                int gateSeed = MixSeed(
                    mazeSeed,
                    MixSeed(cell.x, MixSeed(cell.y, MixSeed(a, unchecked((int)0x9057E511)))));
                if (!RollUnitInterval(gateSeed, chance))
                    continue;

                int pickSeed = MixSeed(gateSeed, unchecked((int)0xA11CE42));
                GameObject prefab = PickPosterPrefabFromPool(posterPrefabs, nonNullPosters, pickSeed);
                if (prefab == null)
                    continue;

                GameObject instance = Instantiate(prefab, Vector3.zero, Quaternion.identity, postersRoot);
                instance.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
            }
        }
    }

    static bool RollUnitInterval(int seed, float chance01)
    {
        unchecked
        {
            uint u = (uint)seed;
            u ^= u >> 16;
            u *= 0x7feb352du;
            u &= 0xffffffu;
            float u01 = u / 16777216f;
            return u01 < Mathf.Clamp01(chance01);
        }
    }

    static GameObject PickPosterPrefabFromPool(GameObject[] prefabs, int nonNullCount, int pickSeed)
    {
        if (prefabs == null || prefabs.Length == 0 || nonNullCount <= 0)
            return null;

        int ordinal = (int)((uint)MixSeed(pickSeed, unchecked((int)0x50E7E7E7)) % (uint)nonNullCount);
        for (int i = 0; i < prefabs.Length; i++)
        {
            if (prefabs[i] == null)
                continue;

            if (ordinal == 0)
                return prefabs[i];

            ordinal--;
        }

        return null;
    }

    static void CollectPosterSpawns(Transform root, List<Transform> into)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate == null)
                continue;

            if (string.Equals(candidate.name, PosterSpawnName, StringComparison.Ordinal))
                into.Add(candidate);
        }
    }

    void TrySpawnMazeChests(Transform mazeRoot, IReadOnlyDictionary<Vector2Int, Transform> cellRoots, int mazeSeed)
    {
        GameObject prefab = _config.MazeChestPrefab;
        if (prefab == null)
            return;

        if (_networkManager != null && _networkManager.IsListening && !_networkManager.IsServer)
            return;

        Transform chestsRoot = CreateChild(mazeRoot, "GeneratedChests");
        List<Transform> cellChestAnchors = new(4);
        int chestIndex = 0;
        bool spawnWithNetcode = _networkManager != null && _networkManager.IsListening;

        foreach (KeyValuePair<Vector2Int, Transform> pair in cellRoots)
        {
            Transform cellRoot = pair.Value;
            if (cellRoot == null)
                continue;

            cellChestAnchors.Clear();
            CollectChestAnchors(cellRoot, cellChestAnchors);

            for (int a = 0; a < cellChestAnchors.Count; a++)
            {
                Transform anchor = cellChestAnchors[a];
                GameObject instance = Instantiate(prefab, Vector3.zero, Quaternion.identity, chestsRoot);
                AlignChestInstanceToAnchor(instance.transform, anchor);
                int lootSeed = MixSeed(mazeSeed, MixSeed(chestIndex++, unchecked((int)0x7E5ECAB1)));

                MazeChest mazeChest = instance.GetComponent<MazeChest>();
                if (mazeChest != null)
                    mazeChest.ConfigureFromMaze(lootSeed);

                if (!spawnWithNetcode)
                    continue;

                NetworkObject networkObject = instance.GetComponent<NetworkObject>();
                if (networkObject != null)
                    networkObject.Spawn();
            }
        }
    }

    /// <summary>
    /// Spawns the configured teleport-orb prefab at up to <see cref="ProceduralMazeConfig.MazeTeleportOrbCount"/>
    /// of the child transforms named <see cref="TeleportOrbAnchorName"/> on generated maze pieces (mirrors
    /// <see cref="TrySpawnMazeChests"/>, but capped/sampled instead of unconditional-per-anchor). When there are
    /// more anchors than the requested count, a maze-seeded random subset is chosen so the choice is reproducible
    /// for a given seed; when there are fewer, one orb spawns at each available anchor. The orb prefab's pivot is
    /// at its base, so an anchor placed on the floor puts the pedestal on the floor. Server-spawned NetworkObjects
    /// are torn down each level by <see cref="ServerDespawnAllLevelNetworkObjects"/>.
    /// </summary>
    void TrySpawnMazeTeleportOrbs(Transform mazeRoot, IReadOnlyDictionary<Vector2Int, Transform> cellRoots, int mazeSeed)
    {
        GameObject prefab = _config.MazeTeleportOrbPrefab;
        int requestedCount = _config.MazeTeleportOrbCount;
        if (prefab == null || requestedCount <= 0)
            return;

        if (_networkManager != null && _networkManager.IsListening && !_networkManager.IsServer)
            return;

        // Gather every anchor maze-wide first (unlike chests, which spawn unconditionally per-anchor) so the
        // count cap can sample across the whole maze rather than per cell.
        List<Transform> allAnchors = new();
        List<Transform> cellOrbAnchors = new(2);
        foreach (KeyValuePair<Vector2Int, Transform> pair in cellRoots)
        {
            Transform cellRoot = pair.Value;
            if (cellRoot == null)
                continue;

            cellOrbAnchors.Clear();
            CollectTeleportOrbAnchors(cellRoot, cellOrbAnchors);
            allAnchors.AddRange(cellOrbAnchors);
        }

        if (allAnchors.Count == 0)
            return;

        List<Transform> chosenAnchors = allAnchors;
        if (allAnchors.Count > requestedCount)
        {
            System.Random rng = new(MixSeed(mazeSeed, unchecked((int)0x0B12EE55)));
            // Partial Fisher-Yates: shuffle only the prefix we need, so we don't waste swaps beyond requestedCount.
            for (int i = 0; i < requestedCount; i++)
            {
                int j = i + rng.Next(allAnchors.Count - i);
                (allAnchors[i], allAnchors[j]) = (allAnchors[j], allAnchors[i]);
            }
            chosenAnchors = allAnchors.GetRange(0, requestedCount);
        }

        Transform orbsRoot = CreateChild(mazeRoot, "GeneratedTeleportOrbs");
        bool spawnWithNetcode = _networkManager != null && _networkManager.IsListening;

        for (int a = 0; a < chosenAnchors.Count; a++)
        {
            Transform anchor = chosenAnchors[a];
            if (anchor == null)
                continue;

            GameObject instance = Instantiate(prefab, anchor.position, anchor.rotation, orbsRoot);

            if (!spawnWithNetcode)
                continue;

            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            if (networkObject != null)
                networkObject.Spawn();
        }
    }

    static void CollectTeleportOrbAnchors(Transform root, List<Transform> into)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate == null)
                continue;

            if (string.Equals(candidate.name, TeleportOrbAnchorName, StringComparison.Ordinal))
                into.Add(candidate);
        }
    }

    /// <summary>
    /// Spawns the configured RatBot prefab at up to <see cref="ProceduralMazeConfig.MazeRatBotCount"/> of the
    /// child transforms named <see cref="RatBotSpawnMarkerName"/> anywhere under the maze root (maze pieces AND
    /// interior rooms — unlike the per-cell orb collection, this is one whole-hierarchy scan). More markers than
    /// the count → a maze-seeded random subset, reproducible per seed. Called after the runtime NavMesh bake so
    /// the RatBots' NavMeshAgents land on a valid mesh. Server-spawned NetworkObjects are torn down each level by
    /// <see cref="ServerDespawnAllLevelNetworkObjects"/>.
    /// </summary>
    void TrySpawnMazeRatBots(Transform mazeRoot, int mazeSeed)
    {
        GameObject prefab = _config.MazeRatBotPrefab;
        int requestedCount = _config.MazeRatBotCount;
        if (prefab == null || requestedCount <= 0)
            return;

        if (_networkManager != null && _networkManager.IsListening && !_networkManager.IsServer)
            return;

        List<Transform> markers = new();
        Transform[] transforms = mazeRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate != null && string.Equals(candidate.name, RatBotSpawnMarkerName, StringComparison.Ordinal))
                markers.Add(candidate);
        }

        if (markers.Count == 0)
            return;

        List<Transform> chosen = markers;
        if (markers.Count > requestedCount)
        {
            System.Random rng = new(MixSeed(mazeSeed, unchecked((int)0x7A7B0757)));
            // Partial Fisher-Yates: shuffle only the prefix we need (teleport-orb idiom).
            for (int i = 0; i < requestedCount; i++)
            {
                int j = i + rng.Next(markers.Count - i);
                (markers[i], markers[j]) = (markers[j], markers[i]);
            }
            chosen = markers.GetRange(0, requestedCount);
        }

        Transform ratBotsRoot = CreateChild(mazeRoot, "GeneratedRatBots");
        bool spawnWithNetcode = _networkManager != null && _networkManager.IsListening;

        for (int a = 0; a < chosen.Count; a++)
        {
            Transform marker = chosen[a];
            if (marker == null)
                continue;

            GameObject instance = Instantiate(prefab, marker.position, Quaternion.Euler(0f, marker.eulerAngles.y, 0f), ratBotsRoot);

            if (!spawnWithNetcode)
                continue;

            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            if (networkObject != null)
                networkObject.Spawn();
        }
    }

    /// <summary>
    /// Spawns pickup items at child transforms whose name starts with <see cref="ItemSpawnNamePrefix"/> on
    /// generated maze pieces, plus any marker carrying a <see cref="MazeItemSpawnPoint"/> (which names its
    /// own prefab and rate instead of drawing from the level pool). Unlike chests/orbs these are NOT
    /// server-spawned NetworkObjects: every peer (and offline play) builds the same pickups locally from
    /// the maze seed, chest-loot style, and stamps a stable item id so pickup/consumption replicates
    /// through the usual item stores.
    /// </summary>
    void TrySpawnMazeItemPickups(Transform mazeRoot, IReadOnlyDictionary<Vector2Int, Transform> cellRoots, int mazeSeed)
    {
        GameObject[] itemPrefabs = _config.MazeItemSpawnPrefabs;
        int nonNullItems = 0;
        for (int i = 0; i < itemPrefabs.Length; i++)
        {
            if (itemPrefabs[i] != null)
                nonNullItems++;
        }

        float poolChance = _config.MazeItemSpawnChance;
        bool poolUsable = nonNullItems > 0 && poolChance > 0f;

        // Built on first use: a level whose pool is empty and whose pieces carry no per-marker overrides
        // (most of them) leaves no empty container behind.
        Transform itemsRoot = null;
        List<Transform> cellItemSpawns = new(4);

        foreach (KeyValuePair<Vector2Int, Transform> pair in cellRoots)
        {
            Vector2Int cell = pair.Key;
            Transform cellRoot = pair.Value;
            if (cellRoot == null)
                continue;

            cellItemSpawns.Clear();
            CollectItemSpawnMarkers(cellRoot, cellItemSpawns);

            for (int a = 0; a < cellItemSpawns.Count; a++)
            {
                Transform marker = cellItemSpawns[a];
                if (marker == null)
                    continue;

                MazeItemSpawnPoint spawnPoint = marker.GetComponent<MazeItemSpawnPoint>();
                GameObject overridePrefab = spawnPoint != null ? spawnPoint.ItemPrefab : null;
                bool isOverride = overridePrefab != null;
                if (!isOverride && !poolUsable)
                    continue;

                float chance = isOverride ? spawnPoint.SpawnChance : poolChance;
                if (chance <= 0f)
                    continue;

                // Per-marker seeds derive from maze seed + cell + marker index (not iteration order),
                // so every peer rolls identical loot regardless of dictionary enumeration order.
                int gateSeed = MixSeed(
                    mazeSeed,
                    MixSeed(cell.x, MixSeed(cell.y, MixSeed(a, unchecked((int)0x17E3A0B5)))));
                if (!RollUnitInterval(gateSeed, chance))
                    continue;

                GameObject prefab;
                int prefabIndex;
                if (isOverride)
                {
                    prefab = overridePrefab;
                    prefabIndex = OverrideItemPrefabIndex;
                }
                else
                {
                    int pickSeed = MixSeed(gateSeed, unchecked((int)0x5EEDBA11));
                    prefabIndex = PickNonNullPrefabIndex(itemPrefabs, nonNullItems, pickSeed);
                    if (prefabIndex < 0)
                        continue;

                    prefab = itemPrefabs[prefabIndex];
                }

                itemsRoot ??= CreateChild(mazeRoot, "GeneratedItemPickups");
                SpawnMazeItemPickup(
                    prefab,
                    marker,
                    itemsRoot,
                    mazeSeed,
                    cell,
                    a,
                    prefabIndex,
                    isOverride && spawnPoint.StandUprightOnMarker,
                    isOverride ? spawnPoint.UprightRotationOffset : Quaternion.identity);
            }
        }
    }

    static int PickNonNullPrefabIndex(GameObject[] prefabs, int nonNullCount, int pickSeed)
    {
        if (prefabs == null || prefabs.Length == 0 || nonNullCount <= 0)
            return -1;

        int pick = (int)((uint)MixSeed(pickSeed, unchecked((int)0x9E3779B9)) % (uint)nonNullCount);
        for (int i = 0; i < prefabs.Length; i++)
        {
            if (prefabs[i] == null)
                continue;

            if (pick == 0)
                return i;

            pick--;
        }

        return -1;
    }

    void SpawnMazeItemPickup(
        GameObject prefab,
        Transform marker,
        Transform itemsRoot,
        int mazeSeed,
        Vector2Int cell,
        int markerIndex,
        int prefabIndex,
        bool standUprightOnMarker,
        Quaternion uprightRotationOffset)
    {
        GameObject instance = Instantiate(prefab, marker.position, marker.rotation, itemsRoot);
        if (standUprightOnMarker)
            StandPickupOnMarker(instance, prefab, marker, uprightRotationOffset);

        if (instance.TryGetComponent(out GrabbableInventoryItem grabbable))
            grabbable.AssignNetworkItemId(ComputeMazeItemPickupId(mazeSeed, cell, markerIndex, prefabIndex));

        // Loot spawns as a stack rather than a single unit, matching MazeChest: a full pack of glowsticks,
        // or enough flare rounds for one full reload.
        if (instance.TryGetComponent(out GlowstickItem glowstick))
            glowstick.SetStackCount(GlowstickItem.MaxStack);
        else if (instance.TryGetComponent(out FlareAmmoItem flareAmmo))
            flareAmmo.SetStackCount(FlareAmmoItem.LootStackSize);

        ApplyMazeItemPickupAtRest(instance);
    }

    /// <summary>
    /// Stands a pickup up on its marker, for markers that name their own prefab.
    ///
    /// Two things make this more than "use the marker's transform". Item prefabs are not authored at
    /// identity — the EnergyDrink root, for one, carries a -90° X so the can is vertical — so upright means
    /// the prefab's own root rotation with the marker's yaw applied on top, not the marker's rotation.
    /// And item pivots sit at the mesh centre, so a marker placed on a table surface would bury the lower
    /// half of the item in it; the instance is lifted until the bottom of its bounds meets the marker.
    ///
    /// Both steps read only the prefab and the marker, so every peer lands on the same pose.
    /// </summary>
    static void StandPickupOnMarker(
        GameObject instance, GameObject prefab, Transform marker, Quaternion uprightRotationOffset)
    {
        Transform instanceRoot = instance.transform;
        instanceRoot.SetPositionAndRotation(
            marker.position,
            Quaternion.Euler(0f, marker.eulerAngles.y, 0f) * prefab.transform.rotation * uprightRotationOffset);

        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        bool hasBounds = false;
        Bounds bounds = new(instanceRoot.position, Vector3.zero);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;

            // Only solid geometry defines where the item's bottom is. A particle system with no live
            // particles (the flare gun's muzzle flash) reports a zero-size bounds at the world origin, and
            // so does any renderer on an inactive child (the gun's stowed shell meshes) — either one drags
            // the fit down to y=0 and lifts the item by the marker's entire world height.
            if (renderer is ParticleSystemRenderer)
                continue;

            Bounds rendererBounds = renderer.bounds;
            if (rendererBounds.size == Vector3.zero)
                continue;

            if (!hasBounds)
            {
                bounds = rendererBounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(rendererBounds);
            }
        }

        if (!hasBounds)
            return;

        instanceRoot.position += new Vector3(0f, marker.position.y - bounds.min.y, 0f);
    }

    static ulong ComputeMazeItemPickupId(int mazeSeed, Vector2Int cell, int markerIndex, int prefabIndex)
    {
        return ComputeStableItemHash($"maze-item:{mazeSeed}:{cell.x}:{cell.y}:{markerIndex}:{prefabIndex}");
    }

    static ulong ComputeStableItemHash(string key)
    {
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        ulong hash = fnvOffset;
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= fnvPrime;
        }

        return hash;
    }

    /// <summary>Maze pickups wait in place until grabbed (kinematic, no gravity), matching chest loot; pickup/drop restores physics.</summary>
    static void ApplyMazeItemPickupAtRest(GameObject root)
    {
        Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < bodies.Length; i++)
        {
            Rigidbody rb = bodies[i];
            if (rb == null)
                continue;

            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.isKinematic = true;
        }

        // Freezing once is not enough for an item that is never touched — see MazeItemPickupRest, which
        // holds the pose until a player actually picks the item up.
        if (bodies.Length > 0 && root.GetComponent<MazeItemPickupRest>() == null)
            root.AddComponent<MazeItemPickupRest>();
    }

    /// <summary>
    /// Drops decorative bat colonies into maze-seeded dead ends, corridors and corners (any cell with one
    /// or two openings — junctions are excluded, they're too open to read as a roost).
    ///
    /// Unlike every other spawner here these are NOT server-spawned NetworkObjects, and there is
    /// deliberately no IsServer gate: the bats are cosmetic, so each peer builds its own roosts from the
    /// maze seed (identical cells everywhere) and fires them for its own local player. Two players who
    /// walk into the same dead end a minute apart each get the scare, and nothing crosses the wire.
    ///
    /// Two things make this more than a cell filter:
    /// <list type="bullet">
    /// <item>A dead end's one opening necessarily faces where the player came from, so bats burst out and
    /// carry on past them. A corridor or corner has two, and which one leads away from the player isn't
    /// known until they arrive — so every opening is handed to <see cref="BatSwarmRoost"/> and the choice
    /// is made at fire time. The roost's +Z is still aimed down the first opening as a fallback for
    /// hand-placed roosts.</item>
    /// <item>Straights outnumber dead ends by a wide margin, so an even draw across all eligible cells
    /// would effectively delete dead-end roosts. <see cref="ProceduralMazeConfig.MazeBatDeadEndShare"/>
    /// reserves a slice of the roost budget for them.</item>
    /// </list>
    /// </summary>
    void TrySpawnMazeBatRoosts(
        Transform mazeRoot,
        MazeCell[,] grid,
        IReadOnlyDictionary<Vector2Int, Transform> cellRoots,
        Vector2Int start,
        Vector2Int exit,
        Vector2Int? jailDeadEndCell,
        InteriorRoomBuildPlan interiorPlan,
        int seed,
        float cellSize)
    {
        GameObject prefab = _config.MazeBatRoostPrefab;
        int requestedCount = _config.MazeBatRoostCount;
        if (prefab == null || requestedCount <= 0)
            return;

        int width = grid.GetLength(0);
        int height = grid.GetLength(1);

        // Walk the grid in fixed order rather than enumerating cellRoots, so the candidate lists — and
        // therefore the seeded shuffles below — come out identical on every peer.
        List<Vector2Int> deadEnds = new();
        List<Vector2Int> passages = new();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                MazeFaceMask openings = grid[x, y].Openings;
                if (openings == MazeFaceMask.None)
                    continue;

                int openFaces = MazeFaceMaskUtility.CountOpenFaces(openings);
                if (openFaces > 2)
                    continue; // tees and crossroads read as junctions, not roosts

                Vector2Int cell = new(x, y);
                if (cell == start || cell == exit)
                    continue;
                if (jailDeadEndCell.HasValue && cell == jailDeadEndCell.Value)
                    continue;
                if (interiorPlan.SkipCells.Contains(cell) || interiorPlan.Anchors.ContainsKey(cell))
                    continue;
                if (!cellRoots.ContainsKey(cell))
                    continue;

                if (openFaces == 1)
                    deadEnds.Add(cell);
                else
                    passages.Add(cell);
            }
        }

        if (deadEnds.Count == 0 && passages.Count == 0)
        {
            LogMazeWarningOnce(
                "maze-bats-no-cells",
                "[Maze] Bat roosts are configured but this layout produced no free dead-end, corridor or corner cells (start, exit, jail and interior-room cells are excluded).",
                this);
            return;
        }

        // Separate seeds so adding or removing a dead end doesn't reshuffle the corridor picks too.
        ShuffleList(deadEnds, new System.Random(MixSeed(seed, unchecked((int)0x0BA7B00C))));
        ShuffleList(passages, new System.Random(MixSeed(seed, unchecked((int)0x0BA7C0DE))));

        List<Vector2Int> ordered = BuildBatRoostPreferenceOrder(deadEnds, passages, requestedCount);
        List<Vector2Int> chosen = SelectSpacedCells(
            ordered, requestedCount, _config.MazeBatRoostMinCellSeparation);

        Transform roostsRoot = CreateChild(mazeRoot, "GeneratedBatRoosts");
        Vector3[] directionScratch = new Vector3[4];

        for (int i = 0; i < chosen.Count; i++)
        {
            Vector2Int cell = chosen[i];
            MazeFaceMask openings = grid[cell.x, cell.y].Openings;

            int exitCount = CollectOpeningDirections(openings, directionScratch);
            Vector3[] exits = new Vector3[exitCount];
            System.Array.Copy(directionScratch, exits, exitCount);

            Vector3 position = CellToWorld(cell.x, cell.y, cellSize) + Vector3.up * _config.MazeBatRoostHeight;
            Quaternion rotation = Quaternion.LookRotation(
                exitCount > 0 ? exits[0] : Vector3.forward, Vector3.up);

            GameObject instance = Instantiate(prefab, position, rotation, roostsRoot);
            instance.name = $"BatRoost_{cell.x}_{cell.y}";

            if (instance.TryGetComponent(out BatSwarmRoost roost))
            {
                roost.ConfigureSwarmSize(_config.MazeBatsPerRoost);
                roost.ConfigureExitDirections(exits);
            }
        }

        if (chosen.Count < requestedCount)
        {
            LogMazeWarningOnce(
                "maze-bats-trimmed",
                $"[Maze] Requested {requestedCount} bat roosts but only placed {chosen.Count} from "
                    + $"{deadEnds.Count} dead-end and {passages.Count} corridor/corner candidate(s).",
                this);
        }
    }

    /// <summary>
    /// Orders candidates so the first <see cref="ProceduralMazeConfig.MazeBatDeadEndShare"/> of the roost
    /// budget comes from dead ends, then corridors/corners, then any dead ends left over. Selection just
    /// walks this list front to back, so the reserve is honoured without the dead ends being able to
    /// starve the corridors (or vice versa) when one pool is short.
    /// </summary>
    List<Vector2Int> BuildBatRoostPreferenceOrder(
        List<Vector2Int> deadEnds,
        List<Vector2Int> passages,
        int requestedCount)
    {
        List<Vector2Int> ordered = new(deadEnds.Count + passages.Count);

        int quota = Mathf.Clamp(
            Mathf.RoundToInt(requestedCount * _config.MazeBatDeadEndShare), 0, requestedCount);

        int taken = 0;
        for (int i = 0; i < deadEnds.Count && taken < quota; i++, taken++)
            ordered.Add(deadEnds[i]);

        for (int i = 0; i < passages.Count; i++)
            ordered.Add(passages[i]);

        for (int i = taken; i < deadEnds.Count; i++)
            ordered.Add(deadEnds[i]);

        return ordered;
    }

    /// <summary>
    /// Greedily takes cells in preference order while keeping them <paramref name="minSeparation"/> cells
    /// apart, relaxing the spacing a step at a time if the layout can't fill the budget at full
    /// separation. Without the relax pass a tight maze would silently return half the requested count.
    /// Shared by the bat roosts and the roach colonies.
    /// </summary>
    static List<Vector2Int> SelectSpacedCells(List<Vector2Int> ordered, int requestedCount, int minSeparation)
    {
        List<Vector2Int> chosen = new(requestedCount);

        for (int separation = minSeparation;
             separation >= 0 && chosen.Count < requestedCount;
             separation--)
        {
            for (int i = 0; i < ordered.Count && chosen.Count < requestedCount; i++)
            {
                Vector2Int candidate = ordered[i];
                if (chosen.Contains(candidate))
                    continue;
                if (!IsCellSeparatedFrom(candidate, chosen, separation))
                    continue;

                chosen.Add(candidate);
            }
        }

        return chosen;
    }

    /// <summary>Chebyshev cell distance test — diagonal neighbours count as adjacent.</summary>
    static bool IsCellSeparatedFrom(Vector2Int candidate, List<Vector2Int> placed, int minSeparation)
    {
        if (minSeparation <= 0)
            return true;

        for (int i = 0; i < placed.Count; i++)
        {
            int dx = Mathf.Abs(candidate.x - placed[i].x);
            int dy = Mathf.Abs(candidate.y - placed[i].y);
            if (Mathf.Max(dx, dy) < minSeparation)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Writes each open face's world direction into <paramref name="into"/> and returns how many. Grid +x
    /// is world +X and grid +y is world +Z (see <see cref="CellToWorld"/>), matching <see cref="Steps"/>.
    /// </summary>
    static int CollectOpeningDirections(MazeFaceMask openings, Vector3[] into)
    {
        int count = 0;
        if ((openings & MazeFaceMask.North) != 0)
            into[count++] = Vector3.forward;
        if ((openings & MazeFaceMask.East) != 0)
            into[count++] = Vector3.right;
        if ((openings & MazeFaceMask.South) != 0)
            into[count++] = Vector3.back;
        if ((openings & MazeFaceMask.West) != 0)
            into[count++] = Vector3.left;

        return count;
    }

    /// <summary>
    /// Drops decorative cockroach nests into maze-seeded cells. Cosmetic and local-only, same contract as
    /// <see cref="TrySpawnMazeBatRoosts"/> — no NetworkObject, no IsServer gate, identical cells on every
    /// peer. Any cell will do here (roaches don't need an escape corridor the way the bats do), so this
    /// only filters out the start, exit, jail and interior-room cells.
    ///
    /// The colony is placed at floor level and works out its own floor and wall surfaces by raycast, so
    /// nothing here needs to know which décor variant the cell rolled.
    /// </summary>
    void TrySpawnMazeRoachColonies(
        Transform mazeRoot,
        MazeCell[,] grid,
        IReadOnlyDictionary<Vector2Int, Transform> cellRoots,
        Vector2Int start,
        Vector2Int exit,
        Vector2Int? jailDeadEndCell,
        InteriorRoomBuildPlan interiorPlan,
        int seed,
        float cellSize)
    {
        GameObject prefab = _config.MazeRoachColonyPrefab;
        int requestedCount = _config.MazeRoachColonyCount;
        if (prefab == null || requestedCount <= 0)
            return;

        int width = grid.GetLength(0);
        int height = grid.GetLength(1);

        // Fixed grid order, as everywhere else here, so the seeded shuffle matches across peers.
        List<Vector2Int> candidates = new();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (grid[x, y].Openings == MazeFaceMask.None)
                    continue;

                Vector2Int cell = new(x, y);
                if (cell == start || cell == exit)
                    continue;
                if (jailDeadEndCell.HasValue && cell == jailDeadEndCell.Value)
                    continue;
                if (interiorPlan.SkipCells.Contains(cell) || interiorPlan.Anchors.ContainsKey(cell))
                    continue;
                if (!cellRoots.ContainsKey(cell))
                    continue;

                candidates.Add(cell);
            }
        }

        if (candidates.Count == 0)
            return;

        ShuffleList(candidates, new System.Random(MixSeed(seed, unchecked((int)0x0C0C4B0A))));

        List<Vector2Int> chosen = SelectSpacedCells(
            candidates, requestedCount, _config.MazeRoachColonyMinCellSeparation);

        Transform coloniesRoot = CreateChild(mazeRoot, "GeneratedRoachColonies");

        for (int i = 0; i < chosen.Count; i++)
        {
            Vector2Int cell = chosen[i];
            Vector3 position = CellToWorld(cell.x, cell.y, cellSize)
                             + Vector3.up * _config.MazeRoachColonyHeight;

            GameObject instance = Instantiate(prefab, position, Quaternion.identity, coloniesRoot);
            instance.name = $"RoachColony_{cell.x}_{cell.y}";

            if (instance.TryGetComponent(out RoachColony colony))
                colony.ConfigureColony(_config.MazeRoachesPerColony, _config.MazeRoachColonySpread);
        }

        if (chosen.Count < requestedCount)
        {
            LogMazeWarningOnce(
                "maze-roaches-trimmed",
                $"[Maze] Requested {requestedCount} roach colonies but only placed {chosen.Count} from "
                    + $"{candidates.Count} candidate cell(s).",
                this);
        }
    }

    static void CollectItemSpawnMarkers(Transform root, List<Transform> into)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate == null)
                continue;

            // A MazeItemSpawnPoint identifies its own marker, so those are free to be named after what they
            // hold ("EnergyDrinkSpawn") rather than carrying the ItemSpawn prefix.
            if (candidate.name.StartsWith(ItemSpawnNamePrefix, StringComparison.Ordinal)
                || candidate.GetComponent<MazeItemSpawnPoint>() != null)
                into.Add(candidate);
        }
    }

    static void CollectChestAnchors(Transform root, List<Transform> into)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate == null)
                continue;

            if (string.Equals(candidate.name, ChestAnchorName, StringComparison.Ordinal))
                into.Add(candidate);
        }
    }

    static void AlignChestInstanceToAnchor(Transform chestRoot, Transform anchor)
    {
        if (chestRoot == null || anchor == null)
            return;

        if (!TryFindNamedChild(chestRoot, ChestMountPointName, out Transform mountPoint))
        {
            chestRoot.SetPositionAndRotation(anchor.position, anchor.rotation);
            return;
        }

        Quaternion rootRotation = anchor.rotation * Quaternion.Inverse(mountPoint.localRotation);
        Vector3 rootPosition = anchor.position - rootRotation * mountPoint.localPosition;
        chestRoot.SetPositionAndRotation(rootPosition, rootRotation);
    }

    /// <summary>
    /// Picks a random point on the runtime-baked NavMesh that a player can be safely teleported to: it is
    /// on the walkable floor (never a roof or pit), inset from walls/props by the bake's agent radius, and
    /// re-checked for standing clearance so the player never spawns clipping geometry. The returned
    /// position already includes the same floor lift used for normal player spawns
    /// (<see cref="ProceduralMazeConfig.SpawnHeight"/>). Intended to be called by the server/host (the
    /// NavMesh is baked locally on every peer, so an offline caller also works).
    /// </summary>
    /// <param name="avoidNearWorld">Prefer a point at least <paramref name="minDistanceFromAvoid"/> from here (e.g. the orb) so the teleport actually relocates the player.</param>
    /// <param name="minDistanceFromAvoid">Preferred minimum distance from <paramref name="avoidNearWorld"/>; relaxed automatically if no far point clears.</param>
    /// <param name="clearanceRadius">Player capsule radius used for the anti-clip check.</param>
    /// <param name="clearanceHeight">Player capsule height used for the anti-clip check.</param>
    /// <param name="worldPosition">The resolved, ready-to-teleport position.</param>
    /// <returns>False only if there is no baked NavMesh or no clearance-valid point was found after retries.</returns>
    public bool TryGetRandomWalkableTeleportPoint(
        Vector3 avoidNearWorld,
        float minDistanceFromAvoid,
        float clearanceRadius,
        float clearanceHeight,
        out Vector3 worldPosition)
    {
        worldPosition = default;

        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        int triCount = (tri.indices != null ? tri.indices.Length : 0) / 3;
        if (triCount == 0)
            return false;

        // Area-weighted cumulative table so points are distributed roughly uniformly over the walkable
        // floor area (not biased toward tiny sliver triangles near walls/corners).
        float[] cumulativeArea = new float[triCount];
        float totalArea = 0f;
        for (int t = 0; t < triCount; t++)
        {
            Vector3 a = tri.vertices[tri.indices[t * 3 + 0]];
            Vector3 b = tri.vertices[tri.indices[t * 3 + 1]];
            Vector3 c = tri.vertices[tri.indices[t * 3 + 2]];
            totalArea += 0.5f * Vector3.Cross(b - a, c - a).magnitude;
            cumulativeArea[t] = totalArea;
        }
        if (totalArea <= 0f)
            return false;

        float floorLift = Mathf.Max(0.05f, _config != null ? _config.SpawnHeight : 0.1f);
        float minDistSqr = minDistanceFromAvoid * minDistanceFromAvoid;

        const int maxAttempts = 32;
        bool hasNearFallback = false;
        Vector3 nearFallback = default;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            Vector3 onMesh = SampleRandomPointOnTriangulation(tri, cumulativeArea, totalArea);

            // Snap onto the authoritative walkable surface (rejects any numerical drift off the triangle).
            if (!NavMesh.SamplePosition(onMesh, out NavMeshHit navHit, 1.0f, NavMesh.AllAreas))
                continue;

            Vector3 floorPos = navHit.position;
            if (!HasStandingClearance(floorPos, clearanceRadius, clearanceHeight))
                continue;

            Vector3 candidate = floorPos + Vector3.up * floorLift;

            if ((candidate - avoidNearWorld).sqrMagnitude >= minDistSqr)
            {
                worldPosition = candidate;
                return true;
            }

            if (!hasNearFallback)
            {
                nearFallback = candidate;
                hasNearFallback = true;
            }
        }

        // Nothing was far enough, but we did find at least one clearance-valid (closer) point — use it
        // rather than fail (common on small maps where every walkable point is within minDistance).
        if (hasNearFallback)
        {
            worldPosition = nearFallback;
            return true;
        }

        return false;
    }

    static Vector3 SampleRandomPointOnTriangulation(NavMeshTriangulation tri, float[] cumulativeArea, float totalArea)
    {
        float r = UnityEngine.Random.value * totalArea;
        int lo = 0;
        int hi = cumulativeArea.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (cumulativeArea[mid] < r)
                lo = mid + 1;
            else
                hi = mid;
        }

        int triIndex = lo;
        Vector3 a = tri.vertices[tri.indices[triIndex * 3 + 0]];
        Vector3 b = tri.vertices[tri.indices[triIndex * 3 + 1]];
        Vector3 c = tri.vertices[tri.indices[triIndex * 3 + 2]];

        float u = UnityEngine.Random.value;
        float v = UnityEngine.Random.value;
        if (u + v > 1f)
        {
            u = 1f - u;
            v = 1f - v;
        }
        return a + u * (b - a) + v * (c - a);
    }

    /// <summary>
    /// True if an upright capsule of the given size standing on <paramref name="floorPos"/> does not overlap
    /// any solid (non-trigger) collider — i.e. a teleported player will not clip a wall or prop. Starts
    /// slightly above the floor so the floor collider itself is not counted, and shrinks the radius a hair
    /// so a point exactly on the NavMesh edge (inset by the agent radius) is not falsely rejected.
    /// </summary>
    static bool HasStandingClearance(Vector3 floorPos, float radius, float height)
    {
        float r = Mathf.Max(0.05f, radius * 0.95f);
        float h = Mathf.Max(r * 2f + 0.1f, height);
        Vector3 bottom = floorPos + Vector3.up * (r + 0.06f);
        Vector3 top = floorPos + Vector3.up * (h - r);
        return !Physics.CheckCapsule(bottom, top, r, Physics.AllLayers, QueryTriggerInteraction.Ignore);
    }

    float ResolveMazeEnemyMinSeparationXZ(float cellSize)
    {
        if (_config.MazeEnemyMinSeparation > 0f)
            return Mathf.Max(0.25f, _config.MazeEnemyMinSeparation);

        return Mathf.Clamp(cellSize * 0.2f, 1.15f, 2.6f);
    }

    float ResolveMazeTrapMinSeparationXZ(float cellSize)
    {
        if (_config.MazeTrapMinSeparation > 0f)
            return Mathf.Max(0.25f, _config.MazeTrapMinSeparation);

        return Mathf.Clamp(cellSize * 0.25f, 1.5f, 4f);
    }

    Vector3 ResolveSpawnPositionWithSeparation(
        Vector2Int cell,
        float cellSize,
        float yOffset,
        int mazeSeed,
        int spawnIndex,
        List<Vector3> placed,
        float minSeparationXZ,
        InteriorRoomBuildPlan interiorPlan,
        List<Transform> mazeTrapRoots)
    {
        bool tightTrapGeometry = mazeTrapRoots != null && mazeTrapRoots.Count > 0;
        int maxAttempts = tightTrapGeometry ? 48 : 18;
        float minSqr = minSeparationXZ * minSeparationXZ;
        Vector3 best = default;
        float bestScore = -1f;
        bool hasValidBest = false;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!TryGetMazeEnemySpawnWorldPosition(
                    cell, cellSize, yOffset, mazeSeed, spawnIndex, attempt, interiorPlan, mazeTrapRoots, out Vector3 candidate))
                continue;
            if (IsMazeEnemySpawnOverlappingTrapGeometry(candidate, mazeTrapRoots))
                continue;

            if (IsFarEnoughXZ(candidate, placed, minSqr))
                return candidate;

            float score = MinHorizontalSqrDistanceToAny(candidate, placed);
            if (!hasValidBest || score > bestScore)
            {
                bestScore = score;
                best = candidate;
                hasValidBest = true;
            }
        }

        if (hasValidBest)
            return best;

        LogMazeWarningOnce(
            "enemy-spawn-floor-fallback",
            "[Maze] Could not confirm a NavMesh-walkable spawn near this cell after retries; using cell-center floor fallback. "
            + "Check NavMesh bake / trap colliders vs. corridor width.",
            this);
        return GetMazeEnemySpawnFallbackPosition(cell, cellSize, yOffset, interiorPlan);
    }

    /// <summary>
    /// True if a capsule around the spawn feet intersects pit triggers or any solid collider on a spawned maze-trap prefab (roof cap, etc.).
    /// </summary>
    static bool IsMazeEnemySpawnOverlappingTrapGeometry(Vector3 worldPosition, List<Transform> mazeTrapRoots)
    {
        Vector3 probeCenter = worldPosition + Vector3.up * 0.45f;
        const float probeRadius = 0.55f;
        Collider[] overlaps = Physics.OverlapSphere(probeCenter, probeRadius, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider c = overlaps[i];
            if (c != null && IsColliderExcludedFromEnemySpawn(c, mazeTrapRoots))
                return true;
        }

        return false;
    }

    /// <summary>Pit kill volumes and all geometry parented under spawned maze-trap instances (MG_Pit roof is often not under <see cref="PitKillZone"/>).</summary>
    static bool IsColliderExcludedFromEnemySpawn(Collider collider, List<Transform> mazeTrapRoots)
    {
        if (collider == null)
            return false;

        if (collider.GetComponentInParent<PitKillZone>() != null)
            return true;

        if (mazeTrapRoots == null || mazeTrapRoots.Count == 0)
            return false;

        Transform t = collider.transform;
        for (int r = 0; r < mazeTrapRoots.Count; r++)
        {
            Transform root = mazeTrapRoots[r];
            if (root != null && t.IsChildOf(root))
                return true;
        }

        return false;
    }

    static bool IsFarEnoughXZ(Vector3 candidate, List<Vector3> placed, float minSqr)
    {
        for (int i = 0; i < placed.Count; i++)
        {
            float dx = candidate.x - placed[i].x;
            float dz = candidate.z - placed[i].z;
            if (dx * dx + dz * dz < minSqr)
                return false;
        }

        return true;
    }

    static float MinHorizontalSqrDistanceToAny(Vector3 candidate, List<Vector3> placed)
    {
        if (placed.Count == 0)
            return float.MaxValue;

        float best = float.MaxValue;
        for (int i = 0; i < placed.Count; i++)
        {
            float dx = candidate.x - placed[i].x;
            float dz = candidate.z - placed[i].z;
            float sqr = dx * dx + dz * dz;
            if (sqr < best)
                best = sqr;
        }

        return best;
    }

    // Vertical band, relative to the corridor floor, within which a NavMesh sample is accepted as
    // "the same walkable layer". Anything outside is a roof/wall-top above or a pit below.
    const float MazeEnemyNavMaxAboveFloor = 1.65f;
    const float MazeEnemyNavMaxBelowFloor = 1.2f;

    /// <summary>
    /// Resolves a jittered, NavMesh-confirmed spawn position for one placement attempt. Returns false
    /// when no walkable point can be confirmed within the corridor-floor band — that happens when the
    /// down-ray's only "floor" is actually a roof, a wall top, or a pit roof-cap (e.g. the jitter pushed
    /// the probe over a pit). Returning false lets the caller try another jitter or fall back to a
    /// guaranteed floor-level position, instead of stranding the enemy on top of the maze.
    /// </summary>
    bool TryGetMazeEnemySpawnWorldPosition(
        Vector2Int cell,
        float cellSize,
        float yOffset,
        int mazeSeed,
        int spawnIndex,
        int placementAttempt,
        InteriorRoomBuildPlan interiorPlan,
        List<Transform> mazeTrapRoots,
        out Vector3 position)
    {
        position = default;
        Vector3 cellCenter = ResolveMazeEnemySpawnHorizontalCellOrigin(cell, cellSize, interiorPlan);
        int jitterSeed = MixSeed(mazeSeed, MixSeed(spawnIndex, MixSeed(placementAttempt, unchecked((int)0x51A4EED5))));
        System.Random jitterRng = new(jitterSeed);
        float jitterRadius = Mathf.Min(cellSize * 0.48f, cellSize * 0.35f * (1f + placementAttempt * 0.14f));
        float jx = (float)(jitterRng.NextDouble() * 2d - 1d) * jitterRadius;
        float jz = (float)(jitterRng.NextDouble() * 2d - 1d) * jitterRadius;
        Vector3 xzOnGrid = cellCenter + new Vector3(jx, 0f, jz);

        // Reference floor Y: the down-ray hit when we have one, otherwise the maze corridor floor
        // (cell-origin Y from _config.Origin). The down-ray result is only a *seed* for the band check
        // below — it is never trusted as the final height, because it may be a roof/wall top.
        float referenceFloorY = TryFindMazeEnemySpawnFloor(xzOnGrid, cellSize, out Vector3 floorPoint, mazeTrapRoots)
            ? floorPoint.y
            : xzOnGrid.y;

        Vector3 sampleFrom = new Vector3(xzOnGrid.x, referenceFloorY, xzOnGrid.z) + Vector3.up * 0.35f;
        float[] snapRadii =
        {
            Mathf.Clamp(cellSize * 0.22f, 0.75f, 3f),
            Mathf.Clamp(cellSize * 0.5f, 2.5f, 8f)
        };

        for (int i = 0; i < snapRadii.Length; i++)
        {
            if (!NavMesh.SamplePosition(sampleFrom, out NavMeshHit navHit, snapRadii[i], NavMesh.AllAreas))
                continue;

            // Stay on the walkable layer the reference floor sits on — reject NavMesh patches far above
            // (roofs/wall tops) or far below (pits). A roof "floor" seed has no NavMesh in-band, so the
            // whole attempt fails here rather than spawning the enemy on top of the maze.
            if (navHit.position.y <= referenceFloorY + MazeEnemyNavMaxAboveFloor
                && navHit.position.y >= referenceFloorY - MazeEnemyNavMaxBelowFloor)
            {
                position = navHit.position + Vector3.up * yOffset;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Floor-level fallback used only when no jittered attempt produced a NavMesh-confirmed spawn.
    /// Never returns a roof/wall top: snaps to NavMesh near the cell-center corridor floor, or the raw
    /// corridor floor (cell-origin Y + offset) when NavMesh is unavailable there.
    /// </summary>
    Vector3 GetMazeEnemySpawnFallbackPosition(
        Vector2Int cell,
        float cellSize,
        float yOffset,
        InteriorRoomBuildPlan interiorPlan)
    {
        Vector3 cellCenter = ResolveMazeEnemySpawnHorizontalCellOrigin(cell, cellSize, interiorPlan);
        Vector3 sampleFrom = cellCenter + Vector3.up * 0.5f;
        float radius = Mathf.Clamp(cellSize * 0.5f, 1f, 8f);
        if (NavMesh.SamplePosition(sampleFrom, out NavMeshHit navHit, radius, NavMesh.AllAreas)
            && navHit.position.y <= cellCenter.y + MazeEnemyNavMaxAboveFloor
            && navHit.position.y >= cellCenter.y - MazeEnemyNavMaxBelowFloor)
            return navHit.position + Vector3.up * yOffset;

        return cellCenter + Vector3.up * yOffset;
    }

    /// <summary>
    /// Finds a floor under <paramref name="xzOnGrid"/> for enemy spawns. A long down-ray can hit
    /// several upward-facing colliders: exterior roof, corridor floor, pit floor, etc. We pick
    /// the one that is actually the corridor floor (not the min-Y pit bottom, not the max-Y roof).
    /// </summary>
    static bool TryFindMazeEnemySpawnFloor(
        Vector3 xzOnGrid,
        float cellSize,
        out Vector3 floorPoint,
        List<Transform> mazeTrapRoots)
    {
        floorPoint = default;
        float rayStartY = xzOnGrid.y + Mathf.Max(24f, cellSize * 2f);
        Vector3 origin = new(xzOnGrid.x, rayStartY, xzOnGrid.z);
        float rayLength = Mathf.Max(80f, cellSize * 5f);

        RaycastHit[] raw = Physics.RaycastAll(
            origin,
            Vector3.down,
            rayLength,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);

        if (raw == null || raw.Length == 0)
            return false;

        const float minFloorNormalY = 0.55f;
        if (!TryBuildUpFacingHitList(raw, minFloorNormalY, mazeTrapRoots, out var list) || list.Count == 0)
            return false;

        list.Sort(CompareHitByYDesc);

        if (list.Count == 1)
        {
            RaycastHit only = list[0];
            if (only.point.y > 2.25f
                && TryFindFloorFromInteriorProbe(
                    xzOnGrid,
                    only.point.y,
                    minFloorNormalY,
                    mazeTrapRoots,
                    out Vector3 fromProbe))
            {
                floorPoint = fromProbe;
                return true;
            }

            floorPoint = only.point;
            return true;
        }

        floorPoint = SelectSpawnFloorFromGapsInSortedDescList(list);
        return true;
    }

    static readonly float[] MazeEnemyInteriorProbeDropsY =
    {
        1.1f, 2.0f, 3.2f, 4.5f, 6.5f
    };

    static int CompareHitByYDesc(RaycastHit a, RaycastHit b) =>
        b.point.y.CompareTo(a.point.y);

    static bool TryBuildUpFacingHitList(
        RaycastHit[] raw,
        float minFloorNormalY,
        List<Transform> mazeTrapRoots,
        out System.Collections.Generic.List<RaycastHit> list)
    {
        list = new System.Collections.Generic.List<RaycastHit>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i].normal.y < minFloorNormalY)
                continue;
            if (IsColliderExcludedFromEnemySpawn(raw[i].collider, mazeTrapRoots))
                continue;

            list.Add(raw[i]);
        }

        return list.Count > 0;
    }

    static Vector3 SelectSpawnFloorFromGapsInSortedDescList(
        System.Collections.Generic.List<RaycastHit> sortedDesc)
    {
        const float deepPitY = -2.5f;
        if (sortedDesc.Count < 2)
            return sortedDesc[0].point;

        int bestIndex = 0;
        float bestGap = 0f;
        for (int i = 0; i < sortedDesc.Count - 1; i++)
        {
            float gap = sortedDesc[i].point.y - sortedDesc[i + 1].point.y;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestIndex = i;
            }
        }

        float hiY = sortedDesc[bestIndex].point.y;
        float loY = sortedDesc[bestIndex + 1].point.y;
        if (loY < deepPitY)
            return sortedDesc[bestIndex].point;

        return sortedDesc[bestIndex + 1].point;
    }

    static bool TryFindFloorFromInteriorProbe(
        Vector3 xzOnGrid,
        float topSurfaceY,
        float minFloorNormalY,
        List<Transform> mazeTrapRoots,
        out Vector3 floorPoint)
    {
        floorPoint = default;
        float x = xzOnGrid.x;
        float z = xzOnGrid.z;
        for (int d = 0; d < MazeEnemyInteriorProbeDropsY.Length; d++)
        {
            float yStart = topSurfaceY - MazeEnemyInteriorProbeDropsY[d];
            if (yStart < -60f)
                break;

            Vector3 origin = new(x, yStart, z);
            RaycastHit[] raw = Physics.RaycastAll(
                origin,
                Vector3.down,
                100f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            if (raw == null || raw.Length == 0)
                continue;

            if (!TryBuildUpFacingHitList(raw, minFloorNormalY, mazeTrapRoots, out var list) || list.Count == 0)
                continue;

            if (list.Count == 1)
            {
                floorPoint = list[0].point;
                if (floorPoint.y < topSurfaceY - 0.35f)
                    return true;
            }
            else
            {
                list.Sort(CompareHitByYDesc);
                floorPoint = SelectSpawnFloorFromGapsInSortedDescList(list);
                if (floorPoint.y < topSurfaceY - 0.35f)
                    return true;
            }
        }

        return false;
    }

    int?[,] ComputeMazeCellDistances(MazeCell[,] cells, Vector2Int start)
    {
        int width = cells.GetLength(0);
        int height = cells.GetLength(1);
        int?[,] distances = new int?[width, height];

        if (cells[start.x, start.y].Openings == MazeFaceMask.None)
            return distances;

        Queue<Vector2Int> queue = new();
        distances[start.x, start.y] = 0;
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            int nextDistance = distances[current.x, current.y]!.Value + 1;

            for (int i = 0; i < Steps.Length; i++)
            {
                DirectionStep step = Steps[i];
                if ((cells[current.x, current.y].Openings & step.Direction) == 0)
                    continue;

                int nextX = current.x + step.Dx;
                int nextY = current.y + step.Dy;
                if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height)
                    continue;

                if (cells[nextX, nextY].Openings == MazeFaceMask.None)
                    continue;

                if (distances[nextX, nextY].HasValue)
                    continue;

                distances[nextX, nextY] = nextDistance;
                queue.Enqueue(new Vector2Int(nextX, nextY));
            }
        }

        return distances;
    }

    static void CollectTrapAnchors(Transform root, List<Transform> into)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate == null)
                continue;

            string name = candidate.name;
            if (string.Equals(name, TrapAnchorName, StringComparison.Ordinal) ||
                string.Equals(name, TrapAnchor2Name, StringComparison.Ordinal))
                into.Add(candidate);
        }
    }

    static bool TryFindTrapMountPoint(Transform root, out Transform mountPoint)
    {
        return TryFindNamedChild(root, TrapMountPointName, out mountPoint);
    }

    static bool TryFindNamedChild(Transform root, string childName, out Transform match)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate == null || !string.Equals(candidate.name, childName, StringComparison.Ordinal))
                continue;

            match = candidate;
            return true;
        }

        match = null;
        return false;
    }

    static void AlignTrapInstanceToAnchor(Transform trapRoot, Transform anchor)
    {
        if (trapRoot == null || anchor == null)
            return;

        if (!TryFindTrapMountPoint(trapRoot, out Transform mountPoint))
        {
            trapRoot.SetPositionAndRotation(anchor.position, anchor.rotation);
            return;
        }

        Quaternion rootRotation = anchor.rotation * Quaternion.Inverse(mountPoint.localRotation);
        Vector3 rootPosition = anchor.position - rootRotation * mountPoint.localPosition;
        trapRoot.SetPositionAndRotation(rootPosition, rootRotation);
    }

    static void ShuffleList<T>(IList<T> list, System.Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }
}
