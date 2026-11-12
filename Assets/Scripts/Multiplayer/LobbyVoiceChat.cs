using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Voice chat in the menu lobby. Player avatars only spawn with the level, so <see cref="NetworkPlayerVoice"/> is not
/// there to carry it; this sends the local microphone over the same wire (<see cref="VoiceMicTransmitter"/> →
/// <see cref="ProximityVoiceSession"/>, which the host relays to everyone while it is in the Menu scene) and plays each
/// other player flat — nobody has a position yet — through their own <see cref="VoicePlaybackStream"/>, at the local
/// mix the crew roster sets (<see cref="VoicePlayerMixSettings"/>). Voice mode and push-to-talk follow settings.
///
/// Added by <see cref="MainMenuController"/>, so it lives and dies with the Menu scene: it lets go of the microphone
/// before the level's avatars open it. Runs only while a non-offline session is connected.
/// </summary>
[DisallowMultipleComponent]
public sealed class LobbyVoiceChat : MonoBehaviour
{
    /// <summary>The running lobby voice, if any; incoming frames with no avatar to play them land here.</summary>
    public static LobbyVoiceChat Active { get; private set; }

    /// <summary>A speaker who has sent nothing for this long and has left the roster gets their stream dropped.</summary>
    const float IdleSpeakerSeconds = 2f;
    const float PruneIntervalSeconds = 1f;

    sealed class Speaker
    {
        public VoicePlaybackStream Stream;
        public float LastFrameTime;
        public float SpeakingUntil;
    }

    MultiplayerSessionController _session;
    VoiceMicTransmitter _transmitter;
    GameObject _speakerRoot;
    bool _running;
    bool _hasMicrophone;
    float _nextPruneTime;
    readonly Dictionary<ulong, Speaker> _speakers = new();
    readonly List<ulong> _pruneScratch = new();

    /// <summary>Lobby voice is up: a lobby session is connected and no avatar carries voice yet.</summary>
    public bool IsRunning => _running;

    /// <summary>A microphone opened when lobby voice started (false: you can hear but not be heard).</summary>
    public bool HasMicrophone => _running && _hasMicrophone;

    /// <summary>Your own speech is going out right now.</summary>
    public bool IsLocalSpeaking => _running && _transmitter != null && _transmitter.IsSendingSpeech;

    /// <summary>Another player's speech-loud audio arrived within the last moment.</summary>
    public bool IsSpeaking(ulong clientId) =>
        _running && _speakers.TryGetValue(clientId, out Speaker speaker) && Time.unscaledTime < speaker.SpeakingUntil;

    void Awake()
    {
        _session = MultiplayerBootstrap.Instance != null
            ? MultiplayerBootstrap.Instance.GetComponent<MultiplayerSessionController>()
            : FindAnyObjectByType<MultiplayerSessionController>();
    }

    void OnEnable()
    {
        Active = this;
        VoicePlayerMixSettings.Changed += HandleVoiceMixChanged;
    }

    void OnDisable()
    {
        VoicePlayerMixSettings.Changed -= HandleVoiceMixChanged;
        StopVoice();
        if (Active == this)
            Active = null;
    }

    void Update()
    {
        bool shouldRun = ShouldRun();
        if (shouldRun && !_running)
            StartVoice();
        else if (!shouldRun && _running)
            StopVoice();

        if (!_running)
            return;

        _transmitter.Tick();
        foreach (Speaker speaker in _speakers.Values)
            speaker.Stream?.Tick();

        if (Time.unscaledTime >= _nextPruneTime)
        {
            _nextPruneTime = Time.unscaledTime + PruneIntervalSeconds;
            PruneDepartedSpeakers();
        }
    }

    bool ShouldRun()
    {
        if (_session == null || !_session.IsSessionActive || _session.IsOfflineSession)
            return false;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsConnectedClient)
            return false;
        // An avatar already carries this client's voice (and holds the microphone): stand aside.
        return !VoiceClientRegistry.TryGet(nm.LocalClientId, out _);
    }

    void StartVoice()
    {
        _running = true;
        _nextPruneTime = Time.unscaledTime + PruneIntervalSeconds;

        _transmitter = new VoiceMicTransmitter(InputSystem.actions, "Player", "PushToTalk", this);
        _hasMicrophone = _transmitter.Start();
        if (!_hasMicrophone)
        {
            Debug.LogWarning(
                "LobbyVoiceChat: No usable microphone. You can hear the lobby but nobody can hear you.", this);
        }
    }

    void StopVoice()
    {
        if (!_running)
            return;
        _running = false;
        _hasMicrophone = false;

        _transmitter?.Dispose();
        _transmitter = null;

        foreach (Speaker speaker in _speakers.Values)
            speaker.Stream?.Dispose();
        _speakers.Clear();

        if (_speakerRoot != null)
        {
            Destroy(_speakerRoot);
            _speakerRoot = null;
        }
    }

    /// <summary>One relayed frame from <paramref name="clientId"/> (see <see cref="ProximityVoiceSession"/>).</summary>
    public void EnqueueRemoteFrame(ulong clientId, ushort frameSeq, short[] frame, int count)
    {
        if (!_running || frame == null || count <= 0)
            return;

        if (!_speakers.TryGetValue(clientId, out Speaker speaker))
        {
            if (_speakerRoot == null)
            {
                _speakerRoot = new GameObject("LobbyVoice");
                _speakerRoot.transform.SetParent(transform, false);
            }

            speaker = new Speaker
            {
                Stream = VoicePlaybackStream.CreateFlat(_speakerRoot, "LobbyVoice_" + clientId),
                SpeakingUntil = float.NegativeInfinity,
            };
            speaker.Stream.SetVolume(VoicePlayerMixSettings.GetEffectiveVolume(clientId));
            _speakers[clientId] = speaker;
        }

        float now = Time.unscaledTime;
        speaker.LastFrameTime = now;
        if (ProximityVoiceSession.FrameRms(frame, count) >= ProximityVoiceSession.SpeechRmsThreshold)
            speaker.SpeakingUntil = now + ProximityVoiceSession.SpeechHangoverSeconds;

        speaker.Stream.EnqueueFrame(frameSeq, frame, count);
    }

    void HandleVoiceMixChanged(ulong clientId)
    {
        if (_speakers.TryGetValue(clientId, out Speaker speaker))
            speaker.Stream?.SetVolume(VoicePlayerMixSettings.GetEffectiveVolume(clientId));
    }

    /// <summary>Drops the streams of players who have gone quiet and are no longer on the lobby roster.</summary>
    void PruneDepartedSpeakers()
    {
        if (_speakers.Count == 0)
            return;

        IReadOnlyList<LobbyPlayerState> players = _session != null ? _session.LobbyPlayers : null;
        float now = Time.unscaledTime;
        _pruneScratch.Clear();
        foreach (KeyValuePair<ulong, Speaker> pair in _speakers)
        {
            if (now - pair.Value.LastFrameTime < IdleSpeakerSeconds || IsOnRoster(players, pair.Key))
                continue;
            _pruneScratch.Add(pair.Key);
        }

        for (int i = 0; i < _pruneScratch.Count; i++)
        {
            ulong clientId = _pruneScratch[i];
            _speakers[clientId].Stream?.Dispose();
            _speakers.Remove(clientId);
        }
    }

    static bool IsOnRoster(IReadOnlyList<LobbyPlayerState> players, ulong clientId)
    {
        if (players == null)
            return false;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].ClientId == clientId)
                return true;
        }
        return false;
    }
}
