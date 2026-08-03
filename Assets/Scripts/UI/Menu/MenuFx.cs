using UnityEngine;
using UnityEngine.UI;

/// <summary>Scrolls a RawImage's uvRect — used for the drifting fog / maze backdrop layers.</summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RawImage))]
public sealed class UiUvScroll : MonoBehaviour
{
    public Vector2 velocity = new(0.01f, 0f);

    RawImage _image;

    void Awake()
    {
        _image = GetComponent<RawImage>();
    }

    void Update()
    {
        Rect uv = _image.uvRect;
        uv.position += velocity * Time.unscaledDeltaTime;
        uv.x = Mathf.Repeat(uv.x, 1f);
        uv.y = Mathf.Repeat(uv.y, 1f);
        _image.uvRect = uv;
    }
}

/// <summary>Candle-style alpha flicker for a Graphic (sines + noise, unscaled time).</summary>
[DisallowMultipleComponent]
public sealed class UiFlicker : MonoBehaviour
{
    public Graphic target;
    [Range(0f, 1f)] public float baseAlpha = 0.5f;
    [Range(0f, 1f)] public float amplitude = 0.18f;
    public float speed = 1f;

    float _seed;

    void Awake()
    {
        if (target == null)
            target = GetComponent<Graphic>();
        _seed = (GetInstanceID() & 0xFFFF) * 0.137f;
    }

    void Update()
    {
        if (target == null)
            return;

        float t = Time.unscaledTime * speed;
        float wave = Mathf.Sin(t * 5.3f + _seed) * 0.35f
                   + Mathf.Sin(t * 11.7f + _seed * 2f) * 0.2f;
        float noise = Mathf.PerlinNoise(t * 1.9f, _seed) - 0.5f;
        float a = Mathf.Clamp01(baseAlpha + (wave * 0.5f + noise) * amplitude);

        Color c = target.color;
        c.a = a;
        target.color = c;
    }
}

/// <summary>
/// Keeps a tiling RawImage's uvRect matched to its rect size so weathering grain stays at a
/// constant density regardless of widget size, with a per-instance offset so no two plates
/// share the same patch of wear.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RawImage))]
public sealed class UiGrungeFit : MonoBehaviour
{
    public float pixelsPerTile = 260f;

    RawImage _image;
    float _offsetX;
    float _offsetY;

    void Awake()
    {
        _image = GetComponent<RawImage>();
        int id = Mathf.Abs(GetInstanceID());
        _offsetX = (id % 613) / 613f;
        _offsetY = (id % 419) / 419f;
        Apply();
    }

    void OnRectTransformDimensionsChange()
    {
        Apply();
    }

    void Apply()
    {
        if (_image == null)
            return;
        Rect r = ((RectTransform)transform).rect;
        _image.uvRect = new Rect(_offsetX, _offsetY,
            Mathf.Max(0.05f, r.width / pixelsPerTile), Mathf.Max(0.05f, r.height / pixelsPerTile));
    }
}

/// <summary>
/// Keeps an Image's hand-cut sprite (<see cref="MenuTheme.HandSprite"/>) matched to its rect —
/// the plates aren't 9-sliced, so each size gets its own silhouette, regenerated when the rect
/// crosses a size step. Fill/Outline siblings share a seed so the frame hugs its plate.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Image))]
public sealed class UiHandPlate : MonoBehaviour
{
    public MenuTheme.HandKind kind = MenuTheme.HandKind.Fill;
    public int seed;
    public float stroke = 2.2f;

    Image _image;
    int _lastW = -1;
    int _lastH = -1;

    void OnEnable()
    {
        Apply();
    }

    void OnRectTransformDimensionsChange()
    {
        if (isActiveAndEnabled)
            Apply();
    }

    public void Apply()
    {
        if (_image == null)
            _image = GetComponent<Image>();

        Rect r = ((RectTransform)transform).rect;
        int w = Mathf.RoundToInt(r.width);
        int h = Mathf.RoundToInt(r.height);
        if (w < 4 || h < 4)
            return;   // layout hasn't sized us yet

        w = Mathf.Max(MenuTheme.HandSizeStep, Mathf.RoundToInt(w / (float)MenuTheme.HandSizeStep) * MenuTheme.HandSizeStep);
        h = Mathf.Max(MenuTheme.HandSizeStep, Mathf.RoundToInt(h / (float)MenuTheme.HandSizeStep) * MenuTheme.HandSizeStep);
        if (w == _lastW && h == _lastH)
            return;

        _lastW = w;
        _lastH = h;
        _image.sprite = MenuTheme.HandSprite(w, h, seed, kind, stroke);
        _image.type = Image.Type.Simple;
    }
}

/// <summary>Gentle alpha pulse for small indicators (e.g. "waiting" dots).</summary>
[DisallowMultipleComponent]
public sealed class UiPulse : MonoBehaviour
{
    public Graphic target;
    public float minAlpha = 0.35f;
    public float maxAlpha = 1f;
    public float speed = 2.2f;

    void Awake()
    {
        if (target == null)
            target = GetComponent<Graphic>();
    }

    void Update()
    {
        if (target == null)
            return;
        float t = (Mathf.Sin(Time.unscaledTime * speed) + 1f) * 0.5f;
        Color c = target.color;
        c.a = Mathf.Lerp(minAlpha, maxAlpha, t);
        target.color = c;
    }
}

/// <summary>
/// Fades a screen (CanvasGroup alpha + slight horizontal slide) in and out, deactivating when hidden.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CanvasGroup))]
public sealed class MenuScreenFader : MonoBehaviour
{
    const float Speed = 9f;
    const float SlideDistance = 26f;

    CanvasGroup _group;
    RectTransform _rect;
    Vector2 _basePosition;
    bool _baseCaptured;
    bool _shown;

    public bool IsShown => _shown;

    void Awake()
    {
        EnsureRefs();
    }

    void EnsureRefs()
    {
        if (_group == null)
            _group = GetComponent<CanvasGroup>();
        if (_rect == null)
            _rect = GetComponent<RectTransform>();
        if (!_baseCaptured && _rect != null)
        {
            _basePosition = _rect.anchoredPosition;
            _baseCaptured = true;
        }
    }

    public void Show(bool instant = false)
    {
        EnsureRefs();
        _shown = true;
        gameObject.SetActive(true);
        _group.blocksRaycasts = true;
        _group.interactable = true;
        if (instant)
        {
            _group.alpha = 1f;
            _rect.anchoredPosition = _basePosition;
        }
    }

    public void Hide(bool instant = false)
    {
        EnsureRefs();
        _shown = false;
        _group.blocksRaycasts = false;
        _group.interactable = false;
        if (instant)
        {
            _group.alpha = 0f;
            gameObject.SetActive(false);
        }
    }

    void Update()
    {
        float target = _shown ? 1f : 0f;
        float a = Mathf.MoveTowards(_group.alpha, target, Time.unscaledDeltaTime * Speed);
        _group.alpha = a;

        Vector2 hidden = _basePosition + new Vector2(SlideDistance, 0f);
        _rect.anchoredPosition = Vector2.Lerp(hidden, _basePosition, EaseOut(a));

        if (!_shown && a <= 0.001f)
            gameObject.SetActive(false);
    }

    static float EaseOut(float t)
    {
        return 1f - (1f - t) * (1f - t);
    }
}

/// <summary>
/// Shared click/hover sounds for menu widgets. Routed through the game's Ui mixer group — volume is
/// controlled entirely by the "UiVolume" exposed parameter on that bus (see GameAudioManager's UI Sound
/// Volume slider), not by a code-side multiplier.
/// </summary>
public static class MenuUiAudio
{
    const string ClipResourcePath = "UI/UIClick";

    const float HoverBaseVolume = 0.07f;
    const float ClickBaseVolume = 0.275f;

    static AudioSource _source;
    static AudioClip _clip;
    static bool _clipSearched;

    static AudioSource Source
    {
        get
        {
            if (_source == null)
            {
                var go = new GameObject("MenuUiAudio");
                Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.DontSave;
                _source = go.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.loop = false;
                _source.spatialBlend = 0f;
                _source.dopplerLevel = 0f;
                if (GameAudioManager.Instance != null)
                    GameAudioManager.RouteUiSource(_source);
            }
            return _source;
        }
    }

    static AudioClip Clip
    {
        get
        {
            if (!_clipSearched)
            {
                _clip = Resources.Load<AudioClip>(ClipResourcePath);
                _clipSearched = true;
            }
            return _clip;
        }
    }

    public static void PlayHover()
    {
        if (Clip == null)
            return;
        Source.pitch = 1.55f;
        Source.PlayOneShot(Clip, HoverBaseVolume);
    }

    public static void PlayClick()
    {
        if (Clip == null)
            return;
        Source.pitch = 1f;
        Source.PlayOneShot(Clip, ClickBaseVolume);
    }
}
