#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Builds Resources/GameAudio/MainMixer.mixer: Master → Music, Sfx; exposes MasterVolume, MusicVolume, SfxVolume.
/// </summary>
public static class GameAudioMixerSetup
{
    const string MixerAssetPath = "Assets/Resources/GameAudio/MainMixer.mixer";

    [MenuItem("Maze Escape/Audio/Create Game Audio Mixer")]
    public static void CreateGameAudioMixer()
    {
        EnsureFolder("Assets", "Resources");
        EnsureFolder("Assets/Resources", "GameAudio");

        if (File.Exists(Path.Combine(Application.dataPath, "Resources/GameAudio/MainMixer.mixer")))
        {
            if (!EditorUtility.DisplayDialog(
                    "Game Audio Mixer",
                    "MainMixer.mixer already exists. Regenerate it? (This resets the mixer.)",
                    "Regenerate",
                    "Cancel"))
                return;

            AssetDatabase.DeleteAsset(MixerAssetPath);
            AssetDatabase.Refresh();
        }

        var editorAsm = typeof(Editor).Assembly;
        var controllerType = editorAsm.GetType("UnityEditor.Audio.AudioMixerController");
        var groupType = editorAsm.GetType("UnityEditor.Audio.AudioMixerGroupController");
        var pathType = editorAsm.GetType("UnityEditor.Audio.AudioGroupParameterPath");

        if (controllerType == null || groupType == null || pathType == null)
        {
            Debug.LogError("GameAudioMixerSetup: Could not resolve UnityEditor.Audio types. Unity version mismatch?");
            return;
        }

        var createMixer = controllerType.GetMethod("CreateMixerControllerAtPath", BindingFlags.Public | BindingFlags.Static);
        if (createMixer == null)
        {
            Debug.LogError("GameAudioMixerSetup: CreateMixerControllerAtPath not found.");
            return;
        }

        var controller = createMixer.Invoke(null, new object[] { MixerAssetPath });
        if (controller == null)
        {
            Debug.LogError("GameAudioMixerSetup: Failed to create mixer.");
            return;
        }

        var masterProp = controllerType.GetProperty("masterGroup", BindingFlags.Public | BindingFlags.Instance);
        var master = masterProp?.GetValue(controller);
        if (master == null)
        {
            Debug.LogError("GameAudioMixerSetup: masterGroup is null.");
            return;
        }

        var createGroup = controllerType.GetMethod("CreateNewGroup", BindingFlags.Public | BindingFlags.Instance);
        var addChild = controllerType.GetMethod("AddChildToParent", BindingFlags.Public | BindingFlags.Instance);
        var addExposed = controllerType.GetMethod("AddExposedParameter", BindingFlags.Public | BindingFlags.Instance);
        var getVolGuid = groupType.GetMethod("GetGUIDForVolume", BindingFlags.Public | BindingFlags.Instance);

        if (createGroup == null || addChild == null || addExposed == null || getVolGuid == null)
        {
            Debug.LogError("GameAudioMixerSetup: Missing expected public APIs on AudioMixerController / Group.");
            return;
        }

        var music = createGroup.Invoke(controller, new object[] { "Music", false });
        var sfx = createGroup.Invoke(controller, new object[] { "Sfx", false });
        var ui = createGroup.Invoke(controller, new object[] { "Ui", false });
        addChild.Invoke(controller, new object[] { music, master });
        addChild.Invoke(controller, new object[] { sfx, master });
        addChild.Invoke(controller, new object[] { ui, master });

        void ExposeGroupVolume(object groupObj)
        {
            var g = getVolGuid.Invoke(groupObj, null);
            var paramPath = Activator.CreateInstance(pathType, groupObj, g);
            addExposed.Invoke(controller, new object[] { paramPath });
        }

        ExposeGroupVolume(master);
        ExposeGroupVolume(music);
        ExposeGroupVolume(sfx);
        ExposeGroupVolume(ui);

        var so = new SerializedObject((UnityEngine.Object)controller);
        var exposed = so.FindProperty("m_ExposedParameters");
        if (exposed == null || !exposed.isArray)
        {
            Debug.LogWarning("GameAudioMixerSetup: Could not find m_ExposedParameters; rename exposed params in the mixer to MasterVolume, MusicVolume, SfxVolume, UiVolume.");
        }
        else
        {
            string[] names = { "MasterVolume", "MusicVolume", "SfxVolume", "UiVolume" };
            int n = Mathf.Min(names.Length, exposed.arraySize);
            for (int i = 0; i < n; i++)
            {
                var nameProp = exposed.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (nameProp != null)
                    nameProp.stringValue = names[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorUtility.SetDirty((UnityEngine.Object)controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorGUIUtility.PingObject((UnityEngine.Object)controller);
        Debug.Log($"Game audio mixer created: {MixerAssetPath}. GameAudioManager loads it from Resources.");
    }

    /// <summary>
    /// Adds a "Ui" child group under Master and exposes its volume as "UiVolume", without touching the
    /// rest of the mixer. Safe to re-run — no-ops if the group/param already exist.
    /// </summary>
    [MenuItem("Maze Escape/Audio/Add UI Mixer Group")]
    public static void AddUiMixerGroup()
    {
        var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerAssetPath);
        if (mixer == null)
        {
            Debug.LogError($"GameAudioMixerSetup: No mixer at {MixerAssetPath}. Run 'Create Game Audio Mixer' first.");
            return;
        }

        if (mixer.FindMatchingGroups("Ui").Length > 0)
        {
            Debug.Log("GameAudioMixerSetup: 'Ui' group already exists — nothing to do.");
            return;
        }

        var editorAsm = typeof(Editor).Assembly;
        var controllerType = editorAsm.GetType("UnityEditor.Audio.AudioMixerController");
        var groupType = editorAsm.GetType("UnityEditor.Audio.AudioMixerGroupController");
        var pathType = editorAsm.GetType("UnityEditor.Audio.AudioGroupParameterPath");

        if (controllerType == null || groupType == null || pathType == null)
        {
            Debug.LogError("GameAudioMixerSetup: Could not resolve UnityEditor.Audio types. Unity version mismatch?");
            return;
        }

        object controller = mixer;
        var masterProp = controllerType.GetProperty("masterGroup", BindingFlags.Public | BindingFlags.Instance);
        var master = masterProp?.GetValue(controller);
        if (master == null)
        {
            Debug.LogError("GameAudioMixerSetup: masterGroup is null.");
            return;
        }

        var createGroup = controllerType.GetMethod("CreateNewGroup", BindingFlags.Public | BindingFlags.Instance);
        var addChild = controllerType.GetMethod("AddChildToParent", BindingFlags.Public | BindingFlags.Instance);
        var addExposed = controllerType.GetMethod("AddExposedParameter", BindingFlags.Public | BindingFlags.Instance);
        var getVolGuid = groupType.GetMethod("GetGUIDForVolume", BindingFlags.Public | BindingFlags.Instance);

        if (createGroup == null || addChild == null || addExposed == null || getVolGuid == null)
        {
            Debug.LogError("GameAudioMixerSetup: Missing expected public APIs on AudioMixerController / Group.");
            return;
        }

        var ui = createGroup.Invoke(controller, new object[] { "Ui", false });
        addChild.Invoke(controller, new object[] { ui, master });

        var g = getVolGuid.Invoke(ui, null);
        var paramPath = Activator.CreateInstance(pathType, ui, g);
        addExposed.Invoke(controller, new object[] { paramPath });

        var so = new SerializedObject((UnityEngine.Object)controller);
        var exposed = so.FindProperty("m_ExposedParameters");
        if (exposed == null || !exposed.isArray || exposed.arraySize == 0)
        {
            Debug.LogWarning("GameAudioMixerSetup: Could not find m_ExposedParameters; rename the new exposed param to UiVolume manually.");
        }
        else
        {
            var nameProp = exposed.GetArrayElementAtIndex(exposed.arraySize - 1).FindPropertyRelative("name");
            if (nameProp != null)
                nameProp.stringValue = "UiVolume";
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorUtility.SetDirty((UnityEngine.Object)controller);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorGUIUtility.PingObject((UnityEngine.Object)controller);
        Debug.Log("GameAudioMixerSetup: Added 'Ui' group under Master, exposed as 'UiVolume'.");
    }

    static void EnsureFolder(string parent, string child)
    {
        if (AssetDatabase.IsValidFolder($"{parent}/{child}"))
            return;
        AssetDatabase.CreateFolder(parent, child);
    }
}
#endif
