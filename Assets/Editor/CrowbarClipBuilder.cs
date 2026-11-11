using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds <c>CrowbarSwing.anim</c> from the Mixamo source <c>Crowbar Swing.fbx</c> ("Standing Melee Attack
/// Downward", exported on the stock Y Bot rig and imported as Humanoid, so its curves are already muscle
/// curves — no retarget needed, unlike the sword's generic source).
///
/// What the build changes relative to the source:
///   • Window — only <see cref="CrowbarItem.SwingSourceStartSeconds"/>..<see cref="CrowbarItem.SwingSourceEndSeconds"/>
///     is kept. The source opens and closes with the bar hanging at the hip, out of the first-person view; the
///     held stance is the sword's guard (HoldPose 7), so the swing starts with the arm already rising into the
///     wind-up and ends on the recovery as the hand passes back across the front of the body.
///   • Muscles only — the root (RootT/RootQ) and IK-goal curves are dropped, exactly like SwordSwing.anim. The
///     clip plays on the masked "Sword Swing" layer over live locomotion; the source's whole-body turn into the
///     strike would otherwise swing the arm across a camera that does not turn with it.
///   • <see cref="TorsoMuscleScale"/> softens the spine/chest twist and bend for the same reason.
///   • The whoosh is baked as an animation event at <see cref="CrowbarItem.SwingWhooshSeconds"/>.
///
/// Re-run (menu <b>Tools/Crowbar/Rebuild Swing Clip</b>) after changing the window, the torso scale or the source.
/// </summary>
public static class CrowbarClipBuilder
{
    const string SourceFbxPath = "Assets/Prefabs/Characters/Animations/Crowbar Swing.fbx";
    const string SourceClipName = "CrowbarSwing_Source";
    const string SwingOutputPath = "Assets/Prefabs/Characters/Animations/CrowbarSwing.anim";
    /// <summary>Reference humanoid clip; its binding set is the ground truth for muscle binding names.</summary>
    const string BindingReferenceClipPath = "Assets/Prefabs/Characters/Animations/Right Hook.anim";

    /// <summary>Resample rate — the same 60fps as SwordSwing.anim, over a 30fps source.</summary>
    const int OutputFrameRate = 60;

    /// <summary>
    /// Multiplier on the spine, chest and upper-chest muscles. The source is a full-body axe chop that coils the
    /// torso a long way round; at 1 the shoulder carries the arm far enough sideways in the first-person view to
    /// lose the bar for most of the wind-up.
    /// </summary>
    const float TorsoMuscleScale = 0.6f;

    [MenuItem("Tools/Crowbar/Rebuild Swing Clip")]
    public static void Rebuild()
    {
        AnimationClip source = LoadSourceClip();
        if (source == null)
        {
            Debug.LogError($"[CrowbarClipBuilder] Source clip '{SourceClipName}' not found in {SourceFbxPath}.");
            return;
        }

        HashSet<string> validBindings = ReadValidMuscleBindings();
        if (validBindings == null)
            return;

        AnimationClip built = BuildSwingClip(source, validBindings, out int curveCount);
        WriteClip(built, SwingOutputPath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[CrowbarClipBuilder] Wrote {SwingOutputPath}: {curveCount} muscle curves, "
            + $"{CrowbarItem.SwingClipSeconds:F3}s, source window {CrowbarItem.SwingSourceStartSeconds:F3}-"
            + $"{CrowbarItem.SwingSourceEndSeconds:F3}s, torso x{TorsoMuscleScale:F2}.");
    }

    static AnimationClip LoadSourceClip()
    {
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(SourceFbxPath))
        {
            if (asset is AnimationClip clip && clip.name == SourceClipName)
                return clip;
        }

        return null;
    }

    static AnimationClip BuildSwingClip(AnimationClip source, HashSet<string> validBindings, out int curveCount)
    {
        AnimationClip clip = new AnimationClip { frameRate = OutputFrameRate };
        float start = CrowbarItem.SwingSourceStartSeconds;
        float length = CrowbarItem.SwingClipSeconds;
        int frameCount = Mathf.CeilToInt(length * OutputFrameRate - 0.001f);
        float frameStep = length / frameCount;

        curveCount = 0;
        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(source))
        {
            // Muscles only: RootT/RootQ and the IK goals (LeftHandT, RightFootQ, ...) are not in the reference
            // clip's binding set, so this one test drops both.
            if (!validBindings.Contains(binding.propertyName))
                continue;

            AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(source, binding);
            if (sourceCurve == null)
                continue;

            float scale = IsTorsoMuscle(binding.propertyName) ? TorsoMuscleScale : 1f;
            Keyframe[] keys = new Keyframe[frameCount + 1];
            for (int f = 0; f <= frameCount; f++)
                keys[f] = new Keyframe(f * frameStep, sourceCurve.Evaluate(start + f * frameStep) * scale);

            AnimationCurve curve = new AnimationCurve(keys);
            // Linear, like SwordSwing.anim: dense enough to read as the source motion, and unlike auto tangents
            // it cannot overshoot a muscle's range across the strike's fast frames.
            for (int f = 0; f <= frameCount; f++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, f, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, f, AnimationUtility.TangentMode.Linear);
            }

            AnimationUtility.SetEditorCurve(clip, binding, curve);
            curveCount++;
        }

        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
        settings.startTime = 0f;
        settings.stopTime = frameCount * frameStep;
        settings.loopTime = false;
        AnimationUtility.SetAnimationClipSettings(clip, settings);

        // Clip-time event: fires on every peer off its own animation, and follows the state's playback speed.
        AnimationUtility.SetAnimationEvents(clip, new[]
        {
            new AnimationEvent
            {
                time = CrowbarItem.SwingWhooshSeconds,
                functionName = nameof(PlayerController.OnCrowbarSwingWhoosh)
            }
        });

        return clip;
    }

    static bool IsTorsoMuscle(string bindingName)
    {
        return bindingName.StartsWith("Spine ") || bindingName.StartsWith("Chest ")
            || bindingName.StartsWith("UpperChest ");
    }

    /// <summary>The binding names an existing, engine-authored humanoid clip uses — the only reliable spelling source.</summary>
    static HashSet<string> ReadValidMuscleBindings()
    {
        AnimationClip reference = AssetDatabase.LoadAssetAtPath<AnimationClip>(BindingReferenceClipPath);
        if (reference == null || !reference.isHumanMotion)
        {
            Debug.LogError($"[CrowbarClipBuilder] Humanoid reference clip missing at {BindingReferenceClipPath}.");
            return null;
        }

        HashSet<string> names = new HashSet<string>();
        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(reference))
        {
            string name = binding.propertyName;
            if (name.StartsWith("Root") || name.EndsWith("T.x") || name.EndsWith("T.y") || name.EndsWith("T.z")
                || name.EndsWith("Q.x") || name.EndsWith("Q.y") || name.EndsWith("Q.z") || name.EndsWith("Q.w"))
            {
                continue;   // root and IK-goal channels, not muscles
            }

            names.Add(name);
        }

        return names;
    }

    static void WriteClip(AnimationClip built, string path)
    {
        built.name = System.IO.Path.GetFileNameWithoutExtension(path);

        AnimationClip existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
        if (existing != null)
        {
            // Overwrite in place so the animator controller's reference survives a rebuild.
            EditorUtility.CopySerialized(built, existing);
            EditorUtility.SetDirty(existing);
        }
        else
        {
            AssetDatabase.CreateAsset(built, path);
        }
    }
}
