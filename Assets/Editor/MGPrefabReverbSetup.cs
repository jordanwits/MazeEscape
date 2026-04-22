#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds an <see cref="AudioReverbZone"/> to each <c>MG_*.prefab</c> under <c>Prefabs/MG_Components</c>
/// and sizes it from <see cref="MazePieceDefinition"/> (interior grid footprint and footprint size).
/// Run once: <b>Maze Escape &gt; Audio &gt; Add or Update Reverb Zones on MG Prefabs</b>
/// </summary>
public static class MGPrefabReverbSetup
{
    public const string MgPrefabFolder = "Assets/Prefabs/MG_Components";
    public const int MenuPriority = 52;

    [MenuItem("Maze Escape/Audio/Add or Update Reverb Zones on MG Prefabs", false, MenuPriority)]
    public static void AddOrUpdateReverbOnMgPrefabs()
    {
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { MgPrefabFolder });
        int count = 0;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!Path.GetFileName(path).StartsWith("MG_", System.StringComparison.Ordinal))
                continue;

            count += AddOrUpdatePrefab(path) ? 1 : 0;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"MGPrefabReverbSetup: updated {count} MG prefab(s) in \"{MgPrefabFolder}\".");
    }

    static bool AddOrUpdatePrefab(string path)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        if (root == null)
        {
            Debug.LogWarning($"MGPrefabReverbSetup: could not load \"{path}\".");
            return false;
        }

        try
        {
            if (!TryGetMazeDefForReverbZone(root, out MazePieceDefinition def))
            {
                Debug.LogWarning(
                    $"MGPrefabReverbSetup: skipped \"{path}\" (no {nameof(MazePieceDefinition)} on root or child).",
                    root);
                return false;
            }

            if (!root.TryGetComponent(out AudioReverbZone zone))
                zone = root.AddComponent<AudioReverbZone>();

            ApplySettings(def, zone);
            EditorUtility.SetDirty(root);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return true;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>
    /// Picks a piece definition for reverb radii. Prefers a component with an explicit
    /// <see cref="MazePieceDefinition.interiorGridFootprint"/> (multi-cell room pieces) so large rooms
    /// get a large zone. Otherwise matches runtime maze resolution.
    /// </summary>
    static bool TryGetMazeDefForReverbZone(GameObject root, out MazePieceDefinition def)
    {
        def = null;
        MazePieceDefinition[] all = root.GetComponentsInChildren<MazePieceDefinition>(true);
        if (all == null || all.Length == 0)
            return false;

        MazePieceDefinition best = null;
        int bestMaxCells = 0;
        for (int i = 0; i < all.Length; i++)
        {
            MazePieceDefinition d = all[i];
            if (d == null || !d.HasExplicitInteriorFootprint)
                continue;

            Vector2Int g = d.ResolveInteriorGridFootprint(new Vector2Int(1, 1));
            int m = Mathf.Max(g.x, g.y);
            if (m > bestMaxCells)
            {
                bestMaxCells = m;
                best = d;
            }
        }

        if (best != null)
        {
            def = best;
            return true;
        }

        return MazePieceDefinition.TryGetDefinitionForResolution(root, out def);
    }

    static void ApplySettings(MazePieceDefinition def, AudioReverbZone zone)
    {
        // Interior footprint (1,1) for normal cells; (3,3) etc. for big room pieces from the definition.
        Vector2Int grid = def.ResolveInteriorGridFootprint(new Vector2Int(1, 1));
        int maxCells = Mathf.Max(1, Mathf.Max(grid.x, grid.y));
        float halfSide = 0.5f * maxCells * def.FootprintSize;

        // Reverb zones are **spheres**. If maxDistance is shorter than center→corner, walls/corners are outside
        // the zone (dead audio) and you only hear reverb in a “pocket” near the center.
        // Farthest point on a square floor from the prefab center (axis-aligned XZ) is a corner: halfSide * sqrt(2).
        float cornerDistance = halfSide * Mathf.Sqrt(2f);
        // Full wet mix for the whole cell floor: all floor samples have distance to center < minDistance.
        const float fullCoverMargin = 0.25f;
        zone.minDistance = cornerDistance + fullCoverMargin;
        // Short fade outside the footprint so the edge doesn’t hard-cut; stays below typical maze cell spacing.
        const float falloffWidth = 1.25f;
        zone.maxDistance = zone.minDistance + falloffWidth;

        // User preset: strong late tail + long decay so the echo is obvious (Cave/Concerthall were too subtle in practice).
        zone.reverbPreset = AudioReverbPreset.User;
        zone.room = -180;
        zone.roomHF = -200;
        zone.roomLF = -100;
        zone.decayTime = 4.5f;
        zone.decayHFRatio = 0.6f;
        zone.reflections = -200;
        zone.reflectionsDelay = 0.04f;
        zone.reverb = -120;
        zone.reverbDelay = 0.07f;
        zone.HFReference = 5000f;
        zone.LFReference = 250f;
        zone.diffusion = 100f;
        zone.density = 100f;
    }
}
#endif
