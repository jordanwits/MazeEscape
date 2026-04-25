using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using System.Collections.Generic;
using Netcode.Transports;

[DisallowMultipleComponent]
public class MultiplayerBootstrap : MonoBehaviour
{
    public static MultiplayerBootstrap Instance { get; private set; }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        EnsureCoreComponents();
    }

    void EnsureCoreComponents()
    {
        if (!TryGetComponent(out SteamworksBootstrap _))
            gameObject.AddComponent<SteamworksBootstrap>();

        if (!TryGetComponent(out SteamLobbyService _))
            gameObject.AddComponent<SteamLobbyService>();

        if (!TryGetComponent(out UnityTransport transport))
            transport = gameObject.AddComponent<UnityTransport>();

        if (!TryGetComponent(out SteamNetworkingSocketsTransport _))
            gameObject.AddComponent<SteamNetworkingSocketsTransport>();

        if (!TryGetComponent(out NetworkManager networkManager))
            networkManager = gameObject.AddComponent<NetworkManager>();

        EnsureNetworkConfig(networkManager, transport);

        if (!TryGetComponent(out MultiplayerSessionController _))
            gameObject.AddComponent<MultiplayerSessionController>();

        if (!TryGetComponent(out MultiplayerSceneFlow _))
            gameObject.AddComponent<MultiplayerSceneFlow>();

        if (!TryGetComponent(out MultiplayerMenuOverlay _))
            gameObject.AddComponent<MultiplayerMenuOverlay>();

        if (!TryGetComponent(out ProceduralMazeCoordinator _))
            gameObject.AddComponent<ProceduralMazeCoordinator>();

        if (!TryGetComponent(out GameAudioManager _))
            gameObject.AddComponent<GameAudioManager>();

        if (!TryGetComponent(out ProximityVoiceSession _))
            gameObject.AddComponent<ProximityVoiceSession>();
    }

    void EnsureNetworkConfig(NetworkManager networkManager, UnityTransport transport)
    {
        if (networkManager.NetworkConfig == null)
            networkManager.NetworkConfig = new NetworkConfig();

        if (networkManager.NetworkConfig.Prefabs == null)
            networkManager.NetworkConfig.Prefabs = new NetworkPrefabs();

        if (networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists == null)
            networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists = new List<NetworkPrefabsList>();

        networkManager.NetworkConfig.NetworkTransport = transport;
    }
}
