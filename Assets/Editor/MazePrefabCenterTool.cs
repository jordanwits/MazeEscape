using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MazePrefabCenterTool
{
    const string MenuPath = "Maze Escape/Center Selected Prefab Root (XZ Center, Floor Y=0)";

    [MenuItem(MenuPath)]
    public static void CenterSelectedPrefabRoots()
    {
        List<Transform> roots = CollectSelectedRoots();
        if (roots.Count == 0)
        {
            Debug.LogWarning("[Maze Escape] Select one or more prefab roots (or any object inside a prefab) and run the command again.");
            return;
        }

        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Center MG prefab footprint");

        try
        {
            foreach (Transform root in roots)
            {
                if (root == null)
                    continue;

                if (!TryCalculateRendererBounds(root, out Bounds bounds))
                {
                    Debug.LogWarning($"[Maze Escape] No MeshRenderer/SkinnedMeshRenderer found under '{GetTransformPath(root)}'.", root.gameObject);
                    continue;
                }

                Vector3 center = bounds.center;
                float minY = bounds.min.y;
                Vector3 worldOffset = new(-center.x, -minY, -center.z);

                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                Undo.RecordObjects(transforms, "Shift prefab geometry to center footprint");

                foreach (Transform t in transforms)
                    t.position += worldOffset;

                Undo.RecordObject(root, "Reset prefab root transform");
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;
                root.localScale = Vector3.one;

                EditorUtility.SetDirty(root.gameObject);
                if (PrefabUtility.IsPartOfPrefabInstance(root.gameObject))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(root);

                if (PrefabUtility.IsPartOfPrefabAsset(root.gameObject))
                    PrefabUtility.SavePrefabAsset(root.gameObject);

                Debug.Log(
                    $"[Maze Escape] Centered '{GetTransformPath(root)}' using renderer bounds. " +
                    $"Applied world shift {worldOffset}.",
                    root.gameObject);
            }
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
        }

        PrefabStage prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
        if (prefabStage != null)
            EditorSceneManager.MarkSceneDirty(prefabStage.scene);
        else if (roots.Count > 0 && roots[0] != null)
            EditorSceneManager.MarkSceneDirty(roots[0].gameObject.scene);

        AssetDatabase.SaveAssets();
    }

    static List<Transform> CollectSelectedRoots()
    {
        HashSet<Transform> unique = new();
        foreach (GameObject go in Selection.gameObjects)
        {
            if (go == null)
                continue;

            Transform root = FindOutermostTransform(go.transform);
            if (root != null)
                unique.Add(root);
        }

        List<Transform> list = new(unique.Count);
        foreach (Transform t in unique)
            list.Add(t);

        list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return list;
    }

    static Transform FindOutermostTransform(Transform selected)
    {
        Transform current = selected;
        while (current.parent != null)
            current = current.parent;

        return current;
    }

    static bool TryCalculateRendererBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return false;

        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);

        return true;
    }

    static string GetTransformPath(Transform t)
    {
        if (t == null)
            return "(null)";

        if (t.parent == null)
            return t.name;

        List<string> segments = new();
        Transform current = t;
        while (current != null)
        {
            segments.Add(current.name);
            current = current.parent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }
}
