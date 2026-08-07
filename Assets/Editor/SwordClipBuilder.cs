using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the sword's two humanoid clips from the authored <c>Sword Swing.anim</c>.
///
/// The authored clip is a GENERIC clip — its curves are bound to the Survivalist rig's transform paths
/// (<c>root/pelvis/spine_01/...</c>). The player's Animator is Humanoid, and a humanoid animator evaluates
/// muscle curves only: generic transform curves aimed at avatar bones are silently ignored, so the authored
/// clip plays as a completely still bind pose. (Verified: sampling it at six times across its length gives a
/// byte-identical rig.) The same trap is documented for <c>Pistol Idle.anim</c>, the flare gun's source pose.
///
/// So both outputs are RETARGETED here rather than referenced directly: the authored transform curves are
/// applied to a real Survivalist instance frame by frame and read back through <see cref="HumanPoseHandler"/>
/// as muscle values. Because the source was authored on this exact rig, the round trip is a re-encoding, not
/// a cross-rig retarget — measured hand error is reported by the builder and is sub-millimetre.
///
///   SwordSwing.anim      full 1.5s swing, all 95 muscles, for the upper-body "Sword Swing" layer.
///   Hold_Sword_Pose.anim single-frame guard stance (frame <see cref="HoldPoseSourceFrame"/> of the swing),
///                        arms + right fingers only, for the Item Hold layer at HoldPose 7.
///
/// <c>Sword Swing.anim</c> itself is never referenced by the animator — it is kept purely as the authoring
/// source, the same arrangement as <c>FlareGun_Reload.anim</c>. Re-run this after re-authoring it.
/// </summary>
public static class SwordClipBuilder
{
    const string SourceClipPath = "Assets/Prefabs/Characters/Animations/Sword Swing.anim";
    const string SwingOutputPath = "Assets/Prefabs/Characters/Animations/SwordSwing.anim";
    const string HoldPoseOutputPath = "Assets/Prefabs/Characters/Animations/Hold_Sword_Pose.anim";
    const string RigPrefabPath = "Assets/Prefabs/Characters/Player_Survivalist.prefab";
    /// <summary>Reference humanoid clip; its binding set is the ground truth for muscle binding names.</summary>
    const string BindingReferenceClipPath = "Assets/Prefabs/Characters/Animations/Right Hook.anim";

    /// <summary>Resample rate. Above the source's 30fps because muscle space is a non-linear function of the
    /// bone rotations, so the midpoints of the authored tangents are worth capturing rather than re-deriving.</summary>
    const int OutputFrameRate = 60;

    /// <summary>
    /// Frame of the source swing used as the held-idle stance. Frame 0 is the wind-up's start: blade out in
    /// front, elbow bent, fingers already closed on the grip — the pose the swing both leaves and returns to
    /// (the hand is within 4cm of it again at frame 44), so entering and leaving the swing does not pop.
    /// </summary>
    const int HoldPoseSourceFrame = 0;

    /// <summary>
    /// Rebuilds both clips from the authored swing. NOTE: this overwrites <c>Hold_Sword_Pose.anim</c>, so it
    /// discards any hold pose tuned with <c>Tools/Sword Pose Tuner</c>. Use
    /// <see cref="RebuildSwingOnly"/> when only the swing changed.
    /// </summary>
    [MenuItem("Tools/Sword/Rebuild Clips (swing + hold pose)")]
    public static void RebuildAll()
    {
        Rebuild(writeSwing: true, writeHoldPose: true);
    }

    /// <summary>Rebuilds only the swing, leaving a tuned <c>Hold_Sword_Pose.anim</c> alone.</summary>
    [MenuItem("Tools/Sword/Rebuild Swing Only")]
    public static void RebuildSwingOnly()
    {
        Rebuild(writeSwing: true, writeHoldPose: false);
    }

    /// <summary>Re-derives the held stance from frame 0 of the swing — the recovery path for a bad tune.</summary>
    public static void RebuildHoldPoseOnly()
    {
        Rebuild(writeSwing: false, writeHoldPose: true);
    }

    static void Rebuild(bool writeSwing, bool writeHoldPose)
    {
        AnimationClip source = AssetDatabase.LoadAssetAtPath<AnimationClip>(SourceClipPath);
        if (source == null)
        {
            Debug.LogError($"[SwordClipBuilder] Source clip not found at {SourceClipPath}.");
            return;
        }

        HashSet<string> validBindings = ReadValidMuscleBindings();
        if (validBindings == null)
            return;

        GameObject rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefabPath);
        if (rigPrefab == null)
        {
            Debug.LogError($"[SwordClipBuilder] Rig prefab not found at {RigPrefabPath}.");
            return;
        }

        GameObject rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
        HumanPoseHandler poseHandler = null;
        try
        {
            // HumanPoseHandler works in the root's space; away from the origin the captured pose lands on a
            // different skeleton entirely (a documented trap from the flare gun pose bake).
            rig.transform.position = Vector3.zero;
            rig.transform.rotation = Quaternion.identity;
            rig.transform.localScale = Vector3.one;

            Animator animator = rig.GetComponentInChildren<Animator>();
            if (animator == null || !animator.isHuman)
            {
                Debug.LogError("[SwordClipBuilder] Rig prefab has no humanoid Animator.");
                return;
            }

            List<TransformCurve> curves = MapSourceCurves(source, rig.transform);
            if (curves.Count == 0)
            {
                Debug.LogError("[SwordClipBuilder] No source curves resolved against the rig — bone names changed?");
                return;
            }

            poseHandler = new HumanPoseHandler(animator.avatar, rig.transform);

            int frameCount = Mathf.RoundToInt(source.length * OutputFrameRate);
            float frameStep = 1f / OutputFrameRate;

            // muscles[muscleIndex][frame]
            float[][] muscles = new float[HumanTrait.MuscleCount][];
            for (int m = 0; m < muscles.Length; m++)
                muscles[m] = new float[frameCount + 1];

            HumanPose pose = new HumanPose();
            int outOfRange = 0;
            float widestMuscle = 0f;
            string widestMuscleName = string.Empty;
            for (int f = 0; f <= frameCount; f++)
            {
                ApplySourcePose(curves, f * frameStep);
                poseHandler.GetHumanPose(ref pose);
                for (int m = 0; m < HumanTrait.MuscleCount; m++)
                {
                    float value = pose.muscles[m];

                    // NOT clamped to [-1,1], deliberately. The authored swing pushes 15 muscles past the
                    // avatar's configured limits (the right shoulder reaches 1.83, the thumb 2.43), and
                    // clamping them moved the hand by up to 145mm — it visibly flattens the wind-up.
                    // Clamping is required when round-tripping through HumanPoseHandler.SetHumanPose, which
                    // does clamp, but ANIMATION CLIP PLAYBACK DOES NOT: sampling an unclamped clip back onto
                    // the rig reproduces the authored hand path to 0.02mm. Measured, both ways.
                    muscles[m][f] = value;

                    if (Mathf.Abs(value) > 1f)
                    {
                        outOfRange++;
                        if (Mathf.Abs(value) > Mathf.Abs(widestMuscle))
                        {
                            widestMuscle = value;
                            widestMuscleName = HumanTrait.MuscleName[m];
                        }
                    }
                }
            }

            if (outOfRange > 0)
            {
                Debug.Log($"[SwordClipBuilder] {outOfRange} muscle samples exceed the avatar's limits "
                    + $"(widest {widestMuscleName} = {widestMuscle:F2}); kept as authored — see the note in this file.");
            }

            if (writeSwing)
            {
                AnimationClip swing = BuildSwingClip(muscles, frameCount, frameStep, validBindings);
                WriteClip(swing, SwingOutputPath);
            }

            if (writeHoldPose)
            {
                AnimationClip hold = BuildHoldPoseClip(muscles, validBindings);
                WriteClip(hold, HoldPoseOutputPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[SwordClipBuilder] Built{(writeSwing ? $" {SwingOutputPath} ({frameCount + 1} frames @ {OutputFrameRate}fps)" : "")}"
                + $"{(writeHoldPose ? $" {HoldPoseOutputPath} (from frame {HoldPoseSourceFrame})" : "")}.");

            if (writeSwing)
                ReportRetargetError(source, curves, rig, animator);
        }
        finally
        {
            poseHandler?.Dispose();
            if (rig != null)
                Object.DestroyImmediate(rig);
        }
    }

    struct TransformCurve
    {
        public Transform Target;
        public string Property;
        public AnimationCurve Curve;
    }

    static List<TransformCurve> MapSourceCurves(AnimationClip source, Transform root)
    {
        List<TransformCurve> result = new List<TransformCurve>();
        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(source))
        {
            if (!binding.propertyName.StartsWith("m_LocalPosition") && !binding.propertyName.StartsWith("m_LocalRotation"))
                continue;   // scale curves are constant on this rig and would only fight the avatar

            Transform target = string.IsNullOrEmpty(binding.path) ? root : root.Find(binding.path);
            if (target == null)
                continue;

            AnimationCurve curve = AnimationUtility.GetEditorCurve(source, binding);
            if (curve == null || curve.length == 0)
                continue;

            result.Add(new TransformCurve { Target = target, Property = binding.propertyName, Curve = curve });
        }

        return result;
    }

    /// <summary>
    /// Writes the authored pose onto the rig at <paramref name="time"/>. Components are gathered per
    /// transform before assignment so a quaternion is never written one axis at a time (each partial write
    /// would be re-normalised against the previous frame's value).
    /// </summary>
    static void ApplySourcePose(List<TransformCurve> curves, float time)
    {
        Dictionary<Transform, Vector3> positions = new Dictionary<Transform, Vector3>();
        Dictionary<Transform, Vector4> rotations = new Dictionary<Transform, Vector4>();

        foreach (TransformCurve entry in curves)
        {
            float value = entry.Curve.Evaluate(time);
            if (entry.Property.StartsWith("m_LocalPosition"))
            {
                if (!positions.TryGetValue(entry.Target, out Vector3 p))
                    p = entry.Target.localPosition;
                if (entry.Property.EndsWith(".x")) p.x = value;
                else if (entry.Property.EndsWith(".y")) p.y = value;
                else p.z = value;
                positions[entry.Target] = p;
            }
            else
            {
                if (!rotations.TryGetValue(entry.Target, out Vector4 r))
                {
                    Quaternion current = entry.Target.localRotation;
                    r = new Vector4(current.x, current.y, current.z, current.w);
                }
                if (entry.Property.EndsWith(".x")) r.x = value;
                else if (entry.Property.EndsWith(".y")) r.y = value;
                else if (entry.Property.EndsWith(".z")) r.z = value;
                else r.w = value;
                rotations[entry.Target] = r;
            }
        }

        foreach (KeyValuePair<Transform, Vector3> kv in positions)
            kv.Key.localPosition = kv.Value;

        foreach (KeyValuePair<Transform, Vector4> kv in rotations)
        {
            Vector4 r = kv.Value;
            float magnitude = Mathf.Sqrt(r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w);
            if (magnitude > 1e-6f)
                kv.Key.localRotation = new Quaternion(r.x / magnitude, r.y / magnitude, r.z / magnitude, r.w / magnitude);
        }
    }

    static AnimationClip BuildSwingClip(float[][] muscles, int frameCount, float frameStep, HashSet<string> validBindings)
    {
        AnimationClip clip = new AnimationClip { frameRate = OutputFrameRate };

        for (int m = 0; m < HumanTrait.MuscleCount; m++)
        {
            string bindingName = MuscleBindingName(HumanTrait.MuscleName[m]);
            if (!validBindings.Contains(bindingName))
            {
                Debug.LogError($"[SwordClipBuilder] '{bindingName}' is not a known muscle binding — skipped.");
                continue;
            }

            Keyframe[] keys = new Keyframe[frameCount + 1];
            for (int f = 0; f <= frameCount; f++)
                keys[f] = new Keyframe(f * frameStep, muscles[m][f]);

            AnimationCurve curve = new AnimationCurve(keys);
            // Sampled densely enough that linear segments read as the authored motion, and unlike auto
            // tangents they cannot overshoot the muscle range across the strike's fast frames.
            for (int f = 0; f <= frameCount; f++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, f, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, f, AnimationUtility.TangentMode.Linear);
            }

            AnimationUtility.SetEditorCurve(clip, MuscleBinding(bindingName), curve);
        }

        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.startTime = 0f;
        settings.stopTime = frameCount * frameStep;
        settings.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        // Whoosh fires from the clip, not from a timer on the attacker. Baked here so it survives a rebuild,
        // and because an animation event is expressed in CLIP time it stays locked to the blade no matter what
        // playback speed the animator state runs at — and it fires identically on every peer, which a
        // server-driven sound cannot (that one arrives a round trip after the swing has already started).
        AnimationUtility.SetAnimationEvents(clip, new[]
        {
            new AnimationEvent
            {
                time = SwordItem.SwingWhooshSeconds,
                functionName = "OnSwordSwingWhoosh"
            }
        });

        return clip;
    }

    /// <summary>
    /// One-frame held-idle stance. Keys the same 38 muscles as the other hold poses: both arms (the Item Hold
    /// layer's mask only lets the right one through, but an unkeyed muscle on an active masked layer snaps to
    /// its default, so keying both keeps the clip safe to reuse on a wider mask) plus the right-hand fingers.
    /// </summary>
    static AnimationClip BuildHoldPoseClip(float[][] muscles, HashSet<string> validBindings)
    {
        AnimationClip clip = new AnimationClip { frameRate = OutputFrameRate };

        for (int m = 0; m < HumanTrait.MuscleCount; m++)
        {
            string muscleName = HumanTrait.MuscleName[m];
            bool isArm = muscleName.Contains("Shoulder") || muscleName.Contains("Arm ")
                || muscleName.Contains("Forearm") || muscleName.Contains("Hand ");
            bool isRightFinger = muscleName.StartsWith("Right ") && IsFingerMuscle(muscleName);
            if (!isArm && !isRightFinger)
                continue;

            string bindingName = MuscleBindingName(muscleName);
            if (!validBindings.Contains(bindingName))
            {
                Debug.LogError($"[SwordClipBuilder] '{bindingName}' is not a known muscle binding — skipped.");
                continue;
            }

            float value = muscles[m][HoldPoseSourceFrame];
            AnimationUtility.SetEditorCurve(clip, MuscleBinding(bindingName), Constant(value));
        }

        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.startTime = 0f;
        settings.stopTime = 1f;
        settings.loopTime = true;
        AnimationUtility.SetAnimationClipSettings(clip, settings);
        return clip;
    }

    static bool IsFingerMuscle(string muscleName)
    {
        return muscleName.Contains("Thumb") || muscleName.Contains("Index") || muscleName.Contains("Middle")
            || muscleName.Contains("Ring") || muscleName.Contains("Little");
    }

    /// <summary>
    /// Muscle name -> animation-clip binding name. They agree everywhere except the fingers, where
    /// <see cref="HumanTrait.MuscleName"/> says "Right Little 1 Stretched" but the clip binds
    /// "RightHand.Little.1 Stretched"; writing the HumanTrait spelling is silently ignored by the animator.
    /// </summary>
    static string MuscleBindingName(string muscleName)
    {
        string side = muscleName.StartsWith("Left ") ? "Left" : muscleName.StartsWith("Right ") ? "Right" : null;
        if (side == null || !IsFingerMuscle(muscleName))
            return muscleName;

        // "<Side> <Digit> <1|2|3> Stretched"  or  "<Side> <Digit> Spread"
        string[] parts = muscleName.Split(' ');
        if (parts.Length == 4)
            return $"{side}Hand.{parts[1]}.{parts[2]} {parts[3]}";
        if (parts.Length == 3)
            return $"{side}Hand.{parts[1]}.{parts[2]}";
        return muscleName;
    }

    static EditorCurveBinding MuscleBinding(string bindingName)
    {
        return EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), bindingName);
    }

    static AnimationCurve Constant(float value)
    {
        AnimationCurve curve = AnimationCurve.Linear(0f, value, 1f, value);
        for (int i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }

        return curve;
    }

    /// <summary>The binding names an existing, engine-authored humanoid clip uses — the only reliable spelling source.</summary>
    static HashSet<string> ReadValidMuscleBindings()
    {
        AnimationClip reference = AssetDatabase.LoadAssetAtPath<AnimationClip>(BindingReferenceClipPath);
        if (reference == null || !reference.isHumanMotion)
        {
            Debug.LogError($"[SwordClipBuilder] Humanoid reference clip missing at {BindingReferenceClipPath}.");
            return null;
        }

        HashSet<string> names = new HashSet<string>();
        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(reference))
            names.Add(binding.propertyName);
        return names;
    }

    static void WriteClip(AnimationClip built, string path)
    {
        built.name = System.IO.Path.GetFileNameWithoutExtension(path);

        AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (existing != null)
        {
            // Overwrite in place so the animator controller's references survive a rebuild.
            EditorUtility.CopySerialized(built, existing);
            EditorUtility.SetDirty(existing);
        }
        else
        {
            AssetDatabase.CreateAsset(built, path);
        }
    }

    /// <summary>
    /// Plays the rebuilt humanoid swing back onto the rig and compares the right hand (in chest space, so the
    /// comparison is independent of root placement) against the authored transform pose at the same times.
    /// This is the check that the muscle re-encoding actually reproduces the animation.
    /// </summary>
    static void ReportRetargetError(AnimationClip source, List<TransformCurve> curves, GameObject rig, Animator animator)
    {
        AnimationClip built = AssetDatabase.LoadAssetAtPath<AnimationClip>(SwingOutputPath);
        if (built == null)
            return;

        Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (chest == null || hand == null)
            return;

        float worst = 0f;
        float worstAt = 0f;
        int samples = Mathf.RoundToInt(source.length * OutputFrameRate);
        for (int f = 0; f <= samples; f++)
        {
            float t = f / (float)OutputFrameRate;

            ApplySourcePose(curves, t);
            Vector3 expected = chest.InverseTransformPoint(hand.position);

            built.SampleAnimation(rig, t);
            Vector3 actual = chest.InverseTransformPoint(hand.position);

            float error = (actual - expected).magnitude;
            if (error > worst)
            {
                worst = error;
                worstAt = t;
            }
        }

        string message = $"[SwordClipBuilder] Retarget check: worst right-hand error {worst * 1000f:F2} mm at t={worstAt:F3}s.";
        if (worst > 0.01f)
            Debug.LogWarning(message + " Over 10mm — inspect the rebuilt clip.");
        else
            Debug.Log(message);
    }
}
