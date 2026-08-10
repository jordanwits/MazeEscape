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

/// <summary>One numbered enemy slot on a <see cref="ProceduralMazeConfig"/>. The enemy species is
/// intentionally not named here: each level fills the four slots with a different mix (e.g. Enemy 2 is
/// the Jailor on Levels 01/03/04 and the Clown on Level02).</summary>
[System.Serializable]
public class MazeEnemySpawn
{
    [Tooltip("Enemy prefab (with NetworkObject) to spawn for this slot. Leave empty to skip the slot.")]
    public GameObject enemyPrefab;
    [Tooltip("How many of this enemy to spawn. Ignored while Enemy Prefab is empty.")]
    public int enemyCount;
}

[CreateAssetMenu(menuName = "Maze Escape/Procedural Maze Config", fileName = "ProceduralMazeConfig")]
public class ProceduralMazeConfig : ScriptableObject
{
    static readonly GameObject[] EmptyPrefabs = new GameObject[0];

    [Header("Runtime")]
    [SerializeField] bool enableGeneration = true;
    [SerializeField] string targetSceneName = "Level01";
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
    [Tooltip(
        "Optional selection weights parallel to Straight Prefabs array indices. "
        + "Use 0 or omit extra entries to use each prefab's MazePieceDefinition weight for that slot.")]
    [SerializeField] int[] straightPrefabWeights;
    [SerializeField] GameObject[] cornerPrefabs = EmptyPrefabs;
    [SerializeField] GameObject[] teePrefabs = EmptyPrefabs;
    [SerializeField] GameObject[] crossPrefabs = EmptyPrefabs;
    [SerializeField] GameObject[] specialPrefabs = EmptyPrefabs;
    [Tooltip("Spawned exactly once per maze on a random dead-end cell (not start, not exit, not an interior room). "
        + "Same topology as other dead ends (MazePieceDefinition DeadEnd + matching open faces). Do not add this to Dead End Prefabs. Leave empty to skip.")]
    [SerializeField] GameObject jailDeadEndPrefab;
    [Tooltip("Leave OFF (default) so the generator never places the same corridor prefab in two orthogonally adjacent cells "
        + "(e.g. two CarnivalStraight2 side by side) when another variant of that topology fits the same openings. "
        + "It falls back to a duplicate only when no other variant matches that cell, so a single-variant pool still builds. "
        + "Tick this to allow duplicates again (old behavior).")]
    [SerializeField] bool allowAdjacentDuplicatePieces;

    [Header("Interior Rooms")]
    [Tooltip("Prefabs with MazePieceDefinition: open faces must match the **outer** openings of the room block (see Interior Room Grid Footprint). Placed on non-start, non-exit cells only.")]
    [SerializeField] GameObject[] interiorRoomPrefabs = EmptyPrefabs;
    [Tooltip("Default grid size for an interior room (cells in X and Z). Example: (2,2) with cellSize 6 → a 12×12 world floor. Per-prefab override: MazePieceDefinition.interiorGridFootprint.")]
    [SerializeField] Vector2Int interiorRoomGridFootprint = new(1, 1);
    [Tooltip("How many interior rooms to try to place each build. Uses maze seed; skips cells where no prefab matches.")]
    [SerializeField] int interiorRoomCount;
    [Tooltip("Minimum Chebyshev grid distance between two interior rooms (e.g. 3 means at least a 2-cell gap on diagonals). Use 1 to only avoid same-cell overlap.")]
    [SerializeField] int interiorRoomMinChebyshevSeparation = 3;
    [Tooltip("Optional per-prefab placement caps parallel to Interior Room Prefabs indices; 0 or missing entries = no cap. "
        + "Capped prefabs place first, in list order, until each cap is met (e.g. caps [1, 2] with Interior Room Count 3 "
        + "places exactly one of prefab 0, then up to two of prefab 1).")]
    [SerializeField] int[] interiorRoomPrefabCounts;

    [Header("Exit Hall")]
    [Tooltip("Optional multi-cell finish piece (MazePieceDefinition with an Interior Grid Footprint of 1×N or N×1, "
        + "Exit Only, one open face at the mouth end). Stamped over a straight run of cells containing the exit cell, "
        + "so the run ends in a long approach hallway instead of a single 6m cell. "
        + "Falls back to the normal single-cell Special piece when no run fits. Leave empty to skip.")]
    [SerializeField] GameObject exitHallPiecePrefab;

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

    [Header("Generated Player Spawn Points")]
    [Tooltip("Y offset above the start cell floor for generated player spawn points.")]
    [SerializeField] float spawnHeight = 1f;
    [Tooltip("Horizontal spacing between generated player spawn points.")]
    [SerializeField] float spawnSpacing = 1.5f;
    [Tooltip("How many player spawn points (Spawn_0..N under GeneratedSpawnPoints) to create at the start cell. Use 0 when the scene provides its own spawn points.")]
    [SerializeField] int spawnPointCount = 4;

    [Header("Enemies")]
    [Tooltip("Enemies spawned after the maze builds, in order Enemy 1 → 2 → 3 → 4. Each level uses a "
        + "different mix — leave a slot's Enemy Prefab empty to skip that slot.")]
    [SerializeField] MazeEnemySpawn enemy1 = new();
    [SerializeField] MazeEnemySpawn enemy2 = new();
    [Tooltip("Minimum graph distance from the start cell for the Enemy 2 slot. Enemy 2 reserves the "
        + "farthest cells first, so use this to keep a dangerous chaser away from the start room. "
        + "Clamped to at least the shared minimum below.")]
    [SerializeField] int enemy2MinCellsFromStart = 5;
    [SerializeField] MazeEnemySpawn enemy3 = new();
    [SerializeField] MazeEnemySpawn enemy4 = new();

    [Header("Enemy Spawn Rules (Shared)")]
    [Tooltip("Extra Y offset added on top of the cell center when spawning enemies (all slots).")]
    [SerializeField] float mazeEnemySpawnHeight;
    [Tooltip("Minimum graph distance from the start cell along open passages (all slots; Enemy 2 adds its own larger minimum). Start cell is never used.")]
    [SerializeField] int mazeEnemyMinCellsFromStart = 2;
    [Tooltip("If true, the farthest (exit) cell is not used for enemy spawns (all slots).")]
    [SerializeField] bool mazeEnemyExcludeExitCell = true;
    [Tooltip("Minimum horizontal distance between enemies spawned in the same batch (all slots). Use 0 for auto (from cell size).")]
    [SerializeField] float mazeEnemyMinSeparation;

    [Header("Jailor Carry Drop (JailorAI Only)")]
    [Tooltip("After maze enemies spawn, assign the carry destination on every JailorAI in the scene. "
        + "Species-specific: does nothing on levels whose hunter is the Clown (Level02 leaves this off).")]
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

    [Header("Maze Traps (Anchor-Based)")]
    [Tooltip("Prefab spawned at child transforms named TrapAnchor or TrapAnchor2 on generated maze pieces. Use a NetworkObject prefab for multiplayer. Leave empty to skip.")]
    [SerializeField] GameObject mazeTrapPrefab;
    [SerializeField] int mazeTrapCount;
    [Tooltip("Minimum graph distance from the start cell along open passages. Start cell is never used.")]
    [SerializeField] int mazeTrapMinCellsFromStart = 2;
    [Tooltip("If true, the farthest (exit) cell is not used for trap spawns.")]
    [SerializeField] bool mazeTrapExcludeExitCell = true;
    [Tooltip("Minimum horizontal distance between spawned traps. Use 0 for auto (from cell size).")]
    [SerializeField] float mazeTrapMinSeparation;

    [Header("Maze Chests (Anchor-Based)")]
    [Tooltip("Prefab spawned at each child transform named ChestAnchor on generated maze pieces. Use a NetworkObject prefab for multiplayer. Leave empty to skip.")]
    [SerializeField] GameObject mazeChestPrefab;

    [Header("Maze Item Pickups (Anchor-Based)")]
    [Tooltip("Loot pool for child transforms whose name starts with ItemSpawn on generated maze pieces. Each marker rolls "
        + "Maze Item Spawn Chance, then spawns one random prefab from this list (maze-seeded). Spawned locally on every peer "
        + "with a stable item id, chest-loot style — use plain GrabbableInventoryItem pickup prefabs, not NetworkObjects.")]
    [SerializeField] GameObject[] mazeItemSpawnPrefabs = EmptyPrefabs;
    [Tooltip("Per ItemSpawn marker, probability [0,1] that an item spawns this build.")]
    [Range(0f, 1f)]
    [SerializeField] float mazeItemSpawnChance = 1f;

    [Header("Teleport Orbs (Anchor-Based)")]
    [Tooltip("Prefab spawned at each child transform named TeleportOrbAnchor on generated maze pieces. Use the TeleportOrb NetworkObject prefab. Leave empty to skip.")]
    [SerializeField] GameObject mazeTeleportOrbPrefab;
    [Tooltip("How many teleport orbs to actually spawn this build. If there are more TeleportOrbAnchor markers in the maze than this, a random subset (seeded by the maze seed, so reproducible per seed) is chosen; if there are fewer anchors, one orb spawns at each available anchor. 0 = spawn no orbs even if anchors exist.")]
    [SerializeField, Min(0)] int mazeTeleportOrbCount = 3;

    [Header("Decorative Bats (Dead-End Roosts)")]
    [Tooltip("Bat roost prefab (BatSwarmRoost) dropped into randomly chosen dead-end cells. Purely cosmetic "
        + "and NOT a NetworkObject — every peer builds its own roosts from the maze seed and fires them for "
        + "its own player. Leave empty to skip bats on this level.")]
    [SerializeField] GameObject mazeBatRoostPrefab;
    [Tooltip("How many dead ends get a bat colony. Dead ends are picked from the maze seed, excluding the "
        + "start, exit and jail cells. 0 = no bats even if a prefab is assigned.")]
    [SerializeField, Min(0)] int mazeBatRoostCount = 3;
    [Tooltip("Bats per colony. Each is a ~180-tri mesh with a shader-driven flap, so this is cheap — the "
        + "limit is readability, not performance. Above roughly 14 the burst turns into an unreadable smear.")]
    [SerializeField, Min(1)] int mazeBatsPerRoost = 8;
    [Tooltip("Height above the cell floor where the colony roosts, in metres. Should sit just under this "
        + "level's ceiling so the bats drop out of the dark rather than materialising at head height.")]
    [SerializeField] float mazeBatRoostHeight = 2.4f;
    [Tooltip("Fraction of roosts reserved for dead-end cells; the rest go in corridors and corners. "
        + "Straights massively outnumber dead ends in a generated maze, so without a reserve an even draw "
        + "would almost never pick one — and a dead end is the strongest version of the scare, since the "
        + "player has walked into a pocket with nowhere to retreat. 1 = dead ends only.")]
    [Range(0f, 1f)]
    [SerializeField] float mazeBatDeadEndShare = 0.5f;
    [Tooltip("Minimum spacing between two roosts, in cells. Dead ends are naturally sparse, but once "
        + "corridors are eligible two colonies can land in neighbouring cells and fire almost together. "
        + "Relaxed automatically if the layout can't satisfy it for the requested roost count.")]
    [SerializeField, Min(0)] int mazeBatRoostMinCellSeparation = 3;

    [Header("Decorative Cockroaches")]
    [Tooltip("Roach colony prefab (RoachColony), dropped into randomly chosen cells. Cosmetic and NOT a "
        + "NetworkObject — every peer builds its own from the maze seed. Leave empty to skip roaches.")]
    [SerializeField] GameObject mazeRoachColonyPrefab;
    [Tooltip("How many cells get a roach nest. 0 = none even if a prefab is assigned.")]
    [SerializeField, Min(0)] int mazeRoachColonyCount = 6;
    [Tooltip("Roaches per nest. Each is a single two-triangle quad, so this is cheap — 10-20 reads as an "
        + "infestation without turning into noise.")]
    [SerializeField, Min(1)] int mazeRoachesPerColony = 14;
    [Tooltip("Height of the walkable floor above the cell root, in metres. The colony is placed here and "
        + "finds its own floor and wall surfaces by raycast from that point.")]
    [SerializeField] float mazeRoachColonyHeight = 1f;
    [Tooltip("Metres out from the colony centre that roaches may be placed. Roughly half a cell.")]
    [SerializeField] float mazeRoachColonySpread = 2.4f;
    [Tooltip("Minimum spacing between two roach nests, in cells. Relaxed automatically if the layout "
        + "can't satisfy it for the requested count.")]
    [SerializeField, Min(0)] int mazeRoachColonyMinCellSeparation = 2;

    [Header("RatBot (Anchor-Based)")]
    [Tooltip("Prefab spawned at child transforms named RatSpawn on generated maze pieces / interior rooms. Use the RatBot NetworkObject prefab. Leave empty to skip.")]
    [SerializeField] GameObject mazeRatBotPrefab;
    [Tooltip("How many RatBots to actually spawn this build. If there are more RatSpawn markers in the maze than this, a random subset (seeded by the maze seed, so reproducible per seed) is chosen; if there are fewer markers, one spawns at each. 0 = spawn none even if markers exist. Default 1 = a single rat somewhere in the maze.")]
    [SerializeField, Min(0)] int mazeRatBotCount = 1;

    [Header("Maze Posters (Cosmetic)")]
    [Tooltip("Child transforms named PosterSpawn on maze pieces: each site rolls this chance (maze seed) then picks a random non-null prefab from the list. Use non-networked prefabs so host and clients match.")]
    [Range(0f, 1f)]
    [SerializeField] float mazePosterSpawnChance = 0.25f;
    [SerializeField] GameObject[] mazePosterPrefabs = EmptyPrefabs;

    [Header("Maze Start Flashlights")]
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
    /// <summary>
    /// Effective weight when choosing among straight variants for this config. Uses <see cref="straightPrefabWeights"/> when set for <paramref name="poolIndex"/>; otherwise the prefab definition.
    /// </summary>
    public int ResolveStraightSelectionWeight(int poolIndex, MazePieceDefinition definition)
    {
        int defW = definition.Weight;
        int[] weights = straightPrefabWeights;
        if (weights == null || poolIndex < 0 || poolIndex >= weights.Length)
            return defW;

        int w = weights[poolIndex];
        return w <= 0 ? defW : Mathf.Max(1, w);
    }

    public GameObject[] CornerPrefabs => cornerPrefabs ?? EmptyPrefabs;
    public GameObject[] TeePrefabs => teePrefabs ?? EmptyPrefabs;
    public GameObject[] CrossPrefabs => crossPrefabs ?? EmptyPrefabs;
    public GameObject[] SpecialPrefabs => specialPrefabs ?? EmptyPrefabs;
    /// <summary>When true, the generator avoids placing the same corridor prefab in two orthogonally adjacent cells (defaults on; inspector exposes the opt-out).</summary>
    public bool AvoidAdjacentDuplicatePieces => !allowAdjacentDuplicatePieces;
    public GameObject[] InteriorRoomPrefabs => interiorRoomPrefabs ?? EmptyPrefabs;
    public Vector2Int InteriorRoomGridFootprint => new(
        Mathf.Max(1, interiorRoomGridFootprint.x),
        Mathf.Max(1, interiorRoomGridFootprint.y));
    public int InteriorRoomCount => Mathf.Max(0, interiorRoomCount);
    public int InteriorRoomMinChebyshevSeparation => Mathf.Max(1, interiorRoomMinChebyshevSeparation);
    /// <summary>Placement cap for <see cref="InteriorRoomPrefabs"/> index <paramref name="poolIndex"/> this build; 0 means uncapped.</summary>
    public int ResolveInteriorRoomPrefabCap(int poolIndex)
    {
        int[] caps = interiorRoomPrefabCounts;
        if (caps == null || poolIndex < 0 || poolIndex >= caps.Length)
            return 0;

        return Mathf.Max(0, caps[poolIndex]);
    }
    public GameObject ExitHallPiecePrefab => exitHallPiecePrefab;
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
    // Enemy slots 1–4 (see MazeEnemySpawn). Property names keep their descriptive role (the code knows
    // slot 2 is the far-reserved chaser, slot 3 the skeletons, etc.); the inspector labels stay generic.
    public GameObject MazeEnemyPrefab => enemy1 != null ? enemy1.enemyPrefab : null;
    public int MazeEnemyCount => enemy1 != null ? Mathf.Max(0, enemy1.enemyCount) : 0;
    public float MazeEnemySpawnHeight => mazeEnemySpawnHeight;
    public int MazeEnemyMinCellsFromStart => Mathf.Max(0, mazeEnemyMinCellsFromStart);
    public bool MazeEnemyExcludeExitCell => mazeEnemyExcludeExitCell;
    public float MazeEnemyMinSeparation => mazeEnemyMinSeparation;
    public GameObject MazeHunterPrefab => enemy2 != null ? enemy2.enemyPrefab : null;
    public int MazeHunterCount => enemy2 != null ? Mathf.Max(0, enemy2.enemyCount) : 0;
    /// <summary>Minimum start distance for the Enemy 2 slot (the far-reserved chaser), never below the shared maze enemy minimum.</summary>
    public int MazeHunterMinCellsFromStart =>
        Mathf.Max(MazeEnemyMinCellsFromStart, enemy2MinCellsFromStart);
    public GameObject MazeSkeletonPrefab => enemy3 != null ? enemy3.enemyPrefab : null;
    public int MazeSkeletonCount => enemy3 != null ? Mathf.Max(0, enemy3.enemyCount) : 0;

    public GameObject MazeWindupMonkeyPrefab => enemy4 != null ? enemy4.enemyPrefab : null;
    public int MazeWindupMonkeyCount => enemy4 != null ? Mathf.Max(0, enemy4.enemyCount) : 0;
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
    public GameObject[] MazeItemSpawnPrefabs => mazeItemSpawnPrefabs ?? EmptyPrefabs;
    /// <summary>Per <c>ItemSpawn</c> marker, probability [0,1] of spawning a pickup for this maze build.</summary>
    public float MazeItemSpawnChance => Mathf.Clamp01(mazeItemSpawnChance);
    public GameObject MazeTeleportOrbPrefab => mazeTeleportOrbPrefab;
    public int MazeTeleportOrbCount => Mathf.Max(0, mazeTeleportOrbCount);
    public GameObject MazeBatRoostPrefab => mazeBatRoostPrefab;
    public int MazeBatRoostCount => Mathf.Max(0, mazeBatRoostCount);
    public int MazeBatsPerRoost => Mathf.Max(1, mazeBatsPerRoost);
    public float MazeBatRoostHeight => mazeBatRoostHeight;
    public float MazeBatDeadEndShare => Mathf.Clamp01(mazeBatDeadEndShare);
    public int MazeBatRoostMinCellSeparation => Mathf.Max(0, mazeBatRoostMinCellSeparation);
    public GameObject MazeRoachColonyPrefab => mazeRoachColonyPrefab;
    public int MazeRoachColonyCount => Mathf.Max(0, mazeRoachColonyCount);
    public int MazeRoachesPerColony => Mathf.Max(1, mazeRoachesPerColony);
    public float MazeRoachColonyHeight => mazeRoachColonyHeight;
    public float MazeRoachColonySpread => Mathf.Max(0.25f, mazeRoachColonySpread);
    public int MazeRoachColonyMinCellSeparation => Mathf.Max(0, mazeRoachColonyMinCellSeparation);
    public GameObject MazeRatBotPrefab => mazeRatBotPrefab;
    public int MazeRatBotCount => Mathf.Max(0, mazeRatBotCount);
    /// <summary>Per <c>PosterSpawn</c> anchor, probability [0,1] of spawning a poster for this maze build.</summary>
    public float MazePosterSpawnChance => Mathf.Clamp01(mazePosterSpawnChance);
    public GameObject[] MazePosterPrefabs => mazePosterPrefabs ?? EmptyPrefabs;
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
