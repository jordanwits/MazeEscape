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
        ApplyReleasedWorldStateWithVelocityDelta(worldPosition, worldRotation, velocityDelta, Vector3.zero);
    }

    public void ApplyReleasedWorldStateWithVelocityDelta(
        Vector3 worldPosition,
        Quaternion worldRotation,
        Vector3 velocityDelta,
        Vector3 angularVelocity)
    {
        ApplyNetworkWorldState(worldPosition, worldRotation, default);
        Rigidbody rb = ItemRigidbody;
        if (rb == null || rb.isKinematic)
            return;

        // Teleport via Rigidbody.position/rotation: the kinematic→dynamic flip in EndHeldState
        // leaves the prior held pose in the Interpolate buffer, so the rendered throw arc
        // appears to start from the carry pose (hand) instead of worldPosition for a frame.
        // Assigning rb.position resets that history.
        rb.position = worldPosition;
        rb.rotation = worldRotation;

        if (velocityDelta.sqrMagnitude > 0.0001f)
            rb.AddForce(velocityDelta, ForceMode.VelocityChange);

        if (angularVelocity.sqrMagnitude > 0.0001f)
            rb.angularVelocity = angularVelocity;
    }
}
