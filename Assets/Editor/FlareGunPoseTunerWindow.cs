using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// DEV TOOL. Drives <see cref="FlareGunPoseTuner"/> in play mode and bakes the result:
/// the arm pose into <c>Hold_FlareGun_Pose.anim</c> (humanoid muscles) and the gun's seating into
/// <c>FlareGunFull.prefab</c> (grip point + rotation offset). Baking from PLAY MODE is deliberate —
/// the arm muscles are relative to the chest, which locomotion drives, so a pose baked from play mode
/// reproduces exactly in play mode.
/// </summary>
public class FlareGunPoseTunerWindow : EditorWindow
{
    const string HoldClipPath = "Assets/Prefabs/Characters/Animations/Hold_FlareGun_Pose.anim";
    const string GunPrefabPath = "Assets/Prefabs/Items/FlareGunFull.prefab";
    const string MixamoClipPath = "Assets/Prefabs/Characters/Animations/Pistol Idle.anim";
    const string PlayerPrefabPath = "Assets/Prefabs/Characters/Player_Survivalist.prefab";

    Vector2 _scroll;
    string _status = "";

    [MenuItem("Tools/Flare Gun Pose Tuner")]
    static void Open()
    {
        GetWindow<FlareGunPoseTunerWindow>("Flare Gun Pose");
    }

    void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.HelpBox(
            "1. Enter play mode and pick up the flare gun.\n" +
            "2. Setup Tuner.\n" +
            "3. Select TUNER_WristR / TUNER_ElbowR / TUNER_WristL / TUNER_ElbowL in the Hierarchy and move "
            + "(or rotate the wrist handles) in the Scene view.\n" +
            "4. To place the gun itself, untick 'Gun follows hand' on the tuner, drag the gun, tick it back on.\n" +
            "5. Bake.",
            MessageType.Info);

        var tuner = Object.FindFirstObjectByType<FlareGunPoseTuner>();
        EditorGUILayout.LabelField("Tuner in scene", tuner != null ? "yes" : "no");
        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("Setup Tuner", GUILayout.Height(28)))
                SetupTuner();

            using (new EditorGUI.DisabledScope(tuner == null))
            {
                if (GUILayout.Button("Reset Handles To Current Pose"))
                {
                    tuner.ResetHandlesToPose();
                    _status = "Handles snapped back onto the animated pose.";
                }

                EditorGUILayout.Space();
                if (GUILayout.Button("BAKE  (arms -> clip, gun -> prefab)", GUILayout.Height(34)))
                    Bake(tuner);

                EditorGUILayout.Space();
                if (GUILayout.Button("Remove Tuner"))
                {
                    Object.DestroyImmediate(tuner);
                    _status = "Tuner removed (handles go with it).";
                }
            }
        }

        if (!Application.isPlaying)
            EditorGUILayout.HelpBox("Enter play mode to use the tuner.", MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Recovery", EditorStyles.boldLabel);
        if (GUILayout.Button("Restore clean Mixamo pistol pose into the clip"))
            RestoreMixamoPose();

        if (!string.IsNullOrEmpty(_status))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_status, MessageType.None);
        }

        EditorGUILayout.EndScrollView();
    }

    void SetupTuner()
    {
        var pc = Object.FindFirstObjectByType<PlayerController>();
        if (pc == null) { _status = "No PlayerController in the scene."; return; }

        var tuner = pc.GetComponent<FlareGunPoseTuner>();
        if (tuner == null)
            tuner = pc.gameObject.AddComponent<FlareGunPoseTuner>();
        tuner.EnsureHandles();
        Selection.activeGameObject = tuner.wristTargetR != null ? tuner.wristTargetR.gameObject : pc.gameObject;
        _status = "Tuner ready. Handles created at the current pose; TUNER_WristR selected.";
    }

    // ---------- baking ----------

    void Bake(FlareGunPoseTuner tuner)
    {
        var pc = tuner.GetComponent<PlayerController>();
        Animator anim = tuner.TunedAnimator;
        if (anim == null || !anim.isHuman) { _status = "No humanoid Animator found."; return; }

        var log = new System.Text.StringBuilder();

        // ---- arms -> muscle curves ----
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(HoldClipPath);
        if (clip == null) { _status = "Hold_FlareGun_Pose.anim not found."; return; }

        var handler = new HumanPoseHandler(anim.avatar, anim.transform);
        HumanPose pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        handler.Dispose();

        int written = WriteArmAndFingerMuscles(clip, pose);
        log.AppendLine("arm + finger muscle curves written: " + written);

        // ---- gun -> prefab ----
        var gun = Object.FindFirstObjectByType<FlareGunItem>();
        if (gun != null && pc != null)
        {
            Transform handR = anim.GetBoneTransform(HumanBodyBones.RightHand);
            Transform socket = handR != null ? handR.Find("GripSocket_R") : null;
            Transform holdPoint, followTransform;
            pc.TryGetFlashlightAttachmentTargets(out holdPoint, out followTransform);

            if (socket == null || followTransform == null)
            {
                log.AppendLine("gun NOT baked (GripSocket_R or camera-pitch transform missing).");
            }
            else
            {
                Transform grip = gun.transform.Find("GripPoint_R");
                Quaternion gripLocalRot = grip != null ? grip.localRotation : Quaternion.identity;
                Quaternion itemRot = gun.transform.rotation;

                // invert GrabbableInventoryItem.ApplyHandSocketHeldPoseAim
                Quaternion offset = gripLocalRot * Quaternion.Inverse(followTransform.rotation) * itemRot;
                Vector3 scale = gun.transform.localScale;
                Vector3 local = Quaternion.Inverse(itemRot) * (socket.position - gun.transform.position);
                Vector3 gripLocalPos = new Vector3(
                    local.x / Mathf.Max(1e-6f, scale.x),
                    local.y / Mathf.Max(1e-6f, scale.y),
                    local.z / Mathf.Max(1e-6f, scale.z));

                GameObject root = PrefabUtility.LoadPrefabContents(GunPrefabPath);
                try
                {
                    Transform prefabGrip = root.transform.Find("GripPoint_R");
                    if (prefabGrip != null)
                        prefabGrip.localPosition = gripLocalPos;
                    var so = new SerializedObject(root.GetComponent<FlareGunItem>());
                    so.FindProperty("heldRotationOffsetEuler").vector3Value = offset.eulerAngles;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.SaveAsPrefabAsset(root, GunPrefabPath);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }

                // keep the live instance consistent so the view does not jump after baking
                if (grip != null)
                    grip.localPosition = gripLocalPos;
                var liveSo = new SerializedObject(gun);
                liveSo.FindProperty("heldRotationOffsetEuler").vector3Value = offset.eulerAngles;
                liveSo.ApplyModifiedPropertiesWithoutUndo();

                log.AppendLine("gun grip = " + gripLocalPos.ToString("F4") + "  rotOffset = " + offset.eulerAngles.ToString("F2"));
            }
        }
        else
        {
            log.AppendLine("gun NOT baked (no FlareGunItem held).");
        }

        AssetDatabase.SaveAssets();

        Vector3 hr = anim.GetBoneTransform(HumanBodyBones.RightHand).position;
        Vector3 hl = anim.GetBoneTransform(HumanBodyBones.LeftHand).position;
        log.AppendLine("baked hand distance = " + (hr - hl).magnitude.ToString("F3") + " m");
        _status = log.ToString();
        Debug.Log("[FlareGunPoseTuner] " + _status);
    }

    static int WriteArmAndFingerMuscles(AnimationClip clip, HumanPose pose)
    {
        var muscleIndex = new Dictionary<string, int>();
        for (int i = 0; i < HumanTrait.MuscleCount; i++)
            muscleIndex[HumanTrait.MuscleName[i]] = i;

        var targets = new List<string>();
        foreach (string side in new[] { "Left", "Right" })
        {
            foreach (string a in new[] { "Shoulder Down-Up", "Shoulder Front-Back", "Arm Down-Up", "Arm Front-Back",
                                         "Arm Twist In-Out", "Forearm Stretch", "Forearm Twist In-Out",
                                         "Hand Down-Up", "Hand In-Out" })
                targets.Add(side + " " + a);
            foreach (string f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            {
                for (int n = 1; n <= 3; n++) targets.Add(side + " " + f + " " + n + " Stretched");
                targets.Add(side + " " + f + " Spread");
            }
        }

        int written = 0;
        foreach (string m in targets)
        {
            int idx;
            if (!muscleIndex.TryGetValue(m, out idx))
                continue;
            float v = Mathf.Clamp(pose.muscles[idx], -1f, 1f);
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve("", typeof(Animator), MuscleToBinding(m)),
                AnimationCurve.Constant(0f, 0.0333f, v));
            written++;
        }

        AnimationClipSettings st = AnimationUtility.GetAnimationClipSettings(clip);
        st.loopTime = true;
        st.loopBlend = false;
        AnimationUtility.SetAnimationClipSettings(clip, st);
        EditorUtility.SetDirty(clip);
        return written;
    }

    /// <summary>
    /// Humanoid finger muscles are keyed under a different name than <see cref="HumanTrait.MuscleName"/>
    /// reports ("RightHand.Index.1 Stretched" vs "Right Index 1 Stretched"); a curve written under the
    /// wrong one is silently ignored. Arm/shoulder/wrist muscles match in both conventions.
    /// </summary>
    static string MuscleToBinding(string muscle)
    {
        string side = muscle.StartsWith("Left ") ? "Left" : (muscle.StartsWith("Right ") ? "Right" : null);
        if (side == null)
            return muscle;

        string rest = muscle.Substring(side.Length + 1);
        foreach (string finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
        {
            if (!rest.StartsWith(finger + " "))
                continue;
            string tail = rest.Substring(finger.Length + 1);
            if (tail == "Spread")
                return side + "Hand." + finger + ".Spread";
            return side + "Hand." + finger + "." + tail.Substring(0, 1) + " Stretched";
        }

        return muscle;
    }

    // ---------- recovery ----------

    void RestoreMixamoPose()
    {
        var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        var src = AssetDatabase.LoadAssetAtPath<AnimationClip>(MixamoClipPath);
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(HoldClipPath);
        if (playerPrefab == null || src == null || clip == null) { _status = "Missing player prefab / Pistol Idle / hold clip."; return; }

        GameObject inst = Object.Instantiate(playerPrefab, Vector3.zero, Quaternion.identity);
        try
        {
            var anim = inst.GetComponentInChildren<Animator>();

            // The Mixamo clip is a generic transform-curve clip; SampleAnimation ignores those on a humanoid
            // animator, so apply the bone-local rotations by hand.
            var rot = new Dictionary<string, Vector4>();
            var have = new Dictionary<string, bool[]>();
            foreach (var b in AnimationUtility.GetCurveBindings(src))
            {
                if (!b.propertyName.StartsWith("m_LocalRotation.")) continue;
                if (b.path == "" || b.path == "FPS_HANDS") continue;
                float v = AnimationUtility.GetEditorCurve(src, b).Evaluate(0f);
                if (!rot.ContainsKey(b.path)) { rot[b.path] = Vector4.zero; have[b.path] = new bool[4]; }
                Vector4 q = rot[b.path];
                char c = b.propertyName[b.propertyName.Length - 1];
                if (c == 'x') { q.x = v; have[b.path][0] = true; }
                else if (c == 'y') { q.y = v; have[b.path][1] = true; }
                else if (c == 'z') { q.z = v; have[b.path][2] = true; }
                else { q.w = v; have[b.path][3] = true; }
                rot[b.path] = q;
            }
            foreach (var kv in rot)
            {
                bool[] h = have[kv.Key];
                if (!(h[0] && h[1] && h[2] && h[3])) continue;
                Transform t = anim.transform.Find(kv.Key);
                if (t != null)
                    t.localRotation = new Quaternion(kv.Value.x, kv.Value.y, kv.Value.z, kv.Value.w).normalized;
            }

            var handler = new HumanPoseHandler(anim.avatar, anim.transform);
            HumanPose pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            handler.Dispose();

            int n = WriteArmAndFingerMuscles(clip, pose);
            AssetDatabase.SaveAssets();
            _status = "Restored the clean Mixamo two-handed pose into the clip (" + n + " curves). "
                + "In play mode, re-pick up the gun (or re-enter play mode) to see it.";
        }
        finally { Object.DestroyImmediate(inst); }
    }
}
