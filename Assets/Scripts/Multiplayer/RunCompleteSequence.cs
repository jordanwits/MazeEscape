using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Peer-local end of a finished run: once the last exit cab has shut, an "ESCAPED" card goes up over everything with
/// the run's clock, a row of awards (most and least damage taken, most kills, most chased, medic, distance, tickets,
/// deaths) and every player's numbers. The numbers are the server's (<see cref="RunStatsTracker"/>), sent whole by
/// <see cref="ElevatorFinishController"/>; this only draws them.
///
/// The counterpart of <see cref="TeamWipeSequence"/> and built the same way: it owns input while it runs
/// (<see cref="PauseMenuController.BlocksGameplayInput"/> reads <see cref="IsRunning"/>), a client whose host leaves
/// first keeps its card (<see cref="MultiplayerSceneFlow"/> and <see cref="MultiplayerSessionController"/> skip their
/// disconnect handling while this runs), and it shares the wipe card's exit row (<see cref="RunEndExitBar"/>): the
/// host can take everyone back to the lobby still connected, anyone can LEAVE, a solo run gets MAIN MENU. Lives in
/// the gameplay scene, so <see cref="IsRunning"/> clears by itself when the lobby or menu load tears it down.
///
/// Awards need two or more players who stayed to the end; a solo run shows only its own row.
/// On a pad the card is a navigation scope over the exit row, starting on its main action; it has no back.
/// </summary>
[DisallowMultipleComponent]
public sealed class RunCompleteSequence : MonoBehaviour
{
    /// <summary>The lock clack and the cab settling before anything covers the screen.</summary>
    const float DoorsSettleSeconds = 1.0f;
    const float CardFadeInSeconds = 0.9f;
    /// <summary>
    /// Over the HUD (10) and the interaction overlays (30); under the achievement toast (900), because the run's last
    /// awards land at this exact moment. The pause menu (4000) is held shut while this runs.
    /// </summary>
    const int CardSortingOrder = 800;

    // Layout on the 1920x1080 reference canvas: title and clock on top, awards, then the table, the exit row underneath.
    const float TitleTop = 36f;
    const float TitleHeight = 120f;
    const float ClockTop = TitleTop + TitleHeight + 4f;
    /// <summary>Awards and table share the band between the clock and the exit row, centred in it.</summary>
    const float ContentTop = 222f;
    const float ContentBottomMargin = 40f;
    const float ReferenceHeight = 1080f;
    static readonly Vector2 AwardSize = new(330f, 112f);
    const float AwardGap = 20f;
    const int AwardsPerRow = 4;
    const float TableGapBelowAwards = 34f;
    const float TableWidth = 1300f;
    const float TablePadding = 32f;
    const float HeaderHeight = 28f;
    const float RowHeight = 52f;
    const float RowGap = 6f;
    const float ExitBarBottom = 40f;

    readonly struct Column
    {
        public readonly string Header;
        public readonly float Width;

        public Column(string header, float width)
        {
            Header = header;
            Width = width;
        }
    }

    static readonly Column[] Columns =
    {
        new("PLAYER", 300f),
        new(string.Empty, 120f), // outcome tag
        new("DAMAGE", 125f),
        new("DEATHS", 105f),
        new("KILLS", 105f),
        new("CHASED", 110f),
        new("HEALED", 115f),
        new("DISTANCE", 135f),
        new("TICKETS", 115f),
    };

    struct Award
    {
        public string Title;
        public string Caption;
        public string Winner;
        /// <summary>Index into <see cref="MenuTheme.PlayerColors"/>, or -1 for a tie.</summary>
        public int ColorIndex;
        public string Value;
    }

    static RunCompleteSequence s_instance;

    RunSummary _summary;
    CanvasGroup _cardGroup;
    RunEndExitBar _exitBar;
    UiNavScope _navScope;
    Camera _fallbackCamera;

    public static bool IsRunning => s_instance != null;

    /// <summary>Starts this peer's run-complete card once; repeated calls are ignored.</summary>
    public static void Begin(RunSummary summary)
    {
        if (s_instance != null)
            return;

        var go = new GameObject(nameof(RunCompleteSequence));
        s_instance = go.AddComponent<RunCompleteSequence>();
        s_instance._summary = summary;
    }

    void Start()
    {
        StartCoroutine(Run());
    }

    void OnDestroy()
    {
        if (s_instance == this)
            s_instance = null;
        UiEventSystem.Close(_navScope);
    }

    IEnumerator Run()
    {
        yield return new WaitForSecondsRealtime(DoorsSettleSeconds);

        BuildCard();
        for (float t = 0f; t < CardFadeInSeconds; t += Time.unscaledDeltaTime)
        {
            _cardGroup.alpha = Mathf.SmoothStep(0f, 1f, t / CardFadeInSeconds);
            yield return null;
        }
        _cardGroup.alpha = 1f;

        // Clickable only once fully shown, so the press that closed the doors can't land on an exit unseen.
        _cardGroup.blocksRaycasts = true;
        _cardGroup.interactable = true;
        _navScope = new UiNavScope(_cardGroup.transform, () => _exitBar != null ? _exitBar.DefaultButton : null);
        UiEventSystem.Open(_navScope);
    }

    void Update()
    {
        // A client whose host left first is disconnected and NGO destroyed every avatar, the player camera with
        // them; draw a blank backdrop so the card does not sit on an uncleared screen.
        if (_cardGroup != null && _fallbackCamera == null && Camera.allCamerasCount == 0)
        {
            var go = new GameObject("RunCompleteBackdropCamera");
            go.transform.SetParent(transform, false);
            _fallbackCamera = go.AddComponent<Camera>();
            _fallbackCamera.clearFlags = CameraClearFlags.SolidColor;
            _fallbackCamera.backgroundColor = MenuTheme.Ink;
            _fallbackCamera.cullingMask = 0;
        }
    }

    // ---- building ---------------------------------------------------------------------------------------

    void BuildCard()
    {
        HudKit.SetHudCanvasVisible(false);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        MenuTheme.ApplyCursor();
        UiEventSystem.Ensure();

        Canvas canvas = MenuWidgets.CreateCanvas("RunCompleteCard", CardSortingOrder, transform);
        _cardGroup = canvas.gameObject.AddComponent<CanvasGroup>();
        _cardGroup.alpha = 0f;
        _cardGroup.blocksRaycasts = false;
        _cardGroup.interactable = false;

        Image dim = MenuWidgets.CreateImage(canvas.transform, "Dim", MenuTheme.Solid(),
            MenuTheme.WithAlpha(MenuTheme.Ink, 0.92f));
        dim.rectTransform.SetStretch();

        Image vignette = MenuWidgets.CreateImage(canvas.transform, "Vignette", MenuTheme.Vignette(), MenuTheme.Ink);
        vignette.rectTransform.SetStretch();

        BuildTitle(canvas.transform);
        BuildClock(canvas.transform);

        RunSummary.Player[] players = _summary.Players ?? Array.Empty<RunSummary.Player>();
        List<Award> awards = PickAwards(players);
        int awardRows = (awards.Count + AwardsPerRow - 1) / AwardsPerRow;
        float awardsHeight = awardRows > 0
            ? awardRows * AwardSize.y + (awardRows - 1) * AwardGap + TableGapBelowAwards
            : 0f;
        float tableHeight = TableHeight(players.Length);

        float bandBottom = ReferenceHeight - (ExitBarBottom + RunEndExitBar.BarHeight + ContentBottomMargin);
        float top = ContentTop + Mathf.Max(0f, (bandBottom - ContentTop - awardsHeight - tableHeight) * 0.5f);
        if (awardRows > 0)
            BuildAwards(canvas.transform, awards, top);

        BuildTable(canvas.transform, players, top + awardsHeight);
        _exitBar = RunEndExitBar.Create(canvas.transform, ExitBarBottom);
    }

    void BuildTitle(Transform parent)
    {
        RectTransform block = TopCentered(MenuWidgets.CreateRect("Title", parent), TitleTop, new Vector2(1200f, TitleHeight));
        block.localRotation = Quaternion.Euler(0f, 0f, -0.6f);

        // Same misprint treatment as the pause title: an amber copy offset behind the bone one.
        TextMeshProUGUI misprint = MenuWidgets.CreateText(block, "TitleMisprint", "ESCAPED", 104f,
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.85f), MenuWidgets.FontKind.Display,
            TextAlignmentOptions.Center, 8f, FontStyles.Bold);
        misprint.rectTransform.SetStretch();
        misprint.rectTransform.anchoredPosition = new Vector2(7f, -5f);
        misprint.textWrappingMode = TextWrappingModes.NoWrap;

        TextMeshProUGUI title = MenuWidgets.CreateText(block, "Title", "ESCAPED", 104f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 8f, FontStyles.Bold);
        title.rectTransform.SetStretch();
        title.textWrappingMode = TextWrappingModes.NoWrap;
    }

    /// <summary>"RUN 15:43 · LEVEL01 4:12 · LEVEL02 6:30 · LEVEL03 5:01" — the run total, then each section.</summary>
    void BuildClock(Transform parent)
    {
        string[] scenes = _summary.SectionScenes;
        float[] seconds = _summary.SectionSeconds;
        if (scenes == null || seconds == null || scenes.Length == 0)
            return;

        string bone = ColorUtility.ToHtmlStringRGB(MenuTheme.Bone);
        string faint = ColorUtility.ToHtmlStringRGB(MenuTheme.Faint);
        var text = new System.Text.StringBuilder();
        text.Append("<color=#").Append(bone).Append(">RUN ").Append(FormatTime(_summary.TotalSeconds)).Append("</color>");
        for (int i = 0; i < scenes.Length && i < seconds.Length; i++)
        {
            text.Append("   <color=#").Append(faint).Append(">·</color>   ");
            text.Append(scenes[i].ToUpperInvariant()).Append(' ').Append(FormatTime(seconds[i]));
        }

        TextMeshProUGUI clock = MenuWidgets.CreateText(parent, "Clock", text.ToString(), 20f, MenuTheme.Mist,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 6f);
        clock.textWrappingMode = TextWrappingModes.NoWrap;
        TopCentered(clock.rectTransform, ClockTop, new Vector2(1400f, 30f));
    }

    // ---- awards -----------------------------------------------------------------------------------------

    static List<Award> PickAwards(RunSummary.Player[] players)
    {
        var awards = new List<Award>();
        var eligible = new List<RunSummary.Player>();
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i].Outcome != RunSummary.Outcome.Left)
                eligible.Add(players[i]);
        }

        if (eligible.Count < 2)
            return awards;

        // Damage needs a spread, or "most" and "least" would name the same people.
        TryAdd(awards, eligible, "PUNCHING BAG", "MOST DAMAGE TAKEN", p => Mathf.Round(p.DamageTaken), most: true,
            requireSpread: true, v => FormatHp(v));
        TryAdd(awards, eligible, "UNTOUCHABLE", "LEAST DAMAGE TAKEN", p => Mathf.Round(p.DamageTaken), most: false,
            requireSpread: true, v => FormatHp(v));
        TryAdd(awards, eligible, "EXTERMINATOR", "MOST KILLS", p => p.Kills, most: true, requireSpread: false,
            v => Count(v, "KILL"));
        TryAdd(awards, eligible, "BAIT", "MOST CHASED", p => p.Chases, most: true, requireSpread: false,
            v => Count(v, "CHASE"));
        TryAdd(awards, eligible, "MEDIC", "HP HEALED ON TEAMMATES", p => Mathf.Round(p.HealingGiven), most: true,
            requireSpread: false, v => FormatHp(v));
        TryAdd(awards, eligible, "MARATHON", "FARTHEST TRAVELLED", p => Mathf.Round(p.DistanceMeters), most: true,
            requireSpread: false, FormatDistance);
        TryAdd(awards, eligible, "JACKPOT", "MOST TICKETS EARNED", p => p.Tickets, most: true, requireSpread: false,
            v => Count(v, "TICKET"));
        TryAdd(awards, eligible, "TOUGH LUCK", "MOST DEATHS", p => p.Deaths, most: true, requireSpread: false,
            v => Count(v, "DEATH"));
        return awards;
    }

    /// <summary>
    /// The best (or, with <paramref name="most"/> off, the lowest) value among <paramref name="players"/>. A most-award
    /// needs a value above zero; ties share the award.
    /// </summary>
    static void TryAdd(List<Award> awards, List<RunSummary.Player> players, string title, string caption,
        Func<RunSummary.Player, float> value, bool most, bool requireSpread, Func<float, string> format)
    {
        float best = value(players[0]);
        float worst = best;
        for (int i = 1; i < players.Count; i++)
        {
            float v = value(players[i]);
            best = most ? Mathf.Max(best, v) : Mathf.Min(best, v);
            worst = most ? Mathf.Min(worst, v) : Mathf.Max(worst, v);
        }

        if (requireSpread && Mathf.Approximately(best, worst))
            return;
        if (most && best <= 0f)
            return;

        var winners = new List<RunSummary.Player>();
        for (int i = 0; i < players.Count; i++)
        {
            if (Mathf.Approximately(value(players[i]), best))
                winners.Add(players[i]);
        }

        var names = new System.Text.StringBuilder();
        for (int i = 0; i < winners.Count; i++)
        {
            if (i > 0)
                names.Append(" & ");
            names.Append(winners[i].Name);
        }

        awards.Add(new Award
        {
            Title = title,
            Caption = caption,
            Winner = names.ToString(),
            ColorIndex = winners.Count == 1 ? winners[0].ColorIndex : -1,
            Value = format(best),
        });
    }

    /// <summary>Lays the awards out in centred rows of up to four, the first row <paramref name="blockTop"/> from the top.</summary>
    static void BuildAwards(Transform parent, List<Award> awards, float blockTop)
    {
        int rows = (awards.Count + AwardsPerRow - 1) / AwardsPerRow;
        for (int row = 0; row < rows; row++)
        {
            int first = row * AwardsPerRow;
            int count = Mathf.Min(AwardsPerRow, awards.Count - first);
            float rowWidth = count * AwardSize.x + (count - 1) * AwardGap;
            float top = blockTop + row * (AwardSize.y + AwardGap);
            for (int i = 0; i < count; i++)
            {
                RectTransform tile = BuildAwardTile(parent, awards[first + i], first + i);
                tile.anchorMin = new Vector2(0.5f, 1f);
                tile.anchorMax = new Vector2(0.5f, 1f);
                tile.pivot = new Vector2(0f, 1f);
                tile.anchoredPosition = new Vector2(-rowWidth * 0.5f + i * (AwardSize.x + AwardGap), -top);
                tile.sizeDelta = AwardSize;
            }
        }
    }

    static RectTransform BuildAwardTile(Transform parent, Award award, int index)
    {
        RectTransform tile = MenuWidgets.CreateRect("Award_" + index, parent);
        Color accent = award.ColorIndex >= 0 ? PlayerColor(award.ColorIndex) : MenuTheme.Bone;
        BuildPanel(tile, 18, MenuTheme.WithAlpha(MenuTheme.Panel, 0.95f), MenuTheme.WithAlpha(MenuTheme.Bone, 0.13f));

        Image strip = MenuWidgets.CreateImage(tile, "Accent", MenuTheme.RoundedRect(3), accent);
        RectTransform stripRt = strip.rectTransform;
        stripRt.anchorMin = new Vector2(0f, 0f);
        stripRt.anchorMax = new Vector2(0f, 1f);
        stripRt.pivot = new Vector2(0f, 0.5f);
        stripRt.anchoredPosition = new Vector2(12f, 0f);
        stripRt.sizeDelta = new Vector2(5f, -28f);

        TextMeshProUGUI title = MenuWidgets.CreateText(tile, "Title", award.Title, 22f, MenuTheme.AmberBright,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.TopLeft, 5f, FontStyles.Bold);
        Place(title.rectTransform, new Vector2(30f, 14f), new Vector2(-22f, 28f));
        title.textWrappingMode = TextWrappingModes.NoWrap;

        TextMeshProUGUI caption = MenuWidgets.CreateText(tile, "Caption", award.Caption, 12f, MenuTheme.Mist,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.TopLeft, 4f);
        Place(caption.rectTransform, new Vector2(30f, 44f), new Vector2(-22f, 18f));
        caption.textWrappingMode = TextWrappingModes.NoWrap;

        TextMeshProUGUI winner = MenuWidgets.CreateText(tile, "Winner", award.Winner, 22f, accent,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.BottomLeft, 2f);
        Place(winner.rectTransform, new Vector2(30f, 72f), new Vector2(-130f, 28f));
        winner.textWrappingMode = TextWrappingModes.NoWrap;
        winner.overflowMode = TextOverflowModes.Ellipsis;

        TextMeshProUGUI value = MenuWidgets.CreateText(tile, "Value", award.Value, 22f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.BottomRight, 2f);
        Place(value.rectTransform, new Vector2(AwardSize.x - 124f, 72f), new Vector2(-22f, 28f));
        value.textWrappingMode = TextWrappingModes.NoWrap;

        return tile;
    }

    // ---- table ------------------------------------------------------------------------------------------

    static float TableHeight(int rows) => HeaderHeight + rows * (RowHeight + RowGap);

    void BuildTable(Transform parent, RunSummary.Player[] players, float top)
    {
        RectTransform table = TopCentered(MenuWidgets.CreateRect("Table", parent), top,
            new Vector2(TableWidth, TableHeight(players.Length)));

        RectTransform header = MenuWidgets.CreateRect("Header", table);
        RowRect(header, 0f, HeaderHeight);
        float x = TablePadding;
        for (int c = 0; c < Columns.Length; c++)
        {
            if (!string.IsNullOrEmpty(Columns[c].Header))
            {
                TextMeshProUGUI label = MenuWidgets.CreateText(header, "H_" + Columns[c].Header, Columns[c].Header,
                    13f, MenuTheme.Mist, MenuWidgets.FontKind.Display,
                    c == 0 ? TextAlignmentOptions.MidlineLeft : TextAlignmentOptions.Midline, 5f);
                Cell(label.rectTransform, c == 0 ? x + 22f : x, Columns[c].Width);
            }

            x += Columns[c].Width;
        }

        ulong localId = LocalClientId();
        for (int i = 0; i < players.Length; i++)
            BuildRow(table, players[i], HeaderHeight + RowGap + i * (RowHeight + RowGap), players[i].ClientId == localId);
    }

    static void BuildRow(RectTransform table, RunSummary.Player p, float top, bool isLocal)
    {
        RectTransform row = MenuWidgets.CreateRect("Row_" + p.Name, table);
        RowRect(row, top, RowHeight);
        BuildPanel(row, 12,
            isLocal ? MenuTheme.WithAlpha(MenuTheme.PanelRaised, 0.95f) : MenuTheme.WithAlpha(MenuTheme.Panel, 0.85f),
            isLocal ? MenuTheme.WithAlpha(MenuTheme.Amber, 0.55f) : MenuTheme.WithAlpha(MenuTheme.Bone, 0.08f),
            shadow: false);

        float x = TablePadding;

        Image swatch = MenuWidgets.CreateImage(row, "Swatch", MenuTheme.RoundedRect(3), PlayerColor(p.ColorIndex));
        RectTransform swatchRt = swatch.rectTransform;
        swatchRt.anchorMin = new Vector2(0f, 0.5f);
        swatchRt.anchorMax = new Vector2(0f, 0.5f);
        swatchRt.pivot = new Vector2(0f, 0.5f);
        swatchRt.anchoredPosition = new Vector2(x, 0f);
        swatchRt.sizeDelta = new Vector2(6f, 28f);

        TextMeshProUGUI name = MenuWidgets.CreateText(row, "Name", p.Name, 22f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 2f);
        name.textWrappingMode = TextWrappingModes.NoWrap;
        name.overflowMode = TextOverflowModes.Ellipsis;
        Cell(name.rectTransform, x + 22f, Columns[0].Width - 30f);
        x += Columns[0].Width;

        OutcomeTag(p.Outcome, out string tagText, out Color tagColor);
        TextMeshProUGUI tag = MenuWidgets.CreateText(row, "Outcome", tagText, 15f, tagColor,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Midline, 5f);
        Cell(tag.rectTransform, x, Columns[1].Width);
        x += Columns[1].Width;

        string[] values =
        {
            Mathf.RoundToInt(p.DamageTaken).ToString(CultureInfo.InvariantCulture),
            p.Deaths.ToString(CultureInfo.InvariantCulture),
            p.Kills.ToString(CultureInfo.InvariantCulture),
            p.Chases.ToString(CultureInfo.InvariantCulture),
            Mathf.RoundToInt(p.HealingGiven).ToString(CultureInfo.InvariantCulture),
            FormatDistance(p.DistanceMeters),
            p.Tickets.ToString(CultureInfo.InvariantCulture),
        };
        bool[] zero =
        {
            Mathf.RoundToInt(p.DamageTaken) == 0, p.Deaths == 0, p.Kills == 0, p.Chases == 0,
            Mathf.RoundToInt(p.HealingGiven) == 0, p.DistanceMeters < 0.5f, p.Tickets == 0,
        };

        for (int v = 0; v < values.Length; v++)
        {
            int c = v + 2;
            TextMeshProUGUI cell = MenuWidgets.CreateText(row, Columns[c].Header, values[v], 22f,
                zero[v] ? MenuTheme.Faint : MenuTheme.Bone, MenuWidgets.FontKind.Display,
                TextAlignmentOptions.Midline, 1f);
            cell.textWrappingMode = TextWrappingModes.NoWrap;
            Cell(cell.rectTransform, x, Columns[c].Width);
            x += Columns[c].Width;
        }
    }

    static void OutcomeTag(RunSummary.Outcome outcome, out string text, out Color color)
    {
        switch (outcome)
        {
            case RunSummary.Outcome.Escaped:
                text = "ESCAPED";
                color = MenuTheme.Moss;
                return;
            case RunSummary.Outcome.Died:
                text = "DIED";
                color = MenuTheme.BloodBright;
                return;
            default:
                text = "LEFT";
                color = MenuTheme.Faint;
                return;
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    /// <summary>A smooth rounded board (cards stay smooth; the hand-cut edge is for things you click).</summary>
    static void BuildPanel(RectTransform rt, int radius, Color fill, Color outline, bool shadow = true)
    {
        if (shadow)
        {
            Image shade = MenuWidgets.CreateImage(rt, "Shadow", MenuTheme.RoundedShadow(radius, 20),
                MenuTheme.WithAlpha(MenuTheme.Ink, 0.5f));
            shade.rectTransform.SetStretch();
            shade.rectTransform.offsetMin = new Vector2(-12f, -18f);
            shade.rectTransform.offsetMax = new Vector2(12f, 8f);
        }

        Image bg = MenuWidgets.CreateRoundedMaskedFill(rt, "Bg", radius, fill);
        MenuWidgets.CreateGrunge(bg.transform, MenuTheme.WithAlpha(Color.white, 0.035f));

        Image frame = MenuWidgets.CreateImage(rt, "Outline", MenuTheme.RoundedOutline(radius, 1.6f), outline);
        frame.rectTransform.SetStretch();
    }

    static RectTransform TopCentered(RectTransform rt, float top, Vector2 size)
    {
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -top);
        rt.sizeDelta = size;
        return rt;
    }

    /// <summary>A full-width strip of the table, <paramref name="top"/> pixels below its top edge.</summary>
    static void RowRect(RectTransform rt, float top, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -top);
        rt.sizeDelta = new Vector2(0f, height);
    }

    /// <summary>A column-wide, row-tall cell starting <paramref name="x"/> pixels from the row's left edge.</summary>
    static void Cell(RectTransform rt, float x, float width)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(x, 0f);
        rt.sizeDelta = new Vector2(width, 0f);
    }

    /// <summary>
    /// Pins a text rect inside a tile: <paramref name="topLeft"/> is the inset from the tile's top-left corner, and
    /// <paramref name="rightAndHeight"/> holds the right edge (negative = inset from the tile's right) and the height.
    /// </summary>
    static void Place(RectTransform rt, Vector2 topLeft, Vector2 rightAndHeight)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.offsetMin = new Vector2(topLeft.x, -topLeft.y - rightAndHeight.y);
        rt.offsetMax = new Vector2(rightAndHeight.x, -topLeft.y);
    }

    static Color PlayerColor(int index)
    {
        Color[] palette = MenuTheme.PlayerColors;
        return palette[Mathf.Abs(index) % palette.Length];
    }

    static ulong LocalClientId()
    {
        NetworkManager nm = NetworkManager.Singleton;
        return nm != null && nm.IsListening ? nm.LocalClientId : 0ul;
    }

    static string FormatTime(float seconds)
    {
        int total = Mathf.Max(0, Mathf.RoundToInt(seconds));
        int h = total / 3600;
        int m = total / 60 % 60;
        int s = total % 60;
        return h > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s)
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, s);
    }

    static string FormatDistance(float meters)
    {
        return meters >= 1000f
            ? (meters / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " KM"
            : Mathf.RoundToInt(meters).ToString(CultureInfo.InvariantCulture) + " M";
    }

    static string FormatHp(float hp) => Mathf.RoundToInt(hp).ToString(CultureInfo.InvariantCulture) + " HP";

    static string Count(float value, string noun)
    {
        int n = Mathf.RoundToInt(value);
        return n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? string.Empty : "S");
    }
}
