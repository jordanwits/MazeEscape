using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Applies CharacterController bumps on the server for props using <see cref="Unity.Netcode.Components.NetworkRigidbody"/>.
/// Clients have a kinematic mirror; host applies force locally via <see cref="PlayerController.OnControllerColliderHit"/>.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public sealed class NetworkedPhysicsPropPush : NetworkBehaviour
{
    [SerializeField, Min(0.1f)]
    float interactionMaxHorizontalDistance = 2.95f;

    [SerializeField, Min(0.05f)]
    float planarSpeedScaleVsPlayerController = 0.85f;

    [SerializeField, Min(0.1f)]
    float planarSpeedCapDelta = 3.5f;

    [Header("Optional tethered balloon (server physics)")]
    [SerializeField] bool balloonHeliumMode;
    [SerializeField] Rigidbody balloonTieAnchorRigidbody;
    [SerializeField] Vector3 balloonAttachmentLocalPosition = new Vector3(0f, 0f, -0.008f);
    [SerializeField, Min(0f)] float balloonBuoyancyAcceleration = 11f;
    [SerializeField] Vector3 balloonBuoyancyForceOffsetLocal = new Vector3(0f, 0f, 0.004f);
    [SerializeField, Min(0f)] float balloonExtraLinearDrag = 0.35f;
    [SerializeField, Min(0f)] float balloonUprightTorqueAcceleration = 22f;
    [SerializeField] Vector3 balloonVisualUpLocal = new Vector3(0f, 1f, 0f);
    [SerializeField, Min(1f)] float balloonTetherSpring = 260f;
    [SerializeField, Min(0f)] float balloonTetherDamper = 32f;
    [SerializeField, Min(0.01f)] float balloonTetherMinDistance = 0.55f;
    [SerializeField, Min(0.01f)] float balloonTetherMaxDistance = 1.05f;

    Rigidbody _rb;
    SpringJoint _balloonJoint;

    bool BalloonSimulatePhysicsForce =>
        NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || IsServer;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (balloonHeliumMode && IsServer)
            EnsureBalloonTetherJoint();
    }

    void Start()
    {
        if (!balloonHeliumMode)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
            return;

        EnsureBalloonTetherJoint();
    }

    public override void OnNetworkDespawn()
    {
        DestroyBalloonJoint();
        base.OnNetworkDespawn();
    }

    void DestroyBalloonJoint()
    {
        if (_balloonJoint != null)
        {
            Destroy(_balloonJoint);
            _balloonJoint = null;
        }
    }

    void EnsureBalloonTetherJoint()
    {
        if (_balloonJoint != null || balloonTieAnchorRigidbody == null || _rb == null || _rb.isKinematic)
            return;

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !IsServer)
            return;

        _balloonJoint = gameObject.AddComponent<SpringJoint>();
        _balloonJoint.connectedBody = balloonTieAnchorRigidbody;
        _balloonJoint.anchor = balloonAttachmentLocalPosition;
        _balloonJoint.connectedAnchor = Vector3.zero;
        _balloonJoint.autoConfigureConnectedAnchor = false;
        _balloonJoint.spring = balloonTetherSpring;
        _balloonJoint.damper = balloonTetherDamper;
        _balloonJoint.minDistance = balloonTetherMinDistance;
        _balloonJoint.maxDistance = balloonTetherMaxDistance;
    }

    void FixedUpdate()
    {
        if (!balloonHeliumMode || !BalloonSimulatePhysicsForce || _rb == null || _rb.isKinematic)
            return;

        Vector3 buoyancyPoint = transform.TransformPoint(balloonBuoyancyForceOffsetLocal);
        _rb.AddForceAtPosition(Vector3.up * balloonBuoyancyAcceleration, buoyancyPoint, ForceMode.Acceleration);

        if (balloonExtraLinearDrag > 0f)
        {
            Vector3 v = _rb.linearVelocity;
            _rb.AddForce(-v * balloonExtraLinearDrag, ForceMode.Acceleration);
        }

        ApplyBalloonUprightTorque();
    }

    void ApplyBalloonUprightTorque()
    {
        if (balloonUprightTorqueAcceleration <= 0f)
            return;

        Vector3 axisDir = balloonVisualUpLocal;
        if (axisDir.sqrMagnitude < 1e-8f)
            return;

        Vector3 worldBalloonUp = transform.TransformDirection(axisDir.normalized);
        Vector3 correctionAxis = Vector3.Cross(worldBalloonUp, Vector3.up);
        float sin = correctionAxis.magnitude;
        if (sin < 1e-5f)
            return;

        correctionAxis /= sin;
        float cos = Mathf.Clamp(Vector3.Dot(worldBalloonUp, Vector3.up), -1f, 1f);
        float angle = Mathf.Acos(cos);
        _rb.AddTorque(correctionAxis * (balloonUprightTorqueAcceleration * angle), ForceMode.Acceleration);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestCharacterBumpServerRpc(
        Vector3 worldPushHorizontalUnit,
        float planarCharacterSpeed,
        ServerRpcParams rpcParams = default)
    {
        if (!IsServer || _rb == null || _rb.isKinematic || !enabled)
            return;

        ulong sender = rpcParams.Receive.SenderClientId;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.ConnectedClients.TryGetValue(sender, out NetworkClient client)
            || client.PlayerObject == null)
            return;

        Vector3 playerPos = client.PlayerObject.transform.position;
        if (HorizontalDistanceSquared(playerPos, transform.position) >
            interactionMaxHorizontalDistance * interactionMaxHorizontalDistance)
            return;

        worldPushHorizontalUnit.y = 0f;
        if (worldPushHorizontalUnit.sqrMagnitude < 0.0008f)
            return;

        worldPushHorizontalUnit.Normalize();

        planarCharacterSpeed = Mathf.Max(0f, planarCharacterSpeed);
        float transfer = Mathf.Clamp(planarCharacterSpeed * planarSpeedScaleVsPlayerController, 0f, planarSpeedCapDelta);

        if (TryGetComponent(out PlayerPhysicsPushReceiver receiver))
            transfer *= Mathf.Max(0.1f, receiver.PushGainMultiplier);

        Vector3 delta = worldPushHorizontalUnit * transfer;
        Vector3 vel = _rb.linearVelocity;
        vel.x += delta.x;
        vel.z += delta.z;
        _rb.linearVelocity = vel;

        if (TryGetComponent(out RigidbodyImpactSfx impactSfx))
            impactSfx.NotifyCharacterControllerBump(planarCharacterSpeed);
    }

    static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }
}
