using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MazePieceDefinition))]
[CanEditMultipleObjects]
public class MazePieceDefinitionEditor : Editor
{
    static readonly (MazeFaceMask Face, Vector3 Outward)[] Faces =
    {
        (MazeFaceMask.North, Vector3.forward),
        (MazeFaceMask.East, Vector3.right),
        (MazeFaceMask.South, Vector3.back),
        (MazeFaceMask.West, Vector3.left)
    };

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Open-Face Tools", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Open-face green/red gizmos are drawn around each piece. " +
            "Use \"Detect From Geometry\" to auto-derive openFaces from collider walls.",
            MessageType.Info);

        if (GUILayout.Button("Detect Open Faces From Geometry"))
            DetectOpenFacesForSelection();
    }

    void DetectOpenFacesForSelection()
    {
        Undo.SetCurrentGroupName("Detect Maze Piece Open Faces");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (Object target in targets)
        {
            if (target is not MazePieceDefinition definition)
                continue;

            MazeFaceMask detected = DetectOpenFaces(definition);
            Undo.RecordObject(definition, "Detect Open Faces");
            definition.SetOpenFacesFromEditor(detected);
            EditorUtility.SetDirty(definition);

            if (PrefabUtility.IsPartOfPrefabInstance(definition))
                PrefabUtility.RecordPrefabInstancePropertyModifications(definition);

            Debug.Log($"[Maze] Detected open faces for '{definition.name}' = {detected}", definition);
        }

        Undo.CollapseUndoOperations(undoGroup);
    }

    public static MazeFaceMask DetectOpenFaces(MazePieceDefinition definition)
    {
        float footprint = definition.FootprintSize;
        float half = footprint * 0.5f;
        float interiorMargin = footprint * 0.1f;

        Transform root = definition.transform;
        List<Collider> colliders = CollectStaticColliders(root);

        MazeFaceMask result = MazeFaceMask.None;

        foreach ((MazeFaceMask face, Vector3 outward) in Faces)
        {
            Vector3 worldOutward = root.TransformDirection(outward);
            Vector3 worldFaceCenter = root.position + worldOutward * (half - interiorMargin * 0.5f);
            Vector3 worldFaceCenter2 = root.position + worldOutward * (half - interiorMargin * 1.5f);

            Vector3 tangent = root.TransformDirection(new Vector3(outward.z, 0f, -outward.x));

            bool blocked = FaceHasBlocker(colliders, worldFaceCenter, tangent, footprint, definition.GizmoHeight)
                        || FaceHasBlocker(colliders, worldFaceCenter2, tangent, footprint, definition.GizmoHeight);

            if (!blocked)
                result |= face;
        }

        return result;
    }

    static List<Collider> CollectStaticColliders(Transform root)
    {
        List<Collider> list = new();
        Collider[] all = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Collider c = all[i];
            if (c == null || c.isTrigger)
                continue;

            if (IsLikelyFloorOrCeiling(c, root))
                continue;

            list.Add(c);
        }

        return list;
    }

    static bool IsLikelyFloorOrCeiling(Collider c, Transform root)
    {
        Bounds b = c.bounds;
        float verticalSpan = b.size.y;
        float horizontalSpan = Mathf.Max(b.size.x, b.size.z);
        if (verticalSpan < 0.25f && horizontalSpan > 1f)
            return true;

        return false;
    }

    static bool FaceHasBlocker(List<Collider> colliders, Vector3 sampleCenter, Vector3 tangent, float footprint, float pieceHeight)
    {
        float step = footprint * 0.2f;
        float interiorReach = footprint * 0.4f;
        Vector3 t = tangent.sqrMagnitude > 0.0001f ? tangent.normalized : Vector3.right;

        for (float offset = -interiorReach; offset <= interiorReach + 0.001f; offset += step)
        {
            Vector3 sample = sampleCenter + t * offset;
            for (float hRatio = 0.2f; hRatio <= 0.81f; hRatio += 0.3f)
            {
                Vector3 probe = new(sample.x, sampleCenter.y - pieceHeight * 0.5f + pieceHeight * hRatio, sample.z);
                for (int i = 0; i < colliders.Count; i++)
                {
                    if (colliders[i].bounds.Contains(probe))
                        return true;
                }
            }
        }

        return false;
    }
}
