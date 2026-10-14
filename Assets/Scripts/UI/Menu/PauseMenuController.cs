using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Esc-toggled in-game pause menu (replaces the old F8 IMGUI overlay): Resume, Settings
/// (audio / environment light / voice), Crew (per-player voice volume and mute, online only), Leave Session,
/// Quit. Lives on the multiplayer bootstrap object so it persists across scene loads; only active in maze
/// gameplay scenes. Gameplay scripts poll <see cref="BlocksGameplayInput"/> to mute player input while open.
///
/// On a pad: Start opens it on RESUME and walks back one layer per press (modal, settings / crew card, the menu).
/// B and Esc walk back the same way, through <see cref="UiEventSystem"/>, which owns "back" for whichever screen is
/// on top; the settings and crew cards and the confirm modal are navigation scopes of their own.
/// </summary>
[DisallowMultipleComponent]
public sealed class PauseMenuController : MonoBehaviour
{
    static bool s_open;

    /// <summary>
    /// True while a full-screen menu owns the mouse: this pause menu, or the game-over card
    /// (<see cref="TeamWipeSequence"/>). Player look/move, the spectator orbit and every cursor re-lock check it.
    /// </summary>
    public static bool BlocksGameplayInput => s_open || TeamWipeSequence.IsRunning;

    MultiplayerSessionController _session;
    MultiplayerSceneFlow _flow;

    GameObject _root;
    MenuModal _modal;
    MenuScreenFader _settingsFader;
    MenuSettingsPanel _settingsPanel;
    TextMeshProUGUI _statusLabel;
    TextMeshProUGUI _sceneLabel;
    TMP_Text _leaveLabel;
    UnityEngine.UI.Button _resumeButton;
    bool _isOpen;
    bool _settingsOpen;

    // Pad navigation: the menu itself (lands on RESUME, back = resume), and each right-hand card while it is out
    // (lands on its first control, back = put the card away).
    UiNavScope _navScope;
    UiNavScope _settingsNavScope;
    UiNavScope _crewNavScope;

    // CREW card: your voice mix for everyone else, the same rows the lobby roster has (MenuCrewVoiceRow). The
    // lobby is the only other place to set it, and voice doesn't run there, so this is where it gets used.
    GameObject _crewNav;
    MenuScreenFader _crewFader;
    RectTransform _crewListRoot;
    bool _crewOpen;
    bool _crewBuilt;

    /// <summary>
    /// One row per other player. Kept (not rebuilt) while the roster is unchanged, as in the lobby, so a slider
    /// isn't yanked out from under the cursor.
    /// </summary>
    struct CrewRow
    {
        public TextMeshProUGUI Name;
        public MenuCrewVoiceRow Voice;
    }

    readonly List<ulong> _crewRowIds = new();
    readonly List<ulong> _crewIdScratch = new();
    readonly Dictionary<ulong, CrewRow> _crewRows = new();

    void Awake()
    {
        TryGetComponent(out _session);
        TryGetComponent(out _flow);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (_isOpen)
            s_open = false;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Close(true);
    }

    static bool InGameplayScene()
    {
        Scene active = SceneManager.GetActiveScene();
        return active.IsValid() && MultiplayerSceneFlow.IsMazeGameplayScene(active.name);
    }

    void Update()
    {
        if (!InGameplayScene())
        {
            if (_isOpen)
                Close(true);
            return;
        }

        // The game-over card owns the screen and the cursor until its own LEAVE; this menu would open underneath
        // it (lower sort order) and its close would re-lock the cursor.
        if (TeamWipeSequence.IsRunning)
        {
            if (_isOpen)
                Close(true);
            return;
        }

        bool togglePressed =
            (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            || (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);

        // Esc (and pad B) is "back" for whatever screen is on top, and UiEventSystem has already spent it this frame
        // if one was showing — this menu's own layers included, or an overlay that Esc just closed. What still
        // reaches here is Esc with nothing open, or Start.
        if (UiEventSystem.UiPressConsumedThisFrame)
            togglePressed = false;

        // While the skeleton's panel is up it closes itself on cancel (Start included); don't ride the same press
        // into pause. Once pause is already open the press is ours again, so it can still be closed.
        if (!_isOpen && SkeletonRpsOverlayController.IsInteractive)
            togglePressed = false;

        if (togglePressed)
        {
            if (!_isOpen)
                Open();
            else
                BackOneLayer();
            UiEventSystem.ConsumeUiPress();
        }

        if (_isOpen)
            RefreshDynamic();
    }

    /// <summary>Start (and Esc, should it get here) walks back the way B does: modal, then a card, then the menu.</summary>
    void BackOneLayer()
    {
        if (_modal != null && _modal.IsOpen)
            _modal.Close();
        else if (_settingsOpen)
            ShowSettings(false);
        else if (_crewOpen)
            ShowCrew(false);
        else
            Close(false);
    }

    // ================================================================ open / close

    public void Open()
    {
        if (_isOpen)
            return;

        if (_root == null)
            BuildUi();

        _root.SetActive(true);
        ShowSettings(false, true);
        ShowCrew(false, true);
        _isOpen = true;
        s_open = true;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        MenuTheme.ApplyCursor();

        UiEventSystem.Ensure();
        RefreshStatics();
        RefreshDynamic();
        UiEventSystem.Open(_navScope);
    }

    public void Close(bool instant)
    {
        _isOpen = false;
        s_open = false;
        _settingsOpen = false;
        _crewOpen = false;

        if (_root != null)
            _root.SetActive(false);
        if (_modal != null)
            _modal.Close();

        // Cards first and the menu last: the selection handed back is whatever was focused before pausing (an
        // overlay's plate underneath), not a card's opener.
        UiEventSystem.Close(_settingsNavScope);
        UiEventSystem.Close(_crewNavScope);
        UiEventSystem.Close(_navScope);

        // The shared EventSystem outlives this menu (it used to own a throwaway one), so let go of a pause plate it
        // still has selected rather than carry that selection into gameplay.
        EventSystem eventSystem = EventSystem.current;
        if (eventSystem != null && _root != null && eventSystem.currentSelectedGameObject != null
            && eventSystem.currentSelectedGameObject.transform.IsChildOf(_root.transform))
            eventSystem.SetSelectedGameObject(null);

        // Re-lock the cursor here, exactly as every other blocking overlay does on close (blackjack, skeleton
        // RPS, prize counter). The old note claimed PlayerController picks this up once BlocksGameplayInput
        // clears, but nothing watches that flag: ApplyCursorLock only runs on a local-control transition, which
        // pausing never causes. So the cursor stayed free after unpausing until the player clicked — and that
        // recapture click fell straight through into an unintended punch or throw.
        if (InGameplayScene()
            && !TeamWipeSequence.IsRunning
            && !BlackjackOverlayController.IsInteractive
            && !SkeletonRpsOverlayController.IsInteractive
            && !CarnivalStoreOverlayController.IsInteractive)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    // ================================================================ build

    void BuildUi()
    {
        Canvas canvas = MenuWidgets.CreateCanvas("PauseMenuUI", 4000, transform);
        _root = canvas.gameObject;
        Transform root = canvas.transform;

        MenuBackdrop.Build(root);

        // ---- left column
        RectTransform block = MenuWidgets.CreateRect("TitleBlock", root);
        block.anchorMin = new Vector2(0f, 1f);
        block.anchorMax = new Vector2(0f, 1f);
        block.pivot = new Vector2(0f, 1f);
        block.anchoredPosition = new Vector2(150f, -150f);
        block.sizeDelta = new Vector2(620f, 240f);

        TextMeshProUGUI overline = MenuWidgets.CreateText(block, "Overline", "DETOUR", 16f,
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.9f), MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 11f);
        SetTop(overline.rectTransform, 0f, 24f);

        TextMeshProUGUI misprint = MenuWidgets.CreateText(block, "TitleMisprint", "PAUSED", 86f,
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.8f), MenuWidgets.FontKind.Display,
            TextAlignmentOptions.Left, 5f, FontStyles.Bold);
        SetTop(misprint.rectTransform, -30f, 100f);
        misprint.rectTransform.anchoredPosition += new Vector2(7f, -5f);

        TextMeshProUGUI title = MenuWidgets.CreateText(block, "Title", "PAUSED", 86f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 5f, FontStyles.Bold);
        SetTop(title.rectTransform, -30f, 100f);

        _sceneLabel = MenuWidgets.CreateText(block, "Scene", string.Empty, 14.5f, MenuTheme.Mist,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 6f);
        SetTop(_sceneLabel.rectTransform, -140f, 24f);

        // ---- nav
        RectTransform nav = MenuWidgets.CreateRect("Nav", root);
        nav.anchorMin = new Vector2(0f, 0f);
        nav.anchorMax = new Vector2(0f, 1f);
        nav.pivot = new Vector2(0f, 1f);
        nav.anchoredPosition = new Vector2(150f, -440f);
        nav.sizeDelta = new Vector2(430f, 320f);
        MenuWidgets.AddVertical(nav.gameObject, new RectOffset(0, 0, 0, 0), 12f);

        _resumeButton = MenuWidgets.CreateNavButton(nav, "RESUME", () => Close(false), out _);
        MenuWidgets.CreateNavButton(nav, "SETTINGS", () => ShowSettings(!_settingsOpen), out _);
        _crewNav = MenuWidgets.CreateNavButton(nav, "CREW", () => ShowCrew(!_crewOpen), out _).gameObject;

        MenuWidgets.CreateNavButton(nav, "LEAVE SESSION", () =>
        {
            _modal.Open("LEAVE SESSION", string.Empty, "LEAVE", true, () =>
            {
                Close(true);
                if (_flow != null)
                    _flow.ReturnToMainMenu();
            });
        }, out MenuButtonFx leaveFx);
        _leaveLabel = leaveFx != null ? leaveFx.label : null;

        MenuWidgets.CreateNavButton(nav, "QUIT TO DESKTOP", () =>
        {
            _modal.Open("QUIT TO DESKTOP", string.Empty, "QUIT", true, () =>
                {
                    if (_flow != null)
                        _flow.QuitApplication();
                    else
                        Application.Quit();
                });
        }, out _);

        // ---- footer status
        RectTransform footer = MenuWidgets.CreateRect("Footer", root);
        footer.anchorMin = new Vector2(0f, 0f);
        footer.anchorMax = new Vector2(0f, 0f);
        footer.pivot = new Vector2(0f, 0f);
        footer.anchoredPosition = new Vector2(150f, 42f);
        footer.sizeDelta = new Vector2(900f, 52f);

        _statusLabel = MenuWidgets.CreateText(footer, "Status", string.Empty, 14f, MenuTheme.Faint,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.BottomLeft);
        RectTransform statusRt = _statusLabel.rectTransform;
        statusRt.anchorMin = Vector2.zero;
        statusRt.anchorMax = Vector2.one;
        statusRt.offsetMin = Vector2.zero;
        statusRt.offsetMax = Vector2.zero;
        _statusLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _statusLabel.overflowMode = TextOverflowModes.Ellipsis;

        // ---- settings panel (right)
        RectTransform screen = MenuWidgets.CreateRect("Screen_Settings", root);
        screen.anchorMin = new Vector2(1f, 0.5f);
        screen.anchorMax = new Vector2(1f, 0.5f);
        screen.pivot = new Vector2(1f, 0.5f);
        screen.anchoredPosition = new Vector2(-150f, 0f);
        screen.sizeDelta = new Vector2(720f, 200f);
        screen.gameObject.AddComponent<CanvasGroup>();
        _settingsFader = screen.gameObject.AddComponent<MenuScreenFader>();

        RectTransform card = MenuWidgets.CreateCard(screen, "Card", 720f);
        var cardRoot = (RectTransform)card.parent;
        cardRoot.anchorMin = new Vector2(1f, 0.5f);
        cardRoot.anchorMax = new Vector2(1f, 0.5f);
        cardRoot.pivot = new Vector2(1f, 0.5f);
        cardRoot.anchoredPosition = Vector2.zero;

        MenuWidgets.CreateText(card, "Title", "SETTINGS", 34f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 6f);
        _settingsPanel = MenuSettingsPanel.Build(card);

        // ---- crew panel (right; the same slot as settings, one or the other)
        RectTransform crewScreen = MenuWidgets.CreateRect("Screen_Crew", root);
        crewScreen.anchorMin = new Vector2(1f, 0.5f);
        crewScreen.anchorMax = new Vector2(1f, 0.5f);
        crewScreen.pivot = new Vector2(1f, 0.5f);
        crewScreen.anchoredPosition = new Vector2(-150f, 0f);
        crewScreen.sizeDelta = new Vector2(720f, 200f);
        crewScreen.gameObject.AddComponent<CanvasGroup>();
        _crewFader = crewScreen.gameObject.AddComponent<MenuScreenFader>();

        RectTransform crewCard = MenuWidgets.CreateCard(crewScreen, "Card", 720f);
        var crewCardRoot = (RectTransform)crewCard.parent;
        crewCardRoot.anchorMin = new Vector2(1f, 0.5f);
        crewCardRoot.anchorMax = new Vector2(1f, 0.5f);
        crewCardRoot.pivot = new Vector2(1f, 0.5f);
        crewCardRoot.anchoredPosition = Vector2.zero;

        MenuWidgets.CreateText(crewCard, "Title", "CREW", 34f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 6f);
        _crewListRoot = MenuWidgets.CreateRect("CrewList", crewCard);
        MenuWidgets.AddVertical(_crewListRoot.gameObject, new RectOffset(0, 0, 4, 2), 12f);

        _settingsFader.Hide(true);
        _crewFader.Hide(true);
        _modal = MenuModal.Create(root);

        _navScope = new UiNavScope(_root.transform, () => _resumeButton, () => Close(false));
        // Both cards land on their first control: the top slider, or the first crew row (nothing when SOLO).
        _settingsNavScope = new UiNavScope(screen, null, () => ShowSettings(false));
        _crewNavScope = new UiNavScope(crewScreen, null, () => ShowCrew(false));

        _root.SetActive(false);
    }

    static void SetTop(RectTransform rt, float y, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(4f, y);
        rt.sizeDelta = new Vector2(0f, height);
    }

    void ShowSettings(bool show, bool instant = false)
    {
        if (show)
            ShowCrew(false, instant);

        _settingsOpen = show;
        if (_settingsFader == null)
            return;
        if (show)
        {
            if (_settingsPanel != null)
                _settingsPanel.SyncFromManagers();
            _settingsFader.Show(instant);
            UiEventSystem.Open(_settingsNavScope);
        }
        else
        {
            _settingsFader.Hide(instant);
            UiEventSystem.Close(_settingsNavScope);
        }
    }

    void ShowCrew(bool show, bool instant = false)
    {
        if (show)
            ShowSettings(false, instant);

        _crewOpen = show;
        if (_crewFader == null)
            return;
        if (show)
        {
            RefreshCrew(true);
            _crewFader.Show(instant);
            UiEventSystem.Open(_crewNavScope);
        }
        else
        {
            _crewFader.Hide(instant);
            UiEventSystem.Close(_crewNavScope);
        }
    }

    /// <summary>A session other players can be in; a solo (Play Offline) run has no crew to mix.</summary>
    bool HasCrewSession()
    {
        return _session != null && _session.IsSessionActive && !_session.IsOfflineSession;
    }

    void RefreshStatics()
    {
        if (_sceneLabel != null)
        {
            Scene active = SceneManager.GetActiveScene();
            _sceneLabel.text = active.IsValid() ? active.name.ToUpperInvariant() : "???";
        }
        if (_leaveLabel != null)
            _leaveLabel.text = _session != null && _session.IsSessionActive ? "LEAVE SESSION" : "BACK TO MENU";
        if (_crewNav != null)
            _crewNav.SetActive(HasCrewSession());
    }

    void RefreshDynamic()
    {
        if (_crewOpen)
            RefreshCrew(false);

        if (_statusLabel == null || _session == null)
            return;
        _statusLabel.text = _session.CurrentTransportLabel.ToUpperInvariant() + "   —   " + _session.CurrentStatus;
    }

    // ================================================================ crew

    /// <summary>
    /// Everyone but you, from the session roster every peer keeps for the whole run — so a mid-run joiner gets a
    /// row as soon as the server announces them. Rows are rebuilt only when that set changes; names are re-labelled
    /// every pass because they arrive a beat after the ids. <paramref name="resyncMix"/> re-reads each kept row's
    /// volume and mute: the mix is cleared between sessions, and the next session can reuse the same client ids.
    /// </summary>
    void RefreshCrew(bool resyncMix)
    {
        if (_crewListRoot == null)
            return;

        IReadOnlyList<LobbyPlayerState> players = _session != null ? _session.LobbyPlayers : null;
        ulong localClientId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;

        _crewIdScratch.Clear();
        if (players != null)
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].ClientId != localClientId)
                    _crewIdScratch.Add(players[i].ClientId);
            }
        }

        if (!_crewBuilt || !CrewRosterMatches())
        {
            RebuildCrewRows();
        }
        else if (resyncMix)
        {
            foreach (CrewRow row in _crewRows.Values)
                row.Voice?.SyncFromSettings();
        }

        if (players == null)
            return;

        string amber = ColorUtility.ToHtmlStringRGB(MenuTheme.Amber);
        for (int i = 0; i < players.Count; i++)
        {
            LobbyPlayerState player = players[i];
            if (!_crewRows.TryGetValue(player.ClientId, out CrewRow row) || row.Name == null)
                continue;

            string who = player.DisplayName;
            if (player.IsHost)
                who += "   <color=#" + amber + ">HOST</color>";
            if (row.Name.text != who)
                row.Name.text = who;
        }
    }

    bool CrewRosterMatches()
    {
        if (_crewRowIds.Count != _crewIdScratch.Count)
            return false;
        for (int i = 0; i < _crewIdScratch.Count; i++)
        {
            if (_crewRowIds[i] != _crewIdScratch[i])
                return false;
        }
        return true;
    }

    void RebuildCrewRows()
    {
        for (int i = _crewListRoot.childCount - 1; i >= 0; i--)
            Destroy(_crewListRoot.GetChild(i).gameObject);
        _crewRows.Clear();
        _crewRowIds.Clear();
        _crewBuilt = true;

        if (_crewIdScratch.Count == 0)
        {
            TextMeshProUGUI empty = MenuWidgets.CreateText(_crewListRoot, "Empty", "SOLO", 13.5f,
                MenuTheme.Faint, MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 4f);
            MenuWidgets.SetLayout(empty, minHeight: 26f, preferredHeight: 26f);
            return;
        }

        for (int i = 0; i < _crewIdScratch.Count; i++)
        {
            ulong clientId = _crewIdScratch[i];
            _crewRowIds.Add(clientId);
            CreateCrewRow(clientId);
        }
    }

    /// <summary>Name line over the shared voice controls — the lobby's crew row without the ready pip.</summary>
    void CreateCrewRow(ulong clientId)
    {
        RectTransform root = MenuWidgets.CreateRect("Crew_" + clientId, _crewListRoot);
        MenuWidgets.SetLayout(root, minHeight: MenuCrewVoiceRow.RowHeight,
            preferredHeight: MenuCrewVoiceRow.RowHeight, flexibleWidth: 1f);

        TextMeshProUGUI name = MenuWidgets.CreateText(root, "Name", string.Empty, 15f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 2f);
        name.richText = true;
        name.overflowMode = TextOverflowModes.Ellipsis;
        RectTransform nameRt = name.rectTransform;
        nameRt.anchorMin = new Vector2(0f, 1f);
        nameRt.anchorMax = new Vector2(1f, 1f);
        nameRt.pivot = new Vector2(0.5f, 1f);
        nameRt.offsetMin = new Vector2(0f, -25f);
        nameRt.offsetMax = new Vector2(-(MenuCrewVoiceRow.MicSize + 8f), 0f);

        _crewRows[clientId] = new CrewRow { Name = name, Voice = new MenuCrewVoiceRow(root, clientId, 0f) };
    }
}
