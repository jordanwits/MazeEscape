using UnityEngine;

/// <summary>
/// Heavy throwables (StarBall, ring toss): held outside hotbar via <see cref="NetworkHeavyThrowableHold"/>.
/// Drops use velocity from <see cref="ApplyReleasedWorldStateWithVelocityDelta"/> with
/// <see cref="ForceMode.VelocityChange"/> instead of impulse.
/// </summary>
public abstract class HeavyThrowableHoldItem : GrabbableInventoryItem
{
    public void ApplyReleasedWorldStateWithVelocityDelta(Vector3 worldPosition, Quaternion worldRotation, Vector3 velocityDelta)
    {
        ApplyNetworkWorldState(worldPosition, worldRotation, default);
        if (velocityDelta.sqrMagnitude <= 0.0001f || ItemRigidbody == null || ItemRigidbody.isKinematic)
            return;

        ItemRigidbody.AddForce(velocityDelta, ForceMode.VelocityChange);
    }
}
