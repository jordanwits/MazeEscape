using UnityEngine;

/// <summary>
/// The lit dance-floor pad in the Severance disco room. The renderer this sits on is a single quad
/// laid flat; every panel, grout line and scuff is drawn procedurally by <c>Severance/DiscoFloor</c>.
/// All this component does is rewrite a tiny one-texel-per-tile texture on the beat and push it to
/// the material through a <see cref="MaterialPropertyBlock"/>, so the whole floor costs one draw call
/// and a 16x16 texture upload no matter how many tiles it has.
///
/// The panels are unlit — they are meant to read as light sources, not as lit surfaces — so a handful
/// of real point lights are created above the pad and tinted to the average colour underneath them.
/// That is what actually spills colour onto the walls and up onto the dark ceiling.
///
/// Purely cosmetic and entirely local: the maze is never network-spawned (every peer builds it from a
/// shared seed), and nothing here touches gameplay, so the pattern runs off <see cref="Time.time"/>
/// and is allowed to sit out of phase between peers.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshRenderer))]
public class DiscoFloor : MonoBehaviour
{
    /// <summary>How a tile picks its colour for the current step.</summary>
    public enum Pattern
    {
        /// <summary>Every tile rolls independently — the mostly-random look of a real floor.</summary>
        Scatter,
        /// <summary>Coloured stripes marching along the pad.</summary>
        Rows,
        /// <summary>Stripes running corner to corner.</summary>
        Diagonal,
        /// <summary>Concentric rings pushing out from the centre, under the ball.</summary>
        Rings,
        /// <summary>Two colours on a chequerboard, swapping each step.</summary>
        Checker,
        /// <summary>One bright band sweeping across a dim floor.</summary>
        Sweep,
    }

    [Header("Grid")]
    [Tooltip("Tiles along each side of the pad. The pad's world size comes from this object's scale, " +
             "so 16 tiles on a 12m quad gives 75cm panels.")]
    [SerializeField, Range(2, 64)] int tilesPerSide = 16;

    [Tooltip("Share of panels (per mille) that sit dead and grey, like a burnt-out cell. Chosen from a " +
             "per-tile hash, so a given panel is the broken one for the whole run.")]
    [SerializeField, Range(0, 300)] int deadTilesPerMille = 70;

    [Tooltip("How bright a dead panel still is, as a fraction of a live one.")]
    [SerializeField, Range(0f, 1f)] float deadTileBrightness = 0.16f;

    [Header("Palette")]
    [Tooltip("Colours the floor picks from. Keep them saturated — the shader's intensity does the work.")]
    [SerializeField]
    Color[] palette =
    {
        new Color(1.00f, 0.10f, 0.16f), // red
        new Color(1.00f, 0.15f, 0.62f), // magenta
        new Color(0.68f, 0.20f, 1.00f), // violet
        new Color(0.22f, 0.34f, 1.00f), // blue
        new Color(0.15f, 0.85f, 1.00f), // cyan
        new Color(0.25f, 1.00f, 0.35f), // green
        new Color(0.85f, 1.00f, 0.20f), // lime
        new Color(1.00f, 0.55f, 0.10f), // orange
        new Color(0.95f, 0.95f, 1.00f), // white
    };

    [Header("Timing")]
    [Tooltip("Track the floor steps to. When set, the beat comes from the song's actual playback position, " +
             "so the panels change on the beat instead of merely at the same tempo. Leave empty for a silent " +
             "floor running on its own clock.")]
    [SerializeField] DiscoMusic music;

    [Tooltip("Fallback tempo, used only when no track is assigned (or while it is paused by its distance " +
             "gate). With a track assigned, its BPM is what governs.")]
    [SerializeField, Min(20f)] float beatsPerMinute = 125f;

    [Tooltip("Beats between colour changes. 1 = every beat, 2 = every other.")]
    [SerializeField, Min(0.25f)] float beatsPerStep = 1f;

    [Tooltip("Beats a pattern runs for before the sequence advances.")]
    [SerializeField, Min(1f)] float beatsPerPattern = 32f;

    [Tooltip("Order the patterns cycle in. Scatter is repeated on purpose — it is the look the room " +
             "sits at most of the time, with the marching patterns as punctuation.")]
    [SerializeField]
    Pattern[] patternSequence =
    {
        Pattern.Scatter, Pattern.Rows, Pattern.Scatter, Pattern.Rings,
        Pattern.Scatter, Pattern.Diagonal, Pattern.Sweep, Pattern.Checker,
    };

    [Tooltip("How fast a panel crossfades to its new colour. High = snaps on the beat, low = smears.")]
    [SerializeField, Min(0.5f)] float fadeSpeed = 14f;

    [Tooltip("Seconds between texture rewrites. The upload is 256 texels, so this is about smoothness, " +
             "not cost.")]
    [SerializeField, Min(0.01f)] float updateInterval = 0.05f;

    [Header("Colour spill")]
    [Tooltip("Real point lights created above the pad, tinted to the floor under them, so the colour " +
             "reaches the walls and ceiling. One per quadrant.")]
    [SerializeField] bool spillLights = true;
    [SerializeField, Min(0f)] float spillIntensity = 7f;
    [Tooltip("Metres above the pad the spill lights sit.")]
    [SerializeField, Min(0.1f)] float spillHeight = 1.9f;
    [Tooltip("Range of each spill light — roughly half the pad plus the walk out to the wall.")]
    [SerializeField, Min(1f)] float spillRange = 13f;

    [Tooltip("How hard the spill lights kick on each beat, as a fraction of their steady level. Needs a " +
             "track assigned above; 0 disables the pulse.")]
    [SerializeField, Range(0f, 1.5f)] float spillBeatPulse = 0.45f;

    [Header("Distance gate")]
    [Tooltip("Beyond this many metres from the local camera the floor stops animating and its spill " +
             "lights switch off. The panels keep their last colours; the render culler hides the whole " +
             "room long before this matters.")]
    [SerializeField, Min(5f)] float activeDistance = 34f;

    static readonly int ColorTexId = Shader.PropertyToID("_ColorTex");
    static readonly int TileCountId = Shader.PropertyToID("_TileCount");

    Renderer _renderer;
    MaterialPropertyBlock _mpb;
    Texture2D _tex;
    Color[] _current;
    Color32[] _pixels;
    Light[] _spill;
    Camera _viewCamera;

    float _nextUpdate;
    float _lastUpdateTime;
    bool _ready;

    /// <summary>Mean colour of the whole pad right now. Handy for anything wanting to match it.</summary>
    public Color AverageColour { get; private set; } = Color.black;

    void Awake()
    {
        _renderer = GetComponent<MeshRenderer>();
        _mpb = new MaterialPropertyBlock();

        int n = Mathf.Max(2, tilesPerSide);
        _tex = new Texture2D(n, n, TextureFormat.RGBA32, false, true)
        {
            name = "DiscoFloorTiles",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontSave,
        };

        _current = new Color[n * n];
        _pixels = new Color32[n * n];

        // Start already lit, rather than fading up from black on the first frame the room is entered.
        float beat = Beat();
        int step = Mathf.FloorToInt(beat / Mathf.Max(0.25f, beatsPerStep));
        Pattern pattern = PatternForBeat(beat);
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                _current[z * n + x] = TargetColour(x, z, n, step, pattern);

        WritePixels(n);
        Upload(n);
        _lastUpdateTime = Time.time;
        _ready = true;
    }

    void Start()
    {
        if (spillLights)
            BuildSpillLights();
    }

    void OnDestroy()
    {
        if (_tex != null)
            Destroy(_tex);
    }

    void Update()
    {
        if (!_ready || Time.time < _nextUpdate)
            return;

        _nextUpdate = Time.time + updateInterval;

        bool near = IsNearLocalView();
        SetSpillEnabled(near);
        if (!near)
        {
            _lastUpdateTime = Time.time;
            return;
        }

        int n = Mathf.Max(2, tilesPerSide);
        float dt = Mathf.Max(0f, Time.time - _lastUpdateTime);
        _lastUpdateTime = Time.time;

        float beat = Beat();
        int step = Mathf.FloorToInt(beat / Mathf.Max(0.25f, beatsPerStep));
        Pattern pattern = PatternForBeat(beat);

        float k = 1f - Mathf.Exp(-fadeSpeed * dt);
        Vector3 sum = Vector3.zero;

        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                Color target = TargetColour(x, z, n, step, pattern);
                Color c = Color.LerpUnclamped(_current[i], target, k);
                _current[i] = c;
                sum += new Vector3(c.r, c.g, c.b);
            }
        }

        float inv = 1f / (n * n);
        AverageColour = new Color(sum.x * inv, sum.y * inv, sum.z * inv, 1f);

        WritePixels(n);
        Upload(n);
        UpdateSpillLights(n, music != null && spillBeatPulse > 0f ? music.BeatEnvelope() : 0f);
    }

    void WritePixels(int n)
    {
        for (int i = 0; i < _current.Length; i++)
        {
            Color c = _current[i];
            _pixels[i] = new Color32(
                (byte)(Mathf.Clamp01(c.r) * 255f),
                (byte)(Mathf.Clamp01(c.g) * 255f),
                (byte)(Mathf.Clamp01(c.b) * 255f),
                255);
        }
    }

    void Upload(int n)
    {
        _tex.SetPixels32(_pixels);
        _tex.Apply(false);

        _renderer.GetPropertyBlock(_mpb);
        _mpb.SetTexture(ColorTexId, _tex);
        _mpb.SetFloat(TileCountId, n);
        _renderer.SetPropertyBlock(_mpb);
    }

    // The song is the clock whenever it is audible; the free-running fallback keeps the floor alive when
    // the track is missing or the distance gate has paused it, rather than freezing mid-pattern.
    float Beat()
    {
        if (music != null && music.TryGetBeat(out float songBeat))
            return songBeat;

        return Time.time * (Mathf.Max(20f, beatsPerMinute) / 60f);
    }

    Pattern PatternForBeat(float beat)
    {
        if (patternSequence == null || patternSequence.Length == 0)
            return Pattern.Scatter;

        int idx = Mathf.FloorToInt(beat / Mathf.Max(1f, beatsPerPattern));
        return patternSequence[Mod(idx, patternSequence.Length)];
    }

    Color TargetColour(int x, int z, int n, int step, Pattern pattern)
    {
        if (palette == null || palette.Length == 0)
            return Color.white;

        int count = palette.Length;
        Color c;

        switch (pattern)
        {
            case Pattern.Rows:
                c = palette[Mod(x + step, count)];
                break;

            case Pattern.Diagonal:
                c = palette[Mod(x + z + step, count)];
                break;

            case Pattern.Rings:
            {
                float half = (n - 1) * 0.5f;
                float d = Vector2.Distance(new Vector2(x, z), new Vector2(half, half));
                c = palette[Mod(Mathf.RoundToInt(d) - step, count)];
                break;
            }

            case Pattern.Checker:
            {
                bool even = ((x + z + step) & 1) == 0;
                c = palette[Mod(even ? step : step + count / 2, count)];
                break;
            }

            case Pattern.Sweep:
            {
                // A band walking the pad, everything else dimmed to a wash of the same colour.
                int band = Mod(step, n + 4) - 2;
                int d = Mathf.Abs(x - band);
                Color hot = palette[Mod(step / n, count)];
                c = d == 0 ? hot : (d <= 2 ? hot * 0.35f : hot * 0.07f);
                break;
            }

            default: // Scatter
                c = palette[Hash(x, z, step) % count];
                break;
        }

        // Burnt-out panels are picked from position alone, so the same cells stay dead all run.
        if (deadTilesPerMille > 0 && Hash(x, z, 9173) % 1000 < deadTilesPerMille)
            c *= deadTileBrightness;

        c.a = 1f;
        return c;
    }

    static int Mod(int v, int m)
    {
        int r = v % m;
        return r < 0 ? r + m : r;
    }

    // Small avalanche hash. The tiles sit on a regular grid, so a weak mix would lay the scatter out
    // in visible stripes instead of noise.
    static int Hash(int a, int b, int c)
    {
        unchecked
        {
            uint h = 2166136261u;
            h = (h ^ (uint)a) * 16777619u;
            h = (h ^ (uint)b) * 16777619u;
            h = (h ^ (uint)c) * 16777619u;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            return (int)(h & 0x7fffffffu);
        }
    }

    // --- colour spill -------------------------------------------------------------------------

    void BuildSpillLights()
    {
        // The quad is scaled to the pad and rotated flat, so derive the plane from the transform
        // rather than from a second set of serialized numbers that could drift out of step with it.
        Vector3 right = transform.right;         // quad local +X, along the pad
        Vector3 forward = transform.up;          // quad local +Y, along the pad
        Vector3 up = -transform.forward;         // the quad's visible face points along local -Z

        float halfX = Mathf.Abs(transform.lossyScale.x) * 0.5f;
        float halfZ = Mathf.Abs(transform.lossyScale.y) * 0.5f;

        Transform holder = new GameObject("SpillLights").transform;
        holder.SetParent(transform.parent != null ? transform.parent : transform, false);
        holder.gameObject.AddComponent<MazeLightCullIgnore>(); // distance-gated here instead

        _spill = new Light[4];
        int i = 0;
        for (int sz = -1; sz <= 1; sz += 2)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            {
                var go = new GameObject("SpillLight");
                go.transform.SetParent(holder, false);
                go.transform.position = transform.position
                                      + right * (sx * halfX * 0.5f)
                                      + forward * (sz * halfZ * 0.5f)
                                      + up * spillHeight;

                Light l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = spillRange;
                l.intensity = spillIntensity;
                l.shadows = LightShadows.None;
                l.color = Color.white;
                _spill[i++] = l;
            }
        }
    }

    void UpdateSpillLights(int n, float beatEnvelope)
    {
        if (_spill == null)
            return;

        // One light per quadrant, tinted to the mean of the tiles beneath it. Index order matches
        // the (sx, sz) loop in BuildSpillLights: x varies fastest, then z.
        int half = Mathf.Max(1, n / 2);
        for (int q = 0; q < _spill.Length; q++)
        {
            Light l = _spill[q];
            if (l == null)
                continue;

            int x0 = (q & 1) == 0 ? 0 : half;
            int z0 = q < 2 ? 0 : half;

            Vector3 sum = Vector3.zero;
            int taken = 0;
            for (int z = z0; z < Mathf.Min(z0 + half, n); z++)
            {
                for (int x = x0; x < Mathf.Min(x0 + half, n); x++)
                {
                    Color c = _current[z * n + x];
                    sum += new Vector3(c.r, c.g, c.b);
                    taken++;
                }
            }

            if (taken == 0)
                continue;

            float invTaken = 1f / taken;
            Vector3 mean = sum * invTaken;

            // Normalise the hue out of the level: a dim step should change the colour of the room,
            // not how brightly lit it is.
            float peak = Mathf.Max(0.001f, Mathf.Max(mean.x, Mathf.Max(mean.y, mean.z)));
            l.color = new Color(mean.x / peak, mean.y / peak, mean.z / peak, 1f);
            l.intensity = spillIntensity * Mathf.Clamp01(peak * 1.6f) * (1f + spillBeatPulse * beatEnvelope);
        }
    }

    void SetSpillEnabled(bool on)
    {
        if (_spill == null)
            return;
        for (int i = 0; i < _spill.Length; i++)
            if (_spill[i] != null && _spill[i].enabled != on)
                _spill[i].enabled = on;
    }

    bool IsNearLocalView()
    {
        Camera cam = ResolveViewpoint();
        if (cam == null)
            return false; // headless server, or no local view yet — nothing to light.
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
