using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Local-player blackjack table view + control overlay. Shown only while the local player occupies a seat. While
/// seated it (a) switches the view to a per-seat zoomed-in table camera, (b) frees the cursor + freezes movement
/// via <see cref="IsInteractive"/>, and (c) shows a control panel (bet / deal / hit / stand / leave) in the shared
/// plate language that routes to the table's <see cref="BlackjackGameController"/> ServerRpcs. Hand totals are
/// deliberately NOT shown — the player reads the cards on the felt and does their own math. A single instance is
/// created on demand and persists (DontDestroyOnLoad) so its canvas/camera can't be torn down by scene churn.
/// </summary>
[DisallowMultipleComponent]
public sealed class BlackjackOverlayController : MonoBehaviour
{
    /// <summary>True while the overlay is shown — PlayerController ORs this into its input-block + cursor-lock gate.</summary>
    public static bool IsInteractive { get; private set; }

    static BlackjackOverlayController _instance;

    PlayerController _player;
    NetworkObject _playerNet;
    NetworkPlayerCarnivalTickets _wallet;
    BlackjackGameController _table;

    GameObject _root;
    CanvasGroup _canvasGroup;
    TMP_Text _bannerText, _balanceValue, _betValue, _message;
    Button _betMinus, _betPlus, _dealBtn, _hitBtn, _standBtn, _leaveBtn;
    MenuButtonFx _dealFx;
    TMP_Text _dealLabel;
    bool _dealStyledAsCancel;

    Camera _bjCamera;
    Camera _fpCamera;
    int _currentSeatIndex = -1;
    bool _shown;
    bool _subscribed;

    public static void NotifySeatInteract(PlayerController player, BlackjackSeat seat)
    {
        if (player == null || seat == null)
            return;
        BlackjackGameController table = seat.Controller;
        if (table == null)
            return;
        EnsureInstance().Bind(player, table);
    }

    static BlackjackOverlayController EnsureInstance()
    {
        if (_instance == null)
        {
            GameObject go = new("BlackjackOverlay");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<BlackjackOverlayController>();
        }
        return _instance;
    }

    void Bind(PlayerController player, BlackjackGameController table)
    {
        _player = player;
        _playerNet = player.GetComponent<NetworkObject>();
        _wallet = player.GetComponent<NetworkPlayerCarnivalTickets>();
        _fpCamera = player.GetComponentInChildren<Camera>(true);

        if (_table != table)
        {
            Unsubscribe();
            _table = table;
            if (_table != null)
            {
                _table.StateChanged += Refresh;
                _subscribed = true;
            }
        }

        EnsureUiBuilt();
        Refresh();
    }

    void Unsubscribe()
    {
        if (_table != null && _subscribed)
            _table.StateChanged -= Refresh;
        _subscribed = false;
    }

    void OnDestroy()
    {
        Unsubscribe();
        if (_instance == this)
            _instance = null;
        if (_shown)
            IsInteractive = false;
    }

    void Update()
    {
        if (_table == null || _player == null || _playerNet == null || !_playerNet.IsSpawned)
        {
            SetShown(false);
            return;
        }

        int seatIndex = _table.SeatIndexOfOccupant(_playerNet.NetworkObjectId);
        if (seatIndex < 0)
        {
            SetShown(false);
            return;
        }

        if (_root == null)
            EnsureUiBuilt();

        _currentSeatIndex = seatIndex;
        SetShown(true);

        // Keep the table camera glued to the local seat's anchor + cursor free; yield raycasts to the pause menu if open.
        Transform camAnchor = _table.GetSeatCameraAnchor(seatIndex);
        if (_bjCamera != null && camAnchor != null)
            _bjCamera.transform.SetPositionAndRotation(camAnchor.position, camAnchor.rotation);
        if (!PauseMenuController.BlocksGameplayInput)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (_canvasGroup != null)
            _canvasGroup.blocksRaycasts = !PauseMenuController.BlocksGameplayInput;

        RefreshFor(seatIndex);
    }

    void SetShown(bool show)
    {
        if (_root != null)
            _root.SetActive(show);
        // The result banner is a child of the canvas (a sibling of _root, so it can float above the panel), so
        // toggling _root doesn't hide it. Clear it explicitly when hiding, or a "YOU WIN/LOSE" banner shown at the
        // moment you leave the table stays stuck on screen (nothing calls RefreshFor again once the seat is freed).
        if (!show && _bannerText != null)
            _bannerText.gameObject.SetActive(false);
        if (show == _shown)
            return;
        _shown = show;
        IsInteractive = show;

        if (show)
        {
            ActivateCamera();
            SeatPlayerOnStool();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            MenuTheme.ApplyCursor();
        }
        else
        {
            DeactivateCamera();
            if (_player != null)
                _player.ExitBlackjackSeat();
            if (!PauseMenuController.BlocksGameplayInput)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }

    /// <summary>Snap the local player's avatar onto the current seat's stool (server replicates pose + sit anim).</summary>
    void SeatPlayerOnStool()
    {
        if (_player == null || _table == null)
            return;
        if (_table.TryGetSeatSitPose(_currentSeatIndex, out Vector3 pos, out Quaternion rot))
            _player.EnterBlackjackSeat(pos, rot);
    }

    // =========================================================================================
    // Camera
    // =========================================================================================

    void ActivateCamera()
    {
        if (_bjCamera == null)
            CreateCamera();
        if (_fpCamera == null && _player != null)
            _fpCamera = _player.GetComponentInChildren<Camera>(true);

        if (_fpCamera != null)
        {
            // Match look so the world renders the same (layers, skybox, clip planes) from the table angle.
            _bjCamera.clearFlags = _fpCamera.clearFlags;
            _bjCamera.backgroundColor = _fpCamera.backgroundColor;
            _bjCamera.cullingMask = _fpCamera.cullingMask;
            _bjCamera.nearClipPlane = Mathf.Min(_fpCamera.nearClipPlane, 0.1f);
            _bjCamera.farClipPlane = _fpCamera.farClipPlane;
        }
        // Hide player avatars so a seated body never blocks the cards on the felt.
        int playerLayer = LayerMask.NameToLayer("Player");
        if (playerLayer >= 0)
            _bjCamera.cullingMask &= ~(1 << playerLayer);
        _bjCamera.fieldOfView = 46f; // zoomed in on the felt

        Transform camAnchor = _table != null ? _table.GetSeatCameraAnchor(_currentSeatIndex) : null;
        if (camAnchor != null)
            _bjCamera.transform.SetPositionAndRotation(camAnchor.position, camAnchor.rotation);

        _bjCamera.gameObject.SetActive(true);
        _bjCamera.enabled = true;
        if (_fpCamera != null)
            _fpCamera.enabled = false;
    }

    void DeactivateCamera()
    {
        if (_bjCamera != null)
        {
            _bjCamera.enabled = false;
            _bjCamera.gameObject.SetActive(false);
        }
        if (_fpCamera != null)
            _fpCamera.enabled = true;
    }

    void CreateCamera()
    {
        GameObject camGo = new("BlackjackTableCamera");
        camGo.transform.SetParent(transform, false);
        _bjCamera = camGo.AddComponent<Camera>();
        _bjCamera.depth = 50f; // above the FP camera
        camGo.SetActive(false);
    }

    // =========================================================================================
    // Refresh
    // =========================================================================================

    void Refresh()
    {
        if (_table == null || _playerNet == null || _root == null)
            return;
        int seatIndex = _table.SeatIndexOfOccupant(_playerNet.NetworkObjectId);
        if (seatIndex >= 0)
            RefreshFor(seatIndex);
    }

    void RefreshFor(int seatIndex)
    {
        if (_root == null)
            return;

        SeatState s = _table.GetSeat(seatIndex);
        BlackjackPhase phase = _table.Phase;
        int balance = _wallet != null ? _wallet.TicketCount : 0;
        bool inRound = s.InRound == 1;
        bool ready = s.IsReady == 1;
        bool betting = phase == BlackjackPhase.Betting && !inRound;
        bool myTurn = phase == BlackjackPhase.PlayerTurns && _table.ActingSeatIndex == seatIndex
            && s.Status == (byte)BlackjackHandStatus.Playing;

        // Hand totals are intentionally NOT shown - the player reads the cards and does their own math.
        _balanceValue.text = balance.ToString();
        _betValue.text = s.Bet.ToString();

        _message.text = BuildMessage(phase, s, seatIndex, myTurn);

        // Result banner (only when a resolved round is showing and we were dealt in).
        bool showBanner = (phase == BlackjackPhase.Resolve || phase == BlackjackPhase.Payout) && inRound
            && (BlackjackSeatResult)s.LastResult != BlackjackSeatResult.None;
        if (showBanner)
        {
            _bannerText.text = BuildBanner(s, out Color c);
            _bannerText.color = c;
        }
        _bannerText.gameObject.SetActive(showBanner);

        // Buttons
        _betMinus.gameObject.SetActive(betting && !ready);
        _betPlus.gameObject.SetActive(betting && !ready);

        bool showDeal = phase == BlackjackPhase.Betting && inRound == false;
        _dealBtn.gameObject.SetActive(showDeal);
        if (showDeal)
        {
            _dealLabel.text = ready ? "CANCEL" : "DEAL";
            _dealBtn.interactable = ready || balance >= BlackjackConfig.MinBet;
            if (_dealStyledAsCancel != ready && _dealFx != null)
            {
                _dealStyledAsCancel = ready;
                MenuWidgets.ApplyPlateStyle(_dealFx,
                    ready ? MenuWidgets.PlateStyle.Ghost : MenuWidgets.PlateStyle.Primary);
            }
        }

        _hitBtn.gameObject.SetActive(myTurn);
        _standBtn.gameObject.SetActive(myTurn);
        _leaveBtn.gameObject.SetActive(true);
    }

    string BuildMessage(BlackjackPhase phase, SeatState s, int seatIndex, bool myTurn)
    {
        switch (phase)
        {
            case BlackjackPhase.Idle:
            case BlackjackPhase.Betting:
                if (s.IsReady == 1)
                    return $"DEALING IN {Mathf.CeilToInt(_table.PhaseTimer)}";
                return "PLACE YOUR BET";
            case BlackjackPhase.Dealing:
                return "DEALING";
            case BlackjackPhase.PlayerTurns:
                if (s.InRound == 0)
                    return "SITTING OUT";
                if (s.Status == (byte)BlackjackHandStatus.Bust)
                    return "BUST";
                if (s.Status == (byte)BlackjackHandStatus.Blackjack)
                    return "BLACKJACK";
                if (myTurn)
                    return $"YOUR MOVE — {Mathf.CeilToInt(_table.PhaseTimer)}";
                if (_table.ActingSeatIndex < 0)
                    return "DEALER DRAWS";
                return $"SEAT {_table.ActingSeatIndex + 1} PLAYING";
            case BlackjackPhase.DealerTurn:
                return "DEALER DRAWS";
            case BlackjackPhase.Resolve:
            case BlackjackPhase.Payout:
                if (s.InRound == 0)
                    return "SAT OUT";
                return "ROUND OVER";
            default:
                return string.Empty;
        }
    }

    static string BuildBanner(SeatState s, out Color color)
    {
        string delta = s.LastPayout > 0 ? $"+{s.LastPayout}" : s.LastPayout.ToString();
        switch ((BlackjackSeatResult)s.LastResult)
        {
            case BlackjackSeatResult.Blackjack: color = MenuTheme.AmberBright; return $"BLACKJACK  {delta}";
            case BlackjackSeatResult.Win: color = MenuTheme.Moss; return $"YOU WIN  {delta}";
            case BlackjackSeatResult.Push: color = MenuTheme.Mist; return "PUSH";
            case BlackjackSeatResult.Bust: color = MenuTheme.BloodBright; return $"BUST  {delta}";
            case BlackjackSeatResult.Lose: color = MenuTheme.BloodBright; return $"DEALER WINS  {delta}";
            case BlackjackSeatResult.Forfeit: color = MenuTheme.BloodBright; return "FORFEIT";
            default: color = MenuTheme.Bone; return string.Empty;
        }
    }

    // =========================================================================================
    // Button actions
    // =========================================================================================

    void OnBetMinus() => _table?.RequestAdjustBet(_player, -BlackjackConfig.BetStep);
    void OnBetPlus() => _table?.RequestAdjustBet(_player, BlackjackConfig.BetStep);
    void OnDeal()
    {
        if (_table == null || _playerNet == null)
            return;
        int seatIndex = _table.SeatIndexOfOccupant(_playerNet.NetworkObjectId);
        if (seatIndex < 0)
            return;
        bool ready = _table.GetSeat(seatIndex).IsReady == 1;
        _table.RequestReady(_player, !ready);
    }
    void OnHit() => _table?.RequestHit(_player);
    void OnStand() => _table?.RequestStand(_player);
    void OnLeave()
    {
        // Optimistically release locally so the player is never stuck even if the server is a beat behind.
        SetShown(false);
        _table?.RequestLeave(_player);
    }

    // =========================================================================================
    // UI construction
    // =========================================================================================

    void EnsureUiBuilt()
    {
        if (_root != null)
            return;

        EnsureEventSystem();
        Canvas canvas = CreateOwnedCanvas();

        const float W = 940f;
        const float H = 232f;

        // panel: dark weathered plate with a bone frame and corner brackets
        _root = new GameObject("BlackjackPanel");
        _root.layer = 5;
        _root.transform.SetParent(canvas.transform, false);
        RectTransform rootRt = _root.AddComponent<RectTransform>();
        rootRt.anchorMin = new Vector2(0.5f, 0f);
        rootRt.anchorMax = new Vector2(0.5f, 0f);
        rootRt.pivot = new Vector2(0.5f, 0f);
        rootRt.anchoredPosition = new Vector2(0f, 28f);
        rootRt.sizeDelta = new Vector2(W, H);
        _canvasGroup = _root.AddComponent<CanvasGroup>();

        Image bg = _root.AddComponent<Image>();
        bg.sprite = MenuTheme.RoundedRect(3);
        bg.type = Image.Type.Sliced;
        bg.color = MenuTheme.WithAlpha(MenuTheme.Panel, 0.96f);
        MenuWidgets.CreateGrunge(_root.transform, MenuTheme.WithAlpha(Color.white, 0.05f));
        Image frame = MenuWidgets.CreateImage(_root.transform, "Frame", MenuTheme.RoundedOutline(3, 1.6f),
            MenuTheme.WithAlpha(MenuTheme.Bone, 0.20f));
        frame.rectTransform.SetStretch();
        MenuWidgets.CreateCornerBrackets(rootRt, MenuTheme.WithAlpha(MenuTheme.Bone, 0.55f));

        TMP_Text title = MakeLabel(_root.transform, "BLACKJACK", 21f, MenuTheme.WithAlpha(MenuTheme.Amber, 0.95f),
            TextAlignmentOptions.MidlineLeft, new Vector2(0f, 1f), new Vector2(26f, -14f), new Vector2(280f, 30f));
        title.characterSpacing = 6f;

        // Result banner - big, anchored near the TOP-center of the screen (above the dealer's cards, in the
        // empty felt/background area) so it never overlaps the hands. ~170px down from the top in 1080-ref space.
        _bannerText = MakeLabel(canvas.transform, "", 58f, MenuTheme.AmberBright, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(900f, 80f));
        _bannerText.characterSpacing = 6f;
        _bannerText.fontStyle = FontStyles.Bold;
        AddUnderlay(_bannerText);
        _bannerText.gameObject.SetActive(false);

        // Tickets + Bet (right cluster) with +/- plates.
        MakeLabel(_root.transform, "TICKETS", 15f, MenuTheme.Mist, TextAlignmentOptions.MidlineRight,
            new Vector2(1f, 1f), new Vector2(-40f, -18f), new Vector2(220f, 22f)).characterSpacing = 5f;
        _balanceValue = MakeLabel(_root.transform, "0", 38f, MenuTheme.AmberBright, TextAlignmentOptions.MidlineRight,
            new Vector2(1f, 1f), new Vector2(-40f, -52f), new Vector2(220f, 44f));

        MakeLabel(_root.transform, "BET", 15f, MenuTheme.Mist, TextAlignmentOptions.Center,
            new Vector2(1f, 1f), new Vector2(-345f, -18f), new Vector2(120f, 22f)).characterSpacing = 5f;
        _betValue = MakeLabel(_root.transform, "5", 38f, MenuTheme.Bone, TextAlignmentOptions.Center,
            new Vector2(1f, 1f), new Vector2(-345f, -54f), new Vector2(120f, 44f));
        _betMinus = MakePlate("-", MenuWidgets.PlateStyle.Ghost, OnBetMinus, out _,
            new Vector2(1f, 1f), new Vector2(-470f, -58f), new Vector2(52f, 52f), 26f);
        _betPlus = MakePlate("+", MenuWidgets.PlateStyle.Ghost, OnBetPlus, out _,
            new Vector2(1f, 1f), new Vector2(-228f, -58f), new Vector2(52f, 52f), 26f);

        // Status message (left-center, prominent - no totals shown).
        _message = MakeLabel(_root.transform, "", 27f, MenuTheme.Bone, TextAlignmentOptions.MidlineLeft,
            new Vector2(0f, 1f), new Vector2(28f, -64f), new Vector2(560f, 40f));
        _message.characterSpacing = 3f;

        // Action plates (bottom).
        _hitBtn = MakePlate("HIT", MenuWidgets.PlateStyle.Primary, OnHit, out _,
            new Vector2(0f, 0f), new Vector2(40f, 24f), new Vector2(250f, 62f), 26f);
        _standBtn = MakePlate("STAND", MenuWidgets.PlateStyle.Ghost, OnStand, out _,
            new Vector2(0f, 0f), new Vector2(304f, 24f), new Vector2(250f, 62f), 26f);
        MenuButtonFx dealFx;
        _dealBtn = MakePlateFx("DEAL", MenuWidgets.PlateStyle.Primary, OnDeal, out _dealLabel, out dealFx,
            new Vector2(0f, 0f), new Vector2(40f, 24f), new Vector2(514f, 62f), 27f);
        _dealFx = dealFx;
        _dealStyledAsCancel = false;
        _leaveBtn = MakePlate("LEAVE", MenuWidgets.PlateStyle.Danger, OnLeave, out _,
            new Vector2(1f, 0f), new Vector2(-40f, 24f), new Vector2(300f, 62f), 24f);

        _root.SetActive(false);
    }

    Canvas CreateOwnedCanvas()
    {
        GameObject canvasGo = new("BlackjackCanvas");
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
        GameObject es = new("EventSystem (Blackjack)");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();
    }

    // --- builders ---

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

    static void AddUnderlay(TMP_Text text)
    {
        Material mat = text.fontMaterial;
        if (mat == null || !mat.HasProperty(ShaderUtilities.ID_UnderlayColor))
            return;
        mat.EnableKeyword(ShaderUtilities.Keyword_Underlay);
        mat.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(0f, 0f, 0f, 0.7f));
        mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0.6f);
        mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, -0.6f);
        mat.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0.3f);
    }

    Button MakePlate(string label, MenuWidgets.PlateStyle style, UnityEngine.Events.UnityAction onClick,
        out TMP_Text labelText, Vector2 pivotAnchor, Vector2 anchoredPos, Vector2 size, float fontSize)
    {
        return MakePlateFx(label, style, onClick, out labelText, out _, pivotAnchor, anchoredPos, size, fontSize);
    }

    Button MakePlateFx(string label, MenuWidgets.PlateStyle style, UnityEngine.Events.UnityAction onClick,
        out TMP_Text labelText, out MenuButtonFx fx, Vector2 pivotAnchor, Vector2 anchoredPos, Vector2 size, float fontSize)
    {
        fx = MenuWidgets.CreatePlate(_root.transform, "Btn_" + label, label, () => onClick?.Invoke(),
            style, size.y, fontSize);
        fx.suppressHoverAudio = true;
        RectTransform r = (RectTransform)fx.transform;
        r.anchorMin = pivotAnchor;
        r.anchorMax = pivotAnchor;
        r.pivot = pivotAnchor;
        r.anchoredPosition = anchoredPos;
        r.sizeDelta = size;
        labelText = fx.label;
        return fx.button;
    }
}
