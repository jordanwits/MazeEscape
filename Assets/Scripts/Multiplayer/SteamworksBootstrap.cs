using System;
using System.IO;
using UnityEngine;
using Steamworks;

[DisallowMultipleComponent]
public class SteamworksBootstrap : MonoBehaviour
{
    public const uint SpacewarAppId = 480;

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
            PrepareSteamAppIdForLocalBuild();

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

            s_IsReady = SteamAPI.Init();
            if (!s_IsReady)
            {
                s_Status =
                    $"SteamAPI.Init failed. Run the Steam client, and make sure steam_appid.txt with {SpacewarAppId} is next to the game .exe.";
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

    static void PrepareSteamAppIdForLocalBuild()
    {
        string appId = SpacewarAppId.ToString();

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId")))
            Environment.SetEnvironmentVariable("SteamAppId", appId);

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamGameId")))
            Environment.SetEnvironmentVariable("SteamGameId", appId);

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
