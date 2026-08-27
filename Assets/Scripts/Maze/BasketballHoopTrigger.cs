using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Trigger volume spanning the hoop opening (tall enough to cover the rim down into the net throat).
/// A basket only counts when the active ball passes all the way DOWN through the volume: it must
/// enter while descending and then leave having dropped at least <see cref="minDropToScore"/> world
/// metres, still moving down. A ball that clips the rim and bounces back out leaves near the height
/// it entered (or moving up), so it does not score.
/// <para>
/// Judging on the full descent rather than a single entry event is what rejects rim-outs, and it
/// works regardless of how the hoop is rotated because it compares world-space Y.
/// </para>
/// Scoring is server-authoritative — the trigger fires on every peer's local physics, but only the
/// server evaluates the make and forwards it to the controller.
/// </summary>
[DisallowMultipleComponent]
public sealed class BasketballHoopTrigger : MonoBehaviour
{
    [SerializeField] BasketballGameController controller;

    [Tooltip("Maximum upward Y velocity (m/s) that still counts as 'going down'. Checked on both entry and exit. Keep ≤ 0 for strict descents; raise slightly to forgive grazing contacts.")]
    [SerializeField] float maxAcceptedUpwardVelocity = 0f;

    [Tooltip("How far (world metres) the ball must fall between entering and leaving the volume to count as a make. A clean shot drops roughly the full height of the trigger; a rim clip that bounces out barely drops. Set to about half the trigger's world height.")]
    [SerializeField, Min(0f)] float minDropToScore = 0.25f;

    // Server-only descent tracking for the single active ball.
    ulong _trackedBallId;
    float _trackedEntryY;
    bool _tracking;

    // Sampled fall rate for the tracked ball. A ball thrown by a client is owner-authoritative, so the
    // server's copy is a KINEMATIC mirror whose linearVelocity reads ~zero however fast it is really
    // moving (same reason NetworkHeavyThrowableHold.EstimateBodySpeedForSettle samples positions).
    // Judging those on rigidbody velocity would hold host throws to a stricter rim than client throws.
    Transform _trackedBallTransform;
    float _lastSampleY;
    float _sampledUpwardSpeed;
    bool _hasSample;

    void Reset()
    {
        controller = GetComponentInParent<BasketballGameController>();
    }

    void OnTriggerEnter(Collider other)
    {
        if (!TryResolveTrackableBall(other, out NetworkObject ballNet))
            return;

        // Start tracking this descent; the basket is judged when the ball leaves the volume.
        _trackedBallId = ballNet.NetworkObjectId;
        _trackedEntryY = ballNet.transform.position.y;
        _tracking = true;
        _trackedBallTransform = ballNet.transform;
        _lastSampleY = _trackedEntryY;
        _sampledUpwardSpeed = 0f;
        _hasSample = false;
    }

    /// <summary>
    /// Runs before the physics step that raises the trigger callbacks, so by the time
    /// <see cref="OnTriggerExit"/> fires <see cref="_sampledUpwardSpeed"/> covers the last full step.
    /// </summary>
    void FixedUpdate()
    {
        if (!_tracking || _trackedBallTransform == null)
            return;

        float y = _trackedBallTransform.position.y;
        float dt = Time.fixedDeltaTime;
        if (dt > 0f)
        {
            _sampledUpwardSpeed = (y - _lastSampleY) / dt;
            _hasSample = true;
        }
        _lastSampleY = y;
    }

    void OnTriggerExit(Collider other)
    {
        if (!_tracking || other == null)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;

        HeavyThrowableHoldItem ball = other.GetComponentInParent<HeavyThrowableHoldItem>();
        if (ball == null)
            return;
        NetworkObject ballNet = ball.GetComponentInParent<NetworkObject>();
        if (ballNet == null || ballNet.NetworkObjectId != _trackedBallId)
            return;

        // The tracked ball has left the volume — this descent is resolved, win or lose.
        _tracking = false;
        _trackedBallTransform = null;

        if (controller == null || !controller.IsActive)
            return;

        if (!IsMovingDown(ball.ItemRigidbody))
            return; // left while rising — a bounce-out, not a make.

        // Must have fallen most of the way through the volume, not just clipped the rim.
        float drop = _trackedEntryY - ballNet.transform.position.y;
        if (drop < minDropToScore)
            return;

        controller.ServerOnBasketScored(ballNet);
    }

    /// <summary>
    /// Descent test applied identically to a host throw (dynamic body) and a client throw (kinematic
    /// server mirror): read the rigidbody when it is simulating, otherwise the sampled Y history.
    /// With no sample yet — the ball crossed the whole volume inside one step — the drop check in
    /// <see cref="OnTriggerExit"/> is the only guard left, and it already rejects anything that did
    /// not fall through.
    /// </summary>
    bool IsMovingDown(Rigidbody rb)
    {
        if (rb != null && !rb.isKinematic)
            return rb.linearVelocity.y <= maxAcceptedUpwardVelocity;

        return !_hasSample || _sampledUpwardSpeed <= maxAcceptedUpwardVelocity;
    }

    /// <summary>
    /// True only on the server, during an active round, when <paramref name="other"/> belongs to the
    /// basketball and it is not entering the volume from below. Outs the ball's <see cref="NetworkObject"/>.
    /// A kinematic mirror has no usable entry velocity, so it is admitted and judged on exit; a ball
    /// rising up through the hoop exits above where it entered and fails the drop check there.
    /// </summary>
    bool TryResolveTrackableBall(Collider other, out NetworkObject ballNet)
    {
        ballNet = null;

        if (controller == null || !controller.IsActive)
            return false;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return false;
        if (other == null)
            return false;

        HeavyThrowableHoldItem ball = other.GetComponentInParent<HeavyThrowableHoldItem>();
        if (ball == null)
            return false;

        Rigidbody rb = ball.ItemRigidbody;
        if (rb == null)
            return false;
        if (!rb.isKinematic && rb.linearVelocity.y > maxAcceptedUpwardVelocity)
            return false;

        ballNet = ball.GetComponentInParent<NetworkObject>();
        return ballNet != null;
    }

    void OnDrawGizmos()
    {
        // Visualises the scoring volume so its height/placement can be tuned: the ball must fall
        // most of this box's world height to count.
        Gizmos.matrix = transform.localToWorldMatrix;
        if (TryGetComponent(out BoxCollider box))
        {
            Gizmos.color = new Color(0.2f, 1f, 0.3f, 0.18f);
            Gizmos.DrawCube(box.center, box.size);
            Gizmos.color = new Color(0.2f, 1f, 0.3f, 1f);
            Gizmos.DrawWireCube(box.center, box.size);
        }
    }
}
