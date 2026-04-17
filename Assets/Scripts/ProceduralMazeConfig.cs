using System.Collections.Generic;
using UnityEngine;

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
    [SerializeField] GameObject roomPrefab;
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
    public GameObject ForcedStartPiecePrefab => forcedStartPiecePrefab;
    public bool ForceStartCellSingleOpening => forceStartCellSingleOpening;
    public GameObject CrossPrefab => crossPrefab;
    public GameObject StraightPrefab => straightPrefab;
    public GameObject DeadEndPrefab => deadEndPrefab;
    public GameObject CornerPrefab => cornerPrefab;
    public GameObject TeePrefab => teePrefab;
    public GameObject RoomPrefab => roomPrefab;
    public GameObject EndCapPrefab => endCapPrefab;
    public float CrossYawOffset => crossYawOffset;
    public float StraightYawOffset => straightYawOffset;
    public float DeadEndYawOffset => deadEndYawOffset;
    public float CornerYawOffset => cornerYawOffset;
    public float TeeYawOffset => teeYawOffset;
    public float SpawnHeight => spawnHeight;
    public float SpawnSpacing => Mathf.Max(0.5f, spawnSpacing);
    public int SpawnPointCount => Mathf.Max(0, spawnPointCount);

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

        if (!yieldedConfiguredPrefab && roomPrefab != null)
            yield return roomPrefab;
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
            MazePieceCategory.Special => roomPrefab,
            _ => null
        };
    }
}
