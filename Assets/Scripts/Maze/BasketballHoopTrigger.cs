using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Trigger collider at the top of the basketball hoop. Only registers a score when the active
/// basketball passes downward through it (so a ball thrown up through the rim doesn't count).
/// Scoring is server-authoritative — the trigger fires on every peer's local physics, but only the
/// server forwards the event to the controller.
/// </summary>
[DisallowMultipleComponent]
public sealed class BasketballHoopTrigger : MonoBehaviour
{
    [SerializeField] BasketballGameController controller;

    [Tooltip("Maximum upward Y velocity (m/s) that still counts as 'going down'. Keep ≤ 0 for strict top-to-bottom; raise slightly to forgive grazing rim hits.")]
    [SerializeField] float maxAcceptedUpwardVelocity = 0f;

    void Reset()
    {
        controller = GetComponentInParent<BasketballGameController>();
    }

    void OnTriggerEnter(Collider other)
    {
        if (controller == null || !controller.IsActive)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;
        if (other == null)
            return;

        HeavyThrowableHoldItem ball = other.GetComponentInParent<HeavyThrowableHoldItem>();
        if (ball == null)
            return;

        Rigidbody rb = ball.ItemRigidbody;
        if (rb == null)
            return;
        if (rb.linearVelocity.y > maxAcceptedUpwardVelocity)
            return;

        NetworkObject ballNet = ball.GetComponentInParent<NetworkObject>();
        if (ballNet == null)
            return;

        controller.ServerOnBasketScored(ballNet);
    }
}
