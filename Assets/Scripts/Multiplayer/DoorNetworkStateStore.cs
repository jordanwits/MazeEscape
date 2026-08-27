using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative, late-join-safe replication of procedural <see cref="HingeInteractDoor"/> open/locked state.
///
/// The dungeon jail doors (and carnival gates) are built locally on every peer from the deterministic maze seed and
/// are NOT Netcode-spawned, so their <c>NetworkVariable</c>s never go live. Previously their open/locked state was
/// carried by best-effort <c>ClientRpc</c> mirrors on <see cref="NetworkPlayerInventory"/> plus a one-shot late-join
/// snapshot — a transient transport that silently dropped on <c>IsBusy</c>, mis-resolved on hash divergence, and
/// missed late joiners. That produced the recurring "closed on host / open on client" desync.
///
/// This store replaces that transport with a single replicated <see cref="NetworkList{T}"/> keyed by
/// <see cref="HingeInteractDoor.DoorId"/>. NGO synchronizes the list's current contents to every client on spawn
/// (including late joiners) and delivers subsequent changes reliably, so door state is persistent replicated state
/// rather than fire-and-forget events. Only doors whose state DEVIATES from their deterministic build default get an
/// entry; unchanged doors need none because every peer builds them identically.
///
/// One instance is spawned by <see cref="ProceduralMazeCoordinator"/> per gameplay scene and torn down with the level.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class DoorNetworkStateStore : NetworkBehaviour
{
    public struct DoorNetState : INetworkSerializable, IEquatable<DoorNetState>
    {
        public ulong DoorId;
        public Vector3 HintPosition;
        public bool Open;
        public bool Locked;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref DoorId);
            serializer.SerializeValue(ref HintPosition);
            serializer.SerializeValue(ref Open);
            serializer.SerializeValue(ref Locked);
        }

        public bool Equals(DoorNetState other) =>
            DoorId == other.DoorId
            && Open == other.Open
            && Locked == other.Locked
            && HintPosition.Equals(other.HintPosition);

        public override bool Equals(object obj) => obj is DoorNetState other && Equals(other);
        public override int GetHashCode() => DoorId.GetHashCode();
    }

    /// <summary>The spawned store instance, or null when no session is running / not yet spawned.</summary>
    public static DoorNetworkStateStore Instance { get; private set; }

    readonly NetworkList<DoorNetState> _doors = new(
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        Instance = this;

        // The server drives door visuals directly through HingeInteractDoor's server paths, so it never needs to
        // consume the replicated list. Pure clients apply the current contents (snap, no animation) and then track
        // live changes (animated).
        if (!IsServer)
        {
            for (int i = 0; i < _doors.Count; i++)
                ApplyEntryToDoor(_doors[i], animate: false);

            _doors.OnListChanged += OnDoorListChanged;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer)
            _doors.OnListChanged -= OnDoorListChanged;

        if (Instance == this)
            Instance = null;
    }

    void OnDoorListChanged(NetworkListEvent<DoorNetState> change)
    {
        switch (change.Type)
        {
            case NetworkListEvent<DoorNetState>.EventType.Add:
            case NetworkListEvent<DoorNetState>.EventType.Insert:
            case NetworkListEvent<DoorNetState>.EventType.Value:
                ApplyEntryToDoor(change.Value, animate: true);
                break;
        }
    }

    static void ApplyEntryToDoor(DoorNetState state, bool animate)
    {
        if (HingeInteractDoor.TryResolveForSync(state.DoorId, state.HintPosition, out HingeInteractDoor door)
            && door != null)
        {
            door.ApplyReplicatedDoorState(state.Locked, state.Open, animate);
        }
    }

    // ---- Server API ---------------------------------------------------------

    /// <summary>Server-only: publish the current open/locked state of <paramref name="door"/> (and its paired leaf).</summary>
    public static void ServerPublish(HingeInteractDoor door)
    {
        if (Instance == null || door == null)
            return;

        Instance.ServerUpsert(door);
        if (door.PairedLeaf != null)
            Instance.ServerUpsert(door.PairedLeaf);
    }

    void ServerUpsert(HingeInteractDoor door)
    {
        if (!IsServer || door == null)
            return;

        DoorNetState next = new()
        {
            DoorId = door.DoorId,
            HintPosition = door.IdentityHintPosition,
            Open = door.IsOpen,
            Locked = door.IsLocked,
        };

        for (int i = 0; i < _doors.Count; i++)
        {
            if (_doors[i].DoorId != next.DoorId)
                continue;

            if (!_doors[i].Equals(next))
                _doors[i] = next;
            return;
        }

        _doors.Add(next);
    }

    /// <summary>Server-only: drop all door entries (called when a new maze is built so state is scoped per level).</summary>
    public static void ServerClear()
    {
        if (Instance != null && Instance.IsServer)
            Instance._doors.Clear();
    }

    /// <summary>
    /// Client-side: when a door finishes building after the store already synced, pull its current replicated state
    /// so it snaps to the right pose. No-op on the server (it is authoritative) and when no entry exists (door is at
    /// its deterministic build default).
    /// </summary>
    public static void ApplyCurrentStateToDoor(HingeInteractDoor door)
    {
        if (Instance == null || door == null || Instance.IsServer)
            return;

        ulong doorId = door.DoorId;
        int bestIndex = -1;
        float bestDistanceSquared = float.MaxValue;

        for (int i = 0; i < Instance._doors.Count; i++)
        {
            DoorNetState state = Instance._doors[i];
            if (state.DoorId == doorId)
            {
                bestIndex = i;
                break; // exact id — nothing beats it
            }

            // Id rescue. DoorId folds each ancestor's sibling index into its hash, and the maze root's index
            // among scene roots legitimately differs per peer: a client receives the dynamically spawned
            // NetworkObjects (enemies, traps, the elevator sync) as scene roots BEFORE it builds its own maze,
            // while the host created the maze root first. So a late joiner's ids do not match the host's, and an
            // exact-id-only lookup silently leaves every door at its build default — an unlocked, open jail cell
            // on the joiner while the host has a prisoner sealed in. Every other consumer already survives this
            // through TryResolveForSync's hint-position fallback; this path is the joiner's only chance to see
            // the state at all, so it gets the same rescue. Nearest hint wins if several entries resolve here,
            // which can happen while a paired leaf is still being built.
            if (!HingeInteractDoor.TryResolveForSync(state.DoorId, state.HintPosition, out HingeInteractDoor resolved)
                || resolved != door)
                continue;

            float distanceSquared = (state.HintPosition - door.IdentityHintPosition).sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared)
                continue;

            bestIndex = i;
            bestDistanceSquared = distanceSquared;
        }

        if (bestIndex < 0)
            return;

        DoorNetState chosen = Instance._doors[bestIndex];
        door.ApplyReplicatedDoorState(chosen.Locked, chosen.Open, animate: false);
    }
}
