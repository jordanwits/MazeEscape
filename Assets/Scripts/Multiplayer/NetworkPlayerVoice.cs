using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Proximity voice on a player avatar. The owner sends its microphone through a <see cref="VoiceMicTransmitter"/>;
/// every other peer plays that player's relayed frames from their head through a <see cref="VoicePlaybackStream"/>.
/// The menu lobby has no avatars, so it carries voice separately (<see cref="LobbyVoiceChat"/>) over the same wire.
/// </summary>
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
    [Tooltip("Where a voice has faded to silence (see GameAudioManager.FadingLogRolloff); full loudness law up to half of this.")]
    [SerializeField, Min(0.5f)] float maxHearingDistance = 30f;

    [Header("Input (push-to-talk; use same Input Actions asset as PlayerController)")]
    [SerializeField] InputActionAsset inputActions;
    [SerializeField] string playerMapName = "Player";
    [SerializeField] string pushToTalkActionName = "PushToTalk";

    NetworkObject _networkObject;
    VoiceMicTransmitter _transmitter;

    VoicePlaybackStream _remoteOutput;
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
            _transmitter = new VoiceMicTransmitter(inputActions, playerMapName, pushToTalkActionName, this);
            if (!_transmitter.Start())
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
            _remoteOutput = new VoicePlaybackStream(anchor.gameObject, minHearingDistance, maxHearingDistance);
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

        _transmitter?.Dispose();
        _transmitter = null;
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
        _transmitter?.Tick();
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

    public void EnqueuePcm16Frame(ushort frameSeq, short[] frame, int count)
    {
        _remoteOutput?.EnqueueFrame(frameSeq, frame, count);
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
}
