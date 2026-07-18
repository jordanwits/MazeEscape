using UnityEngine;

/// <summary>
/// Smooths the first-person view while the player is ragdolled / held / getting up, WITHOUT touching any
/// ragdoll physics. The view normally rides the head bone directly (see
/// <see cref="PlayerController.EnsureCameraPitchParentedToHead"/>), so it inherits the head's raw per-physics-step
/// flailing — a violent, shaky picture. Instead of parenting <c>CameraPitch</c> to the head bone, the controller
/// parents it to the lightweight proxy transform this component owns. The proxy chases the head's world pose each
/// frame with critically-damped position smoothing and exponential rotation smoothing, so the inherited motion is
/// dampened while the player's look input (applied as a local rotation on top of the proxy) stays fully responsive.
///
/// The proxy only ever READS the head transform — it never writes to a Rigidbody — so the ragdoll itself behaves
/// exactly as before. Auto-added at runtime by <see cref="PlayerController"/> alongside
/// <see cref="RagdollCameraCollision"/> and <see cref="FirstPersonViewHeadSync"/>.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(550)] // Before FirstPersonViewHeadSync (600) and RagdollCameraCollision (601) read the pose.
public class RagdollCameraDamper : MonoBehaviour
{
    [Header("Damping")]
    [Tooltip("Smoothing time (seconds) for the view position following the head. Higher = smoother but laggier. 0 = no position damping.")]
    [SerializeField] float positionSmoothTime = 0.07f;
    [Tooltip("Smoothing time (seconds) for the view rotation following the head's tumble. Higher = calmer horizon but laggier. 0 = no rotation damping (raw head spin).")]
    [SerializeField] float rotationSmoothTime = 0.12f;

    [Header("Teleport guard")]
    [Tooltip("If the head jumps farther than this in one frame (slam reposition, respawn, teleport), snap the proxy instead of lerping the camera through geometry. 0 = never snap.")]
    [SerializeField] float snapDistance = 1.25f;

    Transform _proxy;
    Transform _head;
    Transform _lookTarget;
    bool _following;
    Vector3 _positionVelocity;

    /// <summary>
    /// When set (enemy grab hold), the proxy faces this target with a level horizon instead of copying the
    /// head bone's raw, rolled rotation — so a held victim looks at the grabber rather than at the floor.
    /// Cleared by <see cref="EndFollow"/>; set per-frame by the controller while the player is held.
    /// </summary>
    public Transform LookTarget
    {
        get => _lookTarget;
        set
        {
            if (_lookTarget == value)
                return;
            bool wasNull = _lookTarget == null;
            _lookTarget = value;
            // Snap to face the target on the frame the grab starts so the view doesn't swing up from the floor.
            if (_lookTarget != null && wasNull && _head != null)
                Proxy.rotation = ComputeDesiredRotation();
        }
    }

    /// <summary>The smoothed transform the camera pitch is parented to during ragdoll. Created lazily.</summary>
    public Transform Proxy
    {
        get
        {
            if (_proxy == null)
            {
                var go = new GameObject($"[RagdollCamProxy] {name}");
                _proxy = go.transform;
            }
            return _proxy;
        }
    }

    /// <summary>
    /// Begin following <paramref name="head"/>. Snaps the proxy onto the head's current pose so the camera does
    /// not jump on the frame it attaches, then damps toward it on subsequent frames.
    /// </summary>
    public void BeginFollow(Transform head)
    {
        _head = head;
        _following = head != null;
        SnapToHead();
    }

    /// <summary>Stop damping (the camera has detached from the proxy).</summary>
    public void EndFollow()
    {
        _following = false;
        _head = null;
        _lookTarget = null;
    }

    void SnapToHead()
    {
        if (_head == null)
            return;

        Proxy.SetPositionAndRotation(_head.position, ComputeDesiredRotation());
        _positionVelocity = Vector3.zero;
    }

    // The rotation the proxy chases: face the grabber with a level horizon while held, else the head's raw pose.
    Quaternion ComputeDesiredRotation()
    {
        if (_head == null)
            return Quaternion.identity;
        if (_lookTarget == null)
            return _head.rotation;
        Vector3 flat = _lookTarget.position - _head.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 1e-4f)
            return _head.rotation;
        return Quaternion.LookRotation(flat.normalized, Vector3.up);
    }

    void OnDisable()
    {
        EndFollow();
    }

    void OnDestroy()
    {
        if (_proxy != null)
            Destroy(_proxy.gameObject);
    }

    void LateUpdate()
    {
        if (!_following || _head == null)
            return;

        Vector3 headPos = _head.position;
        Quaternion headRot = ComputeDesiredRotation();
        float dt = Time.deltaTime;

        // A large one-frame jump means a slam reposition / teleport / respawn moved the body, not the ragdoll
        // flailing. Snap so the smoothed camera never lerps in a straight line through a wall to catch up.
        if (snapDistance > 0f && (Proxy.position - headPos).sqrMagnitude > snapDistance * snapDistance)
        {
            SnapToHead();
            return;
        }

        Proxy.position = positionSmoothTime > 0f
            ? Vector3.SmoothDamp(Proxy.position, headPos, ref _positionVelocity, positionSmoothTime, Mathf.Infinity, dt)
            : headPos;

        // Frame-rate-independent exponential approach: rotationSmoothTime is the time constant.
        float t = rotationSmoothTime > 1e-4f ? 1f - Mathf.Exp(-dt / rotationSmoothTime) : 1f;
        Proxy.rotation = Quaternion.Slerp(Proxy.rotation, headRot, t);
    }
}
