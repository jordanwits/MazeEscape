using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The sending half of voice chat: opens the default microphone, applies the voice mode (open mic or
/// push-to-talk, see <see cref="VoiceUserSettings"/>) and streams 20 ms PCM16 frames up to the server through
/// <see cref="ProximityVoiceSession.SendVoiceFrameUp"/>, which relays them to everyone else.
///
/// Shared by the level avatar (<see cref="NetworkPlayerVoice"/>, owner side) and the menu lobby
/// (<see cref="LobbyVoiceChat"/>). Only one of them may hold the microphone at a time: the lobby lives in the Menu
/// scene and the avatars only in the levels, so the scene change hands it over.
/// </summary>
public sealed class VoiceMicTransmitter
{
    const int SampleRate = NetworkPlayerVoice.SampleRate;
    const int FrameSamples = NetworkPlayerVoice.FrameSamples;

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

    readonly InputActionAsset _inputActions;
    readonly string _playerMapName;
    readonly string _pushToTalkActionName;
    readonly Object _logContext;

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

    InputActionAsset _runtimeInputActions;
    InputAction _pushToTalkAction;
    bool _pttWired;
    float _pushToTalkTailUntil;

    /// <summary>Unscaled time until which the frames going out count as speech (for a talking indicator).</summary>
    float _speechUntil = float.NegativeInfinity;

    /// <param name="inputActions">
    /// The actions asset that carries push-to-talk (the same InputSystem_Actions the player prefabs use). Null
    /// falls back to V / left shoulder.
    /// </param>
    public VoiceMicTransmitter(InputActionAsset inputActions, string playerMapName, string pushToTalkActionName,
        Object logContext)
    {
        _inputActions = inputActions;
        _playerMapName = playerMapName;
        _pushToTalkActionName = pushToTalkActionName;
        _logContext = logContext;
    }

    /// <summary>A microphone is open and recording into the capture ring.</summary>
    public bool HasMicrophone => !string.IsNullOrEmpty(_micDevice) && _micClip != null;

    /// <summary>Speech-loud audio is going out right now (with the same short hangover the AI uses).</summary>
    public bool IsSendingSpeech => Time.unscaledTime < _speechUntil;

    /// <summary>Wires push-to-talk and opens the microphone. False when there is no usable microphone.</summary>
    public bool Start()
    {
        TryWirePushToTalk();
        return StartMicrophone();
    }

    /// <summary>
    /// Sends whatever the voice mode allows this frame. Call every Update while the netcode session is listening.
    /// </summary>
    public void Tick()
    {
        if (!HasMicrophone)
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

    /// <summary>Closes the microphone and drops the push-to-talk input copy.</summary>
    public void Dispose()
    {
        StopMicrophone();
        if (_pushToTalkAction != null)
            _pushToTalkAction.Disable();

        _pushToTalkAction = null;
        _pttWired = false;
        KeybindSettings.Changed -= HandleKeybindsChanged;
        if (_runtimeInputActions != null)
        {
            Object.Destroy(_runtimeInputActions);
            _runtimeInputActions = null;
        }
        _speechUntil = float.NegativeInfinity;
    }

    void TryWirePushToTalk()
    {
        if (_inputActions == null)
        {
            _pttWired = false;
            return;
        }

        // A private copy carrying the player's rebinds (see KeybindSettings), so push-to-talk follows settings.
        if (_runtimeInputActions == null)
        {
            _runtimeInputActions = Object.Instantiate(_inputActions);
            KeybindSettings.ApplyTo(_runtimeInputActions);
            KeybindSettings.Changed += HandleKeybindsChanged;
        }

        _pushToTalkAction = _runtimeInputActions.FindActionMap(_playerMapName, true)
            .FindAction(_pushToTalkActionName, true);
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
                "VoiceMicTransmitter: Microphone may not support 16kHz; capture quality may be poor.",
                _logContext);
        }
        _micClip = Microphone.Start(_micDevice, true, 1, SampleRate);
        _micReadPosition = 0;
        _micReadCursorStale = true;
        if (_micClip == null)
        {
            _micDevice = null;
            return false;
        }
        if (_micClip.samples < FrameSamples)
        {
            Debug.LogWarning("VoiceMicTransmitter: Microphone buffer too small.", _logContext);
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

        // Same speech gate the server applies before telling the AI (ProximityVoiceSession.IsSpeech); only feeds the
        // local talking indicator, never what is sent.
        if (ProximityVoiceSession.FrameRms(_micSendScratch, FrameSamples) >= ProximityVoiceSession.SpeechRmsThreshold)
            _speechUntil = Time.unscaledTime + ProximityVoiceSession.SpeechHangoverSeconds;

        // Open mic streams every frame, silence included (raw 16 kHz PCM, about 32 KB/s per speaker, relayed by the
        // host to every other player). A sender-side voice gate was tried and cut quiet syllables and word tails,
        // which listeners heard as choppy speech. The AI still only reacts to speech-loud frames (see
        // ProximityVoiceSession.IsSpeech).
        _sendSequence++;
        ProximityVoiceSession.SendVoiceFrameUp(_sendSequence, _micSendScratch, FrameSamples);
    }
}
