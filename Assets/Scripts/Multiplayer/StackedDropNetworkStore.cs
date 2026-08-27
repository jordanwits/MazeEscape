using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>One unit peeled out of a stack and left in the world, replicated so every peer can rebuild it.</summary>
public struct StackedDropState : INetworkSerializable, IEquatable<StackedDropState>
{
    public ulong TemplateItemId;
    public ulong DroppedItemId;
    public Vector3 Position;
    public Quaternion Rotation;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref TemplateItemId);
        serializer.SerializeValue(ref DroppedItemId);
        serializer.SerializeValue(ref Position);
        serializer.SerializeValue(ref Rotation);
    }

    public bool Equals(StackedDropState other) =>
        TemplateItemId == other.TemplateItemId
        && DroppedItemId == other.DroppedItemId
        && Position == other.Position
        && Rotation == other.Rotation;

    public override bool Equals(object obj) => obj is StackedDropState other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(TemplateItemId, DroppedItemId, Position, Rotation);
}

/// <summary>
/// Server-authoritative, late-join-safe record of stack units dropped one at a time this level (a single
/// glowstick or flare round peeled off a held stack). Sibling of <see cref="DoorNetworkStateStore"/> and
/// <see cref="ConsumedItemNetworkStore"/>, hosted on the same per-level infrastructure NetworkObject.
///
/// The problem this solves is the mirror image of <see cref="ConsumedItemNetworkStore"/>'s. A peeled unit is not
/// a Netcode-spawned object and is not derivable from the maze seed either — it is a runtime clone of the held
/// template under a server-issued id — so the only thing that ever created it on other peers was a one-shot
/// ClientRpc. Any peer that could not resolve the template at that instant silently created nothing and had no
/// retry: a client still building its level, and every player who joined afterwards. The drop then existed for
/// some of the party and not others, with nothing able to reconcile it.
///
/// Replicating the drops as a <see cref="NetworkList{T}"/> fixes both halves at once — NGO delivers the current
/// contents to late joiners on spawn and later additions reliably to everyone — and
/// <see cref="ReplayPendingAfterLocalWorldBuild"/> covers the remaining ordering case, where the entries arrive
/// before this peer's maze (and therefore the template item) exists. Rebuilding is idempotent: an id that
/// already resolves locally is skipped, so replay can run as often as it likes. Entries are scoped per level
/// (<see cref="ServerClear"/> on each maze build).
/// </summary>
[DisallowMultipleComponent]
public sealed class StackedDropNetworkStore : NetworkBehaviour
{
    /// <summary>The spawned store instance, or null when no session is running / not yet spawned.</summary>
    public static StackedDropNetworkStore Instance { get; private set; }

    readonly NetworkList<StackedDropState> _drops = new(
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        Instance = this;

        // The server built its own copy at drop time (ServerRecordPeeledDrop), so it has nothing to rebuild.
        if (!IsServer)
        {
            for (int i = 0; i < _drops.Count; i++)
                TryBuildLocalDrop(_drops[i]);

            _drops.OnListChanged += OnDropListChanged;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer)
            _drops.OnListChanged -= OnDropListChanged;

        if (Instance == this)
            Instance = null;
    }

    void OnDropListChanged(NetworkListEvent<StackedDropState> change)
    {
        switch (change.Type)
        {
            case NetworkListEvent<StackedDropState>.EventType.Add:
            case NetworkListEvent<StackedDropState>.EventType.Insert:
            case NetworkListEvent<StackedDropState>.EventType.Value:
                TryBuildLocalDrop(change.Value);
                break;
        }
    }

    /// <summary>
    /// Builds the local clone for one entry. Returns false when the template does not resolve yet — this peer's
    /// level is still being built — which is exactly the case
    /// <see cref="ReplayPendingAfterLocalWorldBuild"/> comes back for.
    /// </summary>
    static bool TryBuildLocalDrop(StackedDropState state)
    {
        if (state.DroppedItemId == 0UL || state.TemplateItemId == 0UL)
            return true; // nothing to build; never worth retrying

        // Already here (built earlier, or this is the server's own copy) — replay must not duplicate it.
        if (GrabbableInventoryItem.TryGetRegistered(state.DroppedItemId, out GrabbableInventoryItem existing)
            && existing != null)
        {
            return true;
        }

        if (!GrabbableInventoryItem.TryGetRegistered(state.TemplateItemId, out GrabbableInventoryItem template)
            || template == null || !template.IsStackable)
        {
            return false;
        }

        BuildClone(template, state, Vector3.zero);
        return true;
    }

    static void BuildClone(GrabbableInventoryItem template, StackedDropState state, Vector3 throwImpulse)
    {
        GameObject clone = Instantiate(template.gameObject, state.Position, state.Rotation);
        clone.transform.SetParent(null, true);
        if (!clone.TryGetComponent(out GrabbableInventoryItem dropped) || dropped == null)
        {
            Destroy(clone);
            return;
        }

        dropped.AssignNetworkItemId(state.DroppedItemId);
        dropped.SetStackCount(1);
        dropped.ApplyNetworkWorldState(state.Position, state.Rotation, throwImpulse);
        if (dropped is GlowstickItem droppedGlow)
            droppedGlow.SetWorldDroppedVisual();
    }

    // ---- Server API ---------------------------------------------------------

    /// <summary>
    /// Server-only: build the peeled unit locally and replicate it. Falls back to a purely local drop when no
    /// store is spawned (offline play mode), so single-player behaves the same.
    /// </summary>
    public static void ServerRecordPeeledDrop(
        GrabbableInventoryItem template,
        ulong droppedItemId,
        Vector3 position,
        Quaternion rotation,
        Vector3 throwImpulse)
    {
        if (template == null || droppedItemId == 0UL)
            return;

        StackedDropState state = new()
        {
            TemplateItemId = template.ItemId,
            DroppedItemId = droppedItemId,
            Position = position,
            Rotation = rotation,
        };

        // The server keeps the throw impulse so its own copy arcs out of the hand; peers that rebuild from the
        // replicated entry place it at rest, which is where the server's will have come to a stop anyway.
        BuildClone(template, state, throwImpulse);

        if (Instance != null && Instance.IsServer)
            Instance._drops.Add(state);
    }

    /// <summary>Server-only: drop all records (called when a new maze is built so state is scoped per level).</summary>
    public static void ServerClear()
    {
        if (Instance != null && Instance.IsServer)
            Instance._drops.Clear();
    }

    /// <summary>
    /// Client-side: rebuild any entry that could not be built when it arrived because this peer had not yet
    /// created the template item. Call once the local level build has registered its pickups.
    /// </summary>
    public static void ReplayPendingAfterLocalWorldBuild()
    {
        if (Instance == null || Instance.IsServer || !Instance.IsSpawned)
            return;

        for (int i = 0; i < Instance._drops.Count; i++)
            TryBuildLocalDrop(Instance._drops[i]);
    }
}
