using UnityEngine;

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

        Gizmos.matrix = transform.localToWorldMatrix;

        float half = FootprintSize * 0.5f;
        Vector3 center = new(0f, GizmoHeight * 0.5f, 0f);
        Vector3 size = new(FootprintSize, GizmoHeight, FootprintSize);

        Gizmos.color = new Color(1f, 1f, 1f, 0.6f);
        Gizmos.DrawWireCube(center, size);

        DrawFace(MazeFaceMask.North, new Vector3(0f, GizmoHeight * 0.5f, half), new Vector3(FootprintSize, GizmoHeight, 0f), alpha);
        DrawFace(MazeFaceMask.East, new Vector3(half, GizmoHeight * 0.5f, 0f), new Vector3(0f, GizmoHeight, FootprintSize), alpha);
        DrawFace(MazeFaceMask.South, new Vector3(0f, GizmoHeight * 0.5f, -half), new Vector3(FootprintSize, GizmoHeight, 0f), alpha);
        DrawFace(MazeFaceMask.West, new Vector3(-half, GizmoHeight * 0.5f, 0f), new Vector3(0f, GizmoHeight, FootprintSize), alpha);

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
