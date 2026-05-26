using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public sealed class MinigameBarrierGizmo : MonoBehaviour
{
    [SerializeField] Color gizmoColor = new Color(1f, 0.2f, 0.2f, 0.25f);

    void OnDrawGizmos()
    {
        var col = GetComponent<BoxCollider>();
        if (col == null) return;

        Matrix4x4 prev = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;

        Gizmos.color = gizmoColor;
        Gizmos.DrawCube(col.center, col.size);

        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 1f);
        Gizmos.DrawWireCube(col.center, col.size);

        Gizmos.matrix = prev;
    }
}
