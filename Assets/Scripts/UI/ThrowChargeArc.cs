using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Curved crescent throw-charge meter that hangs just to the right of the centre crosshair while a
/// heavy throwable is being wound up. Hand-sketched to match <see cref="PlayerVitalsHud"/>: an SDF
/// arc-capsule plus two-octave Perlin wobble bakes a solid crescent and a hollow outline once, then
/// three layered Images read as one gauge — a dark empty track, an amber <c>Vertical</c>-Filled
/// overlay that rises along the curve as charge climbs 0→1, and a bone line-art border on top.
/// Built and driven by PlayerController; the sprites are generated once and shared across players.
/// </summary>
public static class ThrowChargeArc
{
    // ---------------------------------------------------------------- crescent geometry (texture px)
    // A ")" shaped band: centre of curvature sits at the left, so the arc bulges right and opens
    // toward the crosshair on its left. Tips top/bottom, fattest at the middle of the sweep.
    const int TexW = 144;
    const int TexH = 248;
    const float ArcCenterX = -52f;
    const float ArcCenterY = 124f;
    const float ArcRadius = 145f;
    const float ArcHalfAngleDeg = 45f;
    const float BandHalf = 12f;      // half thickness of the band
    const float OutlineHalf = 1.9f;  // half width of the sketched border line
    const float WobbleAmp = 1.5f;    // hand-drawn jitter on the SDF edges
    const float EdgeSoft = 1.2f;     // anti-alias softness

    // ---------------------------------------------------------------- placement (1920x1080 canvas units)
    const float RenderHeight = 176f;
    const float OffsetX = 104f; // right of the centre crosshair
    const float OffsetY = 0f;

    static Sprite _solidSprite;
    static Sprite _outlineSprite;

    /// <summary>Builds the crescent under the given canvas and returns its root; outputs the amber
    /// fill Image so the caller can drive <see cref="Image.fillAmount"/> and tint it.</summary>
    public static GameObject Create(Transform canvasRoot, out Image fill)
    {
        var root = new GameObject("ThrowChargeArc", typeof(RectTransform));
        root.layer = 5;
        var rt = (RectTransform)root.transform;
        rt.SetParent(canvasRoot, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(OffsetX, OffsetY);
        rt.sizeDelta = new Vector2(RenderHeight * TexW / (float)TexH, RenderHeight);

        Sprite solid = SolidSprite();

        // Dark empty track so the unfilled portion reads as a carved groove, not a hole.
        Image track = MakeLayer(rt, "Track", solid);
        track.color = MenuTheme.WithAlpha(MenuTheme.Ink, 0.55f);

        // Amber fill: Vertical/Bottom Filled so it climbs the crescent from the low tip upward.
        fill = MakeLayer(rt, "Fill", solid);
        fill.type = Image.Type.Filled;
        fill.fillMethod = Image.FillMethod.Vertical;
        fill.fillOrigin = (int)Image.OriginVertical.Bottom;
        fill.fillAmount = 0f;
        fill.color = MenuTheme.WithAlpha(MenuTheme.Amber, 0.98f);

        // Sketched bone border on top, always full.
        Image line = MakeLayer(rt, "Outline", OutlineSprite());
        line.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.85f);

        return root;
    }

    static Image MakeLayer(RectTransform parent, string name, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.raycastTarget = false;
        return img;
    }

    // ---------------------------------------------------------------- sprite generation

    static Sprite SolidSprite() => _solidSprite != null ? _solidSprite : (_solidSprite = BuildSprite(false));
    static Sprite OutlineSprite() => _outlineSprite != null ? _outlineSprite : (_outlineSprite = BuildSprite(true));

    static Sprite BuildSprite(bool outline)
    {
        return MakeSprite(TexW, TexH, (x, y) =>
        {
            float d = ArcSdf(x, y) + Wobble(x, y, WobbleAmp);
            if (outline)
                d = Mathf.Abs(d) - OutlineHalf; // hollow border band around the zero crossing
            return Mathf.Clamp01(0.5f - d / EdgeSoft);
        });
    }

    /// <summary>Signed distance to a rounded-capped arc band centred on the +x axis, symmetric
    /// about the horizontal through the arc's centre of curvature.</summary>
    static float ArcSdf(float x, float y)
    {
        float dx = x - ArcCenterX;
        float dy = y - ArcCenterY;
        float rho = Mathf.Sqrt(dx * dx + dy * dy);
        float ang = Mathf.Atan2(dy, dx);
        float half = ArcHalfAngleDeg * Mathf.Deg2Rad;
        if (Mathf.Abs(ang) <= half)
            return Mathf.Abs(rho - ArcRadius) - BandHalf; // inside the sweep: distance to the ring

        // Beyond either tip: round the cap off the nearer endpoint.
        float ex = ArcCenterX + ArcRadius * Mathf.Cos(half);
        float sy = ArcRadius * Mathf.Sin(half);
        float d0 = Mathf.Sqrt((x - ex) * (x - ex) + (y - (ArcCenterY - sy)) * (y - (ArcCenterY - sy)));
        float d1 = Mathf.Sqrt((x - ex) * (x - ex) + (y - (ArcCenterY + sy)) * (y - (ArcCenterY + sy)));
        return Mathf.Min(d0, d1) - BandHalf;
    }

    /// <summary>Two-octave Perlin jitter that makes the SDF edges read as hand-drawn.</summary>
    static float Wobble(float x, float y, float amount)
    {
        float n = (Mathf.PerlinNoise(x * 0.045f + 11.7f, y * 0.045f + 3.9f) - 0.5f) * 2f
                + (Mathf.PerlinNoise(x * 0.13f + 4.2f, y * 0.13f + 8.8f) - 0.5f) * 0.9f;
        return n * amount;
    }

    static Sprite MakeSprite(int w, int h, System.Func<float, float, float> alphaAt)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave,
        };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = Mathf.Clamp01(alphaAt(x + 0.5f, y + 0.5f));
                px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, false);
        var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
