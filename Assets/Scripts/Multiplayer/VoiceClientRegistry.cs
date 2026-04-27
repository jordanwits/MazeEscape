using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maps <see cref="Unity.Netcode.NetworkObject.OwnerClientId"/> to local voice playback components
/// (for routing incoming proximity voice to the right player).
/// </summary>
public static class VoiceClientRegistry
{
    static readonly Dictionary<ulong, NetworkPlayerVoice> s_ByOwnerClient = new();

    public static void Register(ulong ownerClientId, NetworkPlayerVoice playerVoice)
    {
        if (playerVoice == null)
            return;
        s_ByOwnerClient[ownerClientId] = playerVoice;
    }

    public static void Unregister(ulong ownerClientId, NetworkPlayerVoice playerVoice)
    {
        if (s_ByOwnerClient.TryGetValue(ownerClientId, out NetworkPlayerVoice v) && v == playerVoice)
            s_ByOwnerClient.Remove(ownerClientId);
    }

    public static bool TryGet(ulong ownerClientId, out NetworkPlayerVoice playerVoice) =>
        s_ByOwnerClient.TryGetValue(ownerClientId, out playerVoice);

    /// <summary>
    /// Call when the netcode session ends so stale owner→voice mappings cannot route audio to wrong objects.
    /// </summary>
    public static void Clear()
    {
        s_ByOwnerClient.Clear();
    }
}
