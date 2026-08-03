using System;
using System.Collections.Generic;
using UnityEngine;
using Steamworks;

/// <summary>
/// Owns the friends-only lobby the host opens and every way a player can land in one: an invite
/// accepted while the game is running, or an invite accepted while it is closed (which relaunches
/// the game with <c>+connect_lobby</c>). Invites are sent through the matchmaking API rather than
/// the game overlay, because the overlay is only injected into processes Steam itself launched —
/// it never appears in the Unity Editor. Going through the API keeps inviting identical in-editor
/// and in a build.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(SteamworksBootstrap))]
public class SteamLobbyService : MonoBehaviour
{
    const int MaxLobbyMembers = 4;
    const string HostSteamIdKey = "hostSteamId";
    const string GameNameKey = "game";
    const string ConnectLobbyArgument = "+connect_lobby";
    const string OnlineUnavailableStatus = "Online play is unavailable right now.";
    const string InviteExpiredStatus = "That invite is no longer valid.";

    public event Action<ulong> LobbyJoinRequested;
    public event Action<ulong, ulong> LobbyReadyToJoin;
    public event Action<string> StatusChanged;

    ulong _currentLobbyId;
    string _status = string.Empty;
    bool _waitingForClientLobbyJoin;
    bool _launchArgumentsChecked;

    Callback<LobbyCreated_t> _lobbyCreatedCallback;
    Callback<LobbyEnter_t> _lobbyEnterCallback;
    Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;

    public ulong CurrentLobbyId => _currentLobbyId;
    public bool HasLobby => _currentLobbyId != 0UL;
    public string CurrentStatus => _status;

    void OnEnable()
    {
        RegisterCallbacks();
    }

    void Start()
    {
        TryJoinFromLaunchArguments();
    }

    void OnDisable()
    {
        LeaveLobby();
        UnregisterCallbacks();
    }

    public bool CreateLobbyForCurrentHost()
    {
        if (!SteamworksBootstrap.IsReady)
        {
            ReportUnavailable();
            return false;
        }

        RegisterCallbacks();
        _waitingForClientLobbyJoin = false;
        SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxLobbyMembers);
        UpdateStatus("Opening a private session...");
        return true;
    }

    public bool JoinLobby(ulong lobbyId)
    {
        if (!SteamworksBootstrap.IsReady)
        {
            ReportUnavailable();
            return false;
        }

        if (lobbyId == 0UL)
        {
            UpdateStatus(InviteExpiredStatus);
            return false;
        }

        RegisterCallbacks();
        _waitingForClientLobbyJoin = true;
        SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
        UpdateStatus("Joining your friend's session...");
        return true;
    }

    public void LeaveLobby()
    {
        if (_currentLobbyId != 0UL && SteamworksBootstrap.IsReady)
            SteamMatchmaking.LeaveLobby(new CSteamID(_currentLobbyId));
        _currentLobbyId = 0UL;
        _waitingForClientLobbyJoin = false;
    }

    // ---------------------------------------------------------------- invites

    /// <summary>
    /// Fills <paramref name="results"/> with the friends who are logged in right now, the ones
    /// already in this game first. Offline friends are skipped: the lobby is gone long before they
    /// would see the invite.
    /// </summary>
    public bool TryGetFriends(List<OnlineFriend> results)
    {
        if (results == null)
            return false;

        results.Clear();
        if (!SteamworksBootstrap.IsReady)
            return false;

        uint appId = SteamUtils.GetAppID().m_AppId;
        int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (int i = 0; i < count; i++)
        {
            CSteamID friendId = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (SteamFriends.GetFriendPersonaState(friendId) == EPersonaState.k_EPersonaStateOffline)
                continue;

            bool inThisGame = SteamFriends.GetFriendGamePlayed(friendId, out FriendGameInfo_t gameInfo)
                && gameInfo.m_gameID.AppID().m_AppId == appId;

            results.Add(new OnlineFriend(friendId.m_SteamID, SteamFriends.GetFriendPersonaName(friendId), inThisGame));
        }

        results.Sort(CompareFriends);
        return true;
    }

    static int CompareFriends(OnlineFriend a, OnlineFriend b)
    {
        if (a.InThisGame != b.InThisGame)
            return a.InThisGame ? -1 : 1;
        return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// Sends a direct lobby invite. The friend gets it as a Steam notification with a join button;
    /// accepting routes them back here through <see cref="OnGameLobbyJoinRequested"/> (game open)
    /// or <see cref="TryJoinFromLaunchArguments"/> (game closed).
    /// </summary>
    public bool InviteFriend(ulong friendUserId)
    {
        if (!SteamworksBootstrap.IsReady)
        {
            ReportUnavailable();
            return false;
        }

        if (_currentLobbyId == 0UL)
        {
            UpdateStatus("Host a game before inviting friends.");
            return false;
        }

        if (friendUserId == 0UL)
            return false;

        bool sent = SteamMatchmaking.InviteUserToLobby(new CSteamID(_currentLobbyId), new CSteamID(friendUserId));
        UpdateStatus(sent ? "Invite sent." : "Could not send that invite.");
        return sent;
    }

    /// <summary>
    /// A friend who accepts an invite while the game is closed relaunches it with
    /// <c>+connect_lobby &lt;id&gt;</c>. Reading that argument on the first frame is what lets an
    /// invite carry a player all the way into the lobby without anyone typing an id.
    /// </summary>
    void TryJoinFromLaunchArguments()
    {
        if (_launchArgumentsChecked || !SteamworksBootstrap.IsReady)
            return;

        _launchArgumentsChecked = true;

        ulong lobbyId = ParseConnectLobbyId(Environment.GetCommandLineArgs());
        if (lobbyId == 0UL && SteamApps.GetLaunchCommandLine(out string launchLine, 1024) > 0)
            lobbyId = ParseConnectLobbyId((launchLine ?? string.Empty).Split(' '));

        if (lobbyId == 0UL)
            return;

        LobbyJoinRequested?.Invoke(lobbyId);
    }

    static ulong ParseConnectLobbyId(string[] arguments)
    {
        if (arguments == null)
            return 0UL;

        for (int i = 0; i < arguments.Length - 1; i++)
        {
            if (!string.Equals(arguments[i], ConnectLobbyArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ulong.TryParse(arguments[i + 1], out ulong lobbyId))
                return lobbyId;
        }

        return 0UL;
    }

    // ---------------------------------------------------------------- callbacks

    void RegisterCallbacks()
    {
        if (!SteamworksBootstrap.IsReady)
            return;

        _lobbyCreatedCallback ??= Callback<LobbyCreated_t>.Create(OnLobbyCreated);
        _lobbyEnterCallback ??= Callback<LobbyEnter_t>.Create(OnLobbyEntered);
        _gameLobbyJoinRequestedCallback ??= Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
    }

    void UnregisterCallbacks()
    {
        _lobbyCreatedCallback?.Dispose();
        _lobbyEnterCallback?.Dispose();
        _gameLobbyJoinRequestedCallback?.Dispose();
        _lobbyCreatedCallback = null;
        _lobbyEnterCallback = null;
        _gameLobbyJoinRequestedCallback = null;
    }

    void OnLobbyCreated(LobbyCreated_t result)
    {
        if (result.m_eResult != EResult.k_EResultOK)
        {
            UpdateStatus($"Could not open the session ({StripResultPrefix(result.m_eResult)}).");
            return;
        }

        _currentLobbyId = result.m_ulSteamIDLobby;
        CSteamID lobbyId = new CSteamID(_currentLobbyId);
        SteamMatchmaking.SetLobbyData(lobbyId, HostSteamIdKey, SteamworksBootstrap.LocalSteamId.ToString());
        SteamMatchmaking.SetLobbyData(lobbyId, GameNameKey, Application.productName);
        SteamMatchmaking.SetLobbyJoinable(lobbyId, true);
        UpdateStatus("Session ready. Invite friends to join.");
    }

    void OnLobbyEntered(LobbyEnter_t result)
    {
        _currentLobbyId = result.m_ulSteamIDLobby;

        if (!_waitingForClientLobbyJoin)
            return;

        _waitingForClientLobbyJoin = false;
        string hostIdText = SteamMatchmaking.GetLobbyData(new CSteamID(_currentLobbyId), HostSteamIdKey);
        if (!ulong.TryParse(hostIdText, out ulong hostSteamId) || hostSteamId == 0UL)
        {
            UpdateStatus(InviteExpiredStatus);
            return;
        }

        UpdateStatus("Connecting to the host...");
        LobbyReadyToJoin?.Invoke(_currentLobbyId, hostSteamId);
    }

    void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t result)
    {
        UpdateStatus("Invite accepted. Joining...");
        LobbyJoinRequested?.Invoke(result.m_steamIDLobby.m_SteamID);
    }

    // ---------------------------------------------------------------- status

    static string StripResultPrefix(EResult result)
    {
        return result.ToString().Replace("k_EResult", string.Empty);
    }

    /// <summary>The menu only ever sees the neutral line; the platform's own wording stays in the console.</summary>
    void ReportUnavailable()
    {
        Debug.LogWarning($"[Online] {SteamworksBootstrap.Status}", this);
        UpdateStatus(OnlineUnavailableStatus);
    }

    void UpdateStatus(string message)
    {
        _status = message;
        StatusChanged?.Invoke(_status);
        Debug.Log($"[Online] {_status}", this);
    }
}
