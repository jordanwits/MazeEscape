using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Per-player carnival ticket balance. Tickets are a separate soft currency from the 3-slot hotbar
/// inventory — they accumulate via <see cref="CarnivalTicketBundle"/> pickups and are spent at the
/// ticket booth. Server is the only writer; clients read for HUD via <see cref="Changed"/>.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class NetworkPlayerCarnivalTickets : NetworkBehaviour
{
    readonly NetworkVariable<int> _ticketCount = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public int TicketCount => _ticketCount.Value;

    /// <summary>Fires on every replicated change (previous, current). Also fires once on spawn so HUDs initialize from current state.</summary>
    public event Action<int, int> Changed;

    public override void OnNetworkSpawn()
    {
        _ticketCount.OnValueChanged += HandleTicketCountChanged;
        Changed?.Invoke(0, _ticketCount.Value);
    }

    public override void OnNetworkDespawn()
    {
        _ticketCount.OnValueChanged -= HandleTicketCountChanged;
    }

    /// <summary>Server-only. Increments the player's ticket balance and replicates to all clients.</summary>
    public void ServerAdd(int delta)
    {
        if (!IsServer || delta <= 0)
            return;
        _ticketCount.Value += delta;
    }

    void HandleTicketCountChanged(int previous, int current)
    {
        Changed?.Invoke(previous, current);
    }
}
