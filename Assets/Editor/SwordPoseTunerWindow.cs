using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// DEV TOOL. Drives <see cref="SwordPoseTuner"/> in play mode and bakes the result: the right arm and hand
/// into <c>Hold_Sword_Pose.anim</c> (humanoid muscles), and the blade's seat in the fist into
/// <c>Sword.prefab</c> (grip point + rotation offset).
///
/// Baking from PLAY MODE is deliberate — arm muscles are stored relative to the chest, which locomotion
/// drives, so a pose that measures perfectly in an edit-mode preview lands somewhere else in the running game.
/// </summary>
public class SwordPoseTunerWindow : EditorWindow
{
    const string HoldClipPath = "Assets/Prefabs/Characters/Animations/Hold_Sword_Pose.anim";
    const string SwordPrefabPath = "Assets/Prefabs/Items/Sword.prefab";

    Vector2 _scroll;
    string _status = "";

    [MenuItem("Tools/Sword Pose Tuner")]
    static void Open()
    {
        GetWindow<SwordPoseTunerWindow>("Sword Pose");
    }

    void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.HelpBox(
            "1. Enter play mode and pick up the sword.\n" +
            "2. Setup Tuner.\n" +
            "3. ELBOW: select TUNER_ElbowR and move it — swivels the arm without moving the hand.\n" +
            "   WRIST: move/rotate TUNER_WristR, or use the wrist trim sliders below.\n" +
            "   CARRY: type the grip/rotation values below, or untick 'Sword follows hand', drag the blade "
            + "in the Scene view, then hit Capture.\n" +
            "4. Bake.",
            MessageType.Info);

        var tuner = Object.FindFirstObjectByType<SwordPoseTuner>();
        EditorGUILayout.LabelField("Tuner in scene", tuner != null ? "yes" : "no");

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Enter play mode to use the tuner.", MessageType.Warning);
            DrawRecovery();
            DrawStatus();
            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Setup Tuner", GUILayout.Height(28)))
            SetupTuner();

        using (new EditorGUI.DisabledScope(tuner == null))
        {
            if (tuner != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Arm", EditorStyles.boldLabel);
                Undo.RecordObject(tuner, "Sword Pose Tuner");
                tuner.solveArm = EditorGUILayout.Toggle(
                    new GUIContent("Solve arm", "Off shows the raw clip pose again."), tuner.solveArm);
                tuner.wristFromHandle = EditorGUILayout.Toggle(
                    new GUIContent("Wrist from handle", "Rotating TUNER_WristR rolls the grip."), tuner.wristFromHandle);

                EditorGUILayout.LabelField("Wrist trim (deg, forearm frame)");
                tuner.wristEuler = EditorGUILayout.Vector3Field(GUIContent.none, tuner.wristEuler);
                if (GUILayout.Button("Zero wrist trim"))
                    tuner.wristEuler = Vector3.zero;

                if (GUILayout.Button("Reset Handles To Current Pose"))
                {
                    tuner.ResetHandlesToPose();
                    _status = "Handles snapped back onto the animated pose.";
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Sword carry", EditorStyles.boldLabel);
                tuner.applyCarryOverrides = EditorGUILayout.Toggle(
                    new GUIContent("Apply carry live", "Push the values below onto the held sword each frame."),
                    tuner.applyCarryOverrides);
                tuner.carryGripLocalPosition = EditorGUILayout.Vector3Field("Grip offset", tuner.carryGripLocalPosition);
                tuner.carryRotationEuler = EditorGUILayout.Vector3Field("Rotation offset", tuner.carryRotationEuler);

                bool wasFollowing = tuner.swordFollowsHand;
                tuner.swordFollowsHand = EditorGUILayout.Toggle(
                    new GUIContent("Sword follows hand", "Off releases the blade so it can be dragged in the Scene view."),
                    tuner.swordFollowsHand);
                if (wasFollowing && !tuner.swordFollowsHand)
                    _status = "Sword released. Freeze the animation before placing it, or the hand moves out from under it.";

                if (GUILayout.Button("Capture Sword From Scene"))
                {
                    string message;
                    tuner.CaptureCarryFromScene(out message);
                    _status = message;
                }

                Animator anim = tuner.TunedAnimator;
                bool frozen = anim != null && Mathf.Approximately(anim.speed, 0f);
                if (GUILayout.Button(frozen ? "Unfreeze animation (speed 1)" : "Freeze animation (speed 0)"))
                {
                    if (anim != null)
                    {
                        anim.speed = frozen ? 1f : 0f;
                        _status = frozen
                            ? "Animation running again."
                            : "Animation frozen. The hand, socket and player frame are now rigid relative to each "
                              + "other, so a placement captured now bakes exactly — you can even walk around.";
                    }
                }

                EditorGUILayout.Space();
                if (GUILayout.Button("BAKE  (arm -> clip, carry -> prefab)", GUILayout.Height(34)))
                    Bake(tuner);

                EditorGUILayout.Space();
                if (GUILayout.Button("Remove Tuner"))
                {
                    if (anim != null)
                        anim.speed = 1f;
                    Object.DestroyImmediate(tuner);
                    _status = "Tuner removed (handles go with it).";
                }
            }
        }

        DrawRecovery();
        DrawStatus();
        EditorGUILayout.EndScrollView();
    }

    void DrawRecovery()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Recovery", EditorStyles.boldLabel);
        if (GUILayout.Button("Restore hold pose from frame 0 of the swing"))
        {
            SwordClipBuilder.RebuildHoldPoseOnly();
            _status = "Hold_Sword_Pose.anim re-derived from the authored swing. In play mode, re-pick up the "
                + "sword (or re-enter play mode) to see it.";
        }
    }

    void DrawStatus()
    {
        if (string.IsNullOrEmpty(_status))
            return;
        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(_status, MessageType.None);
    }

    void SetupTuner()
    {
        var pc = Object.FindFirstObjectByType<PlayerController>();
        if (pc == null) { _status = "No PlayerController in the scene."; return; }

        var tuner = pc.GetComponent<SwordPoseTuner>();
        if (tuner == null)
            tuner = pc.gameObject.AddComponent<SwordPoseTuner>();
        tuner.EnsureHandles();
        tuner.SeedCarryFromHeldSword();
        Selection.activeGameObject = tuner.wristTargetR != null ? tuner.wristTargetR.gameObject : pc.gameObject;
        _status = "Tuner ready. Handles created at the current pose; TUNER_WristR selected. "
            + (tuner.FindHeldSword() != null ? "Sword found." : "NOTE: no sword in hand — pick one up first.");
    }

    // ---------- baking ----------

    void Bake(SwordPoseTuner tuner)
    {
        Animator anim = tuner.TunedAnimator;
        if (anim == null || !anim.isHuman) { _status = "No humanoid Animator found."; return; }

        // The bake captures whatever the rig is showing. If the Item Hold layer is not actually sitting in
        // Hold_Sword, the right arm on screen is the base layer's idle — baking then silently overwrites the
        // stance with an unrelated pose. (Hit while building this: freezing the animator before the layer had
        // transitioned baked the idle arm, shoulder 1.79 -> -0.16.) Refuse instead.
        if (!IsHoldSwordSettled(anim, out string layerState))
        {
            _status = "NOT baked: the Item Hold layer is showing '" + layerState + "', not a settled Hold_Sword. "
                + "The arm on screen is not the held stance, so baking would overwrite it with the wrong pose. "
                + "Make sure the sword is selected in the hotbar, let the pose settle, then bake "
                + "(unfreeze the animation first if you froze it before picking the sword up).";
            Debug.LogWarning("[SwordPoseTuner] " + _status);
            return;
        }

        var log = new System.Text.StringBuilder();

        // ---- right arm + right fingers -> muscle curves ----
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(HoldClipPath);
        if (clip == null) { _status = "Hold_Sword_Pose.anim not found."; return; }

        var handler = new HumanPoseHandler(anim.avatar, anim.transform);
        HumanPose pose = new HumanPose();
        handler.GetHumanPose(ref pose);
        handler.Dispose();

        int written = WriteRightArmAndFingerMuscles(clip, pose);
        log.AppendLine($"right arm + finger muscle curves written: {written}");

        // ---- carry -> prefab ----
        GrabbableInventoryItem sword = tuner.FindHeldSword();
        Transform socket = tuner.ResolveGripSocket();
        if (sword == null || socket == null)
        {
            log.AppendLine("carry NOT baked (no held sword, or GripSocket_R missing).");
        }
        else
        {
            Vector3 gripLocalPos = tuner.carryGripLocalPosition;
            Vector3 rotationEuler = tuner.carryRotationEuler;

            GameObject root = PrefabUtility.LoadPrefabContents(SwordPrefabPath);
            try
            {
                Transform prefabGrip = root.transform.Find("GripPoint_R");
                if (prefabGrip != null)
                {
                    prefabGrip.localPosition = gripLocalPos;
                    prefabGrip.localRotation = Quaternion.identity;   // angle stays owned by the euler
                }
                var so = new SerializedObject(root.GetComponent<SwordItem>());
                so.FindProperty("heldRotationOffsetEuler").vector3Value = rotationEuler;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, SwordPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            // keep the live instance consistent so the view does not jump after baking
            Transform liveGrip = sword.transform.Find("GripPoint_R");
            if (liveGrip != null)
            {
                liveGrip.localPosition = gripLocalPos;
                liveGrip.localRotation = Quaternion.identity;
            }
            sword.SetHeldRotationOffsetEulerForTuning(rotationEuler);

            log.AppendLine($"carry grip = {gripLocalPos:F4}   rotation offset = {rotationEuler:F2}");

            // Verify the numbers actually reproduce a seated blade: push them, re-seat through the real
            // runtime method, and confirm the grip point lands on the socket. That is the invariant. The
            // blade's own movement is reported but is NOT an error — it is non-zero whenever the values were
            // typed by hand rather than captured, which is just the edit taking effect.
            Vector3 beforePos = sword.transform.position;
            tuner.PushCarryOverrides();
            sword.ApplyHandSocketHeldPose(socket);

            Vector3 gripWorld = sword.transform.TransformPoint(gripLocalPos);
            float seatErrorMm = (gripWorld - socket.position).magnitude * 1000f;
            float bladeMovedMm = (sword.transform.position - beforePos).magnitude * 1000f;
            log.AppendLine($"seat check: grip is {seatErrorMm:F4} mm off the socket (must be ~0); "
                + $"blade moved {bladeMovedMm:F2} mm on re-seating");
            if (seatErrorMm > 0.5f)
                log.AppendLine("  WARNING: grip is not landing on the socket — check GripPoint_R exists and is unrotated.");
        }

        AssetDatabase.SaveAssets();
        _status = log.ToString();
        Debug.Log("[SwordPoseTuner] " + _status);
    }

    /// <summary>
    /// True when the Item Hold layer is fully in <c>Hold_Sword</c>, i.e. the right arm on screen really is the
    /// held stance this tool edits. Resolves the layer by name — the index has moved before.
    /// </summary>
    static bool IsHoldSwordSettled(Animator anim, out string state)
    {
        state = "unknown";
        int layer = -1;
        for (int i = 0; i < anim.layerCount; i++)
        {
            if (anim.GetLayerName(i) == "Item Hold") { layer = i; break; }
        }

        if (layer < 0)
        {
            state = "no 'Item Hold' layer";
            return false;
        }

        if (anim.IsInTransition(layer))
        {
            state = "mid-transition";
            return false;
        }

        if (anim.GetLayerWeight(layer) < 0.999f)
        {
            state = $"layer weight {anim.GetLayerWeight(layer):F2}";
            return false;
        }

        AnimatorStateInfo info = anim.GetCurrentAnimatorStateInfo(layer);
        if (!info.IsName("Hold_Sword"))
        {
            state = "some other hold state";
            return false;
        }

        state = "Hold_Sword";
        return true;
    }

    /// <summary>
    /// Writes the RIGHT arm and right-hand fingers only. The left arm is deliberately left alone: the sword is
    /// one-handed, the Item Hold layer's PlayerArmsOnly mask is right-arm-only, and capturing the left arm here
    /// would bake whatever frame of locomotion it happened to be in into the held stance.
    /// </summary>
    static int WriteRightArmAndFingerMuscles(AnimationClip clip, HumanPose pose)
    {
        var muscleIndex = new Dictionary<string, int>();
        for (int i = 0; i < HumanTrait.MuscleCount; i++)
            muscleIndex[HumanTrait.MuscleName[i]] = i;

        var targets = new List<string>();
        foreach (string a in new[] { "Shoulder Down-Up", "Shoulder Front-Back", "Arm Down-Up", "Arm Front-Back",
                                     "Arm Twist In-Out", "Forearm Stretch", "Forearm Twist In-Out",
                                     "Hand Down-Up", "Hand In-Out" })
            targets.Add("Right " + a);
        foreach (string f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
        {
            for (int n = 1; n <= 3; n++) targets.Add("Right " + f + " " + n + " Stretched");
            targets.Add("Right " + f + " Spread");
        }

        int written = 0;
        foreach (string m in targets)
        {
            int idx;
            if (!muscleIndex.TryGetValue(m, out idx))
                continue;

            // NOT clamped to [-1,1]. Animation clip playback does not clamp muscle values — only
            // HumanPoseHandler.SetHumanPose does — and the sword stance legitimately runs past the avatar's
            // limits (its shoulder sits at 1.83). Clamping here silently flattens the pose; measured at 145mm
            // when the clip builder did it.
            float v = pose.muscles[idx];
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
    /// reports ("RightHand.Index.1 Stretched" vs "Right Index 1 Stretched"); a curve written under the wrong
    /// one is silently ignored. Arm/shoulder/wrist muscles match in both conventions.
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
}
