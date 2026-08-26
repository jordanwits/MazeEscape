using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Local-player overlay for the jail skeleton's rock-paper-scissors game (<see cref="SkeletonRpsChallenge"/>).
/// While shown it frees the cursor and freezes movement via <see cref="IsInteractive"/> (PlayerController ORs
/// this into its gameplay-input gate, exactly like <see cref="BlackjackOverlayController"/>). The first-person
/// camera stays live — the player keeps looking at the skeleton; only the control panel is an overlay.
/// Pacing is client-side: a short suspense beat before every reveal (even when the authoritative result arrives
/// instantly), a hold on each reveal, then back to choosing; match end shows a banner and auto-closes.
/// Because it eats player input while up, the panel must always have a way out that does not depend on the
/// authority answering or on the mouse reaching a plate: cancel (Esc, or Start / B on a pad) walks away, and
/// two throws in a row that go nowhere close it.
/// A single instance is created on demand and persists (DontDestroyOnLoad).
/// </summary>
[DisallowMultipleComponent]
public sealed class SkeletonRpsOverlayController : MonoBehaviour
{
    /// <summary>True while the overlay is shown — PlayerController ORs this into its input-block + cursor-lock gate.</summary>
    public static bool IsInteractive { get; private set; }

    /// <summary>
    /// True while the panel owns the cancel press: shown, or on the frame it consumed one to close itself.
    /// Update order against <see cref="PauseMenuController"/> is undefined, so the closing frame has to keep
    /// swallowing the press or the pause menu opens behind the dismissed panel.
    /// </summary>
    public static bool ConsumesCancelInput => IsInteractive || Time.frameCount == _cancelConsumedFrame;

    static SkeletonRpsOverlayController _instance;
    static int _cancelConsumedFrame = -1;

    const float MinSuspenseSeconds = 0.75f;
    const float WaitingTimeoutSeconds = 8f;
    const float RevealHoldSeconds = 1.8f;
    const float MatchOverHoldSeconds = 2.6f;
    const float NoticeHoldSeconds = 1.8f;
    const float RangeClosePollSlack = 0.9f;
    /// <summary>Throws in a row that neither advanced nor ended the match before the panel stops trying.</summary>
    const int NoProgressStrikesToGiveUp = 2;

    enum OverlayState : byte
    {
        Choosing,
        Waiting,
        Reveal,
        MatchOver,
        Notice,
    }

    PlayerController _player;
    SkeletonRpsChallenge _challenge;

    GameObject _root;
    CanvasGroup _canvasGroup;
    TMP_Text _bannerText, _revealText, _verdictText;
    Image[] _playerPips, _skeletonPips;
    GameObject _rockBtn, _paperBtn, _scissorsBtn;

    OverlayState _state;
    bool _shown;
    float _waitingSince;
    float _stateUntil;
    bool _hasPendingResult;
    SkeletonRpsThrowResult _pendingResult;
    /// <summary>Counts submitted throws; with <see cref="_answeredThrow"/> it says whether the newest one is still unanswered.</summary>
    int _submittedThrow;
    int _answeredThrow;
    int _consecutiveNoProgress;

    public static void Show(PlayerController player, SkeletonRpsChallenge challenge)
    {
        if (player == null || challenge == null || challenge.LocalPlayerConcluded)
            return;
        EnsureInstance().Bind(player, challenge);
    }

    /// <summary>Called by the challenge when the authority resolved a throw for the local player.</summary>
    public static void NotifyThrowResult(SkeletonRpsChallenge challenge, SkeletonRpsThrowResult result)
    {
        SkeletonRpsOverlayController inst = _instance;
        if (inst == null || !inst._shown || inst._challenge != challenge)
            return;

        if (inst._state == OverlayState.Waiting)
        {
            // Replies are reliable-sequenced, so they land in submission order and each one retires exactly ONE
            // outstanding throw. Snapping to _submittedThrow would let a delayed reply to an earlier throw mark the
            // newest one answered, and the Choosing late-accept below would then refuse the reply that is really its.
            inst._answeredThrow = Mathf.Min(inst._answeredThrow + 1, inst._submittedThrow);
            inst._pendingResult = result;
            inst._hasPendingResult = true;
            return;
        }

        // A reply that outran the waiting timeout still answers the throw the player made, so take it while the
        // newest throw is the unanswered one and nothing was thrown since. Suspense already had its 8 seconds.
        if (inst._state == OverlayState.Choosing && inst._answeredThrow != inst._submittedThrow)
        {
            inst._answeredThrow = inst._submittedThrow;
            inst.ApplyResult(result);
        }
    }

    static SkeletonRpsOverlayController EnsureInstance()
    {
        if (_instance == null)
        {
            GameObject go = new("SkeletonRpsOverlay");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SkeletonRpsOverlayController>();
        }
        return _instance;
    }

    void Bind(PlayerController player, SkeletonRpsChallenge challenge)
    {
        _player = player;
        _challenge = challenge;
        _consecutiveNoProgress = 0;
        _answeredThrow = _submittedThrow;

        EnsureUiBuilt();
        // Re-checked on every show: the event system is a plain object that a scene load or the pause menu can take.
        EnsureEventSystem();
        RefreshPips(challenge.LocalKnownPlayerWins, challenge.LocalKnownSkeletonWins);
        EnterChoosing(challenge.LocalPlayerHasUnfinishedMatch ? "FIRST TO 2 TAKES IT — GAME IN PROGRESS" : "FIRST TO 2 TAKES IT");
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

        // Hard invalidation in any state: the player or the skeleton is gone / the player lost normal control
        // (ragdolled, carried off by the Jailor, died) or a level change destroyed the challenge.
        if (_player == null || _challenge == null || !_player.HasNormalInteractiveControl)
        {
            SetShown(false);
            return;
        }

        // Cancel walks away exactly like the plate does. The pause menu yields the same press (see
        // ConsumesCancelInput); while it is open on top, the press belongs to it instead.
        if (!PauseMenuController.BlocksGameplayInput && CancelPressedThisFrame())
        {
            _cancelConsumedFrame = Time.frameCount;
            OnLeave();
            return;
        }

        // While idle at the choice screen, also close when the game stopped mattering: the cell was opened some
        // other way, or the player wandered out of reach. Not applied mid-throw/reveal so a win that unlocks the
        // door doesn't close the overlay before its banner shows.
        if (_state == OverlayState.Choosing)
        {
            HingeInteractDoor door = _challenge.JailDoor;
            if (door == null
                || !door.IsLocked
                || !_challenge.IsInInteractRange(_player.transform.position, RangeClosePollSlack))
            {
                SetShown(false);
                return;
            }
        }

        // Cursor stays free while shown; yield raycasts to the pause menu when it is open.
        if (!PauseMenuController.BlocksGameplayInput)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        if (_canvasGroup != null)
            _canvasGroup.blocksRaycasts = !PauseMenuController.BlocksGameplayInput;

        switch (_state)
        {
            case OverlayState.Waiting:
                TickWaiting();
                break;
            case OverlayState.Reveal:
                if (Time.unscaledTime >= _stateUntil)
                    EnterChoosingKeepingLastReveal();
                break;
            case OverlayState.MatchOver:
            case OverlayState.Notice:
                if (Time.unscaledTime >= _stateUntil)
                    SetShown(false);
                break;
        }
    }

    /// <summary>
    /// The cancel press, keyboard and pad. The pad half is not optional: <see cref="PauseMenuController"/> gives up
    /// its Start toggle while <see cref="ConsumesCancelInput"/> is true, and nothing hands a pad a UI selection to
    /// work the plates with, so Start / B is a pad player's only way out of the panel.
    /// </summary>
    static bool CancelPressedThisFrame()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            return true;

        Gamepad pad = Gamepad.current;
        return pad != null
            && (pad.startButton.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame);
    }

    void TickWaiting()
    {
        float elapsed = Time.unscaledTime - _waitingSince;
        if (!_hasPendingResult && elapsed > WaitingTimeoutSeconds)
        {
            // The authority never answered (dropped session, despawned store) — don't strand the panel.
            EnterChoosingOrGiveUp("NO ANSWER — THROW AGAIN");
            return;
        }

        if (!_hasPendingResult || elapsed < MinSuspenseSeconds)
        {
            int dots = 1 + Mathf.FloorToInt(elapsed / 0.28f) % 3;
            _revealText.text = "THE BONES STIR" + new string('.', dots);
            _revealText.color = MenuTheme.Mist;
            return;
        }

        _hasPendingResult = false;
        ApplyResult(_pendingResult);
    }

    void ApplyResult(SkeletonRpsThrowResult result)
    {
        if (!result.Accepted)
        {
            switch ((SkeletonRpsRejectReason)result.RejectReason)
            {
                case SkeletonRpsRejectReason.OutOfRange:
                    EnterChoosingOrGiveUp("STEP CLOSER");
                    break;
                case SkeletonRpsRejectReason.AlreadyPlayed:
                    EnterNotice("IT REMEMBERS YOU", MenuTheme.BloodBright);
                    break;
                case SkeletonRpsRejectReason.DoorNotLocked:
                    EnterNotice("THE CELL IS ALREADY OPEN", MenuTheme.Mist);
                    break;
                default:
                    SetShown(false);
                    break;
            }
            return;
        }

        _consecutiveNoProgress = 0;
        RefreshPips(result.PlayerRoundWins, result.SkeletonRoundWins);
        _challenge.PlayRoundRevealSfx();

        _revealText.text = $"YOU {ChoiceName(result.PlayerChoice)}  —  BONES {ChoiceName(result.SkeletonChoice)}";
        _revealText.color = MenuTheme.Bone;

        if (result.RoundWasTie)
        {
            _verdictText.text = "DEAD EVEN — AGAIN";
            _verdictText.color = MenuTheme.Mist;
        }
        else if (result.PlayerWonRound)
        {
            _verdictText.text = "YOUR ROUND";
            _verdictText.color = MenuTheme.Moss;
        }
        else
        {
            _verdictText.text = "ITS ROUND";
            _verdictText.color = MenuTheme.BloodBright;
        }
        _verdictText.gameObject.SetActive(true);

        if (result.MatchOver)
        {
            _state = OverlayState.MatchOver;
            _stateUntil = Time.unscaledTime + MatchOverHoldSeconds;
            SetChoiceButtonsVisible(false);
            if (result.PlayerWonMatch)
            {
                ShowBanner("THE CELL OPENS", MenuTheme.Moss);
            }
            else
            {
                ShowBanner("THE BONES KEEP YOU", MenuTheme.BloodBright);
                _challenge.PlayMatchLossSfx();
            }
            return;
        }

        _state = OverlayState.Reveal;
        _stateUntil = Time.unscaledTime + RevealHoldSeconds;
        SetChoiceButtonsVisible(false);
    }

    static string ChoiceName(byte choice)
    {
        switch ((SkeletonRpsChoice)choice)
        {
            case SkeletonRpsChoice.Rock: return "ROCK";
            case SkeletonRpsChoice.Paper: return "PAPER";
            case SkeletonRpsChoice.Scissors: return "SCISSORS";
            default: return "?";
        }
    }

    /// <summary>
    /// Back to the choice screen after a throw that went nowhere (no answer, or refused for being too far).
    /// Choosing is the one state with no time bound of its own, so a second dead throw in a row ends the panel
    /// instead of parking the player there — the plates may be exactly what they cannot reach.
    /// </summary>
    void EnterChoosingOrGiveUp(string status)
    {
        _consecutiveNoProgress++;
        if (_consecutiveNoProgress >= NoProgressStrikesToGiveUp)
        {
            EnterNotice("THE BONES IGNORE YOU", MenuTheme.Mist);
            return;
        }

        EnterChoosing(status);
    }

    void EnterChoosing(string status)
    {
        _state = OverlayState.Choosing;
        _hasPendingResult = false;
        _revealText.text = status;
        _revealText.color = MenuTheme.Mist;
        _verdictText.gameObject.SetActive(false);
        HideBanner();
        SetChoiceButtonsVisible(true);
    }

    /// <summary>
    /// Return to choosing after a round reveal WITHOUT wiping the reveal/verdict lines — the result stays
    /// readable until the next throw overwrites it (the wipe happens in <see cref="OnChoice"/>/<see cref="TickWaiting"/>).
    /// </summary>
    void EnterChoosingKeepingLastReveal()
    {
        _state = OverlayState.Choosing;
        _hasPendingResult = false;
        HideBanner();
        SetChoiceButtonsVisible(true);
    }

    void EnterNotice(string banner, Color color)
    {
        _state = OverlayState.Notice;
        _stateUntil = Time.unscaledTime + NoticeHoldSeconds;
        _revealText.text = string.Empty;
        _verdictText.gameObject.SetActive(false);
        SetChoiceButtonsVisible(false);
        ShowBanner(banner, color);
    }

    void OnChoice(SkeletonRpsChoice choice)
    {
        if (_state != OverlayState.Choosing || _player == null || _challenge == null)
            return;

        _state = OverlayState.Waiting;
        _waitingSince = Time.unscaledTime;
        _hasPendingResult = false;
        _verdictText.gameObject.SetActive(false);
        SetChoiceButtonsVisible(false);
        // Counted before submitting: offline and on the host the throw resolves inside the call below.
        _submittedThrow++;
        _challenge.SubmitLocalThrow(_player, choice);
    }

    void OnLeave() => SetShown(false);

    void SetShown(bool show)
    {
        if (_root != null)
            _root.SetActive(show);
        // The banner floats on the canvas above the panel (sibling of _root), so hide it explicitly.
        if (!show && _bannerText != null)
            _bannerText.gameObject.SetActive(false);
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
            _hasPendingResult = false;
            _challenge = null;
            _player = null;
            if (!PauseMenuController.BlocksGameplayInput)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }

    // =========================================================================================
    // UI construction (plate language; layout mirrors BlackjackOverlayController)
    // =========================================================================================

    void EnsureUiBuilt()
    {
        if (_root != null)
            return;

        Canvas canvas = CreateOwnedCanvas();

        const float W = 840f;
        const float H = 248f;

        _root = new GameObject("SkeletonRpsPanel");
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

        TMP_Text title = MakeLabel(_root.transform, "ROCK · PAPER · SCISSORS", 21f,
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.95f), TextAlignmentOptions.MidlineLeft,
            new Vector2(0f, 1f), new Vector2(26f, -14f), new Vector2(380f, 30f));
        title.characterSpacing = 6f;

        MakeLabel(_root.transform, "ONE GAME — WIN 2 ROUNDS", 14f, MenuTheme.Mist,
            TextAlignmentOptions.MidlineRight, new Vector2(1f, 1f), new Vector2(-26f, -14f),
            new Vector2(330f, 26f)).characterSpacing = 4f;

        BuildScoreRow();

        // Reveal + verdict lines (centre).
        _revealText = MakeLabel(_root.transform, "", 26f, MenuTheme.Bone, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0f, -104f), new Vector2(760f, 36f));
        _revealText.characterSpacing = 3f;
        _verdictText = MakeLabel(_root.transform, "", 19f, MenuTheme.Mist, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0f, -138f), new Vector2(760f, 26f));
        _verdictText.characterSpacing = 5f;
        _verdictText.gameObject.SetActive(false);

        // Match banner - big, top-center of the screen (matches the blackjack result banner placement).
        _bannerText = MakeLabel(canvas.transform, "", 58f, MenuTheme.AmberBright, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(1000f, 80f));
        _bannerText.characterSpacing = 6f;
        _bannerText.fontStyle = FontStyles.Bold;
        AddUnderlay(_bannerText);
        _bannerText.gameObject.SetActive(false);

        // Throw plates (bottom-left cluster) + walk away (bottom-right).
        _rockBtn = MakePlate("ROCK", MenuWidgets.PlateStyle.Primary, () => OnChoice(SkeletonRpsChoice.Rock),
            new Vector2(0f, 0f), new Vector2(36f, 24f), new Vector2(170f, 58f), 24f).gameObject;
        _paperBtn = MakePlate("PAPER", MenuWidgets.PlateStyle.Primary, () => OnChoice(SkeletonRpsChoice.Paper),
            new Vector2(0f, 0f), new Vector2(218f, 24f), new Vector2(170f, 58f), 24f).gameObject;
        _scissorsBtn = MakePlate("SCISSORS", MenuWidgets.PlateStyle.Primary, () => OnChoice(SkeletonRpsChoice.Scissors),
            new Vector2(0f, 0f), new Vector2(400f, 24f), new Vector2(184f, 58f), 24f).gameObject;
        MakePlate("WALK AWAY", MenuWidgets.PlateStyle.Danger, OnLeave,
            new Vector2(1f, 0f), new Vector2(-36f, 24f), new Vector2(196f, 58f), 21f);

        _root.SetActive(false);
    }

    void BuildScoreRow()
    {
        RectTransform row = new GameObject("ScoreRow", typeof(RectTransform)).GetComponent<RectTransform>();
        row.gameObject.layer = 5;
        row.SetParent(_root.transform, false);
        row.anchorMin = row.anchorMax = row.pivot = new Vector2(0.5f, 1f);
        row.anchoredPosition = new Vector2(0f, -58f);
        row.sizeDelta = new Vector2(460f, 26f);

        TMP_Text you = MakeLabel(row, "YOU", 16f, MenuTheme.Bone, TextAlignmentOptions.MidlineRight,
            new Vector2(0.5f, 0.5f), new Vector2(-150f, 0f), new Vector2(110f, 24f));
        you.characterSpacing = 4f;

        _playerPips = new Image[SkeletonRpsChallenge.RoundWinsToTakeMatch];
        for (int i = 0; i < _playerPips.Length; i++)
            _playerPips[i] = MakePip(row, new Vector2(-72f + i * 26f, 0f));

        TMP_Text mid = MakeLabel(row, "VS", 13f, MenuTheme.WithAlpha(MenuTheme.Mist, 0.7f), TextAlignmentOptions.Center,
            new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(40f, 24f));
        mid.characterSpacing = 3f;

        _skeletonPips = new Image[SkeletonRpsChallenge.RoundWinsToTakeMatch];
        for (int i = 0; i < _skeletonPips.Length; i++)
            _skeletonPips[i] = MakePip(row, new Vector2(46f + i * 26f, 0f));

        TMP_Text bones = MakeLabel(row, "BONES", 16f, MenuTheme.Bone, TextAlignmentOptions.MidlineLeft,
            new Vector2(0.5f, 0.5f), new Vector2(163f, 0f), new Vector2(130f, 24f));
        bones.characterSpacing = 4f;
    }

    Image MakePip(Transform parent, Vector2 anchoredPos)
    {
        Image pip = MenuWidgets.CreateImage(parent, "Pip", MenuTheme.RoundedRect(7),
            MenuTheme.WithAlpha(MenuTheme.Bone, 0.22f));
        RectTransform rt = pip.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = new Vector2(14f, 14f);
        return pip;
    }

    void RefreshPips(int playerWins, int skeletonWins)
    {
        if (_playerPips == null)
            return;
        for (int i = 0; i < _playerPips.Length; i++)
        {
            _playerPips[i].color = i < playerWins
                ? MenuTheme.AmberBright
                : MenuTheme.WithAlpha(MenuTheme.Bone, 0.22f);
        }
        for (int i = 0; i < _skeletonPips.Length; i++)
        {
            _skeletonPips[i].color = i < skeletonWins
                ? MenuTheme.BloodBright
                : MenuTheme.WithAlpha(MenuTheme.Bone, 0.22f);
        }
    }

    void SetChoiceButtonsVisible(bool visible)
    {
        if (_rockBtn != null)
            _rockBtn.SetActive(visible);
        if (_paperBtn != null)
            _paperBtn.SetActive(visible);
        if (_scissorsBtn != null)
            _scissorsBtn.SetActive(visible);
    }

    void ShowBanner(string text, Color color)
    {
        if (_bannerText == null)
            return;
        _bannerText.text = text;
        _bannerText.color = color;
        _bannerText.gameObject.SetActive(true);
    }

    void HideBanner()
    {
        if (_bannerText != null)
            _bannerText.gameObject.SetActive(false);
    }

    Canvas CreateOwnedCanvas()
    {
        GameObject canvasGo = new("SkeletonRpsCanvas");
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

    /// <summary>
    /// The canvas outlives scene loads, so the event system feeding it has to as well — a scene-bound one dies
    /// on the next level and leaves the plates unclickable.
    /// </summary>
    static void EnsureEventSystem()
    {
        if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null)
            return;
        GameObject es = new("EventSystem (SkeletonRps)");
        DontDestroyOnLoad(es);
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
        Vector2 pivotAnchor, Vector2 anchoredPos, Vector2 size, float fontSize)
    {
        MenuButtonFx fx = MenuWidgets.CreatePlate(_root.transform, "Btn_" + label, label, () => onClick?.Invoke(),
            style, size.y, fontSize);
        fx.suppressHoverAudio = true;
        RectTransform r = (RectTransform)fx.transform;
        r.anchorMin = pivotAnchor;
        r.anchorMax = pivotAnchor;
        r.pivot = pivotAnchor;
        r.anchoredPosition = anchoredPos;
        r.sizeDelta = size;
        return fx.button;
    }
}
