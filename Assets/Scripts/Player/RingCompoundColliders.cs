using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Unity does not allow <b>concave MeshCollider</b> on dynamic (non-kinematic) <see cref="Rigidbody"/>.
/// Convex hulls on a torus close the hole, so the mesh cannot behave like a real ring on pegs. This builds a
/// <b>compound collider</b> from oriented box segments around the tube perimeter.
/// </summary>
[DefaultExecutionOrder(-100)]
[DisallowMultipleComponent]
public sealed class RingCompoundColliders : MonoBehaviour
{
    const string SegmentRootName = "__RingCollisionSegments";

    /// <summary>Centroid circle lies in the plane perpendicular to this (local mesh space).</summary>
    [SerializeField] Vector3 torusHoleAxisLocal = Vector3.up;

    [SerializeField] [Range(6, 48)] int segmentCount = 16;

    [Tooltip("Tube center-circle radius from the hole axis (local mesh units, before parent Transform scale).")]
    [SerializeField] float majorRadiusLocal = 0.0068f;

    [Tooltip("Tube cross-section radius (distance from tube center circle to inner/outer surface of the donut).")]
    [SerializeField] float minorRadiusLocal = 0.0018f;

    [Tooltip(
        "Radial box thickness as multiple of tube diameter (2× minor radius). Mathematical torus = 2.0 (flush with tube). Old sizing used ~2.35 on radius and invaded the peg hole.")]
    [SerializeField] [Range(1.45f, 2.1f)] float radialColliderDiameterMultiple = 1.92f;

    [Tooltip("Extent along tube axis (hole direction), vs tube diameter.")]
    [SerializeField] [Range(1.6f, 2.2f)] float holeAxisColliderDiameterMultiple = 2.05f;

    [Tooltip("Slight chord overlap along the ring so collisions do not fall through seams.")]
    [SerializeField] float segmentChordOverlapFactor = 1.08f;

    [SerializeField] PhysicsMaterial physicsMaterial;

#if UNITY_EDITOR
    [Header("Editor gizmo (Prefab / Scene)")]
    [SerializeField] bool showCompoundColliderGizmo = true;
    [Tooltip("Faint wire boxes when this object is not selected (easier to find under parents in Prefab Mode).")]
    [SerializeField] bool drawGizmoWhenUnselected = true;
#endif

    /// <summary>Hole axis direction in ring local space (matches segment layout).</summary>
    public Vector3 NormalizedTorusHoleAxisLocal =>
        torusHoleAxisLocal.sqrMagnitude < 1e-8f ? Vector3.up : torusHoleAxisLocal.normalized;

    void Awake()
    {
        RebuildSegments();
    }

#if UNITY_EDITOR
    /// <summary>Unity logs mesh vs dynamic Rigidbody errors as soon as the prefab imports; removing the MeshCollider avoids that if it was added by mistake.</summary>
    void OnValidate()
    {
        if (Application.isPlaying)
            return;
        if (!TryStripIllegalConcaveMeshCollider())
            return;
        EditorUtility.SetDirty(gameObject);
        if (gameObject.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
    }

    /// <returns>True if a problematic component was destroyed.</returns>
    bool TryStripIllegalConcaveMeshCollider()
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        MeshCollider mc = GetComponent<MeshCollider>();
        if (mc == null || rb == null || rb.isKinematic || mc.convex)
            return false;
        DestroyImmediate(mc);
        return true;
    }

    [ContextMenu("Ring/Rebuild Compound Colliders (Editor)")]
    void EditorRebuild()
    {
        if (Application.isPlaying)
            return;
        RebuildSegments();
        EditorUtility.SetDirty(gameObject);
        if (gameObject.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
    }

    void OnDrawGizmos()
    {
        if (!showCompoundColliderGizmo)
            return;

        Transform active = Selection.activeTransform;
        bool lineageSelected =
            active != null && (active == transform || transform.IsChildOf(active));

        float alpha;
        if (lineageSelected)
            alpha = 1f;
        else if (drawGizmoWhenUnselected)
            alpha = 0.44f;
        else
            return;

        DrawColliderGizmo(alpha);
    }

    /// <summary>Matches <see cref="RebuildSegments"/> box layout (cyan wire boxes + orange hole axis).</summary>
    void DrawColliderGizmo(float alpha)
    {
        if (!enabled || majorRadiusLocal < 1e-4f || minorRadiusLocal < 1e-6f || segmentCount < 6)
            return;

        Matrix4x4 prevMatrix = Gizmos.matrix;
        Color prevColor = Gizmos.color;

        Vector3 holeAxis = torusHoleAxisLocal.sqrMagnitude > 0.01f ? torusHoleAxisLocal.normalized : Vector3.up;
        OrthonormalTangentBasis(holeAxis, out Vector3 u, out Vector3 v);

        float chord = Mathf.Max(majorRadiusLocal * 2f * Mathf.Sin(Mathf.PI / segmentCount), minorRadiusLocal * 0.75f)
            * Mathf.Max(segmentChordOverlapFactor, 1f);
        float tubeDiameter = minorRadiusLocal * 2f;
        Vector3 size = new Vector3(
            tubeDiameter * (holeAxisColliderDiameterMultiple * 0.5f),
            tubeDiameter * (radialColliderDiameterMultiple * 0.5f),
            chord);

        Gizmos.color = new Color(0f, 0.86f, 1f, Mathf.Clamp01(alpha));

        for (int i = 0; i < segmentCount; i++)
        {
            float angle = i * Mathf.PI * 2f / segmentCount;
            Vector3 radial = Mathf.Cos(angle) * u + Mathf.Sin(angle) * v;
            Vector3 tangent = Mathf.Sin(angle) * u - Mathf.Cos(angle) * v;
            Quaternion rot = Quaternion.LookRotation(tangent, radial);
            Matrix4x4 segmentLocal = Matrix4x4.TRS(majorRadiusLocal * radial, rot, Vector3.one);
            Gizmos.matrix = transform.localToWorldMatrix * segmentLocal;
            Gizmos.DrawWireCube(Vector3.zero, size);
        }

        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.52f, 0.12f, Mathf.Clamp01(alpha * 0.85f));
        Vector3 holeAxisHint = NormalizedTorusHoleAxisLocal * Mathf.Max(minorRadiusLocal * 5f, majorRadiusLocal * 0.5f);
        Gizmos.DrawLine(-holeAxisHint, holeAxisHint);

        Gizmos.matrix = prevMatrix;
        Gizmos.color = prevColor;
    }
#endif

    public void RebuildSegments()
    {
        DestroyMeshColliderIfAny();
        ClearSegmentChildren();

        if (majorRadiusLocal < 1e-4f || minorRadiusLocal < 1e-6f || segmentCount < 6)
            return;

        Vector3 holeAxis = torusHoleAxisLocal.sqrMagnitude > 0.01f ? torusHoleAxisLocal.normalized : Vector3.up;
        OrthonormalTangentBasis(holeAxis, out Vector3 u, out Vector3 v);

        float chord = Mathf.Max(majorRadiusLocal * 2f * Mathf.Sin(Mathf.PI / segmentCount), minorRadiusLocal * 0.75f)
            * Mathf.Max(segmentChordOverlapFactor, 1f);

        Transform rootTransform = new GameObject(SegmentRootName).transform;
        rootTransform.SetParent(transform, false);
        rootTransform.localPosition = Vector3.zero;
        rootTransform.localRotation = Quaternion.identity;
        rootTransform.localScale = Vector3.one;

        for (int i = 0; i < segmentCount; i++)
        {
            float angle = i * Mathf.PI * 2f / segmentCount;
            Vector3 radial = Mathf.Cos(angle) * u + Mathf.Sin(angle) * v;
            Vector3 tangent = Mathf.Sin(angle) * u - Mathf.Cos(angle) * v;
            Quaternion rot = Quaternion.LookRotation(tangent, radial);

            var segGo = new GameObject($"{SegmentRootName}_{i:00}");
            Transform segTf = segGo.transform;
            segTf.SetParent(rootTransform, false);
            segTf.localRotation = rot;
            segTf.localPosition = majorRadiusLocal * radial;
            BoxCollider bx = segGo.AddComponent<BoxCollider>();
            bx.material = physicsMaterial;
            bx.center = Vector3.zero;
            float tubeDiameter = minorRadiusLocal * 2f;
            bx.size = new Vector3(
                tubeDiameter * (holeAxisColliderDiameterMultiple * 0.5f),
                tubeDiameter * (radialColliderDiameterMultiple * 0.5f),
                chord);
        }
    }

    void DestroyMeshColliderIfAny()
    {
        MeshCollider mc = GetComponent<MeshCollider>();
        if (mc == null)
            return;
#if UNITY_EDITOR
        if (!Application.isPlaying)
            DestroyImmediate(mc);
        else
#endif
            Destroy(mc);
    }

    void ClearSegmentChildren()
    {
        Transform existing = transform.Find(SegmentRootName);
        if (existing == null)
            return;
#if UNITY_EDITOR
        if (!Application.isPlaying)
            DestroyImmediate(existing.gameObject);
        else
#endif
            Destroy(existing.gameObject);
    }

    static void OrthonormalTangentBasis(Vector3 holeAxisNormalized, out Vector3 u, out Vector3 v)
    {
        Vector3 orthRef = Mathf.Abs(Vector3.Dot(holeAxisNormalized, Vector3.forward)) > 0.95f ? Vector3.right : Vector3.forward;
        u = Vector3.Cross(holeAxisNormalized, orthRef);
        if (u.sqrMagnitude < 1e-6f)
            u = Vector3.Cross(holeAxisNormalized, Vector3.up);
        u.Normalize();
        v = Vector3.Cross(holeAxisNormalized, u).normalized;
    }
}
