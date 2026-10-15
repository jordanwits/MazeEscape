using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Server-side bookkeeping for the achievements that judge a whole section or a whole run: deaths, team size,
/// tickets earned, energy drinks, who hit an enemy, who was chased, and the section clock. Static, because it has to
/// outlive the avatars and the scene — a run spans three section loads, and the avatars are rebuilt for each one.
/// Keyed by client id, which the NGO connection keeps for the whole session.
///
/// A run starts when the first maze section loads after a non-maze scene (the lobby can start on any section, so the
/// start scene is recorded and "full run" achievements require Level01). Any non-maze scene ends it. Sections restart
/// their per-section sets on load, and the section clock restarts again once the server's own maze is built.
/// </summary>
public static class AchievementRunTracker
{
    const int HighRollerTickets = 50;
    const int WiredDrinks = 3;

    static bool s_RunActive;
    static string s_RunStartScene;
    static int s_RunDeaths;
    static int s_RunMaxPlayers;
    static readonly Dictionary<ulong, int> s_TicketsEarned = new();

    static string s_SectionScene;
    static float s_SectionStartTime;
    static bool s_SectionClockStartedOnBuild;
    static readonly HashSet<ulong> s_HitEnemyThisSection = new();
    static readonly HashSet<ulong> s_ChasedThisSection = new();
    static readonly Dictionary<ulong, int> s_DrinksThisSection = new();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        ProceduralMazeCoordinator.LocalMazeBuilt -= HandleLocalMazeBuilt;
        ProceduralMazeCoordinator.LocalMazeBuilt += HandleLocalMazeBuilt;
        EndRun();
    }

    /// <summary>
    /// A level entered straight from the Editor is already loaded before the sceneLoaded subscription can see it;
    /// pick it up here so the run and the section clock still start. A real run loads every section after this.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CatchInitialScene()
    {
        Scene active = SceneManager.GetActiveScene();
        if (s_SectionScene == null && MultiplayerSceneFlow.IsMazeGameplayScene(active.name))
            HandleSceneLoaded(active, LoadSceneMode.Single);
    }

    /// <summary>True on the server, and with no session at all (a level played straight from the Editor).</summary>
    public static bool IsAuthority
    {
        get
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm == null || !nm.IsListening || nm.IsServer;
        }
    }

    static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single)
            return;

        if (!MultiplayerSceneFlow.IsMazeGameplayScene(scene.name))
        {
            EndRun();
            return;
        }

        if (!s_RunActive)
        {
            s_RunActive = true;
            s_RunStartScene = scene.name;
            s_RunDeaths = 0;
            s_RunMaxPlayers = 0;
            s_TicketsEarned.Clear();
        }

        s_SectionScene = scene.name;
        s_SectionStartTime = Time.time;
        s_SectionClockStartedOnBuild = false;
        s_HitEnemyThisSection.Clear();
        s_ChasedThisSection.Clear();
        s_DrinksThisSection.Clear();
        SampleTeamSize();
    }

    /// <summary>The clock runs from when the server's maze stands, not from the scene load (the build takes seconds).</summary>
    static void HandleLocalMazeBuilt(string sceneName)
    {
        if (s_SectionClockStartedOnBuild || !string.Equals(sceneName, s_SectionScene, System.StringComparison.OrdinalIgnoreCase))
            return;

        s_SectionClockStartedOnBuild = true;
        s_SectionStartTime = Time.time;
    }

    static void EndRun()
    {
        s_RunActive = false;
        s_RunStartScene = null;
        s_RunDeaths = 0;
        s_RunMaxPlayers = 0;
        s_TicketsEarned.Clear();
        s_SectionScene = null;
        s_HitEnemyThisSection.Clear();
        s_ChasedThisSection.Clear();
        s_DrinksThisSection.Clear();
    }

    static int ConnectedPlayerCount()
    {
        NetworkManager nm = NetworkManager.Singleton;
        return nm != null && nm.IsListening && nm.IsServer ? nm.ConnectedClientsIds.Count : 1;
    }

    static void SampleTeamSize()
    {
        int count = ConnectedPlayerCount();
        if (count > s_RunMaxPlayers)
            s_RunMaxPlayers = count;
    }

    public static void ServerNotePlayerDied(ulong clientId)
    {
        if (IsAuthority)
            s_RunDeaths++;
    }

    public static void ServerNoteEnemyHit(ulong clientId)
    {
        if (IsAuthority)
            s_HitEnemyThisSection.Add(clientId);
    }

    public static void ServerNoteChased(ulong clientId)
    {
        if (IsAuthority)
            s_ChasedThisSection.Add(clientId);
    }

    public static void ServerNoteTicketsEarned(ulong clientId, int amount)
    {
        if (!IsAuthority || amount <= 0)
            return;

        s_TicketsEarned.TryGetValue(clientId, out int total);
        total += amount;
        s_TicketsEarned[clientId] = total;
        if (total >= HighRollerTickets)
            AchievementAwards.ServerAward(clientId, AchievementId.HighRoller);
    }

    public static void ServerNoteEnergyDrink(ulong clientId)
    {
        if (!IsAuthority)
            return;

        s_DrinksThisSection.TryGetValue(clientId, out int count);
        count++;
        s_DrinksThisSection[clientId] = count;
        if (count >= WiredDrinks)
            AchievementAwards.ServerAward(clientId, AchievementId.Wired);
    }

    /// <summary>
    /// Server, as the exit cab leaves with <paramref name="escaped"/> aboard (living and inside). Awards the section
    /// escape, the per-section skill challenges for those aboard, and — when <paramref name="endsRun"/> — the run ones.
    /// </summary>
    public static void ServerSectionCompleted(string sceneName, bool endsRun, IReadOnlyList<ulong> escaped)
    {
        if (!IsAuthority || escaped == null || escaped.Count == 0)
            return;

        SampleTeamSize();
        int connected = ConnectedPlayerCount();

        AchievementId? escape = SectionEscapeAchievement(sceneName);
        float sectionSeconds = Time.time - s_SectionStartTime;
        bool beatPar = TryGetSpeedrunPar(sceneName, out float par) && sectionSeconds <= par;

        for (int i = 0; i < escaped.Count; i++)
        {
            ulong clientId = escaped[i];
            if (escape.HasValue)
                AchievementAwards.ServerAward(clientId, escape.Value);
            if (!s_HitEnemyThisSection.Contains(clientId))
                AchievementAwards.ServerAward(clientId, AchievementId.Pacifist);
            if (!s_ChasedThisSection.Contains(clientId))
                AchievementAwards.ServerAward(clientId, AchievementId.Ghost);
            if (beatPar)
                AchievementAwards.ServerAward(clientId, AchievementId.Speedrunner);
        }

        if (connected > 1 && escaped.Count == 1)
            AchievementAwards.ServerAward(escaped[0], AchievementId.LastOneStanding);

        if (!endsRun)
            return;

        // Clocking Out is "finish a run", so it counts from wherever the lobby started it; the rest need all three.
        for (int i = 0; i < escaped.Count; i++)
            AchievementAwards.ServerAward(escaped[i], AchievementId.ClockingOut);

        bool fullRun = string.Equals(s_RunStartScene, MultiplayerSceneFlow.MazeSectionSceneNames[0], System.StringComparison.OrdinalIgnoreCase);
        if (!fullRun)
            return;

        if (s_RunDeaths == 0)
            AchievementAwards.ServerAwardAll(AchievementId.PerfectAttendance);
        if (s_RunMaxPlayers <= 1 && connected == 1)
            AchievementAwards.ServerAward(escaped[0], AchievementId.LoneSurvivor);
        if (connected >= 4)
            AchievementAwards.ServerAwardAll(AchievementId.FullCrew);
    }

    static AchievementId? SectionEscapeAchievement(string sceneName)
    {
        string[] sections = MultiplayerSceneFlow.MazeSectionSceneNames;
        if (string.Equals(sceneName, sections[0], System.StringComparison.OrdinalIgnoreCase))
            return AchievementId.Detour;
        if (sections.Length > 1 && string.Equals(sceneName, sections[1], System.StringComparison.OrdinalIgnoreCase))
            return AchievementId.StepRightUp;
        // The last section's escape is Clocking Out, awarded with the run end above.
        return null;
    }

    static bool TryGetSpeedrunPar(string sceneName, out float seconds)
    {
        seconds = 0f;
        NetworkManager nm = NetworkManager.Singleton;
        ProceduralMazeCoordinator coordinator = nm != null ? nm.GetComponent<ProceduralMazeCoordinator>() : null;
        if (coordinator == null)
            coordinator = Object.FindAnyObjectByType<ProceduralMazeCoordinator>();
        return coordinator != null && coordinator.TryGetSpeedrunParSeconds(sceneName, out seconds) && seconds > 0f;
    }
}
