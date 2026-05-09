using UnityEngine;

/// <summary>
/// Carnival StarBall: held at the hold point like other grabbables but never uses hotbar slots
/// (handled by <see cref="NetworkStarBallHold"/>).
/// </summary>
[DisallowMultipleComponent]
public class StarBallItem : GrabbableInventoryItem
{
    protected override void Awake()
    {
        _itemTypeId = TypeIdStarBall;
        base.Awake();
    }

    /// <summary>
    /// Heavy ball: <see cref="GrabbableInventoryItem.ApplyNetworkWorldState"/> uses <see cref="ForceMode.Impulse"/>,
    /// which for large mass yields almost no velocity. Star ball releases use <see cref="ForceMode.VelocityChange"/>
    /// so designers set speeds in m/s.
    /// </summary>
    public void ApplyReleasedWorldStateWithVelocityDelta(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocityDelta)
    {
        ApplyNetworkWorldState(worldPosition, worldRotation, default);
        if (velocityDelta.sqrMagnitude <= 0.0001f || ItemRigidbody == null || ItemRigidbody.isKinematic)
            return;

        ItemRigidbody.AddForce(velocityDelta, ForceMode.VelocityChange);
    }
}
