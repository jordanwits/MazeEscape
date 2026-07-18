using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a string of point lights (a rope light) with an irregular, "bad power connection"
/// flicker: mostly lit, with random dropouts and the occasional rapid stutter burst before it
/// recovers. The pattern is deliberately non-rhythmic so it reads as a faulty electrical
/// connection rather than an animated effect.
///
/// Purely cosmetic, so it runs locally on every client (no networking) — the flicker does not
/// need to be identical across machines. Attach to the RopeLight root; by default it gathers the
/// Light components on this object and its children and flickers them together as one fixture.
/// </summary>
[DisallowMultipleComponent]
public class RopeLightFlicker : MonoBehaviour
{
    [Header("Lights")]
    [Tooltip("Include Light components found on child objects (the rope's individual bulbs). Leave on for a normal rope light.")]
    [SerializeField] bool includeChildLights = true;
    [Tooltip("Optional explicit list. When non-empty, only these lights flicker and auto-gathering is skipped.")]
    [SerializeField] List<Light> lightsOverride = new List<Light>();

    [Header("Lit / Out levels")]
    [Tooltip("Intensity multiplier applied to each light's original intensity while lit (1 = normal).")]
    [SerializeField, Range(0f, 1f)] float onLevel = 1f;
    [Tooltip("Intensity multiplier while the connection has dropped out. 0 = fully off. A small value (e.g. 0.05) gives a dim brown-out instead of a hard cut.")]
    [SerializeField, Range(0f, 1f)] float offLevel;

    [Header("Timing (seconds)")]
    [Tooltip("How long the rope stays steadily lit between glitches. Picked randomly each cycle for a non-rhythmic feel.")]
    [SerializeField] float minOnTime = 0.12f;
    [SerializeField] float maxOnTime = 2.2f;
    [Tooltip("How long a single dropout lasts when the connection cuts out.")]
    [SerializeField] float minOffTime = 0.04f;
    [SerializeField] float maxOffTime = 0.3f;

    [Header("Stutter bursts")]
    [Tooltip("Chance (0-1) that a glitch is a rapid multi-blink stutter instead of a single clean dropout.")]
    [SerializeField, Range(0f, 1f)] float stutterChance = 0.4f;
    [Tooltip("Maximum number of rapid on/off blinks in a stutter burst (a random count from 2 up to this is used).")]
    [SerializeField] int maxStutterBlinks = 5;
    [Tooltip("Duration range for each individual blink within a stutter burst.")]
    [SerializeField] float minStutterBlink = 0.02f;
    [SerializeField] float maxStutterBlink = 0.09f;

    [Header("Audio (buzz synced to the flicker)")]
    [Tooltip("Looping electrical buzz for this rope light. It is muted the instant the bulbs drop out and unmuted " +
             "when they come back, so the sound flickers in lock-step with the lights. Leave empty for a silent rope light.")]
    [SerializeField] AudioClip flickerLoopClip;
    [Tooltip("Overall loudness of the buzz (0-1), before the SFX bus slider.")]
    [SerializeField, Range(0f, 1f)] float audioVolume = 0.175f;
    [Tooltip("Distance (metres) within which the buzz plays at full volume before it fades with distance.")]
    [SerializeField, Min(0.01f)] float audioMinDistance = 1.5f;
    [Tooltip("Max distance (metres) at which the buzz can still be heard (linear rolloff to silence here).")]
    [SerializeField, Min(0.1f)] float audioMaxDistance = 8f;

    readonly List<Light> _lights = new List<Light>();
    readonly List<float> _baseIntensities = new List<float>();
    Coroutine _routine;
    AudioSource _audio;
    bool _audioLit = true;   // tracks the last flicker state so re-enables resync the mute
    bool _started;           // Start() has run: mixer routing done, safe to (re)start playback

    void Awake()
    {
        GatherLights();
        SetupAudio();
    }

    void Start()
    {
        _started = true;
        if (_audio == null || _lights.Count == 0)
            return;

        // Route through the SFX bus (also opts the source into wall-occlusion). Done in Start because the
        // GameAudioManager singleton initialises at DefaultExecutionOrder -20, before this default-order Start.
        GameAudioManager.RouteSfxSource(_audio);
        if (isActiveAndEnabled)
            StartAudio();
    }

    void OnEnable()
    {
        if (_lights.Count == 0)
            return;
        _routine = StartCoroutine(FlickerLoop());
        // First playback is kicked off by Start() (after mixer routing); this resumes the buzz on later re-enables.
        if (_started)
            StartAudio();
    }

    void OnDisable()
    {
        if (_routine != null)
        {
            StopCoroutine(_routine);
            _routine = null;
        }
        // Leave the rope in its normal lit state when the effect is switched off.
        SetLevel(onLevel);
        StopAudio();
    }

    void GatherLights()
    {
        _lights.Clear();
        _baseIntensities.Clear();

        if (lightsOverride != null && lightsOverride.Count > 0)
        {
            foreach (Light l in lightsOverride)
                if (l != null)
                    _lights.Add(l);
        }
        else if (includeChildLights)
        {
            // includeInactive: false — only lights that start enabled participate.
            GetComponentsInChildren(false, _lights);
        }
        else
        {
            Light self = GetComponent<Light>();
            if (self != null)
                _lights.Add(self);
        }

        foreach (Light l in _lights)
            _baseIntensities.Add(l.intensity);
    }

    IEnumerator FlickerLoop()
    {
        // Random initial offset so multiple rope lights in a level don't glitch in lock-step.
        yield return new WaitForSeconds(Random.Range(0f, maxOnTime));

        while (true)
        {
            // Steady, lit stretch.
            SetLevel(onLevel);
            yield return new WaitForSeconds(Random.Range(minOnTime, maxOnTime));

            // A glitch: either a single dropout or a rapid stutter burst.
            if (Random.value < stutterChance)
            {
                int blinks = Random.Range(2, Mathf.Max(3, maxStutterBlinks + 1));
                for (int i = 0; i < blinks; i++)
                {
                    SetLevel(offLevel);
                    yield return new WaitForSeconds(Random.Range(minStutterBlink, maxStutterBlink));
                    SetLevel(onLevel);
                    yield return new WaitForSeconds(Random.Range(minStutterBlink, maxStutterBlink));
                }
            }
            else
            {
                SetLevel(offLevel);
                yield return new WaitForSeconds(Random.Range(minOffTime, maxOffTime));
            }
        }
    }

    void SetLevel(float level)
    {
        bool lit = level > 0f;
        for (int i = 0; i < _lights.Count; i++)
        {
            Light l = _lights[i];
            if (l == null)
                continue;
            l.intensity = _baseIntensities[i] * level;
            // Fully disabling a zero-intensity light lets the renderer skip it entirely.
            l.enabled = lit;
        }

        // Drive the buzz off the exact same on/off decision as the bulbs, so the sound cuts and stutters
        // in perfect sync. Muting (vs. Stop) keeps the loop phase running for a seamless, click-free cut.
        _audioLit = lit;
        if (_audio != null)
            _audio.mute = !lit;
    }

    void SetupAudio()
    {
        if (flickerLoopClip == null)
            return;   // no clip assigned → light-only rope light, no AudioSource created

        _audio = GetComponent<AudioSource>();
        if (_audio == null)
            _audio = gameObject.AddComponent<AudioSource>();

        _audio.clip = flickerLoopClip;
        _audio.loop = true;
        _audio.playOnAwake = false;                    // started from Start()/OnEnable after routing
        _audio.spatialBlend = 1f;                      // fully 3D so the buzz sits on the rope light
        _audio.rolloffMode = AudioRolloffMode.Linear;  // simple adjustable audible radius
        _audio.dopplerLevel = 0f;
        _audio.minDistance = Mathf.Min(audioMinDistance, audioMaxDistance);
        _audio.maxDistance = audioMaxDistance;
        _audio.volume = audioVolume;
        _audio.mute = false;                           // rope starts lit
    }

    void StartAudio()
    {
        if (_audio == null)
            return;
        _audio.mute = !_audioLit;   // resync to the current flicker state
        if (!_audio.isPlaying)
            _audio.Play();
    }

    void StopAudio()
    {
        if (_audio != null)
            _audio.Stop();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // Push inspector tweaks onto an existing source live. Never AddComponent here — disallowed on prefab assets;
        // the runtime source is created in SetupAudio(). In edit mode _audio is usually null until play.
        if (audioMinDistance > audioMaxDistance)
            audioMinDistance = audioMaxDistance;

        if (_audio == null)
            return;

        _audio.clip = flickerLoopClip;
        _audio.loop = true;
        _audio.spatialBlend = 1f;
        _audio.rolloffMode = AudioRolloffMode.Linear;
        _audio.dopplerLevel = 0f;
        _audio.minDistance = audioMinDistance;
        _audio.maxDistance = audioMaxDistance;
        _audio.volume = audioVolume;
    }
#endif
}
