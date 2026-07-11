using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Network relay for the jail skeleton's rock-paper-scissors game (<see cref="SkeletonRpsChallenge"/>).
/// The challenge component is built locally on every peer by the deterministic maze build and is not
/// Netcode-spawned, so — exactly like the procedural hinge-door RPCs in the main file — throws travel
/// through the player's own spawned inventory object, keyed by the jail door's DoorId + a position hint,
/// and the authoritative result is returned only to the throwing owner.
/// </summary>
public partial class NetworkPlayerInventory
{
    /// <summary>Extra server-side tolerance on the challenge's interact range (owner moved since the client-side check).</summary>
    const float SkeletonRpsServerRangeSlack = 1.5f;

    /// <summary>Local-owner entry point: host resolves directly, clients relay to the server.</summary>
    public void RequestSkeletonRpsThrow(SkeletonRpsChallenge challenge, SkeletonRpsChoice choice)
    {
        if (challenge == null || !IsSpawned || !IsOwner)
            return;

        if (IsServer)
        {
            if (!TryGetConnectedPlayerPosition(OwnerClientId, out Vector3 playerPosition))
                return;
            if (!challenge.IsInInteractRange(playerPosition, SkeletonRpsServerRangeSlack))
                return;

            challenge.ServerProcessThrow(OwnerClientId, choice, out SkeletonRpsThrowResult result);
            // The host is also the throwing player: apply the result locally, no RPC needed.
            challenge.NotifyThrowResolved(result);
            return;
        }

        RequestSkeletonRpsThrowServerRpc(challenge.ChallengeId, challenge.AnchorPosition, (byte)choice);
    }

    [ServerRpc]
    void RequestSkeletonRpsThrowServerRpc(ulong challengeId, Vector3 hintPosition, byte choice, ServerRpcParams serverRpcParams = default)
    {
        ulong senderId = serverRpcParams.Receive.SenderClientId;
        if (senderId != OwnerClientId)
            return;
        if (!SkeletonRpsChallenge.TryResolve(challengeId, hintPosition, out SkeletonRpsChallenge challenge) || challenge == null)
            return;
        if (!TryGetConnectedPlayerPosition(senderId, out Vector3 playerPosition))
            return;

        SkeletonRpsThrowResult result;
        if (!challenge.IsInInteractRange(playerPosition, SkeletonRpsServerRangeSlack))
        {
            result = new SkeletonRpsThrowResult
            {
                PlayerChoice = choice,
                RejectReason = (byte)SkeletonRpsRejectReason.OutOfRange,
            };
        }
        else
        {
            challenge.ServerProcessThrow(senderId, (SkeletonRpsChoice)choice, out result);
        }

        ClientRpcParams toSender = new()
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { senderId } },
        };
        SkeletonRpsThrowResultClientRpc(challengeId, hintPosition, result, toSender);
    }

    [ClientRpc]
    void SkeletonRpsThrowResultClientRpc(ulong challengeId, Vector3 hintPosition, SkeletonRpsThrowResult result, ClientRpcParams clientRpcParams = default)
    {
        if (!IsOwner)
            return;
        if (!SkeletonRpsChallenge.TryResolve(challengeId, hintPosition, out SkeletonRpsChallenge challenge) || challenge == null)
            return;

        challenge.NotifyThrowResolved(result);
    }
}
