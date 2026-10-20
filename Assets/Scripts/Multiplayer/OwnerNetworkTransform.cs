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

    /// <summary>
    /// Every state this transform sends carries all three position axes. NGO sends only the axes that moved past
    /// the threshold, and with <see cref="NetworkTransform.SwitchTransformSpaceWhenParented"/> on (the Jailor carry
    /// needs it) a receiver fills each axis left out of an update from its CURRENT interpolated value, which trails
    /// the newest target by the interpolation delay. So after a landing or a step down, the first walking update
    /// without a Y change pinned the observer's Y wherever the lagging fall had reached and the player walked on
    /// hovering above the floor until their height changed again. A zero threshold means no axis is ever left out.
    /// The cost is a small position update every tick even while standing still.
    /// </summary>
    protected override void Awake()
    {
        base.Awake();
        PositionThreshold = 0f;
    }

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
