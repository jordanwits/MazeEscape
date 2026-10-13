using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tracks every enabled <see cref="PlayerHealth"/> in the scene so server-side AI fallback paths
/// (Jailor / Clown / Zombie target search) can iterate candidates without per-frame
/// <c>FindObjectsByType</c> scans. Modeled on <see cref="VoiceClientRegistry"/>; entries are
/// added in <c>PlayerHealth.OnEnable</c> and removed in <c>OnDisable</c>.
/// </summary>
public static class PlayerHealthRegistry
{
    static readonly List<PlayerHealth> s_All = new();
    static readonly IReadOnlyList<PlayerHealth> s_AllReadOnly = s_All;

    public static IReadOnlyList<PlayerHealth> All => s_AllReadOnly;

    public static void Register(PlayerHealth playerHealth)
    {
        if (playerHealth == null)
            return;
        if (s_All.Contains(playerHealth))
            return;
        s_All.Add(playerHealth);
    }

    public static void Unregister(PlayerHealth playerHealth)
    {
        if (playerHealth == null)
            return;
        s_All.Remove(playerHealth);
    }

    public static void Clear()
    {
        s_All.Clear();
    }

    // ---- Hazard stand-in hits ---------------------------------------------------------------------------
    // A server hazard's physics overlap cannot see a joined client (see NetworkPlayerAvatar.TryGetHazardStandInCapsule),
    // so hazards run these after their own query. Players the query can see are never returned here.

    /// <summary>Adds every living player whose stand-in capsule overlaps the oriented box to <paramref name="results"/>.</summary>
    public static void CollectStandInHitsInBox(Vector3 center, Vector3 halfExtents, Quaternion rotation, List<PlayerHealth> results)
    {
        Quaternion toLocal = Quaternion.Inverse(rotation);
        for (int i = 0; i < s_All.Count; i++)
        {
            PlayerHealth player = s_All[i];
            if (!TryGetStandInCapsule(player, out Vector3 a, out Vector3 b, out float radius))
                continue;

            Vector3 localA = toLocal * (a - center);
            Vector3 localB = toLocal * (b - center);
            if (SegmentToBoxDistance(localA, localB, halfExtents) <= radius)
                results.Add(player);
        }
    }

    /// <summary>The first living player whose stand-in capsule overlaps the sphere, or null.</summary>
    public static PlayerHealth FindStandInHitInSphere(Vector3 center, float sphereRadius)
    {
        for (int i = 0; i < s_All.Count; i++)
        {
            PlayerHealth player = s_All[i];
            if (!TryGetStandInCapsule(player, out Vector3 a, out Vector3 b, out float radius))
                continue;

            Vector3 closest = ClosestPointOnSegment(a, b, center);
            float reach = radius + sphereRadius;
            if ((closest - center).sqrMagnitude <= reach * reach)
                return player;
        }

        return null;
    }

    static bool TryGetStandInCapsule(PlayerHealth player, out Vector3 a, out Vector3 b, out float radius)
    {
        a = b = Vector3.zero;
        radius = 0f;
        if (player == null || player.IsDead || !player.isActiveAndEnabled)
            return false;

        return player.TryGetComponent(out NetworkPlayerAvatar avatar)
            && avatar.TryGetHazardStandInCapsule(out a, out b, out radius);
    }

    static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        Vector3 ab = b - a;
        float lengthSq = ab.sqrMagnitude;
        if (lengthSq < 1e-8f)
            return a;
        float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSq);
        return a + ab * t;
    }

    /// <summary>
    /// Shortest distance from segment AB to an origin-centred axis-aligned box. Distance to a convex shape is convex
    /// along a line, so a ternary search over the segment finds the minimum.
    /// </summary>
    static float SegmentToBoxDistance(Vector3 a, Vector3 b, Vector3 halfExtents)
    {
        float lo = 0f;
        float hi = 1f;
        for (int i = 0; i < 24; i++)
        {
            float m1 = lo + (hi - lo) / 3f;
            float m2 = hi - (hi - lo) / 3f;
            if (PointToBoxDistanceSq(Vector3.Lerp(a, b, m1), halfExtents) <= PointToBoxDistanceSq(Vector3.Lerp(a, b, m2), halfExtents))
                hi = m2;
            else
                lo = m1;
        }

        return Mathf.Sqrt(PointToBoxDistanceSq(Vector3.Lerp(a, b, (lo + hi) * 0.5f), halfExtents));
    }

    static float PointToBoxDistanceSq(Vector3 p, Vector3 halfExtents)
    {
        float dx = Mathf.Max(Mathf.Abs(p.x) - halfExtents.x, 0f);
        float dy = Mathf.Max(Mathf.Abs(p.y) - halfExtents.y, 0f);
        float dz = Mathf.Max(Mathf.Abs(p.z) - halfExtents.z, 0f);
        return dx * dx + dy * dy + dz * dz;
    }
}
