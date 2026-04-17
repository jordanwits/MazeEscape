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

    const string ConfigResourceName = "ProceduralMazeConfig";
    const string SeedRequestMessageName = "maze-seed-request";
    const string SeedResponseMessageName = "maze-seed-response";

    static readonly DirectionStep[] Steps =
    {
        new(0, 1, MazeFaceMask.North),
        new(1, 0, MazeFaceMask.East),
        new(0, -1, MazeFaceMask.South),
        new(-1, 0, MazeFaceMask.West)
    };

    [Header("Config")]
    [Tooltip("If set, used instead of Resources/ProceduralMazeConfig. Use per-scene or per-mode maze + enemy settings.")]
    [SerializeField] ProceduralMazeConfig configOverride;
    [Tooltip("If set, used instead of Maze Enemy Prefab on the config (same maze asset, different enemy per scene).")]
    [SerializeField] GameObject mazeEnemyPrefabOverride;

    [Header("Navigation (runtime)")]
    [Tooltip("After the maze is built, bake a NavMesh from geometry under the maze root so NavMeshAgents (e.g. zombies) can path. Uses NavMeshSurface from the AI Navigation package.")]
    [SerializeField] bool rebuildNavMeshAfterMaze = true;
    [Tooltip("Physics = MeshCollider/BoxCollider on floor pieces. Render Meshes = Renderer geometry (use if walkable meshes have no colliders).")]
    [SerializeField] NavMeshCollectGeometry navMeshBakeGeometry = NavMeshCollectGeometry.PhysicsColliders;

    ProceduralMazeConfig _config;
    NetworkManager _networkManager;
    bool _handlersRegistered;
    bool _hasCurrentSeed;
    int _currentSeed;
    Coroutine _sceneRoutine;
    readonly HashSet<string> _loggedMazeWarnings = new();
    string _lastServerMazeBuildSceneName;
    int _lastServerMazeBuildSeed = int.MinValue;

    void Awake()
    {
        _config = configOverride != null
            ? configOverride
            : Resources.Load<ProceduralMazeConfig>(ConfigResourceName);
        _networkManager = GetComponent<NetworkManager>();

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
        UnregisterMessageHandlers();
    }

    void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (!ShouldManageScene(scene))
            return;

        RestartSceneRoutine(HandleSceneLoadedRoutine(scene));
    }

    IEnumerator HandleSceneLoadedRoutine(Scene scene)
    {
        yield return null;

        if (!ShouldManageScene(scene))
            yield break;

        if (IsServerListening())
        {
            EnsureMessageHandlersRegistered();
            if (_hasCurrentSeed)
                BuildMazeInScene(scene, _currentSeed);
            yield break;
        }

        if (IsClientConnected())
        {
            EnsureMessageHandlersRegistered();
            yield return RequestSeedFromServer();
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
        if (_config == null || !_config.EnableGeneration)
            return;

        EnsureMessageHandlersRegistered();
        _currentSeed = _config.RandomizeHostSeed ? UnityEngine.Random.Range(int.MinValue, int.MaxValue) : _config.OfflineSeed;
        _hasCurrentSeed = true;

        Scene activeScene = SceneManager.GetActiveScene();
        if (ShouldManageScene(activeScene))
            BuildMazeInScene(activeScene, _currentSeed);
    }

    void HandleClientConnected(ulong clientId)
    {
        EnsureMessageHandlersRegistered();

        if (_networkManager == null || !_networkManager.IsListening)
            return;

        if (_networkManager.IsServer)
        {
            if (clientId != _networkManager.LocalClientId && _hasCurrentSeed)
                SendSeedToClient(clientId);

            return;
        }

        if (clientId == _networkManager.LocalClientId)
            RestartSceneRoutine(RequestSeedFromServer());
    }

    void HandleClientDisconnected(ulong clientId)
    {
        if (_networkManager == null)
            return;

        if (!_networkManager.IsServer && clientId == _networkManager.LocalClientId)
            _hasCurrentSeed = false;
    }

    IEnumerator RequestSeedFromServer()
    {
        yield return null;

        if (_networkManager == null || !_networkManager.IsListening || _networkManager.IsServer)
            yield break;

        EnsureMessageHandlersRegistered();
        if (_networkManager.CustomMessagingManager == null)
            yield break;

        using FastBufferWriter writer = new(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe((byte)1);
        _networkManager.CustomMessagingManager.SendNamedMessage(
            SeedRequestMessageName,
            NetworkManager.ServerClientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    void EnsureMessageHandlersRegistered()
    {
        if (_handlersRegistered || _networkManager == null || _networkManager.CustomMessagingManager == null)
            return;

        _networkManager.CustomMessagingManager.RegisterNamedMessageHandler(SeedRequestMessageName, HandleSeedRequest);
        _networkManager.CustomMessagingManager.RegisterNamedMessageHandler(SeedResponseMessageName, HandleSeedResponse);
        _handlersRegistered = true;
    }

    void UnregisterMessageHandlers()
    {
        if (!_handlersRegistered || _networkManager == null || _networkManager.CustomMessagingManager == null)
            return;

        _networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(SeedRequestMessageName);
        _networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(SeedResponseMessageName);
        _handlersRegistered = false;
    }

    void HandleSeedRequest(ulong senderClientId, FastBufferReader _)
    {
        if (_networkManager == null || !_networkManager.IsServer || !_hasCurrentSeed)
            return;

        SendSeedToClient(senderClientId);
    }

    void HandleSeedResponse(ulong _, FastBufferReader reader)
    {
        if (_networkManager == null || _networkManager.IsServer)
            return;

        reader.ReadValueSafe(out int seed);
        _currentSeed = seed;
        _hasCurrentSeed = true;

        Scene activeScene = SceneManager.GetActiveScene();
        if (ShouldManageScene(activeScene))
            BuildMazeInScene(activeScene, seed);
    }

    void SendSeedToClient(ulong clientId)
    {
        if (_networkManager == null || _networkManager.CustomMessagingManager == null)
            return;

        using FastBufferWriter writer = new(sizeof(int), Allocator.Temp);
        writer.WriteValueSafe(_currentSeed);
        _networkManager.CustomMessagingManager.SendNamedMessage(
            SeedResponseMessageName,
            clientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    bool ShouldManageScene(Scene scene)
    {
        return _config != null
            && _config.EnableGeneration
            && _config.HasMinimumStarterSet
            && scene.IsValid()
            && scene.isLoaded
            && string.Equals(scene.name, _config.TargetSceneName, StringComparison.Ordinal);
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
        ValidateConfiguredPieceSetup();

        DespawnAllSpawnedZombieEnemies();

        GameObject root = GetOrCreateRoot(scene);
        ClearRoot(root.transform);
        root.transform.position = Vector3.zero;

        Vector2Int logicalSize = _config.MazeSize;
        MazeLayout layout = GenerateMazeLayout(seed, logicalSize.x, logicalSize.y);
        MazeCell[,] grid = layout.Cells;
        Vector2Int start = layout.Start;
        Vector2Int exit = FindFarthestCell(grid, start);

        Transform cellsRoot = CreateChild(root.transform, "Cells");
        float cellSize = _config.CellSize;
        float blockerOffset = _config.BlockerOffset > 0f ? _config.BlockerOffset : cellSize * 0.5f;
        int width = grid.GetLength(0);
        int height = grid.GetLength(1);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (grid[x, y].Openings == MazeFaceMask.None)
                    continue;

                Transform cellRoot = CreateChild(cellsRoot, $"Cell_{x}_{y}");
                cellRoot.position = CellToWorld(x, y, cellSize);

                bool isStart = x == start.x && y == start.y;
                bool isExit = x == exit.x && y == exit.y;
                BuildCell(cellRoot, grid[x, y].Openings, blockerOffset, isStart, isExit, seed, new Vector2Int(x, y));
            }
        }

        CreateSpawnPoints(root.transform, start, cellSize);
        MultiplayerSpawnRegistry.Instance?.RefreshSpawnPoints();
        TryRebuildRuntimeNavMesh(root);
        TrySpawnMazeEnemies(root.transform, grid, start, exit, seed, cellSize);
        Debug.Log($"[Maze] Built seeded maze {seed} from logical size {logicalSize.x}x{logicalSize.y} into {width}x{height} cells in scene \"{scene.name}\".", this);
    }

    bool IsDuplicateServerMazeBuild(Scene scene, int seed)
    {
        if (!IsServerListening())
            return false;

        return _lastServerMazeBuildSeed == seed
            && !string.IsNullOrEmpty(_lastServerMazeBuildSceneName)
            && string.Equals(_lastServerMazeBuildSceneName, scene.name, StringComparison.Ordinal);
    }

    void DespawnAllSpawnedZombieEnemies()
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

            if (networkObject.gameObject.GetComponent<NetworkZombieAvatar>() != null)
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

    void BuildCell(Transform parent, MazeFaceMask openings, float blockerOffset, bool isStart, bool isExit, int seed, Vector2Int cellCoordinates)
    {
        if (openings == MazeFaceMask.None)
            return;

        if (!MazePieceResolver.TryResolve(_config, openings, isStart, isExit, seed, cellCoordinates, out MazePieceMatch match, out string failureReason))
        {
            LogMazeWarningOnce(
                $"resolve:{openings}",
                $"[Maze] {failureReason} Cell ({cellCoordinates.x}, {cellCoordinates.y}) could not be built.",
                this);
            return;
        }

        Instantiate(match.Prefab, parent.position, match.Rotation, parent);

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

    void ValidateConfiguredPieceSetup()
    {
        ValidatePiecePool("dead-end", MazePieceCategory.DeadEnd, _config.EnumerateConfiguredPrefabs(MazePieceCategory.DeadEnd), true);
        ValidatePiecePool("straight", MazePieceCategory.Straight, _config.EnumerateConfiguredPrefabs(MazePieceCategory.Straight), true);
        ValidatePiecePool("corner", MazePieceCategory.Corner, _config.EnumerateConfiguredPrefabs(MazePieceCategory.Corner), true);
        ValidatePiecePool("tee", MazePieceCategory.Tee, _config.EnumerateConfiguredPrefabs(MazePieceCategory.Tee), true);
        ValidatePiecePool("cross", MazePieceCategory.Cross, _config.EnumerateConfiguredPrefabs(MazePieceCategory.Cross), true);
        ValidatePiecePool("special", null, _config.SpecialPrefabs, false);

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

        if (_config.RoomPrefab != null && !hasConfiguredSpecialPrefabs)
        {
            LogMazeWarningOnce(
                "legacy-room-fallback",
                "[Maze] RoomPrefab is still using legacy start/exit fallback behavior. Add a MazePieceDefinition and move it into Special Prefabs for exact face matching.",
                this);
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

            MazePieceDefinition definition = prefab.GetComponent<MazePieceDefinition>();
            if (definition == null)
            {
                LogMazeWarningOnce(
                    $"missing-definition:{poolName}:{prefab.name}",
                    $"[Maze] Prefab \"{prefab.name}\" in the {poolName} pool is missing a MazePieceDefinition component.",
                    this);
                continue;
            }

            if (definition.OpenFaces == MazeFaceMask.None)
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

    void CreateSpawnPoints(Transform root, Vector2Int startCell, float cellSize)
    {
        if (_config.SpawnPointCount <= 0)
            return;

        Transform spawnRoot = CreateChild(root, "GeneratedSpawnPoints");
        Vector3 center = CellToWorld(startCell.x, startCell.y, cellSize) + Vector3.up * _config.SpawnHeight;

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

        if (_networkManager != null && _networkManager.IsListening && !_networkManager.IsServer)
            return;

        NavMeshSurface surface = mazeRoot.GetComponent<NavMeshSurface>();
        if (surface == null)
            surface = mazeRoot.AddComponent<NavMeshSurface>();

        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = navMeshBakeGeometry;
        surface.layerMask = ~0;

        surface.BuildNavMesh();
    }

    void TrySpawnMazeEnemies(Transform mazeRoot, MazeCell[,] grid, Vector2Int start, Vector2Int exit, int seed, float cellSize)
    {
        GameObject prefab = mazeEnemyPrefabOverride != null ? mazeEnemyPrefabOverride : _config.MazeEnemyPrefab;
        int count = _config.MazeEnemyCount;
        if (prefab == null || count <= 0)
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

                if (x == start.x && y == start.y)
                    continue;

                if (_config.MazeEnemyExcludeExitCell && x == exit.x && y == exit.y)
                    continue;

                int? d = distances[x, y];
                if (!d.HasValue || d.Value < _config.MazeEnemyMinCellsFromStart)
                    continue;

                candidates.Add(new Vector2Int(x, y));
            }
        }

        if (candidates.Count == 0)
        {
            LogMazeWarningOnce("enemies-no-cells", "[Maze] No valid cells for maze enemies (check min distance / maze size).", this);
            return;
        }

        if (count > candidates.Count)
        {
            LogMazeWarningOnce(
                "enemies-trimmed",
                $"[Maze] Requested {_config.MazeEnemyCount} maze enemies but only {candidates.Count} cells match rules; spawning {candidates.Count}.",
                this);
            count = candidates.Count;
        }

        ShuffleList(candidates, new System.Random(MixSeed(seed, unchecked((int)0x39FEA0B9))));

        Debug.Log(
            $"[Maze] Spawning {count} maze enemy/enemies from config asset \"{_config.name}\" " +
            $"(Maze Enemy Count field = {_config.MazeEnemyCount}). Prefab: \"{(prefab != null ? prefab.name : "null")}\". " +
            $"{(mazeEnemyPrefabOverride != null ? $"Prefab override on coordinator: \"{mazeEnemyPrefabOverride.name}\"." : "No enemy prefab override on coordinator (using config prefab).")}",
            this);

        Transform enemiesRoot = CreateChild(mazeRoot, "GeneratedEnemies");
        float yOffset = _config.MazeEnemySpawnHeight;
        bool spawnWithNetcode = _networkManager != null && _networkManager.IsListening;

        List<Vector3> placedEnemyPositions = new(count);
        float minSeparationXZ = ResolveMazeEnemyMinSeparationXZ(cellSize);

        for (int i = 0; i < count; i++)
        {
            Vector2Int cell = candidates[i];
            Vector3 position = ResolveSpawnPositionWithSeparation(
                cell,
                cellSize,
                yOffset,
                seed,
                i,
                placedEnemyPositions,
                minSeparationXZ);

            placedEnemyPositions.Add(position);
            GameObject instance = Instantiate(prefab, position, Quaternion.identity, enemiesRoot);

            if (!spawnWithNetcode)
                continue;

            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            if (networkObject != null)
                networkObject.Spawn();
        }
    }

    float ResolveMazeEnemyMinSeparationXZ(float cellSize)
    {
        if (_config.MazeEnemyMinSeparation > 0f)
            return Mathf.Max(0.25f, _config.MazeEnemyMinSeparation);

        return Mathf.Clamp(cellSize * 0.2f, 1.15f, 2.6f);
    }

    Vector3 ResolveSpawnPositionWithSeparation(
        Vector2Int cell,
        float cellSize,
        float yOffset,
        int mazeSeed,
        int spawnIndex,
        List<Vector3> placed,
        float minSeparationXZ)
    {
        const int maxAttempts = 18;
        float minSqr = minSeparationXZ * minSeparationXZ;
        Vector3 best = GetMazeEnemySpawnWorldPosition(cell, cellSize, yOffset, mazeSeed, spawnIndex, 0);
        float bestScore = -1f;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            Vector3 candidate = GetMazeEnemySpawnWorldPosition(cell, cellSize, yOffset, mazeSeed, spawnIndex, attempt);
            if (IsFarEnoughXZ(candidate, placed, minSqr))
                return candidate;

            float score = MinHorizontalSqrDistanceToAny(candidate, placed);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
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

    Vector3 GetMazeEnemySpawnWorldPosition(
        Vector2Int cell,
        float cellSize,
        float yOffset,
        int mazeSeed,
        int spawnIndex,
        int placementAttempt)
    {
        Vector3 cellCenter = CellToWorld(cell.x, cell.y, cellSize);
        int jitterSeed = MixSeed(mazeSeed, MixSeed(spawnIndex, MixSeed(placementAttempt, unchecked((int)0x51A4EED5))));
        System.Random jitterRng = new(jitterSeed);
        float jitterRadius = Mathf.Min(cellSize * 0.48f, cellSize * 0.35f * (1f + placementAttempt * 0.14f));
        float jx = (float)(jitterRng.NextDouble() * 2d - 1d) * jitterRadius;
        float jz = (float)(jitterRng.NextDouble() * 2d - 1d) * jitterRadius;
        Vector3 xzOnGrid = cellCenter + new Vector3(jx, 0f, jz);

        if (!TryFindLowestWalkableSurfaceBelow(xzOnGrid, cellSize, out Vector3 floorPoint))
            floorPoint = xzOnGrid;

        float snapRadius = Mathf.Clamp(cellSize * 0.22f, 0.75f, 3f);
        Vector3 sampleFrom = floorPoint + Vector3.up * 0.35f;
        if (NavMesh.SamplePosition(sampleFrom, out NavMeshHit navHit, snapRadius, NavMesh.AllAreas))
        {
            if (navHit.position.y <= floorPoint.y + 2.5f)
                return navHit.position + Vector3.up * yOffset;
        }

        return floorPoint + Vector3.up * yOffset;
    }

    static bool TryFindLowestWalkableSurfaceBelow(Vector3 xzOnGrid, float cellSize, out Vector3 floorPoint)
    {
        floorPoint = default;
        float rayStartY = xzOnGrid.y + Mathf.Max(24f, cellSize * 2f);
        Vector3 origin = new(xzOnGrid.x, rayStartY, xzOnGrid.z);
        float rayLength = Mathf.Max(80f, cellSize * 5f);

        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            Vector3.down,
            rayLength,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);

        if (hits == null || hits.Length == 0)
            return false;

        const float minFloorNormalY = 0.55f;
        float bestY = float.MaxValue;
        bool found = false;

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (hit.normal.y < minFloorNormalY)
                continue;

            if (hit.point.y < bestY)
            {
                bestY = hit.point.y;
                floorPoint = hit.point;
                found = true;
            }
        }

        return found;
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
