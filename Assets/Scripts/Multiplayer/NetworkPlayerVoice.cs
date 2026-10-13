using System.Collections.Concurrent;
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

    [Header("3D (others hear you here)")]
    [Tooltip("If null, resolved to CameraPitch or player camera.")]
    [SerializeField] Transform voiceAnchor;

    [SerializeField, Min(0.1f)] float minHearingDistance = 1.2f;
    [SerializeField, Min(0.5f)] float maxHearingDistance = 22f;

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

    InputAction _pushToTalkAction;
    bool _pttWired;

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

            // This player's slot in the local voice mix (lobby crew card); survives the level switch.
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

        _pushToTalkAction = inputActions.FindActionMap(playerMapName, true)
            .FindAction(pushToTalkActionName, true);
        _pushToTalkAction?.Enable();
        _pttWired = _pushToTalkAction != null;
    }

    bool CanSendThisFrame()
    {
        if (VoiceUserSettings.IsOpenMic)
            return true;
        if (VoiceUserSettings.IsPushToTalk)
        {
            if (_pttWired && _pushToTalkAction != null)
                return _pushToTalkAction.IsPressed();
            if (Keyboard.current != null)
                return Keyboard.current.vKey.isPressed;
            if (Gamepad.current != null)
                return Gamepad.current.leftShoulder.isPressed;
        }
        return true;
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
        _sendSequence++;
        ProximityVoiceSession.SendVoiceFrameUp(_sendSequence, _micSendScratch, FrameSamples);
    }

    public void EnqueuePcm16Frame(ushort frameSeq, short[] frame, int count)
    {
        if (frame == null || count <= 0 || _remoteOutput == null)
            return;
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
        readonly GameObject _host;
        readonly AudioSource _source;
        readonly ConcurrentQueue<float> _queue = new();
        const int MaxQueuedSamples = 24000;
        public RemoteVoiceOutput(GameObject anchor, float min, float max)
        {
            _host = new GameObject("ProximityVoice3D");
            _host.transform.SetParent(anchor.transform, false);
            _host.transform.localPosition = Vector3.zero;
            _host.transform.localRotation = Quaternion.identity;
            _source = _host.AddComponent<AudioSource>();
            _source.spatialBlend = 1f;
            _source.rolloffMode = AudioRolloffMode.Logarithmic;
            _source.dopplerLevel = 0f;
            _source.minDistance = min;
            _source.maxDistance = max;
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
            for (int i = 0; i < data.Length; i++)
            {
                if (!_queue.TryDequeue(out float s))
                    s = 0f;
                data[i] = s;
            }
        }

        public void EnqueueShorts(short[] frame, int count)
        {
            if (count <= 0)
                return;
            // One budget check per frame, then a bounded dequeue pass — avoid reading ConcurrentQueue.Count
            // around every enqueued sample (it can be O(n) and was on the per-sample hot path for proximity voice).
            int room = MaxQueuedSamples - count;
            if (room < 0)
                room = 0;
            int excess = _queue.Count - room;
            for (int d = 0; d < excess; d++)
            {
                if (!_queue.TryDequeue(out _))
                    break;
            }
            for (int i = 0; i < count; i++)
                _queue.Enqueue(frame[i] / 32768f);
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
