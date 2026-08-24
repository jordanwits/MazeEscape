using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Your local mix of everyone else's proximity voice: how loud each other player is in YOUR ears and
/// whether they are muted. Purely client-side — nothing here is replicated, so muting someone does not
/// tell them (or anyone else) about it.
///
/// Keyed by NGO client id, which is only meaningful for the lifetime of one session, so this is
/// session-scoped: <see cref="Clear"/> runs with the rest of the voice teardown in
/// <see cref="ProximityVoiceSession.InvalidateProximityMessaging"/>. It deliberately survives scene
/// loads, so a slider set in the lobby still applies after the level switch rebuilds the avatars.
/// </summary>
public static class VoicePlayerMixSettings
{
    public const float DefaultVolume = 1f;

    struct Entry
    {
        public float Volume;
        public bool Muted;
    }

    static readonly Dictionary<ulong, Entry> s_ByClientId = new();

    /// <summary>Raised when one player's volume or mute state changes; the argument is that client id.</summary>
    public static event Action<ulong> Changed;

    public static float GetVolume(ulong clientId) =>
        s_ByClientId.TryGetValue(clientId, out Entry entry) ? entry.Volume : DefaultVolume;

    public static bool IsMuted(ulong clientId) =>
        s_ByClientId.TryGetValue(clientId, out Entry entry) && entry.Muted;

    /// <summary>What the playback AudioSource should actually run at — mute wins over the slider.</summary>
    public static float GetEffectiveVolume(ulong clientId)
    {
        if (!s_ByClientId.TryGetValue(clientId, out Entry entry))
            return DefaultVolume;
        return entry.Muted ? 0f : entry.Volume;
    }

    public static void SetVolume(ulong clientId, float volume)
    {
        Entry entry = Get(clientId);
        float clamped = Mathf.Clamp01(volume);
        if (Mathf.Approximately(entry.Volume, clamped))
            return;

        entry.Volume = clamped;
        s_ByClientId[clientId] = entry;
        Changed?.Invoke(clientId);
    }

    public static void SetMuted(ulong clientId, bool muted)
    {
        Entry entry = Get(clientId);
        if (entry.Muted == muted)
            return;

        entry.Muted = muted;
        s_ByClientId[clientId] = entry;
        Changed?.Invoke(clientId);
    }

    /// <summary>Flips mute for one player and returns the new state (for toggle buttons).</summary>
    public static bool ToggleMuted(ulong clientId)
    {
        bool muted = !IsMuted(clientId);
        SetMuted(clientId, muted);
        return muted;
    }

    /// <summary>
    /// Drops every entry when the session ends. Notifies per id first so anything still holding a
    /// voice source falls back to the default volume instead of keeping a stale mute.
    /// </summary>
    public static void Clear()
    {
        if (s_ByClientId.Count == 0)
            return;

        var ids = new List<ulong>(s_ByClientId.Keys);
        s_ByClientId.Clear();
        for (int i = 0; i < ids.Count; i++)
            Changed?.Invoke(ids[i]);
    }

    static Entry Get(ulong clientId) =>
        s_ByClientId.TryGetValue(clientId, out Entry entry)
            ? entry
            : new Entry { Volume = DefaultVolume, Muted = false };
}
