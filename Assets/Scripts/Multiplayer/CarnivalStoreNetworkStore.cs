using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative, late-join-safe replication of carnival prize-counter sales. Sibling of
/// <see cref="DoorNetworkStateStore"/> / <see cref="ConsumedItemNetworkStore"/> / <see cref="CarnivalRadioNetworkStore"/>,
/// hosted on the same per-level infrastructure NetworkObject (DoorStateStore.prefab) and cleared per maze build.
///
/// The <see cref="CarnivalStore"/> prop is nested inside the deterministically-placed carnival room prefab, so it is
/// NOT Netcode-spawned and cannot adjudicate anything itself. A player clicking BUY therefore asks this store:
/// the server resolves the buyer from the sender client id, re-checks they are actually standing at the counter,
/// debits their <see cref="NetworkPlayerCarnivalTickets"/> wallet, and appends the sale to a <see cref="NetworkList{T}"/>.
///
/// Every peer (host included) then builds the sold item LOCALLY from that list — the goods are ordinary local
/// pickups with a stable id derived from (storeId, sale sequence), the same model chest and maze loot use, so
/// pickup/consumption replicate through the existing item paths. Late joiners get the whole list on spawn and
/// rebuild the counter exactly as it stands; anything already taken is suppressed by
/// <see cref="ConsumedItemNetworkStore"/> or re-attached to its holder by the inventory slot sync.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class CarnivalStoreNetworkStore : NetworkBehaviour
{
    public struct StoreSale : INetworkSerializable, IEquatable<StoreSale>
    {
        /// <summary><see cref="CarnivalStore.StoreId"/> of the counter that sold it.</summary>
        public int StoreId;
        /// <summary>Monotonic per-session sale number; combined with StoreId it seeds the item's stable id.</summary>
        public int Sequence;
        /// <summary>Index into that counter's authored stock list.</summary>
        public int ItemIndex;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref StoreId);
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref ItemIndex);
        }

        public bool Equals(StoreSale other) =>
            StoreId == other.StoreId && Sequence == other.Sequence && ItemIndex == other.ItemIndex;

        public override bool Equals(object obj) => obj is StoreSale other && Equals(other);
        public override int GetHashCode() => (StoreId * 397) ^ Sequence;
    }

    /// <summary>Extra slack on the server's range re-check, so a buyer who shuffled a step isn't refused.</summary>
    const float ServerRangeSlack = 1.5f;

    /// <summary>The spawned store instance, or null when no session is running / not yet spawned.</summary>
    public static CarnivalStoreNetworkStore Instance { get; private set; }

    readonly NetworkList<StoreSale> _sales = new(
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    /// <summary>
    /// Server-only. Never reset by <see cref="ServerClear"/>: sale numbers seed the sold items' stable ids, and
    /// reusing one after a level switch would collide with an id a peer still has registered.
    /// </summary>
    int _nextSaleSequence;

    public override void OnNetworkSpawn()
    {
        Instance = this;

        // The goods are local builds driven by shared state, so EVERY peer (host included) applies the list and
        // tracks changes — same as the radio store, unlike the door store where the server drives visuals directly.
        for (int i = 0; i < _sales.Count; i++)
            ApplySale(_sales[i]);

        _sales.OnListChanged += OnSalesListChanged;
    }

    public override void OnNetworkDespawn()
    {
        _sales.OnListChanged -= OnSalesListChanged;

        if (Instance == this)
            Instance = null;
    }

    void OnSalesListChanged(NetworkListEvent<StoreSale> change)
    {
        switch (change.Type)
        {
            case NetworkListEvent<StoreSale>.EventType.Add:
            case NetworkListEvent<StoreSale>.EventType.Insert:
            case NetworkListEvent<StoreSale>.EventType.Value:
                ApplySale(change.Value);
                break;
        }
    }

    static void ApplySale(StoreSale sale)
    {
        if (CarnivalStore.TryResolve(sale.StoreId, out CarnivalStore store) && store != null)
            store.ApplyDispense(sale.Sequence, sale.ItemIndex);
    }

    /// <summary>
    /// Client/host: rebuild the sales already on the books for a counter that finished building after the store
    /// synced (late joiner, or a maze built after the infrastructure object spawned). <see cref="CarnivalStore.ApplyDispense"/>
    /// is idempotent, so replaying a sale the counter already dispensed is a no-op.
    /// </summary>
    public static void ApplyCurrentSalesToStore(CarnivalStore store)
    {
        if (Instance == null || store == null)
            return;

        for (int i = 0; i < Instance._sales.Count; i++)
        {
            StoreSale sale = Instance._sales[i];
            if (sale.StoreId == store.StoreId)
                store.ApplyDispense(sale.Sequence, sale.ItemIndex);
        }
    }

    // ---- Client/host request path -------------------------------------------

    /// <summary>Any peer: ask the server to sell item <paramref name="itemIndex"/> at counter <paramref name="storeId"/>.</summary>
    public static void RequestPurchase(int storeId, int itemIndex)
    {
        if (Instance != null)
            Instance.PurchaseRequestServerRpc(storeId, itemIndex);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void PurchaseRequestServerRpc(int storeId, int itemIndex, RpcParams rpcParams = default)
    {
        ulong senderClientId = rpcParams.Receive.SenderClientId;
        CarnivalStorePurchaseResult result = ServerTrySell(senderClientId, storeId, itemIndex);
        PurchaseResultRpc(storeId, itemIndex, (byte)result, RpcTarget.Single(senderClientId, RpcTargetUse.Temp));
    }

    /// <summary>
    /// Server-only. Validates the sale against the server's own view of the world (never a client-supplied
    /// position or price), debits the wallet, and records the sale for every peer to build.
    /// </summary>
    CarnivalStorePurchaseResult ServerTrySell(ulong buyerClientId, int storeId, int itemIndex)
    {
        if (!IsServer)
            return CarnivalStorePurchaseResult.Unavailable;

        if (!CarnivalStore.TryResolve(storeId, out CarnivalStore store) || store == null)
            return CarnivalStorePurchaseResult.Unavailable;

        if (!store.TryGetStock(itemIndex, out CarnivalStoreStockEntry entry) || entry.prefab == null)
            return CarnivalStorePurchaseResult.Unavailable;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null
            || !nm.ConnectedClients.TryGetValue(buyerClientId, out NetworkClient client)
            || client.PlayerObject == null)
        {
            return CarnivalStorePurchaseResult.Unavailable;
        }

        // Range is re-checked here so a client cannot buy from across the map by faking the UI state.
        if (!store.IsInInteractRange(client.PlayerObject.transform.position, ServerRangeSlack))
            return CarnivalStorePurchaseResult.OutOfRange;

        NetworkPlayerCarnivalTickets wallet = client.PlayerObject.GetComponent<NetworkPlayerCarnivalTickets>();
        if (wallet == null)
            return CarnivalStorePurchaseResult.Unavailable;

        // A free item still has to be a real sale, but ServerTrySpend rejects non-positive amounts.
        if (entry.price > 0 && !wallet.ServerTrySpend(entry.price))
            return CarnivalStorePurchaseResult.NotEnoughTickets;

        _sales.Add(new StoreSale
        {
            StoreId = storeId,
            Sequence = _nextSaleSequence++,
            ItemIndex = itemIndex,
        });

        return CarnivalStorePurchaseResult.Granted;
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void PurchaseResultRpc(int storeId, int itemIndex, byte result, RpcParams rpcParams = default)
    {
        if (!CarnivalStore.TryResolve(storeId, out CarnivalStore store) || store == null)
            return;

        CarnivalStoreOverlayController.NotifyPurchaseResult(store, itemIndex, (CarnivalStorePurchaseResult)result);
    }

    // ---- Server API ---------------------------------------------------------

    /// <summary>Server-only: drop all sales (called when a new maze is built so state is scoped per level).</summary>
    public static void ServerClear()
    {
        if (Instance != null && Instance.IsServer)
            Instance._sales.Clear();
    }
}
