using System;
using UnityEngine;
using Steamworks;

[DisallowMultipleComponent]
[RequireComponent(typeof(SteamworksBootstrap))]
public class SteamLobbyService : MonoBehaviour
{
    const int MaxLobbyMembers = 4;
    const string HostSteamIdKey = "hostSteamId";
    const string GameNameKey = "game";

    public event Action<ulong> LobbyJoinRequested;
    public event Action<ulong, ulong> LobbyReadyToJoin;
    public event Action<string> StatusChanged;

    ulong _currentLobbyId;
    string _status = "Steam lobby service idle.";
    bool _waitingForClientLobbyJoin;

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

    void OnDisable()
    {
        LeaveLobby();
        UnregisterCallbacks();
    }

    public bool CreateLobbyForCurrentHost()
    {
        if (!SteamworksBootstrap.IsReady)
        {
            UpdateStatus(SteamworksBootstrap.Status);
            return false;
        }

        RegisterCallbacks();
        _waitingForClientLobbyJoin = false;
        SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, MaxLobbyMembers);
        UpdateStatus("Creating Steam friends-only lobby...");
        return true;
    }

    public bool JoinLobby(ulong lobbyId)
    {
        if (!SteamworksBootstrap.IsReady)
        {
            UpdateStatus(SteamworksBootstrap.Status);
            return false;
        }

        if (lobbyId == 0UL)
        {
            UpdateStatus("Enter a Steam lobby ID before joining.");
            return false;
        }

        RegisterCallbacks();
        _waitingForClientLobbyJoin = true;
        SteamMatchmaking.JoinLobby(new CSteamID(lobbyId));
        UpdateStatus($"Joining Steam lobby {lobbyId}...");
        return true;
    }

    public void LeaveLobby()
    {
        if (_currentLobbyId != 0UL && SteamworksBootstrap.IsReady)
            SteamMatchmaking.LeaveLobby(new CSteamID(_currentLobbyId));
        _currentLobbyId = 0UL;
        _waitingForClientLobbyJoin = false;
    }

    public bool OpenInviteDialog()
    {
        if (!SteamworksBootstrap.IsReady)
        {
            UpdateStatus(SteamworksBootstrap.Status);
            return false;
        }

        if (_currentLobbyId == 0UL)
        {
            UpdateStatus("Create a Steam lobby before inviting friends.");
            return false;
        }

        SteamFriends.ActivateGameOverlayInviteDialog(new CSteamID(_currentLobbyId));
        UpdateStatus("Steam invite overlay opened.");
        return true;
    }

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
            UpdateStatus($"Steam lobby creation failed: {result.m_eResult}.");
            return;
        }

        _currentLobbyId = result.m_ulSteamIDLobby;
        CSteamID lobbyId = new CSteamID(_currentLobbyId);
        SteamMatchmaking.SetLobbyData(lobbyId, HostSteamIdKey, SteamworksBootstrap.LocalSteamId.ToString());
        SteamMatchmaking.SetLobbyData(lobbyId, GameNameKey, Application.productName);
        SteamMatchmaking.SetLobbyJoinable(lobbyId, true);
        UpdateStatus($"Steam lobby ready. Lobby ID: {_currentLobbyId}");
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
            UpdateStatus("Steam lobby is missing host connection data.");
            return;
        }

        UpdateStatus($"Steam lobby joined. Connecting to host {hostSteamId}...");
        LobbyReadyToJoin?.Invoke(_currentLobbyId, hostSteamId);
    }

    void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t result)
    {
        ulong lobbyId = result.m_steamIDLobby.m_SteamID;
        UpdateStatus($"Steam invite accepted for lobby {lobbyId}.");
        LobbyJoinRequested?.Invoke(lobbyId);
    }

    void UpdateStatus(string message)
    {
        _status = message;
        StatusChanged?.Invoke(_status);
        Debug.Log($"[Steam Lobby] {_status}", this);
    }
}
