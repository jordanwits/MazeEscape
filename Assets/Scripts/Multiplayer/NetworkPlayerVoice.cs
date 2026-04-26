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
    readonly float[] _micFloatScratch = new float[FrameSamples];
    readonly short[] _micSendScratch = new short[FrameSamples];
    float[] _wrap1;
    float[] _wrap2;
    ushort _sendSequence;

    InputAction _pushToTalkAction;
    bool _pttWired;

    RemoteVoiceOutput _remoteOutput;

    public override void OnNetworkSpawn()
    {
        _networkObject = GetComponent<NetworkObject>();
        VoiceClientRegistry.Register(_networkObject.OwnerClientId, this);

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
        }
    }

    public override void OnNetworkDespawn()
    {
        if (_networkObject != null)
            VoiceClientRegistry.Unregister(_networkObject.OwnerClientId, this);

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
        if (!IsSpawned || !IsOwner)
            return;
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            return;
        if (string.IsNullOrEmpty(_micDevice) || _micClip == null)
            return;
        if (!CanSendThisFrame())
            return;
        if (!Microphone.IsRecording(_micDevice))
            return;
        PullMicAndSend();
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

    void PullMicAndSend()
    {
        int w = Microphone.GetPosition(_micDevice);
        if (w < 0)
            return;
        int len = _micClip.samples;
        int r = _micReadPosition;
        int available = w - r;
        if (available < 0)
            available += len;
        if (available < FrameSamples)
            return;
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
