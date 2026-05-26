using UnityEngine;

/// <summary>
/// A single scoring peg on the <see cref="RingTossGameController"/> booth. Place the marker at the
/// peg's <b>base</b> (where it meets the deck); the peg is assumed to rise along world up by
/// <see cref="pegHeight"/>. A thrown ring counts as "ringed" on this peg when its torus centre comes
/// to rest horizontally within <see cref="captureRadius"/> of the peg axis and no higher than the peg
/// top. Detection is geometric (evaluated once on the server when the round resolves) rather than
/// collider-based, because a correctly ringed peg passes through the ring's empty hole and never
/// overlaps the ring's solid tube colliders.
/// </summary>
[DisallowMultipleComponent]
public sealed class RingTossPeg : MonoBehaviour
{
    [Tooltip("Tickets/points awarded for a ring that lands on this peg.")]
    [SerializeField, Min(0)] int points = 5;

    [Tooltip("Horizontal distance (world metres) from the peg axis within which a ring centre counts as ringed. Keep below the ring's inner-hole radius so only a threaded ring scores.")]
    [SerializeField, Min(0.01f)] float captureRadius = 0.13f;

    [Tooltip("Peg height (world metres) above this marker. A ring counts only if its centre is at or below the peg top.")]
    [SerializeField, Min(0.01f)] float pegHeight = 0.3f;

    public int Points => points;
    public float CaptureRadius => captureRadius;

    /// <summary>
    /// Horizontal distance from the peg axis to <paramref name="ringCentreWorld"/>, or
    /// <see cref="float.PositiveInfinity"/> if the ring is not ringed on this peg (too far out, or
    /// resting above the peg top). The controller uses the finite distance to pick the nearest peg
    /// when several would capture.
    /// </summary>
    public float HorizontalCaptureDistance(Vector3 ringCentreWorld)
    {
        Vector3 baseP = transform.position;
        float dx = ringCentreWorld.x - baseP.x;
        float dz = ringCentreWorld.z - baseP.z;
        float horizontal = Mathf.Sqrt(dx * dx + dz * dz);
        if (horizontal > captureRadius)
            return float.PositiveInfinity;

        // The ring must be around the peg (centre no higher than the top, allowing a small lip), not
        // perched on the tip or floating above.
        if (ringCentreWorld.y > baseP.y + pegHeight + captureRadius)
            return float.PositiveInfinity;
        if (ringCentreWorld.y < baseP.y - pegHeight)
            return float.PositiveInfinity;

        return horizontal;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        DrawGizmo(0.5f);
    }

    void OnDrawGizmosSelected()
    {
        DrawGizmo(1f);
    }

    void DrawGizmo(float alpha)
    {
        // Capture cylinder: ring centres resting inside this volume score this peg. Position the
        // marker so the cylinder hugs the visible peg.
        Color c = points >= 20 ? new Color(0.3f, 0.6f, 1f, alpha)
            : points >= 10 ? new Color(0.3f, 1f, 0.4f, alpha)
            : new Color(1f, 0.85f, 0.2f, alpha);

        Vector3 baseP = transform.position;
        Vector3 top = baseP + Vector3.up * pegHeight;
        int seg = 20;
        Gizmos.color = c;
        Vector3 prevB = baseP + Vector3.right * captureRadius;
        Vector3 prevT = top + Vector3.right * captureRadius;
        for (int i = 1; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            Vector3 r = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * captureRadius;
            Vector3 b = baseP + r;
            Vector3 t = top + r;
            Gizmos.DrawLine(prevB, b);
            Gizmos.DrawLine(prevT, t);
            Gizmos.DrawLine(b, t);
            prevB = b;
            prevT = t;
        }
    }
#endif
}
