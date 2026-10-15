using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds and drives the entire main-menu UI at runtime (Menu scene). The root is just the logo
/// and a left button rail; anything with more to say (settings, the lobby) takes over as a
/// centered dedicated screen with the rail hidden and a BACK button to return.
/// Joining someone else's game is invite-only — there is no id to type in anywhere.
///
/// Each screen is a <see cref="UiNavScope"/> while it shows. A pad lands on PLAY OFFLINE, the first setting, or
/// the lobby's READY / START plate; B / Esc goes back from settings, and in the lobby asks to leave (the lobby has
/// no BACK on purpose).
/// </summary>
[DisallowMultipleComponent]
public sealed class MainMenuController : MonoBehaviour
{
    enum MenuScreen { Root, Settings, Lobby }

    const float CardWidth = 720f;

    // Root signpost plates — deliberately larger than the pause menu's rail, which keeps the defaults.
    const float NavHeight = 76f;
    const float NavFontSize = 29f;

    /// <summary>Corner radius of the lobby's survivor plates; smaller than a panel's so they read as cards, not boards.</summary>
    const int PortraitRadius = 12;

    /// <summary>Distance from the screen edge to every lobby corner group, so the four corners frame the hallway evenly.</summary>
    const float LobbyMargin = 80f;

    // Left-hand lobby column: the crew roster as bare elements in the top-left corner. No card —
    // the roster sits directly on the canvas, over the hallway.
    const float LobbyColumnWidth = 402f;

    /// <summary>Crew row for another player: name line over the voice slider (<see cref="MenuCrewVoiceRow"/>).</summary>
    const float CrewRowHeight = MenuCrewVoiceRow.RowHeight;
    /// <summary>Your own crew row is a single name line — you cannot mute or re-mix yourself.</summary>
    const float CrewSelfRowHeight = 30f;

    // Bottom-right dock: the survivor plates in one row over the lobby action button, both the
    // dock's full width. Four 112-wide plates with 12 between them make the 484.
    const float LobbyDockWidth = 484f;
    const float SurvivorPlateHeight = 158f;
    const float SurvivorPlateSpacing = 12f;
    const float LobbyActionHeight = 72f;

    // LEAVE LOBBY, bottom-left, centred on the action button's line.
    const float LeaveWidth = 240f;
    const float LeaveHeight = 56f;

    [Tooltip("Wordmark shown at the top of the menu. Assets/Branding is outside Resources, so this is a direct scene reference.")]
    [SerializeField] Sprite logoSprite;

    MultiplayerSessionController _session;
    MultiplayerSceneFlow _flow;

    MenuModal _modal;
    MenuFriendsPanel _friendsPanel;

    MenuScreenFader _rootFader;
    MenuScreenFader _settingsFader;
    MenuScreenFader _lobbyFader;
    UiNavScope _rootNavScope;
    UiNavScope _settingsNavScope;
    UiNavScope _lobbyNavScope;
    Button _playOfflineButton;
    MenuScreen _current = MenuScreen.Root;
    LobbyCharacterPreview _characterPreview;

    /// <summary>The lobby screen opens itself once per session; after that BACK stays respected.</summary>
    bool _lobbyAutoShown;

    struct CharacterCard
    {
        public Button Button;
        public Image Background;
        public Image Outline;
        public Image Portrait;
        public TextMeshProUGUI Name;
        public Image Ledge;
    }

    /// <summary>
    /// What the lobby's one action plate does when pressed. Everyone toggles between READY UP and READY;
    /// the host's plate becomes START once every player, the host included, is ready.
    /// </summary>
    enum LobbyAction { ReadyUp, Ready, Start }

    // lobby screen
    TextMeshProUGUI _lobbyGateLabel;
    TextMeshProUGUI _crewCountLabel;
    RectTransform _crewListRoot;
    RectTransform _playerListRoot;
    CharacterCard[] _characterCards;
    Button _lobbyActionButton;
    MenuButtonFx _lobbyActionFx;
    LobbyAction _lobbyAction;
    /// <summary>Host-only: the level picker only feeds the host's START.</summary>
    GameObject _levelSelectSection;
    /// <summary>The section the host deploys to; set by the top-right level picker.</summary>
    string _selectedScene = MultiplayerSceneFlow.GameSceneName;
    Button _inviteButton;

    /// <summary>
    /// One crew row per player. Rows are kept (not rebuilt) while the roster is unchanged so a voice
    /// slider is not yanked out from under the cursor every time someone toggles ready.
    /// </summary>
    struct CrewRow
    {
        public Image ReadyChip;
        public TextMeshProUGUI Name;
    }

    readonly List<ulong> _crewRowIds = new();
    readonly Dictionary<ulong, CrewRow> _crewRows = new();

    int _renderedLobbyHash = -1;

    void Awake()
    {
        if (MultiplayerBootstrap.Instance != null)
        {
            _flow = MultiplayerBootstrap.Instance.GetComponent<MultiplayerSceneFlow>();
            _session = MultiplayerBootstrap.Instance.GetComponent<MultiplayerSessionController>();
        }
        else
        {
            _flow = FindAnyObjectByType<MultiplayerSceneFlow>();
            _session = FindAnyObjectByType<MultiplayerSessionController>();
        }

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        MenuTheme.ApplyCursor();
        UiEventSystem.Ensure();

        // Tape treatment for the hallway behind the UI; disables itself when this menu goes away.
        if (GetComponent<MenuVhsFx>() == null)
            gameObject.AddComponent<MenuVhsFx>();

        BuildUi();

        // The selected survivor standing in the hallway while the lobby screen is up (3D, not UI).
        var previewGo = new GameObject(nameof(LobbyCharacterPreview));
        previewGo.transform.SetParent(transform, false);
        _characterPreview = previewGo.AddComponent<LobbyCharacterPreview>();

        if (_session != null)
        {
            _session.LobbyStateChanged += OnLobbyStateChanged;
            _session.SessionFailed += OnSessionFailed;
        }

        bool inLobby = HasLobbySession;
        _lobbyAutoShown = inLobby;
        ShowScreen(inLobby ? MenuScreen.Lobby : MenuScreen.Root, true);
        RefreshLobby();

        // A drop that happened in game was held for this menu (the return load built it fresh).
        OnSessionFailed();
    }

    void OnDestroy()
    {
        if (_session != null)
        {
            _session.LobbyStateChanged -= OnLobbyStateChanged;
            _session.SessionFailed -= OnSessionFailed;
        }

        UiEventSystem.Close(_rootNavScope);
        UiEventSystem.Close(_settingsNavScope);
        UiEventSystem.Close(_lobbyNavScope);
    }

    /// <summary>A refused or failed join, or a drop from a joined session: why, as one data line.</summary>
    void OnSessionFailed()
    {
        if (_session != null && _modal != null && _session.TryConsumeSessionFailure(out string title, out string body))
            _modal.Open(title, body, "OK", false, null);
    }

    // ================================================================ build

    void BuildUi()
    {
        Canvas canvas = MenuWidgets.CreateCanvas("MainMenuUI", 100, transform);
        Transform root = canvas.transform;

        // Light wash only — the hallway built into Menu.unity is the background.
        MenuBackdrop.BuildMenuScrim(root);
        _rootFader = BuildRootScreen(root);

        _settingsFader = BuildSettingsScreen(root);
        _lobbyFader = BuildLobbyScreen(root);

        _friendsPanel = MenuFriendsPanel.Create(root, _session);
        _modal = MenuModal.Create(root);

        _rootNavScope = new UiNavScope(_rootFader.transform, () => _playOfflineButton);
        _settingsNavScope = new UiNavScope(_settingsFader.transform, null, () => ShowScreen(MenuScreen.Root));
        // The action plate, not a survivor plate: your own plate is not clickable, and ready is the one thing everyone does.
        _lobbyNavScope = new UiNavScope(_lobbyFader.transform, () => _lobbyActionButton, ConfirmLeaveLobby);
    }

    /// <summary>
    /// The root menu: logo plus the button rail, faded as one unit. Dedicated screens hide all of
    /// it — leaving the logo up would collide with the centered card.
    /// </summary>
    MenuScreenFader BuildRootScreen(Transform root)
    {
        RectTransform screen = MenuWidgets.CreateStretched("Screen_Root", root);
        screen.gameObject.AddComponent<CanvasGroup>();
        var fader = screen.gameObject.AddComponent<MenuScreenFader>();
        BuildTitleBlock(screen);
        BuildNav(screen);
        return fader;
    }

    void BuildTitleBlock(Transform root)
    {
        RectTransform block = MenuWidgets.CreateRect("TitleBlock", root);
        block.anchorMin = new Vector2(0.5f, 1f);
        block.anchorMax = new Vector2(0.5f, 1f);
        block.pivot = new Vector2(0.5f, 1f);
        block.anchoredPosition = new Vector2(0f, -96f);
        block.sizeDelta = new Vector2(620f, 200f);

        // halo hugs the wordmark; it must not reach the nav stack or the plates wash out against the lit wall
        Image glow = MenuWidgets.CreateImage(block, "TitleGlow", MenuTheme.SoftGlow(), MenuTheme.WithAlpha(MenuTheme.Amber, 0.09f));
        RectTransform glowRt = glow.rectTransform;
        glowRt.anchorMin = new Vector2(0.5f, 1f);
        glowRt.anchorMax = new Vector2(0.5f, 1f);
        glowRt.pivot = new Vector2(0.5f, 0.5f);
        glowRt.anchoredPosition = new Vector2(0f, -78f);
        glowRt.sizeDelta = new Vector2(880f, 460f);
        var glowFlicker = glow.gameObject.AddComponent<UiFlicker>();
        glowFlicker.target = glow;
        glowFlicker.baseAlpha = 0.09f;
        glowFlicker.amplitude = 0.035f;
        glowFlicker.speed = 0.9f;

        if (logoSprite == null)
            Debug.LogWarning($"[{nameof(MainMenuController)}] No logo sprite assigned — the title block will be blank.", this);

        Image logo = MenuWidgets.CreateImage(block, "Logo", logoSprite, Color.white);
        logo.preserveAspect = true;
        RectTransform logoRt = logo.rectTransform;
        logoRt.anchorMin = new Vector2(0.5f, 1f);
        logoRt.anchorMax = new Vector2(0.5f, 1f);
        logoRt.pivot = new Vector2(0.5f, 1f);
        logoRt.anchoredPosition = Vector2.zero;
        logoRt.sizeDelta = new Vector2(560f, 172f);   // artwork is 1280x393
        logoRt.localRotation = Quaternion.Euler(0f, 0f, 1.1f);   // hung by hand, not printed
        var logoFlicker = logo.gameObject.AddComponent<UiFlicker>();
        logoFlicker.target = logo;
        logoFlicker.baseAlpha = 1f;
        logoFlicker.amplitude = 0.05f;
        logoFlicker.speed = 0.8f;
    }

    void BuildNav(Transform root)
    {
        RectTransform nav = MenuWidgets.CreateRect("Nav", root);
        nav.anchorMin = new Vector2(0.5f, 1f);
        nav.anchorMax = new Vector2(0.5f, 1f);
        nav.pivot = new Vector2(0.5f, 1f);
        nav.anchoredPosition = new Vector2(0f, -318f);
        nav.sizeDelta = new Vector2(700f, 420f);
        VerticalLayoutGroup layout = MenuWidgets.AddVertical(nav.gameObject, new RectOffset(0, 0, 0, 0), 16f, TextAnchor.UpperCenter);
        // signpost stack: each plank keeps its own measured width instead of stretching to the rail
        layout.childForceExpandWidth = false;

        _playOfflineButton = MenuWidgets.CreateNavButton(nav, "PLAY OFFLINE", OnPlayOfflineClicked, out _, true, true, NavHeight, NavFontSize);
        MenuWidgets.CreateNavButton(nav, "HOST GAME", OnHostGameClicked, out _, true, true, NavHeight, NavFontSize);
        MenuWidgets.CreateNavButton(nav, "SETTINGS", () => ShowScreen(MenuScreen.Settings), out _, true, true, NavHeight, NavFontSize);

        MenuWidgets.CreateNavButton(nav, "EXIT", () =>
        {
            _modal.Open("EXIT TO DESKTOP", string.Empty, "EXIT", true, QuitApplication);
        }, out _, true, true, NavHeight, NavFontSize);
    }

    /// <summary>A dedicated screen: centered on the canvas, shown with the button rail hidden.</summary>
    RectTransform CreateScreenRoot(Transform root, string name, out MenuScreenFader fader)
    {
        RectTransform screen = MenuWidgets.CreateRect(name, root);
        screen.anchorMin = new Vector2(0.5f, 0.5f);
        screen.anchorMax = new Vector2(0.5f, 0.5f);
        screen.pivot = new Vector2(0.5f, 0.5f);
        screen.anchoredPosition = Vector2.zero;
        screen.sizeDelta = new Vector2(CardWidth, 200f);
        screen.gameObject.AddComponent<CanvasGroup>();
        fader = screen.gameObject.AddComponent<MenuScreenFader>();
        fader.Hide(true);
        return screen;
    }

    /// <summary>A dedicated screen that lays itself out across the whole canvas (the lobby).</summary>
    RectTransform CreateFullScreenRoot(Transform root, string name, out MenuScreenFader fader)
    {
        RectTransform screen = MenuWidgets.CreateStretched(name, root);
        screen.gameObject.AddComponent<CanvasGroup>();
        fader = screen.gameObject.AddComponent<MenuScreenFader>();
        fader.Hide(true);
        return screen;
    }

    /// <summary>Bottom-of-card control that returns a dedicated screen to the button rail.</summary>
    void CreateBackButton(Transform card)
    {
        MenuWidgets.CreateSpacer(card, 6f);
        MenuWidgets.CreateGhostButton(card, "BACK", () => ShowScreen(MenuScreen.Root), false, 50f);
    }

    // ---------------------------------------------------------------- settings screen

    MenuScreenFader BuildSettingsScreen(Transform root)
    {
        RectTransform screen = CreateScreenRoot(root, "Screen_Settings", out MenuScreenFader fader);
        RectTransform card = MenuWidgets.CreateCard(screen, "Card", CardWidth);
        CenterCard(card);

        MenuWidgets.CreateText(card, "Title", "SETTINGS", 34f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 6f);
        MenuSettingsPanel.Build(card);
        CreateBackButton(card);
        return fader;
    }

    // ---------------------------------------------------------------- lobby screen

    /// <summary>
    /// The lobby is three corner groups on one margin: the crew roster top-left, the survivor plates
    /// and the one action plate (READY UP / READY / START) bottom-right, and the exit bottom-left. The
    /// host's level picker sits top-right on its own, so it can be dropped without touching the rest.
    /// Nothing here sits in a panel — the middle stays empty on purpose so the hallway, and whoever
    /// is patrolling it, remains the backdrop with the local player's picked survivor standing in it
    /// (see <see cref="LobbyCharacterPreview"/>).
    /// </summary>
    MenuScreenFader BuildLobbyScreen(Transform root)
    {
        RectTransform screen = CreateFullScreenRoot(root, "Screen_Lobby", out MenuScreenFader fader);

        BuildLobbyDock(screen);
        BuildLobbyCrewPanel(screen);
        BuildLobbyExitControls(screen);
        BuildLevelSelect(screen);

        return fader;
    }

    /// <summary>
    /// The crew roster in the top-left corner: everyone in the lobby by name, each with your local
    /// voice mix for them (<see cref="VoicePlayerMixSettings"/>) — a volume slider and a mic mute
    /// toggle. No card: these are loose elements sitting straight on the canvas, over the hallway.
    /// </summary>
    void BuildLobbyCrewPanel(Transform screen)
    {
        RectTransform column = MenuWidgets.CreateRect("Crew", screen);
        column.anchorMin = new Vector2(0f, 1f);
        column.anchorMax = new Vector2(0f, 1f);
        column.pivot = new Vector2(0f, 1f);
        column.anchoredPosition = new Vector2(LobbyMargin, -LobbyMargin);
        column.sizeDelta = new Vector2(LobbyColumnWidth, 100f);
        MenuWidgets.AddVertical(column.gameObject, new RectOffset(0, 0, 0, 0), 10f);
        // Pivot is the top edge, so the column grows downward as players join.
        var fitter = column.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        RectTransform headRow = MenuWidgets.CreateRow(column, "HeadRow", 34f, 10f);
        TextMeshProUGUI head = MenuWidgets.CreateText(headRow, "Head", "CREW", 26f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 6f);
        MenuWidgets.SetLayout(head, flexibleWidth: 1f);
        _crewCountLabel = MenuWidgets.CreateText(headRow, "Count", string.Empty, 13.5f, MenuTheme.Mist,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineRight, 3f);
        MenuWidgets.SetLayout(_crewCountLabel, minWidth: 170f, preferredWidth: 170f);

        // The one piece of chrome left: a rule doing the job the card's top edge used to.
        MenuWidgets.CreateHairline(column, MenuTheme.WithAlpha(MenuTheme.Bone, 0.16f));

        _crewListRoot = MenuWidgets.CreateRect("CrewList", column);
        MenuWidgets.AddVertical(_crewListRoot.gameObject, new RectOffset(0, 0, 4, 2), 8f);

        MenuWidgets.CreateSpacer(column, 2f);

        _inviteButton = MenuWidgets.CreateGhostButton(column, "INVITE FRIENDS", () =>
        {
            if (_friendsPanel != null)
                _friendsPanel.Open();
        }, false, 50f);
    }

    /// <summary>
    /// Bottom-right dock: the survivor plates in one row, then the ready count and the lobby's one
    /// action plate — pick who you play, then ready up. The dock's bottom edge is pinned to the
    /// margin and it grows upward, so the action plate never moves.
    /// </summary>
    void BuildLobbyDock(Transform screen)
    {
        RectTransform dock = MenuWidgets.CreateRect("Dock", screen);
        dock.anchorMin = new Vector2(1f, 0f);
        dock.anchorMax = new Vector2(1f, 0f);
        dock.pivot = new Vector2(1f, 0f);
        dock.anchoredPosition = new Vector2(-LobbyMargin, LobbyMargin);
        dock.sizeDelta = new Vector2(LobbyDockWidth, 100f);
        MenuWidgets.AddVertical(dock.gameObject, new RectOffset(0, 0, 0, 0), 0f);
        var fitter = dock.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        if (_session != null && _session.LobbyCharacterCount > 0)
        {
            BuildCharacterSelect(dock);
        }
        else
        {
            _playerListRoot = MenuWidgets.CreateRect("PlayerList", dock);
            MenuWidgets.AddVertical(_playerListRoot.gameObject, new RectOffset(0, 0, 0, 0), 8f);
        }

        MenuWidgets.CreateSpacer(dock, 28f);

        // Everyone sees the count, so a joiner can tell who the room is waiting on.
        _lobbyGateLabel = MenuWidgets.CreateText(dock, "Gate", string.Empty,
            14f, MenuTheme.Mist, MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 4f);
        MenuWidgets.SetLayout(_lobbyGateLabel, minHeight: 20f, preferredHeight: 20f);

        MenuWidgets.CreateSpacer(dock, 10f);

        // Built as a Nav plate so it carries the solid plate's shadow in all three states.
        _lobbyActionFx = MenuWidgets.CreatePlate(dock, "LobbyAction", "READY UP", OnLobbyActionClicked,
            MenuWidgets.PlateStyle.Nav, LobbyActionHeight, 24f);
        _lobbyActionButton = _lobbyActionFx.button;
        ApplyLobbyAction(LobbyAction.ReadyUp);
    }

    /// <summary>
    /// Which maze section the run starts on: a small LEVEL picker in the top-right corner, level with
    /// the CREW heading. Temporary — it will not ship in the final game, so it stands alone and
    /// nothing else is laid out around it. Host-only: only the host has a START, and only the host's
    /// <see cref="_selectedScene"/> is read (see <see cref="StartGameFromLobby"/>), so showing the
    /// picker to a joiner would be a control that does nothing.
    /// </summary>
    void BuildLevelSelect(Transform screen)
    {
        string[] scenes = MultiplayerSceneFlow.MazeSectionSceneNames;
        if (scenes == null || scenes.Length <= 1)
            return;

        const float height = 34f;
        const float headWidth = 64f;
        const float gap = 12f;
        float segmentsWidth = scenes.Length * 46f + (scenes.Length - 1) * 8f;

        RectTransform holder = MenuWidgets.CreateRow(screen, "LevelSelect", height, gap, TextAnchor.MiddleRight);
        holder.anchorMin = new Vector2(1f, 1f);
        holder.anchorMax = new Vector2(1f, 1f);
        holder.pivot = new Vector2(1f, 1f);
        holder.anchoredPosition = new Vector2(-LobbyMargin, -LobbyMargin);
        holder.sizeDelta = new Vector2(headWidth + gap + segmentsWidth, height);
        _levelSelectSection = holder.gameObject;

        TextMeshProUGUI head = MenuWidgets.CreateText(holder, "Head", "LEVEL", 13f,
            MenuTheme.WithAlpha(MenuTheme.Mist, 0.9f), MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineRight, 8f);
        MenuWidgets.SetLayout(head, minWidth: headWidth, preferredWidth: headWidth);

        var labels = new string[scenes.Length];
        for (int i = 0; i < scenes.Length; i++)
            labels[i] = (i + 1).ToString();

        MenuSegmented levelSelect = MenuWidgets.CreateSegmented(holder, labels, height);
        MenuWidgets.SetLayout(levelSelect, minWidth: segmentsWidth, preferredWidth: segmentsWidth,
            minHeight: height, preferredHeight: height);

        int startIndex = System.Array.IndexOf(scenes, _selectedScene);
        if (startIndex < 0)
            startIndex = 0;
        _selectedScene = scenes[startIndex];
        levelSelect.Set(startIndex, false);
        levelSelect.Changed += index =>
        {
            if (index >= 0 && index < scenes.Length)
                _selectedScene = scenes[index];
        };
    }


    // ---------------------------------------------------------------- crew roster

    /// <summary>
    /// Rebuilds the roster only when the set of players changes, then re-labels every row. Names arrive
    /// a beat after the client ids do (each peer announces its own), which is why the text is refreshed
    /// separately from the rows themselves.
    /// </summary>
    void RefreshCrewList(IReadOnlyList<LobbyPlayerState> players)
    {
        if (_crewListRoot == null)
            return;

        if (!CrewRosterMatches(players))
            RebuildCrewRows(players);

        string amber = ColorUtility.ToHtmlStringRGB(MenuTheme.Amber);

        for (int i = 0; i < players.Count; i++)
        {
            LobbyPlayerState player = players[i];
            if (!_crewRows.TryGetValue(player.ClientId, out CrewRow row))
                continue;

            if (row.Name != null)
            {
                string who = player.DisplayName;
                if (player.IsHost)
                    who += "   <color=#" + amber + ">HOST</color>";
                row.Name.text = who;
            }

            if (row.ReadyChip != null)
                row.ReadyChip.color = player.IsReady ? MenuTheme.Moss : MenuTheme.Faint;
        }
    }

    bool CrewRosterMatches(IReadOnlyList<LobbyPlayerState> players)
    {
        if (_crewRowIds.Count != players.Count)
            return false;
        for (int i = 0; i < players.Count; i++)
        {
            if (_crewRowIds[i] != players[i].ClientId)
                return false;
        }
        return true;
    }

    void RebuildCrewRows(IReadOnlyList<LobbyPlayerState> players)
    {
        for (int i = _crewListRoot.childCount - 1; i >= 0; i--)
            Destroy(_crewListRoot.GetChild(i).gameObject);
        _crewRows.Clear();
        _crewRowIds.Clear();

        if (players.Count == 0)
        {
            TextMeshProUGUI empty = MenuWidgets.CreateText(_crewListRoot, "Empty", "SYNCING", 13.5f,
                MenuTheme.Faint, MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 4f);
            MenuWidgets.SetLayout(empty, minHeight: 26f, preferredHeight: 26f);
            return;
        }

        ulong localClientId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;
        for (int i = 0; i < players.Count; i++)
        {
            ulong clientId = players[i].ClientId;
            _crewRowIds.Add(clientId);
            CreateCrewRow(clientId, clientId == localClientId);
        }
    }

    /// <summary>
    /// One roster row: ready pip, name, and — for everyone but you — the local voice controls. The
    /// slider and MUTE are listener-side only (<see cref="VoicePlayerMixSettings"/>): nothing about
    /// them is replicated, so the person you turn down is never told.
    /// </summary>
    void CreateCrewRow(ulong clientId, bool isLocal)
    {
        float height = isLocal ? CrewSelfRowHeight : CrewRowHeight;
        RectTransform root = MenuWidgets.CreateRect("Crew_" + clientId, _crewListRoot);
        MenuWidgets.SetLayout(root, minHeight: height, preferredHeight: height, flexibleWidth: 1f);

        Image chip = MenuWidgets.CreateImage(root, "ReadyChip", MenuTheme.Solid(), MenuTheme.Faint);
        RectTransform chipRt = chip.rectTransform;
        chipRt.anchorMin = new Vector2(0f, 1f);
        chipRt.anchorMax = new Vector2(0f, 1f);
        chipRt.pivot = new Vector2(0f, 1f);
        chipRt.anchoredPosition = new Vector2(1f, -8f);
        chipRt.sizeDelta = new Vector2(9f, 9f);
        chipRt.localRotation = Quaternion.Euler(0f, 0f, 45f);

        TextMeshProUGUI name = MenuWidgets.CreateText(root, "Name", string.Empty, 15f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 2f);
        name.richText = true;
        name.overflowMode = TextOverflowModes.Ellipsis;
        RectTransform nameRt = name.rectTransform;
        nameRt.anchorMin = new Vector2(0f, 1f);
        nameRt.anchorMax = new Vector2(1f, 1f);
        nameRt.pivot = new Vector2(0.5f, 1f);
        nameRt.offsetMin = new Vector2(20f, -25f);
        nameRt.offsetMax = new Vector2(isLocal ? 0f : -(MenuCrewVoiceRow.MicSize + 8f), 0f);

        _crewRows[clientId] = new CrewRow { ReadyChip = chip, Name = name };
        if (!isLocal)
            new MenuCrewVoiceRow(root, clientId, 20f);
    }

    void BuildLobbyExitControls(Transform screen)
    {
        // The only way out of the lobby: there is no BACK, so you cannot leave a live session
        // running behind the root menu. Centred on the action plate's line so the two bottom
        // corners share a baseline; the pivot stays centred so hover grows the plate in place.
        Button leave = MenuWidgets.CreateGhostButton(screen, "LEAVE LOBBY", ConfirmLeaveLobby, true, LeaveHeight);
        var leaveRt = (RectTransform)leave.transform;
        leaveRt.anchorMin = Vector2.zero;
        leaveRt.anchorMax = Vector2.zero;
        leaveRt.pivot = new Vector2(0.5f, 0.5f);
        leaveRt.anchoredPosition = new Vector2(LobbyMargin + LeaveWidth * 0.5f, LobbyMargin + LobbyActionHeight * 0.5f);
        leaveRt.sizeDelta = new Vector2(LeaveWidth, LeaveHeight);
    }

    /// <summary>LEAVE LOBBY, and the lobby's back press: always through the confirm, never straight out.</summary>
    void ConfirmLeaveLobby()
    {
        _modal.Open("LEAVE LOBBY", string.Empty, "LEAVE", true, () =>
        {
            if (_flow != null)
                _flow.ReturnToMainMenu();
            else if (_session != null)
                _session.ShutdownSession();
        });
    }

    /// <summary>
    /// One portrait plate per lobby character. Each plate doubles as the player roster: it shows
    /// who holds the character and their ready state. Exactly one player may hold each character.
    /// </summary>
    void BuildCharacterSelect(RectTransform dock)
    {
        // One row across the dock; the layout group splits its width evenly, so the plates stay
        // exactly aligned with the action plate's edges under them.
        RectTransform strip = MenuWidgets.CreateRect("Survivors", dock);
        MenuWidgets.SetLayout(strip, minHeight: SurvivorPlateHeight, preferredHeight: SurvivorPlateHeight);
        var row = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.spacing = SurvivorPlateSpacing;
        row.childAlignment = TextAnchor.MiddleCenter;
        row.childControlWidth = true;
        row.childControlHeight = true;
        row.childForceExpandWidth = true;
        row.childForceExpandHeight = true;

        int count = _session.LobbyCharacterCount;
        _characterCards = new CharacterCard[count];

        for (int i = 0; i < count; i++)
        {
            int index = i;
            MultiplayerProjectSettings.LobbyCharacter character = _session.GetLobbyCharacter(i);

            // no LayoutElement: the row drives the plate size
            RectTransform root = MenuWidgets.CreateRect("Character_" + i, strip);

            RectTransform plate = MenuWidgets.CreateStretched("Plate", root);

            // Smooth like the panels, not torn: these read as cards even though they are clickable.
            Image cardShadow = MenuWidgets.CreateImage(plate, "Shadow",
                MenuTheme.RoundedShadow(PortraitRadius, 18), MenuTheme.WithAlpha(MenuTheme.Ink, 0.5f));
            cardShadow.rectTransform.SetStretch();
            cardShadow.rectTransform.offsetMin = new Vector2(-9f, -13f);
            cardShadow.rectTransform.offsetMax = new Vector2(9f, 5f);

            // masked: the portrait fills the plate edge to edge, so its square corners would
            // otherwise sit outside the rounded silhouette
            Image bg = MenuWidgets.CreateRoundedMaskedFill(plate, "Bg", PortraitRadius,
                MenuTheme.WithAlpha(MenuTheme.Tile, 0.92f), true);

            Image portrait = MenuWidgets.CreateImage(bg.transform, "Portrait", character != null ? character.Portrait : null, Color.white);
            RectTransform portraitRt = portrait.rectTransform;
            portraitRt.anchorMin = new Vector2(0f, 0f);
            portraitRt.anchorMax = new Vector2(1f, 1f);
            portraitRt.offsetMin = new Vector2(3f, 40f);
            portraitRt.offsetMax = new Vector2(-3f, -3f);
            portrait.type = Image.Type.Simple;
            portrait.preserveAspect = true;

            Image shade = MenuWidgets.CreateImage(bg.transform, "Shade", MenuTheme.VerticalGradient(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.9f));
            RectTransform shadeRt = shade.rectTransform;
            shadeRt.anchorMin = new Vector2(0f, 0f);
            shadeRt.anchorMax = new Vector2(1f, 0f);
            shadeRt.pivot = new Vector2(0.5f, 0f);
            shadeRt.anchoredPosition = new Vector2(0f, 38f);
            shadeRt.sizeDelta = new Vector2(-6f, 42f);

            TextMeshProUGUI name = MenuWidgets.CreateText(plate, "Name",
                character != null ? character.DisplayName : ("SURVIVOR " + (i + 1)), 13f, MenuTheme.Bone,
                MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 2f);
            RectTransform nameRt = name.rectTransform;
            nameRt.anchorMin = new Vector2(0f, 0f);
            nameRt.anchorMax = new Vector2(1f, 0f);
            nameRt.pivot = new Vector2(0.5f, 0f);
            nameRt.anchoredPosition = new Vector2(0f, 11f);   // centred in the band now the owner line is gone
            nameRt.sizeDelta = new Vector2(0f, 18f);

            Image outline = MenuWidgets.CreateImage(plate, "Outline", MenuTheme.RoundedOutline(PortraitRadius, 1.8f), MenuTheme.WithAlpha(MenuTheme.Bone, 0.30f));
            outline.rectTransform.SetStretch();

            // torn ledge under the plate — lights up mustard under your pick
            Image ledge = MenuWidgets.CreateImage(plate, "Ledge", MenuTheme.TornBar(), Color.clear);
            RectTransform ledgeRt = ledge.rectTransform;
            ledgeRt.anchorMin = new Vector2(0.05f, 0f);
            ledgeRt.anchorMax = new Vector2(0.72f, 0f);
            ledgeRt.pivot = new Vector2(0f, 1f);
            ledgeRt.anchoredPosition = new Vector2(0f, -2f);
            ledgeRt.sizeDelta = new Vector2(0f, 4f);

            var button = root.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.22f, 1.18f, 1.08f, 1f);
            colors.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            colors.selectedColor = colors.highlightedColor;   // pad focus reads as hover
            colors.disabledColor = Color.white;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            button.onClick.AddListener(() =>
            {
                MenuUiAudio.PlayClick();
                if (_session != null)
                    _session.RequestSelectCharacter(index);
            });

            _characterCards[i] = new CharacterCard
            {
                Button = button,
                Background = bg,
                Outline = outline,
                Portrait = portrait,
                Name = name,
                Ledge = ledge,
            };
        }
    }

    void RefreshCharacterCards(IReadOnlyList<LobbyPlayerState> players)
    {
        if (_characterCards == null || _session == null)
            return;

        ulong localClientId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;
        bool selectionOpen = _session.CanSelectCharactersNow;

        for (int i = 0; i < _characterCards.Length; i++)
        {
            CharacterCard cardUi = _characterCards[i];

            // Ownership reads off the plate itself — amber frame/name/ledge for yours, greyed
            // portrait for someone else's. No badges or status lines.
            bool taken = false;
            bool mine = false;
            for (int p = 0; p < players.Count; p++)
            {
                if (players[p].CharacterIndex != i)
                    continue;
                taken = true;
                mine = players[p].ClientId == localClientId;
                break;
            }

            if (cardUi.Portrait != null)
                cardUi.Portrait.color = taken && !mine ? new Color(0.42f, 0.42f, 0.45f, 0.95f) : Color.white;

            if (cardUi.Outline != null)
                cardUi.Outline.color = mine
                    ? MenuTheme.Amber
                    : MenuTheme.WithAlpha(MenuTheme.Bone, taken ? 0.14f : 0.30f);

            if (cardUi.Background != null)
                cardUi.Background.color = mine
                    ? MenuTheme.WithAlpha(new Color(0.20f, 0.17f, 0.09f, 1f), 0.95f)
                    : MenuTheme.WithAlpha(MenuTheme.Tile, 0.92f);

            if (cardUi.Name != null)
                cardUi.Name.color = mine ? MenuTheme.AmberBright : (taken ? MenuTheme.Mist : MenuTheme.Bone);

            if (cardUi.Ledge != null)
                cardUi.Ledge.color = mine ? MenuTheme.WithAlpha(MenuTheme.Amber, 0.9f) : Color.clear;

            if (cardUi.Button != null)
                cardUi.Button.interactable = selectionOpen && !taken;
        }
    }

    static void CenterCard(RectTransform cardContent)
    {
        // CreateCard returns the content rect; its parent is the card root we place.
        var cardRoot = (RectTransform)cardContent.parent;
        cardRoot.anchorMin = new Vector2(0.5f, 0.5f);
        cardRoot.anchorMax = new Vector2(0.5f, 0.5f);
        cardRoot.pivot = new Vector2(0.5f, 0.5f);
        cardRoot.anchoredPosition = Vector2.zero;
        cardRoot.sizeDelta = new Vector2(CardWidth, cardRoot.sizeDelta.y);
    }

    // ================================================================ actions

    void OnPlayOfflineClicked()
    {
        if (_session == null)
        {
            _modal.Open("MULTIPLAYER UNAVAILABLE", "This scene has no multiplayer session.", "OK", false, null);
            return;
        }
        if (_flow != null)
            _flow.RequestOfflineGame();
        else
            _session.StartOfflineGame(MultiplayerSceneFlow.GameSceneName);
    }

    void OnHostGameClicked()
    {
        if (_session == null)
        {
            _modal.Open("MULTIPLAYER UNAVAILABLE", "This scene has no multiplayer session.", "OK", false, null);
            return;
        }
        // Already hosting (the player backed out of the lobby): reopen it rather than failing on
        // "a session is already running".
        if (HasLobbySession)
        {
            ShowScreen(MenuScreen.Lobby);
            return;
        }
        if (_flow != null)
            _flow.RequestHostOnlineLobby();
        else
            _session.StartOnlineHost();
    }

    /// <summary>
    /// Does what the plate currently says rather than re-deriving it, so a press can never do
    /// something other than its label — e.g. un-ready the host on a START press.
    /// </summary>
    void OnLobbyActionClicked()
    {
        if (_session == null)
            return;
        if (_lobbyAction == LobbyAction.Start)
            StartGameFromLobby();
        else
            _session.SetLocalPlayerReady(_lobbyAction == LobbyAction.ReadyUp);
    }

    void StartGameFromLobby()
    {
        if (_flow != null)
            _flow.RequestStartGameFromLobby(_selectedScene);
        else
            _session.StartGameFromLobby(_selectedScene);
    }

    /// <summary>
    /// Restyles the action plate for its state: a dark plate to READY UP, locked-in moss once READY,
    /// and the mustard primary for the host's START.
    /// </summary>
    void ApplyLobbyAction(LobbyAction action)
    {
        _lobbyAction = action;
        if (_lobbyActionFx == null)
            return;

        bool start = action == LobbyAction.Start;
        MenuWidgets.ApplyPlateStyle(_lobbyActionFx, start ? MenuWidgets.PlateStyle.Primary : MenuWidgets.PlateStyle.Nav);
        // ApplyPlateStyle only ever turns the ledge on, so coming back from START has to turn it off.
        _lobbyActionFx.ledgeAlways = start;

        // Ready reads as a locked-in moss plate, not the mustard used for selection. Kept on START too:
        // the fade out of the selected state is then a moss-to-mustard crossfade instead of a snap.
        _lobbyActionFx.fillSelected = MenuTheme.Moss;
        _lobbyActionFx.frameSelected = MenuTheme.WithAlpha(new Color(0.24f, 0.29f, 0.13f, 1f), 0.95f);
        _lobbyActionFx.labelSelected = MenuTheme.InkOnAccent;
        _lobbyActionFx.SetSelected(action == LobbyAction.Ready);

        if (_lobbyActionFx.label != null)
        {
            _lobbyActionFx.label.text = action == LobbyAction.Start ? "START"
                : action == LobbyAction.Ready ? "READY"
                : "READY UP";
        }
    }

    void QuitApplication()
    {
        if (_flow != null)
        {
            _flow.QuitApplication();
            return;
        }
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ================================================================ state

    void ShowScreen(MenuScreen screen, bool instant = false)
    {
        _current = screen;
        // The rail and the dedicated screens are mutually exclusive — that is what makes a screen "dedicated".
        ToggleFader(_rootFader, screen == MenuScreen.Root, instant);
        ToggleFader(_settingsFader, screen == MenuScreen.Settings, instant);
        ToggleFader(_lobbyFader, screen == MenuScreen.Lobby, instant);

        // Hidden screens let go of navigation before the shown one takes it, so returning to the rail lands on the
        // plate that left it (SETTINGS, HOST GAME).
        if (screen != MenuScreen.Root)
            UiEventSystem.Close(_rootNavScope);
        if (screen != MenuScreen.Settings)
            UiEventSystem.Close(_settingsNavScope);
        if (screen != MenuScreen.Lobby)
            UiEventSystem.Close(_lobbyNavScope);
        UiEventSystem.Open(screen == MenuScreen.Root ? _rootNavScope
            : screen == MenuScreen.Settings ? _settingsNavScope
            : _lobbyNavScope);
    }

    static void ToggleFader(MenuScreenFader fader, bool show, bool instant)
    {
        if (fader == null)
            return;
        if (show)
            fader.Show(instant);
        else
            fader.Hide(instant);
    }

    /// <summary>
    /// A session the lobby screen belongs to. The offline run also starts a (loopback) host, but it
    /// goes straight into the level — showing it the lobby would flash the screen for a frame.
    /// </summary>
    bool HasLobbySession => _session != null && _session.IsSessionActive && !_session.IsOfflineSession;

    void OnLobbyStateChanged()
    {
        // Opens itself when the session first comes up (hosting, or landing here from an invite).
        // Only once per session, or BACK would be undone by the next player joining.
        if (HasLobbySession && _session.LobbyPlayers.Count > 0 && !_lobbyAutoShown)
        {
            _lobbyAutoShown = true;
            ShowScreen(MenuScreen.Lobby);
        }
        RefreshLobby();
    }

    void Update()
    {
        if (!HasLobbySession)
        {
            _lobbyAutoShown = false;
            // session ended while looking at the lobby -> back to the buttons
            if (_current == MenuScreen.Lobby)
                ShowScreen(MenuScreen.Root);
        }

        // Only your own pick, only while the lobby screen itself is up — every other screen (and a
        // dead session) tears the preview down.
        if (_characterPreview != null)
        {
            _characterPreview.Apply(_current == MenuScreen.Lobby && HasLobbySession,
                _session != null ? _session.LocalCharacterIndex : -1);
        }

        RefreshLobbyDynamic();
    }

    void RefreshLobbyDynamic()
    {
        if (_session == null || _current != MenuScreen.Lobby)
            return;

        IReadOnlyList<LobbyPlayerState> lobbyPlayers = _session.LobbyPlayers;
        int ready = 0;
        for (int i = 0; i < lobbyPlayers.Count; i++)
        {
            if (lobbyPlayers[i].IsReady)
                ready++;
        }
        bool everyoneReady = _session.AreAllLobbyPlayersReady && lobbyPlayers.Count > 0;

        if (_crewCountLabel != null)
        {
            // headcount only — the ready count sits over the action plate
            int slots = Mathf.Max(_session.LobbyCharacterCount, lobbyPlayers.Count);
            _crewCountLabel.text = $"{lobbyPlayers.Count}/{slots}";
            _crewCountLabel.color = MenuTheme.Mist;
        }

        bool isHost = _session.IsLobbyHost;
        if (_levelSelectSection != null && _levelSelectSection.activeSelf != isHost)
            _levelSelectSection.SetActive(isHost);

        if (_lobbyGateLabel != null)
        {
            _lobbyGateLabel.text = everyoneReady ? "ALL READY" : $"{ready}/{lobbyPlayers.Count} READY";
            _lobbyGateLabel.color = everyoneReady ? MenuTheme.Moss : MenuTheme.Mist;
        }

        // The host's own ready is part of "everyone", so START only ever follows the host's READY;
        // anyone joining or un-readying drops the host back to READY.
        LobbyAction action = !_session.IsLocalReady ? LobbyAction.ReadyUp
            : isHost && everyoneReady ? LobbyAction.Start
            : LobbyAction.Ready;
        if (action != _lobbyAction)
            ApplyLobbyAction(action);
        if (_lobbyActionButton != null)
            _lobbyActionButton.interactable = !_session.IsGameStartRequested;

        // Kept visible but dimmed when there is nothing to invite into, so the row doesn't pop in and out.
        if (_inviteButton != null)
            _inviteButton.interactable = _session.CanInviteFriends;
    }

    void RefreshLobby()
    {
        if (_session == null)
            return;

        IReadOnlyList<LobbyPlayerState> players = _session.LobbyPlayers;

        int hash = players.Count;
        for (int i = 0; i < players.Count; i++)
        {
            LobbyPlayerState p = players[i];
            hash = hash * 31 + p.ClientId.GetHashCode();
            hash = hash * 31 + (p.IsReady ? 1 : 0);
            hash = hash * 31 + (p.IsHost ? 2 : 0);
            hash = hash * 31 + p.CharacterIndex;
            hash = hash * 31 + (p.DisplayName != null ? p.DisplayName.GetHashCode() : 0);
        }
        hash = hash * 31 + (_session.IsGameStartRequested ? 1 : 0);
        hash = hash * 31 + (_session.CanSelectCharactersNow ? 2 : 0);
        if (hash == _renderedLobbyHash)
            return;
        _renderedLobbyHash = hash;

        RefreshCrewList(players);

        if (_characterCards != null)
        {
            RefreshCharacterCards(players);
            return;
        }

        if (_playerListRoot == null)
            return;

        for (int i = _playerListRoot.childCount - 1; i >= 0; i--)
            Destroy(_playerListRoot.GetChild(i).gameObject);

        if (players.Count == 0)
        {
            MenuWidgets.CreateText(_playerListRoot, "Empty", "SYNCING", 14f, MenuTheme.Faint,
                MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 4f);
            return;
        }

        ulong localClientId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : ulong.MaxValue;

        for (int i = 0; i < players.Count; i++)
        {
            LobbyPlayerState p = players[i];
            RectTransform row = MenuWidgets.CreateRect("Player_" + p.ClientId, _playerListRoot);
            MenuWidgets.SetLayout(row, minHeight: 50f, preferredHeight: 50f);

            Image bg = MenuWidgets.CreateImage(row, "Bg", MenuTheme.RoundedRect(14), MenuTheme.WithAlpha(MenuTheme.Tile, 0.88f));
            bg.rectTransform.SetStretch();
            Image frame = MenuWidgets.CreateImage(row, "Frame", MenuTheme.RoundedOutline(14, 1.6f), MenuTheme.WithAlpha(MenuTheme.Bone, 0.18f));
            frame.rectTransform.SetStretch();

            Image chip = MenuWidgets.CreateImage(row, "ReadyChip", MenuTheme.Solid(), p.IsReady ? MenuTheme.Moss : MenuTheme.Faint);
            RectTransform chipRt = chip.rectTransform;
            chipRt.anchorMin = new Vector2(0f, 0.5f);
            chipRt.anchorMax = new Vector2(0f, 0.5f);
            chipRt.anchoredPosition = new Vector2(24f, 0f);
            chipRt.sizeDelta = new Vector2(10f, 10f);
            chipRt.localRotation = Quaternion.Euler(0f, 0f, 45f);
            if (!p.IsReady)
            {
                var pulse = chip.gameObject.AddComponent<UiPulse>();
                pulse.target = chip;
                pulse.minAlpha = 0.25f;
                pulse.maxAlpha = 0.8f;
            }

            string who = "PLAYER " + p.ClientId;
            if (p.IsHost)
                who += "   <color=#" + ColorUtility.ToHtmlStringRGB(MenuTheme.Amber) + ">HOST</color>";
            if (p.ClientId == localClientId)
                who += "   <color=#" + ColorUtility.ToHtmlStringRGB(MenuTheme.Faint) + ">YOU</color>";

            TextMeshProUGUI name = MenuWidgets.CreateText(row, "Name", who, 16f, MenuTheme.Bone,
                MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 2.5f);
            RectTransform nameRt = name.rectTransform;
            nameRt.anchorMin = Vector2.zero;
            nameRt.anchorMax = Vector2.one;
            nameRt.offsetMin = new Vector2(46f, 0f);
            nameRt.offsetMax = new Vector2(-130f, 0f);
            name.richText = true;

            TextMeshProUGUI state = MenuWidgets.CreateText(row, "State", p.IsReady ? "READY" : "—", 13.5f,
                p.IsReady ? MenuTheme.Moss : MenuTheme.Faint, MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineRight, 4f);
            RectTransform stateRt = state.rectTransform;
            stateRt.anchorMin = new Vector2(1f, 0f);
            stateRt.anchorMax = new Vector2(1f, 1f);
            stateRt.pivot = new Vector2(1f, 0.5f);
            stateRt.anchoredPosition = new Vector2(-20f, 0f);
            stateRt.sizeDelta = new Vector2(120f, 0f);
        }
    }
}
