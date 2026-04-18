using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
public class MazeFinishTrigger : MonoBehaviour
{
    const string FinishReachedMessageName = "maze-finish-reached";

    [SerializeField] bool addKinematicRigidbody = true;

    Collider _triggerCollider;
    NetworkManager _networkManager;
    MultiplayerSceneFlow _sceneFlow;
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
        CacheSceneFlow();
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
        _triggerCollider = GetComponent<Collider>();
        if (_triggerCollider != null)
            _triggerCollider.isTrigger = true;

        if (!addKinematicRigidbody)
            return;

        Rigidbody triggerBody = GetComponent<Rigidbody>();
        if (triggerBody == null)
            triggerBody = gameObject.AddComponent<Rigidbody>();

        triggerBody.isKinematic = true;
        triggerBody.useGravity = false;
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
        CacheSceneFlow();

        if (_sceneFlow != null)
        {
            _sceneFlow.ReturnToMainMenu();
            return;
        }

        Debug.LogWarning("[Maze] MultiplayerSceneFlow was not found, falling back to direct menu scene load.", this);
        SceneManager.LoadScene(MultiplayerSceneFlow.MenuSceneName, LoadSceneMode.Single);
    }

    void CacheSceneFlow()
    {
        if (_sceneFlow == null)
            _sceneFlow = FindAnyObjectByType<MultiplayerSceneFlow>();
    }
}
