using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// The game's one <see cref="EventSystem"/>. Unity's UI wants exactly one active at a time, and every screen here is
/// built at runtime, so a single instance is made when the game starts (<see cref="MultiplayerBootstrap"/>) and kept
/// for the whole process; every menu and overlay calls <see cref="Ensure"/> instead of making its own.
///
/// Before this, each overlay left its own DontDestroyOnLoad EventSystem behind and the Menu scene carried an authored
/// one, so the menu ran with two and Unity logged an error every frame. Scenes must not author an EventSystem: one
/// found on a scene load is switched off with a warning naming it.
/// </summary>
public static class UiEventSystem
{
    static EventSystem s_instance;
    static bool s_watchingSceneLoads;

    /// <summary>The shared EventSystem, created on first use. Safe to call every time a screen opens.</summary>
    public static EventSystem Ensure()
    {
        if (!s_watchingSceneLoads)
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            s_watchingSceneLoads = true;
        }

        if (s_instance != null)
            return s_instance;

        var go = new GameObject("EventSystem");
        Object.DontDestroyOnLoad(go);
        s_instance = go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
        DisableOthers();
        return s_instance;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (s_instance != null)
            DisableOthers();
    }

    static void DisableOthers()
    {
        EventSystem[] all = Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Exclude);
        for (int i = 0; i < all.Length; i++)
        {
            EventSystem other = all[i];
            if (other == null || other == s_instance || !other.enabled)
                continue;

            other.enabled = false;
            Debug.LogWarning(
                $"[{nameof(UiEventSystem)}] Disabled the extra EventSystem '{other.name}' in scene '{other.gameObject.scene.name}'. "
                + "The game keeps one persistent EventSystem; remove this one from the scene.",
                other);
        }
    }
}
