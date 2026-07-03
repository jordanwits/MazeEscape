using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Visual tuning for the two-handed StarBall carry. The ball's hands are placed by IK onto two grip points
/// (GripPoint_R / GripPoint_L, children of the ball). Enter Play, click Setup, then in the Scene view
/// Move (W) / Rotate (E) each grip until the hands sit right — the hands follow live. Save writes the grips
/// into StarBall.prefab (and the palm-rotation weight into the player prefabs). Editor-only; delete any time.
/// </summary>
public class StarBallGripTuner : EditorWindow
{
    const string StarBallPrefab = "Assets/Prefabs/Maze Components/Carnival/StarBall.prefab";
    static readonly string[] PlayerPrefabs =
    {
        "Assets/Prefabs/Characters/Player_Survivalist.prefab",
        "Assets/Prefabs/Characters/Player_Survivalist2.prefab",
        "Assets/Prefabs/Characters/Player_Survivalist3.prefab",
        "Assets/Prefabs/Characters/Player_Survivalist4.prefab",
    };

    [MenuItem("Tools/StarBall Grip/Open Tuner")]
    static void Open() => GetWindow<StarBallGripTuner>("StarBall Grip");

    void OnInspectorUpdate() => Repaint();

    void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "1. Enter Play (Dev_IKTest).\n" +
            "2. Click 'Setup' — gives the big StarBall and selects the Right grip.\n" +
            "3. In the Scene view, Move (W) / Rotate (E) the selected grip:\n" +
            "   • position = where that hand sits on the ball,\n" +
            "   • rotation = palm facing (rotate so the palm faces the ball).\n" +
            "4. Switch between grips with the buttons below.\n" +
            "5. Click 'Save Grips To Prefab' when both hands look right.",
            MessageType.Info);

        EditorGUILayout.Space();

        Transform gr = FindGrip("GripPoint_R");
        Transform gl = FindGrip("GripPoint_L");
        EditorGUILayout.LabelField("StarBall held:", gr != null ? "yes" : "no (click Setup in Play)");
        if (gr != null) EditorGUILayout.LabelField("Right grip local", gr.localPosition.ToString("F3") + "  e" + gr.localEulerAngles.ToString("F0"));
        if (gl != null) EditorGUILayout.LabelField("Left grip local", gl.localPosition.ToString("F3") + "  e" + gl.localEulerAngles.ToString("F0"));

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("Setup (give StarBall + select Right grip)"))
                Setup();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select Right Grip")) SelectGrip("GripPoint_R");
                if (GUILayout.Button("Select Left Grip")) SelectGrip("GripPoint_L");
            }
        }

        using (new EditorGUI.DisabledScope(gr == null || gl == null))
        {
            if (GUILayout.Button("Save Grips To StarBall Prefab"))
                Save(gr, gl);
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "Tip: rotate a grip so the palm presses the ball's side. The hand follows the grip's rotation.\n" +
            "Save also writes the two-hand palm strength into the player prefabs so it holds up after Play.",
            MessageType.None);
    }

    static StarBallItem FindHeldStarBall()
    {
        foreach (var s in Object.FindObjectsByType<StarBallItem>(FindObjectsSortMode.None))
            if (s.IsHeld && !s.HeldAttachToHandSocket)
                return s;
        return null;
    }

    static Transform FindGrip(string name)
    {
        var ball = FindHeldStarBall();
        return ball != null ? ball.transform.Find(name) : null;
    }

    static void SelectGrip(string name)
    {
        Transform g = FindGrip(name);
        if (g != null)
        {
            Selection.activeGameObject = g.gameObject;
            SceneView.FrameLastActiveSceneView();
        }
        else Debug.LogWarning("[StarBallGrip] StarBall not held. Click Setup first.");
    }

    static void Setup()
    {
        var pc = Object.FindFirstObjectByType<PlayerController>();
        if (pc == null) { Debug.LogWarning("[StarBallGrip] No PlayerController in scene."); return; }

        // drop whatever heavy item is held, then give the big (non-socket) StarBall
        MethodInfo drop = typeof(PlayerController).GetMethod("TryDropHeldHeavyThrowable", BindingFlags.NonPublic | BindingFlags.Instance);
        drop?.Invoke(pc, null);

        StarBallItem target = null;
        foreach (var s in Object.FindObjectsByType<StarBallItem>(FindObjectsSortMode.None))
            if (!s.HeldAttachToHandSocket) { target = s; break; }
        if (target == null) { Debug.LogWarning("[StarBallGrip] No two-handed StarBall (non-socket) found in scene."); return; }
        if (target.TryGetComponent(out NetworkHeavyThrowableHold hold))
            hold.TryPickupOffline(pc);

        // make sure the palm rotation is fully visible while tuning
        var ik = pc.GetComponent<PlayerItemHoldIK>();
        if (ik != null)
        {
            var so = new SerializedObject(ik);
            var p = so.FindProperty("twoHandRotationWeight");
            if (p != null) { p.floatValue = 1f; so.ApplyModifiedProperties(); }
        }

        // finger-curl driver for open/closed hands
        if (pc.GetComponent<TwoHandFingerCurl>() == null)
            pc.gameObject.AddComponent<TwoHandFingerCurl>();

        SelectGrip("GripPoint_R");
    }

    static void Save(Transform gr, Transform gl)
    {
        Vector3 rp = gr.localPosition, lp = gl.localPosition;
        Quaternion rr = gr.localRotation, lr = gl.localRotation;

        var root = PrefabUtility.LoadPrefabContents(StarBallPrefab);
        try
        {
            var item = root.GetComponentInChildren<GrabbableInventoryItem>();
            Transform prgr = item.transform.Find("GripPoint_R");
            Transform prgl = item.transform.Find("GripPoint_L");
            if (prgr != null) { prgr.localPosition = rp; prgr.localRotation = rr; }
            if (prgl != null) { prgl.localPosition = lp; prgl.localRotation = lr; }
            PrefabUtility.SaveAsPrefabAsset(root, StarBallPrefab);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        // persist the palm-rotation strength into the player prefabs (default was too weak)
        var live = Object.FindFirstObjectByType<PlayerItemHoldIK>();
        float weight = 1f;
        if (live != null)
        {
            var so = new SerializedObject(live);
            var p = so.FindProperty("twoHandRotationWeight");
            if (p != null) weight = p.floatValue;
        }
        foreach (string path in PlayerPrefabs)
        {
            var proot = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var ik = proot.GetComponent<PlayerItemHoldIK>();
                if (ik != null)
                {
                    var so = new SerializedObject(ik);
                    var p = so.FindProperty("twoHandRotationWeight");
                    if (p != null) { p.floatValue = weight; so.ApplyModifiedPropertiesWithoutUndo(); }
                }
                // ensure the open-palm driver is on the player (opens both hands for the ball)
                if (proot.GetComponent<TwoHandFingerCurl>() == null)
                    proot.AddComponent<TwoHandFingerCurl>();
                PrefabUtility.SaveAsPrefabAsset(proot, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(proot); }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[StarBallGrip] Saved grips to StarBall.prefab, palm weight {weight:F2}, open-palm driver to player prefabs.");
    }
}
