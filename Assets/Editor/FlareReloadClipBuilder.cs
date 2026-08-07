using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the per-round-count flare reload clips from the single authored <c>FlareGun_Reload.anim</c>.
///
/// A reload now loads the whole gun in one press, so the off hand has to make one fetch-and-insert trip per
/// round. The authored clip already contains exactly one such trip, and its arm pose at
/// <see cref="CycleStartFrame"/> matches the pose at <see cref="CycleEndFrame"/> almost exactly (measured
/// pose distance 0.0068 over the 32 moving muscle/goal curves), so the N-round clips are made by replaying
/// that middle span N times between the same lead-in and settle:
///
///   [0 .. CycleStart)  lead-in — hand still at rest while the barrel breaks open
///   [CycleStart .. CycleEnd)  x N — hand drops to the pouch, brings a round up, inserts, returns
///   [CycleEnd .. end]  settle — hand comes to rest while the barrel snaps shut
///
/// Splicing the authored motion (rather than re-animating) keeps the hand path exactly as tuned. The left
/// hand's finger muscles are flat-constant in the source — an open palm with the round floating beside it —
/// so they are replaced here with the thumb/index pinch from <c>Hold_Pinch_Pose.anim</c> (the grip the key
/// and carnival ticket use), mirrored onto the left hand.
///
/// The gun's own reload visual is scripted in <see cref="FlareGunItem"/> against the SAME phase constants,
/// so the two stay in step; if the source clip is re-authored, update the frame constants below and re-run.
/// Deliberately does not touch <c>Hold_FlareGun_Pose.anim</c>.
/// </summary>
public static class FlareReloadClipBuilder
{
    const string SourceClipPath = "Assets/Prefabs/Characters/Animations/FlareGun_Reload.anim";
    const string PinchClipPath = "Assets/Prefabs/Characters/Animations/Hold_Pinch_Pose.anim";
    const string OutputFolder = "Assets/Prefabs/Characters/Animations";
    const string OutputPrefix = "FlareGun_Reload_";

    // Phase boundaries in source frames at 60 fps. Keep in sync with FlareGunItem's reload timeline.
    public const int SourceFrameRate = 60;
    public const int CycleStartFrame = 16;   // 0.2667s — hand starts leaving rest
    public const int CycleEndFrame = 92;     // 1.5333s — hand back at rest
    public const int SourceEndFrame = 102;   // 1.7000s

    public const int LeadInFrames = CycleStartFrame;
    public const int CycleFrames = CycleEndFrame - CycleStartFrame;
    public const int TailFrames = SourceEndFrame - CycleEndFrame;

    [MenuItem("Tools/Flare Gun/Rebuild Reload Clips")]
    public static void RebuildAll()
    {
        AnimationClip source = AssetDatabase.LoadAssetAtPath<AnimationClip>(SourceClipPath);
        if (source == null)
        {
            Debug.LogError($"[FlareReloadClipBuilder] Source clip not found at {SourceClipPath}.");
            return;
        }

        Dictionary<string, float> pinch = ReadLeftHandPinchMuscles();
        if (pinch == null)
            return;

        for (int rounds = 1; rounds <= FlareGunItem.MaxRounds; rounds++)
            BuildClip(source, pinch, rounds);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[FlareReloadClipBuilder] Rebuilt {FlareGunItem.MaxRounds} reload clips from {SourceClipPath}.");
    }

    /// <summary>
    /// The pinch grip's finger muscles, keyed by their LEFT-hand binding name. Humanoid muscle values are
    /// side-symmetric (the same number curls the same way on either hand), so the authored right-hand pinch
    /// transfers to the left by renaming the binding — no sign flips.
    /// </summary>
    static Dictionary<string, float> ReadLeftHandPinchMuscles()
    {
        AnimationClip pinchClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(PinchClipPath);
        if (pinchClip == null)
        {
            Debug.LogError($"[FlareReloadClipBuilder] Pinch pose clip not found at {PinchClipPath}.");
            return null;
        }

        Dictionary<string, float> result = new Dictionary<string, float>();
        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(pinchClip))
        {
            if (!binding.propertyName.StartsWith("RightHand."))
                continue;

            AnimationCurve curve = AnimationUtility.GetEditorCurve(pinchClip, binding);
            if (curve == null || curve.length == 0)
                continue;

            result["LeftHand." + binding.propertyName.Substring("RightHand.".Length)] = curve.Evaluate(0f);
        }

        if (result.Count == 0)
        {
            Debug.LogError($"[FlareReloadClipBuilder] No RightHand finger curves found in {PinchClipPath}.");
            return null;
        }

        return result;
    }

    static void BuildClip(AnimationClip source, Dictionary<string, float> leftHandPinch, int rounds)
    {
        int totalFrames = LeadInFrames + CycleFrames * rounds + TailFrames;
        float frameStep = 1f / SourceFrameRate;

        AnimationClip built = new AnimationClip { frameRate = source.frameRate };

        foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(source))
        {
            AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(source, binding);
            if (sourceCurve == null || sourceCurve.length == 0)
                continue;

            // Off hand: swap the source's open-palm fingers for the pinch, held for the whole reload.
            if (leftHandPinch.TryGetValue(binding.propertyName, out float pinchValue))
            {
                AnimationUtility.SetEditorCurve(built, binding, Constant(pinchValue, totalFrames * frameStep));
                continue;
            }

            // Curves that never move (legs, spine, the untouched right-hand grip) are copied flat.
            if (IsFlat(sourceCurve))
            {
                AnimationUtility.SetEditorCurve(built, binding, Constant(sourceCurve.Evaluate(0f), totalFrames * frameStep));
                continue;
            }

            Keyframe[] keys = new Keyframe[totalFrames + 1];
            for (int f = 0; f <= totalFrames; f++)
                keys[f] = new Keyframe(f * frameStep, sourceCurve.Evaluate(SourceTimeForFrame(f, rounds) * frameStep));

            AnimationCurve resampled = new AnimationCurve(keys);
            // Sampled at the source's own frame rate, so linear segments reproduce it exactly and — unlike
            // auto tangents — cannot overshoot across the loop seam or on the quaternion component curves.
            for (int f = 0; f <= totalFrames; f++)
            {
                AnimationUtility.SetKeyLeftTangentMode(resampled, f, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(resampled, f, AnimationUtility.TangentMode.Linear);
            }

            AnimationUtility.SetEditorCurve(built, binding, resampled);
        }

        AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(source);
        settings.startTime = 0f;
        settings.stopTime = totalFrames * frameStep;
        AnimationUtility.SetAnimationClipSettings(built, settings);

        string path = $"{OutputFolder}/{OutputPrefix}{rounds}.anim";
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
    /// Output frame -> source frame: lead-in passes through, the middle repeats the cycle span
    /// <paramref name="rounds"/> times, then the settle passes through.
    /// </summary>
    static int SourceTimeForFrame(int outputFrame, int rounds)
    {
        if (outputFrame < LeadInFrames)
            return outputFrame;

        int afterLeadIn = outputFrame - LeadInFrames;
        int cycleTotal = CycleFrames * rounds;
        if (afterLeadIn >= cycleTotal)
            return CycleEndFrame + (afterLeadIn - cycleTotal);

        return CycleStartFrame + (afterLeadIn % CycleFrames);
    }

    static bool IsFlat(AnimationCurve curve)
    {
        float first = curve.keys[0].value;
        for (int i = 1; i < curve.length; i++)
        {
            if (Mathf.Abs(curve.keys[i].value - first) > 0.0001f)
                return false;
        }

        return true;
    }

    static AnimationCurve Constant(float value, float length)
    {
        AnimationCurve curve = AnimationCurve.Linear(0f, value, length, value);
        for (int i = 0; i < curve.length; i++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
        }

        return curve;
    }
}
