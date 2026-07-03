using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Drives the carnival radio prop: plays a looping music clip from a spatial (3D) AudioSource routed through the
/// Music bus. The audible reach is a simple adjustable radius (<see cref="maxHearingDistance"/>) thanks to linear
/// rolloff, so tuning "how far away the radio can be heard" is one field in the inspector.
///
/// Players can turn it on/off by aiming at it and pressing E (handled in PlayerController.Carnival). Because the
/// radio is nested in the deterministically-placed carnival room (not Netcode-spawned), the on/off state is shared
/// across all players by <see cref="CarnivalRadioNetworkStore"/>: this component only plays/pauses its local
/// AudioSource, and the store replicates who toggled what (late-join safe). Offline, the toggle is purely local.
///
/// This is purely a world sound source. The Clown has no generic audio-proximity attraction — it only reacts to
/// sprinting players, zombie noise, voice chat, and explicit lures (e.g. the wind-up monkey), so the radio never
/// draws it. Nothing here touches the AI on purpose.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public sealed class CarnivalRadio : MonoBehaviour
{
    [Header("Networking")]
    [Tooltip("Stable id used to replicate this radio's on/off state. Each radio placed in a level needs a UNIQUE id.")]
    [SerializeField] int radioId = 1;

    [Header("Clip")]
    [Tooltip("Looping music clip the radio broadcasts. Defaults to CarnivalMusic on the prefab.")]
    [SerializeField] AudioClip musicClip;

    [Header("Audible range")]
    [Tooltip("Max distance (metres) at which the radio can still be heard. Volume fades linearly from full at " +
             "Min Distance down to silence here. This is the radius you adjust to make it reach further / less far.")]
    [SerializeField, Min(0.1f)] float maxHearingDistance = 12f;

    [Tooltip("Distance (metres) within which the radio plays at full volume before it starts fading out.")]
    [SerializeField, Min(0.01f)] float minDistance = 1.5f;

    [Tooltip("Overall loudness of the radio (0-1), before the Music bus slider.")]
    [SerializeField, Range(0f, 1f)] float volume = 1f;

    [Header("Interaction prompts")]
    [SerializeField] string turnOnPromptMessage = "Press E to turn on the radio";
    [SerializeField] string turnOffPromptMessage = "Press E to turn off the radio";

    // Radios register themselves so the network store can resolve them by id (they aren't Netcode-spawned).
    static readonly Dictionary<int, CarnivalRadio> s_registry = new();

    AudioSource _audio;
    bool _isOn = true;   // radios default to ON / playing

    public int RadioId => radioId;
    public bool IsOn => _isOn;
    public string InteractPromptMessage => _isOn ? turnOffPromptMessage : turnOnPromptMessage;

    public static bool TryResolve(int id, out CarnivalRadio radio) =>
        s_registry.TryGetValue(id, out radio) && radio != null;

    void Awake()
    {
        _audio = GetComponent<AudioSource>();
        ConfigureSource();
    }

    void OnEnable()
    {
        // Register early (before the store spawns) so the store's OnNetworkSpawn can push any replicated state onto us.
        s_registry[radioId] = this;
    }

    void OnDisable()
    {
        if (s_registry.TryGetValue(radioId, out CarnivalRadio current) && current == this)
            s_registry.Remove(radioId);
    }

    void Start()
    {
        // Route once the GameAudioManager singleton is available (it initialises at DefaultExecutionOrder -20).
        GameAudioManager.RouteMusicSource(_audio);

        // If we built after the store already synced (e.g. late joiner), adopt the current replicated on/off state.
        CarnivalRadioNetworkStore.ApplyCurrentStateToRadio(this);

        // Make the AudioSource match our state (starts playback when on).
        SetOn(_isOn);
    }

    /// <summary>Player pressed E while aiming at the radio. Routes through the network store when online.</summary>
    public void RequestToggle()
    {
        NetworkManager nm = NetworkManager.Singleton;
        bool online = nm != null && nm.IsListening && CarnivalRadioNetworkStore.Instance != null;

        if (online)
            CarnivalRadioNetworkStore.RequestToggle(radioId);
        else
            SetOn(!_isOn);   // offline / no store: toggle the local source directly
    }

    /// <summary>Turn the local AudioSource on (resume/play) or off (pause). Called locally and by the network store.</summary>
    public void SetOn(bool on)
    {
        _isOn = on;
        if (_audio == null)
            return;

        if (on)
        {
            _audio.UnPause();               // resume if paused
            if (!_audio.isPlaying && _audio.clip != null)
                _audio.Play();              // start from scratch if it was never playing
        }
        else
        {
            _audio.Pause();                 // keep position so it resumes where it left off
        }
    }

    void ConfigureSource()
    {
        if (_audio == null)
            return;

        if (musicClip != null)
            _audio.clip = musicClip;

        _audio.loop = true;
        _audio.playOnAwake = false;          // we start it in Start() after routing the mixer group
        _audio.spatialBlend = 1f;            // fully 3D / positional
        _audio.rolloffMode = AudioRolloffMode.Linear;
        _audio.dopplerLevel = 0f;
        _audio.minDistance = Mathf.Min(minDistance, maxHearingDistance);
        _audio.maxDistance = maxHearingDistance;
        _audio.volume = volume;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // Push inspector tweaks onto an existing AudioSource live. Do NOT AddComponent here — that's disallowed on
        // prefab assets. RequireComponent guarantees the source exists at runtime; in-editor it may be null until then.
        if (_audio == null)
            _audio = GetComponent<AudioSource>();
        if (_audio == null)
            return;

        if (minDistance > maxHearingDistance)
            minDistance = maxHearingDistance;

        if (musicClip != null)
            _audio.clip = musicClip;
        _audio.loop = true;
        _audio.spatialBlend = 1f;
        _audio.rolloffMode = AudioRolloffMode.Linear;
        _audio.dopplerLevel = 0f;
        _audio.minDistance = minDistance;
        _audio.maxDistance = maxHearingDistance;
        _audio.volume = volume;
    }

    void OnDrawGizmosSelected()
    {
        // Inner solid-ish sphere = full volume; outer wire sphere = the edge of audibility (maxHearingDistance).
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.25f);
        Gizmos.DrawWireSphere(transform.position, minDistance);
        Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, maxHearingDistance);
    }
#endif
}
