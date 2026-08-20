using UnityEngine;

/// <summary>
/// The track playing in the Severance disco room, and the beat clock everything in the room runs off.
///
/// The clock is read from <see cref="AudioSource.time"/> rather than from <see cref="Time.time"/>, so the
/// floor and the wall washes step on the actual song rather than on a free-running timer that happens to
/// share its tempo. That also means they stay in step after a pause, after the distance gate stops and
/// restarts playback, and regardless of when the room was built.
///
/// A 3D source on the Music bus, same idiom as <see cref="CarnivalRadio"/>: linear rolloff, so "how far
/// away can you hear it" is one field. Routing through <see cref="GameAudioManager.RouteMusicSource"/> is
/// also what opts it into wall occlusion, so the track muffles through the room's walls as you approach.
///
/// Client-visual/audio only — nothing here is networked. Each peer builds the maze locally and plays its
/// own copy, so two players can be at different points in the track; everything that reads the beat reads
/// it from the source that peer can actually hear, which is the only thing that has to agree.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public sealed class DiscoMusic : MonoBehaviour
{
    [Header("Track")]
    [Tooltip("Looping track for the room.")]
    [SerializeField] AudioClip track;

    [Tooltip("Tempo of the track. Everything that syncs to the music derives its beat from this, so a wrong " +
             "value shows up as the floor drifting out of step over a few bars.")]
    [SerializeField, Min(20f)] float beatsPerMinute = 125f;

    [Tooltip("Seconds into the clip where the first downbeat lands. Raise this if the track opens with a " +
             "lead-in and the lights feel consistently early.")]
    [SerializeField] float beatOffsetSeconds = 0f;

    [Tooltip("Beats per bar. Effects that change once a bar (the wall-wash colour rotation) use this.")]
    [SerializeField, Min(1)] int beatsPerBar = 4;

    [Header("Audible range")]
    [Tooltip("Metres at which the track fades to silence. Volume falls linearly from Min Distance to here, " +
             "so this is the radius to adjust. Set it past the room's walls and you hear it (muffled) from " +
             "the corridor on the way in.")]
    [SerializeField, Min(0.1f)] float maxHearingDistance = 30f;

    [Tooltip("Metres within which the track plays at full volume before it starts fading.")]
    [SerializeField, Min(0.01f)] float minDistance = 5f;

    [Tooltip("Loudness before the Music bus slider.")]
    [SerializeField, Range(0f, 1f)] float volume = 0.8f;

    [Header("Distance gate")]
    [Tooltip("Beyond this many metres from the local camera the track pauses. It resumes where it left off, " +
             "so the beat clock stays meaningful. Keep it comfortably past Max Hearing Distance.")]
    [SerializeField, Min(5f)] float activeDistance = 40f;

    AudioSource _audio;
    Camera _viewCamera;
    bool _playing;

    /// <summary>Tempo the room is running at.</summary>
    public float BeatsPerMinute => Mathf.Max(20f, beatsPerMinute);

    /// <summary>Beats in a bar.</summary>
    public int BeatsPerBar => Mathf.Max(1, beatsPerBar);

    /// <summary>
    /// Position in the track measured in beats since the first downbeat, fractional. False when the track
    /// is not audible (no clip, or paused by the distance gate) — callers should fall back to their own
    /// clock rather than freezing.
    /// </summary>
    public bool TryGetBeat(out float beat)
    {
        beat = 0f;
        if (_audio == null || _audio.clip == null || !_audio.isPlaying)
            return false;

        beat = (_audio.time - beatOffsetSeconds) * (BeatsPerMinute / 60f);
        return true;
    }

    /// <summary>
    /// 1 on the beat, decaying to 0 before the next one — the envelope light effects pulse with.
    /// <paramref name="decay"/> is how many beats the tail lasts; 0 when the track is not playing.
    /// </summary>
    public float BeatEnvelope(float decay = 0.55f)
    {
        if (!TryGetBeat(out float beat))
            return 0f;

        float intoBeat = beat - Mathf.Floor(beat);
        return Mathf.Exp(-intoBeat / Mathf.Max(0.01f, decay));
    }

    /// <summary>Index of the current bar, for effects that change once a bar. 0 when not playing.</summary>
    public int Bar => TryGetBeat(out float beat) ? Mathf.FloorToInt(beat / BeatsPerBar) : 0;

    void Awake()
    {
        _audio = GetComponent<AudioSource>();
        ConfigureSource();
    }

    void Start()
    {
        // Route once the GameAudioManager singleton exists (it initialises at DefaultExecutionOrder -20).
        GameAudioManager.RouteMusicSource(_audio);
    }

    void Update()
    {
        bool near = IsNearLocalView();
        if (near == _playing)
            return;

        _playing = near;
        if (_audio == null || _audio.clip == null)
            return;

        if (near)
        {
            _audio.UnPause();
            if (!_audio.isPlaying)
                _audio.Play();
        }
        else
        {
            _audio.Pause();   // keeps the position, so the beat clock resumes where it left off
        }
    }

    void ConfigureSource()
    {
        if (_audio == null)
            return;

        if (track != null)
            _audio.clip = track;

        _audio.loop = true;
        _audio.playOnAwake = false;      // the distance gate starts it
        _audio.spatialBlend = 1f;        // fully positional
        _audio.rolloffMode = AudioRolloffMode.Linear;
        _audio.dopplerLevel = 0f;
        _audio.minDistance = Mathf.Min(minDistance, maxHearingDistance);
        _audio.maxDistance = maxHearingDistance;
        _audio.volume = volume;
    }

    bool IsNearLocalView()
    {
        Camera cam = ResolveViewpoint();
        if (cam == null)
            return false; // headless server, or no local view yet.
        return (cam.transform.position - transform.position).sqrMagnitude <= activeDistance * activeDistance;
    }

    // Camera.main is null in gameplay (PlayerView is deliberately Untagged) — same fallback the render
    // cullers use.
    Camera ResolveViewpoint()
    {
        if (_viewCamera != null && _viewCamera.isActiveAndEnabled && _viewCamera.gameObject.activeInHierarchy)
            return _viewCamera;

        Camera cam = Camera.main;
        if (cam == null)
        {
            Camera[] cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] != null && cams[i].isActiveAndEnabled && cams[i].cameraType == CameraType.Game)
                {
                    cam = cams[i];
                    break;
                }
            }
        }

        _viewCamera = cam;
        return _viewCamera;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // Push inspector tweaks onto an existing source. Do NOT AddComponent here — disallowed on prefab
        // assets; RequireComponent guarantees it exists at runtime.
        if (_audio == null)
            _audio = GetComponent<AudioSource>();
        if (_audio == null)
            return;

        if (minDistance > maxHearingDistance)
            minDistance = maxHearingDistance;

        ConfigureSource();
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.3f, 0.7f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, minDistance);
        Gizmos.color = new Color(0.6f, 0.3f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, maxHearingDistance);
    }
#endif
}
