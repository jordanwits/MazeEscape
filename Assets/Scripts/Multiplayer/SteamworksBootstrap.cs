using System;
using System.IO;
using UnityEngine;
using Steamworks;

[DisallowMultipleComponent]
public class SteamworksBootstrap : MonoBehaviour
{
    /// <summary>Valve's Spacewar test app — the placeholder until <see cref="MultiplayerProjectSettings.SteamAppId"/> is set.</summary>
    public const uint SpacewarAppId = 480;

    /// <summary>The game's app id, from <see cref="MultiplayerProjectSettings"/> (the single place it is set).</summary>
    public static uint ConfiguredAppId
    {
        get
        {
            MultiplayerProjectSettings settings = Resources.Load<MultiplayerProjectSettings>("MultiplayerProjectSettings");
            return settings != null ? settings.SteamAppId : SpacewarAppId;
        }
    }

    /// <summary>
    /// Whether this process runs Steam on its own (environment app id, steam_appid.txt) instead of being launched
    /// by Steam. The Editor and development builds always do, and so does any build still on the Spacewar
    /// placeholder, which has no store app to relaunch through. A release build under the real id never does.
    /// </summary>
    public static bool RunsOutsideSteam(uint appId) =>
        Application.isEditor || Debug.isDebugBuild || appId == SpacewarAppId;

    static SteamworksBootstrap s_Instance;
    static bool s_IsReady;
    static string s_Status = "Steamworks is not initialized.";
    static ulong s_LocalSteamId;
    static string s_LocalPersonaName = string.Empty;

    public static bool IsReady => s_IsReady;
    public static string Status => s_Status;
    public static ulong LocalSteamId => s_LocalSteamId;
    public static string LocalPersonaName => s_LocalPersonaName;

    void Awake()
    {
        if (s_Instance != null && s_Instance != this)
        {
            Destroy(this);
            return;
        }

        s_Instance = this;
        InitializeSteamworks();
    }

    void Update()
    {
        if (s_IsReady)
            SteamAPI.RunCallbacks();
    }

    void OnDestroy()
    {
        if (s_Instance != this)
            return;

        if (s_IsReady)
            SteamAPI.Shutdown();

        s_IsReady = false;
        s_LocalSteamId = 0UL;
        s_LocalPersonaName = string.Empty;
        s_Status = "Steamworks shut down.";
        s_Instance = null;
    }

    void InitializeSteamworks()
    {
        if (s_IsReady)
            return;

        try
        {
            uint appId = ConfiguredAppId;
            bool runsOutsideSteam = RunsOutsideSteam(appId);
            if (runsOutsideSteam)
                PrepareSteamAppIdForLocalLaunch(appId);

            if (!Packsize.Test())
            {
                s_Status = "Steamworks pack size check failed.";
                Debug.LogError($"[Steam] {s_Status}", this);
                return;
            }

            if (!DllCheck.Test())
            {
                s_Status = "Steamworks native DLL check failed.";
                Debug.LogError($"[Steam] {s_Status}", this);
                return;
            }

            // A release build started straight from its exe (shortcut, install folder) would otherwise run without the
            // store app behind it. Steam relaunches it properly and this copy exits.
            if (!runsOutsideSteam && SteamAPI.RestartAppIfNecessary(new AppId_t(appId)))
            {
                s_Status = "Relaunching through Steam.";
                Debug.Log($"[Steam] {s_Status}", this);
                Application.Quit();
                return;
            }

            s_IsReady = SteamAPI.Init();
            if (!s_IsReady)
            {
                s_Status = runsOutsideSteam
                    ? $"SteamAPI.Init failed. Run the Steam client, and make sure steam_appid.txt with {appId} is next to the game .exe."
                    : "SteamAPI.Init failed. Run the Steam client and start the game from Steam.";
                Debug.LogWarning($"[Steam] {s_Status}", this);
                return;
            }

            s_LocalSteamId = SteamUser.GetSteamID().m_SteamID;
            s_LocalPersonaName = SteamFriends.GetPersonaName();
            s_Status = $"Steam ready as {s_LocalPersonaName} ({s_LocalSteamId}).";
            Debug.Log($"[Steam] {s_Status}", this);
        }
        catch (System.Exception ex)
        {
            s_IsReady = false;
            s_Status = $"Steamworks initialization failed: {ex.Message}";
            Debug.LogError($"[Steam] {s_Status}", this);
        }
    }

    /// <summary>
    /// Lets SteamAPI.Init find the app id when Steam did not launch the process. Only for <see cref="RunsOutsideSteam"/>
    /// processes: under a real id in a release build this would override Steam's own launch and keep it from
    /// relaunching the game. The dev-only steam_appid.txt is written only in the Editor and development builds, so a
    /// shipped build never leaves one in its install folder or in whatever folder it was started from.
    /// </summary>
    static void PrepareSteamAppIdForLocalLaunch(uint configuredAppId)
    {
        string appId = configuredAppId.ToString();

        // A player keeps what Steam put there when Steam launched it. The Editor is never launched by Steam and its
        // process outlives play sessions, so a value from an earlier session (before the id changed) must not stick.
        bool overwrite = Application.isEditor;
        if (overwrite || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId")))
            Environment.SetEnvironmentVariable("SteamAppId", appId);

        if (overwrite || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamGameId")))
            Environment.SetEnvironmentVariable("SteamGameId", appId);

        if (!Application.isEditor && !Debug.isDebugBuild)
            return;

        WriteSteamAppIdIfMissing(AppContext.BaseDirectory, appId);
        WriteSteamAppIdIfMissing(Directory.GetCurrentDirectory(), appId);
    }

    static void WriteSteamAppIdIfMissing(string folder, string appId)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        string path = Path.Combine(folder, "steam_appid.txt");
        if (File.Exists(path))
            return;

        try
        {
            File.WriteAllText(path, appId);
            Debug.Log($"[Steam] Created development steam_appid.txt at {path}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Steam] Could not create development steam_appid.txt at {path}: {ex.Message}");
        }
    }
}
