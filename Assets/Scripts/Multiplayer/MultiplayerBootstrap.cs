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

        if (!TryGetComponent(out PauseMenuController _))
            gameObject.AddComponent<PauseMenuController>();

        if (!TryGetComponent(out ProceduralMazeCoordinator _))
            gameObject.AddComponent<ProceduralMazeCoordinator>();

        if (!TryGetComponent(out GameAudioManager _))
            gameObject.AddComponent<GameAudioManager>();

        if (!TryGetComponent(out GameDisplayBrightness _))
            gameObject.AddComponent<GameDisplayBrightness>();

        if (!TryGetComponent(out GameGraphicsSettings _))
            gameObject.AddComponent<GameGraphicsSettings>();

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

        EnsureDefaultNetworkPrefabsList(networkManager);

        networkManager.NetworkConfig.NetworkTransport = transport;
    }

    /// <summary>
    /// Runtime-created <see cref="NetworkManager"/> has no inspector-assigned prefab lists.
    /// Without loading <c>Resources/DefaultNetworkPrefabs</c>, clients fail to spawn objects whose
    /// hashes exist only on the host (e.g. missing NetworkPrefab / hash mismatch errors).
    /// </summary>
    static void EnsureDefaultNetworkPrefabsList(NetworkManager networkManager)
    {
        var defaults = Resources.Load<NetworkPrefabsList>("DefaultNetworkPrefabs");
        if (defaults == null)
        {
            Debug.LogWarning(
                "[Multiplayer] Could not load Resources/DefaultNetworkPrefabs. Register network prefabs manually or keep DefaultNetworkPrefabs.asset under Assets/Resources.",
                networkManager);
            return;
        }

        var lists = networkManager.NetworkConfig.Prefabs.NetworkPrefabsLists;
        if (!lists.Contains(defaults))
            lists.Add(defaults);

        EnsureNestedPrefabHashOverrides(defaults);
    }

    /// <summary>
    /// Unity auto-generates a unique <c>GlobalObjectIdHash</c> for every <see cref="NetworkObject"/>
    /// nested inside another prefab (e.g. each <c>BasketballGame</c> placed inside
    /// <c>CarnivalMainRoom</c>). The server uses that override hash in spawn messages; without an
    /// explicit Hash override entry the receiving client has no prefab mapping and silently drops the
    /// spawn — visible as "no collider on the BasketballGame" + a non-functional Start button on
    /// joined clients. We add the override → base-prefab mapping to the loaded
    /// <see cref="NetworkPrefabsList"/> in memory (the asset on disk is not modified) so NGO picks it
    /// up when <c>NetworkManager.Initialize</c> reprocesses the list on <c>StartHost</c> /
    /// <c>StartClient</c>.
    /// Hash values are read directly from <c>CarnivalMainRoom.prefab</c> prefab-modification
    /// entries; rerun the diff if the parent prefab is re-saved and the auto-generated hashes change.
    /// </summary>
    static void EnsureNestedPrefabHashOverrides(NetworkPrefabsList defaults)
    {
        // Intentionally empty. The previous BasketballGame nested-override hashes (2806273795 /
        // 1843502869) no longer exist; the nested hashes that DO remain in maze piece prefabs are all
        // cases an override cannot fix, so mapping them here would only paper over the bug:
        //   - the Jail door (3896871295) and the two CarnivalStart gates (3284113694 / 2801997722) are
        //     built locally on every peer, so a resolving hash would instantiate a SECOND door leaf on
        //     clients on top of the one the maze already made — and the Jail instance overrides
        //     useKeyToUnlock/jailCellStartUnlocked, which the root Door_A prefab does not carry;
        //   - the MG_Finish / SeveranceFinishHall1 elevator copies are authoring records that are never
        //     spawned (ProceduralMazeCoordinator spawns the registered Resources/ElevatorFinishSync).
        // ProceduralMazeCoordinator.ReportIfMazeSpawnHashUnregistered reports any unregistered hash the
        // server actually spawns, so a new offender surfaces as an error instead of a silent client drop.
        // Add an EnsureHashOverrideEntry call here only for a nested object that clients do NOT build
        // locally. See [[ngo-prefab-hash-gotcha]] for why the runtime override exists at all.
    }

    static GameObject FindPrefabByBaseHash(NetworkPrefabsList list, uint baseHash)
    {
        if (list == null)
            return null;
        foreach (var entry in list.PrefabList)
        {
            if (entry == null || entry.Prefab == null)
                continue;
            if (!entry.Prefab.TryGetComponent(out NetworkObject networkObject))
                continue;
            if (networkObject.PrefabIdHash == baseHash)
                return entry.Prefab;
        }
        return null;
    }

    static void EnsureHashOverrideEntry(NetworkPrefabsList list, uint sourceHash, GameObject targetPrefab)
    {
        foreach (var entry in list.PrefabList)
        {
            if (entry != null
                && entry.Override == NetworkPrefabOverride.Hash
                && entry.SourceHashToOverride == sourceHash)
            {
                return;
            }
        }

        list.Add(new NetworkPrefab
        {
            Override = NetworkPrefabOverride.Hash,
            SourceHashToOverride = sourceHash,
            OverridingTargetPrefab = targetPrefab,
        });
    }
}
