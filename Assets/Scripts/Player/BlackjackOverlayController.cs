using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Local-player blackjack table view + control overlay. Shown only while the local player occupies a seat. While
/// seated it (a) switches the view to a per-seat zoomed-in table camera, (b) frees the cursor + freezes movement
/// via <see cref="IsInteractive"/>, and (c) shows a casino-style control panel (bet / deal / hit / stand / leave)
/// that routes to the table's <see cref="BlackjackGameController"/> ServerRpcs. Hand totals are deliberately NOT
/// shown — the player reads the cards on the felt and does their own math. A single instance is created on demand
/// and persists (DontDestroyOnLoad) so its canvas/camera can't be torn down by other HUD/scene churn.
/// </summary>
[DisallowMultipleComponent]
public sealed class BlackjackOverlayController : MonoBehaviour
{
    /// <summary>True while the overlay is shown — PlayerController ORs this into its input-block + cursor-lock gate.</summary>
    public static bool IsInteractive { get; private set; }

    static BlackjackOverlayController _instance;

    // Palette
    static readonly Color PanelBg = new(0.05f, 0.08f, 0.07f, 0.93f);
    static readonly Color Gold = new(0.93f, 0.78f, 0.38f, 1f);
    static readonly Color TextLight = new(0.93f, 0.96f, 0.95f, 1f);
    static readonly Color TextDim = new(0.66f, 0.72f, 0.70f, 1f);
    static readonly Color BtnGreen = new(0.18f, 0.55f, 0.28f, 1f);
    static readonly Color BtnAmber = new(0.80f, 0.52f, 0.16f, 1f);
    static readonly Color BtnGold = new(0.74f, 0.58f, 0.20f, 1f);
    static readonly Color BtnGray = new(0.26f, 0.29f, 0.31f, 1f);

    PlayerController _player;
    NetworkObject _playerNet;
    NetworkPlayerCarnivalTickets _wallet;
    BlackjackGameController _table;

    GameObject _root;
    CanvasGroup _canvasGroup;
    Text _bannerText, _balanceValue, _betValue, _message;
    Button _betMinus, _betPlus, _dealBtn, _hitBtn, _standBtn, _leaveBtn;
    Text _dealLabel;

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
            SetButtonColor(_dealBtn, ready ? BtnGray : BtnGold);
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
                    return $"Bet placed - dealing in {Mathf.CeilToInt(_table.PhaseTimer)}s";
                return "Set your bet, then DEAL";
            case BlackjackPhase.Dealing:
                return "Dealing...";
            case BlackjackPhase.PlayerTurns:
                if (s.InRound == 0)
                    return "Sitting out - next round soon";
                if (s.Status == (byte)BlackjackHandStatus.Bust)
                    return "Busted!";
                if (s.Status == (byte)BlackjackHandStatus.Blackjack)
                    return "Blackjack!";
                if (myTurn)
                    return $"Your move - Hit or Stand  ({Mathf.CeilToInt(_table.PhaseTimer)}s)";
                if (_table.ActingSeatIndex < 0)
                    return "Dealer's turn...";
                return $"Seat {_table.ActingSeatIndex + 1} is playing...";
            case BlackjackPhase.DealerTurn:
                return "Dealer draws...";
            case BlackjackPhase.Resolve:
            case BlackjackPhase.Payout:
                if (s.InRound == 0)
                    return "Sat out - next round soon";
                return "Round over";
            default:
                return string.Empty;
        }
    }

    static string BuildBanner(SeatState s, out Color color)
    {
        string delta = s.LastPayout > 0 ? $"+{s.LastPayout}" : s.LastPayout.ToString();
        switch ((BlackjackSeatResult)s.LastResult)
        {
            case BlackjackSeatResult.Blackjack: color = Gold; return $"BLACKJACK!  {delta}";
            case BlackjackSeatResult.Win: color = new Color(0.45f, 0.9f, 0.5f); return $"YOU WIN  {delta}";
            case BlackjackSeatResult.Push: color = TextDim; return "PUSH";
            case BlackjackSeatResult.Bust: color = new Color(0.95f, 0.4f, 0.4f); return $"BUST  {delta}";
            case BlackjackSeatResult.Lose: color = new Color(0.95f, 0.4f, 0.4f); return $"DEALER WINS  {delta}";
            case BlackjackSeatResult.Forfeit: color = new Color(0.95f, 0.4f, 0.4f); return "FORFEIT";
            default: color = TextLight; return string.Empty;
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

        _root = MakePanel(canvas.transform, "BlackjackPanel", PanelBg, new Vector2(0.5f, 0f), new Vector2(0f, 28f), new Vector2(W, H));
        _canvasGroup = _root.AddComponent<CanvasGroup>();

        // Gold accent strip along the top of the panel.
        GameObject strip = MakePanel(_root.transform, "Accent", Gold, new Vector2(0.5f, 1f), new Vector2(0f, 0f), new Vector2(W, 4f));
        strip.GetComponent<RectTransform>().anchorMin = new Vector2(0f, 1f);
        strip.GetComponent<RectTransform>().anchorMax = new Vector2(1f, 1f);
        strip.GetComponent<RectTransform>().sizeDelta = new Vector2(0f, 4f);

        MakeLabel(_root.transform, "BLACKJACK", 22, FontStyle.Bold, Gold, TextAnchor.MiddleLeft,
            new Vector2(0f, 1f), new Vector2(24f, -12f), new Vector2(280f, 32f));

        // Result banner - big, anchored near the TOP-center of the screen (above the dealer's cards, in the
        // empty felt/background area) so it never overlaps the hands. ~170px down from the top in 1080-ref space.
        _bannerText = MakeLabel(canvas.transform, "", 56, FontStyle.Bold, Gold, TextAnchor.MiddleCenter,
            new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(900f, 80f));
        AddShadow(_bannerText, 3f);
        _bannerText.gameObject.SetActive(false);

        // Tickets + Bet (right cluster) with +/- buttons.
        MakeLabel(_root.transform, "TICKETS", 18, FontStyle.Bold, TextDim, TextAnchor.MiddleRight, new Vector2(1f, 1f), new Vector2(-40f, -16f), new Vector2(220f, 22f));
        _balanceValue = MakeLabel(_root.transform, "0", 40, FontStyle.Bold, Gold, TextAnchor.MiddleRight, new Vector2(1f, 1f), new Vector2(-40f, -52f), new Vector2(220f, 44f));

        MakeLabel(_root.transform, "BET", 18, FontStyle.Bold, TextDim, TextAnchor.MiddleCenter, new Vector2(1f, 1f), new Vector2(-345f, -16f), new Vector2(120f, 22f));
        _betValue = MakeLabel(_root.transform, "5", 40, FontStyle.Bold, TextLight, TextAnchor.MiddleCenter, new Vector2(1f, 1f), new Vector2(-345f, -54f), new Vector2(120f, 44f));
        _betMinus = MakeButton(_root.transform, "-", BtnGray, OnBetMinus, out _, new Vector2(1f, 1f), new Vector2(-470f, -58f), new Vector2(52f, 52f), 30);
        _betPlus = MakeButton(_root.transform, "+", BtnGray, OnBetPlus, out _, new Vector2(1f, 1f), new Vector2(-228f, -58f), new Vector2(52f, 52f), 30);

        // Status message (center, prominent - no totals shown).
        _message = MakeLabel(_root.transform, "", 28, FontStyle.Bold, TextLight, TextAnchor.MiddleLeft, new Vector2(0f, 1f), new Vector2(28f, -64f), new Vector2(560f, 40f));

        // Action buttons (bottom).
        _hitBtn = MakeButton(_root.transform, "HIT", BtnGreen, OnHit, out _, new Vector2(0f, 0f), new Vector2(40f, 24f), new Vector2(250f, 66f), 30);
        _standBtn = MakeButton(_root.transform, "STAND", BtnAmber, OnStand, out _, new Vector2(0f, 0f), new Vector2(304f, 24f), new Vector2(250f, 66f), 30);
        _dealBtn = MakeButton(_root.transform, "DEAL", BtnGold, OnDeal, out _dealLabel, new Vector2(0f, 0f), new Vector2(40f, 24f), new Vector2(514f, 66f), 32);
        _leaveBtn = MakeButton(_root.transform, "LEAVE TABLE", BtnGray, OnLeave, out _, new Vector2(1f, 0f), new Vector2(-40f, 24f), new Vector2(300f, 66f), 26);

        _root.SetActive(false);
    }

    Canvas CreateOwnedCanvas()
    {
        GameObject canvasGo = new("BlackjackCanvas");
        canvasGo.transform.SetParent(transform, false);
        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30;
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

    // --- uGUI builders ---

    static GameObject MakePanel(Transform parent, string name, Color color, Vector2 pivotAnchor, Vector2 anchoredPos, Vector2 size)
    {
        GameObject go = new(name);
        go.transform.SetParent(parent, false);
        RectTransform r = go.AddComponent<RectTransform>();
        r.anchorMin = pivotAnchor;
        r.anchorMax = pivotAnchor;
        r.pivot = pivotAnchor;
        r.anchoredPosition = anchoredPos;
        r.sizeDelta = size;
        Image img = go.AddComponent<Image>();
        img.color = color;
        return go;
    }

    static Text MakeLabel(Transform parent, string text, int fontSize, FontStyle style, Color color, TextAnchor anchor,
        Vector2 pivotAnchor, Vector2 anchoredPos, Vector2 size)
    {
        GameObject go = new("Label");
        go.transform.SetParent(parent, false);
        Text t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = fontSize;
        t.fontStyle = style;
        t.alignment = anchor;
        t.color = color;
        t.text = text;
        t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform r = t.rectTransform;
        r.anchorMin = pivotAnchor;
        r.anchorMax = pivotAnchor;
        r.pivot = pivotAnchor;
        r.anchoredPosition = anchoredPos;
        r.sizeDelta = size;
        return t;
    }

    static void AddShadow(Text t, float dist)
    {
        Shadow sh = t.gameObject.AddComponent<Shadow>();
        sh.effectColor = new Color(0f, 0f, 0f, 0.6f);
        sh.effectDistance = new Vector2(dist, -dist);
    }

    static Button MakeButton(Transform parent, string label, Color bg, UnityEngine.Events.UnityAction onClick, out Text labelText,
        Vector2 pivotAnchor, Vector2 anchoredPos, Vector2 size, int fontSize)
    {
        GameObject go = new($"Btn_{label}");
        go.transform.SetParent(parent, false);
        RectTransform r = go.AddComponent<RectTransform>();
        r.anchorMin = pivotAnchor;
        r.anchorMax = pivotAnchor;
        r.pivot = pivotAnchor;
        r.anchoredPosition = anchoredPos;
        r.sizeDelta = size;
        Image img = go.AddComponent<Image>();
        img.color = bg;

        Button btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        SetButtonColor(btn, bg);
        btn.onClick.AddListener(onClick);

        labelText = MakeLabel(go.transform, label, fontSize, FontStyle.Bold, TextLight, TextAnchor.MiddleCenter,
            new Vector2(0.5f, 0.5f), Vector2.zero, size);
        labelText.rectTransform.anchorMin = Vector2.zero;
        labelText.rectTransform.anchorMax = Vector2.one;
        labelText.rectTransform.offsetMin = Vector2.zero;
        labelText.rectTransform.offsetMax = Vector2.zero;
        return btn;
    }

    static void SetButtonColor(Button btn, Color bg)
    {
        if (btn.targetGraphic is Image img)
            img.color = bg;
        ColorBlock cb = btn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
        cb.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
        cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.6f);
        cb.colorMultiplier = 1f;
        btn.colors = cb;
    }
}
