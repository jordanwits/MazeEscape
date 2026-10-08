using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The finished cell layout of the maze this peer last built — every cell's open faces after rooms, the start
/// and the exit hall were stamped — kept so it can be drawn as a top-down map after the fact (the game-over
/// card's <see cref="RunTrailMap"/>). <see cref="ProceduralMazeCoordinator"/> builds every level locally and
/// deterministically on every peer, so each peer publishes an identical footprint and nothing is networked.
///
/// Grid +x is world +X and grid +y is world +Z. Cell (x, y) is centred on <c>Origin + (x, y) * CellSize</c>
/// (see ProceduralMazeCoordinator.CellToWorld), so it spans ±half a cell around that point. Pieces bigger than
/// the cells they occupy add <see cref="ExtraFloorAreas"/> for the ground the grid does not account for.
/// </summary>
public sealed class MazeFootprint
{
    readonly MazeFaceMask[] _cells;
    readonly List<Rect> _extraFloorAreas;

    MazeFootprint(string sceneName, int width, int height, Vector3 origin, float cellSize, MazeFaceMask[] cells,
        Vector2Int start, Vector2Int exit, List<Rect> extraFloorAreas)
    {
        SceneName = sceneName;
        Width = width;
        Height = height;
        Origin = origin;
        CellSize = cellSize;
        _cells = cells;
        Start = start;
        Exit = exit;
        _extraFloorAreas = extraFloorAreas ?? new List<Rect>();
    }

    /// <summary>The last maze built on this peer, or null before the first build.</summary>
    public static MazeFootprint Current { get; private set; }

    public string SceneName { get; }
    public int Width { get; }
    public int Height { get; }
    public Vector3 Origin { get; }
    public float CellSize { get; }
    public Vector2Int Start { get; }
    public Vector2Int Exit { get; }

    /// <summary>
    /// World X/Z rectangles of built pieces that cover ground no floor cell covers — a room bigger than the cells
    /// it was placed on (Level01's start room reaches out past the edge of the maze). Walkable, wall-less on the
    /// grid side.
    /// </summary>
    public IReadOnlyList<Rect> ExtraFloorAreas => _extraFloorAreas;

    /// <summary>Called by the coordinator once a build has placed every cell.</summary>
    public static void Publish(string sceneName, int width, int height, Vector3 origin, float cellSize,
        MazeFaceMask[] cellsRowMajor, Vector2Int start, Vector2Int exit, List<Rect> extraFloorAreas)
    {
        Current = new MazeFootprint(sceneName, width, height, origin, cellSize, cellsRowMajor, start, exit,
            extraFloorAreas);
    }

    /// <summary>Open faces of a cell; <see cref="MazeFaceMask.None"/> is solid rock (and anything off the grid).</summary>
    public MazeFaceMask GetOpenings(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return MazeFaceMask.None;
        return _cells[y * Width + x];
    }

    public bool IsFloor(int x, int y) => GetOpenings(x, y) != MazeFaceMask.None;

    /// <summary>Smallest cell rectangle holding every floor cell (grid units; empty when nothing was built).</summary>
    public RectInt GetFloorBounds()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (!IsFloor(x, y))
                    continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < minX ? new RectInt(0, 0, 0, 0) : new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>World X/Z of a cell rectangle's outer corner (the lower-left edge of its first cell).</summary>
    public Vector2 CellCornerWorldXZ(int x, int y)
    {
        return new Vector2(Origin.x + (x - 0.5f) * CellSize, Origin.z + (y - 0.5f) * CellSize);
    }

    /// <summary>World X/Z rectangle holding every floor cell and every extra floor area; zero-size when empty.</summary>
    public Rect GetWorldRect()
    {
        RectInt cells = GetFloorBounds();
        bool any = cells.width > 0;
        Rect world = any
            ? new Rect(CellCornerWorldXZ(cells.xMin, cells.yMin), new Vector2(cells.width, cells.height) * CellSize)
            : default;

        for (int i = 0; i < _extraFloorAreas.Count; i++)
        {
            Rect area = _extraFloorAreas[i];
            world = any
                ? Rect.MinMaxRect(Mathf.Min(world.xMin, area.xMin), Mathf.Min(world.yMin, area.yMin),
                    Mathf.Max(world.xMax, area.xMax), Mathf.Max(world.yMax, area.yMax))
                : area;
            any = true;
        }

        return world;
    }

    /// <summary>Cell containing a world X/Z point (may be off the grid).</summary>
    public Vector2Int WorldToCell(Vector2 worldXZ)
    {
        return new Vector2Int(
            Mathf.RoundToInt((worldXZ.x - Origin.x) / CellSize),
            Mathf.RoundToInt((worldXZ.y - Origin.z) / CellSize));
    }

    /// <summary>True when a world X/Z point lies inside one of the <see cref="ExtraFloorAreas"/>.</summary>
    public bool IsInExtraFloorArea(Vector2 worldXZ)
    {
        for (int i = 0; i < _extraFloorAreas.Count; i++)
        {
            if (_extraFloorAreas[i].Contains(worldXZ))
                return true;
        }

        return false;
    }
}
