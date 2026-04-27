using System.Collections.Generic;
using UnityEngine;

public enum MazeSpecialRoomVariant
{
    Original,
    Alternate
}

/// <summary>Where to place the generated <see cref="ProceduralMazeConfig.JailorCarryDestinationMarkerName"/> for <see cref="JailorAI"/>.</summary>
public enum JailorCarryDestinationMazeAnchor
{
    ExitCell,
    StartCell,
}

[CreateAssetMenu(menuName = "Maze Escape/Procedural Maze Config", fileName = "ProceduralMazeConfig")]
public class ProceduralMazeConfig : ScriptableObject
{
    static readonly GameObject[] EmptyPrefabs = new GameObject[0];

    [Header("Runtime")]
    [SerializeField] bool enableGeneration = true;
    [SerializeField] string targetSceneName = "Main";
    [SerializeField] bool buildOfflineInPlayMode = true;
    [SerializeField] bool randomizeOfflineSeed;
    [SerializeField] bool randomizeHostSeed = true;
    [SerializeField] int offlineSeed = 12345;

    [Header("Layout")]
    [SerializeField] Vector2Int mazeSize = new(10, 10);
    [SerializeField] Vector3 origin = Vector3.zero;
    [SerializeField] float cellSize = 18f;
    [SerializeField] float blockerOffset = 9f;
    [SerializeField] int minStraightRun = 1;
    [SerializeField] int maxStraightRun = 4;
    [Tooltip("Along one continuous straight corridor (same row or column in the core maze), the total number of filler straight cells between junctions cannot exceed this sum. Use 0 to disable. Prevents very long hallways from chained segments.")]
    [SerializeField] int maxStraightCellsPerCorridorChain = 4;
    [Tooltip("Maximum consecutive steps the core maze algorithm can take in the same direction before it must turn. Limits junction-to-junction straight runs. Use 0 to disable.")]
    [SerializeField] int maxConsecutiveSameDirection = 2;
    [Tooltip("Chance that core generation expands a random frontier cell instead of the newest one. Higher values branch more often and reduce main-trunk behavior.")]
    [Range(0f, 1f)]
    [SerializeField] float randomFrontierSelectionChance = 0.45f;
    [SerializeField] float endCapYawOffset;
    [SerializeField] string generatedRootName = "GeneratedMaze";

    [Header("Piece Variants")]
    [SerializeField] GameObject[] deadEndPrefabs = EmptyPrefabs;
    [SerializeField] GameObject[] straightPrefabs = EmptyPrefabs;
    [SerializeField] GameObject[] cornerPrefabs = EmptyPrefabs;
    [SerializeField] GameObject[] teePrefabs = EmptyPrefabs;
    [SerializeField] GameObject[] crossPrefabs = EmptyPrefabs;
    [SerializeField] GameObject[] specialPrefabs = EmptyPrefabs;
    [Tooltip("Spawned exactly once per maze on a random dead-end cell (not start, not exit, not an interior room). "
        + "Same topology as other dead ends (MazePieceDefinition DeadEnd + matching open faces). Do not add this to Dead End Prefabs. Leave empty to skip.")]
    [SerializeField] GameObject jailDeadEndPrefab;

    [Header("Interior rooms (throughout maze)")]
    [Tooltip("Prefabs with MazePieceDefinition: open faces must match the **outer** openings of the room block (see Interior Room Grid Footprint). Placed on non-start, non-exit cells only.")]
    [SerializeField] GameObject[] interiorRoomPrefabs = EmptyPrefabs;
    [Tooltip("Default grid size for an interior room (cells in X and Z). Example: (2,2) with cellSize 6 →12×12 world floor. Per-prefab override: MazePieceDefinition.interiorGridFootprint.")]
    [SerializeField] Vector2Int interiorRoomGridFootprint = new(1, 1);
    [Tooltip("How many interior rooms to try to place each build. Uses maze seed; skips cells where no prefab matches.")]
    [SerializeField] int interiorRoomCount;
    [Tooltip("Minimum Chebyshev grid distance between two interior rooms (e.g. 3 means at least a 2-cell gap on diagonals). Use 1 to only avoid same-cell overlap.")]
    [SerializeField] int interiorRoomMinChebyshevSeparation = 3;

    [Header("Start Cell")]
    [Tooltip("When set, the maze start cell always uses this prefab. For a one-opening (end-cap) piece, enable Force Start Cell Single Opening too, or use open faces that cover every start pattern (e.g. a cross).")]
    [SerializeField] GameObject forcedStartPiecePrefab;
    [Tooltip("Deterministically retries generation (same session seed, salted tries) until core cell (0,0) has exactly one open passage, so a single-opening forced start prefab always fits.")]
    [SerializeField] bool forceStartCellSingleOpening;

    [Header("Legacy Starter Pieces")]
    [SerializeField] GameObject crossPrefab;
    [SerializeField] GameObject straightPrefab;
    [SerializeField] GameObject deadEndPrefab;
    [SerializeField] GameObject cornerPrefab;
    [SerializeField] GameObject teePrefab;
    [Tooltip("Original room piece (e.g. MG_Room). Used when Special Room Variant is Original, or as fallback if Alternate is selected but Alternate Room Prefab is empty.")]
    [SerializeField] GameObject roomPrefab;
    [Tooltip("Second room piece (e.g. MG_Room2). Used when Special Room Variant is Alternate.")]
    [SerializeField] GameObject alternateRoomPrefab;
    [Tooltip("Which legacy room prefab is used for special/start-exit fallback when Special Prefabs is empty.")]
    [SerializeField] MazeSpecialRoomVariant specialRoomVariant = MazeSpecialRoomVariant.Original;
    [SerializeField] GameObject endCapPrefab;
    [SerializeField] float crossYawOffset;
    [SerializeField] float straightYawOffset;
    [SerializeField] float deadEndYawOffset;
    [SerializeField] float cornerYawOffset;
    [SerializeField] float teeYawOffset;

    [Header("Generated Spawns")]
    [SerializeField] float spawnHeight = 1f;
    [SerializeField] float spawnSpacing = 1.5f;
    [SerializeField] int spawnPointCount = 4;

    [Header("Maze enemies (optional)")]
    [Tooltip("Prefab spawned after the maze is built (e.g. zombie with NetworkObject). Leave empty to skip.")]
    [SerializeField] GameObject mazeEnemyPrefab;
    [SerializeField] int mazeEnemyCount;
    [Tooltip("Extra Y offset added on top of the cell center when spawning enemies.")]
    [SerializeField] float mazeEnemySpawnHeight;
    [Tooltip("Minimum graph distance from the start cell along open passages. Start cell is never used.")]
    [SerializeField] int mazeEnemyMinCellsFromStart = 2;
    [Tooltip("If true, the farthest (exit) cell is not used for enemy spawns.")]
    [SerializeField] bool mazeEnemyExcludeExitCell = true;
    [Tooltip("Minimum horizontal distance between enemies spawned in the same batch. Use 0 for auto (from cell size).")]
    [SerializeField] float mazeEnemyMinSeparation;

    [Header("Maze Jailor (optional, in addition to maze enemies)")]
    [Tooltip("Spawned after zombies in the same GeneratedEnemies pass, using extra candidate cells (same rules as maze enemies).")]
    [SerializeField] GameObject mazeJailorPrefab;
    [Tooltip("How many Jailors to spawn (usually 1). Cannot exceed free cells after zombie spawns.")]
    [SerializeField] int mazeJailorCount = 1;

    [Header("Jailor carry drop (maze)")]
    [Tooltip("After maze enemies and maze jailor(s) spawn, assign carry destination on every JailorAI in the scene.")]
    [SerializeField] bool assignJailorCarryDestinationAfterSpawn = true;
    [Tooltip(
        "If true, looks for a child Transform with Jailor Carry Anchor Transform Name anywhere under the built maze (your room prefab instance). "
        + "Use that as the drop point wherever that piece spawned. If none is found, falls back to the generated exit/start marker.")]
    [SerializeField] bool preferJailorCarryAnchorFromMazePrefab;
    [Tooltip("Exact name of the empty (or object) on your maze piece prefab, e.g. JailorCarryDrop.")]
    [SerializeField] string jailorCarryAnchorTransformName = "JailorCarryDrop";
    [SerializeField] JailorCarryDestinationMazeAnchor jailorCarryDestinationMazeAnchor = JailorCarryDestinationMazeAnchor.ExitCell;
    [Tooltip("Added on top of the chosen cell center before NavMesh sampling.")]
    [SerializeField] float jailorCarryDestinationYOffset = 0.05f;
    [Tooltip("How far to search for a NavMesh point from the raw cell position.")]
    [SerializeField] float jailorCarryDestinationNavMeshSearchRadius = 4f;
    [Tooltip("Child name under the generated maze root. Re-created each maze build if assign is enabled.")]
    [SerializeField] string jailorCarryDestinationMarkerName = "JailorCarryDestination";

    [Header("Maze traps (anchor-based, optional)")]
    [Tooltip("Prefab spawned at child transforms named TrapAnchor or TrapAnchor2 on generated maze pieces. Use a NetworkObject prefab for multiplayer.")]
    [SerializeField] GameObject mazeTrapPrefab;
    [SerializeField] int mazeTrapCount;
    [Tooltip("Minimum graph distance from the start cell along open passages. Start cell is never used.")]
    [SerializeField] int mazeTrapMinCellsFromStart = 2;
    [Tooltip("If true, the farthest (exit) cell is not used for trap spawns.")]
    [SerializeField] bool mazeTrapExcludeExitCell = true;
    [Tooltip("Minimum horizontal distance between spawned traps. Use 0 for auto (from cell size).")]
    [SerializeField] float mazeTrapMinSeparation;

    [Header("Maze chests (anchor-based, optional)")]
    [Tooltip("Prefab spawned at each child transform named ChestAnchor on generated maze pieces. Use a NetworkObject prefab for multiplayer.")]
    [SerializeField] GameObject mazeChestPrefab;

    [Header("Maze start flashlights (optional)")]
    [Tooltip("Placed on children named LightSpawn, LightSpawn1, LightSpawn2, … on the start piece. At maze build, spawns one per connected player, in order, up to the number of those transforms. Use a NetworkObject prefab in multiplayer.")]
    [SerializeField] GameObject mazeStartFlashlightPrefab;

    public bool EnableGeneration => enableGeneration;
    public string TargetSceneName => targetSceneName;
    public bool BuildOfflineInPlayMode => buildOfflineInPlayMode;
    public bool RandomizeOfflineSeed => randomizeOfflineSeed;
    public bool RandomizeHostSeed => randomizeHostSeed;
    public int OfflineSeed => offlineSeed;
    public Vector2Int MazeSize => new(Mathf.Max(2, mazeSize.x), Mathf.Max(2, mazeSize.y));
    public Vector3 Origin => origin;
    public float CellSize => Mathf.Max(1f, cellSize);
    public float BlockerOffset => Mathf.Max(0f, blockerOffset);
    public int MinStraightRun => Mathf.Max(1, minStraightRun);
    public int MaxStraightRun => Mathf.Max(MinStraightRun, maxStraightRun);
    public int MaxStraightCellsPerCorridorChain => Mathf.Max(0, maxStraightCellsPerCorridorChain);
    public int MaxConsecutiveSameDirection => Mathf.Max(0, maxConsecutiveSameDirection);
    public float RandomFrontierSelectionChance => Mathf.Clamp01(randomFrontierSelectionChance);
    public float EndCapYawOffset => endCapYawOffset;
    public string GeneratedRootName => string.IsNullOrWhiteSpace(generatedRootName) ? "GeneratedMaze" : generatedRootName.Trim();
    public GameObject[] DeadEndPrefabs => deadEndPrefabs ?? EmptyPrefabs;
    public GameObject[] StraightPrefabs => straightPrefabs ?? EmptyPrefabs;
    public GameObject[] CornerPrefabs => cornerPrefabs ?? EmptyPrefabs;
    public GameObject[] TeePrefabs => teePrefabs ?? EmptyPrefabs;
    public GameObject[] CrossPrefabs => crossPrefabs ?? EmptyPrefabs;
    public GameObject[] SpecialPrefabs => specialPrefabs ?? EmptyPrefabs;
    public GameObject[] InteriorRoomPrefabs => interiorRoomPrefabs ?? EmptyPrefabs;
    public Vector2Int InteriorRoomGridFootprint => new(
        Mathf.Max(1, interiorRoomGridFootprint.x),
        Mathf.Max(1, interiorRoomGridFootprint.y));
    public int InteriorRoomCount => Mathf.Max(0, interiorRoomCount);
    public int InteriorRoomMinChebyshevSeparation => Mathf.Max(1, interiorRoomMinChebyshevSeparation);
    public GameObject ForcedStartPiecePrefab => forcedStartPiecePrefab;
    public bool ForceStartCellSingleOpening => forceStartCellSingleOpening;
    public GameObject CrossPrefab => crossPrefab;
    public GameObject StraightPrefab => straightPrefab;
    public GameObject DeadEndPrefab => deadEndPrefab;
    public GameObject CornerPrefab => cornerPrefab;
    public GameObject TeePrefab => teePrefab;
    public GameObject RoomPrefab => roomPrefab;
    public GameObject AlternateRoomPrefab => alternateRoomPrefab;
    public MazeSpecialRoomVariant SpecialRoomVariant => specialRoomVariant;
    public GameObject EffectiveSpecialRoomPrefab =>
        specialRoomVariant == MazeSpecialRoomVariant.Alternate && alternateRoomPrefab != null
            ? alternateRoomPrefab
            : roomPrefab;
    public GameObject EndCapPrefab => endCapPrefab;
    public float CrossYawOffset => crossYawOffset;
    public float StraightYawOffset => straightYawOffset;
    public float DeadEndYawOffset => deadEndYawOffset;
    public float CornerYawOffset => cornerYawOffset;
    public float TeeYawOffset => teeYawOffset;
    public float SpawnHeight => spawnHeight;
    public float SpawnSpacing => Mathf.Max(0.5f, spawnSpacing);
    public int SpawnPointCount => Mathf.Max(0, spawnPointCount);
    public GameObject MazeEnemyPrefab => mazeEnemyPrefab;
    public int MazeEnemyCount => Mathf.Max(0, mazeEnemyCount);
    public float MazeEnemySpawnHeight => mazeEnemySpawnHeight;
    public int MazeEnemyMinCellsFromStart => Mathf.Max(0, mazeEnemyMinCellsFromStart);
    public bool MazeEnemyExcludeExitCell => mazeEnemyExcludeExitCell;
    public float MazeEnemyMinSeparation => mazeEnemyMinSeparation;
    public GameObject MazeJailorPrefab => mazeJailorPrefab;
    public int MazeJailorCount => Mathf.Max(0, mazeJailorCount);
    public bool AssignJailorCarryDestinationAfterSpawn => assignJailorCarryDestinationAfterSpawn;
    public bool PreferJailorCarryAnchorFromMazePrefab => preferJailorCarryAnchorFromMazePrefab;
    public string JailorCarryAnchorTransformName =>
        string.IsNullOrWhiteSpace(jailorCarryAnchorTransformName)
            ? "JailorCarryDrop"
            : jailorCarryAnchorTransformName.Trim();
    public JailorCarryDestinationMazeAnchor JailorCarryDestinationMazeAnchor => jailorCarryDestinationMazeAnchor;
    public float JailorCarryDestinationYOffset => jailorCarryDestinationYOffset;
    public float JailorCarryDestinationNavMeshSearchRadius =>
        Mathf.Max(0.5f, jailorCarryDestinationNavMeshSearchRadius);
    public string JailorCarryDestinationMarkerName =>
        string.IsNullOrWhiteSpace(jailorCarryDestinationMarkerName)
            ? "JailorCarryDestination"
            : jailorCarryDestinationMarkerName.Trim();
    public GameObject MazeTrapPrefab => mazeTrapPrefab;
    public int MazeTrapCount => Mathf.Max(0, mazeTrapCount);
    public int MazeTrapMinCellsFromStart => Mathf.Max(0, mazeTrapMinCellsFromStart);
    public bool MazeTrapExcludeExitCell => mazeTrapExcludeExitCell;
    public float MazeTrapMinSeparation => mazeTrapMinSeparation;
    public GameObject MazeChestPrefab => mazeChestPrefab;
    public GameObject MazeStartFlashlightPrefab => mazeStartFlashlightPrefab;
    public GameObject JailDeadEndPrefab => jailDeadEndPrefab;

    public bool HasMinimumStarterSet => HasAssignedForCategory(MazePieceCategory.Cross)
        && HasAssignedForCategory(MazePieceCategory.Straight)
        && HasAssignedForCategory(MazePieceCategory.DeadEnd)
        && HasAssignedForCategory(MazePieceCategory.Corner)
        && HasAssignedForCategory(MazePieceCategory.Tee);

    public IEnumerable<GameObject> EnumerateTopologyPrefabs(MazePieceCategory category)
    {
        GameObject[] configuredPool = GetVariantPool(category);
        bool yieldedConfiguredPrefab = false;

        for (int i = 0; i < configuredPool.Length; i++)
        {
            if (configuredPool[i] == null)
                continue;

            yieldedConfiguredPrefab = true;
            yield return configuredPool[i];
        }

        if (yieldedConfiguredPrefab)
            yield break;

        GameObject legacyPrefab = GetLegacyPrefab(category);
        if (legacyPrefab != null)
            yield return legacyPrefab;
    }

    public IEnumerable<GameObject> EnumerateSpecialPrefabs()
    {
        GameObject[] configuredSpecialPrefabs = SpecialPrefabs;
        bool yieldedConfiguredPrefab = false;
        for (int i = 0; i < configuredSpecialPrefabs.Length; i++)
        {
            if (configuredSpecialPrefabs[i] == null)
                continue;

            yieldedConfiguredPrefab = true;
            yield return configuredSpecialPrefabs[i];
        }

        GameObject legacyRoom = EffectiveSpecialRoomPrefab;
        if (!yieldedConfiguredPrefab && legacyRoom != null)
            yield return legacyRoom;
    }

    public IEnumerable<GameObject> EnumerateConfiguredPrefabs(MazePieceCategory category)
    {
        foreach (GameObject prefab in GetVariantPool(category))
        {
            if (prefab != null)
                yield return prefab;
        }

        GameObject legacyPrefab = GetLegacyPrefab(category);
        if (legacyPrefab != null)
            yield return legacyPrefab;
    }

    public bool HasAssignedForCategory(MazePieceCategory category)
    {
        foreach (GameObject prefab in EnumerateConfiguredPrefabs(category))
        {
            if (prefab != null)
                return true;
        }

        return false;
    }

    GameObject[] GetVariantPool(MazePieceCategory category)
    {
        return category switch
        {
            MazePieceCategory.DeadEnd => DeadEndPrefabs,
            MazePieceCategory.Straight => StraightPrefabs,
            MazePieceCategory.Corner => CornerPrefabs,
            MazePieceCategory.Tee => TeePrefabs,
            MazePieceCategory.Cross => CrossPrefabs,
            MazePieceCategory.Special => SpecialPrefabs,
            _ => EmptyPrefabs
        };
    }

    GameObject GetLegacyPrefab(MazePieceCategory category)
    {
        return category switch
        {
            MazePieceCategory.DeadEnd => deadEndPrefab,
            MazePieceCategory.Straight => straightPrefab,
            MazePieceCategory.Corner => cornerPrefab,
            MazePieceCategory.Tee => teePrefab,
            MazePieceCategory.Cross => crossPrefab,
            MazePieceCategory.Special => EffectiveSpecialRoomPrefab,
            _ => null
        };
    }
}
