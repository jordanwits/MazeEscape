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
    public LobbyPlayerState(ulong clientId, bool isReady, bool isHost, int characterIndex, string displayName)
    {
        ClientId = clientId;
        IsReady = isReady;
        IsHost = isHost;
        CharacterIndex = characterIndex;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? MultiplayerSessionController.FallbackDisplayName(clientId)
            : displayName;
    }

    public ulong ClientId { get; }
    public bool IsReady { get; }
    public bool IsHost { get; }
    /// <summary>Index into <see cref="MultiplayerProjectSettings"/> lobby characters; -1 = none.</summary>
    public int CharacterIndex { get; }
    /// <summary>Steam persona name when Steam is up, otherwise a PLAYER n placeholder. Never empty.</summary>
    public string DisplayName { get; }
}

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkManager))]
[RequireComponent(typeof(UnityTransport))]
public class MultiplayerSessionController : MonoBehaviour
{
    const string HostLoopbackAddress = "127.0.0.1";
    const string HostListenAddress = "0.0.0.0";
    const string LobbyReadyRequestMessageName = "lobby-ready-request";
    const string LobbyCharacterRequestMessageName = "lobby-character-request";
    const string LobbyNameRequestMessageName = "lobby-name-request";
    const string LobbyStateMessageName = "lobby-state";
    const byte NoCharacterByte = 0xFF;
    const int MaxDisplayNameLength = 24;
    const string OnlineUnavailableStatus = "Online play is unavailable right now.";

    [SerializeField] string defaultAddress = "127.0.0.1";
    [SerializeField] ushort defaultPort = 7777;

    NetworkManager _networkManager;
    UnityTransport _unityTransport;
    SteamLobbyService _steamLobby;
    SteamNetworkingSocketsTransport _steamTransport;
    MultiplayerProjectSettings _projectSettings;
    GameObject _playerPrefab;
    string _status = string.Empty;
    MultiplayerTransportMode _transportMode = MultiplayerTransportMode.DirectIp;
    bool _isOfflineSession;
    Coroutine _pendingGameLoad;
    bool _playerPrefabConfigured;
    Vector3 _levelStartSpawnPosition;
    Quaternion _levelStartSpawnRotation = Quaternion.identity;
    bool _hasLevelStartSpawn;
    readonly Dictionary<ulong, Coroutine> _pendingSpawnMoves = new();
    readonly Dictionary<ulong, bool> _serverLobbyReadyByClient = new();
    readonly Dictionary<ulong, int> _serverLobbyCharacterByClient = new();
    readonly Dictionary<ulong, string> _serverLobbyNameByClient = new();
    readonly List<LobbyPlayerState> _lobbyPlayers = new();
    bool _lobbyMessageHandlersRegistered;
    bool _lobbyReadyRequestHandlerRegistered;
    bool _localReady;
    bool _allLobbyPlayersReady;
    bool _gameStartRequested;
    int _localCharacterIndex = -1;

    public event Action<string> StatusChanged;
    public event Action LobbyStateChanged;

    public string DefaultAddress => defaultAddress;
    public ushort DefaultPort => defaultPort;
    public string CurrentStatus => _status;
    public MultiplayerTransportMode CurrentTransportMode => _transportMode;

    public string CurrentTransportLabel
    {
        get
        {
            if (_isOfflineSession)
                return "Offline";
            return _transportMode == MultiplayerTransportMode.SteamP2P ? "Online" : "Local";
        }
    }

    /// <summary>True while this session is the solo run started from Play Offline (loopback only).</summary>
    public bool IsOfflineSession => _isOfflineSession;
    public bool IsOnlineReady => SteamworksBootstrap.IsReady && IsSteamTransportAvailable;
    public ulong LocalSteamId => SteamworksBootstrap.LocalSteamId;
    public string LocalSteamName => SteamworksBootstrap.LocalPersonaName;
    public ulong CurrentSteamLobbyId => _steamLobby != null ? _steamLobby.CurrentLobbyId : 0UL;

    /// <summary>Friends can only be invited into a live online lobby.</summary>
    public bool CanInviteFriends => IsOnlineReady && CurrentSteamLobbyId != 0UL;
    public bool IsSessionActive => _networkManager != null && _networkManager.IsListening;
    public bool IsLobbyHost => _networkManager != null && _networkManager.IsHost;
    public bool IsLocalReady => _localReady;
    public bool AreAllLobbyPlayersReady => _allLobbyPlayersReady;
    public bool CanHostStartGame => IsLobbyHost && _lobbyPlayers.Count > 0 && _allLobbyPlayersReady && !_gameStartRequested;
    public IReadOnlyList<LobbyPlayerState> LobbyPlayers => _lobbyPlayers;
    public bool IsGameStartRequested => _gameStartRequested;
    public int LocalCharacterIndex => _localCharacterIndex;

    public int LobbyCharacterCount
    {
        get
        {
            EnsureProjectSettings();
            return _projectSettings != null ? _projectSettings.LobbyCharacterCount : 0;
        }
    }

    /// <summary>Characters can only be picked in the menu lobby, before the host starts the game.</summary>
    public bool CanSelectCharactersNow
    {
        get
        {
            if (!IsSessionActive || _gameStartRequested || LobbyCharacterCount == 0)
                return false;
            Scene active = SceneManager.GetActiveScene();
            return active.IsValid() && active.name == MultiplayerSceneFlow.MenuSceneName;
        }
    }

    public MultiplayerProjectSettings.LobbyCharacter GetLobbyCharacter(int index)
    {
        EnsureProjectSettings();
        return _projectSettings != null ? _projectSettings.GetLobbyCharacter(index) : null;
    }

    public string GetLobbyCharacterName(int index)
    {
        MultiplayerProjectSettings.LobbyCharacter character = GetLobbyCharacter(index);
        return character != null ? character.DisplayName : string.Empty;
    }

    /// <summary>The client that currently owns a character, if any (from the replicated lobby list).</summary>
    public bool TryGetCharacterOwner(int characterIndex, out ulong ownerClientId)
    {
        for (int i = 0; i < _lobbyPlayers.Count; i++)
        {
            if (_lobbyPlayers[i].CharacterIndex == characterIndex)
            {
                ownerClientId = _lobbyPlayers[i].ClientId;
                return true;
            }
        }

        ownerClientId = 0;
        return false;
    }

    /// <summary>Placeholder used until (or unless) a real name arrives — Steam is off in LAN/direct-IP play.</summary>
    public static string FallbackDisplayName(ulong clientId) => "PLAYER " + clientId;

    /// <summary>
    /// The name this machine announces to the lobby. Empty when Steam is not up; the server then keeps
    /// the <see cref="FallbackDisplayName"/> placeholder rather than showing a blank row.
    /// </summary>
    static string LocalDisplayName()
    {
        if (!SteamworksBootstrap.IsReady)
            return string.Empty;
        return SanitizeDisplayName(SteamworksBootstrap.LocalPersonaName);
    }

    /// <summary>
    /// Persona names are user-authored: cap the length so the payload stays bounded, and strip the
    /// angle brackets that would otherwise be parsed as TMP rich-text tags in the crew list.
    /// </summary>
    static string SanitizeDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        name = name.Trim().Replace('<', ' ').Replace('>', ' ');
        if (name.Length > MaxDisplayNameLength)
            name = name.Substring(0, MaxDisplayNameLength);
        return name.Trim();
    }

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
        EnsureConnectionApprovalCallback();
    }

    void OnEnable()
    {
        if (_networkManager == null)
            return;

        _networkManager.OnServerStarted += HandleServerStarted;
        _networkManager.OnClientConnectedCallback += HandleClientConnected;
        _networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
        _networkManager.OnServerStopped += HandleNetworkManagerStopped;
        _networkManager.OnClientStopped += HandleNetworkManagerStopped;
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
        _networkManager.OnServerStopped -= HandleNetworkManagerStopped;
        _networkManager.OnClientStopped -= HandleNetworkManagerStopped;
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        UnregisterLobbyMessageHandlers();

        if (_steamLobby != null)
        {
            _steamLobby.LobbyReadyToJoin -= HandleSteamLobbyReadyToJoin;
            _steamLobby.StatusChanged -= HandleSteamLobbyStatusChanged;
        }
    }

    // Direct-IP host/join is the LAN transport fallback. It has no menu entry point any more —
    // players host from Play > Host Game and join by invite — but the path is kept for
    // transport-level debugging and for StartOnlineHost's fallback when the platform layer is down.
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

        _isOfflineSession = false;
        SelectDirectIpTransport();
        ConfigurePlayerPrefab();
        ConfigureDirectHostTransport(port);
        bool started = _networkManager.StartHost();
        if (started)
            EnsureLobbyMessageHandlersRegistered();
        UpdateStatus(started
            ? $"Hosting on port {port}."
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

        _isOfflineSession = false;
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

    /// <summary>
    /// Solo run: a host bound to loopback only — nothing outside this machine can reach it — that
    /// drops straight into the level with no lobby step. It is still a host rather than a
    /// "no netcode" mode because every gameplay system (enemy AI, maze build, spawning, hit
    /// adjudication) runs off server authority.
    /// </summary>
    public void StartOfflineGame(string sceneName = null)
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

        string targetScene = MultiplayerSceneFlow.IsMazeGameplayScene(sceneName)
            ? sceneName
            : MultiplayerSceneFlow.GameSceneName;

        SelectDirectIpTransport();
        ConfigurePlayerPrefab();
        ConfigureOfflineHostTransport(defaultPort);
        _isOfflineSession = true;

        if (!_networkManager.StartHost())
        {
            _isOfflineSession = false;
            UpdateStatus("Could not start the solo run. Check the Unity console for details.");
            return;
        }

        EnsureLobbyMessageHandlersRegistered();
        UpdateStatus("Starting solo run...");
        CancelPendingGameLoad();
        _pendingGameLoad = StartCoroutine(LoadGameSceneWhenSessionReady(targetScene));
    }

    /// <summary>
    /// Opens the lobby friends join from an invite. Uses friend-to-friend networking when the
    /// platform layer is up; without it the lobby still opens locally so the flow never dead-ends
    /// on an unusable button.
    /// </summary>
    public void StartOnlineHost()
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
        {
            StartDirectIpHost(defaultPort);
            UpdateStatus("Hosting locally — friends can't be invited right now.");
            return;
        }

        _isOfflineSession = false;
        ConfigurePlayerPrefab();
        bool started = _networkManager.StartHost();
        if (!started)
        {
            UpdateStatus("Could not open the session. Check the Unity console for details.");
            return;
        }

        EnsureLobbyMessageHandlersRegistered();
        if (_steamLobby == null || !_steamLobby.CreateLobbyForCurrentHost())
            UpdateStatus("Session started, but friends can't be invited right now.");
    }

    /// <summary>Connects to the host of a lobby this player was invited into.</summary>
    public void StartOnlineClient(ulong hostUserId)
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

        if (hostUserId == 0UL)
        {
            UpdateStatus("That invite is no longer valid.");
            return;
        }

        if (!SelectSteamTransport())
        {
            UpdateStatus(OnlineUnavailableStatus);
            return;
        }

        _isOfflineSession = false;
        _steamTransport.ConnectToSteamID = hostUserId;
        ConfigurePlayerPrefab();
        bool started = _networkManager.StartClient();
        if (started)
            EnsureLobbyMessageHandlersRegistered();
        UpdateStatus(started
            ? "Connecting to the host..."
            : "Could not connect to the host. Check the Unity console for details.");
    }

    public void JoinLobbyFromInvite(ulong lobbyId)
    {
        if (_steamLobby == null)
        {
            UpdateStatus(OnlineUnavailableStatus);
            return;
        }

        _steamLobby.JoinLobby(lobbyId);
    }

    /// <summary>Friends who are logged in right now, for the lobby's invite list.</summary>
    public bool TryGetFriends(List<OnlineFriend> results)
    {
        return _steamLobby != null && _steamLobby.TryGetFriends(results);
    }

    public bool InviteFriend(ulong friendUserId)
    {
        if (_steamLobby == null)
        {
            UpdateStatus(OnlineUnavailableStatus);
            return false;
        }

        return _steamLobby.InviteFriend(friendUserId);
    }

    public void ShutdownSession()
    {
        if (_networkManager == null || !_networkManager.IsListening)
        {
            UpdateStatus("No active session to stop.");
            return;
        }

        CancelPendingGameLoad();
        CancelAllPendingSpawnMoves();
        _steamLobby?.LeaveLobby();
        ClearLobbyState();
        UnregisterLobbyMessageHandlers();
        ProximityVoiceSession.InvalidateProximityMessaging();
        _networkManager.Shutdown();
        _isOfflineSession = false;
        SelectDirectIpTransport();
        ConfigureDirectClientTransport(defaultAddress, defaultPort);
        UpdateStatus("Session stopped.");
    }

    /// <summary>
    /// The host's local client is not connected the instant <c>StartHost</c> returns, and
    /// <c>NetworkSceneManager</c> refuses a load until it is — so poll instead of assuming.
    /// </summary>
    IEnumerator LoadGameSceneWhenSessionReady(string sceneName)
    {
        const float timeoutSeconds = 10f;
        float elapsed = 0f;

        while (elapsed < timeoutSeconds)
        {
            if (_networkManager == null || !_networkManager.IsListening)
            {
                _pendingGameLoad = null;
                yield break;
            }

            if (_networkManager.IsHost && _networkManager.SceneManager != null
                && _networkManager.ConnectedClientsIds.Count > 0)
            {
                _gameStartRequested = true;
                LobbyStateChanged?.Invoke();

                SceneEventProgressStatus status = _networkManager.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
                if (status == SceneEventProgressStatus.Started)
                {
                    _pendingGameLoad = null;
                    yield break;
                }

                _gameStartRequested = false;
                LobbyStateChanged?.Invoke();
            }

            yield return null;
            elapsed += Time.deltaTime;
        }

        _pendingGameLoad = null;
        UpdateStatus($"Could not load {sceneName}. Check the Unity console for details.");
    }

    void CancelPendingGameLoad()
    {
        if (_pendingGameLoad == null)
            return;

        StopCoroutine(_pendingGameLoad);
        _pendingGameLoad = null;
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

    /// <summary>
    /// Ask to own a lobby character. Server-validated: only in the menu lobby before game start,
    /// and only if no other player currently owns that character.
    /// </summary>
    public void RequestSelectCharacter(int characterIndex)
    {
        if (_networkManager == null || !_networkManager.IsListening)
        {
            UpdateStatus("Join or host a lobby before picking a character.");
            return;
        }

        if (!CanSelectCharactersNow)
            return;

        if (characterIndex < 0 || characterIndex >= LobbyCharacterCount || characterIndex == _localCharacterIndex)
            return;

        if (_networkManager.IsServer)
        {
            ServerTrySelectCharacter(_networkManager.LocalClientId, characterIndex);
            return;
        }

        SendCharacterRequest(characterIndex);
    }

    public void StartGameFromLobby(string sceneName = null)
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

        // Only maze gameplay scenes have a matching ProceduralMazeConfig; fall back to the default.
        string targetScene = MultiplayerSceneFlow.IsMazeGameplayScene(sceneName)
            ? sceneName
            : MultiplayerSceneFlow.GameSceneName;

        _gameStartRequested = true;
        LobbyStateChanged?.Invoke();
        SceneEventProgressStatus status = _networkManager.SceneManager.LoadScene(
            targetScene,
            LoadSceneMode.Single);

        if (status == SceneEventProgressStatus.Started)
        {
            UpdateStatus($"Starting {targetScene} for all ready players...");
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

    /// <summary>
    /// Fails quietly on purpose: the platform layer's own wording ("SteamAPI.Init failed...") is
    /// developer diagnostics, so it goes to the console and each caller decides what the player sees.
    /// </summary>
    bool SelectSteamTransport()
    {
        if (!SteamworksBootstrap.IsReady)
        {
            Debug.LogWarning($"[Multiplayer] Online transport unavailable: {SteamworksBootstrap.Status}", this);
            return false;
        }

        if (_steamTransport == null)
            _steamTransport = GetComponent<SteamNetworkingSocketsTransport>();
        if (_steamTransport == null)
        {
            Debug.LogWarning("[Multiplayer] Online transport component is missing.", this);
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

    /// <summary>Solo run: bind the listen socket to loopback so the session is unreachable from the network.</summary>
    void ConfigureOfflineHostTransport(ushort port)
    {
        EnsureNetworkConfig(_unityTransport);
        _unityTransport.SetConnectionData(HostLoopbackAddress, port, HostLoopbackAddress);
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

        // Player must already be listed in Resources/DefaultNetworkPrefabs; AddNetworkPrefab here duplicates its GlobalObjectIdHash.
        _networkManager.NetworkConfig.PlayerPrefab = null;
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

        // Connection approval: clients send the build version in NetworkConfig.ConnectionData; the host
        // rejects mismatched versions and joins past MaxPlayers in OnConnectionApproval.
        _networkManager.NetworkConfig.ConnectionApproval = true;
        ApplyConnectionData();

        if (transport != null)
            _networkManager.NetworkConfig.NetworkTransport = transport;
    }

    void EnsureProjectSettings()
    {
        if (_projectSettings == null)
            _projectSettings = Resources.Load<MultiplayerProjectSettings>("MultiplayerProjectSettings");
    }

    void ApplyConnectionData()
    {
        if (_networkManager == null || _networkManager.NetworkConfig == null)
            return;
        EnsureProjectSettings();
        string version = _projectSettings != null ? _projectSettings.BuildVersion : Application.version;
        _networkManager.NetworkConfig.ConnectionData = System.Text.Encoding.UTF8.GetBytes(version ?? string.Empty);
    }

    void EnsureConnectionApprovalCallback()
    {
        if (_networkManager == null)
            return;
        _networkManager.ConnectionApprovalCallback = OnConnectionApproval;
    }

    void OnConnectionApproval(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        // Player objects are spawned manually by this controller (see TrySpawnOrMovePlayerToLevelStart);
        // NGO must not auto-spawn one here.
        response.CreatePlayerObject = false;

        EnsureProjectSettings();
        string expectedVersion = _projectSettings != null ? _projectSettings.BuildVersion : Application.version;
        string clientVersion = (request.Payload != null && request.Payload.Length > 0)
            ? System.Text.Encoding.UTF8.GetString(request.Payload)
            : string.Empty;

        if (!string.Equals(clientVersion, expectedVersion, System.StringComparison.Ordinal))
        {
            response.Approved = false;
            response.Reason = $"Build version mismatch (server: {expectedVersion}, client: {clientVersion}).";
            return;
        }

        int max = _projectSettings != null ? Mathf.Max(1, _projectSettings.MaxPlayers) : 4;
        // The connecting client is not yet in ConnectedClientsIds — so count >= max means this would be
        // the (max+1)'th and must be rejected. The host's own loopback approval runs while count == 0.
        if (_networkManager != null && _networkManager.ConnectedClientsIds.Count >= max)
        {
            response.Approved = false;
            response.Reason = $"Lobby is full ({max}/{max}).";
            return;
        }

        response.Approved = true;
    }

    void HandleServerStarted()
    {
        if (_networkManager == null)
            return;

        EnsureLobbyMessageHandlersRegistered();
        ResetServerLobbyState();

        if (!_networkManager.IsHost)
            return;

        if (_isOfflineSession)
            UpdateStatus("Solo run started.");
        else if (_transportMode == MultiplayerTransportMode.SteamP2P)
            UpdateStatus("Hosting. Invite friends when you're ready.");
        else
            UpdateStatus($"Hosting locally on port {defaultPort}.");
    }

    void HandleClientConnected(ulong clientId)
    {
        if (_networkManager == null)
            return;

        if (_networkManager.IsHost && clientId == _networkManager.LocalClientId)
        {
            RegisterLobbyClient(clientId);
            QueueSpawnOrMovePlayerToLevelStart(clientId);
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
            SendNameRequest(LocalDisplayName());
            UpdateStatus("Connected.");
        }
    }

    void HandleClientDisconnected(ulong clientId)
    {
        if (_networkManager == null)
            return;

        CancelPendingSpawnMove(clientId);

        if (clientId == _networkManager.LocalClientId && !_networkManager.IsServer)
        {
            // Release the Steam lobby too, exactly as ShutdownSession does. That path is unreachable here (NGO
            // has already dropped IsListening, so IsSessionActive is false), and SteamLobbyService.OnDisable
            // never runs on the DontDestroyOnLoad bootstrap — so a dropped client stayed a member of the host's
            // dead lobby, kept reporting a live lobby id it does not own, and leaked that membership outright
            // once it next hosted or joined and overwrote the handle.
            _steamLobby?.LeaveLobby();
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

    /// <summary>
    /// Clear the lobby-handler bookkeeping in lock-step with NGO shutdown. <see cref="NetworkManager.Shutdown"/>
    /// destroys the <see cref="Unity.Netcode.CustomMessagingManager"/> and the next <c>StartHost</c>/<c>StartClient</c>
    /// builds a fresh one, so handlers registered on the old manager are gone. Without this, a client dropped by
    /// the host (host quit, kick, transport drop) keeps <c>_lobbyMessageHandlersRegistered</c> stuck true —
    /// <see cref="EnsureLobbyMessageHandlersRegistered"/> then short-circuits on it for the rest of the process and
    /// the next lobby that player hosts or joins registers nothing: ready-ups are dropped so START never enables,
    /// and a joiner's crew roster never populates. <see cref="ProximityVoiceSession"/> guards the identical hazard
    /// the same way. Safe to run after teardown: the unregister resets both flags when the manager is already gone.
    /// </summary>
    void HandleNetworkManagerStopped(bool _) => UnregisterLobbyMessageHandlers();

    void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        if (!MultiplayerSceneFlow.IsMazeGameplayScene(scene.name))
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

        StartOnlineClient(hostSteamId);
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
            _networkManager.CustomMessagingManager.RegisterNamedMessageHandler(LobbyCharacterRequestMessageName, HandleLobbyCharacterRequest);
            _networkManager.CustomMessagingManager.RegisterNamedMessageHandler(LobbyNameRequestMessageName, HandleLobbyNameRequest);
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
        {
            _networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(LobbyReadyRequestMessageName);
            _networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(LobbyCharacterRequestMessageName);
            _networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(LobbyNameRequestMessageName);
        }
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

    void HandleLobbyCharacterRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        reader.ReadValueSafe(out byte characterByte);
        ServerTrySelectCharacter(senderClientId, characterByte == NoCharacterByte ? -1 : characterByte);
    }

    void ServerTrySelectCharacter(ulong clientId, int characterIndex)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        // Selection is lobby-only: ignore requests after the host pressed start or outside the menu.
        if (!CanSelectCharactersNow)
            return;

        if (characterIndex < 0 || characterIndex >= LobbyCharacterCount)
            return;

        // one owner per character
        foreach (KeyValuePair<ulong, int> pair in _serverLobbyCharacterByClient)
        {
            if (pair.Value == characterIndex && pair.Key != clientId)
            {
                // refused — rebroadcast authoritative state so the requester's UI stays correct
                PublishServerLobbyState();
                return;
            }
        }

        _serverLobbyCharacterByClient[clientId] = characterIndex;
        PublishServerLobbyState();
    }

    int ServerFindFreeCharacterIndex()
    {
        int count = LobbyCharacterCount;
        for (int index = 0; index < count; index++)
        {
            bool taken = false;
            foreach (KeyValuePair<ulong, int> pair in _serverLobbyCharacterByClient)
            {
                if (pair.Value == index)
                {
                    taken = true;
                    break;
                }
            }

            if (!taken)
                return index;
        }

        return -1;
    }

    void HandleLobbyNameRequest(ulong senderClientId, FastBufferReader reader)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        reader.ReadValueSafe(out string rawName);
        SetServerLobbyName(senderClientId, SanitizeDisplayName(rawName));
    }

    void SendNameRequest(string displayName)
    {
        if (_networkManager == null || _networkManager.CustomMessagingManager == null)
            return;
        if (string.IsNullOrEmpty(displayName))
            return;

        // Growable writer: a persona name is variable-length UTF-16 and the cap only bounds characters.
        using FastBufferWriter writer = new(128, Allocator.Temp, 1024);
        writer.WriteValueSafe(displayName);
        _networkManager.CustomMessagingManager.SendNamedMessage(
            LobbyNameRequestMessageName,
            NetworkManager.ServerClientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    void SetServerLobbyName(ulong clientId, string displayName)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;
        if (string.IsNullOrEmpty(displayName))
            return;

        _serverLobbyNameByClient[clientId] = displayName;
        PublishServerLobbyState();
    }

    void SendCharacterRequest(int characterIndex)
    {
        if (_networkManager == null || _networkManager.CustomMessagingManager == null)
            return;

        using FastBufferWriter writer = new(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe(characterIndex >= 0 && characterIndex < NoCharacterByte ? (byte)characterIndex : NoCharacterByte);
        _networkManager.CustomMessagingManager.SendNamedMessage(
            LobbyCharacterRequestMessageName,
            NetworkManager.ServerClientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    void HandleLobbyStateMessage(ulong senderClientId, FastBufferReader reader)
    {
        if (_networkManager == null || _networkManager.IsServer)
            return;

        reader.ReadValueSafe(out int playerCount);
        _lobbyPlayers.Clear();
        bool allReady = playerCount > 0;
        _localReady = false;
        _localCharacterIndex = -1;

        for (int i = 0; i < playerCount; i++)
        {
            reader.ReadValueSafe(out ulong clientId);
            reader.ReadValueSafe(out byte readyByte);
            reader.ReadValueSafe(out byte hostByte);
            reader.ReadValueSafe(out byte characterByte);
            reader.ReadValueSafe(out string displayName);

            bool isReady = readyByte != 0;
            int characterIndex = characterByte == NoCharacterByte ? -1 : characterByte;
            _lobbyPlayers.Add(new LobbyPlayerState(clientId, isReady, hostByte != 0, characterIndex, displayName));
            allReady &= isReady;

            if (clientId == _networkManager.LocalClientId)
            {
                _localReady = isReady;
                _localCharacterIndex = characterIndex;
            }
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
        _serverLobbyCharacterByClient.Clear();
        _serverLobbyNameByClient.Clear();
        foreach (ulong clientId in _networkManager.ConnectedClientsIds)
        {
            _serverLobbyReadyByClient[clientId] = false;
            _serverLobbyCharacterByClient[clientId] = ServerFindFreeCharacterIndex();
            SeedServerLobbyName(clientId);
        }

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

        // every player always owns a character: hand the newcomer the lowest free one
        if (!_serverLobbyCharacterByClient.ContainsKey(clientId))
            _serverLobbyCharacterByClient[clientId] = ServerFindFreeCharacterIndex();

        // A remote client's real name arrives a moment later on its own message; until then the
        // placeholder keeps the crew row from rendering blank.
        SeedServerLobbyName(clientId);

        PublishServerLobbyState();
    }

    void UnregisterLobbyClient(ulong clientId)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        _serverLobbyReadyByClient.Remove(clientId);
        _serverLobbyCharacterByClient.Remove(clientId);
        _serverLobbyNameByClient.Remove(clientId);
        PublishServerLobbyState();
    }

    /// <summary>
    /// Fills in a name for a client we have not heard from yet: the host knows its own persona name
    /// directly, everyone else gets the placeholder until their name message lands.
    /// </summary>
    void SeedServerLobbyName(ulong clientId)
    {
        if (_serverLobbyNameByClient.ContainsKey(clientId))
            return;

        string name = clientId == _networkManager.LocalClientId ? LocalDisplayName() : string.Empty;
        _serverLobbyNameByClient[clientId] = string.IsNullOrEmpty(name) ? FallbackDisplayName(clientId) : name;
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
            int characterIndex = _serverLobbyCharacterByClient.TryGetValue(pair.Key, out int idx) ? idx : -1;
            string displayName = _serverLobbyNameByClient.TryGetValue(pair.Key, out string named) ? named : string.Empty;
            _lobbyPlayers.Add(new LobbyPlayerState(pair.Key, pair.Value, isHost, characterIndex, displayName));
            allReady &= pair.Value;
        }

        _localReady = _serverLobbyReadyByClient.TryGetValue(_networkManager.LocalClientId, out bool hostReady) && hostReady;
        _localCharacterIndex = _serverLobbyCharacterByClient.TryGetValue(_networkManager.LocalClientId, out int localIdx) ? localIdx : -1;
        _allLobbyPlayersReady = allReady;
        LobbyStateChanged?.Invoke();
        SendLobbyStateToClients();
    }

    void SendLobbyStateToClients()
    {
        if (_networkManager == null || _networkManager.CustomMessagingManager == null || !_networkManager.IsServer)
            return;

        // Names are variable-length, so the writer is allowed to grow instead of being sized up front.
        int payloadSize = sizeof(int) + _lobbyPlayers.Count * (sizeof(ulong) + 3 * sizeof(byte) + 4 + 2 * MaxDisplayNameLength);
        using FastBufferWriter writer = new(payloadSize, Allocator.Temp, 64 * 1024);
        writer.WriteValueSafe(_lobbyPlayers.Count);
        for (int i = 0; i < _lobbyPlayers.Count; i++)
        {
            LobbyPlayerState player = _lobbyPlayers[i];
            writer.WriteValueSafe(player.ClientId);
            writer.WriteValueSafe((byte)(player.IsReady ? 1 : 0));
            writer.WriteValueSafe((byte)(player.IsHost ? 1 : 0));
            writer.WriteValueSafe(player.CharacterIndex >= 0 && player.CharacterIndex < NoCharacterByte
                ? (byte)player.CharacterIndex
                : NoCharacterByte);
            writer.WriteValueSafe(player.DisplayName);
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
        _serverLobbyCharacterByClient.Clear();
        _serverLobbyNameByClient.Clear();
        _lobbyPlayers.Clear();
        _localReady = false;
        _allLobbyPlayersReady = false;
        _gameStartRequested = false;
        _localCharacterIndex = -1;
        LobbyStateChanged?.Invoke();
    }

    void QueueSpawnOrMovePlayerToLevelStart(ulong clientId)
    {
        if (!_hasLevelStartSpawn || _networkManager == null || !_networkManager.IsServer)
            return;

        Scene activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid() || !MultiplayerSceneFlow.IsMazeGameplayScene(activeScene.name))
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
            GameObject prefabToSpawn = GetServerPlayerPrefabForClient(clientId);
            if (prefabToSpawn == null)
                return false;

            GameObject playerInstance = Instantiate(prefabToSpawn, spawnPosition, spawnRotation);
            playerObject = playerInstance.GetComponent<NetworkObject>();
            if (playerObject == null)
            {
                Debug.LogError("[Multiplayer] Player prefab must have a NetworkObject to spawn from the lobby.", this);
                Destroy(playerInstance);
                return false;
            }

            // A player arriving from the previous maze section keeps the health and hotbar they came in with.
            // Health goes on before the spawn so NetworkPlayerRespawn replicates it in the spawn snapshot; the
            // hotbar has to wait until the NetworkVariables exist.
            bool hasCarriedState = LevelCarryOverStore.TryGetState(clientId, out LevelCarryOverStore.PlayerState carried);
            if (hasCarriedState)
            {
                PlayerHealth carriedHealth = playerInstance.GetComponent<PlayerHealth>();
                if (carriedHealth != null)
                    carriedHealth.ApplyCarriedHealth(carried.Health);
            }

            playerObject.SpawnAsPlayerObject(clientId, true);

            if (hasCarriedState)
            {
                NetworkPlayerInventory carriedInventory = playerObject.GetComponent<NetworkPlayerInventory>();
                if (carriedInventory != null)
                    carriedInventory.ServerRestoreCarriedInventory(carried);

                LevelCarryOverStore.ConsumeState(clientId);
            }
        }
        else
        {
            ServerMoveSpawnedPlayerObject(playerObject, spawnPosition, spawnRotation);
        }

        NetworkPlayerRespawn playerRespawn = playerObject.GetComponent<NetworkPlayerRespawn>();
        if (playerRespawn != null)
            playerRespawn.ApplyInitialSpawn(spawnPosition, spawnRotation);
        else
            ServerMoveSpawnedPlayerObject(playerObject, spawnPosition, spawnRotation);

        return true;
    }

    /// <summary>
    /// Moves an already-spawned player object from the server. A client-owned player's transform is owner
    /// authoritative, so writing it here fights that owner's interpolator (the host sees the player jump and
    /// rubber-band straight back); the owner performs the move itself from
    /// <see cref="NetworkPlayerRespawn.ApplyInitialSpawn"/>'s owner-targeted ClientRpc. Only the instance that
    /// actually holds transform authority writes — that includes the host's own player and any player the
    /// server is driving during a Jailor carry.
    /// </summary>
    static void ServerMoveSpawnedPlayerObject(NetworkObject playerObject, Vector3 position, Quaternion rotation)
    {
        if (playerObject == null)
            return;

        bool hasNetworkedTransform = playerObject.TryGetComponent(out OwnerNetworkTransform ownerNetTransform)
            && ownerNetTransform.IsSpawned;

        if (hasNetworkedTransform && !ownerNetTransform.CanCommitToTransform)
            return;

        playerObject.transform.SetPositionAndRotation(position, rotation);

        if (hasNetworkedTransform)
            ownerNetTransform.Teleport(position, rotation, Vector3.one);
    }

    /// <summary>The lobby-selected character prefab for a client; falls back to the default player prefab.</summary>
    GameObject GetServerPlayerPrefabForClient(ulong clientId)
    {
        if (_serverLobbyCharacterByClient.TryGetValue(clientId, out int characterIndex))
        {
            MultiplayerProjectSettings.LobbyCharacter character = GetLobbyCharacter(characterIndex);
            if (character != null && character.PlayerPrefab != null)
                return character.PlayerPrefab;
        }

        return _playerPrefab;
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
