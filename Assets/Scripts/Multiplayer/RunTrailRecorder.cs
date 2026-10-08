using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Remembers where every player went in the current maze section, for the game-over map
/// (<see cref="RunTrailMap"/> on <see cref="TeamWipeSequence"/>'s card).
///
/// Purely local and runs on every peer: each peer already sees every avatar move (the owner its own, everyone
/// else through the replicated transforms), so each records its own copy and nothing is networked. A peer that
/// joined mid-section only knows the paths from when it arrived.
///
/// A trail stops when its player dies (death is permanent for the section) and the whole recording freezes
/// when the wipe begins. Lives on the multiplayer bootstrap (DontDestroyOnLoad) and starts over with every
/// maze section load.
/// </summary>
[DisallowMultipleComponent]
public sealed class RunTrailRecorder : MonoBehaviour
{
    const float SampleIntervalSeconds = 0.15f;
    /// <summary>Finer than the map can show; the map thins it again by on-screen distance.</summary>
    const float MinStepMeters = 0.5f;
    /// <summary>
    /// Faster than anyone moves on foot or in the Jailor's arms; a sample this far from the last one (scaled by
    /// the time between them) is a teleport orb or a spawn placement, and draws as a gap rather than a line
    /// through the walls.
    /// </summary>
    const float MaxPlausibleSpeed = 12f;
    const float MinBreakMeters = 5f;
    /// <summary>A freshly spawned avatar can sit at its prefab pose for a frame or two before its spawn placement lands.</summary>
    const float SettleSeconds = 0.5f;
    const float IdentityRefreshSeconds = 2f;

    public sealed class Trail
    {
        public string DisplayName;
        public int ColorIndex;
        /// <summary>Ends with this player's death (rather than with them leaving or the section ending).</summary>
        public bool EndedByDeath;
        public readonly List<Vector3> Points = new();
        /// <summary>Seconds since the section started, one per point.</summary>
        public readonly List<float> Times = new();
        /// <summary>True where the path jumped (teleport) — no line is drawn INTO that point.</summary>
        public readonly List<bool> BreakBefore = new();
        /// <summary>
        /// Indices of points where a jump came out of a teleporter (<see cref="TeleportOrb"/>): the point before is
        /// where the player used it, this one is where they landed.
        /// </summary>
        public readonly List<int> TeleporterArrivals = new();

        internal float FirstSeenTime;
        internal float LastSampleTime;
        internal bool ColorAssigned;
    }

    static RunTrailRecorder s_instance;

    readonly List<Trail> _trails = new();
    readonly Dictionary<PlayerHealth, Trail> _byAvatar = new();
    float _sectionStartTime;
    float _nextSampleTime;
    float _nextIdentityRefresh;
    bool _recording;
    bool _frozen;

    /// <summary>Every trail recorded in the current section (empty outside one).</summary>
    public static IReadOnlyList<Trail> CurrentTrails => s_instance != null ? s_instance._trails : System.Array.Empty<Trail>();

    void Awake()
    {
        s_instance = this;
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        if (s_instance == this)
            s_instance = null;
    }

    void Start()
    {
        // A level opened straight from the editor loaded before this bootstrap existed.
        Scene active = SceneManager.GetActiveScene();
        if (!_recording && active.IsValid() && MultiplayerSceneFlow.IsMazeGameplayScene(active.name))
            BeginSection();
    }

    void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single)
            return;

        if (MultiplayerSceneFlow.IsMazeGameplayScene(scene.name))
            BeginSection();
        else
        {
            _recording = false;
            Clear();
        }
    }

    void BeginSection()
    {
        Clear();
        _sectionStartTime = Time.time;
        _nextSampleTime = 0f;
        _recording = true;
    }

    void Clear()
    {
        _trails.Clear();
        _byAvatar.Clear();
        _frozen = false;
    }

    void Update()
    {
        if (!_recording)
            return;

        // The wipe card is drawing what was recorded; nothing after this moment belongs on it.
        if (TeamWipeSequence.IsRunning)
        {
            if (!_frozen)
                FreezeForWipe();
            return;
        }

        float now = Time.time;
        if (now < _nextSampleTime)
            return;
        _nextSampleTime = now + SampleIntervalSeconds;

        bool refreshIdentity = now >= _nextIdentityRefresh;
        if (refreshIdentity)
            _nextIdentityRefresh = now + IdentityRefreshSeconds;

        NetworkManager nm = NetworkManager.Singleton;
        bool inSession = nm != null && nm.IsListening;
        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || !player.isActiveAndEnabled)
                continue;
            if (inSession && (!player.TryGetComponent(out NetworkObject networkObject) || !networkObject.IsSpawned))
                continue;

            if (!_byAvatar.TryGetValue(player, out Trail trail))
            {
                trail = new Trail { FirstSeenTime = now };
                _byAvatar[player] = trail;
                _trails.Add(trail);
                refreshIdentity = true;
            }

            if (refreshIdentity || trail.DisplayName == null)
                RefreshIdentity(player, trail);

            if (trail.EndedByDeath || now - trail.FirstSeenTime < SettleSeconds)
                continue;

            if (Sample(trail, player.transform.position, now - _sectionStartTime) && LeftFromTeleporter(trail))
                trail.TeleporterArrivals.Add(trail.Points.Count - 1);

            if (player.IsDead)
                trail.EndedByDeath = true;
        }
    }

    /// <summary>
    /// Whether the jump just appended started at a teleporter. Only the teleported player's own machine is told
    /// about a teleport (and the server, which moved them); everyone else just sees them reappear elsewhere. But
    /// using an orb means standing within its reach for the whole charge, and every peer has the orbs, so the
    /// spot a jump started from identifies it on every machine alike.
    /// </summary>
    static bool LeftFromTeleporter(Trail trail)
    {
        // The last point before the jump is within a sample step of where the player stood charging the orb.
        const float slack = MinStepMeters + 0.5f;
        Vector3 departure = trail.Points[trail.Points.Count - 2];
        foreach (TeleportOrb orb in FindObjectsByType<TeleportOrb>())
        {
            if (orb == null)
                continue;
            Vector3 offset = orb.transform.position - departure;
            offset.y = 0f;
            float reach = orb.InteractMaxDistance + slack;
            if (offset.sqrMagnitude <= reach * reach)
                return true;
        }

        return false;
    }

    /// <summary>
    /// A wipe means everyone still here is dead — including the player whose death set it off, who has not been
    /// sampled since (the wipe can begin inside that very damage call), and on a client possibly not even
    /// replicated as dead yet when the wipe message lands. So the closing pass takes every remaining avatar's
    /// final spot and ends its trail in a death without asking. An avatar that is already gone left the game,
    /// and its trail stays as it was.
    /// </summary>
    void FreezeForWipe()
    {
        _frozen = true;
        float time = Time.time - _sectionStartTime;
        foreach (KeyValuePair<PlayerHealth, Trail> pair in _byAvatar)
        {
            Trail trail = pair.Value;
            if (pair.Key == null || trail.EndedByDeath)
                continue;

            if (trail.Points.Count > 0 || Time.time - trail.FirstSeenTime >= SettleSeconds)
                Sample(trail, pair.Key.transform.position, time);
            trail.EndedByDeath = true;
        }
    }

    /// <summary>Records <paramref name="position"/> if it moved enough; true when it was appended as a jump.</summary>
    static bool Sample(Trail trail, Vector3 position, float time)
    {
        int count = trail.Points.Count;
        if (count == 0)
        {
            Append(trail, position, time, breakBefore: false);
            return false;
        }

        Vector3 last = trail.Points[count - 1];
        float dx = position.x - last.x;
        float dz = position.z - last.z;
        float distance = Mathf.Sqrt(dx * dx + dz * dz);
        float sinceLast = time - trail.LastSampleTime;
        trail.LastSampleTime = time;

        if (distance > Mathf.Max(MinBreakMeters, MaxPlausibleSpeed * sinceLast))
        {
            Append(trail, position, time, breakBefore: true);
            return true;
        }

        if (distance >= MinStepMeters)
            Append(trail, position, time, breakBefore: false);
        return false;
    }

    static void Append(Trail trail, Vector3 position, float time, bool breakBefore)
    {
        trail.Points.Add(position);
        trail.Times.Add(time);
        trail.BreakBefore.Add(breakBefore);
        trail.LastSampleTime = time;
    }

    /// <summary>
    /// Name and colour from the crew roster. The colour follows the lobby character slot (unique per player) so
    /// a player keeps it across sections; with no roster row it takes the first slot nobody else is using.
    /// </summary>
    void RefreshIdentity(PlayerHealth player, Trail trail)
    {
        trail.DisplayName = PlayerIdentity.DisplayName(player);

        int characterIndex = PlayerIdentity.CharacterIndex(player);
        int colors = MenuTheme.PlayerColors.Length;
        if (characterIndex >= 0)
        {
            trail.ColorIndex = characterIndex % colors;
            trail.ColorAssigned = true;
            return;
        }

        if (trail.ColorAssigned)
            return;

        trail.ColorAssigned = true;
        for (int candidate = 0; candidate < colors; candidate++)
        {
            bool taken = false;
            for (int i = 0; i < _trails.Count; i++)
            {
                if (_trails[i] != trail && _trails[i].ColorIndex == candidate)
                {
                    taken = true;
                    break;
                }
            }

            if (!taken)
            {
                trail.ColorIndex = candidate;
                return;
            }
        }
    }
}
