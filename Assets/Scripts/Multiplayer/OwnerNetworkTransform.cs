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
}
