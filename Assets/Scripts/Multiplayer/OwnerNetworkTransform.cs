using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Owner-authoritative motion normally. While <see cref="NetworkPlayerAvatar.IsCarriedByJailor"/>, the server
/// parents this object under the Jailor and drives pose — owner deltas would fight reparent and appear offset on remotes.
/// </summary>
public class OwnerNetworkTransform : NetworkTransform
{
    NetworkPlayerAvatar _avatar;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        TryGetComponent(out _avatar);
    }

    protected override bool OnIsServerAuthoritative()
    {
        return _avatar != null && _avatar.IsCarriedByJailor;
    }

    internal void RefreshAuthorityAfterCarryStateChanged()
    {
        if (!IsSpawned)
            return;
        Initialize();
    }

    /// <summary>
    /// On non-authoritative instances, snaps the transform to the latest replicated state and re-initializes
    /// interpolators. Called when a joining client finishes building the procedural maze so remote players
    /// are not drawn at a stale interpolated Y while the floor appears (looked like the host was floating).
    /// </summary>
    public void SnapObserverToLatestNetworkState()
    {
        if (!IsSpawned || CanCommitToTransform)
            return;

        if (NetworkObject != null
            && NetworkObject.transform.parent != null
            && NetworkObject.transform.parent.GetComponentInParent<NetworkObject>() != null)
        {
            return;
        }

        Vector3 pos = GetSpaceRelativePosition(true);
        Quaternion rot = GetSpaceRelativeRotation(true);
        if (InLocalSpace)
        {
            transform.localPosition = pos;
            transform.localRotation = rot;
        }
        else
        {
            transform.SetPositionAndRotation(pos, rot);
        }

        Initialize();
    }
}
