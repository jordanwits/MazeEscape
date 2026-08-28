using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Trigger volume spanning a single hole opening in the HoleBoard (a "skee-board" panel). A throw only
/// counts when the ball passes all the way THROUGH the hole: it must enter the volume from the FRONT
/// side of the board and leave from the REAR side. Which side the ball is on is judged by the sign of
/// its offset along the board's through-axis (<see cref="ThroughAxis"/> = the trigger's local forward,
/// authored to point toward the rear) relative to the trigger's centre. A ball that strikes the board
/// and bounces leaves on the same (front) side it entered, so it does not score.
/// <para>
/// Because the make is decided by a front→rear plane crossing rather than a fixed travel distance, the
/// box collider can be resized freely (depth along the through-axis only needs to be deep enough to
/// catch the ball as it passes — it has no effect on the scoring threshold). It also works regardless of
/// how the booth is rotated when the maze places it, since the axis is read in world space.
/// </para>
/// Scoring is server-authoritative — the trigger fires on every peer's local physics, but only the
/// server evaluates the make and forwards its <see cref="points"/> to the controller.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public sealed class HoleBoardHoleTrigger : MonoBehaviour
{
    [SerializeField, Tooltip("Controller notified when a ball passes through. Auto-resolved from a parent on Reset/Awake.")]
    HoleBoardGameController controller;

    [SerializeField, Min(0), Tooltip("Points awarded when a ball is sunk through this hole.")]
    int points = 10;

    /// <summary>Front→rear direction the ball must travel. Authored as the trigger's local forward.</summary>
    Vector3 ThroughAxis => transform.forward;

    public int Points => points;

    // Server-only pass tracking, keyed by ball, so several of the round's balls inside this hole at
    // once are each judged on their own crossing. Entries live only for the length of a pass.
    readonly Dictionary<ulong, float> _enterPlaneOffsets = new Dictionary<ulong, float>(4);

    void Reset()
    {
        controller = GetComponentInParent<HoleBoardGameController>(true);
        Collider col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    void Awake()
    {
        if (controller == null)
            controller = GetComponentInParent<HoleBoardGameController>(true);
    }

    void OnTriggerEnter(Collider other)
    {
        if (!TryResolveServerBall(other, out NetworkObject ballNet))
            return;

        // Start tracking this ball's pass; the make is judged when it leaves the volume. We record which
        // side of the board plane the ball entered on so the exit can tell a clean pass-through (the ball
        // crossed to the far side) from a rim bounce (it left on the same side it came in).
        _enterPlaneOffsets[ballNet.NetworkObjectId] = PlaneOffset(ballNet.transform.position);
    }

    void OnTriggerExit(Collider other)
    {
        if (_enterPlaneOffsets.Count == 0)
            return;
        if (!TryResolveServerBall(other, out NetworkObject ballNet))
            return;
        if (!_enterPlaneOffsets.TryGetValue(ballNet.NetworkObjectId, out float enterOffset))
            return;

        // This ball has left the volume — its pass is resolved, sink or miss.
        _enterPlaneOffsets.Remove(ballNet.NetworkObjectId);

        if (controller == null || !controller.IsActive)
            return;

        // A clean sink crosses the board plane: it entered on one face and left through the other, so the
        // signed offsets have opposite signs. A rim bounce enters and leaves on the same side (same sign).
        float exitOffset = PlaneOffset(ballNet.transform.position);
        if (enterOffset * exitOffset >= 0f)
            return;

        controller.ServerOnHoleScored(ballNet, points);
    }

    /// <summary>
    /// Server-only. Drops every in-progress pass. Called by <see cref="HoleBoardGameController"/> when a
    /// round's balls are spawned or cleared, so a ball that despawned inside the hole leaves nothing behind.
    /// </summary>
    public void ServerClearPassTracking() => _enterPlaneOffsets.Clear();

    /// <summary>
    /// Signed distance of <paramref name="worldPos"/> from the board plane, measured along the
    /// through-axis. Opposite signs at enter vs exit mean the ball passed all the way through.
    /// </summary>
    float PlaneOffset(Vector3 worldPos) => Vector3.Dot(worldPos - transform.position, ThroughAxis);

    /// <summary>
    /// True only on the server when <paramref name="other"/> belongs to one of the balls spawned for the
    /// round in progress. Outs the ball's <see cref="NetworkObject"/>. Any other throwable carried into
    /// the hole — a stray ball, a ring from the next booth — is ignored so it cannot displace a real pass.
    /// </summary>
    bool TryResolveServerBall(Collider other, out NetworkObject ballNet)
    {
        ballNet = null;
        if (other == null || controller == null)
            return false;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return false;

        HeavyThrowableHoldItem ball = other.GetComponentInParent<HeavyThrowableHoldItem>();
        if (ball == null)
            return false;

        NetworkObject net = ball.GetComponentInParent<NetworkObject>();
        if (net == null || !controller.IsTrackedBall(net))
            return false;

        ballNet = net;
        return true;
    }

    void OnDrawGizmos()
    {
        // Visualises the scoring volume so its size/placement can be tuned. The ball scores by crossing
        // from the front of this box (local -Z) to the rear (local +Z / forward). Colour hints at value.
        Gizmos.matrix = transform.localToWorldMatrix;
        if (!TryGetComponent(out BoxCollider box))
            return;

        Color tint = points >= 40 ? new Color(1f, 0.92f, 0.2f, 1f)   // yellow
            : points >= 10 ? new Color(0.25f, 0.55f, 1f, 1f)         // blue
            : new Color(0.3f, 1f, 0.45f, 1f);                        // green
        Gizmos.color = new Color(tint.r, tint.g, tint.b, 0.18f);
        Gizmos.DrawCube(box.center, box.size);
        Gizmos.color = tint;
        Gizmos.DrawWireCube(box.center, box.size);
    }
}
