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
    /// <summary>
    /// Extra server-side tolerance on the challenge's interact range. The server measures against the owner's
    /// replicated transform, which is stalest right after an authority hand-off (Jailor carry release), and the
    /// cell the win opens is the one the thrower is already standing in — so generous slack costs nothing.
    /// </summary>
    const float SkeletonRpsServerRangeSlack = 3f;

    /// <summary>Local-owner entry point: host resolves directly, clients relay to the server.</summary>
    public void RequestSkeletonRpsThrow(SkeletonRpsChallenge challenge, SkeletonRpsChoice choice)
    {
        if (challenge == null || !IsSpawned || !IsOwner)
            return;

        if (IsServer)
        {
            // The overlay only leaves its waiting state on an answer, so every branch below resolves the throw.
            if (!TryGetConnectedPlayerPosition(OwnerClientId, out Vector3 playerPosition))
            {
                challenge.NotifyThrowResolved(new SkeletonRpsThrowResult
                {
                    PlayerChoice = (byte)choice,
                    RejectReason = (byte)SkeletonRpsRejectReason.Unavailable,
                });
                return;
            }
            if (!challenge.IsInInteractRange(playerPosition, SkeletonRpsServerRangeSlack))
            {
                challenge.NotifyThrowResolved(new SkeletonRpsThrowResult
                {
                    PlayerChoice = (byte)choice,
                    RejectReason = (byte)SkeletonRpsRejectReason.OutOfRange,
                });
                return;
            }

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
        // Spoofed sender: no reply at all — answering would hand a stranger a seat at someone else's game.
        if (senderId != OwnerClientId)
            return;

        SkeletonRpsThrowResult result;
        if (!SkeletonRpsChallenge.TryResolve(challengeId, hintPosition, out SkeletonRpsChallenge challenge) || challenge == null
            || !TryGetConnectedPlayerPosition(senderId, out Vector3 playerPosition))
        {
            // The sender's overlay is waiting on this reply; a miss here still has to say something.
            result = new SkeletonRpsThrowResult
            {
                PlayerChoice = choice,
                RejectReason = (byte)SkeletonRpsRejectReason.Unavailable,
            };
        }
        else if (!challenge.IsInInteractRange(playerPosition, SkeletonRpsServerRangeSlack))
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
