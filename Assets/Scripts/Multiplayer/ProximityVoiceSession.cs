using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Registers custom messages: clients send <see cref="VoiceUpMessageName"/>, the server relays
/// <see cref="VoiceDownMessageName"/> to other clients.
/// </summary>
[DisallowMultipleComponent]
public class ProximityVoiceSession : MonoBehaviour
{
    public const string VoiceUpMessageName = "maze-escape-prox-voice-up";
    public const string VoiceDownMessageName = "maze-escape-prox-voice-down";
    static bool s_HandlersRegistered;

    /// <summary>
    /// Fixed scratch buffers for voice PCM decode. Netcode message handlers are non-reentrant; avoids per-frame GC on the host when relaying.
    /// </summary>
    static readonly short[] s_ServerVoicePcmScratch = new short[NetworkPlayerVoice.FrameSamples];
    static readonly short[] s_ClientVoicePcmScratch = new short[NetworkPlayerVoice.FrameSamples];

    void OnEnable() => RegisterHandlers();
    void OnDestroy() => UnregisterHandlers();

    void Update()
    {
        if (s_HandlersRegistered)
            return;
        RegisterHandlers();
    }

    void RegisterHandlers()
    {
        if (s_HandlersRegistered)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null)
            return;

        nm.CustomMessagingManager.RegisterNamedMessageHandler(
            VoiceUpMessageName, HandleVoiceUp);
        nm.CustomMessagingManager.RegisterNamedMessageHandler(
            VoiceDownMessageName, HandleVoiceDown);
        s_HandlersRegistered = true;
    }

    void UnregisterHandlers()
    {
        if (!s_HandlersRegistered)
            return;
        if (NetworkManager.Singleton == null
            || NetworkManager.Singleton.CustomMessagingManager == null)
            return;

        var cmm = NetworkManager.Singleton.CustomMessagingManager;
        cmm.UnregisterNamedMessageHandler(VoiceUpMessageName);
        cmm.UnregisterNamedMessageHandler(VoiceDownMessageName);
        s_HandlersRegistered = false;
    }

    static void HandleVoiceUp(ulong senderId, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
            return;
        reader.ReadValueSafe(out ushort seq);
        reader.ReadValueSafe(out ushort count);
        if (count == 0 || count > NetworkPlayerVoice.FrameSamples)
            return;
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out short s);
            s_ServerVoicePcmScratch[i] = s;
        }
        ServerProximityVoiceNotifications.NotifyVoiceFrameFromClient(senderId);
        RelayToOthers(senderId, seq, count, s_ServerVoicePcmScratch);
    }

    static void RelayToOthers(ulong senderId, ushort seq, int count, short[] samples)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
            return;
        CustomMessagingManager cmm = nm.CustomMessagingManager;
        if (cmm == null)
            return;
        int payload = 8 + 2 + 2 + count * 2;
        foreach (ulong clientId in nm.ConnectedClientsIds)
        {
            if (clientId == senderId)
                continue;
            using (FastBufferWriter w = new FastBufferWriter(payload, Allocator.Temp))
            {
                w.WriteValueSafe(senderId);
                w.WriteValueSafe(seq);
                w.WriteValueSafe((ushort)count);
                for (int i = 0; i < count; i++)
                    w.WriteValueSafe(samples[i]);
                cmm.SendNamedMessage(
                    VoiceDownMessageName,
                    clientId,
                    w,
                    NetworkDelivery.Unreliable);
            }
        }
    }

    public static void SendVoiceFrameUp(ushort sequence, short[] samples, int count)
    {
        if (count <= 0 || count > NetworkPlayerVoice.FrameSamples || samples == null)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsClient || !nm.IsListening)
            return;
        if (nm.CustomMessagingManager == null)
            return;
        int size = 2 + 2 + count * 2;
        using (FastBufferWriter w = new FastBufferWriter(size, Allocator.Temp))
        {
            w.WriteValueSafe(sequence);
            w.WriteValueSafe((ushort)count);
            for (int i = 0; i < count; i++)
                w.WriteValueSafe(samples[i]);
            nm.CustomMessagingManager.SendNamedMessage(
                VoiceUpMessageName,
                NetworkManager.ServerClientId,
                w,
                NetworkDelivery.Unreliable);
        }
    }

    static void HandleVoiceDown(ulong _, FastBufferReader reader)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsClient)
            return;
        reader.ReadValueSafe(out ulong originalSpeaker);
        if (originalSpeaker == nm.LocalClientId)
            return;
        reader.ReadValueSafe(out ushort seq);
        reader.ReadValueSafe(out ushort count);
        if (count == 0 || count > NetworkPlayerVoice.FrameSamples)
            return;
        if (!VoiceClientRegistry.TryGet(originalSpeaker, out NetworkPlayerVoice target))
            return;
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out short s);
            s_ClientVoicePcmScratch[i] = s;
        }
        target.EnqueuePcm16Frame(seq, s_ClientVoicePcmScratch, count);
    }
}
