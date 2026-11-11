using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Harmonica playback relay. Purely cosmetic and owner-predicted, like the flare gun's muzzle flash: the owner
/// starts and stops the track on its own machine the instant Attack changes, then tells the server, which checks
/// the harmonica really is that player's and narrowcasts the start/stop to every other client.
///
/// The harmonica is a seed-built local pickup (not a NetworkObject), so it is addressed by its stable item id.
/// Only a START is checked against the selected slot; a STOP is relayed for any harmonica the player is
/// carrying, because by the time it arrives the player may already have scrolled away from it (and every peer
/// silences a stashed harmonica on its own anyway).
/// </summary>
public partial class NetworkPlayerInventory
{
    static readonly List<ulong> s_HarmonicaObserverClientIds = new List<ulong>(16);

    /// <summary>Owner-side: the local track started (<paramref name="playing"/>) or stopped.</summary>
    public void NotifyHarmonicaPlayback(ulong itemId, bool playing, int trackIndex)
    {
        if (!IsSpawned || itemId == 0UL)
            return;

        byte track = (byte)Mathf.Clamp(trackIndex, 0, byte.MaxValue);
        if (IsServer)
        {
            ServerRelayHarmonicaPlayback(itemId, playing, track);
            return;
        }

        RequestHarmonicaPlaybackServerRpc(itemId, playing, track);
    }

    [ServerRpc]
    void RequestHarmonicaPlaybackServerRpc(ulong itemId, bool playing, byte track, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerRelayHarmonicaPlayback(itemId, playing, track);
    }

    void ServerRelayHarmonicaPlayback(ulong itemId, bool playing, byte track)
    {
        if (!IsServer)
            return;

        if (playing)
        {
            int sel = SelectedSlotIndex;
            if (GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdHarmonica || GetSlotItemId(sel) != itemId)
                return;

            PlayerHealth health = playerController != null
                ? playerController.GetComponent<PlayerHealth>()
                : GetComponent<PlayerHealth>();
            if (health != null && health.IsDead)
                return;
        }
        else if (!ServerCarriesItem(itemId))
        {
            return;
        }

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        s_HarmonicaObserverClientIds.Clear();
        foreach (ulong id in nm.ConnectedClientsIds)
        {
            if (id != OwnerClientId)
                s_HarmonicaObserverClientIds.Add(id);
        }

        if (s_HarmonicaObserverClientIds.Count == 0)
            return;

        ApplyHarmonicaPlaybackClientRpc(itemId, playing, track, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = s_HarmonicaObserverClientIds.ToArray() }
        });
    }

    bool ServerCarriesItem(ulong itemId)
    {
        for (int i = 0; i < MaxSlotCount; i++)
        {
            if (GetSlotItemId(i) == itemId)
                return true;
        }

        return false;
    }

    [ClientRpc]
    void ApplyHarmonicaPlaybackClientRpc(ulong itemId, bool playing, byte track, ClientRpcParams clientRpcParams = default)
    {
        if (!GrabbableInventoryItem.TryGetRegistered(itemId, out GrabbableInventoryItem g) || g is not HarmonicaItem harmonica)
            return;

        if (playing)
            harmonica.PlayTrack(track, holderIsLocalListener: false);
        else
            harmonica.StopTrack();
    }
}
