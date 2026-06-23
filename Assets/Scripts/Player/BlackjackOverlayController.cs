using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Local-player, runtime-built control overlay for the blackjack table. Shown only while the local player
/// occupies a seat; provides Bet +/- , Ready, Hit, Stand and Leave buttons that route to the table's
/// <see cref="BlackjackGameController"/> ServerRpcs. A single instance is created on demand. While shown it
/// raises <see cref="IsInteractive"/> so <see cref="PlayerController"/> freezes movement and frees the cursor;
/// it yields raycasts to the pause menu when that is open.
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
    Text _messageText;
    Text _infoText;
    Text _betText;
    Button _betMinus, _betPlus, _readyBtn, _hitBtn, _standBtn, _leaveBtn;
    Text _readyLabel;
    bool _shown;
    bool _subscribed;

    /// <summary>Bind/refresh the overlay to the table the local player just interacted with.</summary>
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
            GameObject go = new GameObject("BlackjackOverlay");
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

        if (_table != table)
        {
            Unsubscribe();
            _table = table;
            Subscribe();
        }

        EnsureUiBuilt();
        Refresh();
    }

    void Subscribe()
    {
        if (_table != null && !_subscribed)
        {
            _table.StateChanged += Refresh;
            _subscribed = true;
        }
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
            EnsureUiBuilt();   // rebuild if our panel/canvas was torn down for any reason

        SetShown(true);

        // Keep the cursor free + assert raycast state every frame so the pause menu can take over cleanly.
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
        if (show == _shown)
            return;
        _shown = show;
        IsInteractive = show;
        if (show)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (!PauseMenuController.BlocksGameplayInput)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Refresh()
    {
        if (_table == null || _playerNet == null)
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
        bool isMyTurn = phase == BlackjackPhase.PlayerTurns
            && _table.ActingSeatIndex == seatIndex
            && s.Status == (byte)BlackjackHandStatus.Playing;

        // --- Info line: your hand / dealer up / balance ---
        string hand;
        if (s.Cards.Length > 0)
        {
            int total = _table.SeatTotal(seatIndex, out bool soft, out _);
            hand = soft ? $"{total} (soft)" : total.ToString();
        }
        else
        {
            hand = "-";
        }

        int dealerVisible = _table.DealerVisibleTotal();
        string dealer = _table.Dealer.Cards.Length > 0 ? dealerVisible.ToString() : "-";
        _infoText.text = $"Hand: {hand}    Dealer: {dealer}    Tickets: {balance}";
        _betText.text = $"Bet: {s.Bet}";

        // --- Message line ---
        _messageText.text = BuildMessage(phase, s, seatIndex, isMyTurn);

        // --- Button enable/labels per phase ---
        bool betting = phase == BlackjackPhase.Betting && s.InRound == 0;
        bool ready = s.IsReady == 1;
        _betMinus.interactable = betting && !ready;
        _betPlus.interactable = betting && !ready;
        _readyBtn.interactable = betting && balance >= BlackjackConfig.MinBet;
        _readyLabel.text = ready ? "Cancel" : "Ready";
        _hitBtn.interactable = isMyTurn;
        _standBtn.interactable = isMyTurn;
        _leaveBtn.interactable = true;
    }

    string BuildMessage(BlackjackPhase phase, SeatState s, int seatIndex, bool isMyTurn)
    {
        switch (phase)
        {
            case BlackjackPhase.Idle:
            case BlackjackPhase.Betting:
                if (s.IsReady == 1)
                    return $"Ready — waiting for deal ({Mathf.CeilToInt(_table.PhaseTimer)}s)";
                return "Set your bet and press Ready";
            case BlackjackPhase.Dealing:
                return "Dealing...";
            case BlackjackPhase.PlayerTurns:
                if (s.InRound == 0)
                    return "Waiting for the next round";
                if (s.Status == (byte)BlackjackHandStatus.Bust)
                    return "Bust!";
                if (s.Status == (byte)BlackjackHandStatus.Blackjack)
                    return "Blackjack!";
                if (isMyTurn)
                    return $"Your turn — Hit or Stand ({Mathf.CeilToInt(_table.PhaseTimer)}s)";
                if (_table.ActingSeatIndex < 0)
                    return "Dealer's turn";
                return $"Seat {_table.ActingSeatIndex + 1} is playing...";
            case BlackjackPhase.DealerTurn:
                return "Dealer is drawing...";
            case BlackjackPhase.Resolve:
            case BlackjackPhase.Payout:
                if (s.InRound == 0)
                    return "Sat out — next round soon";
                return BuildResultMessage(s);
            default:
                return string.Empty;
        }
    }

    static string BuildResultMessage(SeatState s)
    {
        string delta = s.LastPayout > 0 ? $"+{s.LastPayout}" : s.LastPayout.ToString();
        switch ((BlackjackSeatResult)s.LastResult)
        {
            case BlackjackSeatResult.Blackjack: return $"BLACKJACK!  {delta} tickets";
            case BlackjackSeatResult.Win: return $"You win!  {delta} tickets";
            case BlackjackSeatResult.Push: return "Push — bet returned";
            case BlackjackSeatResult.Bust: return $"Bust!  {delta} tickets";
            case BlackjackSeatResult.Lose: return $"You lose  {delta} tickets";
            case BlackjackSeatResult.Forfeit: return "Forfeited";
            default: return "Round over";
        }
    }

    // =========================================================================================
    // Button actions
    // =========================================================================================

    void OnBetMinus() => _table?.RequestAdjustBet(_player, -BlackjackConfig.BetStep);
    void OnBetPlus() => _table?.RequestAdjustBet(_player, BlackjackConfig.BetStep);
    void OnReady()
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
    void OnLeave() => _table?.RequestLeave(_player);

    // =========================================================================================
    // UI construction
    // =========================================================================================

    void EnsureUiBuilt()
    {
        if (_root != null)
            return;

        EnsureEventSystem();

        Canvas canvas = CreateOwnedCanvas();

        _root = new GameObject("BlackjackPanel");
        _root.transform.SetParent(canvas.transform, false);
        RectTransform rect = _root.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, 28f);
        rect.sizeDelta = new Vector2(760f, 168f);

        _canvasGroup = _root.AddComponent<CanvasGroup>();

        Image bg = _root.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.72f);

        _messageText = CreateText(_root.transform, "Message", new Vector2(16f, -10f), new Vector2(-16f, -44f),
            26, TextAnchor.MiddleCenter, new Color(1f, 0.95f, 0.7f, 1f));
        _messageText.rectTransform.anchorMin = new Vector2(0f, 1f);
        _messageText.rectTransform.anchorMax = new Vector2(1f, 1f);
        _messageText.rectTransform.pivot = new Vector2(0.5f, 1f);
        _messageText.rectTransform.offsetMin = new Vector2(12f, -46f);
        _messageText.rectTransform.offsetMax = new Vector2(-12f, -8f);

        _infoText = CreateText(_root.transform, "Info", Vector2.zero, Vector2.zero,
            20, TextAnchor.MiddleCenter, new Color(0.85f, 0.92f, 1f, 1f));
        _infoText.rectTransform.anchorMin = new Vector2(0f, 1f);
        _infoText.rectTransform.anchorMax = new Vector2(1f, 1f);
        _infoText.rectTransform.pivot = new Vector2(0.5f, 1f);
        _infoText.rectTransform.offsetMin = new Vector2(12f, -84f);
        _infoText.rectTransform.offsetMax = new Vector2(-12f, -50f);

        // Button row (manual horizontal placement).
        float x = 14f;
        const float y = 12f;
        const float h = 54f;
        _betMinus = CreateButton(_root.transform, "-", new Vector2(x, y), new Vector2(48f, h), OnBetMinus, out _);
        x += 54f;
        _betText = CreateText(_root.transform, "BetText", new Vector2(x, y), new Vector2(120f, h),
            22, TextAnchor.MiddleCenter, Color.white);
        _betText.rectTransform.anchorMin = new Vector2(0f, 0f);
        _betText.rectTransform.anchorMax = new Vector2(0f, 0f);
        _betText.rectTransform.pivot = new Vector2(0f, 0f);
        _betText.rectTransform.anchoredPosition = new Vector2(x, y);
        _betText.rectTransform.sizeDelta = new Vector2(120f, h);
        x += 126f;
        _betPlus = CreateButton(_root.transform, "+", new Vector2(x, y), new Vector2(48f, h), OnBetPlus, out _);
        x += 60f;
        _readyBtn = CreateButton(_root.transform, "Ready", new Vector2(x, y), new Vector2(120f, h), OnReady, out _readyLabel);
        x += 130f;
        _hitBtn = CreateButton(_root.transform, "Hit", new Vector2(x, y), new Vector2(96f, h), OnHit, out _);
        x += 106f;
        _standBtn = CreateButton(_root.transform, "Stand", new Vector2(x, y), new Vector2(96f, h), OnStand, out _);
        x += 106f;
        _leaveBtn = CreateButton(_root.transform, "Leave", new Vector2(x, y), new Vector2(96f, h), OnLeave, out _);

        _root.SetActive(false);
    }

    Canvas CreateOwnedCanvas()
    {
        // A dedicated canvas parented to this (DontDestroyOnLoad) overlay, so a HUD/scene teardown
        // elsewhere can never destroy our panel out from under us (the earlier disappearing-overlay bug).
        GameObject canvasGo = new GameObject("BlackjackCanvas");
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
        GameObject es = new GameObject("EventSystem (Blackjack)");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();
    }

    static Text CreateText(Transform parent, string name, Vector2 pos, Vector2 size,
        int fontSize, TextAnchor anchor, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Text t = go.AddComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = fontSize;
        t.alignment = anchor;
        t.color = color;
        t.raycastTarget = false;
        RectTransform r = t.rectTransform;
        r.anchorMin = new Vector2(0f, 0f);
        r.anchorMax = new Vector2(0f, 0f);
        r.pivot = new Vector2(0f, 0f);
        r.anchoredPosition = pos;
        r.sizeDelta = size;
        return t;
    }

    static Button CreateButton(Transform parent, string label, Vector2 pos, Vector2 size,
        UnityEngine.Events.UnityAction onClick, out Text labelText)
    {
        GameObject go = new GameObject($"Btn_{label}");
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = new Color(0.18f, 0.22f, 0.3f, 0.95f);
        RectTransform r = img.rectTransform;
        r.anchorMin = new Vector2(0f, 0f);
        r.anchorMax = new Vector2(0f, 0f);
        r.pivot = new Vector2(0f, 0f);
        r.anchoredPosition = pos;
        r.sizeDelta = size;

        Button btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        ColorBlock cb = btn.colors;
        cb.normalColor = new Color(0.18f, 0.22f, 0.3f, 0.95f);
        cb.highlightedColor = new Color(0.28f, 0.34f, 0.46f, 1f);
        cb.pressedColor = new Color(0.12f, 0.16f, 0.22f, 1f);
        cb.disabledColor = new Color(0.12f, 0.12f, 0.12f, 0.5f);
        btn.colors = cb;
        btn.onClick.AddListener(onClick);

        labelText = CreateText(go.transform, "Label", Vector2.zero, size, 22, TextAnchor.MiddleCenter, Color.white);
        RectTransform lr = labelText.rectTransform;
        lr.anchorMin = Vector2.zero;
        lr.anchorMax = Vector2.one;
        lr.offsetMin = Vector2.zero;
        lr.offsetMax = Vector2.zero;

        return btn;
    }
}
