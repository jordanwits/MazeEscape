using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative, late-join-safe replication of the carnival radio(s) on/off state. Sibling of
/// <see cref="DoorNetworkStateStore"/> / <see cref="ConsumedItemNetworkStore"/>, hosted on the same per-level
/// infrastructure NetworkObject (DoorStateStore.prefab) and cleared per maze build.
///
/// The <see cref="CarnivalRadio"/> prop is nested inside the deterministically-placed carnival room prefab, so it
/// is NOT Netcode-spawned and its own NetworkVariables never go live. A player toggling it must therefore route the
/// request through this spawned store: any client invokes <see cref="ToggleRequestServerRpc"/>, the server flips the
/// replicated state, and every peer (including late joiners and the host) drives its LOCAL AudioSource from the
/// replicated <see cref="NetworkList{T}"/>. Radios default to ON, so only radios that have been toggled carry an
/// entry; an untouched radio just plays from its build default on every peer with no network traffic.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class CarnivalRadioNetworkStore : NetworkBehaviour
{
    public struct RadioNetState : INetworkSerializable, IEquatable<RadioNetState>
    {
        public int RadioId;
        public bool On;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref RadioId);
            serializer.SerializeValue(ref On);
        }

        public bool Equals(RadioNetState other) => RadioId == other.RadioId && On == other.On;
        public override bool Equals(object obj) => obj is RadioNetState other && Equals(other);
        public override int GetHashCode() => RadioId;
    }

    /// <summary>The spawned store instance, or null when no session is running / not yet spawned.</summary>
    public static CarnivalRadioNetworkStore Instance { get; private set; }

    readonly NetworkList<RadioNetState> _radios = new(
        readPerm: NetworkVariableReadPermission.Everyone,
        writePerm: NetworkVariableWritePermission.Server);

    public override void OnNetworkSpawn()
    {
        Instance = this;

        // Audio is local playback driven by shared state, so EVERY peer (host included) applies the list and tracks
        // changes — unlike the door store, where the server drives visuals directly and skips consuming the list.
        for (int i = 0; i < _radios.Count; i++)
            ApplyEntryToRadio(_radios[i]);

        _radios.OnListChanged += OnRadioListChanged;
    }

    public override void OnNetworkDespawn()
    {
        _radios.OnListChanged -= OnRadioListChanged;

        if (Instance == this)
            Instance = null;
    }

    void OnRadioListChanged(NetworkListEvent<RadioNetState> change)
    {
        switch (change.Type)
        {
            case NetworkListEvent<RadioNetState>.EventType.Add:
            case NetworkListEvent<RadioNetState>.EventType.Insert:
            case NetworkListEvent<RadioNetState>.EventType.Value:
                ApplyEntryToRadio(change.Value);
                break;
        }
    }

    static void ApplyEntryToRadio(RadioNetState state)
    {
        if (CarnivalRadio.TryResolve(state.RadioId, out CarnivalRadio radio) && radio != null)
            radio.SetOn(state.On);
    }

    // ---- Client/host request path -------------------------------------------

    /// <summary>Any peer: ask the server to flip the given radio's on/off state. No-op offline (caller toggles locally).</summary>
    public static void RequestToggle(int radioId)
    {
        if (Instance != null)
            Instance.ToggleRequestServerRpc(radioId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ToggleRequestServerRpc(int radioId)
    {
        // Resolve the current state (default ON when there's no entry yet) and write the flipped value back.
        for (int i = 0; i < _radios.Count; i++)
        {
            if (_radios[i].RadioId != radioId)
                continue;

            _radios[i] = new RadioNetState { RadioId = radioId, On = !_radios[i].On };
            return;
        }

        _radios.Add(new RadioNetState { RadioId = radioId, On = false });
    }

    // ---- Server API ---------------------------------------------------------

    /// <summary>Server-only: drop all radio entries (called when a new maze is built so state is scoped per level).</summary>
    public static void ServerClear()
    {
        if (Instance != null && Instance.IsServer)
            Instance._radios.Clear();
    }

    /// <summary>
    /// Client/host: pull the current replicated state for a radio that finished building after the store synced, so it
    /// starts in the right on/off state. No-op (radio keeps its ON build default) when no entry exists or no store is
    /// spawned (offline).
    /// </summary>
    public static void ApplyCurrentStateToRadio(CarnivalRadio radio)
    {
        if (Instance == null || radio == null)
            return;

        for (int i = 0; i < Instance._radios.Count; i++)
        {
            if (Instance._radios[i].RadioId == radio.RadioId)
            {
                radio.SetOn(Instance._radios[i].On);
                return;
            }
        }
    }
}
