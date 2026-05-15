using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Tethers a balloon body to a fixed kinematic ground anchor with a rigid ball-joint pendulum.
/// The body's attach point is locked to the anchor — angular motion is free — so the balloon
/// swings around the anchor like an inverted pendulum held aloft by buoyancy. Player
/// CharacterController bumps (routed through <see cref="NetworkedPhysicsPropPush"/>) push the
/// body sideways and the joint swings it back.
/// </summary>
/// <remarks>
/// Server (or singleplayer) drives buoyancy and upright torque. Clients run a kinematic mirror
/// whose transform is replicated by NGO's NetworkTransform; the joint exists on clients too but
/// is inert against the kinematic body.
/// </remarks>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(NetworkObject))]
public sealed class BalloonTether : NetworkBehaviour
{
    [Header("Tether")]
    [Tooltip("Kinematic Rigidbody at the ground tie-point. The balloon's string tip is locked to this point.")]
    [SerializeField] Rigidbody anchorRigidbody;

    [Tooltip("Transform on the body (typically the bottom tip of the rope mesh) that locks to the anchor.")]
    [SerializeField] Transform bodyAttachPoint;

    [Header("Buoyancy")]
    [Tooltip("Constant upward acceleration applied to the body (ForceMode.Acceleration). Must exceed gravity for the balloon to rest above the anchor.")]
    [SerializeField, Min(0f)] float buoyancyAcceleration = 20f;

    [Header("Upright")]
    [Tooltip("Torque acceleration that returns the balloon's visual-up axis toward world up after a bump.")]
    [SerializeField, Min(0f)] float uprightTorqueAcceleration = 22f;

    [Tooltip("Body-local axis that should point toward world up at rest.")]
    [SerializeField] Vector3 visualUpLocal = Vector3.up;

    Rigidbody _rb;
    Collider _collider;
    ConfigurableJoint _joint;

    bool SimulateLocally =>
        NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || IsServer;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _collider = GetComponent<Collider>();
        EnsureAnchorLocked();
        EnsureJoint();
    }

    void Start()
    {
        IgnoreOtherBalloonCollisions();
    }

    void IgnoreOtherBalloonCollisions()
    {
        if (_collider == null)
            return;

        foreach (BalloonTether other in FindObjectsByType<BalloonTether>(FindObjectsSortMode.None))
        {
            if (other == this || other._collider == null)
                continue;
            Physics.IgnoreCollision(_collider, other._collider);
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        EnsureAnchorLocked();
        EnsureJoint();
    }

    public override void OnNetworkDespawn()
    {
        DestroyJoint();
        base.OnNetworkDespawn();
    }

    void EnsureAnchorLocked()
    {
        if (anchorRigidbody == null)
            return;

        if (!anchorRigidbody.isKinematic)
        {
            anchorRigidbody.linearVelocity = Vector3.zero;
            anchorRigidbody.angularVelocity = Vector3.zero;
        }

        anchorRigidbody.isKinematic = true;
        anchorRigidbody.useGravity = false;
    }

    void EnsureJoint()
    {
        if (_joint != null)
            return;
        if (anchorRigidbody == null || _rb == null || bodyAttachPoint == null)
            return;

        _joint = gameObject.AddComponent<ConfigurableJoint>();
        _joint.connectedBody = anchorRigidbody;
        _joint.autoConfigureConnectedAnchor = false;
        _joint.anchor = transform.InverseTransformPoint(bodyAttachPoint.position);
        _joint.connectedAnchor = anchorRigidbody.transform.InverseTransformPoint(bodyAttachPoint.position);
        _joint.xMotion = ConfigurableJointMotion.Locked;
        _joint.yMotion = ConfigurableJointMotion.Locked;
        _joint.zMotion = ConfigurableJointMotion.Locked;
        _joint.angularXMotion = ConfigurableJointMotion.Free;
        _joint.angularYMotion = ConfigurableJointMotion.Free;
        _joint.angularZMotion = ConfigurableJointMotion.Free;
        _joint.projectionMode = JointProjectionMode.PositionAndRotation;
        _joint.projectionDistance = 0.005f;
        _joint.projectionAngle = 0.5f;
        _joint.enableCollision = false;
        _joint.enablePreprocessing = false;
    }

    void DestroyJoint()
    {
        if (_joint == null)
            return;

        Destroy(_joint);
        _joint = null;
    }

    void FixedUpdate()
    {
        if (!SimulateLocally || _rb == null || _rb.isKinematic)
            return;

        _rb.AddForce(Vector3.up * buoyancyAcceleration, ForceMode.Acceleration);
        ApplyUprightTorque();
    }

    void ApplyUprightTorque()
    {
        if (uprightTorqueAcceleration <= 0f || visualUpLocal.sqrMagnitude < 1e-8f)
            return;

        Vector3 worldUp = transform.TransformDirection(visualUpLocal.normalized);
        Vector3 axis = Vector3.Cross(worldUp, Vector3.up);
        float sin = axis.magnitude;
        if (sin < 1e-5f)
            return;

        axis /= sin;
        float cos = Mathf.Clamp(Vector3.Dot(worldUp, Vector3.up), -1f, 1f);
        float angle = Mathf.Acos(cos);
        _rb.AddTorque(axis * (uprightTorqueAcceleration * angle), ForceMode.Acceleration);
    }
}
