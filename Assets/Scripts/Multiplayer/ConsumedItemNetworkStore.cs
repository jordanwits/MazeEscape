using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative, late-join-safe record of world items that were permanently CONSUMED or removed
/// this level (bandage used, key used, a world glowstick fully merged into an existing stack). Sibling of
/// <see cref="DoorNetworkStateStore"/> and hosted on the same per-level infrastructure NetworkObject.
///
/// The problem: consumable world items (chest loot, seed-placed pickups) are NOT Netcode-spawned — every
/// peer builds an identical LOCAL copy from the deterministic maze/chest seed and shares a stable item id.
/// Consumption was replicated only by a one-shot <c>ConsumeItemClientRpc</c> / <c>RemoveWorldItemClientRpc</c>
/// that destroys the copy on currently-connected clients. A client joining later deterministically rebuilds
/// the already-consumed item and is left with a permanent ghost: a bandage/key lying where it was used, an
/// interactable whose pickup silently fails on the server (the id no longer resolves).
///
/// This store replicates the set of consumed item ids in a <see cref="NetworkList{T}"/>. NGO syncs the
/// current contents to every client on spawn (late joiners included) and delivers later additions reliably.
/// A client destroys any locally-registered item whose id is tombstoned, and a freshly-built item
/// self-checks the tombstone set on registration (see <see cref="GrabbableInventoryItem"/>), so the ghost is
/// removed whether it was built before or after the store synced. Entries are scoped per level
/// (<see cref="ServerClear"/> on each maze build).
/// </summary>
[DisallowMultipleComponent]
public sealed class ConsumedItemNetworkStore : NetworkBehaviour
{
    /// <summary>The spawned store instance, or null when no session is running / not yet spawned.</summary>
    public static ConsumedItemNetworkStore Instance { get; private set; }

    readonly NetworkList<ulong> _consumedItemIds = new(
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        Instance = this;

        // The server destroyed the real item at consume time, so it never has a ghost to clean up. Pure
        // clients destroy any already-built ghost for each tombstoned id, then track subsequent additions.
        if (!IsServer)
        {
            for (int i = 0; i < _consumedItemIds.Count; i++)
                DestroyLocalGhost(_consumedItemIds[i]);

            _consumedItemIds.OnListChanged += OnConsumedListChanged;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer)
            _consumedItemIds.OnListChanged -= OnConsumedListChanged;

        if (Instance == this)
            Instance = null;
    }

    void OnConsumedListChanged(NetworkListEvent<ulong> change)
    {
        switch (change.Type)
        {
            case NetworkListEvent<ulong>.EventType.Add:
            case NetworkListEvent<ulong>.EventType.Insert:
            case NetworkListEvent<ulong>.EventType.Value:
                DestroyLocalGhost(change.Value);
                break;
        }
    }

    static void DestroyLocalGhost(ulong itemId)
    {
        if (itemId == 0UL)
            return;
        if (GrabbableInventoryItem.TryGetRegistered(itemId, out GrabbableInventoryItem item) && item != null)
            Destroy(item.gameObject);
    }

    // ---- Server API ---------------------------------------------------------

    /// <summary>Server-only: record that the item id was permanently consumed/removed this level.</summary>
    public static void ServerMarkConsumed(ulong itemId)
    {
        if (itemId == 0UL || Instance == null || !Instance.IsServer)
            return;

        for (int i = 0; i < Instance._consumedItemIds.Count; i++)
        {
            if (Instance._consumedItemIds[i] == itemId)
                return;
        }

        Instance._consumedItemIds.Add(itemId);
    }

    /// <summary>Server-only: drop all tombstones (called when a new maze is built so state is scoped per level).</summary>
    public static void ServerClear()
    {
        if (Instance != null && Instance.IsServer)
            Instance._consumedItemIds.Clear();
    }

    /// <summary>
    /// Client-side: has this item id been consumed this level? Used by a freshly-built local item to
    /// self-destruct if it is a rebuilt ghost of an already-consumed pickup. Returns false when no store is
    /// spawned (offline / server-authoritative host, which has no ghosts to suppress).
    /// </summary>
    public static bool IsConsumed(ulong itemId)
    {
        if (itemId == 0UL || Instance == null)
            return false;

        for (int i = 0; i < Instance._consumedItemIds.Count; i++)
        {
            if (Instance._consumedItemIds[i] == itemId)
                return true;
        }

        return false;
    }
}
