using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Local-player shop overlay for the carnival prize counter (<see cref="CarnivalStore"/>). While shown it frees
/// the cursor and freezes movement via <see cref="IsInteractive"/> (PlayerController ORs this into its
/// gameplay-input gate, exactly like <see cref="BlackjackOverlayController"/> / <see cref="SkeletonRpsOverlayController"/>).
/// The first-person camera stays live — the player keeps looking at the booth; only the shop panel is an overlay.
///
/// The goods are a grid of icon cards (the item's own HUD hotbar icon, so the shelf reads at a glance), in a
/// scroll view that only starts scrolling once the stock outgrows <see cref="MaxGridHeight"/>. Clicking a card
/// buys it. Cards stay legible whether or not you can afford them — the price colour carries affordability,
/// rather than fading the tile out to 35% and hiding the price.
///
/// The panel is deliberately wordless: there is no status line and the server sends no result back. Every
/// outcome is read off the world instead — the ticket balance drops, the goods appear on the counter behind
/// you, an owned one-per-player row flips to OWNED. A refused purchase is simply nothing happening.
///
/// The ticket balance tracks <see cref="NetworkPlayerCarnivalTickets"/> live, so a teammate's payout landing
/// mid-browse unlocks the shelf without reopening. A single instance is created on demand and persists
/// (DontDestroyOnLoad) so its canvas can't be torn down by scene churn.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalStoreOverlayController : MonoBehaviour
{
    /// <summary>True while the overlay is shown — PlayerController ORs this into its input-block + cursor-lock gate.</summary>
    public static bool IsInteractive { get; private set; }

    static CarnivalStoreOverlayController _instance;

    /// <summary>Slack on the "walked away" auto-close so a small shuffle at the counter doesn't shut the shop.</summary>
    const float RangeClosePollSlack = 1.2f;
    /// <summary>Per-card lock after a click, so a double-click doesn't quietly buy two flare guns.</summary>
    const float BuyCooldownSeconds = 0.4f;

    sealed class StockTile
    {
        public int Index;
        public Button Button;
        public TMP_Text PriceText;
        public Image PriceChip;
        public int Price;
        public float LockedUntil;
        /// <summary>Tri-state so the first refresh always writes the price colour (-1 = never styled).</summary>
        public int ShownAffordable = -1;
        /// <summary>Tri-state mirror of "already owned", so the OWNED swap only runs on a real change.</summary>
        public int ShownOwned = -1;
        /// <summary>Overlays the price with OWNED once a one-per-player row has been bought.</summary>
        public TMP_Text OwnedText;
    }

    PlayerController _player;
    NetworkPlayerCarnivalTickets _wallet;
    CarnivalStore _store;

    GameObject _root;
    CanvasGroup _canvasGroup;
    Canvas _canvas;
    TMP_Text _balanceValue;
    StockTile[] _tiles;

    bool _shown;
    int _lastDisplayedBalance = -1;

    public static void Show(PlayerController player, CarnivalStore store)
    {
        if (player == null || store == null)
            return;
        EnsureInstance().Bind(player, store);
    }

    static CarnivalStoreOverlayController EnsureInstance()
    {
        if (_instance == null)
        {
            GameObject go = new("CarnivalStoreOverlay");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<CarnivalStoreOverlayController>();
        }
        return _instance;
    }

    void Bind(PlayerController player, CarnivalStore store)
    {
        _player = player;
        _wallet = player.GetComponent<NetworkPlayerCarnivalTickets>();

        // The grid is built from the counter's stock, so a different counter needs a fresh panel. Deactivate
        // before destroying: Destroy is deferred to end of frame, so a still-active old panel would draw over
        // the new one for a frame.
        if (_store != store && _root != null)
        {
            _root.SetActive(false);
            Destroy(_root);
            _root = null;
            _tiles = null;
        }
        _store = store;

        EnsureUiBuilt();
        _lastDisplayedBalance = -1;
        Refresh();
        SetShown(true);
    }

    void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
        if (_shown)
            IsInteractive = false;
    }

    void Update()
    {
        if (!_shown)
            return;

        // Close on anything that takes normal standing control away (death, ragdoll, Jailor carry, level change),
        // or when the player is no longer at the counter.
        if (_player == null || _store == null || !_player.HasNormalInteractiveControl
            || !_store.isActiveAndEnabled
            || !_store.IsInInteractRange(_player.transform.position, RangeClosePollSlack))
        {
            SetShown(false);
            return;
        }

        // Cursor stays free while shown; yield raycasts to the pause menu when it is open.
        if (!PauseMenuController.BlocksGameplayInput)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (_canvasGroup != null)
            _canvasGroup.blocksRaycasts = !PauseMenuController.BlocksGameplayInput;

        Refresh();
    }

    void SetShown(bool show)
    {
        if (_root != null)
            _root.SetActive(show);
        if (show == _shown)
            return;

        _shown = show;
        IsInteractive = show;

        if (show)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            MenuTheme.ApplyCursor();
        }
        else
        {
            _store = null;
            _player = null;
            _wallet = null;
            if (!PauseMenuController.BlocksGameplayInput)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }

    // =========================================================================================
    // Refresh
    // =========================================================================================

    int Balance => _wallet != null ? _wallet.TicketCount : 0;

    void Refresh()
    {
        if (_root == null || _tiles == null)
            return;

        int balance = Balance;
        if (balance != _lastDisplayedBalance)
        {
            _lastDisplayedBalance = balance;
            _balanceValue.text = balance.ToString();
        }

        float now = Time.unscaledTime;
        for (int i = 0; i < _tiles.Length; i++)
        {
            StockTile tile = _tiles[i];

            // An owned one-per-player row is the only card that genuinely stops being clickable — everything
            // else stays lit even when unaffordable so its price is readable.
            bool owned = _store.IsAlreadyOwnedBy(_player, tile.Index);
            int ownedFlag = owned ? 1 : 0;
            if (tile.ShownOwned != ownedFlag)
            {
                tile.ShownOwned = ownedFlag;
                if (tile.OwnedText != null)
                    tile.OwnedText.gameObject.SetActive(owned);
                tile.PriceText.gameObject.SetActive(!owned);
                tile.PriceChip.gameObject.SetActive(!owned);
            }

            tile.Button.interactable = !owned && now >= tile.LockedUntil;
            if (owned)
                continue;

            int affordableFlag = balance >= tile.Price ? 1 : 0;
            if (tile.ShownAffordable != affordableFlag)
            {
                tile.ShownAffordable = affordableFlag;
                Color c = affordableFlag == 1 ? MenuTheme.AmberBright : MenuTheme.WithAlpha(MenuTheme.Faint, 0.95f);
                tile.PriceText.color = c;
                tile.PriceChip.color = MenuTheme.WithAlpha(c, affordableFlag == 1 ? 0.92f : 0.55f);
            }
        }
    }

    void OnBuy(int index)
    {
        if (_store == null || _player == null || _tiles == null || index < 0 || index >= _tiles.Length)
            return;

        StockTile tile = _tiles[index];
        if (Time.unscaledTime < tile.LockedUntil)
            return;
        tile.LockedUntil = Time.unscaledTime + BuyCooldownSeconds;

        // These two are silent guards, not messages: they stop a pointless request going to the server. The
        // player reads the outcome off the panel itself — the ticket balance drops, an owned row flips to
        // OWNED, and the goods appear on the counter behind them.
        if (_store.IsAlreadyOwnedBy(_player, index))
            return;

        if (Balance < tile.Price)
            return;

        _store.RequestPurchase(_player, index);
    }

    void OnClose() => SetShown(false);

    // =========================================================================================
    // UI construction (plate language; the cards mirror the lobby's character-select plates)
    // =========================================================================================

    const int Columns = 4;
    const float CellWidth = 190f;
    const float CellHeight = 214f;
    const float CellSpacing = 16f;
    const float PanelPadding = 34f;
    const float PanelWidth = Columns * CellWidth + (Columns - 1) * CellSpacing + 2f * PanelPadding;
    const float HeaderHeight = 118f;
    const float FooterHeight = 96f;
    /// <summary>Grid viewport cap — past this the shelf scrolls instead of growing the panel off-screen.</summary>
    const float MaxGridHeight = 640f;
    const int CardRadius = 14;

    void EnsureUiBuilt()
    {
        if (_root != null)
            return;

        EnsureEventSystem();
        if (_canvas == null)
            _canvas = CreateOwnedCanvas();

        int count = _store != null ? _store.StockCount : 0;
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)Columns));
        float neededGridHeight = rows * CellHeight + (rows - 1) * CellSpacing;
        float gridHeight = Mathf.Min(neededGridHeight, MaxGridHeight);
        float height = HeaderHeight + gridHeight + FooterHeight;

        _root = new GameObject("CarnivalStorePanel");
        _root.layer = 5;
        _root.transform.SetParent(_canvas.transform, false);
        RectTransform rootRt = _root.AddComponent<RectTransform>();
        rootRt.anchorMin = rootRt.anchorMax = rootRt.pivot = new Vector2(0.5f, 0.5f);
        rootRt.anchoredPosition = Vector2.zero;
        rootRt.sizeDelta = new Vector2(PanelWidth, height);
        _canvasGroup = _root.AddComponent<CanvasGroup>();

        Image bg = _root.AddComponent<Image>();
        bg.sprite = MenuTheme.RoundedRect(3);
        bg.type = Image.Type.Sliced;
        bg.color = MenuTheme.WithAlpha(MenuTheme.Panel, 0.97f);
        MenuWidgets.CreateGrunge(_root.transform, MenuTheme.WithAlpha(Color.white, 0.05f));
        Image frame = MenuWidgets.CreateImage(_root.transform, "Frame", MenuTheme.RoundedOutline(3, 1.6f),
            MenuTheme.WithAlpha(MenuTheme.Bone, 0.20f));
        frame.rectTransform.SetStretch();
        MenuWidgets.CreateCornerBrackets(rootRt, MenuTheme.WithAlpha(MenuTheme.Bone, 0.55f));

        BuildHeader();
        BuildGrid(count, gridHeight, neededGridHeight > gridHeight + 0.5f);
        BuildFooter();

        _root.SetActive(false);
    }

    void BuildHeader()
    {
        TMP_Text title = MakeLabel(_root.transform, "PRIZE COUNTER", 26f,
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.95f), TextAlignmentOptions.MidlineLeft,
            new Vector2(0f, 1f), new Vector2(PanelPadding, -18f), new Vector2(460f, 34f));
        title.characterSpacing = 6f;

        MakeLabel(_root.transform, "TICKETS BUY PRIZES — PRIZES KEEP YOU ALIVE", 15f, MenuTheme.Mist,
            TextAlignmentOptions.MidlineLeft, new Vector2(0f, 1f), new Vector2(PanelPadding, -54f),
            new Vector2(560f, 24f)).characterSpacing = 3f;

        MakeLabel(_root.transform, "TICKETS", 15f, MenuTheme.Mist, TextAlignmentOptions.MidlineRight,
            new Vector2(1f, 1f), new Vector2(-PanelPadding, -18f), new Vector2(220f, 22f)).characterSpacing = 5f;
        _balanceValue = MakeLabel(_root.transform, "0", 40f, MenuTheme.AmberBright, TextAlignmentOptions.MidlineRight,
            new Vector2(1f, 1f), new Vector2(-PanelPadding, -46f), new Vector2(220f, 46f));

        Image rule = MenuWidgets.CreateImage(_root.transform, "HeaderRule", MenuTheme.Solid(),
            MenuTheme.WithAlpha(MenuTheme.Bone, 0.16f));
        RectTransform ruleRt = rule.rectTransform;
        ruleRt.anchorMin = ruleRt.anchorMax = ruleRt.pivot = new Vector2(0.5f, 1f);
        ruleRt.anchoredPosition = new Vector2(0f, -HeaderHeight + 10f);
        ruleRt.sizeDelta = new Vector2(PanelWidth - 2f * PanelPadding, 1.5f);
    }

    /// <summary>
    /// The shelf: a house scroll view whose content holds one <see cref="GridLayoutGroup"/>. The scroll view is
    /// re-anchored by hand because <see cref="MenuWidgets.CreateScrollView"/> is authored for a
    /// VerticalLayoutGroup parent (its LayoutElement, and the height clamp that drives it, are inert here — the
    /// viewport height is fixed by the caller instead, and only the content scrolls).
    /// </summary>
    void BuildGrid(int count, float gridHeight, bool scrolls)
    {
        RectTransform content = MenuWidgets.CreateScrollView(_root.transform, gridHeight);
        RectTransform scrollRoot = (RectTransform)content.parent.parent;
        scrollRoot.anchorMin = scrollRoot.anchorMax = scrollRoot.pivot = new Vector2(0.5f, 1f);
        scrollRoot.anchoredPosition = new Vector2(0f, -HeaderHeight);
        scrollRoot.sizeDelta = new Vector2(PanelWidth - 2f * PanelPadding, gridHeight);

        // The viewport height is sized to the stock, so a shelf that fits has nothing to scroll. Switch the bar
        // off outright rather than leaning on ScrollRect's AutoHide, which would still leave its track drawn
        // until the first layout pass runs.
        Transform scrollbar = scrollRoot.Find("Scrollbar");
        if (scrollbar != null)
            scrollbar.gameObject.SetActive(scrolls);

        RectTransform gridHolder = MenuWidgets.CreateRect("Grid", content);
        var grid = gridHolder.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(CellWidth, CellHeight);
        grid.spacing = new Vector2(CellSpacing, CellSpacing);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = Columns;
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperCenter;

        _tiles = new StockTile[count];
        for (int i = 0; i < count; i++)
            _tiles[i] = BuildCard(gridHolder, i);
    }

    StockTile BuildCard(RectTransform gridHolder, int index)
    {
        _store.TryGetStock(index, out CarnivalStoreStockEntry entry);

        // No LayoutElement: the grid drives the cell size.
        RectTransform cardRoot = MenuWidgets.CreateRect("Item_" + index, gridHolder);
        RectTransform plate = MenuWidgets.CreateStretched("Plate", cardRoot);

        Image shadow = MenuWidgets.CreateImage(plate, "Shadow", MenuTheme.RoundedShadow(CardRadius, 16),
            MenuTheme.WithAlpha(MenuTheme.Ink, 0.5f));
        shadow.rectTransform.SetStretch();
        shadow.rectTransform.offsetMin = new Vector2(-8f, -12f);
        shadow.rectTransform.offsetMax = new Vector2(8f, 4f);

        // raycastTarget: this is the card's click surface, and MenuButtonFx on the root reads its pointer events.
        Image cardBg = MenuWidgets.CreateImage(plate, "Bg", MenuTheme.RoundedRect(CardRadius),
            MenuTheme.WithAlpha(MenuTheme.Tile, 0.92f), true);
        cardBg.rectTransform.SetStretch();

        Image icon = MenuWidgets.CreateImage(plate, "Icon", _store.GetIcon(index), Color.white);
        icon.type = Image.Type.Simple;
        icon.preserveAspect = true;
        RectTransform iconRt = icon.rectTransform;
        iconRt.anchorMin = Vector2.zero;
        iconRt.anchorMax = Vector2.one;
        iconRt.offsetMin = new Vector2(24f, 78f);
        iconRt.offsetMax = new Vector2(-24f, -18f);

        // Gradient band so the name/price sit on ink rather than on the icon's tail.
        Image shade = MenuWidgets.CreateImage(plate, "Shade", MenuTheme.VerticalGradient(),
            MenuTheme.WithAlpha(MenuTheme.Ink, 0.9f));
        RectTransform shadeRt = shade.rectTransform;
        shadeRt.anchorMin = new Vector2(0f, 0f);
        shadeRt.anchorMax = new Vector2(1f, 0f);
        shadeRt.pivot = new Vector2(0.5f, 0f);
        shadeRt.anchoredPosition = new Vector2(0f, 3f);
        shadeRt.sizeDelta = new Vector2(-6f, 78f);

        TMP_Text name = MenuWidgets.CreateText(plate, "Name", _store.GetDisplayName(index).ToUpperInvariant(),
            15f, MenuTheme.Bone, MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 2f);
        RectTransform nameRt = name.rectTransform;
        nameRt.anchorMin = new Vector2(0f, 0f);
        nameRt.anchorMax = new Vector2(1f, 0f);
        nameRt.pivot = new Vector2(0.5f, 0f);
        nameRt.anchoredPosition = new Vector2(0f, 44f);
        nameRt.sizeDelta = new Vector2(-12f, 20f);
        name.textWrappingMode = TextWrappingModes.NoWrap;
        name.enableAutoSizing = true;
        name.fontSizeMin = 11f;
        name.fontSizeMax = 15f;

        // Mustard diamond + number, the same ticket shorthand the HUD counter uses.
        Image chip = MenuWidgets.CreateImage(plate, "TicketChip", MenuTheme.Solid(),
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.92f));
        RectTransform chipRt = chip.rectTransform;
        chipRt.anchorMin = chipRt.anchorMax = new Vector2(0.5f, 0f);
        chipRt.pivot = new Vector2(1f, 0.5f);
        chipRt.anchoredPosition = new Vector2(-14f, 22f);
        chipRt.sizeDelta = new Vector2(9f, 9f);
        chipRt.localRotation = Quaternion.Euler(0f, 0f, 45f);

        TMP_Text price = MenuWidgets.CreateText(plate, "Price", entry.price.ToString(), 22f,
            MenuTheme.AmberBright, MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 2f);
        RectTransform priceRt = price.rectTransform;
        priceRt.anchorMin = priceRt.anchorMax = new Vector2(0.5f, 0f);
        priceRt.pivot = new Vector2(0f, 0.5f);
        priceRt.anchoredPosition = new Vector2(-6f, 22f);
        priceRt.sizeDelta = new Vector2(90f, 28f);
        price.textWrappingMode = TextWrappingModes.NoWrap;

        // Sits exactly where the price does and swaps in once a one-per-player row is owned.
        TMP_Text ownedText = MenuWidgets.CreateText(plate, "Owned", "OWNED", 18f,
            MenuTheme.WithAlpha(MenuTheme.Moss, 0.95f), MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 4f);
        RectTransform ownedRt = ownedText.rectTransform;
        ownedRt.anchorMin = ownedRt.anchorMax = new Vector2(0.5f, 0f);
        ownedRt.pivot = new Vector2(0.5f, 0.5f);
        ownedRt.anchoredPosition = new Vector2(0f, 22f);
        ownedRt.sizeDelta = new Vector2(CellWidth - 20f, 28f);
        ownedText.textWrappingMode = TextWrappingModes.NoWrap;
        ownedText.gameObject.SetActive(false);

        Image outline = MenuWidgets.CreateImage(plate, "Outline", MenuTheme.RoundedOutline(CardRadius, 1.8f),
            MenuTheme.WithAlpha(MenuTheme.Bone, 0.30f));
        outline.rectTransform.SetStretch();

        int captured = index;
        var button = cardRoot.gameObject.AddComponent<Button>();
        button.targetGraphic = cardBg;
        button.transition = Selectable.Transition.None;   // MenuButtonFx owns the visuals
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => OnBuy(captured));

        var fx = cardRoot.gameObject.AddComponent<MenuButtonFx>();
        fx.button = button;
        fx.fill = cardBg;
        fx.frame = outline;
        fx.label = name;
        fx.plate = plate;
        fx.plateBaseTilt = 0f;   // cards line up in a grid; the hand-placed tilt belongs to loose plates
        fx.suppressHoverAudio = true;
        MenuWidgets.ApplyPlateStyle(fx, MenuWidgets.PlateStyle.Nav);

        return new StockTile
        {
            Index = index,
            Button = button,
            PriceText = price,
            PriceChip = chip,
            OwnedText = ownedText,
            Price = entry.price,
        };
    }

    void BuildFooter()
    {
        MakePlateFx("DONE", MenuWidgets.PlateStyle.Danger, OnClose,
            new Vector2(1f, 0f), new Vector2(-PanelPadding, 26f), new Vector2(200f, 56f), 22f);
    }

    Canvas CreateOwnedCanvas()
    {
        GameObject canvasGo = new("CarnivalStoreCanvas");
        canvasGo.transform.SetParent(transform, false);
        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30;
        canvas.vertexColorAlwaysGammaSpace = true;
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGo.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    static void EnsureEventSystem()
    {
        if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null)
            return;
        GameObject es = new("EventSystem (CarnivalStore)");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();
    }

    static TMP_Text MakeLabel(Transform parent, string text, float fontSize, Color color, TextAlignmentOptions alignment,
        Vector2 pivotAnchor, Vector2 anchoredPos, Vector2 size)
    {
        TextMeshProUGUI t = MenuWidgets.CreateText(parent, "Label", text, fontSize, color,
            MenuWidgets.FontKind.Display, alignment, 2f);
        RectTransform r = t.rectTransform;
        r.anchorMin = pivotAnchor;
        r.anchorMax = pivotAnchor;
        r.pivot = pivotAnchor;
        r.anchoredPosition = anchoredPos;
        r.sizeDelta = size;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        return t;
    }

    MenuButtonFx MakePlateFx(string label, MenuWidgets.PlateStyle style, UnityEngine.Events.UnityAction onClick,
        Vector2 pivotAnchor, Vector2 anchoredPos, Vector2 size, float fontSize)
    {
        MenuButtonFx fx = MenuWidgets.CreatePlate(_root.transform, "Btn_" + label, label,
            () => onClick?.Invoke(), style, size.y, fontSize);
        fx.suppressHoverAudio = true;
        RectTransform r = (RectTransform)fx.transform;
        r.anchorMin = pivotAnchor;
        r.anchorMax = pivotAnchor;
        r.pivot = pivotAnchor;
        r.anchoredPosition = anchoredPos;
        r.sizeDelta = size;
        return fx;
    }
}
