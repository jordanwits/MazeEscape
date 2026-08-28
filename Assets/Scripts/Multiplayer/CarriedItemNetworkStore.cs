using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>One hotbar item carried through an elevator into this section, replicated so a peer that was not
/// present at the switch can rebuild it.</summary>
public struct CarriedItemState : INetworkSerializable, IEquatable<CarriedItemState>
{
    public ulong ItemId;
    public byte TypeId;
    public byte StackCount;
    /// <summary>0…1 charge as captured at the switch; only meaningful for a flashlight.</summary>
    public float ChargeNormalized;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ItemId);
        serializer.SerializeValue(ref TypeId);
        serializer.SerializeValue(ref StackCount);
        serializer.SerializeValue(ref ChargeNormalized);
    }

    public bool Equals(CarriedItemState other) =>
        ItemId == other.ItemId
        && TypeId == other.TypeId
        && StackCount == other.StackCount
        && ChargeNormalized.Equals(other.ChargeNormalized);

    public override bool Equals(object obj) => obj is CarriedItemState other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(ItemId, TypeId, StackCount, ChargeNormalized);
}

/// <summary>
/// Server-authoritative, late-join-safe record of the LOCAL (seed-built) hotbar items players carried into this
/// section from the previous one. Sibling of <see cref="DoorNetworkStateStore"/>,
/// <see cref="ConsumedItemNetworkStore"/> and <see cref="StackedDropNetworkStore"/>, hosted on the same
/// per-level infrastructure NetworkObject.
///
/// The problem is the same shape as <see cref="StackedDropNetworkStore"/>'s, one level up. A carried item is not
/// Netcode-spawned and is not derivable from this section's maze seed either: it belongs to the PREVIOUS
/// section, and <see cref="LevelCarryOverStore"/> keeps it alive only by parking the surviving object in the
/// <see cref="LevelCarryOverPen"/> on each peer that was connected at the moment of the switch. A player who
/// joins afterwards builds only this section, so no object with that item id ever exists on their machine and
/// every resolution path — the slot refresh, the world-item snapshot, a later drop — is exact-id and simply
/// fails. They saw their teammates permanently empty-handed for anything brought in from the previous section,
/// and a handed-over item was invisible on the floor and unpickable, for the rest of the run.
///
/// Replicating the carried items as a <see cref="NetworkList{T}"/> closes it: NGO delivers the current contents
/// to late joiners on spawn, and a joining peer instantiates its own local copy from the item's type and leaves
/// it parked in the same inert state a peer that WAS present leaves its copy in, so the ordinary replicated
/// paths (<c>RefreshInventoryViewFromNetwork</c>, the world-item snapshot) seat it from there. Rebuilding is
/// idempotent: an id that already resolves locally is skipped, which is what makes this a no-op for every peer
/// that was present at the switch. Entries are scoped to one section (<see cref="ServerClear"/>).
///
/// Network-spawned carried items (the Jailor key) are deliberately NOT recorded here: the server re-spawns those
/// from their prefab hash (<c>ServerTrySpawnCarriedNetworkItem</c>) and NGO synchronizes the replacement to a
/// joiner by itself.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarriedItemNetworkStore : NetworkBehaviour
{
    const string SectionConfigsResourcesPath = "MazeConfigs";

    /// <summary>Far below any level geometry, matching <see cref="LevelCarryOverPen"/>.</summary>
    static readonly Vector3 ParkPosition = new Vector3(0f, -5000f, 0f);

    /// <summary>The spawned store instance, or null when no session is running / not yet spawned.</summary>
    public static CarriedItemNetworkStore Instance { get; private set; }

    readonly NetworkList<CarriedItemState> _carried = new(
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    static readonly Dictionary<byte, GameObject> PrefabByTypeId = new();
    static readonly HashSet<byte> WarnedMissingPrefabTypeIds = new();
    static bool s_prefabsIndexed;
    static Transform s_parkingRoot;

    public override void OnNetworkSpawn()
    {
        Instance = this;

        // The server holds the real objects — they rode the carry-over pen through the load — so it has nothing
        // to rebuild.
        if (!IsServer)
        {
            for (int i = 0; i < _carried.Count; i++)
                TryBuildLocalCarriedItem(_carried[i]);

            _carried.OnListChanged += OnCarriedListChanged;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer)
            _carried.OnListChanged -= OnCarriedListChanged;

        if (Instance == this)
            Instance = null;
    }

    void OnCarriedListChanged(NetworkListEvent<CarriedItemState> change)
    {
        switch (change.Type)
        {
            case NetworkListEvent<CarriedItemState>.EventType.Add:
            case NetworkListEvent<CarriedItemState>.EventType.Insert:
            case NetworkListEvent<CarriedItemState>.EventType.Value:
                TryBuildLocalCarriedItem(change.Value);
                break;
        }
    }

    /// <summary>
    /// Builds this peer's local copy for one entry. Returns false only when no prefab could be resolved for the
    /// item's type, which is the case <see cref="ReplayPendingAfterLocalWorldBuild"/> comes back for.
    /// </summary>
    static bool TryBuildLocalCarriedItem(CarriedItemState state)
    {
        if (state.ItemId == 0UL)
            return true; // nothing to build; never worth retrying

        // Already here — this peer was present at the switch and its own copy rode the pen across, or an earlier
        // pass built the clone. Replay must not duplicate it, and a present peer must not be disturbed at all.
        if (GrabbableInventoryItem.TryGetRegistered(state.ItemId, out GrabbableInventoryItem existing)
            && existing != null)
        {
            return true;
        }

        GameObject prefab = FindPrefabForTypeId(state.TypeId);
        if (prefab == null)
        {
            WarnMissingPrefabOnce(state.TypeId);
            return false;
        }

        BuildLocalCopy(prefab, state);
        return true;
    }

    static void BuildLocalCopy(GameObject prefab, CarriedItemState state)
    {
        Transform parking = EnsureLevelParkingRoot();
        GameObject clone = Instantiate(prefab, parking);
        if (!clone.TryGetComponent(out GrabbableInventoryItem item) || item == null)
        {
            Destroy(clone);
            return;
        }

        item.AssignNetworkItemId(state.ItemId);
        if (item.IsStackable)
            item.SetStackCount(Mathf.Max(1, state.StackCount));
        if (item is FlashlightItem flashlight)
            flashlight.ApplyCarriedBattery(state.ChargeNormalized);

        // Same call the carry-over uses on a peer that WAS present at the switch, against this level's parking
        // root instead of the pen: it leaves the copy inert, hidden and out of reach until the replicated
        // inventory seats it on its carrier, or the world-item snapshot places it if it has since been dropped.
        item.PrepareForLevelCarryOver(parking);
    }

    /// <summary>
    /// Where a freshly built copy waits to be claimed. Deliberately NOT the DontDestroyOnLoad
    /// <see cref="LevelCarryOverPen"/>: a copy built for an item that has already been dropped leaves its
    /// parking parent by unparenting, and a GameObject keeps the scene of the parent it was last under — parked
    /// in the pen it would stay in DontDestroyOnLoad and bleed into the next section as an item no other peer
    /// has. Level-owned like every other local pickup, so it dies with the section. If this root is ever built
    /// against the wrong scene it is torn down with that scene, taking the unclaimed copy with it, and
    /// <see cref="ReplayPendingAfterLocalWorldBuild"/> rebuilds it once the real level is up.
    /// </summary>
    static Transform EnsureLevelParkingRoot()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        if (s_parkingRoot != null && s_parkingRoot.gameObject.scene == activeScene)
            return s_parkingRoot;

        GameObject root = new GameObject("CarriedItemParking");
        root.transform.position = ParkPosition;
        s_parkingRoot = root.transform;
        return s_parkingRoot;
    }

    // ---- Prefab table -------------------------------------------------------

    /// <summary>
    /// Item prefab for a replicated type id. Carried items come from the PREVIOUS section, so the lookup spans
    /// every section config's loot pool rather than the one this level happens to use: a sword picked up in the
    /// dungeon has to be rebuildable in the carnival, and the office section has no loot pool at all.
    /// </summary>
    static GameObject FindPrefabForTypeId(byte typeId)
    {
        if (typeId == GrabbableInventoryItem.TypeIdNone)
            return null;

        if (!s_prefabsIndexed)
            IndexItemPrefabs();

        if (PrefabByTypeId.TryGetValue(typeId, out GameObject prefab) && prefab == null)
        {
            IndexItemPrefabs();
            PrefabByTypeId.TryGetValue(typeId, out prefab);
        }

        return prefab;
    }

    static void IndexItemPrefabs()
    {
        PrefabByTypeId.Clear();

        foreach (ProceduralMazeConfig config in Resources.LoadAll<ProceduralMazeConfig>(SectionConfigsResourcesPath))
        {
            if (config == null)
                continue;

            foreach (GameObject prefab in config.MazeItemSpawnPrefabs)
                TryIndexPrefab(prefab);
        }

        // Only cache the table as final once the second source was actually readable; a half-built table would
        // otherwise stick for the rest of the domain.
        s_prefabsIndexed = IndexUniquelyTypedNetworkPrefabs();
    }

    static void TryIndexPrefab(GameObject prefab)
    {
        if (prefab == null || !prefab.TryGetComponent(out GrabbableInventoryItem item) || item == null)
            return;

        byte typeId = item.ItemTypeId;
        if (typeId == GrabbableInventoryItem.TypeIdNone || PrefabByTypeId.ContainsKey(typeId))
            return;

        PrefabByTypeId[typeId] = prefab;
    }

    /// <summary>
    /// Second source for the table: the registered network prefab list, which is where a type that no loot pool
    /// lists still lives (a chest's flashlight is a plain local instantiate of a prefab that only appears there).
    /// A type claimed by more than one prefab is deliberately left unresolved rather than guessed — several
    /// unrelated prefabs share <see cref="GrabbableInventoryItem.TypeIdKey"/>, and rebuilding a carried item as
    /// the wrong object is worse than not rebuilding it.
    /// </summary>
    static bool IndexUniquelyTypedNetworkPrefabs()
    {
        NetworkManager nm = NetworkManager.Singleton;
        NetworkConfig config = nm != null ? nm.NetworkConfig : null;
        if (config == null || config.Prefabs == null || config.Prefabs.NetworkPrefabsLists == null)
            return false;

        Dictionary<byte, GameObject> uniqueByTypeId = new();
        HashSet<byte> ambiguousTypeIds = new();

        foreach (NetworkPrefabsList list in config.Prefabs.NetworkPrefabsLists)
        {
            if (list == null || list.PrefabList == null)
                continue;

            foreach (NetworkPrefab entry in list.PrefabList)
            {
                GameObject prefab = entry != null ? entry.Prefab : null;
                if (prefab == null || !prefab.TryGetComponent(out GrabbableInventoryItem item) || item == null)
                    continue;

                byte typeId = item.ItemTypeId;
                if (typeId == GrabbableInventoryItem.TypeIdNone || PrefabByTypeId.ContainsKey(typeId))
                    continue;

                if (uniqueByTypeId.TryGetValue(typeId, out GameObject first) && first != prefab)
                {
                    ambiguousTypeIds.Add(typeId);
                    continue;
                }

                uniqueByTypeId[typeId] = prefab;
            }
        }

        foreach (KeyValuePair<byte, GameObject> pair in uniqueByTypeId)
        {
            if (!ambiguousTypeIds.Contains(pair.Key))
                PrefabByTypeId[pair.Key] = pair.Value;
        }

        return true;
    }

    static void WarnMissingPrefabOnce(byte typeId)
    {
        if (!WarnedMissingPrefabTypeIds.Add(typeId))
            return;

        Debug.LogWarning(
            $"[{nameof(CarriedItemNetworkStore)}] No pickup prefab found for carried item type {typeId};"
            + " this peer cannot rebuild it, so a teammate carrying one in from the previous section shows"
            + " empty-handed here. Add the prefab to a ProceduralMazeConfig's Maze Item Spawn Prefabs.");
    }

    // ---- Server API ---------------------------------------------------------

    /// <summary>
    /// Server-only: record one LOCAL item a player carried into the section that has just started. Called from
    /// <see cref="NetworkPlayerInventory.ServerRestoreCarriedInventory"/> as each surviving item is re-seated.
    /// </summary>
    public static void ServerRecordCarriedItem(ulong itemId, byte typeId, int stackCount, float chargeNormalized)
    {
        if (itemId == 0UL || Instance == null || !Instance.IsServer)
            return;

        for (int i = 0; i < Instance._carried.Count; i++)
        {
            if (Instance._carried[i].ItemId == itemId)
                return;
        }

        Instance._carried.Add(new CarriedItemState
        {
            ItemId = itemId,
            TypeId = typeId,
            StackCount = (byte)Mathf.Clamp(stackCount, 0, byte.MaxValue),
            ChargeNormalized = Mathf.Clamp01(chargeNormalized),
        });
    }

    /// <summary>
    /// Server-only: drop all records so they are scoped to one section. Called from
    /// <see cref="LevelCarryOverStore.ServerCaptureAllPlayers"/> — i.e. from the elevator, before the load —
    /// rather than from the maze build like its siblings, because the build and the player restore that refills
    /// this list race each other in the next section and clearing on the build can wipe what it just wrote.
    /// </summary>
    public static void ServerClear()
    {
        if (Instance != null && Instance.IsServer)
            Instance._carried.Clear();
    }

    /// <summary>
    /// Client-side: retry any entry that could not be built when it arrived. Call once the local level build has
    /// registered its pickups, alongside the other post-build repairs.
    /// </summary>
    public static void ReplayPendingAfterLocalWorldBuild()
    {
        if (Instance == null || Instance.IsServer || !Instance.IsSpawned)
            return;

        for (int i = 0; i < Instance._carried.Count; i++)
            TryBuildLocalCarriedItem(Instance._carried[i]);
    }
}
