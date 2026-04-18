using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public readonly struct InteriorDoorwaySpec : IEquatable<InteriorDoorwaySpec>
{
    public InteriorDoorwaySpec(Vector2Int cell, MazeFaceMask direction)
    {
        Cell = cell;
        Direction = direction;
    }

    public Vector2Int Cell { get; }
    public MazeFaceMask Direction { get; }

    public bool Equals(InteriorDoorwaySpec other)
    {
        return Cell == other.Cell && Direction == other.Direction;
    }

    public override bool Equals(object obj)
    {
        return obj is InteriorDoorwaySpec other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (Cell.GetHashCode() * 397) ^ (int)Direction;
        }
    }
}

[DisallowMultipleComponent]
public class MazePieceDefinition : MonoBehaviour
{
    [SerializeField] MazePieceCategory category = MazePieceCategory.Straight;
    [SerializeField] MazeFaceMask openFaces = MazeFaceMask.None;
    [SerializeField] bool canRotate = true;
    [SerializeField] int weight = 1;
    [SerializeField] bool useClosedFaceCaps;
    [SerializeField] bool startOnly;
    [SerializeField] bool exitOnly;
    [SerializeField] float yawOffset;

    [Header("Interior rooms (optional)")]
    [Tooltip("When used as an interior room prefab: how many maze cells this piece occupies in XZ. (0,0) = use ProceduralMazeConfig interiorRoomGridFootprint. Requires a solid rectangle of open cells with passages only inward between them.")]
    [SerializeField] Vector2Int interiorGridFootprint;

    [Header("Editor Gizmo")]
    [Tooltip("Footprint of a single cell, used for gizmo drawing. Should match ProceduralMazeConfig.cellSize (default 6).")]
    [SerializeField] float footprintSize = 6f;
    [Tooltip("Wall height used only for visualization in the Scene view.")]
    [SerializeField] float gizmoHeight = 6f;
    [SerializeField] bool drawGizmoAlways = true;

    public MazePieceCategory Category => category;
    public MazeFaceMask OpenFaces => openFaces;
    public bool CanRotate => canRotate;
    public int Weight => Mathf.Max(1, weight);
    public bool UseClosedFaceCaps => useClosedFaceCaps;
    public bool StartOnly => startOnly;
    public bool ExitOnly => exitOnly;
    public float YawOffset => yawOffset;
    public bool HasExplicitInteriorFootprint => interiorGridFootprint.x >= 1 && interiorGridFootprint.y >= 1;
    public Vector2Int ResolveInteriorGridFootprint(Vector2Int configDefault)
    {
        if (interiorGridFootprint.x < 1 || interiorGridFootprint.y < 1)
            return new Vector2Int(Mathf.Max(1, configDefault.x), Mathf.Max(1, configDefault.y));

        return new Vector2Int(Mathf.Max(1, interiorGridFootprint.x), Mathf.Max(1, interiorGridFootprint.y));
    }

    public float FootprintSize => Mathf.Max(0.01f, footprintSize);
    public float GizmoHeight => Mathf.Max(0.01f, gizmoHeight);

    public bool AllowsContext(bool isStart, bool isExit)
    {
        if (startOnly && !isStart)
            return false;

        if (exitOnly && !isExit)
            return false;

        return true;
    }

    public bool TryMatch(MazeFaceMask requiredOpenFaces, out int quarterTurns)
    {
        int turnCount = canRotate ? 4 : 1;
        for (int i = 0; i < turnCount; i++)
        {
            if (MazeFaceMaskUtility.Rotate(openFaces, i) != requiredOpenFaces)
                continue;

            quarterTurns = i;
            return true;
        }

        quarterTurns = 0;
        return false;
    }

    /// <summary>
    /// Uses the root's <see cref="MazePieceDefinition"/> if present; otherwise finds one on a child
    /// (e.g. a door section). When several children have openings, prefers an explicit
    /// <see cref="interiorGridFootprint"/>, then a sole non-<see cref="MazeFaceMask.None"/> definition,
    /// then the definition with the most open faces.
    /// </summary>
    public static bool TryGetDefinitionForResolution(GameObject prefabRoot, out MazePieceDefinition definition)
    {
        definition = null;
        if (prefabRoot == null)
            return false;

        if (prefabRoot.TryGetComponent(out definition))
            return true;

        MazePieceDefinition[] all = prefabRoot.GetComponentsInChildren<MazePieceDefinition>(true);
        if (all == null || all.Length == 0)
            return false;

        MazePieceDefinition explicitFootprint = null;
        List<MazePieceDefinition> withOpen = new();
        for (int i = 0; i < all.Length; i++)
        {
            MazePieceDefinition d = all[i];
            if (d.interiorGridFootprint.x >= 1 && d.interiorGridFootprint.y >= 1)
                explicitFootprint = d;

            if (d.openFaces != MazeFaceMask.None)
                withOpen.Add(d);
        }

        if (explicitFootprint != null)
        {
            definition = explicitFootprint;
            return true;
        }

        if (withOpen.Count == 1)
        {
            definition = withOpen[0];
            return true;
        }

        if (withOpen.Count == 0)
            return false;

        int bestCount = -1;
        for (int i = 0; i < withOpen.Count; i++)
        {
            int c = MazeFaceMaskUtility.CountOpenFaces(withOpen[i].openFaces);
            if (c > bestCount)
            {
                bestCount = c;
                definition = withOpen[i];
            }
        }

        return definition != null;
    }

    public static bool TryGetExactInteriorDoorwaySpecs(
        GameObject prefabRoot,
        Vector2Int footprint,
        float cellSize,
        out List<InteriorDoorwaySpec> specs)
    {
        specs = null;
        if (prefabRoot == null || footprint.x < 1 || footprint.y < 1)
            return false;

        MazePieceDefinition[] all = prefabRoot.GetComponentsInChildren<MazePieceDefinition>(true);
        if (all == null || all.Length == 0)
            return false;

        Transform root = prefabRoot.transform;
        HashSet<InteriorDoorwaySpec> unique = new();
        for (int i = 0; i < all.Length; i++)
        {
            MazePieceDefinition definition = all[i];
            if (definition == null || definition.transform == root || definition.OpenFaces == MazeFaceMask.None)
                continue;

            if (!TryMapTransformToInteriorCell(root, definition.transform, footprint, cellSize, out Vector2Int cell))
                continue;

            AddDoorwaySpecs(unique, cell, definition.OpenFaces);
        }

        if (unique.Count == 0)
            return false;

        specs = new List<InteriorDoorwaySpec>(unique);
        return true;
    }

    static bool TryMapTransformToInteriorCell(
        Transform root,
        Transform marker,
        Vector2Int footprint,
        float cellSize,
        out Vector2Int cell)
    {
        cell = default;
        if (root == null || marker == null || footprint.x < 1 || footprint.y < 1 || cellSize <= 0.01f)
            return false;

        Vector3 local = root.InverseTransformPoint(marker.position);
        float startX = -(footprint.x - 1) * 0.5f * cellSize;
        float startZ = -(footprint.y - 1) * 0.5f * cellSize;
        int ix = Mathf.RoundToInt((local.x - startX) / cellSize);
        int iy = Mathf.RoundToInt((local.z - startZ) / cellSize);
        if (ix < 0 || ix >= footprint.x || iy < 0 || iy >= footprint.y)
            return false;

        cell = new Vector2Int(ix, iy);
        return true;
    }

    static void AddDoorwaySpecs(HashSet<InteriorDoorwaySpec> specs, Vector2Int cell, MazeFaceMask faces)
    {
        if ((faces & MazeFaceMask.North) != 0)
            specs.Add(new InteriorDoorwaySpec(cell, MazeFaceMask.North));
        if ((faces & MazeFaceMask.East) != 0)
            specs.Add(new InteriorDoorwaySpec(cell, MazeFaceMask.East));
        if ((faces & MazeFaceMask.South) != 0)
            specs.Add(new InteriorDoorwaySpec(cell, MazeFaceMask.South));
        if ((faces & MazeFaceMask.West) != 0)
            specs.Add(new InteriorDoorwaySpec(cell, MazeFaceMask.West));
    }

#if UNITY_EDITOR
    public void SetOpenFacesFromEditor(MazeFaceMask faces)
    {
        openFaces = faces;
    }
#endif

    void OnValidate()
    {
        weight = Mathf.Max(1, weight);
        footprintSize = Mathf.Max(0.01f, footprintSize);
        gizmoHeight = Mathf.Max(0.01f, gizmoHeight);
        if (interiorGridFootprint.x < 0 || interiorGridFootprint.y < 0)
            interiorGridFootprint = Vector2Int.zero;
    }

    void OnDrawGizmos()
    {
        if (!drawGizmoAlways)
            return;

        DrawFaceGizmos(0.35f);
    }

    void OnDrawGizmosSelected()
    {
        if (drawGizmoAlways)
            return;

        DrawFaceGizmos(0.6f);
    }

    void DrawFaceGizmos(float alpha)
    {
        Matrix4x4 previousMatrix = Gizmos.matrix;
        Color previousColor = Gizmos.color;

        // Do not use localToWorldMatrix: non-uniform scale on prefab roots (e.g. legacy layout scale)
        // skews the footprint into a tall slab. Also ignore pitch/roll here because maze placement
        // only supports flat XZ footprints with optional Y-axis rotation.
        int cellsX = interiorGridFootprint.x >= 1 ? interiorGridFootprint.x : 1;
        int cellsZ = interiorGridFootprint.y >= 1 ? interiorGridFootprint.y : 1;
        float sizeX = cellsX * FootprintSize;
        float sizeZ = cellsZ * FootprintSize;
        float halfX = sizeX * 0.5f;
        float halfZ = sizeZ * 0.5f;

        Quaternion flatRotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position, flatRotation, Vector3.one);

        Vector3 center = new Vector3(0f, GizmoHeight * 0.5f, 0f);
        Vector3 size = new Vector3(sizeX, GizmoHeight, sizeZ);

        Gizmos.color = new Color(1f, 1f, 1f, 0.6f);
        Gizmos.DrawWireCube(center, size);

        DrawFace(MazeFaceMask.North, new Vector3(0f, GizmoHeight * 0.5f, halfZ), new Vector3(sizeX, GizmoHeight, 0f), alpha);
        DrawFace(MazeFaceMask.East, new Vector3(halfX, GizmoHeight * 0.5f, 0f), new Vector3(0f, GizmoHeight, sizeZ), alpha);
        DrawFace(MazeFaceMask.South, new Vector3(0f, GizmoHeight * 0.5f, -halfZ), new Vector3(sizeX, GizmoHeight, 0f), alpha);
        DrawFace(MazeFaceMask.West, new Vector3(-halfX, GizmoHeight * 0.5f, 0f), new Vector3(0f, GizmoHeight, sizeZ), alpha);

        Gizmos.matrix = previousMatrix;
        Gizmos.color = previousColor;
    }

    void DrawFace(MazeFaceMask face, Vector3 center, Vector3 size, float alpha)
    {
        bool isOpen = (openFaces & face) != 0;
        Gizmos.color = isOpen
            ? new Color(0.15f, 0.85f, 0.25f, alpha)
            : new Color(0.95f, 0.2f, 0.2f, alpha);
        Gizmos.DrawCube(center, size + new Vector3(0.05f, 0f, 0.05f));
    }
}
