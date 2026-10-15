using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Carries server-decided achievement awards to the earning client as an NGO named message (see
/// <see cref="AchievementAwards"/>). Lives on the bootstrap object next to the NetworkManager (added in
/// MultiplayerBootstrap.EnsureCoreComponents). Every StartHost/StartClient builds a fresh CustomMessagingManager, so
/// the handler is (re)registered whenever the manager instance changes rather than once per process.
/// </summary>
[DisallowMultipleComponent]
public sealed class AchievementNetworkRelay : MonoBehaviour
{
    const string MessageName = "Detour.Achievement";
    public const byte KindUnlock = 0;
    public const byte KindProgress = 1;

    NetworkManager _networkManager;
    CustomMessagingManager _registeredOn;

    void Awake()
    {
        _networkManager = GetComponent<NetworkManager>();
    }

    void Update()
    {
        NetworkManager nm = _networkManager != null ? _networkManager : NetworkManager.Singleton;
        CustomMessagingManager messaging = nm != null && nm.IsListening ? nm.CustomMessagingManager : null;
        if (messaging == _registeredOn)
            return;

        _registeredOn = messaging;
        // The host awards itself in place (AchievementAwards), so only a pure client needs the handler.
        if (messaging != null && !nm.IsServer)
            messaging.RegisterNamedMessageHandler(MessageName, HandleMessage);
    }

    void OnDestroy()
    {
        if (_registeredOn != null)
        {
            try { _registeredOn.UnregisterNamedMessageHandler(MessageName); }
            catch (System.Exception) { /* the manager is already torn down with the session */ }
        }

        _registeredOn = null;
    }

    public static void ServerSend(NetworkManager nm, ulong clientId, byte kind, AchievementId id, int amount)
    {
        if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
            return;

        using var writer = new FastBufferWriter(sizeof(byte) * 2 + sizeof(int), Allocator.Temp);
        writer.WriteValueSafe(kind);
        writer.WriteValueSafe((byte)id);
        writer.WriteValueSafe(amount);
        nm.CustomMessagingManager.SendNamedMessage(MessageName, clientId, writer, NetworkDelivery.Reliable);
    }

    static void HandleMessage(ulong senderClientId, FastBufferReader reader)
    {
        if (senderClientId != NetworkManager.ServerClientId)
            return;

        reader.ReadValueSafe(out byte kind);
        reader.ReadValueSafe(out byte rawId);
        reader.ReadValueSafe(out int amount);
        var id = (AchievementId)rawId;
        if (kind == KindProgress)
            Achievements.AddProgress(id, amount);
        else
            Achievements.Unlock(id);
    }
}
