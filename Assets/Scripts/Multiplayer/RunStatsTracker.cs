using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Server-side numbers for the run-complete card (<see cref="RunCompleteSequence"/>): per player, damage taken, HP
/// healed on teammates, deaths, kills, chases, distance travelled and tickets, plus each section's clock.
///
/// The data is static, keyed by client id, because a run spans three section loads and the avatars are rebuilt for
/// each one (the same reason as <see cref="AchievementRunTracker"/>, whose run lifecycle this mirrors: a run starts on
/// the first maze section after a non-maze scene, and any non-maze scene ends it). The component itself lives on the
/// network bootstrap and only samples positions for the distance stat.
///
/// Gameplay reports through <see cref="AchievementHooks"/>, <see cref="AchievementRunTracker"/>,
/// <see cref="PlayerHealth"/> and the bandage heal; every entry point is a no-op on a pure client.
/// </summary>
[DisallowMultipleComponent]
public sealed class RunStatsTracker : MonoBehaviour
{
    const float SampleIntervalSeconds = 0.25f;
    /// <summary>A longer step between two samples is a teleport or a respawn, not travel.</summary>
    const float MaxStepMeters = 6f;
    /// <summary>Below this a step is interpolation jitter on a player standing still.</summary>
    const float MinStepMeters = 0.02f;
    /// <summary>A lock-on this soon after the previous one on the same player is the same chase.</summary>
    const float ChaseGapSeconds = 5f;

    sealed class Record
    {
        public string Name;
        public int CharacterIndex = -1;
        public float DamageTaken;
        public float HealingGiven;
        public int Deaths;
        public int Kills;
        public int Chases;
        public float DistanceMeters;
        public int Tickets;
        public float LastTargetedTime = float.NegativeInfinity;
        public bool HasLastPosition;
        public Vector3 LastPosition;
    }

    static bool s_RunActive;
    static readonly Dictionary<ulong, Record> s_Records = new();
    /// <summary>Client ids in the order they were first seen, so the card's rows stay put.</summary>
    static readonly List<ulong> s_Order = new();
    static readonly List<string> s_SectionScenes = new();
    static readonly List<float> s_SectionSeconds = new();
    static string s_SectionScene;
    static float s_SectionStartTime;
    static bool s_SectionClockStartedOnBuild;
    static bool s_SectionRecorded;

    float _nextSampleTime;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        ProceduralMazeCoordinator.LocalMazeBuilt -= HandleLocalMazeBuilt;
        ProceduralMazeCoordinator.LocalMazeBuilt += HandleLocalMazeBuilt;
        EndRun();
    }

    /// <summary>A level entered straight from the Editor is loaded before the sceneLoaded subscription can see it.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CatchInitialScene()
    {
        Scene active = SceneManager.GetActiveScene();
        if (s_SectionScene == null && MultiplayerSceneFlow.IsMazeGameplayScene(active.name))
            HandleSceneLoaded(active, LoadSceneMode.Single);
    }

    static bool IsAuthority => AchievementRunTracker.IsAuthority;

    static bool Recording => s_RunActive && IsAuthority;

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
            EndRun();
            s_RunActive = true;
        }

        s_SectionScene = scene.name;
        s_SectionStartTime = Time.time;
        s_SectionClockStartedOnBuild = false;
        s_SectionRecorded = false;

        // Every avatar is rebuilt at the new section's start; a step from the old position is not travel.
        foreach (Record record in s_Records.Values)
            record.HasLastPosition = false;
    }

    /// <summary>The section clock runs from when the server's maze stands, not from the scene load.</summary>
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
        s_Records.Clear();
        s_Order.Clear();
        s_SectionScenes.Clear();
        s_SectionSeconds.Clear();
        s_SectionScene = null;
        s_SectionClockStartedOnBuild = false;
        s_SectionRecorded = false;
    }

    static Record GetRecord(ulong clientId)
    {
        if (!s_Records.TryGetValue(clientId, out Record record))
        {
            record = new Record();
            s_Records[clientId] = record;
            s_Order.Add(clientId);
        }

        return record;
    }

    static bool TryGetRecord(PlayerHealth player, out ulong clientId, out Record record)
    {
        record = null;
        if (!AchievementAwards.TryGetOwner(player, out clientId))
            return false;

        record = GetRecord(clientId);
        return true;
    }

    // ---- sampling ---------------------------------------------------------------------------------------

    void Update()
    {
        if (!Recording || Time.time < _nextSampleTime)
            return;

        _nextSampleTime = Time.time + SampleIntervalSeconds;
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
            SamplePlayer(players[i]);
    }

    static void SamplePlayer(PlayerHealth player)
    {
        if (player == null || !TryGetRecord(player, out _, out Record record))
            return;

        RefreshIdentity(player, record);

        if (player.IsDead)
        {
            record.HasLastPosition = false;
            return;
        }

        Vector3 position = player.transform.position;
        // Carried to the jail by the Jailor: moving, but not under their own power.
        bool carried = player.TryGetComponent(out NetworkPlayerAvatar avatar) && avatar.IsCarriedByJailor;
        if (record.HasLastPosition && !carried)
        {
            float step = Vector3.Distance(record.LastPosition, position);
            if (step >= MinStepMeters && step <= MaxStepMeters)
                record.DistanceMeters += step;
        }

        record.LastPosition = position;
        record.HasLastPosition = true;
    }

    /// <summary>Kept current while the player is here, so a player who leaves mid-run keeps their name on the card.</summary>
    static void RefreshIdentity(PlayerHealth player, Record record)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return;

        record.Name = PlayerIdentity.DisplayName(player);
        int characterIndex = PlayerIdentity.CharacterIndex(player);
        if (characterIndex >= 0)
            record.CharacterIndex = characterIndex;
    }

    // ---- gameplay entry points (server; no-ops elsewhere) -----------------------------------------------

    /// <summary>HP a player actually lost (the overkill on a lethal hit is not counted).</summary>
    public static void ServerNoteDamageTaken(PlayerHealth player, float amount)
    {
        if (Recording && amount > 0f && TryGetRecord(player, out _, out Record record))
            record.DamageTaken += amount;
    }

    public static void ServerNoteTeammateHealed(ulong healerClientId, float amount)
    {
        if (Recording && amount > 0f)
            GetRecord(healerClientId).HealingGiven += amount;
    }

    public static void ServerNotePlayerDied(ulong clientId)
    {
        if (Recording)
            GetRecord(clientId).Deaths++;
    }

    public static void ServerNoteEnemyKilled(PlayerHealth killer)
    {
        if (Recording && TryGetRecord(killer, out _, out Record record))
            record.Kills++;
    }

    public static void ServerNoteTargeted(PlayerHealth target)
    {
        if (!Recording || !TryGetRecord(target, out _, out Record record))
            return;

        // Several AIs re-acquire the same target while they keep chasing it, and two enemies can lock on together:
        // count a chase only when nothing has been on this player for a while.
        if (Time.time - record.LastTargetedTime >= ChaseGapSeconds)
            record.Chases++;
        record.LastTargetedTime = Time.time;
    }

    public static void ServerNoteTicketsEarned(ulong clientId, int amount)
    {
        if (Recording && amount > 0)
            GetRecord(clientId).Tickets += amount;
    }

    /// <summary>Server, as an exit cab leaves: stops this section's clock. Repeat calls for the same section are ignored.</summary>
    public static void ServerSectionCompleted(string sceneName)
    {
        if (!Recording || s_SectionRecorded
            || !string.Equals(sceneName, s_SectionScene, System.StringComparison.OrdinalIgnoreCase))
            return;

        s_SectionRecorded = true;
        s_SectionScenes.Add(sceneName);
        s_SectionSeconds.Add(Mathf.Max(0f, Time.time - s_SectionStartTime));
    }

    /// <summary>
    /// Server, when the last exit cab has shut on <paramref name="escaped"/> (living and aboard). Everyone who took
    /// part gets a row: escaped, dead in the last section, or gone.
    /// </summary>
    public static RunSummary ServerBuildSummary(IReadOnlyList<ulong> escaped)
    {
        NetworkManager nm = NetworkManager.Singleton;
        bool session = nm != null && nm.IsListening;
        var connected = new HashSet<ulong>();
        if (session)
        {
            foreach (ulong id in nm.ConnectedClientsIds)
                connected.Add(id);
        }
        else
            connected.Add(0);

        // Last look at everyone still here, so a player who never got sampled still gets a row and a name.
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i] != null && TryGetRecord(players[i], out _, out Record record))
                RefreshIdentity(players[i], record);
        }

        foreach (ulong id in connected)
            GetRecord(id);

        var escapedSet = new HashSet<ulong>();
        if (escaped != null)
        {
            for (int i = 0; i < escaped.Count; i++)
                escapedSet.Add(escaped[i]);
        }

        var rows = new RunSummary.Player[s_Order.Count];
        var usedColors = new HashSet<int>();
        for (int i = 0; i < s_Order.Count; i++)
        {
            Record record = s_Records[s_Order[i]];
            if (record.CharacterIndex >= 0)
                usedColors.Add(record.CharacterIndex % MenuTheme.PlayerColors.Length);
        }

        for (int i = 0; i < s_Order.Count; i++)
        {
            ulong id = s_Order[i];
            Record record = s_Records[id];
            rows[i] = new RunSummary.Player
            {
                ClientId = id,
                Name = !string.IsNullOrEmpty(record.Name)
                    ? record.Name
                    : session ? MultiplayerSessionController.FallbackDisplayName(id) : "YOU",
                ColorIndex = ResolveColor(record, usedColors),
                Outcome = escapedSet.Contains(id)
                    ? RunSummary.Outcome.Escaped
                    : connected.Contains(id) ? RunSummary.Outcome.Died : RunSummary.Outcome.Left,
                DamageTaken = record.DamageTaken,
                HealingGiven = record.HealingGiven,
                Deaths = record.Deaths,
                Kills = record.Kills,
                Chases = record.Chases,
                DistanceMeters = record.DistanceMeters,
                Tickets = record.Tickets,
            };
        }

        return new RunSummary
        {
            Players = rows,
            SectionScenes = s_SectionScenes.ToArray(),
            SectionSeconds = s_SectionSeconds.ToArray(),
        };
    }

    /// <summary>The lobby character slot's colour, as everywhere else; otherwise the first slot nobody has.</summary>
    static int ResolveColor(Record record, HashSet<int> usedColors)
    {
        int colors = MenuTheme.PlayerColors.Length;
        if (record.CharacterIndex >= 0)
            return record.CharacterIndex % colors;

        for (int candidate = 0; candidate < colors; candidate++)
        {
            if (usedColors.Add(candidate))
                return candidate;
        }

        return 0;
    }
}
