using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

/// <summary>
/// The receiving half of voice chat for one remote speaker: a streaming AudioSource fed with the PCM16 frames the
/// server relays (see <see cref="ProximityVoiceSession"/>). Used positionally by <see cref="NetworkPlayerVoice"/>
/// (the voice comes from the speaker's head in a level) and flat by <see cref="LobbyVoiceChat"/> (nobody has a
/// body in the lobby).
/// </summary>
public sealed class VoicePlaybackStream
{
    const int SampleRate = NetworkPlayerVoice.SampleRate;

    /// <summary>
    /// Playback latency bound. An underrun plays silence and keeps the clock moving, so after any stall (a
    /// network hiccup, a hitch on either end) the late frames queued up behind it for good — on open mic the
    /// queue never emptied, and that speaker stayed up to 1.5 s late for the rest of the section. Past
    /// <see cref="TrimAboveSamples"/> the oldest audio is dropped back to <see cref="TrimToSamples"/>, which stays
    /// above one capture burst (a USB headset measured here delivers up to 160 ms at once), or a trim would run
    /// the queue dry before the next burst lands.
    /// </summary>
    const int TrimAboveSamples = SampleRate * 400 / 1000;
    const int TrimToSamples = SampleRate * 200 / 1000;
    /// <summary>
    /// Once the queue runs dry, silence is held until this much has arrived again. Push-to-talk stops the stream
    /// between presses, and any stall empties the queue; without a cushion the first frame that lands a little
    /// late (network jitter, a capture burst) played as a gap mid-word.
    /// </summary>
    const int PrefillSamples = SampleRate * 80 / 1000;

    readonly GameObject _host;
    readonly AudioSource _source;
    readonly ConcurrentQueue<float> _queue = new();
    /// <summary>
    /// Samples in <see cref="_queue"/>, kept with Interlocked: <see cref="PcmRead"/> dequeues on the audio thread,
    /// and ConcurrentQueue.Count is too slow to read per frame.
    /// </summary>
    int _queuedSamples;
    /// <summary>Audio thread only: waiting for <see cref="PrefillSamples"/> after an underrun.</summary>
    bool _prefilling = true;

    /// <summary>Newest frame sequence accepted from this speaker, and when (see <see cref="EnqueueFrame"/>).</summary>
    ushort _lastReceivedSequence;
    float _lastReceivedRealtime = float.NegativeInfinity;

    /// <summary>Positional: heard from <paramref name="anchor"/>, fading out between the two distances.</summary>
    public VoicePlaybackStream(GameObject anchor, float minDistance, float maxDistance)
        : this(anchor, "ProximityVoice3D", true)
    {
        // Fades to silence at max: Unity's Logarithmic law never does, and voices carried faintly level-wide.
        GameAudioManager.ApplyFadingLogRolloff(_source, minDistance, maxDistance);
    }

    /// <summary>Flat (2D): the same loudness wherever the listener is.</summary>
    public static VoicePlaybackStream CreateFlat(GameObject parent, string name) =>
        new VoicePlaybackStream(parent, name, false);

    VoicePlaybackStream(GameObject parent, string name, bool positional)
    {
        _host = new GameObject(name);
        _host.transform.SetParent(parent.transform, false);
        _host.transform.localPosition = Vector3.zero;
        _host.transform.localRotation = Quaternion.identity;
        _source = _host.AddComponent<AudioSource>();
        _source.spatialBlend = positional ? 1f : 0f;
        _source.dopplerLevel = 0f;
        // Never the source Unity virtualises when more than the real-voice budget (32) is playing: a virtual
        // streaming source stops pulling PcmRead, and the queue backs up behind it.
        _source.priority = 0;
        _source.loop = true;
        _source.playOnAwake = true;
        GameAudioManager.RouteVoiceSource(_source);
        var clip = AudioClip.Create("ProximityVoice", SampleRate * 2, 1, SampleRate, true, PcmRead);
        _source.clip = clip;
        _source.Play();
    }

    /// <summary>Ghost (dead speaker): non-positional, so the audio occlusion pass leaves it transparent too.</summary>
    public void SetGhost(bool ghost)
    {
        float blend = ghost ? 0f : 1f;
        if (_source != null && !Mathf.Approximately(_source.spatialBlend, blend))
            _source.spatialBlend = blend;
    }

    /// <summary>Per-player trim on top of the Voice mixer bus; 0 = muted for this listener only.</summary>
    public void SetVolume(float volume)
    {
        if (_source != null)
            _source.volume = Mathf.Clamp01(volume);
    }

    /// <summary>
    /// Queues one relayed frame. Frames ride unreliable delivery, so one can arrive behind a newer one (or twice);
    /// playing it then would put that audio out of order. A pause longer than a second (push-to-talk, ghost
    /// filtering) starts a fresh window, so a sequence counter that moved on a long way meanwhile is never
    /// mistaken for old.
    /// </summary>
    public void EnqueueFrame(ushort frameSeq, short[] frame, int count)
    {
        if (frame == null || count <= 0)
            return;

        float now = Time.unscaledTime;
        if (now - _lastReceivedRealtime < 1f && unchecked((short)(frameSeq - _lastReceivedSequence)) <= 0)
            return;
        _lastReceivedSequence = frameSeq;
        _lastReceivedRealtime = now;

        EnqueueShorts(frame, count);
    }

    void PcmRead(float[] data)
    {
        if (_prefilling)
        {
            if (Volatile.Read(ref _queuedSamples) < PrefillSamples)
            {
                System.Array.Clear(data, 0, data.Length);
                return;
            }
            _prefilling = false;
        }

        int played = 0;
        int i = 0;
        for (; i < data.Length; i++)
        {
            if (!_queue.TryDequeue(out float s))
            {
                // Ran dry: rest of this buffer is silence, and playback waits for a fresh cushion rather than
                // trickling out single late samples.
                _prefilling = true;
                break;
            }
            data[i] = s;
            played++;
        }
        for (; i < data.Length; i++)
            data[i] = 0f;
        if (played > 0)
            Interlocked.Add(ref _queuedSamples, -played);
    }

    void EnqueueShorts(short[] frame, int count)
    {
        for (int i = 0; i < count; i++)
            _queue.Enqueue(frame[i] / 32768f);

        int depth = Interlocked.Add(ref _queuedSamples, count);
        if (depth <= TrimAboveSamples)
            return;

        int excess = depth - TrimToSamples;
        int dropped = 0;
        while (dropped < excess && _queue.TryDequeue(out _))
            dropped++;
        Interlocked.Add(ref _queuedSamples, -dropped);
    }

    public void Dispose()
    {
        if (_source != null)
        {
            if (_source.clip != null)
            {
                _source.Stop();
                Object.Destroy(_source.clip);
            }
        }
        if (_host != null)
            Object.Destroy(_host);
    }
}
