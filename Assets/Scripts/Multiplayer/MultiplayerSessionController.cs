using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;
using Netcode.Transports;

public enum MultiplayerTransportMode
{
    DirectIp,
    SteamP2P
}

public readonly struct LobbyPlayerState
{
    public LobbyPlayerState(ulong clientId, bool isReady, bool isHost)
    {
        ClientId = clientId;
        IsReady = isReady;
        IsHost = isHost;
    }

    public ulong ClientId { get; }
    public bool IsReady { get; }
    public bool IsHost { get; }
}

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkManager))]
[RequireComponent(typeof(UnityTransport))]
public class MultiplayerSessionController : MonoBehaviour
{
    const string HostLoopbackAddress = "127.0.0.1";
    const string HostListenAddress = "0.0.0.0";
    const string LobbyReadyRequestMessageName = "lobby-ready-request";
    const string LobbyStateMessageName = "lobby-state";

    [SerializeField] string defaultAddress = "127.0.0.1";
    [SerializeField] ushort defaultPort = 7777;

    NetworkManager _networkManager;
    UnityTransport _unityTransport;
    SteamLobbyService _steamLobby;
    SteamNetworkingSocketsTransport _steamTransport;
    MultiplayerProjectSettings _projectSettings;
    GameObject _playerPrefab;
    string _status = "Multiplayer foundation ready. F8 toggles the debug menu.";
    MultiplayerTransportMode _transportMode = MultiplayerTransportMode.DirectIp;
    bool _playerPrefabConfigured;
    Vector3 _levelStartSpawnPosition;
    Quaternion _levelStartSpawnRotation = Quaternion.identity;
    bool _hasLevelStartSpawn;
    readonly Dictionary<ulong, Coroutine> _pendingSpawnMoves = new();
    readonly Dictionary<ulong, bool> _serverLobbyReadyByClient = new();
    readonly List<LobbyPlayerState> _lobbyPlayers = new();
    bool _lobbyMessageHandlersRegistered;
    bool _lobbyReadyRequestHandlerRegistered;
    bool _localReady;
    bool _allLobbyPlayersReady;
    bool _gameStartRequested;

    public event Action<string> StatusChanged;
    public event Action LobbyStateChanged;

    public string DefaultAddress => defaultAddress;
    public ushort DefaultPort => defaultPort;
    public string CurrentStatus => _status;
    public MultiplayerTransportMode CurrentTransportMode => _transportMode;
    public string CurrentTransportLabel => _transportMode == MultiplayerTransportMode.SteamP2P ? "Steam P2P" : "Direct IP";
    public bool IsSteamReady => SteamworksBootstrap.IsReady && IsSteamTransportAvailable;
    public string SteamStatus => SteamworksBootstrap.Status;
    public ulong LocalSteamId => SteamworksBootstrap.LocalSteamId;
    public string LocalSteamName => SteamworksBootstrap.LocalPersonaName;
    public ulong CurrentSteamLobbyId => _steamLobby != null ? _steamLobby.CurrentLobbyId : 0UL;
    public bool IsSessionActive => _networkManager != null && _networkManager.IsListening;
    public bool IsLobbyHost => _networkManager != null && _networkManager.IsHost;
    public bool IsLocalReady => _localReady;
    public bool AreAllLobbyPlayersReady => _allLobbyPlayersReady;
    public bool CanHostStartGame => IsLobbyHost && _lobbyPlayers.Count > 0 && _allLobbyPlayersReady && !_gameStartRequested;
    public IReadOnlyList<LobbyPlayerState> LobbyPlayers => _lobbyPlayers;

    bool IsSteamTransportAvailable => _steamTransport != null;

    void Awake()
    {
        _networkManager = GetComponent<NetworkManager>();
        _unityTransport = GetComponent<UnityTransport>();
        _steamLobby = GetComponent<SteamLobbyService>();
        _steamTransport = GetComponent<SteamNetworkingSocketsTransport>();
        EnsureNetworkConfig(_unityTransport);
        ConfigureDirectClientTransport(defaultAddress, defaultPort);
        ConfigurePlayerPrefab();
    }

    void OnEnable()
    {
        if (_networkManager == null)
            return;

        _networkManager.OnServerStarted += HandleServerStarted;
        _networkManager.OnClientConnectedCallback += HandleClientConnected;
        _networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        if (_steamLobby == null)
            _steamLobby = GetComponent<SteamLobbyService>();
        if (_steamLobby != null)
        {
            _steamLobby.LobbyReadyToJoin += HandleSteamLobbyReadyToJoin;
            _steamLobby.StatusChanged += HandleSteamLobbyStatusChanged;
        }
    }

    void OnDisable()
    {
        if (_networkManager == null)
            return;

        _networkManager.OnServerStarted -= HandleServerStarted;
        _networkManager.OnClientConnectedCallback -= HandleClientConnected;
        _networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        UnregisterLobbyMessageHandlers();

        if (_steamLobby != null)
        {
            _steamLobby.LobbyReadyToJoin -= HandleSteamLobbyReadyToJoin;
            _steamLobby.StatusChanged -= HandleSteamLobbyStatusChanged;
        }
    }

    public void StartHost(ushort? portOverride = null)
    {
        StartDirectIpHost(portOverride);
    }

    public void StartClient(string address, ushort port)
    {
        StartDirectIpClient(address, port);
    }

    public void StartDirectIpHost(ushort? portOverride = null)
    {
        if (_networkManager == null || _unityTransport == null)
        {
            UpdateStatus("NetworkManager is not ready yet.");
            return;
        }

        if (_networkManager.IsListening)
        {
            UpdateStatus("A session is already running.");
            return;
        }

        ushort port = portOverride ?? defaultPort;
        defaultPort = port;

        SelectDirectIpTransport();
        ConfigurePlayerPrefab();
        ConfigureDirectHostTransport(port);
        bool started = _networkManager.StartHost();
        if (started)
            EnsureLobbyMessageHandlersRegistered();
        UpdateStatus(started
            ? $"Direct IP lobby started on port {port}. Ready up, then host can start."
            : "Host start failed. Check the Unity console for details.");
    }

    public void StartDirectIpClient(string address, ushort port)
    {
        if (_networkManager == null || _unityTransport == null)
        {
            UpdateStatus("NetworkManager is not ready yet.");
            return;
        }

        if (_networkManager.IsListening)
        {
            UpdateStatus("A session is already running.");
            return;
        }

        defaultAddress = string.IsNullOrWhiteSpace(address) ? DefaultAddress : address.Trim();
        defaultPort = port;

        SelectDirectIpTransport();
        ConfigurePlayerPrefab();
        ConfigureDirectClientTransport(defaultAddress, defaultPort);
        bool started = _networkManager.StartClient();
        if (started)
            EnsureLobbyMessageHandlersRegistered();
        UpdateStatus(started
            ? $"Joining lobby at {defaultAddress}:{defaultPort}..."
            : "Client start failed. Check the Unity console for details.");
    }

    public void StartSteamHost()
    {
        if (_networkManager == null)
        {
            UpdateStatus("NetworkManager is not ready yet.");
            return;
        }

        if (_networkManager.IsListening)
        {
            UpdateStatus("A session is already running.");
            return;
        }

        if (!SelectSteamTransport())
            return;

        ConfigurePlayerPrefab();
        bool started = _networkManager.StartHost();
        if (started)
            EnsureLobbyMessageHandlersRegistered();
        if (started)
        {
            bool lobbyCreateStarted = _steamLobby != null && _steamLobby.CreateLobbyForCurrentHost();
            string lobbyMessage = lobbyCreateStarted ? " Creating Steam lobby..." : " Steam lobby was not created.";
            UpdateStatus($"Steam lobby host started. Share Steam ID {LocalSteamId}.{lobbyMessage}");
        }
        else
        {
            UpdateStatus("Steam host start failed. Check the Unity console for details.");
        }
    }

    public void StartSteamClient(ulong hostSteamId)
    {
        if (_networkManager == null)
        {
            UpdateStatus("NetworkManager is not ready yet.");
            return;
        }

        if (_networkManager.IsListening)
        {
            UpdateStatus("A session is already running.");
            return;
        }

        if (hostSteamId == 0UL)
        {
            UpdateStatus("Enter a valid host SteamID64 before joining.");
            return;
        }

        if (!SelectSteamTransport())
            return;

        _steamTransport.ConnectToSteamID = hostSteamId;
        ConfigurePlayerPrefab();
        bool started = _networkManager.StartClient();
        if (started)
            EnsureLobbyMessageHandlersRegistered();
        UpdateStatus(started
            ? $"Joining Steam lobby host {hostSteamId}..."
            : "Steam client start failed. Check the Unity console for details.");
    }

    public void JoinSteamLobby(ulong lobbyId)
    {
        if (_steamLobby == null)
        {
            UpdateStatus("Steam lobby service is not ready.");
            return;
        }

        _steamLobby.JoinLobby(lobbyId);
    }

    public void OpenSteamInviteDialog()
    {
        if (_steamLobby == null)
        {
            UpdateStatus("Steam lobby service is not ready.");
            return;
        }

        _steamLobby.OpenInviteDialog();
    }

    public void ShutdownSession()
    {
        if (_networkManager == null || !_networkManager.IsListening)
        {
            UpdateStatus("No active session to stop.");
            return;
        }

        CancelAllPendingSpawnMoves();
        _steamLobby?.LeaveLobby();
        ClearLobbyState();
        UnregisterLobbyMessageHandlers();
        ProximityVoiceSession.InvalidateProximityMessaging();
        _networkManager.Shutdown();
        SelectDirectIpTransport();
        ConfigureDirectClientTransport(defaultAddress, defaultPort);
        UpdateStatus("Session stopped.");
    }

    public void SetLocalPlayerReady(bool ready)
    {
        if (_networkManager == null || !_networkManager.IsListening)
        {
            UpdateStatus("Join or host a lobby before readying up.");
            return;
        }

        if (_gameStartRequested)
            return;

        _localReady = ready;
        LobbyStateChanged?.Invoke();

        if (_networkManager.IsServer)
        {
            SetServerLobbyReady(_networkManager.LocalClientId, ready);
            return;
        }

        SendReadyRequest(ready);
    }

    public void StartGameFromLobby()
    {
        if (_networkManager == null || !_networkManager.IsHost)
        {
            UpdateStatus("Only the host can start the game.");
            return;
        }

        if (!_allLobbyPlayersReady || _lobbyPlayers.Count == 0)
        {
            UpdateStatus("Everyone must be ready before the host can start.");
            return;
        }

        if (_networkManager.SceneManager == null)
        {
            UpdateStatus("Netcode scene management is not available.");
            return;
        }

        _gameStartRequested = true;
        LobbyStateChanged?.Invoke();
        SceneEventProgressStatus status = _networkManager.SceneManager.LoadScene(
            MultiplayerSceneFlow.GameSceneName,
            LoadSceneMode.Single);

        if (status == SceneEventProgressStatus.Started)
        {
            UpdateStatus($"Starting {MultiplayerSceneFlow.GameSceneName} for all ready players...");
            return;
        }

        _gameStartRequested = false;
        LobbyStateChanged?.Invoke();
        UpdateStatus($"Could not start game scene load: {status}.");
    }

    void CancelAllPendingSpawnMoves()
    {
        foreach (ulong clientId in new List<ulong>(_pendingSpawnMoves.Keys))
            CancelPendingSpawnMove(clientId);
    }

    void SelectDirectIpTransport()
    {
        _transportMode = MultiplayerTransportMode.DirectIp;
        EnsureNetworkConfig(_unityTransport);
    }

    bool SelectSteamTransport()
    {
        if (!SteamworksBootstrap.IsReady)
        {
            UpdateStatus(SteamworksBootstrap.Status);
            return false;
        }

        if (_steamTransport == null)
            _steamTransport = GetComponent<SteamNetworkingSocketsTransport>();
        if (_steamTransport == null)
        {
            UpdateStatus("Steam Networking Sockets transport is missing.");
            return false;
        }

        _transportMode = MultiplayerTransportMode.SteamP2P;
        EnsureNetworkConfig(_steamTransport);
        return true;
    }

    void ConfigureDirectHostTransport(ushort port)
    {
        EnsureNetworkConfig(_unityTransport);
        _unityTransport.SetConnectionData(HostLoopbackAddress, port, HostListenAddress);
    }

    void ConfigureDirectClientTransport(string address, ushort port)
    {
        EnsureNetworkConfig(_unityTransport);
        _unityTransport.SetConnectionData(address, port);
    }

    void ConfigurePlayerPrefab()
    {
        EnsureNetworkConfig();
        if (_playerPrefabConfigured || _networkManager == null || _networkManager.NetworkConfig == null)
            return;

        _projectSettings ??= Resources.Load<MultiplayerProjectSettings>("MultiplayerProjectSettings");
        if (_projectSettings == null || _projectSettings.PlayerPrefab == null)
        {
            Debug.LogWarning("[Multiplayer] MultiplayerProjectSettings asset is missing or has no player prefab assigned.", this);
            return;
        }

        _playerPrefab = _projectSettings.PlayerPrefab;
        _levelStartSpawnPosition = _projectSettings.LevelStartPosition;
        _levelStartSpawnRotation = _projectSettings.LevelStartRotation;
        _hasLevelStartSpawn = true;

        _networkManager.NetworkConfig.PlayerPrefab = null;
        _networkManager.AddNetworkPrefab(_playerPrefab);
        _playerPrefabConfigured = true;
    }

    void EnsureNetworkConfig(NetworkTransport transport = null)
    {
        if (_networkManager == null)
            return;

        if (_networkManager.NetworkConfig == null)
            _networkManager.NetworkConfig = new NetworkConfig();

        if (_networkManager.NetworkConfig.Prefabs == null)
            _networkManager.NetworkConfig.Prefabs = new NetworkPrefabs();

        if (_networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists == null)
            _networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists = new List<NetworkPrefabsList>();

        _networkManager.NetworkConfig.EnableSceneManagement = true;

        if (transport != null)
            _networkManager.NetworkConfig.NetworkTransport = transport;
    }

    void HandleServerStarted()
    {
        if (_networkManager == null)
            return;

        EnsureLobbyMessageHandlersRegistered();
        ResetServerLobbyState();

        if (!_networkManager.IsHost)
            return;

        if (_transportMode == MultiplayerTransportMode.SteamP2P)
            UpdateStatus($"Steam host session active. Steam ID: {LocalSteamId}.");
        else
            UpdateStatus($"Direct IP host session active on port {defaultPort}.");
    }

    void HandleClientConnected(ulong clientId)
    {
        if (_networkManager == null)
            return;

        if (_networkManager.IsHost && clientId == _networkManager.LocalClientId)
        {
            RegisterLobbyClient(clientId);
            QueueSpawnOrMovePlayerToLevelStart(clientId);
            UpdateStatus(_transportMode == MultiplayerTransportMode.SteamP2P
                ? $"Steam host client connected locally. Steam ID: {LocalSteamId}."
                : $"Host client connected locally on port {defaultPort}.");
            return;
        }

        if (_networkManager.IsServer)
        {
            RegisterLobbyClient(clientId);
            QueueSpawnOrMovePlayerToLevelStart(clientId);
            UpdateStatus($"Client {clientId} connected.");
            return;
        }

        if (clientId == _networkManager.LocalClientId)
        {
            EnsureLobbyMessageHandlersRegistered();
            UpdateStatus(_transportMode == MultiplayerTransportMode.SteamP2P
                ? "Connected to Steam lobby host. Ready up when everyone is in."
                : $"Connected to lobby at {defaultAddress}:{defaultPort}. Ready up when everyone is in.");
        }
    }

    void HandleClientDisconnected(ulong clientId)
    {
        if (_networkManager == null)
            return;

        CancelPendingSpawnMove(clientId);

        if (clientId == _networkManager.LocalClientId && !_networkManager.IsServer)
        {
            ClearLobbyState();
            UpdateStatus("Disconnected from host.");
            return;
        }

        if (_networkManager.IsServer)
        {
            UnregisterLobbyClient(clientId);
            UpdateStatus($"Client {clientId} disconnected.");
        }
    }

    void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        if (scene.name != MultiplayerSceneFlow.GameSceneName)
            return;

        QueueSpawnOrMoveAllPlayersToLevelStart();
    }

    void UpdateStatus(string message)
    {
        _status = message;
        StatusChanged?.Invoke(_status);
        Debug.Log($"[Multiplayer] {_status}", this);
    }

    void HandleSteamLobbyReadyToJoin(ulong lobbyId, ulong hostSteamId)
    {
        if (_networkManager != null && _networkManager.IsListening)
            return;

        StartSteamClient(hostSteamId);
    }

    void HandleSteamLobbyStatusChanged(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            UpdateStatus(message);
    }

    void EnsureLobbyMessageHandlersRegistered()
    {
        if (_lobbyMessageHandlersRegistered || _networkManager == null || _networkManager.CustomMessagingManager == null)
            return;

        if (_networkManager.IsServer)
        {
            _networkManager.CustomMessagingManager.RegisterNamedMessageHandler(LobbyReadyRequestMessageName, HandleLobbyReadyRequest);
            _lobbyReadyRequestHandlerRegistered = true;
        }

        _networkManager.CustomMessagingManager.RegisterNamedMessageHandler(LobbyStateMessageName, HandleLobbyStateMessage);
        _lobbyMessageHandlersRegistered = true;
    }

    void UnregisterLobbyMessageHandlers()
    {
        if (!_lobbyMessageHandlersRegistered || _networkManager == null || _networkManager.CustomMessagingManager == null)
        {
            _lobbyMessageHandlersRegistered = false;
            _lobbyReadyRequestHandlerRegistered = false;
            return;
        }

        if (_lobbyReadyRequestHandlerRegistered)
            _networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(LobbyReadyRequestMessageName);
        _networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(LobbyStateMessageName);
        _lobbyMessageHandlersRegistered = false;
        _lobbyReadyRequestHandlerRegistered = false;
    }

    void HandleLobbyReadyRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        reader.ReadValueSafe(out byte readyByte);
        SetServerLobbyReady(senderClientId, readyByte != 0);
    }

    void HandleLobbyStateMessage(ulong senderClientId, FastBufferReader reader)
    {
        if (_networkManager == null || _networkManager.IsServer)
            return;

        reader.ReadValueSafe(out int playerCount);
        _lobbyPlayers.Clear();
        bool allReady = playerCount > 0;
        _localReady = false;

        for (int i = 0; i < playerCount; i++)
        {
            reader.ReadValueSafe(out ulong clientId);
            reader.ReadValueSafe(out byte readyByte);
            reader.ReadValueSafe(out byte hostByte);

            bool isReady = readyByte != 0;
            _lobbyPlayers.Add(new LobbyPlayerState(clientId, isReady, hostByte != 0));
            allReady &= isReady;

            if (clientId == _networkManager.LocalClientId)
                _localReady = isReady;
        }

        _allLobbyPlayersReady = allReady;
        LobbyStateChanged?.Invoke();
    }

    void SendReadyRequest(bool ready)
    {
        if (_networkManager == null || _networkManager.CustomMessagingManager == null)
            return;

        using FastBufferWriter writer = new(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe((byte)(ready ? 1 : 0));
        _networkManager.CustomMessagingManager.SendNamedMessage(
            LobbyReadyRequestMessageName,
            NetworkManager.ServerClientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    void ResetServerLobbyState()
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        _serverLobbyReadyByClient.Clear();
        foreach (ulong clientId in _networkManager.ConnectedClientsIds)
            _serverLobbyReadyByClient[clientId] = false;

        _localReady = false;
        _gameStartRequested = false;
        PublishServerLobbyState();
    }

    void RegisterLobbyClient(ulong clientId)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        if (!_serverLobbyReadyByClient.ContainsKey(clientId))
            _serverLobbyReadyByClient[clientId] = false;

        PublishServerLobbyState();
    }

    void UnregisterLobbyClient(ulong clientId)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        _serverLobbyReadyByClient.Remove(clientId);
        PublishServerLobbyState();
    }

    void SetServerLobbyReady(ulong clientId, bool ready)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        _serverLobbyReadyByClient[clientId] = ready;
        PublishServerLobbyState();
    }

    void PublishServerLobbyState()
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        _lobbyPlayers.Clear();
        bool allReady = _serverLobbyReadyByClient.Count > 0;
        foreach (KeyValuePair<ulong, bool> pair in _serverLobbyReadyByClient)
        {
            bool isHost = pair.Key == _networkManager.LocalClientId;
            _lobbyPlayers.Add(new LobbyPlayerState(pair.Key, pair.Value, isHost));
            allReady &= pair.Value;
        }

        _localReady = _serverLobbyReadyByClient.TryGetValue(_networkManager.LocalClientId, out bool hostReady) && hostReady;
        _allLobbyPlayersReady = allReady;
        LobbyStateChanged?.Invoke();
        SendLobbyStateToClients();
    }

    void SendLobbyStateToClients()
    {
        if (_networkManager == null || _networkManager.CustomMessagingManager == null || !_networkManager.IsServer)
            return;

        int payloadSize = sizeof(int) + _lobbyPlayers.Count * (sizeof(ulong) + sizeof(byte) + sizeof(byte));
        using FastBufferWriter writer = new(payloadSize, Allocator.Temp);
        writer.WriteValueSafe(_lobbyPlayers.Count);
        for (int i = 0; i < _lobbyPlayers.Count; i++)
        {
            LobbyPlayerState player = _lobbyPlayers[i];
            writer.WriteValueSafe(player.ClientId);
            writer.WriteValueSafe((byte)(player.IsReady ? 1 : 0));
            writer.WriteValueSafe((byte)(player.IsHost ? 1 : 0));
        }

        foreach (ulong clientId in _networkManager.ConnectedClientsIds)
        {
            if (clientId == _networkManager.LocalClientId)
                continue;

            _networkManager.CustomMessagingManager.SendNamedMessage(
                LobbyStateMessageName,
                clientId,
                writer,
                NetworkDelivery.ReliableSequenced);
        }
    }

    void ClearLobbyState()
    {
        _serverLobbyReadyByClient.Clear();
        _lobbyPlayers.Clear();
        _localReady = false;
        _allLobbyPlayersReady = false;
        _gameStartRequested = false;
        LobbyStateChanged?.Invoke();
    }

    void QueueSpawnOrMovePlayerToLevelStart(ulong clientId)
    {
        if (!_hasLevelStartSpawn || _networkManager == null || !_networkManager.IsServer)
            return;

        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid() || activeScene.name != MultiplayerSceneFlow.GameSceneName)
            return;

        if (_pendingSpawnMoves.ContainsKey(clientId))
            return;

        Coroutine routine = StartCoroutine(WaitAndSpawnOrMovePlayerToLevelStart(clientId));
        _pendingSpawnMoves[clientId] = routine;
    }

    void QueueSpawnOrMoveAllPlayersToLevelStart()
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        foreach (ulong clientId in _networkManager.ConnectedClientsIds)
            QueueSpawnOrMovePlayerToLevelStart(clientId);
    }

    IEnumerator WaitAndSpawnOrMovePlayerToLevelStart(ulong clientId)
    {
        const float timeoutSeconds = 5f;
        float elapsed = 0f;

        while (elapsed < timeoutSeconds)
        {
            bool allowProjectSettingsFallback = elapsed >= timeoutSeconds - Time.deltaTime;
            if (TrySpawnOrMovePlayerToLevelStart(clientId, allowProjectSettingsFallback))
                break;

            yield return null;
            elapsed += Time.deltaTime;
        }

        _pendingSpawnMoves.Remove(clientId);
    }

    void CancelPendingSpawnMove(ulong clientId)
    {
        if (!_pendingSpawnMoves.TryGetValue(clientId, out Coroutine routine))
            return;

        if (routine != null)
            StopCoroutine(routine);

        _pendingSpawnMoves.Remove(clientId);
    }

    bool TrySpawnOrMovePlayerToLevelStart(ulong clientId, bool allowProjectSettingsFallback)
    {
        if (!_hasLevelStartSpawn || _networkManager == null || !_networkManager.IsServer)
            return false;

        if (!_networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client))
            return false;

        if (!TryGetLevelStartSpawn(allowProjectSettingsFallback, out Vector3 spawnPosition, out Quaternion spawnRotation))
            return false;

        NetworkObject playerObject = client.PlayerObject;
        if (playerObject == null)
        {
            if (_playerPrefab == null)
                return false;

            GameObject playerInstance = Instantiate(_playerPrefab, spawnPosition, spawnRotation);
            playerObject = playerInstance.GetComponent<NetworkObject>();
            if (playerObject == null)
            {
                Debug.LogError("[Multiplayer] Player prefab must have a NetworkObject to spawn from the lobby.", this);
                Destroy(playerInstance);
                return false;
            }

            playerObject.SpawnAsPlayerObject(clientId, true);
        }
        else
        {
            playerObject.transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        }

        NetworkPlayerRespawn playerRespawn = playerObject.GetComponent<NetworkPlayerRespawn>();
        if (playerRespawn != null)
            playerRespawn.ApplyInitialSpawn(spawnPosition, spawnRotation);
        else
            playerObject.transform.SetPositionAndRotation(spawnPosition, spawnRotation);

        return true;
    }

    bool TryGetLevelStartSpawn(bool allowProjectSettingsFallback, out Vector3 position, out Quaternion rotation)
    {
        if (MultiplayerSpawnRegistry.Instance != null)
        {
            MultiplayerSpawnRegistry.Instance.RefreshSpawnPoints();
            if (MultiplayerSpawnRegistry.Instance.TryGetInitialJoinSpawn(out position, out rotation))
                return true;
        }

        if (allowProjectSettingsFallback)
        {
            position = _levelStartSpawnPosition;
            rotation = _levelStartSpawnRotation;
            return true;
        }

        position = default;
        rotation = default;
        return false;
    }
}
