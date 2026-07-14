using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marks the interior volume of the Carnival Main room. Placed on the CarnivalMainRoom maze-piece root,
/// it registers itself in a static list so the local <see cref="PlayerController"/> can poll whether it is
/// standing inside the room and show/hide its carnival ticket counter accordingly — tickets are only earned
/// and spent at the booths in this room, so the counter is meaningless anywhere else.
/// <para>
/// Purely a client-side UI concern: no networking, no physics. The box is expressed in the piece root's
/// local space (so it tracks the room wherever the maze places it) and only needs to roughly enclose the
/// interior; tune <see cref="localCenter"/> / <see cref="localSize"/> in the Inspector against the gizmo.
/// </para>
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalMainRoomZone : MonoBehaviour
{
    static readonly List<CarnivalMainRoomZone> ActiveZones = new List<CarnivalMainRoomZone>();

    [SerializeField, Tooltip("Interior volume center in the piece root's local space (walls span ±12 in X/Z, floor→ceiling y 0→13).")]
    Vector3 localCenter = new Vector3(0f, 6f, 0f);

    [SerializeField, Tooltip("Interior volume size in the piece root's local space. Kept just inside the ±12 walls so the corridor/doorways don't count.")]
    Vector3 localSize = new Vector3(23f, 14f, 23f);

    void OnEnable()
    {
        if (!ActiveZones.Contains(this))
            ActiveZones.Add(this);
    }

    void OnDisable()
    {
        ActiveZones.Remove(this);
    }

    public bool Contains(Vector3 worldPoint)
    {
        Vector3 local = transform.InverseTransformPoint(worldPoint);
        Vector3 half = localSize * 0.5f;
        return Mathf.Abs(local.x - localCenter.x) <= half.x
            && Mathf.Abs(local.y - localCenter.y) <= half.y
            && Mathf.Abs(local.z - localCenter.z) <= half.z;
    }

    /// <summary>True if <paramref name="worldPoint"/> lies inside any active Carnival Main room zone.</summary>
    public static bool IsPointInsideAny(Vector3 worldPoint)
    {
        for (int i = 0; i < ActiveZones.Count; i++)
        {
            CarnivalMainRoomZone zone = ActiveZones[i];
            if (zone != null && zone.Contains(worldPoint))
                return true;
        }
        return false;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.75f, 0.1f, 0.5f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(localCenter, localSize);
    }
}
