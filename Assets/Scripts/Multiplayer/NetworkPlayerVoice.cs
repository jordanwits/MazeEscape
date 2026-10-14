using System.Collections.Concurrent;
using System.Threading;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayerVoice : NetworkBehaviour
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 320; // 20ms @ 16kHz mono

    /// <summary>
    /// Most capture a sender lets queue up before skipping to the newest audio (300 ms). Must stay well above the
    /// size of one capture burst: a USB sound card measured here hands Unity up to 160 ms of samples at once, and a
    /// 150 ms budget threw most of each burst away.
    /// </summary>
    const int MaxMicLatencySamples = SampleRate * 300 / 1000;
    /// <summary>Per-Update send cap, a little over the latency budget above.</summary>
    const int MaxMicFramesPerUpdate = 16;
    /// <summary>A gap this long between reads (a hitch, a stall) may have let the 1 s capture ring lap the cursor.</summary>
    const float MicResyncGapSeconds = 0.5f;
    /// <summary>
    /// Push-to-talk keeps sending this long after the key comes up: the end of the last word is still inside the
    /// capture driver at that moment (up to 160 ms on the USB headset measured here) and was otherwise never sent.
    /// </summary>
    const float PushToTalkReleaseTailSeconds = 0.2f;
    /// <summary>Open-mic voice gate hangover in whole frames (<see cref="ProximityVoiceSession.VoiceActivationHangoverSeconds"/>).</summary>
    static readonly int VoiceGateHangoverFrames =
        Mathf.RoundToInt(ProximityVoiceSession.VoiceActivationHangoverSeconds * SampleRate / FrameSamples);

    [Header("3D (others hear you here)")]
    [Tooltip("If null, resolved to CameraPitch or player camera.")]
    [SerializeField] Transform voiceAnchor;

    [SerializeField, Min(0.1f)] float minHearingDistance = 1.2f;
    [Tooltip("Where a voice has faded to silence (see GameAudioManager.FadingLogRolloff); full loudness law up to half of this.")]
    [SerializeField, Min(0.5f)] float maxHearingDistance = 30f;

    [Header("Input (push-to-talk; use same Input Actions asset as PlayerController)")]
    [SerializeField] InputActionAsset inputActions;
    [SerializeField] string playerMapName = "Player";
    [SerializeField] string pushToTalkActionName = "PushToTalk";

    NetworkObject _networkObject;
    string _micDevice;
    AudioClip _micClip;
    int _micReadPosition;
    /// <summary>Sending stopped (push-to-talk released, nothing read yet): the next read starts from "now".</summary>
    bool _micReadCursorStale = true;
    float _lastMicPullRealtime;
    readonly float[] _micFloatScratch = new float[FrameSamples];
    readonly short[] _micSendScratch = new short[FrameSamples];
    float[] _wrap1;
    float[] _wrap2;
    ushort _sendSequence;

    /// <summary>Frames the open-mic gate still sends after the last loud one (counts down in audio time, not wall time).</summary>
    int _voiceGateFramesLeft;
    /// <summary>The last frame the closed gate held back, sent first when speech starts so the onset isn't clipped.</summary>
    readonly short[] _voicePreRoll = new short[FrameSamples];
    bool _hasVoicePreRoll;

    /// <summary>Receive side: newest frame sequence accepted from this speaker, and when (see <see cref="EnqueuePcm16Frame"/>).</summary>
    ushort _lastReceivedSequence;
    float _lastReceivedRealtime = float.NegativeInfinity;

    InputActionAsset _runtimeInputActions;
    InputAction _pushToTalkAction;
    bool _pttWired;
    float _pushToTalkTailUntil;

    RemoteVoiceOutput _remoteOutput;
    /// <summary>Cached because <see cref="_networkObject"/> is gone by the time we unsubscribe.</summary>
    ulong _voiceOwnerClientId;
    PlayerHealth _speakerHealth;

    public override void OnNetworkSpawn()
    {
        _networkObject = GetComponent<NetworkObject>();
        _voiceOwnerClientId = _networkObject.OwnerClientId;
        VoiceClientRegistry.Register(_voiceOwnerClientId, this);

        if (IsOwner)
        {
            TryWirePushToTalk();
            if (!StartMicrophone())
            {
                Debug.LogWarning(
                    "NetworkPlayerVoice: No usable microphone. Proximity voice capture is disabled for this client.",
                    this);
            }
        }
        else
        {
            Transform anchor = voiceAnchor;
            if (anchor == null)
                anchor = FindBestVoiceTransform();
            if (anchor == null)
                anchor = transform;
            _remoteOutput = new RemoteVoiceOutput(anchor.gameObject, minHearingDistance, maxHearingDistance);
            _speakerHealth = GetComponent<PlayerHealth>();
            UpdateGhostVoice();

            // This player's slot in the local voice mix (lobby crew roster, pause-menu CREW card); survives the level switch.
            ApplyVoiceMix();
            VoicePlayerMixSettings.Changed += HandleVoiceMixChanged;
        }
    }

    void HandleVoiceMixChanged(ulong clientId)
    {
        if (clientId == _voiceOwnerClientId)
            ApplyVoiceMix();
    }

    void ApplyVoiceMix()
    {
        _remoteOutput?.SetVolume(VoicePlayerMixSettings.GetEffectiveVolume(_voiceOwnerClientId));
    }

    public override void OnNetworkDespawn()
    {
        VoicePlayerMixSettings.Changed -= HandleVoiceMixChanged;

        if (_networkObject != null)
            VoiceClientRegistry.Unregister(_voiceOwnerClientId, this);

        StopMicrophone();
        if (_pushToTalkAction != null)
        {
            _pushToTalkAction.Disable();
        }

        _pushToTalkAction = null;
        KeybindSettings.Changed -= HandleKeybindsChanged;
        if (_runtimeInputActions != null)
        {
            Destroy(_runtimeInputActions);
            _runtimeInputActions = null;
        }
        _remoteOutput?.Dispose();
        _remoteOutput = null;
    }

    void Update()
    {
        if (!IsSpawned)
            return;
        if (!IsOwner)
        {
            UpdateGhostVoice();
            return;
        }
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            return;
        if (string.IsNullOrEmpty(_micDevice) || _micClip == null)
            return;
        if (!CanSendThisFrame())
        {
            _micReadCursorStale = true;
            return;
        }
        if (!Microphone.IsRecording(_micDevice))
            return;
        PullMicAndSend();
    }

    /// <summary>
    /// A dead speaker's frames only ever reach dead listeners (the server filters them — see
    /// <see cref="ProximityVoiceSession"/>), and those listeners are spectating somewhere else entirely, so the
    /// voice is played flat instead of from the body. Polled rather than evented: replicated life state only
    /// raises <see cref="PlayerHealth.Died"/> on clients, never the way back.
    /// </summary>
    void UpdateGhostVoice()
    {
        _remoteOutput?.SetGhost(_speakerHealth != null && _speakerHealth.IsDead);
    }

    void TryWirePushToTalk()
    {
        if (inputActions == null)
        {
            _pttWired = false;
            return;
        }

        // A private copy carrying the player's rebinds (see KeybindSettings), so push-to-talk follows settings.
        if (_runtimeInputActions == null)
        {
            _runtimeInputActions = Instantiate(inputActions);
            KeybindSettings.ApplyTo(_runtimeInputActions);
            KeybindSettings.Changed += HandleKeybindsChanged;
        }

        _pushToTalkAction = _runtimeInputActions.FindActionMap(playerMapName, true)
            .FindAction(pushToTalkActionName, true);
        _pushToTalkAction?.Enable();
        _pttWired = _pushToTalkAction != null;
    }

    void HandleKeybindsChanged()
    {
        KeybindSettings.ApplyTo(_runtimeInputActions);
    }

    bool CanSendThisFrame()
    {
        if (VoiceUserSettings.IsOpenMic)
            return true;
        if (VoiceUserSettings.IsPushToTalk && TryReadPushToTalk(out bool held))
        {
            float now = Time.realtimeSinceStartup;
            if (held)
                _pushToTalkTailUntil = now + PushToTalkReleaseTailSeconds;
            return held || now < _pushToTalkTailUntil;
        }
        return true;
    }

    /// <summary>False when there is no push-to-talk input at all (no action, keyboard or gamepad): then we just send.</summary>
    bool TryReadPushToTalk(out bool held)
    {
        if (_pttWired && _pushToTalkAction != null)
        {
            held = _pushToTalkAction.IsPressed();
            return true;
        }
        if (Keyboard.current != null)
        {
            held = Keyboard.current.vKey.isPressed;
            return true;
        }
        if (Gamepad.current != null)
        {
            held = Gamepad.current.leftShoulder.isPressed;
            return true;
        }
        held = false;
        return false;
    }

    bool StartMicrophone()
    {
        if (Microphone.devices == null || Microphone.devices.Length == 0)
            return false;
        _micDevice = Microphone.devices[0];
        int minFreq;
        int maxFreq;
        Microphone.GetDeviceCaps(_micDevice, out minFreq, out maxFreq);
        if (maxFreq > 0 && maxFreq < SampleRate)
        {
            Debug.LogWarning(
                "NetworkPlayerVoice: Microphone may not support 16kHz; capture quality may be poor.",
                this);
        }
        _micClip = Microphone.Start(_micDevice, true, 1, SampleRate);
        _micReadPosition = 0;
        if (_micClip == null)
            return false;
        if (_micClip.samples < FrameSamples)
        {
            Debug.LogWarning("NetworkPlayerVoice: Microphone buffer too small.", this);
            StopMicrophone();
            return false;
        }
        return true;
    }

    void StopMicrophone()
    {
        if (string.IsNullOrEmpty(_micDevice))
        {
            _micDevice = null;
            _micClip = null;
            return;
        }
        if (Microphone.IsRecording(_micDevice))
            Microphone.End(_micDevice);
        _micDevice = null;
        _micClip = null;
    }

    /// <summary>
    /// Sends every whole 20 ms frame the mic has captured since the last call, not one per Update: real time needs
    /// 50 frames a second, so below 50 fps a one-frame read fell behind, the 1 s capture ring lapped it, and listeners
    /// heard choppy speech with whole seconds missing. A backlog past <see cref="MaxMicLatencySamples"/> is skipped
    /// rather than sent late, and the cursor jumps to "now" after any pause in sending (push-to-talk released, a long
    /// hitch), so audio from before a press is never sent and the writer can never lap the reader.
    /// </summary>
    void PullMicAndSend()
    {
        int w = Microphone.GetPosition(_micDevice);
        if (w < 0)
            return;
        int len = _micClip.samples;

        float now = Time.realtimeSinceStartup;
        if (_micReadCursorStale || now - _lastMicPullRealtime > MicResyncGapSeconds)
        {
            _micReadPosition = w;
            _micReadCursorStale = false;
            // Whatever the voice gate held or was counting down belongs to audio from before the pause.
            _hasVoicePreRoll = false;
            _voiceGateFramesLeft = 0;
        }
        _lastMicPullRealtime = now;

        int available = w - _micReadPosition;
        if (available < 0)
            available += len;
        if (available > MaxMicLatencySamples)
        {
            _micReadPosition = (w - FrameSamples + len) % len;
            available = FrameSamples;
        }

        for (int sent = 0; available >= FrameSamples && sent < MaxMicFramesPerUpdate; sent++)
        {
            SendMicFrame(_micReadPosition, len);
            available -= FrameSamples;
        }
    }

    void SendMicFrame(int r, int len)
    {
        if (r + FrameSamples <= len)
        {
            _micClip.GetData(_micFloatScratch, r);
        }
        else
        {
            int p1 = len - r;
            int p2 = FrameSamples - p1;
            if (_wrap1 == null || _wrap1.Length < p1)
                _wrap1 = new float[p1];
            if (_wrap2 == null || _wrap2.Length < p2)
                _wrap2 = new float[p2];
            _micClip.GetData(_wrap1, r);
            _micClip.GetData(_wrap2, 0);
            for (int i = 0; i < p1; i++)
                _micFloatScratch[i] = _wrap1[i];
            for (int j = 0; j < p2; j++)
                _micFloatScratch[p1 + j] = _wrap2[j];
        }
        _micReadPosition = (r + FrameSamples) % len;

        for (int i = 0; i < FrameSamples; i++)
        {
            float f = Mathf.Clamp(_micFloatScratch[i], -1f, 1f);
            _micSendScratch[i] = (short)(f * 32767f);
        }

        if (VoiceUserSettings.IsOpenMic && !PassOpenMicVoiceGate())
            return;

        _sendSequence++;
        ProximityVoiceSession.SendVoiceFrameUp(_sendSequence, _micSendScratch, FrameSamples);
    }

    /// <summary>
    /// Open mic used to stream every frame, silence included: raw 16 kHz PCM, about 32 KB/s per speaker, which the
    /// host relays to every other player (about 2.3 Mbit/s of upload at four players with nobody talking). Now a
    /// frame goes out only while it is speech-loud (<see cref="ProximityVoiceSession.VoiceActivationRmsThreshold"/>)
    /// or within the hangover after the last loud one. The frame just before an onset is held back and sent first,
    /// so the start of a word isn't clipped. Push-to-talk is not gated: holding the key already says "send".
    /// Also resets the receiver's playback latency at every pause (its queue drains while nothing arrives).
    /// </summary>
    bool PassOpenMicVoiceGate()
    {
        if (ProximityVoiceSession.FrameRms(_micSendScratch, FrameSamples) >= ProximityVoiceSession.VoiceActivationRmsThreshold)
        {
            _voiceGateFramesLeft = VoiceGateHangoverFrames;
        }
        else if (_voiceGateFramesLeft > 0)
        {
            _voiceGateFramesLeft--;
        }
        else
        {
            System.Array.Copy(_micSendScratch, _voicePreRoll, FrameSamples);
            _hasVoicePreRoll = true;
            return false;
        }

        if (_hasVoicePreRoll)
        {
            _hasVoicePreRoll = false;
            _sendSequence++;
            ProximityVoiceSession.SendVoiceFrameUp(_sendSequence, _voicePreRoll, FrameSamples);
        }
        return true;
    }

    public void EnqueuePcm16Frame(ushort frameSeq, short[] frame, int count)
    {
        if (frame == null || count <= 0 || _remoteOutput == null)
            return;

        // Frames ride unreliable delivery, so one can arrive behind a newer one (or twice); playing it then would
        // put that audio out of order. A pause longer than a second (voice gate, push-to-talk, ghost filtering)
        // starts a fresh window, so a sequence counter that moved on a long way meanwhile is never mistaken for old.
        float now = Time.unscaledTime;
        if (now - _lastReceivedRealtime < 1f && unchecked((short)(frameSeq - _lastReceivedSequence)) <= 0)
            return;
        _lastReceivedSequence = frameSeq;
        _lastReceivedRealtime = now;

        _remoteOutput.EnqueueShorts(frame, count);
    }

    Transform FindBestVoiceTransform()
    {
        foreach (Transform t in GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == "CameraPitch")
                return t;
        }
        if (GetComponentInChildren<PlayerController>(true) is { } p && p.LookPitchTransform != null)
            return p.LookPitchTransform;
        return null;
    }

    sealed class RemoteVoiceOutput
    {
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
        /// Once the queue runs dry, silence is held until this much has arrived again. The open-mic voice gate stops
        /// the stream between utterances, so every utterance starts on an empty queue; without a cushion the first
        /// frame that lands a little late (network jitter, a capture burst) played as a gap mid-word.
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

        public RemoteVoiceOutput(GameObject anchor, float min, float max)
        {
            _host = new GameObject("ProximityVoice3D");
            _host.transform.SetParent(anchor.transform, false);
            _host.transform.localPosition = Vector3.zero;
            _host.transform.localRotation = Quaternion.identity;
            _source = _host.AddComponent<AudioSource>();
            _source.spatialBlend = 1f;
            // Fades to silence at max: Unity's Logarithmic law never does, and voices carried faintly level-wide.
            GameAudioManager.ApplyFadingLogRolloff(_source, min, max);
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

        public void EnqueueShorts(short[] frame, int count)
        {
            if (count <= 0)
                return;
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
}
