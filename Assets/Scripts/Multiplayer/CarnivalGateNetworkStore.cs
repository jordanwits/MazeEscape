using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative, late-join-safe state of the carnival main room's battery gates. Sibling of
/// <see cref="CarnivalRadioNetworkStore"/> / <see cref="CarnivalStoreNetworkStore"/>, hosted on the same per-level
/// infrastructure NetworkObject (DoorStateStore.prefab) and reset on every maze build.
///
/// The gates, their control panel and the battery indicators are built locally inside the deterministically placed
/// CarnivalMainRoom prefab, so none of them is Netcode-spawned and their own NetworkVariables could never go live.
/// Their shared state lives here instead, as ONE replicated <see cref="GateState"/>. It only changes when somebody
/// presses a panel button or the battery runs flat: the battery drains continuously while the gates are closed, so
/// the state records the charge at a server-time stamp and every peer extrapolates the live level from it
/// (<see cref="CarnivalGateSystem.ChargeAt"/>) instead of the server writing a draining value every frame.
/// Rules (costs, validation, ticket spending) live in <see cref="CarnivalGateSystem"/>; this class is transport.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class CarnivalGateNetworkStore : NetworkBehaviour
{
    /// <summary>The gates' shared state. Blittable so it replicates by memcpy.</summary>
    public struct GateState : INetworkSerializeByMemcpy, IEquatable<GateState>
    {
        /// <summary>1 while the gates are closed (or closing), 0 while open.</summary>
        public byte Closed;
        /// <summary>Battery charge 0..1 at <see cref="Stamp"/>. While closed it drains from here.</summary>
        public float Charge;
        /// <summary>Server time (<c>NetworkManager.ServerTime.Time</c>) when <see cref="Charge"/> was recorded.</summary>
        public double Stamp;
        /// <summary>1 while the next Close press costs nothing: set at level start and by every recharge.</summary>
        public byte FreeClose;

        /// <summary>Gates open, battery full, first close free: the state every level starts in.</summary>
        public static GateState Initial => new GateState { Closed = 0, Charge = 1f, Stamp = 0d, FreeClose = 1 };

        public bool IsClosed => Closed != 0;
        public bool HasFreeClose => FreeClose != 0;

        public bool Equals(GateState other) =>
            Closed == other.Closed && Charge.Equals(other.Charge) && Stamp.Equals(other.Stamp)
            && FreeClose == other.FreeClose;

        public override bool Equals(object obj) => obj is GateState other && Equals(other);
        public override int GetHashCode() => Closed ^ (FreeClose << 1) ^ Charge.GetHashCode() ^ Stamp.GetHashCode();
    }

    /// <summary>The spawned store instance, or null when no session is running / not yet spawned.</summary>
    public static CarnivalGateNetworkStore Instance { get; private set; }

    readonly NetworkVariable<GateState> _state = new NetworkVariable<GateState>(
        GateState.Initial,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>True while a spawned store is carrying the gate state for this session.</summary>
    public static bool IsLive => Instance != null && Instance.IsSpawned;

    /// <summary>The replicated gate state. Only meaningful while <see cref="IsLive"/>.</summary>
    public static GateState CurrentState => IsLive ? Instance._state.Value : GateState.Initial;

    public override void OnNetworkSpawn()
    {
        Instance = this;
    }

    public override void OnNetworkDespawn()
    {
        if (Instance == this)
            Instance = null;
    }

    // ---- Client/host request path -------------------------------------------

    /// <summary>Any peer: ask the server to press a gate panel button. No-op when no store is spawned.</summary>
    public static void RequestAction(CarnivalGateSystem.GateAction action)
    {
        if (IsLive)
            Instance.ActionRequestServerRpc((byte)action);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ActionRequestServerRpc(byte action, RpcParams rpcParams = default)
    {
        if (action > (byte)CarnivalGateSystem.GateAction.Recharge)
            return;

        CarnivalGateSystem.ServerHandleRequest((CarnivalGateSystem.GateAction)action, rpcParams.Receive.SenderClientId);
    }

    // ---- Server API ---------------------------------------------------------

    /// <summary>Server-only: publish a new gate state to every peer.</summary>
    public static void ServerWrite(GateState state)
    {
        if (IsLive && Instance.IsServer)
            Instance._state.Value = state;
    }

    /// <summary>Server-only: gates open and battery full again (called when a new maze is built).</summary>
    public static void ServerClear()
    {
        if (IsLive && Instance.IsServer)
            Instance._state.Value = GateState.Initial;
    }
}
