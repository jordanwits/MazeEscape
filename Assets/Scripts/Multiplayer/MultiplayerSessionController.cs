using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkManager))]
[RequireComponent(typeof(UnityTransport))]
public class MultiplayerSessionController : MonoBehaviour
{
    const string HostLoopbackAddress = "127.0.0.1";
    const string HostListenAddress = "0.0.0.0";

    [SerializeField] string defaultAddress = "127.0.0.1";
    [SerializeField] ushort defaultPort = 7777;

    NetworkManager _networkManager;
    UnityTransport _transport;
    MultiplayerProjectSettings _projectSettings;
    string _status = "Multiplayer foundation ready. F8 toggles the debug menu.";
    bool _playerPrefabConfigured;
    Vector3 _levelStartSpawnPosition;
    Quaternion _levelStartSpawnRotation = Quaternion.identity;
    bool _hasLevelStartSpawn;
    readonly Dictionary<ulong, Coroutine> _pendingSpawnMoves = new();

    public event Action<string> StatusChanged;

    public string DefaultAddress => defaultAddress;
    public ushort DefaultPort => defaultPort;
    public string CurrentStatus => _status;
    public bool IsSessionActive => _networkManager != null && _networkManager.IsListening;

    void Awake()
    {
        _networkManager = GetComponent<NetworkManager>();
        _transport = GetComponent<UnityTransport>();
        EnsureNetworkConfig();
        ConfigureClientTransport(defaultAddress, defaultPort);
        ConfigurePlayerPrefab();
    }

    void OnEnable()
    {
        if (_networkManager == null)
            return;

        _networkManager.OnServerStarted += HandleServerStarted;
        _networkManager.OnClientConnectedCallback += HandleClientConnected;
        _networkManager.OnClientDisconnectCallback += HandleClientDisconnected;
    }

    void OnDisable()
    {
        if (_networkManager == null)
            return;

        _networkManager.OnServerStarted -= HandleServerStarted;
        _networkManager.OnClientConnectedCallback -= HandleClientConnected;
        _networkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    public void StartHost(ushort? portOverride = null)
    {
        if (_networkManager == null || _transport == null)
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

        ConfigurePlayerPrefab();
        ConfigureHostTransport(port);
        bool started = _networkManager.StartHost();
        UpdateStatus(started
            ? $"Host started on port {port}. This chunk uses Unity Transport while the Steam lobby layer is built next."
            : "Host start failed. Check the Unity console for details.");
    }

    public void StartClient(string address, ushort port)
    {
        if (_networkManager == null || _transport == null)
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

        ConfigurePlayerPrefab();
        ConfigureClientTransport(defaultAddress, defaultPort);
        bool started = _networkManager.StartClient();
        UpdateStatus(started
            ? $"Joining {defaultAddress}:{defaultPort}..."
            : "Client start failed. Check the Unity console for details.");
    }

    public void ShutdownSession()
    {
        if (_networkManager == null || !_networkManager.IsListening)
        {
            UpdateStatus("No active session to stop.");
            return;
        }

        CancelAllPendingSpawnMoves();
        _networkManager.Shutdown();
        ConfigureClientTransport(defaultAddress, defaultPort);
        UpdateStatus("Session stopped.");
    }

    void CancelAllPendingSpawnMoves()
    {
        foreach (ulong clientId in new List<ulong>(_pendingSpawnMoves.Keys))
            CancelPendingSpawnMove(clientId);
    }

    void ConfigureHostTransport(ushort port)
    {
        EnsureNetworkConfig();
        _transport.SetConnectionData(HostLoopbackAddress, port, HostListenAddress);
    }

    void ConfigureClientTransport(string address, ushort port)
    {
        EnsureNetworkConfig();
        _transport.SetConnectionData(address, port);
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

        _levelStartSpawnPosition = _projectSettings.LevelStartPosition;
        _levelStartSpawnRotation = _projectSettings.LevelStartRotation;
        _hasLevelStartSpawn = true;

        _networkManager.NetworkConfig.PlayerPrefab = _projectSettings.PlayerPrefab;
        _networkManager.AddNetworkPrefab(_projectSettings.PlayerPrefab);
        _playerPrefabConfigured = true;
    }

    void EnsureNetworkConfig()
    {
        if (_networkManager == null)
            return;

        if (_networkManager.NetworkConfig == null)
            _networkManager.NetworkConfig = new NetworkConfig();

        if (_networkManager.NetworkConfig.Prefabs == null)
            _networkManager.NetworkConfig.Prefabs = new NetworkPrefabs();

        if (_networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists == null)
            _networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists = new List<NetworkPrefabsList>();

        _networkManager.NetworkConfig.NetworkTransport = _transport;
    }

    void HandleServerStarted()
    {
        if (_networkManager == null)
            return;

        if (_networkManager.IsHost)
            UpdateStatus($"Host session active on port {defaultPort}.");
    }

    void HandleClientConnected(ulong clientId)
    {
        if (_networkManager == null)
            return;

        if (_networkManager.IsHost && clientId == _networkManager.LocalClientId)
        {
            QueueMovePlayerToLevelStart(clientId);
            UpdateStatus($"Host client connected locally on port {defaultPort}.");
            return;
        }

        if (_networkManager.IsServer)
        {
            QueueMovePlayerToLevelStart(clientId);
            UpdateStatus($"Client {clientId} connected.");
            return;
        }

        if (clientId == _networkManager.LocalClientId)
            UpdateStatus($"Connected to host at {defaultAddress}:{defaultPort}.");
    }

    void HandleClientDisconnected(ulong clientId)
    {
        if (_networkManager == null)
            return;

        CancelPendingSpawnMove(clientId);

        if (clientId == _networkManager.LocalClientId && !_networkManager.IsServer)
        {
            UpdateStatus("Disconnected from host.");
            return;
        }

        if (_networkManager.IsServer)
            UpdateStatus($"Client {clientId} disconnected.");
    }

    void UpdateStatus(string message)
    {
        _status = message;
        StatusChanged?.Invoke(_status);
        Debug.Log($"[Multiplayer] {_status}", this);
    }

    void QueueMovePlayerToLevelStart(ulong clientId)
    {
        if (!_hasLevelStartSpawn || _networkManager == null || !_networkManager.IsServer)
            return;

        if (_pendingSpawnMoves.ContainsKey(clientId))
            return;

        Coroutine routine = StartCoroutine(WaitAndMovePlayerToLevelStart(clientId));
        _pendingSpawnMoves[clientId] = routine;
    }

    IEnumerator WaitAndMovePlayerToLevelStart(ulong clientId)
    {
        const float timeoutSeconds = 5f;
        float elapsed = 0f;

        while (elapsed < timeoutSeconds)
        {
            if (TryMovePlayerToLevelStart(clientId))
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

    bool TryMovePlayerToLevelStart(ulong clientId)
    {
        if (!_hasLevelStartSpawn || _networkManager == null || !_networkManager.IsServer)
            return false;

        if (!_networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client))
            return false;

        if (client.PlayerObject == null)
            return false;

        Vector3 spawnPosition = _levelStartSpawnPosition;
        Quaternion spawnRotation = _levelStartSpawnRotation;
        if (MultiplayerSpawnRegistry.Instance != null
            && MultiplayerSpawnRegistry.Instance.TryGetInitialJoinSpawn(out Vector3 registryPosition, out Quaternion registryRotation))
        {
            spawnPosition = registryPosition;
            spawnRotation = registryRotation;
        }

        NetworkPlayerRespawn playerRespawn = client.PlayerObject.GetComponent<NetworkPlayerRespawn>();
        if (playerRespawn != null)
            playerRespawn.ApplyInitialSpawn(spawnPosition, spawnRotation);
        else
            client.PlayerObject.transform.SetPositionAndRotation(spawnPosition, spawnRotation);

        return true;
    }
}
