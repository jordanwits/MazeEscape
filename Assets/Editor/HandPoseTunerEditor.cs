using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Scene-view handles + bake button for <see cref="HandPoseTuner"/>. Drag the cyan Hand handle and the
/// yellow Elbow handle; tweak the wrist sliders in the inspector; click Bake to write the pose into the
/// one-hand hold clip. Menu "Tools/Hand Pose Tuner/Setup" gives the player a flashlight and adds the tuner.
/// </summary>
[CustomEditor(typeof(HandPoseTuner))]
public class HandPoseTunerEditor : Editor
{
    const string OneHandClipPath = "Assets/Prefabs/Characters/Animations/Hold_OneHand_Pose.anim";

    [MenuItem("Tools/Hand Pose Tuner/Setup (give flashlight + add tuner)")]
    static void Setup()
    {
        var pc = Object.FindFirstObjectByType<PlayerController>();
        if (pc == null) { Debug.LogWarning("[HandPoseTuner] No PlayerController in the scene. Enter Play in Dev_IKTest first."); return; }

        if (Application.isPlaying)
        {
            var flash = Object.FindFirstObjectByType<FlashlightItem>();
            if (flash != null && !flash.IsHeld)
            {
                MethodInfo pickup = typeof(PlayerController).GetMethod(
                    "TryPickupItemLocal", BindingFlags.NonPublic | BindingFlags.Instance);
                if (pickup != null) pickup.Invoke(pc, new object[] { flash });
            }
        }
        else
        {
            Debug.LogWarning("[HandPoseTuner] Enter Play mode for a live preview (the arm poses over the running animation).");
        }

        var tuner = pc.GetComponent<HandPoseTuner>();
        if (tuner == null) tuner = pc.gameObject.AddComponent<HandPoseTuner>();
        tuner.livePreview = true;
        Selection.activeGameObject = pc.gameObject;
        SceneView.FrameLastActiveSceneView();
    }

    void OnSceneGUI()
    {
        var t = (HandPoseTuner)target;
        if (!t.livePreview) return;

        Vector3 handW = t.transform.TransformPoint(t.handTargetLocal);
        Vector3 elbowW = t.transform.TransformPoint(t.elbowHintLocal);

        Handles.color = Color.cyan;
        Handles.SphereHandleCap(0, handW, Quaternion.identity, 0.03f, EventType.Repaint);
        Handles.Label(handW + Vector3.up * 0.05f, "Hand");
        Handles.color = Color.yellow;
        Handles.SphereHandleCap(0, elbowW, Quaternion.identity, 0.025f, EventType.Repaint);
        Handles.Label(elbowW + Vector3.up * 0.05f, "Elbow");

        EditorGUI.BeginChangeCheck();
        Vector3 newHand = Handles.PositionHandle(handW, t.transform.rotation);
        Vector3 newElbow = Handles.PositionHandle(elbowW, t.transform.rotation);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(t, "Tune Arm");
            t.handTargetLocal = t.transform.InverseTransformPoint(newHand);
            t.elbowHintLocal = t.transform.InverseTransformPoint(newElbow);
        }
    }

    public override void OnInspectorGUI()
    {
        EditorGUILayout.HelpBox(
            "PLAY MODE:\n" +
            "• Tools ▸ Hand Pose Tuner ▸ Setup — gives a flashlight + adds this.\n" +
            "• Drag the cyan Hand handle and yellow Elbow handle in the Scene view.\n" +
            "• Use the wrist sliders below (pitch/yaw/roll).\n" +
            "• The flashlight stays pointing forward while you pose the arm.\n" +
            "• Click Bake when it looks right, then toggle livePreview off to check.",
            MessageType.Info);

        DrawDefaultInspector();

        EditorGUILayout.Space();
        var t = (HandPoseTuner)target;
        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("Snap Fields To Current Live Pose"))
            {
                Undo.RecordObject(t, "Snap Hand Tuner");
                t.InitFromCurrentPose();
            }
            if (GUILayout.Button("Bake Arm + Wrist To Hold_OneHand Clip"))
                Bake(t);
        }
    }

    static void Bake(HandPoseTuner t)
    {
        var animator = t.TunerAnimator;
        if (animator == null || !animator.isHuman) { Debug.LogWarning("[HandPoseTuner] No humanoid animator on the player."); return; }
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(OneHandClipPath);
        if (clip == null) { Debug.LogWarning("[HandPoseTuner] Hold_OneHand_Pose.anim not found."); return; }

        t.ApplyNow(); // make sure the skeleton reflects the current fields

        var handler = new HumanPoseHandler(animator.avatar, t.transform);
        var pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        string[] muscles = HumanTrait.MuscleName;

        int written = 0;
        for (int i = 0; i < muscles.Length; i++)
        {
            string m = muscles[i];
            if (!m.StartsWith("Right")) continue;
            bool finger = m.Contains("Thumb") || m.Contains("Index") || m.Contains("Middle") || m.Contains("Ring") || m.Contains("Little");
            bool arm = !finger && (m.Contains("Shoulder") || m.Contains("Arm") || m.Contains("Forearm") || m.Contains("Hand"));
            if (!arm) continue;
            float v = Mathf.Clamp(pose.muscles[i], -1f, 1f);
            var binding = EditorCurveBinding.FloatCurve("", typeof(Animator), m);
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, 1f, v));
            written++;
        }
        handler.Dispose();

        EditorUtility.SetDirty(clip);
        AssetDatabase.SaveAssets();
        Debug.Log($"[HandPoseTuner] Baked {written} right-arm/wrist muscles into Hold_OneHand_Pose.anim. " +
                  "Toggle livePreview OFF to see the baked result. Fingers were left untouched.");
    }
}
