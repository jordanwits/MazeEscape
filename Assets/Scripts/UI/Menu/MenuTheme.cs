using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Style source-of-truth for every runtime-built UI surface: palette, fonts, and a cache of
/// procedurally generated sprites/textures (tiles, frames, grunge, torn bars, cursor, fog).
/// The language is "condemned funhouse noticeboard": warm charcoal tiles, bone frames and
/// type, one mustard accent, everything lightly weathered. No authored art required.
/// </summary>
public static class MenuTheme
{
    // ---------------------------------------------------------------- palette
    public static readonly Color Ink = Hex("0D0C09");           // deepest background / shadows
    public static readonly Color Bg = Hex("171511");
    public static readonly Color Panel = Hex("1E1B15");
    public static readonly Color PanelRaised = Hex("27231A");
    public static readonly Color Tile = Hex("14120D");          // button plate fill
    public static readonly Color Stroke = Hex("3B362B");        // hairlines
    public static readonly Color StrokeBright = Hex("57503F");

    public static readonly Color Bone = Hex("E6E1D3");          // primary text + frames
    public static readonly Color Mist = Hex("A69F8F");          // secondary text
    public static readonly Color Faint = Hex("6E6759");         // tertiary text

    public static readonly Color Amber = Hex("C06E38");         // muted orange accent / selected
    public static readonly Color AmberBright = Hex("D98A4E");
    public static readonly Color AmberDeep = Hex("5C3211");     // edge on orange plates
    public static readonly Color InkOnAccent = Hex("1A1007");   // text on orange
    public static readonly Color Blood = Hex("A23A33");         // destructive accent
    public static readonly Color BloodBright = Hex("C75A50");
    public static readonly Color Moss = Hex("8C9C5A");          // ready / positive

    public static Color WithAlpha(Color c, float a)
    {
        c.a = a;
        return c;
    }

    static Color Hex(string hex)
    {
        return ColorUtility.TryParseHtmlString("#" + hex, out Color c) ? c : Color.magenta;
    }

    // ---------------------------------------------------------------- fonts

    static TMP_FontAsset _displayFont;
    static TMP_FontAsset _bodyFont;

    // Condensed grotesque for display type (Bahnschrift = DIN, ships with Win10+).
    // Both variable-font style names and standalone family names are tried, then
    // hard classics, then the body font.
    static readonly string[][] DisplayFontCandidates =
    {
        new[] { "Bahnschrift", "SemiBold Condensed" },
        new[] { "Bahnschrift SemiBold Condensed", "Regular" },
        new[] { "Bahnschrift", "SemiBold" },
        new[] { "Bahnschrift", "Regular" },
        new[] { "Franklin Gothic Medium", "Regular" },
        new[] { "Impact", "Regular" },
        new[] { "Arial Narrow", "Bold" },
    };

    static readonly string[][] BodyFontCandidates =
    {
        new[] { "Segoe UI", "Regular" },
        new[] { "Tahoma", "Regular" },
    };

    /// <summary>Condensed display face for titles, buttons and headers.</summary>
    public static TMP_FontAsset DisplayFont
    {
        get
        {
            if (_displayFont == null)
                _displayFont = CreateOsFontAsset(DisplayFontCandidates) ?? BodyFont;
            return _displayFont;
        }
    }

    public static TMP_FontAsset BodyFont
    {
        get
        {
            if (_bodyFont == null)
            {
                _bodyFont = CreateOsFontAsset(BodyFontCandidates);
                if (_bodyFont == null)
                    _bodyFont = TMP_Settings.defaultFontAsset;
                if (_bodyFont == null)
                    _bodyFont = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            }
            return _bodyFont;
        }
    }

    static TMP_FontAsset CreateOsFontAsset(string[][] familyStyleCandidates)
    {
        foreach (string[] candidate in familyStyleCandidates)
        {
            try
            {
                // family-name overload resolves the font file through the OS font engine,
                // which works where CreateFontAsset(Font) fails for dynamic OS fonts.
                TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(candidate[0], candidate[1], 90);
                if (asset != null)
                {
                    asset.hideFlags = HideFlags.HideAndDontSave;
                    return asset;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MenuTheme] OS font '{candidate[0]} {candidate[1]}' unavailable: {e.Message}");
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- sprite cache

    static readonly Dictionary<string, Sprite> SpriteCache = new();
    static readonly Dictionary<string, Texture2D> TextureCache = new();

    static Sprite CacheSprite(string key, System.Func<Sprite> build)
    {
        if (SpriteCache.TryGetValue(key, out Sprite cached) && cached != null)
            return cached;
        Sprite s = build();
        SpriteCache[key] = s;
        return s;
    }

    static Texture2D NewTexture(int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        return tex;
    }

    static Sprite ToSprite(Texture2D tex, Vector4 border)
    {
        var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
            new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    static float RoundedBoxDistance(Vector2 p, Vector2 halfSize, float radius)
    {
        Vector2 q = new(Mathf.Abs(p.x) - (halfSize.x - radius), Mathf.Abs(p.y) - (halfSize.y - radius));
        Vector2 qPos = new(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f));
        return qPos.magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
    }

    /// <summary>White 9-sliced near-square plate fill; tint via Image.color.</summary>
    public static Sprite RoundedRect(int radius)
    {
        return CacheSprite($"rect{radius}", () =>
        {
            const int pad = 4;
            const int center = 16;
            int size = 2 * (radius + pad) + center;
            Texture2D tex = NewTexture(size, size);
            var half = new Vector2(size * 0.5f - pad, size * 0.5f - pad);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f);
                    float d = RoundedBoxDistance(p, half, radius);
                    float a = Mathf.Clamp01(0.5f - d / 1.25f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            int b = radius + pad + 2;
            return ToSprite(tex, new Vector4(b, b, b, b));
        });
    }

    /// <summary>White 9-sliced inner frame stroke; tint via Image.color.</summary>
    public static Sprite RoundedOutline(int radius, float thickness)
    {
        return CacheSprite($"line{radius}_{thickness:0.#}", () =>
        {
            const int pad = 4;
            const int center = 16;
            int size = 2 * (radius + pad) + center;
            Texture2D tex = NewTexture(size, size);
            var half = new Vector2(size * 0.5f - pad, size * 0.5f - pad);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f);
                    float d = RoundedBoxDistance(p, half, radius);
                    // band hugging the inside edge: d in [-thickness, 0]
                    float band = Mathf.Abs(d + thickness * 0.5f) - thickness * 0.5f;
                    float a = Mathf.Clamp01(0.5f - band / 1.1f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            int b = radius + pad + 2;
            return ToSprite(tex, new Vector4(b, b, b, b));
        });
    }

    /// <summary>
    /// Rounded rect that fades out over <paramref name="blur"/> px beyond its edge — the drop
    /// shadow under smooth panels. 9-sliced, so one sprite serves every card size.
    /// </summary>
    public static Sprite RoundedShadow(int radius, int blur)
    {
        return CacheSprite($"rshadow{radius}_{blur}", () =>
        {
            const int center = 16;
            int pad = blur + 4;
            int size = 2 * (radius + pad) + center;
            Texture2D tex = NewTexture(size, size);
            var half = new Vector2(size * 0.5f - pad, size * 0.5f - pad);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f - size * 0.5f, y + 0.5f - size * 0.5f);
                    float d = RoundedBoxDistance(p, half, radius);
                    float t = Mathf.Clamp01(1f - d / blur);
                    float a = t * t * (3f - 2f * t);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            int b = radius + pad + 2;
            return ToSprite(tex, new Vector4(b, b, b, b));
        });
    }

    // ---------------------------------------------------------------- hand-cut plates

    public enum HandKind { Fill, Outline, Shadow }

    /// <summary>
    /// Hand-cut plate family: a rounded rect whose edge wanders with low-frequency noise, like a
    /// board sawn by eye or a scrap torn against a straightedge. Generated per size (no slicing —
    /// slicing would stretch the wobble flat on long edges) at a capped resolution, so the bilinear
    /// upscale doubles as edge softening. Same (size, seed) → same silhouette across
    /// Fill/Outline/Shadow, which is what lets a frame hug its plate.
    /// Sizes are rounded to <see cref="HandSizeStep"/> before lookup to keep the cache small.
    /// </summary>
    public const int HandSizeStep = 8;

    public static Sprite HandPlate(int w, int h, int seed) => HandSprite(w, h, seed, HandKind.Fill, 0f);
    public static Sprite HandOutline(int w, int h, int seed, float stroke = 2.2f) => HandSprite(w, h, seed, HandKind.Outline, stroke);
    public static Sprite HandShadow(int w, int h, int seed) => HandSprite(w, h, seed, HandKind.Shadow, 0f);

    public static Sprite HandSprite(int w, int h, int seed, HandKind kind, float stroke)
    {
        w = Mathf.Max(HandSizeStep, Mathf.RoundToInt(w / (float)HandSizeStep) * HandSizeStep);
        h = Mathf.Max(HandSizeStep, Mathf.RoundToInt(h / (float)HandSizeStep) * HandSizeStep);
        seed = ((seed % 977) + 977) % 977;
        return CacheSprite($"hand{(int)kind}_{w}x{h}_{seed}_{stroke:0.#}", () => BuildHandSprite(w, h, seed, kind, stroke));
    }

    // Per-kind resolution caps: outlines need near-native pixels to read as a drawn line rather
    // than a glow; fills are forgiving (upscale blur doubles as edge softening); shadows are blurred
    // on purpose. All SDF math runs in plate space so every kind of the same (size, seed) shares one
    // silhouette regardless of its texture resolution.
    static float HandResolutionCap(HandKind kind)
    {
        switch (kind)
        {
            case HandKind.Outline: return 460f;
            case HandKind.Shadow: return 160f;
            default: return 230f;
        }
    }

    static Sprite BuildHandSprite(int w, int h, int seed, HandKind kind, float stroke)
    {
        float scale = Mathf.Min(1f, HandResolutionCap(kind) / Mathf.Max(w, h));
        // shadows bleed well past their plate, so they get real margin
        float padPlate = kind == HandKind.Shadow ? 30f : 7f;
        int pad = Mathf.CeilToInt(padPlate * scale) + 2;
        int tw = Mathf.Max(12, Mathf.RoundToInt(w * scale)) + pad * 2;
        int th = Mathf.Max(12, Mathf.RoundToInt(h * scale)) + pad * 2;

        Texture2D tex = NewTexture(tw, th);
        var px = new Color32[tw * th];

        var half = new Vector2(w * 0.5f, h * 0.5f);
        float minHalf = Mathf.Min(half.x, half.y);
        // soft: radius takes a third of the short side, wobble takes up to ~13% more
        float radius = minHalf * (0.32f + ((seed * 13) % 7) * 0.014f);
        float wobbleAmp = Mathf.Clamp(minHalf * 0.14f, 2.5f, 14f);
        float wobbleFreq = 0.030f + ((seed * 7) % 5) * 0.0035f;
        float ox = (seed % 31) * 3.17f;
        float oy = (seed % 17) * 7.91f;
        float aa = 1.35f / scale;                 // ~1.35 texture px of edge smoothing
        float strokeHalf = Mathf.Max(0.6f, stroke * 0.5f);
        float blur = 24f;

        for (int y = 0; y < th; y++)
        {
            for (int x = 0; x < tw; x++)
            {
                // plate-space position (UI pixels from the plate center)
                var p = new Vector2((x + 0.5f - tw * 0.5f) / scale, (y + 0.5f - th * 0.5f) / scale);
                float d = RoundedBoxDistance(p, half, radius);

                // the hand-cut: push the edge in and out with two octaves of noise
                float n = Mathf.PerlinNoise(p.x * wobbleFreq + ox, p.y * wobbleFreq + oy) - 0.5f
                        + 0.4f * (Mathf.PerlinNoise(p.x * wobbleFreq * 2.7f + oy, p.y * wobbleFreq * 2.7f + ox) - 0.5f);
                d += n * 2f * wobbleAmp;

                float a;
                byte v = 255;
                switch (kind)
                {
                    case HandKind.Outline:
                        // band hugging the edge, alpha-jittered so it reads drawn, not printed
                        float band = Mathf.Abs(d + strokeHalf) - strokeHalf;
                        a = Mathf.Clamp01(0.5f - band / aa);
                        a *= 0.8f + 0.2f * Mathf.PerlinNoise(p.x * 0.11f + oy, p.y * 0.11f + ox);
                        break;
                    case HandKind.Shadow:
                        float t = Mathf.Clamp01(1f - (d + blur * 0.15f) / blur);
                        a = t * t * (3f - 2f * t);
                        a *= a;
                        break;
                    default:
                        a = Mathf.Clamp01(0.5f - d / aa);
                        // weathering baked into the fill RGB (a separate rect overlay would show
                        // as a haze box past the silhouette): broad mottling plus sparse flecks
                        if (a > 0f)
                        {
                            float mottle = 0.55f * Mathf.PerlinNoise(p.x * 0.045f + ox * 1.7f, p.y * 0.045f + oy * 1.3f)
                                         + 0.45f * Mathf.PerlinNoise(p.x * 0.14f + oy, p.y * 0.14f + ox);
                            float shade = 0.90f + 0.10f * mottle;
                            float fleck = Mathf.PerlinNoise(p.x * 0.55f + ox, p.y * 0.55f + oy);
                            if (fleck > 0.74f)
                                shade -= 0.22f * (fleck - 0.74f) / 0.26f;
                            v = (byte)(Mathf.Clamp01(shade) * 255f);
                        }
                        break;
                }

                px[y * tw + x] = new Color32(v, v, v, (byte)(Mathf.Clamp01(a) * 255f));
            }
        }

        tex.SetPixels32(px);
        tex.Apply(false, false);
        return ToSprite(tex, Vector4.zero);
    }

    /// <summary>Soft radial glow, white; tint via Image.color.</summary>
    public static Sprite SoftGlow()
    {
        return CacheSprite("glow", () =>
        {
            const int size = 256;
            Texture2D tex = NewTexture(size, size);
            var px = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Pow(Mathf.Clamp01(1f - r), 3.1f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return ToSprite(tex, Vector4.zero);
        });
    }

    public static Sprite Circle()
    {
        return CacheSprite("circle", () =>
        {
            const int size = 64;
            Texture2D tex = NewTexture(size, size);
            var px = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half;
                    float dy = y + 0.5f - half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - (half - 2f);
                    float a = Mathf.Clamp01(0.5f - d / 1.25f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return ToSprite(tex, Vector4.zero);
        });
    }

    /// <summary>
    /// Hollow ring / annulus (no inner fill) for radial-fill progress meters. <paramref name="thickness"/>
    /// is the band width as a fraction of the radius. Use with Image type=Filled, Radial360 to draw a
    /// border circle that fills around.
    /// </summary>
    public static Sprite Ring(float thickness = 0.16f)
    {
        int key = Mathf.RoundToInt(Mathf.Clamp(thickness, 0.02f, 0.9f) * 100f);
        return CacheSprite("ring_" + key, () =>
        {
            const int size = 128;
            Texture2D tex = NewTexture(size, size);
            var px = new Color32[size * size];
            float half = size * 0.5f;
            float outer = half - 2f;                       // 2px AA margin
            float band = Mathf.Max(1.5f, Mathf.Clamp(thickness, 0.02f, 0.9f) * outer);
            float mid = outer - band * 0.5f;
            float halfBand = band * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - half;
                    float dy = y + 0.5f - half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float d = Mathf.Abs(r - mid) - halfBand;   // <=0 inside the band
                    float a = Mathf.Clamp01(0.5f - d / 1.25f); // 1.25px AA edge, matches Circle()
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return ToSprite(tex, Vector4.zero);
        });
    }


    /// <summary>
    /// Microphone glyph for the crew list's per-player mute toggle. <paramref name="muted"/> adds the
    /// struck-through variant: the slash is knocked out of the mic body first so it reads as a cut
    /// rather than a line laid on top. Drawn from signed distances (same 1.25px AA edge as
    /// <see cref="Circle"/>); tint via Image.color.
    /// </summary>
    public static Sprite MicIcon(bool muted)
    {
        return CacheSprite(muted ? "mic_muted" : "mic", () =>
        {
            const int size = 128;
            Texture2D tex = NewTexture(size, size);
            var px = new Color32[size * size];

            // Proportions are in this 128px box: capsule head, open cradle under it, stem, foot.
            Vector2 headTop = new(64f, 95f);
            Vector2 headBottom = new(64f, 85f);
            const float headRadius = 13f;

            Vector2 cradleCenter = new(64f, 84f);
            const float cradleRadius = 25f;
            const float cradleHalfBand = 4f;

            Vector2 stemTop = new(64f, 59f);
            Vector2 stemBottom = new(64f, 36f);
            const float stemRadius = 4f;

            Vector2 footLeft = new(47f, 33f);
            Vector2 footRight = new(81f, 33f);
            const float footRadius = 4f;

            // Top-left to bottom-right, matching the universal "no" slash.
            Vector2 slashFrom = new(32f, 100f);
            Vector2 slashTo = new(96f, 28f);
            const float slashRadius = 5f;
            const float slashCutRadius = 8.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 pt = new(x + 0.5f, y + 0.5f);

                    float d = SegmentDistance(pt, headTop, headBottom) - headRadius;
                    d = Mathf.Min(d, CradleDistance(pt, cradleCenter, cradleRadius, cradleHalfBand));
                    d = Mathf.Min(d, SegmentDistance(pt, stemTop, stemBottom) - stemRadius);
                    d = Mathf.Min(d, SegmentDistance(pt, footLeft, footRight) - footRadius);

                    if (muted)
                    {
                        float slash = SegmentDistance(pt, slashFrom, slashTo);
                        d = Mathf.Max(d, -(slash - slashCutRadius));   // knock the gap out of the mic
                        d = Mathf.Min(d, slash - slashRadius);         // then lay the bar in the gap
                    }

                    float a = Mathf.Clamp01(0.5f - d / 1.25f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            return ToSprite(tex, Vector4.zero);
        });
    }

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 pa = p - a;
        Vector2 ba = b - a;
        float denom = Vector2.Dot(ba, ba);
        float h = denom <= Mathf.Epsilon ? 0f : Mathf.Clamp01(Vector2.Dot(pa, ba) / denom);
        return (pa - ba * h).magnitude;
    }

    /// <summary>
    /// The mic's open cradle: the lower half of an annulus, with round caps where it stops at the
    /// centre line (above that line the nearest point on the shape is one of those two caps).
    /// </summary>
    static float CradleDistance(Vector2 p, Vector2 center, float radius, float halfBand)
    {
        if (p.y <= center.y)
            return Mathf.Abs((p - center).magnitude - radius) - halfBand;

        float left = (p - new Vector2(center.x - radius, center.y)).magnitude;
        float right = (p - new Vector2(center.x + radius, center.y)).magnitude;
        return Mathf.Min(left, right) - halfBand;
    }

    /// <summary>Darkened-corner vignette; render as white-alpha mask, tint black at the Image.</summary>
    public static Sprite Vignette()
    {
        return CacheSprite("vignette", () =>
        {
            const int size = 512;
            Texture2D tex = NewTexture(size, size);
            var px = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.41421356f;
                    float t = Mathf.Clamp01((r - 0.42f) / 0.58f);
                    float a = Mathf.Pow(t * t * (3f - 2f * t), 1.4f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return ToSprite(tex, Vector4.zero);
        });
    }

    /// <summary>Vertical white→transparent gradient (alpha 1 at bottom, 0 at top).</summary>
    public static Sprite VerticalGradient()
    {
        return CacheSprite("vgrad", () =>
        {
            const int h = 256;
            Texture2D tex = NewTexture(4, h);
            var px = new Color32[4 * h];
            for (int y = 0; y < h; y++)
            {
                float t = 1f - y / (float)(h - 1);
                byte a = (byte)(Mathf.Pow(t, 1.5f) * 255f);
                for (int x = 0; x < 4; x++)
                    px[y * 4 + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return ToSprite(tex, Vector4.zero);
        });
    }

    public static Sprite Solid()
    {
        return CacheSprite("solid", () =>
        {
            Texture2D tex = NewTexture(4, 4);
            var px = new Color32[16];
            for (int i = 0; i < 16; i++)
                px[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return ToSprite(tex, Vector4.zero);
        });
    }

    /// <summary>
    /// Horizontal bar with a ragged, torn right end and lightly nibbled edges — the
    /// under-ledge on selected plates, title strikes, tape strips. Tint via Image.color.
    /// </summary>
    public static Sprite TornBar()
    {
        return CacheSprite("tornbar", () =>
        {
            const int w = 128;
            const int h = 12;
            Texture2D tex = NewTexture(w, h);
            var px = new Color32[w * h];
            var rng = new System.Random(7741);
            float[] topNibble = new float[w];
            float[] botNibble = new float[w];
            for (int x = 0; x < w; x++)
            {
                topNibble[x] = (float)rng.NextDouble();
                botNibble[x] = (float)rng.NextDouble();
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float a = 1f;
                    // torn right end: erode the last ~14 px with noise
                    float tearStart = w - 15f;
                    if (x > tearStart)
                    {
                        float t = (x - tearStart) / 14f;
                        float noise = Mathf.PerlinNoise(x * 0.9f, y * 0.55f);
                        a = Mathf.Clamp01(1f - t * 1.6f + (noise - 0.5f) * 0.9f);
                    }
                    // lightly nibbled top/bottom edge — stays a bar, not a scribble
                    if (y == h - 1 && topNibble[x] > 0.88f) a = 0f;
                    if (y == 0 && botNibble[x] > 0.88f) a = 0f;
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            // slice: keep the left cap + torn right cap, stretch the middle
            return ToSprite(tex, new Vector4(6, 0, 20, 0));
        });
    }

    /// <summary>Small tag chip with roughly eroded edges (tape/label tabs). Tint via Image.color.</summary>
    public static Sprite RoughChip()
    {
        return CacheSprite("roughchip", () =>
        {
            const int w = 64;
            const int h = 36;
            Texture2D tex = NewTexture(w, h);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // distance to edge, eroded by noise
                    float ex = Mathf.Min(x, w - 1 - x);
                    float ey = Mathf.Min(y, h - 1 - y);
                    float edge = Mathf.Min(ex, ey);
                    float noise = Mathf.PerlinNoise(x * 0.55f + 3.7f, y * 0.55f);
                    float a = Mathf.Clamp01(edge - 1.6f * noise + 0.8f);
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return ToSprite(tex, new Vector4(10, 10, 10, 10));
        });
    }

    // ---------------------------------------------------------------- atmosphere textures

    /// <summary>
    /// Tileable weathering grain: fine fBm speckle broken by faint diagonal scratches.
    /// Alpha holds the mask (white RGB) — overlay at very low alpha on plates and panels.
    /// </summary>
    public static Texture2D GrungeTexture()
    {
        if (TextureCache.TryGetValue("grunge", out Texture2D cached) && cached != null)
            return cached;

        const int size = 256;
        Texture2D tex = NewTexture(size, size);
        tex.wrapMode = TextureWrapMode.Repeat;

        var px = new Color32[size * size];
        var rng = new System.Random(52107);
        // sparse scratch set: seed lines with random angle/offset, drawn softly
        const int scratchCount = 8;
        var scratches = new Vector3[scratchCount]; // x: angle, y: offset, z: sharpness
        for (int i = 0; i < scratchCount; i++)
        {
            scratches[i] = new Vector3(
                Mathf.Lerp(0.55f, 1.05f, (float)rng.NextDouble()) * (rng.Next(2) == 0 ? 1f : -1f),
                (float)rng.NextDouble() * size,
                Mathf.Lerp(0.3f, 1.1f, (float)rng.NextDouble()));
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                float v = y / (float)size;

                // two octaves of tileable value noise via Perlin on a torus approximation
                float n = 0.62f * Mathf.PerlinNoise(u * 6f + 11.13f, v * 6f + 5.71f)
                        + 0.38f * Mathf.PerlinNoise(u * 17f + 3.29f, v * 17f + 9.44f);

                // speckle: hard grain dots
                float speck = (float)rng.NextDouble();
                float grain = speck > 0.975f ? 0.85f : 0f;

                // scratches (wrap distance so the texture stays tileable)
                float scratch = 0f;
                for (int i = 0; i < scratchCount; i++)
                {
                    float line = x * scratches[i].x + scratches[i].y;
                    float d = Mathf.Abs(Mathf.Repeat(y - line, size));
                    d = Mathf.Min(d, size - d);
                    if (d < scratches[i].z)
                        scratch = Mathf.Max(scratch, 1f - d / scratches[i].z);
                }

                float a = Mathf.Clamp01(Mathf.Pow(n, 2.4f) * 0.7f + grain + scratch * 0.30f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        TextureCache["grunge"] = tex;
        return tex;
    }

    /// <summary>
    /// Large tileable wall mottling: low-frequency stains + mid grain, for backdrops.
    /// Alpha mask; tint at the RawImage.
    /// </summary>
    public static Texture2D WallTexture()
    {
        if (TextureCache.TryGetValue("wall", out Texture2D cached) && cached != null)
            return cached;

        const int size = 512;
        Texture2D tex = NewTexture(size, size);
        tex.wrapMode = TextureWrapMode.Repeat;

        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                float v = y / (float)size;
                float f = 0.5f * Mathf.PerlinNoise(u * 3f + 91.2f, v * 3f + 17.8f)
                        + 0.3f * Mathf.PerlinNoise(u * 7f + 44.1f, v * 7f + 60.3f)
                        + 0.2f * Mathf.PerlinNoise(u * 19f + 8.8f, v * 19f + 23.5f);
                // push contrast so stains read as patches, not uniform noise
                f = Mathf.Clamp01((f - 0.42f) * 1.9f);
                f = f * f * (3f - 2f * f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(f * 200f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        TextureCache["wall"] = tex;
        return tex;
    }

    /// <summary>Tileable fBm value-noise used for drifting fog layers (alpha holds the noise).</summary>
    public static Texture2D FogTexture()
    {
        if (TextureCache.TryGetValue("fog", out Texture2D cached) && cached != null)
            return cached;

        const int size = 256;
        Texture2D tex = NewTexture(size, size);
        tex.wrapMode = TextureWrapMode.Repeat;

        var rng = new System.Random(91342);
        const int baseLattice = 8;
        int octaves = 4;
        float[][] lattices = new float[octaves][];
        int[] latticeSizes = new int[octaves];
        for (int o = 0; o < octaves; o++)
        {
            int n = baseLattice << o;
            latticeSizes[o] = n;
            lattices[o] = new float[n * n];
            for (int i = 0; i < n * n; i++)
                lattices[o][i] = (float)rng.NextDouble();
        }

        float SampleLattice(int o, float u, float v)
        {
            int n = latticeSizes[o];
            float fu = u * n;
            float fv = v * n;
            int x0 = Mathf.FloorToInt(fu) % n;
            int y0 = Mathf.FloorToInt(fv) % n;
            int x1 = (x0 + 1) % n;
            int y1 = (y0 + 1) % n;
            float tx = fu - Mathf.Floor(fu);
            float ty = fv - Mathf.Floor(fv);
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float[] l = lattices[o];
            float a = Mathf.Lerp(l[y0 * n + x0], l[y0 * n + x1], tx);
            float b = Mathf.Lerp(l[y1 * n + x0], l[y1 * n + x1], tx);
            return Mathf.Lerp(a, b, ty);
        }

        var px = new Color32[size * size];
        float[] weights = { 0.5f, 0.27f, 0.15f, 0.08f };
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                float v = y / (float)size;
                float f = 0f;
                for (int o = 0; o < octaves; o++)
                    f += SampleLattice(o, u, v) * weights[o];
                f = Mathf.Pow(Mathf.Clamp01(f), 1.7f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(f * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        TextureCache["fog"] = tex;
        return tex;
    }

    /// <summary>Faint maze-line motif (recursive backtracker) drifting behind the menu.</summary>
    public static Texture2D MazeTexture()
    {
        if (TextureCache.TryGetValue("maze", out Texture2D cached) && cached != null)
            return cached;

        const int cols = 30;
        const int rows = 17;
        const int cell = 34;
        const int line = 2;
        int w = cols * cell;
        int h = rows * cell;
        Texture2D tex = NewTexture(w, h);
        tex.wrapMode = TextureWrapMode.Repeat;

        // carve a maze: walls[x,y] bit 0 = wall to east, bit 1 = wall to south
        var rng = new System.Random(20260611);
        int[,] walls = new int[cols, rows];
        bool[,] visited = new bool[cols, rows];
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
                walls[x, y] = 3;

        var stack = new Stack<Vector2Int>();
        var start = new Vector2Int(rng.Next(cols), rng.Next(rows));
        visited[start.x, start.y] = true;
        stack.Push(start);
        var dirs = new Vector2Int[4];
        while (stack.Count > 0)
        {
            Vector2Int c = stack.Peek();
            int count = 0;
            if (c.x > 0 && !visited[c.x - 1, c.y]) dirs[count++] = Vector2Int.left;
            if (c.x < cols - 1 && !visited[c.x + 1, c.y]) dirs[count++] = Vector2Int.right;
            if (c.y > 0 && !visited[c.x, c.y - 1]) dirs[count++] = Vector2Int.down;
            if (c.y < rows - 1 && !visited[c.x, c.y + 1]) dirs[count++] = Vector2Int.up;
            if (count == 0)
            {
                stack.Pop();
                continue;
            }
            Vector2Int d = dirs[rng.Next(count)];
            Vector2Int n = c + d;
            if (d == Vector2Int.right) walls[c.x, c.y] &= ~1;
            else if (d == Vector2Int.left) walls[n.x, n.y] &= ~1;
            else if (d == Vector2Int.up) walls[c.x, c.y] &= ~2;
            else walls[n.x, n.y] &= ~2;
            visited[n.x, n.y] = true;
            stack.Push(n);
        }

        var px = new Color32[w * h];
        void Plot(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h)
                return;
            px[y * w + x] = new Color32(255, 255, 255, 255);
        }

        for (int cx = 0; cx < cols; cx++)
        {
            for (int cy = 0; cy < rows; cy++)
            {
                int ox = cx * cell;
                int oy = cy * cell;
                if ((walls[cx, cy] & 1) != 0 && cx < cols - 1)
                {
                    for (int y = 0; y < cell; y++)
                        for (int t = 0; t < line; t++)
                            Plot(ox + cell - 1 - t, oy + y);
                }
                if ((walls[cx, cy] & 2) != 0 && cy < rows - 1)
                {
                    for (int x = 0; x < cell; x++)
                        for (int t = 0; t < line; t++)
                            Plot(ox + x, oy + cell - 1 - t);
                }
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        TextureCache["maze"] = tex;
        return tex;
    }

    // ---------------------------------------------------------------- cursor

    static Texture2D _cursorTexture;
    static readonly Vector2 CursorHotspot = new(2f, 2f);

    /// <summary>Bone arrow cursor with an ink rim, generated once. 32×32, hotspot near the tip.</summary>
    public static Texture2D CursorTexture()
    {
        if (_cursorTexture != null)
            return _cursorTexture;

        const int size = 32;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point,
            hideFlags = HideFlags.HideAndDontSave,
        };

        // classic pointer polygon in texture space (y up), tip at top-left
        Vector2[] poly =
        {
            new(3f, 29f), new(3f, 8f), new(8.6f, 13.8f), new(11.4f, 5.6f),
            new(15.4f, 7.2f), new(12.4f, 15.2f), new(20f, 15.2f),
        };

        bool Inside(Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if (poly[i].y > p.y != poly[j].y > p.y &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        var fill = new bool[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                fill[y * size + x] = Inside(new Vector2(x + 0.5f, y + 0.5f));

        var px = new Color32[size * size];
        Color32 bone = Bone;
        Color32 ink = new Color32(13, 12, 9, 255);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                if (fill[i])
                {
                    px[i] = bone;
                    continue;
                }
                bool edge = false;
                for (int dy = -1; dy <= 1 && !edge; dy++)
                {
                    for (int dx = -1; dx <= 1 && !edge; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < size && ny < size && fill[ny * size + nx])
                            edge = true;
                    }
                }
                px[i] = edge ? ink : new Color32(0, 0, 0, 0);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        _cursorTexture = tex;
        return tex;
    }

    /// <summary>Swap the OS cursor for the themed one (call when a menu/overlay shows the cursor).</summary>
    public static void ApplyCursor()
    {
        Cursor.SetCursor(CursorTexture(), CursorHotspot, CursorMode.Auto);
    }
}
