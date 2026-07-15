using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Top-left vitals cluster for the local player, drawn in a hand-sketched glowing-gauge style:
/// a wobbly-outlined body silhouette whose interior drains head-down as health falls (moss through
/// 75%+, then ochre → amber → blood), a graduated stamina arc cradling the figure's feet, and a
/// ruler-marked flashlight charge gauge (bolt icon) that appears only while a flashlight is in
/// hand. All sprites are generated at runtime (SDF capsules + Perlin wobble + baked glow) from the
/// MenuTheme palette — no authored art. Lives on the player GameObject; PlayerHealth drives the
/// health side and owns whole-cluster visibility, PlayerController drives stamina/charge and
/// toggles only its own readouts, so Jailor-carry — which drops control but not life — keeps the
/// body gauge on screen.
/// </summary>
[DisallowMultipleComponent]
public sealed class PlayerVitalsHud : MonoBehaviour
{
    // ---------------------------------------------------------------- layout (canvas units)
    const float RootX = 8f;
    const float RootY = -2f;

    const float FigureHeight = 158f;
    const float FigureCenterX = 108.3f;
    const float FigureTopY = -40.8f;

    // Bar + icon placement, arranged live in play mode by the user and captured verbatim: the
    // charge bar is vertical on the left, the stamina bar is horizontal along the bottom, and each
    // carries its icon. Both draw the same meter sprite (Horizontal/Left Filled fill); the bar's
    // rotation decides whether the fill reads bottom→top (charge, 90°) or left→right (stamina, 0°).
    const float MeterWidth = 158f; // bar long-axis length (sizeDelta.x); thickness from texture aspect
    static readonly Vector2 ChargeBarPos = new(32f, -129f);
    const float ChargeBarRotDeg = 90f;
    static readonly Vector2 ChargeIconPos = new(90.8f, 11.7f);
    static readonly Vector2 ChargeIconSize = new(17f, 21f);
    const float ChargeIconRotDeg = 270f;
    static readonly Vector2 StaminaBarPos = new(105f, -205f);
    const float StaminaBarRotDeg = 0f;
    static readonly Vector2 StaminaIconPos = new(84.8f, -23.8f);
    static readonly Vector2 StaminaIconSize = new(23f, 19f);
    const float StaminaIconRotDeg = 0f;

    // ---------------------------------------------------------------- figure geometry
    // Health figure comes from the hand-drawn PlayerSketch, baked to two Resources sprites
    // (solid silhouette fill + line-art outline) by the offline bake — see player-vitals-hud memory.
    const string FigureFillResource = "UI/PlayerSketchFill";
    const string FigureOutlineResource = "UI/PlayerSketchOutline";
    const float FigureAspect = 394f / 528f; // baked sprite dimensions
    // Vertical-fill amounts corresponding to the feet/head extents inside the baked texture.
    const float FillMin = 0.045f;
    const float FillMax = 0.954f;

    // Meter bar: band + ruler ticks. Long 360x72 aspect so the vertical bars stay slim at full
    // figure height. Both bars share this sprite; the band starts at MeterBandX0, leaving the zone
    // below it for the bar's icon.
    const int MeterTexW = 360;
    const int MeterTexH = 72;
    const float MeterBandX0 = 52f; // band start; the icon sits in the zone below it (both bars)
    const float MeterBandX1 = 344f;
    const float MeterBandY = 24f;
    const float MeterFillInset = 2.5f;

    // ---------------------------------------------------------------- palette
    static readonly Color Ochre = new(0.79f, 0.64f, 0.24f);      // mid-health step between Moss and Amber
    static readonly Color ChargeYellow = new(0.85f, 0.72f, 0.30f); // flashlight gauge fill
    // Same electric pulse pair the energy-drink buff used on the legacy stamina bar image.
    static readonly Color BoostColorA = new(1f, 0.9f, 0.2f);
    static readonly Color BoostColorB = new(0.35f, 1f, 0.55f);

    static Sprite _figureFillSprite;
    static Sprite _figureOutlineSprite;
    static Sprite _meterTrackSprite;
    static Sprite _meterFillSprite;
    static Sprite _chargeIconSprite;
    static Sprite _staminaIconSprite;

    GameObject _root;
    GameObject _controllerReadoutsRoot; // stamina + charge bars (PlayerController's readouts)
    GameObject _meterRoot;
    Image _bodyGhost;
    Image _bodyFill;
    Image _bodyOutline;
    Image _staminaFill;
    Image _meterFill;

    float _health = 1f;
    float _stamina = 1f;
    bool _boosted;
    float _charge = 1f;
    bool _chargeShown;
    float _damageFlash;

    Color _outlineBaseColor;
    Color _ghostBaseColor;
    Color _staminaBaseColor;

    /// <summary>Shared accessor: PlayerHealth and PlayerController both attach to the same cluster.</summary>
    public static PlayerVitalsHud Ensure(GameObject player)
    {
        var hud = player.GetComponent<PlayerVitalsHud>();
        if (hud == null)
            hud = player.AddComponent<PlayerVitalsHud>();
        return hud;
    }

    void Awake()
    {
        Build();
    }

    void OnDestroy()
    {
        if (_root != null)
            Destroy(_root);
    }

    public void SetHealth(float normalized)
    {
        if (_root == null)
            return;
        _health = Mathf.Clamp01(normalized);
        // Map so 0 sits at the feet and 1 clears the top of the head (texture has glow margins).
        _bodyFill.fillAmount = _health <= 0f ? 0f : Mathf.Lerp(FillMin, FillMax, _health);
        _bodyFill.color = HealthColor(_health);
    }

    public void SetStamina(float normalized, bool boosted)
    {
        if (_root == null)
            return;
        _stamina = Mathf.Clamp01(normalized);
        _boosted = boosted;
        // Vertical bar: remap into the band's span (fill runs bottom→up via the 90° rotation).
        float x0 = (MeterBandX0 + MeterFillInset) / MeterTexW;
        float x1 = (MeterBandX1 - MeterFillInset) / MeterTexW;
        _staminaFill.fillAmount = Mathf.Lerp(x0, x1, _stamina);
    }

    /// <summary>Flashlight gauge: shown only while a flashlight is actually in hand.</summary>
    public void SetFlashlightCharge(bool heldFlashlight, float normalized)
    {
        if (_root == null)
            return;
        _chargeShown = heldFlashlight;
        if (_meterRoot.activeSelf != heldFlashlight)
            _meterRoot.SetActive(heldFlashlight);
        if (!heldFlashlight)
            return;
        _charge = Mathf.Clamp01(normalized);
        // The fill band starts right of the bolt zone, so remap charge into the band's span.
        float x0 = (MeterBandX0 + MeterFillInset) / MeterTexW;
        float x1 = (MeterBandX1 - MeterFillInset) / MeterTexW;
        _meterFill.fillAmount = Mathf.Lerp(x0, x1, _charge);
    }

    /// <summary>Kick the damage flash (outline blushes blood, then decays).</summary>
    public void NotifyDamaged()
    {
        _damageFlash = 1f;
    }

    public void SetHealthVisible(bool visible)
    {
        if (_root != null && _root.activeSelf != visible)
            _root.SetActive(visible);
    }

    public void SetStaminaVisible(bool visible)
    {
        if (_controllerReadoutsRoot != null && _controllerReadoutsRoot.activeSelf != visible)
            _controllerReadoutsRoot.SetActive(visible);
    }

    void Update()
    {
        if (_root == null || !_root.activeInHierarchy)
            return;

        if (_damageFlash > 0f)
            _damageFlash = Mathf.Max(0f, _damageFlash - Time.unscaledDeltaTime * 2.6f);

        // Critical-health heartbeat on the outline; the damage flash rides on top of it.
        float critical = _health < 0.25f ? 1f - _health / 0.25f : 0f;
        float heartbeat = critical > 0f ? (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5.2f)) * critical : 0f;
        float redden = Mathf.Max(_damageFlash, heartbeat * 0.6f);
        _bodyOutline.color = Color.Lerp(_outlineBaseColor, MenuTheme.WithAlpha(MenuTheme.BloodBright, 0.95f), redden);
        _bodyGhost.color = Color.Lerp(_ghostBaseColor, MenuTheme.WithAlpha(MenuTheme.BloodBright, 0.18f), _damageFlash);

        // Stamina bar: electric pulse while boosted, blood pulse when winded.
        Color stam;
        if (_boosted)
        {
            float t = 0.5f + 0.5f * Mathf.Sin(Time.time * 10f);
            stam = MenuTheme.WithAlpha(Color.Lerp(BoostColorA, BoostColorB, t), 0.95f);
        }
        else
        {
            stam = _staminaBaseColor;
            if (_stamina < 0.25f)
            {
                float winded = 1f - _stamina / 0.25f;
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 6f);
                stam = Color.Lerp(stam, MenuTheme.WithAlpha(MenuTheme.BloodBright, 0.9f), winded * pulse);
            }
        }
        _staminaFill.color = stam;

        // Charge gauge: fades toward blood as the battery dies.
        if (_chargeShown)
        {
            Color charge = MenuTheme.WithAlpha(ChargeYellow, 0.95f);
            if (_charge < 0.25f)
            {
                float low = 1f - _charge / 0.25f;
                float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 7f);
                charge = Color.Lerp(charge, MenuTheme.WithAlpha(MenuTheme.BloodBright, 0.95f), low * pulse);
            }
            _meterFill.color = charge;
        }
    }

    // ---------------------------------------------------------------- build

    void Build()
    {
        if (_root != null)
            return;

        Canvas canvas = HudKit.EnsureHudCanvas();

        _root = new GameObject("PlayerVitalsHud", typeof(RectTransform));
        _root.layer = 5;
        var rootRect = (RectTransform)_root.transform;
        rootRect.SetParent(canvas.transform, false);
        rootRect.anchorMin = new Vector2(0f, 1f);
        rootRect.anchorMax = new Vector2(0f, 1f);
        rootRect.pivot = new Vector2(0f, 1f);
        rootRect.anchoredPosition = new Vector2(RootX, RootY);
        rootRect.sizeDelta = new Vector2(244f, 244f);

        // Soft ink halo instead of a hard plate: keeps the readouts legible on bright scenes
        // while letting the gauges float like instrument markings.
        Image backGlow = CreateImage(rootRect, "BackGlow", MenuTheme.SoftGlow());
        var glowRect = backGlow.rectTransform;
        glowRect.anchorMin = new Vector2(0f, 1f);
        glowRect.anchorMax = new Vector2(0f, 1f);
        glowRect.pivot = new Vector2(0.5f, 0.5f);
        glowRect.anchoredPosition = new Vector2(120f, -126f);
        glowRect.sizeDelta = new Vector2(380f, 380f);
        backGlow.color = MenuTheme.WithAlpha(MenuTheme.Ink, 0.62f);

        // ---- controller-owned readouts (stamina arc + charge meter)
        _controllerReadoutsRoot = new GameObject("ControllerReadouts", typeof(RectTransform));
        _controllerReadoutsRoot.layer = 5;
        var controllerRect = (RectTransform)_controllerReadoutsRoot.transform;
        controllerRect.SetParent(rootRect, false);
        controllerRect.anchorMin = Vector2.zero;
        controllerRect.anchorMax = Vector2.one;
        controllerRect.offsetMin = Vector2.zero;
        controllerRect.offsetMax = Vector2.zero;

        // Stamina bar — horizontal along the bottom, running-figure icon.
        _staminaBaseColor = MenuTheme.WithAlpha(MenuTheme.AmberBright, 0.95f);
        _staminaFill = CreateBar(controllerRect, "StaminaBar", StaminaBarPos, StaminaBarRotDeg,
            MeterTrackSprite(), MeterFillSprite(), _staminaBaseColor, out GameObject staminaRoot);
        _staminaFill.fillAmount = (MeterBandX1 - MeterFillInset) / MeterTexW;
        AddBarIcon(staminaRoot, StaminaIconSprite(), StaminaIconPos, StaminaIconSize, StaminaIconRotDeg);

        // Flashlight charge bar — vertical on the left; hidden until a flashlight is in hand.
        _meterFill = CreateBar(controllerRect, "ChargeGauge", ChargeBarPos, ChargeBarRotDeg,
            MeterTrackSprite(), MeterFillSprite(), MenuTheme.WithAlpha(ChargeYellow, 0.95f), out _meterRoot);
        AddBarIcon(_meterRoot, ChargeIconSprite(), ChargeIconPos, ChargeIconSize, ChargeIconRotDeg);

        _meterRoot.SetActive(false);

        // ---- body gauge (health)
        var bodyGo = new GameObject("BodyGauge", typeof(RectTransform));
        bodyGo.layer = 5;
        var bodyRect = (RectTransform)bodyGo.transform;
        bodyRect.SetParent(rootRect, false);
        bodyRect.anchorMin = new Vector2(0f, 1f);
        bodyRect.anchorMax = new Vector2(0f, 1f);
        bodyRect.pivot = new Vector2(0.5f, 1f);
        bodyRect.anchoredPosition = new Vector2(FigureCenterX, FigureTopY);
        bodyRect.sizeDelta = new Vector2(FigureHeight * FigureAspect, FigureHeight);

        _bodyGhost = CreateImage(bodyRect, "Ghost", FigureFillSprite());
        StretchToParent(_bodyGhost.rectTransform);
        _ghostBaseColor = MenuTheme.WithAlpha(MenuTheme.Bone, 0.06f);
        _bodyGhost.color = _ghostBaseColor;

        _bodyFill = CreateImage(bodyRect, "Fill", FigureFillSprite());
        StretchToParent(_bodyFill.rectTransform);
        _bodyFill.type = Image.Type.Filled;
        _bodyFill.fillMethod = Image.FillMethod.Vertical;
        _bodyFill.fillOrigin = (int)Image.OriginVertical.Bottom;
        _bodyFill.fillAmount = FillMax;
        _bodyFill.color = HealthColor(1f);

        _bodyOutline = CreateImage(bodyRect, "Outline", FigureOutlineSprite());
        StretchToParent(_bodyOutline.rectTransform);
        _outlineBaseColor = MenuTheme.WithAlpha(MenuTheme.Bone, 0.85f);
        _bodyOutline.color = _outlineBaseColor;
    }

    static Image CreateImage(RectTransform parent, string name, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        var image = go.AddComponent<Image>();
        image.sprite = sprite;
        image.raycastTarget = false;
        return image;
    }

    static void StretchToParent(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    /// <summary>
    /// Builds one gauge bar (track + Horizontal/Left Filled fill) at the given position and rotation.
    /// Rotation decides the fill read-out: 90° → bottom→top, 0° → left→right. Returns the fill Image;
    /// outputs the bar root for visibility toggling.
    /// </summary>
    Image CreateBar(RectTransform parent, string name, Vector2 pos, float rotDeg, Sprite track, Sprite fill, Color fillColor, out GameObject root)
    {
        root = new GameObject(name, typeof(RectTransform));
        root.layer = 5;
        var rect = (RectTransform)root.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = pos;
        rect.sizeDelta = new Vector2(MeterWidth, MeterWidth * MeterTexH / (float)MeterTexW);
        rect.localRotation = Quaternion.Euler(0f, 0f, rotDeg);

        Image trackImg = CreateImage(rect, "Track", track);
        StretchToParent(trackImg.rectTransform);
        trackImg.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.30f);

        Image fillImg = CreateImage(rect, "Fill", fill);
        StretchToParent(fillImg.rectTransform);
        fillImg.type = Image.Type.Filled;
        fillImg.fillMethod = Image.FillMethod.Horizontal;
        fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
        fillImg.color = fillColor;
        return fillImg;
    }

    /// <summary>Places the bar's icon (position/size/rotation captured from the play-mode layout).</summary>
    Image AddBarIcon(GameObject barRoot, Sprite icon, Vector2 localPos, Vector2 boxSize, float rotDeg)
    {
        Image img = CreateImage((RectTransform)barRoot.transform, "Icon", icon);
        var r = img.rectTransform;
        r.anchorMin = new Vector2(0f, 0.5f);
        r.anchorMax = new Vector2(0f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = localPos;
        r.sizeDelta = boxSize;
        r.localRotation = Quaternion.Euler(0f, 0f, rotDeg);
        img.preserveAspect = true;
        img.color = Color.white;
        return img;
    }

    /// <summary>Moss through 75%+, then ochre → amber → blood toward empty.</summary>
    static Color HealthColor(float t)
    {
        Color c;
        if (t >= 0.75f)
            c = MenuTheme.Moss;
        else if (t >= 0.5f)
            c = Color.Lerp(Ochre, MenuTheme.Moss, (t - 0.5f) / 0.25f);
        else if (t >= 0.25f)
            c = Color.Lerp(MenuTheme.Amber, Ochre, (t - 0.25f) / 0.25f);
        else
            c = Color.Lerp(MenuTheme.Blood, MenuTheme.Amber, t / 0.25f);
        return MenuTheme.WithAlpha(c, 0.66f);
    }

    // ---------------------------------------------------------------- sprite generation

    static Sprite FigureFillSprite() => _figureFillSprite != null ? _figureFillSprite : (_figureFillSprite = Resources.Load<Sprite>(FigureFillResource));
    static Sprite FigureOutlineSprite() => _figureOutlineSprite != null ? _figureOutlineSprite : (_figureOutlineSprite = Resources.Load<Sprite>(FigureOutlineResource));
    static Sprite MeterTrackSprite() => _meterTrackSprite != null ? _meterTrackSprite : (_meterTrackSprite = BuildMeterSprite(true, MeterBandX0, MeterBandX1));
    static Sprite MeterFillSprite() => _meterFillSprite != null ? _meterFillSprite : (_meterFillSprite = BuildMeterSprite(false, MeterBandX0, MeterBandX1));
    static Sprite ChargeIconSprite() => _chargeIconSprite != null ? _chargeIconSprite : (_chargeIconSprite = Resources.Load<Sprite>("UI/ChargeIcon"));
    static Sprite StaminaIconSprite() => _staminaIconSprite != null ? _staminaIconSprite : (_staminaIconSprite = Resources.Load<Sprite>("UI/StaminaIcon"));

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

    /// <summary>Two-octave Perlin jitter that makes SDF edges read as hand-drawn instead of vector-clean.</summary>
    static float Wobble(float x, float y, float amount)
    {
        float n = (Mathf.PerlinNoise(x * 0.045f + 11.7f, y * 0.045f + 3.9f) - 0.5f) * 2f
                + (Mathf.PerlinNoise(x * 0.13f + 4.2f, y * 0.13f + 8.8f) - 0.5f) * 0.9f;
        return n * amount;
    }

    static Sprite BuildMeterSprite(bool track, float bx0, float bx1)
    {
        Vector2 b0 = new(bx0, MeterBandY);
        Vector2 b1 = new(bx1, MeterBandY);

        return MakeSprite(MeterTexW, MeterTexH, (x, y) =>
        {
            var p = new Vector2(x, y);
            if (!track)
            {
                // inner band, inset from the track outline; solid, no bloom halo (glow removed)
                float fd = Capsule(p, new Vector2(bx0 + MeterFillInset, MeterBandY),
                    new Vector2(bx1 - MeterFillInset, MeterBandY), 6f) + Wobble(x, y, 0.7f);
                return Mathf.Clamp01(0.5f - fd / 1.2f);
            }

            // hollow track outline
            float d = Capsule(p, b0, b1, 9f) + Wobble(x, y, 0.9f);
            float band = Mathf.Abs(d) - 1.4f;
            float a = Mathf.Clamp01(0.5f - band / 1.1f);

            // ruler ticks above the band: minor every 10%, tall majors at 0/50/100%
            for (int i = 0; i <= 10; i++)
            {
                bool major = i == 0 || i == 5 || i == 10;
                float tx = Mathf.Lerp(bx0, bx1, i / 10f);
                var t0 = new Vector2(tx, MeterBandY + 12f);
                var t1 = new Vector2(tx, MeterBandY + (major ? 22f : 17f));
                float td = Capsule(p, t0, t1, major ? 1.3f : 0.9f) + Wobble(x + 53f, y + 29f, 0.6f);
                a = Mathf.Max(a, Mathf.Clamp01(0.5f - td / 1.1f));
            }
            return a;
        });
    }

    static float Capsule(Vector2 p, Vector2 a, Vector2 b, float r)
    {
        Vector2 pa = p - a;
        Vector2 ba = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(pa, ba) / Mathf.Max(Vector2.Dot(ba, ba), 1e-5f));
        return (pa - ba * t).magnitude - r;
    }
}
