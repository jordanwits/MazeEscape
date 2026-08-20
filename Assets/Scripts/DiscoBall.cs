using UnityEngine;

/// <summary>
/// Drives the mirror ball hanging in the Severance disco room.
///
/// The moving dots on the walls are real lighting, not a texture: a point light sits inside the ball
/// carrying a cubemap <see cref="Light.cookie"/> of bright specks (DiscoBallCookie.cubemap), so URP
/// projects that pattern out in every direction and occludes it against the room like any other light.
/// Spinning the light with the ball is what sweeps the dots around. One light does the whole 360°,
/// which is why this is a point-plus-cubemap rather than a ring of cookied spots.
///
/// The ball itself is a faceted mirror: pin spots aimed at it give the individual facets their glints,
/// and a reflection probe holding a cubemap of the room (traced offline by the build pass, not
/// rendered at runtime) is what puts the colour of the dance floor into the mirrors.
///
/// It also owns the room's coloured wall washes: they are listed here rather than left to themselves so
/// one component holds the room's distance gate, and so they can kick on the beat of
/// <see cref="DiscoMusic"/> and rotate colours each bar.
///
/// Client-visual only. Nothing here is networked — the maze is built locally by every peer.
/// </summary>
[DisallowMultipleComponent]
public class DiscoBall : MonoBehaviour
{
    [Header("Rig")]
    [Tooltip("Pivot holding the ball mesh and the dot projector. Spun about its own Y axis.")]
    [SerializeField] Transform spin;

    [Tooltip("Point light inside the ball carrying the dot cookie. Spins with the ball.")]
    [SerializeField] Light dotProjector;

    [Tooltip("Pin spots aimed at the ball from different sides. These are what make it sparkle: from a " +
             "player's eye the mirrors reflect the dark far wall, not the lit floor, so the ball reads " +
             "almost entirely through glints. Three of them means some facets flash from any viewpoint. " +
             "They do not spin.")]
    [SerializeField] Light[] keyLights;

    [Tooltip("Any other lights in the room that should follow the same distance gate — the coloured " +
             "wall washes, typically. They are excluded from MazeLightCuller so that turning your back " +
             "on them can't switch the room's colour off; this is what pays for that instead.")]
    [SerializeField] Light[] extraLights;

    [Header("Music sync")]
    [Tooltip("Track the room is running to. When set, the wall washes kick on every beat and rotate their " +
             "colours once a bar. Leave empty and they just burn steadily.")]
    [SerializeField] DiscoMusic music;

    [Tooltip("How hard the wall washes kick on each beat, as a fraction of their authored intensity.")]
    [SerializeField, Range(0f, 2f)] float washBeatPulse = 0.8f;

    [Tooltip("Rotate the wash colours between fixtures once per bar, so the room changes colour with the " +
             "music instead of sitting on one palette.")]
    [SerializeField] bool rotateWashColoursPerBar = true;

    [Header("Motion")]
    [Tooltip("Degrees per second. A real ball motor is about one turn a minute, so 6-10 reads right; " +
             "faster starts to look like a searchlight.")]
    [SerializeField] float spinDegreesPerSecond = 7f;

    [Header("Distance gate")]
    [Tooltip("Beyond this many metres from the local camera the ball stops spinning and its lights " +
             "switch off. Sized to cover the room and its doorways.")]
    [SerializeField, Min(5f)] float activeDistance = 34f;

    Camera _viewCamera;
    bool _active = true;

    // Authored wash levels, captured before the pulse ever writes to them — re-reading intensity/colour
    // later would capture our own modulation as the baseline and let it drift.
    float[] _washBaseIntensity;
    Color[] _washBaseColour;
    int _lastBar = int.MinValue;

    void Awake()
    {
        if (spin == null)
            spin = transform;

        if (extraLights != null)
        {
            _washBaseIntensity = new float[extraLights.Length];
            _washBaseColour = new Color[extraLights.Length];
            for (int i = 0; i < extraLights.Length; i++)
            {
                if (extraLights[i] == null)
                    continue;
                _washBaseIntensity[i] = extraLights[i].intensity;
                _washBaseColour[i] = extraLights[i].color;
            }
        }
    }

    void Update()
    {
        bool near = IsNearLocalView();
        if (near != _active)
        {
            _active = near;
            SetLight(dotProjector, near);
            if (keyLights != null)
                for (int i = 0; i < keyLights.Length; i++)
                    SetLight(keyLights[i], near);
            if (extraLights != null)
                for (int i = 0; i < extraLights.Length; i++)
                    SetLight(extraLights[i], near);
        }

        if (!near)
            return;

        if (spin != null && Mathf.Abs(spinDegreesPerSecond) > 0.001f)
            spin.Rotate(0f, spinDegreesPerSecond * Time.deltaTime, 0f, Space.Self);

        DriveWashesToMusic();
    }

    /// <summary>
    /// Kick the wall washes on every beat and cycle which fixture wears which colour each bar. Both read
    /// the song's own playback position through <see cref="DiscoMusic"/>, so they land on the track rather
    /// than merely at its tempo.
    /// </summary>
    void DriveWashesToMusic()
    {
        if (music == null || extraLights == null || _washBaseIntensity == null)
            return;

        float envelope = music.BeatEnvelope();
        int count = extraLights.Length;

        if (rotateWashColoursPerBar && count > 1)
        {
            int bar = music.Bar;
            if (bar != _lastBar)
            {
                _lastBar = bar;
                for (int i = 0; i < count; i++)
                {
                    if (extraLights[i] == null)
                        continue;
                    int from = ((i + bar) % count + count) % count;
                    extraLights[i].color = _washBaseColour[from];
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (extraLights[i] == null)
                continue;
            extraLights[i].intensity = _washBaseIntensity[i] * (1f + washBeatPulse * envelope);
        }
    }

    static void SetLight(Light l, bool on)
    {
        if (l != null && l.enabled != on)
            l.enabled = on;
    }

    bool IsNearLocalView()
    {
        Camera cam = ResolveViewpoint();
        if (cam == null)
            return false; // headless server, or no local view yet.
        return (cam.transform.position - transform.position).sqrMagnitude <= activeDistance * activeDistance;
    }

    // Camera.main is null in gameplay (PlayerView is deliberately Untagged) — same fallback the
    // render cullers use.
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
}
