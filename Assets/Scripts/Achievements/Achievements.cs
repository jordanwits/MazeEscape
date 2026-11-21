using System.Collections.Generic;
using Steamworks;
using UnityEngine;

/// <summary>
/// The LOCAL player's achievements. Gameplay never calls this directly — the server decides an unlock and routes it
/// to the earning client through <see cref="AchievementAwards"/>; this is where it lands on that client.
///
/// Every unlock is recorded in PlayerPrefs (so direct-IP / LAN sessions and the Editor keep a record and never repeat
/// the toast) and pushed to Steam when Steamworks is up. Steam's own overlay shows the popup in a Steam-launched
/// build; elsewhere (no Steam, or the Editor, where the overlay is never injected) <see cref="AchievementToast"/>
/// shows it instead.
/// </summary>
public static class Achievements
{
    const string UnlockKeyPrefix = "achievement.";
    const string ProgressKeyPrefix = "achievement.progress.";

    public static bool IsUnlocked(AchievementId id)
    {
        return AchievementCatalog.TryGet(id, out AchievementCatalog.Entry entry)
            && PlayerPrefs.GetInt(UnlockKeyPrefix + entry.ApiName, 0) == 1;
    }

    public static int GetProgress(AchievementId id)
    {
        return AchievementCatalog.TryGet(id, out AchievementCatalog.Entry entry) && entry.StatName != null
            ? PlayerPrefs.GetInt(ProgressKeyPrefix + entry.StatName, 0)
            : 0;
    }

    public static void Unlock(AchievementId id)
    {
        if (!AchievementCatalog.TryGet(id, out AchievementCatalog.Entry entry))
            return;

        // Steam is told every time, even when the local record already has it: a fresh install (or another PC)
        // has an empty PlayerPrefs while Steam may not have the unlock yet, and SetAchievement is idempotent.
        bool pushedToSteam = TryPushToSteam(entry);

        if (PlayerPrefs.GetInt(UnlockKeyPrefix + entry.ApiName, 0) == 1)
            return;

        PlayerPrefs.SetInt(UnlockKeyPrefix + entry.ApiName, 1);
        PlayerPrefs.Save();
        Debug.Log($"[Achievements] Unlocked {entry.ApiName} ({entry.Title}){(pushedToSteam ? " — sent to Steam" : string.Empty)}.");

        if (!pushedToSteam || Application.isEditor)
            AchievementToast.Show(entry.Title, entry.Description);
    }

    /// <summary>Adds to a lifetime counter (e.g. skeletons crumbled) and unlocks its achievement once the count is reached.</summary>
    public static void AddProgress(AchievementId id, int amount)
    {
        if (amount <= 0
            || !AchievementCatalog.TryGet(id, out AchievementCatalog.Entry entry)
            || entry.StatName == null)
            return;

        string key = ProgressKeyPrefix + entry.StatName;
        int count = PlayerPrefs.GetInt(key, 0);
        // Steam may already hold a higher count from another machine; never count down from it.
        if (SteamworksBootstrap.IsReady && SteamUserStats.GetStat(entry.StatName, out int steamCount) && steamCount > count)
            count = steamCount;

        count += amount;
        PlayerPrefs.SetInt(key, count);
        PlayerPrefs.Save();

        if (SteamworksBootstrap.IsReady && SteamUserStats.SetStat(entry.StatName, count))
            SteamUserStats.StoreStats();

        if (count >= entry.ProgressTarget)
            Unlock(id);
    }

    /// <summary>
    /// Sends Steam whatever this machine has on record that Steam does not have yet: unlocks (and lifetime counts)
    /// earned while Steam could not take them — before the app defined its achievements, or in a session without
    /// Steam. Without this they only reach Steam if the player earns them again, and some never come up twice.
    /// Returns false while Steam cannot report achievement state yet, so the caller can retry.
    /// </summary>
    public static bool TrySyncLocalRecordToSteam()
    {
        if (!SteamworksBootstrap.IsReady)
            return false;

        bool readable = false;
        int unlocksSent = 0;
        int countsSent = 0;
        try
        {
            IReadOnlyList<AchievementCatalog.Entry> entries = AchievementCatalog.All;
            for (int i = 0; i < entries.Count; i++)
            {
                AchievementCatalog.Entry entry = entries[i];
                // False until Steam has this user's stats, or when the app does not define the name.
                if (!SteamUserStats.GetAchievement(entry.ApiName, out bool achievedOnSteam))
                    continue;

                readable = true;
                if (!achievedOnSteam
                    && PlayerPrefs.GetInt(UnlockKeyPrefix + entry.ApiName, 0) == 1
                    && SteamUserStats.SetAchievement(entry.ApiName))
                    unlocksSent++;

                if (entry.StatName == null)
                    continue;

                int localCount = PlayerPrefs.GetInt(ProgressKeyPrefix + entry.StatName, 0);
                if (localCount > 0
                    && SteamUserStats.GetStat(entry.StatName, out int steamCount)
                    && localCount > steamCount
                    && SteamUserStats.SetStat(entry.StatName, localCount))
                    countsSent++;
            }

            if (unlocksSent > 0 || countsSent > 0)
                SteamUserStats.StoreStats();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Achievements] Could not sync earlier unlocks to Steam: {ex.Message}");
            return true;
        }

        if (unlocksSent > 0 || countsSent > 0)
            Debug.Log($"[Achievements] Sent Steam {unlocksSent} earlier unlock(s) and {countsSent} lifetime count(s) it did not have.");

        return readable;
    }

    static bool TryPushToSteam(AchievementCatalog.Entry entry)
    {
        if (!SteamworksBootstrap.IsReady)
            return false;

        try
        {
            // False when the app (e.g. the Spacewar placeholder) does not define this API name.
            if (!SteamUserStats.SetAchievement(entry.ApiName))
                return false;

            SteamUserStats.StoreStats();
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[Achievements] Steam rejected {entry.ApiName}: {ex.Message}");
            return false;
        }
    }
}
