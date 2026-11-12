using UnityEngine;

/// <summary>
/// The receiving half of voice chat for one remote speaker: plays the PCM16 frames the server relays (see
/// <see cref="ProximityVoiceSession"/>). Used positionally by <see cref="NetworkPlayerVoice"/> (the voice comes from
/// the speaker's head in a level) and flat by <see cref="LobbyVoiceChat"/> (nobody has a body in the lobby). The owner
/// must call <see cref="Tick"/> every frame.
///
/// <para><b>Why a ring buffer and not a streamed clip.</b> Playback used to be a streamed AudioClip fed from a
/// PCMReaderCallback. Unity pulls a streamed clip 400 ms at a time, every 400 ms, far ahead of what is heard
/// (measured 2026-11-12: 4096 + 2304 samples per pull at 16 kHz). Real-time speech has only just arrived by each pull,
/// so it was either a little short — padded with silence that then played — or a little long, which tripped a 400 ms
/// trim that threw 200 ms away and left the next pull short. With a perfect network that measured 18 dropouts
/// (2.4 s of silence) in 11 s of speech: the "mic cuts out for a split second" bug.</para>
///
/// <para>Now the source loops a plain in-memory clip and each frame is written into it with SetData, at the slot its
/// sequence number gives it, a short adaptive distance ahead of the play head (the jitter buffer). Unity mixes a
/// loaded clip in small blocks, so audio plays as soon as it is written. Everything behind the play head is zeroed
/// again, so when the speaker stops (push-to-talk, a stall) the ring plays silence instead of replaying old speech two
/// seconds later. Measured with a steady signal: no dropouts on a perfect network (was 18 in 11 s).</para>
/// </summary>
public sealed class VoicePlaybackStream
{
    const int SampleRate = NetworkPlayerVoice.SampleRate;
    /// <summary>Frames are written, and played regions cleared, in whole blocks of this size, aligned in the ring.</summary>
    const int Block = NetworkPlayerVoice.FrameSamples;
    /// <summary>The looping clip: 2 s, a whole number of blocks, so an aligned block never straddles its end.</summary>
    const int RingSamples = SampleRate * 2;

    /// <summary>
    /// Never write closer than this ahead of the play head: Unity's mixer may already have read that far, and audio
    /// written behind its read position is simply never heard.
    /// </summary>
    const int GuardSamples = SampleRate * 40 / 1000;
    /// <summary>
    /// Jitter buffer: where a talk spurt starts, ahead of the play head. Must cover the gaps between frame arrivals —
    /// the sender's, host's and listener's frame times plus network jitter. Grows on underruns, shrinks back slowly.
    /// </summary>
    const int BaseLeadSamples = SampleRate * 120 / 1000;
    const int MaxLeadSamples = SampleRate * 400 / 1000;
    const int LeadStepSamples = SampleRate * 30 / 1000;
    /// <summary>
    /// More queued than target + this (a clump after a stall) and the queue skips forward to the newest audio, so
    /// latency never creeps up for the rest of the section.
    /// </summary>
    const int OverflowHeadroomSamples = SampleRate * 160 / 1000;
    /// <summary>Running dry less than this after the last frame is an underrun; longer is the speaker pausing.</summary>
    const float UnderrunWindowSeconds = 0.25f;
    /// <summary>
    /// A listener frame this long is a local hitch: running dry across it says nothing about network jitter, so it
    /// does not grow the jitter buffer (the gap itself cannot be helped).
    /// </summary>
    const float LocalHitchSeconds = 0.1f;
    /// <summary>The jitter buffer shrinks one step after this long without an underrun.</summary>
    const float LeadDecaySeconds = 20f;
    /// <summary>
    /// A frame this many sequence numbers behind the newest is not a straggler but a sender that started counting
    /// again (a new transmitter): it starts a fresh spurt instead of being dropped as hopelessly late.
    /// </summary>
    const int SequenceResetFrames = 64;

    readonly GameObject _host;
    readonly AudioSource _source;
    readonly AudioClip _clip;
    readonly float[] _frameScratch = new float[Block];
    static readonly float[] s_ZeroBlock = new float[Block];
    static float[] s_ZeroRing;

    /// <summary>Samples the play head has consumed since Play (absolute, so it never wraps); always ≡ timeSamples (mod ring).</summary>
    long _played;
    int _lastTimeSamples;
    double _lastDspTime;
    /// <summary>End of the furthest frame written; [_played, _writtenEnd) is the queued audio (holes stay silent).</summary>
    long _writtenEnd;
    /// <summary>Every whole block before this has been zeroed after playing.</summary>
    long _cleared;
    /// <summary>A talk spurt is queued; false once the play head has run past the last frame written.</summary>
    bool _active;

    /// <summary>
    /// The newest frame accepted and where it went. Every other frame of the spurt is placed relative to it by
    /// sequence number, so a lost frame leaves a 20 ms hole instead of pulling everything after it forward.
    /// </summary>
    ushort _newestSeq;
    long _newestSlot;

    int _targetLead = BaseLeadSamples;
    float _lastFrameTime = float.NegativeInfinity;
    float _lastUnderrunTime;

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
        // Never the source Unity drops when more than the real-voice budget (32) is playing.
        _source.priority = 0;
        _source.loop = true;
        _source.playOnAwake = true;
        GameAudioManager.RouteVoiceSource(_source);
        // Not streamed: a loaded clip is mixed straight from memory, so SetData ahead of the play head is heard
        // within one mix block. Created silent.
        _clip = AudioClip.Create("ProximityVoice", RingSamples, 1, SampleRate, false);
        _source.clip = _clip;
        _source.Play();
        _lastDspTime = AudioSettings.dspTime;
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
    /// Every frame: follows the play head, clears what it has played, and lets the jitter buffer shrink back after
    /// a calm spell. Without it a speaker who stops talking would have their last words replayed when the ring loops.
    /// </summary>
    public void Tick()
    {
        if (_source == null || _clip == null)
            return;

        // Stopped by something else (its object was disabled and re-enabled): start it again. The play head restarts
        // at 0, which the ring arithmetic absorbs; the next frame starts a fresh spurt.
        if (!_source.isPlaying)
        {
            if (!_source.isActiveAndEnabled)
                return;
            _source.Play();
            _lastTimeSamples = _source.timeSamples;
            _lastDspTime = AudioSettings.dspTime;
            _active = false;
        }

        AdvancePlayHead();

        float now = Time.unscaledTime;
        if (_targetLead > BaseLeadSamples && now - _lastUnderrunTime > LeadDecaySeconds)
        {
            _targetLead = Mathf.Max(BaseLeadSamples, _targetLead - LeadStepSamples);
            _lastUnderrunTime = now;
        }
    }

    /// <summary>
    /// Writes one relayed frame into the ring, at the slot its sequence number gives it. Frames ride unreliable
    /// delivery, so some never arrive and some arrive behind a newer one (or twice): a missing frame stays a 20 ms
    /// hole, a straggler still drops into its own slot if that has not played yet, and a duplicate rewrites the same
    /// audio. (Writing frames back to back as they came used to shorten the stream by every lost or reordered
    /// frame, drain the buffer and rebuffer with a gap.) A pause (push-to-talk, ghost filtering) ends the spurt; the
    /// next frame starts a fresh one wherever the sequence counter has got to.
    /// </summary>
    public void EnqueueFrame(ushort frameSeq, short[] frame, int count)
    {
        if (frame == null || count <= 0 || _clip == null || _source == null)
            return;

        float now = Time.unscaledTime;
        AdvancePlayHead();

        short sinceNewest = unchecked((short)(frameSeq - _newestSeq));
        bool restarted = _active && sinceNewest <= -SequenceResetFrames;
        if (_active && sinceNewest <= 0 && !restarted)
        {
            // A straggler or a duplicate: its own slot, if that is still ahead of what the mixer has read.
            long ownSlot = _newestSlot + sinceNewest * (long)Block;
            if (ownSlot >= _played + GuardSamples)
                WriteFrame(frame, count, ownSlot);
            return;
        }

        long slot = _active && !restarted ? _newestSlot + sinceNewest * (long)Block : long.MinValue;
        bool late = slot < _played + GuardSamples;
        bool tooFar = !late && slot + Block - _played > _targetLead + OverflowHeadroomSamples;
        if (late || tooFar)
        {
            // Ran dry while frames were still coming: the gaps between arrivals are longer than the buffer allows.
            if (late && !restarted && now - _lastFrameTime < UnderrunWindowSeconds
                && Time.unscaledDeltaTime < LocalHitchSeconds)
            {
                _targetLead = Mathf.Min(MaxLeadSamples, _targetLead + LeadStepSamples);
                _lastUnderrunTime = now;
            }

            // Start (again) a jitter buffer's worth ahead; everything in between is silence already. Too far ahead
            // is a clump (a stall on either end or in between, then everything at once): the queued backlog past
            // the new start is dropped, so latency drops back instead of staying that far behind for good.
            long start = AlignUp(_played + _targetLead);
            if (tooFar)
            {
                for (long pos = start + Block; pos < _writtenEnd; pos += Block)
                    WriteBlock(s_ZeroBlock, pos);
                _writtenEnd = start;
            }
            slot = start;
        }

        _newestSeq = frameSeq;
        _newestSlot = slot;
        _lastFrameTime = now;
        _active = true;
        WriteFrame(frame, count, slot);
    }

    void WriteFrame(short[] frame, int count, long slot)
    {
        int n = Mathf.Min(count, Block);
        for (int i = 0; i < n; i++)
            _frameScratch[i] = frame[i] / 32768f;
        for (int i = n; i < Block; i++)
            _frameScratch[i] = 0f;
        WriteBlock(_frameScratch, slot);
        if (slot + Block > _writtenEnd)
            _writtenEnd = slot + Block;
    }

    /// <summary>
    /// Brings <see cref="_played"/> up to the source's play head and zeroes every whole block it has passed.
    /// </summary>
    void AdvancePlayHead()
    {
        int timeSamples = _source.timeSamples;
        double dsp = AudioSettings.dspTime;
        long delta = (timeSamples - _lastTimeSamples + RingSamples) % RingSamples;
        // timeSamples wraps every 2 s, so a longer hitch hides whole laps; the DSP clock says how many.
        long expected = (long)((dsp - _lastDspTime) * SampleRate);
        if (expected - delta > RingSamples * 3 / 4)
            delta += (expected - delta + RingSamples / 2) / RingSamples * RingSamples;
        _lastTimeSamples = timeSamples;
        _lastDspTime = dsp;
        _played += delta;

        if (_active && _writtenEnd <= _played)
            _active = false;

        long playedBlocks = _played - _played % Block;
        if (playedBlocks - _cleared > RingSamples / 2)
        {
            // Over a second went by unseen (a hitch): nothing can still be queued, so wipe the whole ring at once.
            s_ZeroRing ??= new float[RingSamples];
            _clip.SetData(s_ZeroRing, 0);
            _cleared = playedBlocks;
            _active = false;
            return;
        }
        for (; _cleared < playedBlocks; _cleared += Block)
            WriteBlock(s_ZeroBlock, _cleared);
    }

    void WriteBlock(float[] block, long absolutePosition)
    {
        _clip.SetData(block, (int)(absolutePosition % RingSamples));
    }

    static long AlignUp(long position) => (position + Block - 1) / Block * Block;

    public void Dispose()
    {
        if (_source != null)
            _source.Stop();
        if (_clip != null)
            Object.Destroy(_clip);
        if (_host != null)
            Object.Destroy(_host);
    }
}
