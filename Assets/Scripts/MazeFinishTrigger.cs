using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class MazeFinishTrigger : MonoBehaviour
{
    const string FinishReachedMessageName = "maze-finish-reached";
    const string IsolatedTriggerChildName = "FinishTriggerVolume";

    [SerializeField] bool addKinematicRigidbody = true;

    Collider _triggerCollider;
    NetworkManager _networkManager;
    bool _finishTriggered;
    bool _handlerRegistered;

    void Reset()
    {
        ConfigureTrigger();
    }

    void Awake()
    {
        RefreshNetworkManager();
        ConfigureTrigger();
    }

    void OnEnable()
    {
        _finishTriggered = false;
        RefreshNetworkManager();
        EnsureMessageHandlerRegistered();
    }

    void OnDisable()
    {
        UnregisterMessageHandler();
    }

    void OnDestroy()
    {
        UnregisterMessageHandler();
    }

    void OnTriggerEnter(Collider other)
    {
        TryHandleFinish(other);
    }

    void OnTriggerStay(Collider other)
    {
        TryHandleFinish(other);
    }

    void RefreshNetworkManager()
    {
        NetworkManager singleton = NetworkManager.Singleton;
        if (_networkManager == singleton)
            return;

        UnregisterMessageHandler();
        _networkManager = singleton;
    }

    void ConfigureTrigger()
    {
        Transform isolated = transform.Find(IsolatedTriggerChildName);
        if (isolated != null)
        {
            _triggerCollider = isolated.GetComponent<Collider>();
            if (_triggerCollider != null)
                _triggerCollider.isTrigger = true;

            if (addKinematicRigidbody && _triggerCollider != null)
                ApplyKinematicRigidbody(_triggerCollider.gameObject);

            if (_triggerCollider != null && isolated.GetComponent<MazeFinishTriggerColliderRelay>() == null)
                isolated.gameObject.AddComponent<MazeFinishTriggerColliderRelay>().Bind(this);

            return;
        }

        _triggerCollider = GetComponent<Collider>();
        if (_triggerCollider != null)
            _triggerCollider.isTrigger = true;

        if (!addKinematicRigidbody || _triggerCollider == null)
            return;

        if (ShouldIsolateKinematicRigidbodyAwayFromSubtreeMeshColliders())
        {
            if (_triggerCollider is BoxCollider boxCollider)
                MigrateBoxTriggerToChildVolume(boxCollider);
            else
            {
                Debug.LogWarning(
                    "[Maze] MazeFinishTrigger needs a BoxCollider on this maze piece to isolate kinematic RB from mesh colliders below; add Rigidbody on root anyway (may convex-cook descendant meshes).",
                    this);

                ApplyKinematicRigidbody(gameObject);
            }

            return;
        }

        ApplyKinematicRigidbody(gameObject);
    }

    /// <summary>
    /// A kinematic Rigidbody on this transform aggregates every descendant collider into one compound rigid body.
    /// Convex MeshColliders then undergo PhysX convex hull cooking (256-triangle warning / Mesh_0). Isolating the
    /// trigger BoxCollider on a leaf Rigidbody avoids compounding dungeon/elevator mesh colliders.
    /// </summary>
    bool ShouldIsolateKinematicRigidbodyAwayFromSubtreeMeshColliders()
    {
        foreach (MeshCollider meshCollider in GetComponentsInChildren<MeshCollider>(true))
        {
            if (meshCollider != null && meshCollider.enabled)
                return true;
        }

        return false;
    }

    void MigrateBoxTriggerToChildVolume(BoxCollider box)
    {
        Vector3 center = box.center;
        Vector3 size = box.size;
        PhysicsMaterial sharedMaterial = box.sharedMaterial;
        int layer = box.gameObject.layer;
        LayerMask include = box.includeLayers;
        LayerMask exclude = box.excludeLayers;

        Destroy(box);

        GameObject volume = new(IsolatedTriggerChildName);
        volume.layer = layer;
        Transform v = volume.transform;
        v.SetParent(transform, false);
        v.localPosition = Vector3.zero;
        v.localRotation = Quaternion.identity;
        v.localScale = Vector3.one;

        BoxCollider childBox = volume.AddComponent<BoxCollider>();
        childBox.isTrigger = true;
        childBox.center = center;
        childBox.size = size;
        childBox.sharedMaterial = sharedMaterial;
        childBox.includeLayers = include;
        childBox.excludeLayers = exclude;

        volume.AddComponent<MazeFinishTriggerColliderRelay>().Bind(this);

        _triggerCollider = childBox;

        ApplyKinematicRigidbody(volume);
    }

    void ApplyKinematicRigidbody(GameObject rbHost)
    {
        if (rbHost.GetComponent<Rigidbody>() != null)
            return;

        Rigidbody triggerBody = rbHost.AddComponent<Rigidbody>();
        triggerBody.isKinematic = true;
        triggerBody.useGravity = false;
    }

    internal void RelayTriggerEnter(Collider other)
    {
        TryHandleFinish(other);
    }

    internal void RelayTriggerStay(Collider other)
    {
        TryHandleFinish(other);
    }

    void EnsureMessageHandlerRegistered()
    {
        if (_handlerRegistered
            || _networkManager == null
            || !_networkManager.IsListening
            || !_networkManager.IsServer
            || _networkManager.CustomMessagingManager == null)
        {
            return;
        }

        _networkManager.CustomMessagingManager.RegisterNamedMessageHandler(FinishReachedMessageName, HandleFinishReachedMessage);
        _handlerRegistered = true;
    }

    void UnregisterMessageHandler()
    {
        if (!_handlerRegistered || _networkManager == null || _networkManager.CustomMessagingManager == null)
            return;

        _networkManager.CustomMessagingManager.UnregisterNamedMessageHandler(FinishReachedMessageName);
        _handlerRegistered = false;
    }

    void HandleFinishReachedMessage(ulong senderClientId, FastBufferReader reader)
    {
        if (_networkManager == null || !_networkManager.IsServer)
            return;

        if (reader.TryBeginRead(sizeof(byte)))
        {
            byte ignoredValue;
            reader.ReadValueSafe(out ignoredValue);
        }

        Debug.Log($"[Maze] Client {senderClientId} reached the finish.");
        TriggerFinishReturn();
    }

    void TryHandleFinish(Collider other)
    {
        if (_finishTriggered || other == null)
            return;

        PlayerController playerController = other.GetComponentInParent<PlayerController>();
        if (playerController == null)
            return;

        RefreshNetworkManager();

        if (_networkManager != null && _networkManager.IsListening)
        {
            NetworkObject playerNetworkObject = playerController.GetComponent<NetworkObject>();
            if (playerNetworkObject == null || !playerNetworkObject.IsOwner)
                return;

            if (_networkManager.IsServer)
            {
                TriggerFinishReturn();
                return;
            }

            SendFinishReachedMessage();
            return;
        }

        TriggerFinishReturn();
    }

    void SendFinishReachedMessage()
    {
        if (_networkManager == null || !_networkManager.IsListening || _networkManager.IsServer)
            return;

        if (_networkManager.CustomMessagingManager == null)
        {
            Debug.LogWarning("[Maze] Could not notify the host that the finish was reached because CustomMessagingManager is unavailable.", this);
            return;
        }

        _finishTriggered = true;

        using FastBufferWriter writer = new(sizeof(byte), Allocator.Temp);
        writer.WriteValueSafe((byte)1);
        _networkManager.CustomMessagingManager.SendNamedMessage(
            FinishReachedMessageName,
            NetworkManager.ServerClientId,
            writer,
            NetworkDelivery.ReliableSequenced);
    }

    void TriggerFinishReturn()
    {
        if (_finishTriggered)
            return;

        _finishTriggered = true;
        // Return-to-menu (and any finish celebration) intentionally disabled pending rework.
    }
}

sealed class MazeFinishTriggerColliderRelay : MonoBehaviour
{
    MazeFinishTrigger _owner;

    public void Bind(MazeFinishTrigger owner) => _owner = owner;

    void OnDestroy()
    {
        _owner = null;
    }

    void OnTriggerEnter(Collider other)
    {
        if (_owner != null)
            _owner.RelayTriggerEnter(other);
    }

    void OnTriggerStay(Collider other)
    {
        if (_owner != null)
            _owner.RelayTriggerStay(other);
    }
}
