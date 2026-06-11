using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Style source-of-truth for the menu system: palette, fonts, and a cache of procedurally
/// generated sprites/textures (rounded panels, glows, fog, vignette, maze motif).
/// Everything is built at runtime so menus need no hand-authored art or scene wiring.
/// </summary>
public static class MenuTheme
{
    // ---------------------------------------------------------------- palette
    public static readonly Color Ink = Hex("07080B");          // deepest background
    public static readonly Color Bg = Hex("0B0D12");
    public static readonly Color Panel = Hex("10131A");
    public static readonly Color PanelRaised = Hex("171B24");
    public static readonly Color Stroke = Hex("262B36");
    public static readonly Color StrokeBright = Hex("3C4453");

    public static readonly Color Bone = Hex("E9E2D0");          // primary text
    public static readonly Color Mist = Hex("9A937F");          // secondary text
    public static readonly Color Faint = Hex("615C4F");         // tertiary text

    public static readonly Color Amber = Hex("E2A13F");         // candle accent
    public static readonly Color AmberBright = Hex("FFC465");
    public static readonly Color AmberDeep = Hex("8A5F1E");
    public static readonly Color Blood = Hex("A8353A");         // destructive accent
    public static readonly Color BloodBright = Hex("D4555B");
    public static readonly Color Moss = Hex("7E9E55");          // ready / positive

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
    static readonly string[] DisplayFontCandidates = { "Constantia", "Palatino Linotype", "Georgia", "Cambria" };

    /// <summary>Engraved-serif display face for titles/buttons; falls back to the body font.</summary>
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
                _bodyFont = TMP_Settings.defaultFontAsset;
                if (_bodyFont == null)
                    _bodyFont = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            }
            return _bodyFont;
        }
    }

    static TMP_FontAsset CreateOsFontAsset(string[] familyCandidates)
    {
        foreach (string wanted in familyCandidates)
        {
            try
            {
                // family-name overload resolves the font file through the OS font engine,
                // which works where CreateFontAsset(Font) fails for dynamic OS fonts.
                TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(wanted, "Regular", 90);
                if (asset != null)
                {
                    asset.hideFlags = HideFlags.HideAndDontSave;
                    return asset;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MenuTheme] OS font '{wanted}' unavailable: {e.Message}");
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

    /// <summary>White 9-sliced rounded-rect fill; tint via Image.color.</summary>
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

    /// <summary>White 9-sliced rounded-rect inner stroke; tint via Image.color.</summary>
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

    // ---------------------------------------------------------------- atmosphere textures

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
}
