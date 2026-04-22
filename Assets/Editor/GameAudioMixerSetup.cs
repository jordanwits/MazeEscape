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
        addChild.Invoke(controller, new object[] { music, master });
        addChild.Invoke(controller, new object[] { sfx, master });

        void ExposeGroupVolume(object groupObj)
        {
            var g = getVolGuid.Invoke(groupObj, null);
            var paramPath = Activator.CreateInstance(pathType, groupObj, g);
            addExposed.Invoke(controller, new object[] { paramPath });
        }

        ExposeGroupVolume(master);
        ExposeGroupVolume(music);
        ExposeGroupVolume(sfx);

        var so = new SerializedObject((UnityEngine.Object)controller);
        var exposed = so.FindProperty("m_ExposedParameters");
        if (exposed == null || !exposed.isArray)
        {
            Debug.LogWarning("GameAudioMixerSetup: Could not find m_ExposedParameters; rename exposed params in the mixer to MasterVolume, MusicVolume, SfxVolume.");
        }
        else
        {
            string[] names = { "MasterVolume", "MusicVolume", "SfxVolume" };
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

    static void EnsureFolder(string parent, string child)
    {
        if (AssetDatabase.IsValidFolder($"{parent}/{child}"))
            return;
        AssetDatabase.CreateFolder(parent, child);
    }
}
#endif
