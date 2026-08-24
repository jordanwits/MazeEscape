using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Builds and drives the entire main-menu UI at runtime (Menu scene). The root is just the logo
/// and a left button rail; anything with more to say (settings, the lobby) takes over as a
/// centered dedicated screen with the rail hidden and a BACK button to return.
/// Joining someone else's game is invite-only — there is no id to type in anywhere.
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

    // Left-hand lobby column: the crew roster as bare elements in the top-left corner. No card —
    // the roster sits directly on the canvas, over the hallway.
    const float LobbyColumnWidth = 402f;
    const float LobbyColumnInset = 76f;

    /// <summary>Square hit area of a crew row's microphone toggle.</summary>
    const float CrewMicSize = 26f;

    /// <summary>Crew row for another player: name line over the voice slider.</summary>
    const float CrewRowHeight = 68f;
    /// <summary>Your own crew row is a single name line — you cannot mute or re-mix yourself.</summary>
    const float CrewSelfRowHeight = 30f;

    // Lobby survivor grid: 2x2 on the right, above START. Values measured off the hand-placed
    // layout, then squared up — the GridLayoutGroup is what keeps the four plates exactly aligned.
    const float SurvivorPlateWidth = 108f;
    const float SurvivorPlateHeight = 154f;
    const float SurvivorGridSpacingX = 31f;
    const float SurvivorGridSpacingY = 50f;
    const float SurvivorGridInsetRight = 159f;

    /// <summary>
    /// Bottom of the plate grid. Chosen so the grid plus its caption sit centred in the gap between
    /// the top of the screen (the right column is otherwise empty since the crew card moved under
    /// LOBBY) and the START block (top edge y=212). The caption-to-grid-top block is 392 tall.
    /// </summary>
    const float SurvivorGridBottom = 362f;

    [Tooltip("Wordmark shown at the top of the menu. Assets/Branding is outside Resources, so this is a direct scene reference.")]
    [SerializeField] Sprite logoSprite;

    MultiplayerSessionController _session;
    MultiplayerSceneFlow _flow;

    MenuModal _modal;
    MenuFriendsPanel _friendsPanel;

    MenuScreenFader _rootFader;
    MenuScreenFader _settingsFader;
    MenuScreenFader _lobbyFader;
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

    // lobby screen
    TextMeshProUGUI _lobbyGateLabel;
    TextMeshProUGUI _crewCountLabel;
    RectTransform _crewListRoot;
    RectTransform _playerListRoot;
    CharacterCard[] _characterCards;
    Button _readyButton;
    MenuButtonFx _readyFx;
    TextMeshProUGUI _readyLabel;
    Button _startButton;
    GameObject _startSection;
    /// <summary>Host-only: the level picker only feeds the host's START.</summary>
    GameObject _levelSelectSection;
    /// <summary>The section the host deploys to; set by the top-centre level picker.</summary>
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
        public Slider Volume;
        public TextMeshProUGUI VolumeValue;
        public Button MicButton;
        public Image MicIcon;
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
        EnsureEventSystem();

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
        }

        bool inLobby = HasLobbySession;
        _lobbyAutoShown = inLobby;
        ShowScreen(inLobby ? MenuScreen.Lobby : MenuScreen.Root, true);
        RefreshLobby();
    }

    void OnDestroy()
    {
        if (_session != null)
        {
            _session.LobbyStateChanged -= OnLobbyStateChanged;
        }
    }

    static void EnsureEventSystem()
    {
        if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null)
            return;
        var go = new GameObject("EventSystem (Menu)");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
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

        MenuWidgets.CreateNavButton(nav, "PLAY OFFLINE", OnPlayOfflineClicked, out _, true, true, NavHeight, NavFontSize);
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
    /// The lobby spreads over the whole screen instead of stacking into one card: the crew roster
    /// top-left, the level picker top-centre, the host's deploy controls bottom-right, the exit
    /// bottom-left, and the survivor plates on the right. Nothing here sits in a panel — the middle
    /// stays empty on purpose so the hallway, and whoever is patrolling it, remains the backdrop with
    /// the local player's picked survivor standing in it (see <see cref="LobbyCharacterPreview"/>).
    /// </summary>
    MenuScreenFader BuildLobbyScreen(Transform root)
    {
        RectTransform screen = CreateFullScreenRoot(root, "Screen_Lobby", out MenuScreenFader fader);

        // Strip first so the edge elements draw over it if a wide screen ever brings them into contact.
        BuildLobbySurvivorStrip(screen);
        BuildLobbyCrewPanel(screen);
        BuildLobbyStartControls(screen);
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
        column.anchoredPosition = new Vector2(LobbyColumnInset, -LobbyColumnInset);
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
    /// Host's go button, bottom-right — mirroring the exits bottom-left with READY UP between them.
    /// No panel around it: START is one action, and a card here would crowd the survivor strip.
    /// </summary>
    void BuildLobbyStartControls(Transform screen)
    {
        RectTransform holder = MenuWidgets.CreateRect("StartControls", screen);
        holder.anchorMin = new Vector2(1f, 0f);
        holder.anchorMax = new Vector2(1f, 0f);
        holder.pivot = new Vector2(1f, 0f);
        holder.anchoredPosition = new Vector2(-118f, 108f);
        holder.sizeDelta = new Vector2(320f, 104f);
        MenuWidgets.AddVertical(holder.gameObject, new RectOffset(0, 0, 0, 0), 10f);
        _startSection = holder.gameObject;   // host-only

        _lobbyGateLabel = MenuWidgets.CreateText(holder, "Gate", string.Empty,
            14f, MenuTheme.Faint, MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 4f);
        MenuWidgets.SetLayout(_lobbyGateLabel, minHeight: 20f, preferredHeight: 20f);

        _startButton = MenuWidgets.CreatePrimaryButton(holder, "START", OnStartClicked, 70f);
    }

    /// <summary>
    /// Which maze section the run starts on, pinned top-centre where the wordmark sits on the root
    /// screen. Ships in release builds. Host-only: only the host has a START, and only the host's
    /// <see cref="_selectedScene"/> is read (see <see cref="OnStartClicked"/>), so showing the picker
    /// to a joiner would be a control that does nothing.
    /// </summary>
    void BuildLevelSelect(Transform screen)
    {
        string[] scenes = MultiplayerSceneFlow.MazeSectionSceneNames;
        if (scenes == null || scenes.Length <= 1)
            return;

        RectTransform holder = MenuWidgets.CreateRect("LevelSelect", screen);
        holder.anchorMin = new Vector2(0.5f, 1f);
        holder.anchorMax = new Vector2(0.5f, 1f);
        holder.pivot = new Vector2(0.5f, 1f);
        holder.anchoredPosition = new Vector2(0f, -76f);
        holder.sizeDelta = new Vector2(340f, 74f);
        MenuWidgets.AddVertical(holder.gameObject, new RectOffset(0, 0, 0, 0), 8f, TextAnchor.UpperCenter);
        _levelSelectSection = holder.gameObject;

        TextMeshProUGUI head = MenuWidgets.CreateText(holder, "Head", "START LEVEL", 13f,
            MenuTheme.WithAlpha(MenuTheme.Mist, 0.9f), MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 8f);
        MenuWidgets.SetLayout(head, minHeight: 20f, preferredHeight: 20f);

        var labels = new string[scenes.Length];
        for (int i = 0; i < scenes.Length; i++)
            labels[i] = (i + 1).ToString();

        MenuSegmented levelSelect = MenuWidgets.CreateSegmented(holder, labels, 40f);

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
        nameRt.offsetMax = new Vector2(isLocal ? 0f : -(CrewMicSize + 8f), 0f);

        var row = new CrewRow { ReadyChip = chip, Name = name };
        if (isLocal)
        {
            _crewRows[clientId] = row;
            return;
        }

        TextMeshProUGUI caption = MenuWidgets.CreateText(root, "VoiceCaption", "VOICE", 10.5f,
            MenuTheme.WithAlpha(MenuTheme.Faint, 0.9f), MenuWidgets.FontKind.Display,
            TextAlignmentOptions.MidlineLeft, 3f);
        RectTransform captionRt = caption.rectTransform;
        captionRt.anchorMin = new Vector2(0f, 0f);
        captionRt.anchorMax = new Vector2(0f, 0f);
        captionRt.pivot = new Vector2(0f, 0f);
        captionRt.anchoredPosition = new Vector2(20f, 6f);
        captionRt.sizeDelta = new Vector2(46f, 24f);

        Slider volume = MenuWidgets.CreateSlider(root);
        var volumeRt = (RectTransform)volume.transform;
        volumeRt.anchorMin = new Vector2(0f, 0f);
        volumeRt.anchorMax = new Vector2(1f, 0f);
        volumeRt.pivot = new Vector2(0.5f, 0f);
        volumeRt.offsetMin = new Vector2(70f, 7f);
        volumeRt.offsetMax = new Vector2(-56f, 29f);
        volume.SetValueWithoutNotify(VoicePlayerMixSettings.GetVolume(clientId));

        TextMeshProUGUI volumeValue = MenuWidgets.CreateText(root, "VolumeValue", string.Empty, 12.5f,
            MenuTheme.Mist, MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineRight, 1f);
        RectTransform volumeValueRt = volumeValue.rectTransform;
        volumeValueRt.anchorMin = new Vector2(1f, 0f);
        volumeValueRt.anchorMax = new Vector2(1f, 0f);
        volumeValueRt.pivot = new Vector2(1f, 0f);
        volumeValueRt.anchoredPosition = new Vector2(0f, 6f);
        volumeValueRt.sizeDelta = new Vector2(50f, 24f);
        volumeValue.text = Mathf.RoundToInt(volume.value * 100f) + "%";

        volume.onValueChanged.AddListener(v =>
        {
            VoicePlayerMixSettings.SetVolume(clientId, v);
            volumeValue.text = Mathf.RoundToInt(v * 100f) + "%";
        });

        // Bare glyph, no plate: the icon itself is the control, and the struck-through variant
        // carries the state (see MenuTheme.MicIcon).
        Image mic = MenuWidgets.CreateImage(root, "Mic", MenuTheme.MicIcon(false), MenuTheme.Bone, true);
        mic.preserveAspect = true;
        RectTransform micRt = mic.rectTransform;
        micRt.anchorMin = new Vector2(1f, 1f);
        micRt.anchorMax = new Vector2(1f, 1f);
        micRt.pivot = new Vector2(1f, 1f);
        micRt.anchoredPosition = new Vector2(0f, -1f);
        micRt.sizeDelta = new Vector2(CrewMicSize, CrewMicSize);

        var micButton = mic.gameObject.AddComponent<Button>();
        micButton.targetGraphic = mic;
        micButton.transition = Selectable.Transition.ColorTint;
        micButton.navigation = new Navigation { mode = Navigation.Mode.None };
        micButton.onClick.AddListener(() =>
        {
            MenuUiAudio.PlayClick();
            VoicePlayerMixSettings.ToggleMuted(clientId);
            ApplyCrewMuteVisual(clientId);
        });

        row.Volume = volume;
        row.VolumeValue = volumeValue;
        row.MicButton = micButton;
        row.MicIcon = mic;
        _crewRows[clientId] = row;
        ApplyCrewMuteVisual(clientId);
    }

    /// <summary>Pushes the stored mute state onto one row — rows are not rebuilt on a mute click.</summary>
    void ApplyCrewMuteVisual(ulong clientId)
    {
        if (!_crewRows.TryGetValue(clientId, out CrewRow row))
            return;

        bool muted = VoicePlayerMixSettings.IsMuted(clientId);

        if (row.MicIcon != null)
            row.MicIcon.sprite = MenuTheme.MicIcon(muted);

        // ColorTint overwrites the Image colour every state change, so the mute state has to live in
        // the button's palette rather than on the Image itself.
        if (row.MicButton != null)
        {
            ColorBlock colors = row.MicButton.colors;
            colors.normalColor = muted ? MenuTheme.BloodBright : MenuTheme.WithAlpha(MenuTheme.Bone, 0.85f);
            colors.highlightedColor = muted ? MenuTheme.Bone : MenuTheme.AmberBright;
            colors.pressedColor = muted ? MenuTheme.Blood : MenuTheme.Amber;
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = MenuTheme.Faint;
            row.MicButton.colors = colors;
        }

        if (row.Volume != null)
            row.Volume.interactable = !muted;
        if (row.VolumeValue != null)
            row.VolumeValue.color = muted ? MenuTheme.Faint : MenuTheme.Mist;
    }

    void BuildLobbySurvivorStrip(Transform screen)
    {
        RectTransform grid = MenuWidgets.CreateRect("Survivors", screen);
        grid.anchorMin = new Vector2(1f, 0f);
        grid.anchorMax = new Vector2(1f, 0f);
        grid.pivot = new Vector2(1f, 0f);
        grid.anchoredPosition = new Vector2(-SurvivorGridInsetRight, SurvivorGridBottom);
        grid.sizeDelta = new Vector2(
            SurvivorPlateWidth * 2f + SurvivorGridSpacingX,
            SurvivorPlateHeight * 2f + SurvivorGridSpacingY);

        if (_session != null && _session.LobbyCharacterCount > 0)
        {
            BuildCharacterSelect(grid);
        }
        else
        {
            _playerListRoot = MenuWidgets.CreateRect("PlayerList", grid);
            MenuWidgets.AddVertical(_playerListRoot.gameObject, new RectOffset(0, 0, 0, 0), 8f);
            _playerListRoot.SetStretch();
        }

        // header reads as a caption under the grid, not a title over it
        TextMeshProUGUI head = MenuWidgets.CreateText(screen, "SurvivorsHead", "SURVIVORS", 15f,
            MenuTheme.WithAlpha(MenuTheme.Mist, 0.85f), MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 10f);
        RectTransform headRt = head.rectTransform;
        headRt.anchorMin = new Vector2(1f, 0f);
        headRt.anchorMax = new Vector2(1f, 0f);
        headRt.pivot = new Vector2(1f, 1f);
        headRt.anchoredPosition = new Vector2(-SurvivorGridInsetRight, SurvivorGridBottom - 12f);
        headRt.sizeDelta = new Vector2(SurvivorPlateWidth * 2f + SurvivorGridSpacingX, 22f);

        // READY UP sits under your own portrait rather than in a side panel — it is the one thing
        // every player (host or not) has to press.
        RectTransform readyHolder = MenuWidgets.CreateRect("ReadyHolder", screen);
        readyHolder.anchorMin = new Vector2(0.5f, 0f);
        readyHolder.anchorMax = new Vector2(0.5f, 0f);
        readyHolder.pivot = new Vector2(0.5f, 0f);
        readyHolder.anchoredPosition = new Vector2(0f, 108f);
        readyHolder.sizeDelta = new Vector2(460f, 70f);

        _readyButton = MenuWidgets.CreateGhostButton(readyHolder, "READY UP", OnReadyClicked, false, 70f);
        ((RectTransform)_readyButton.transform).SetStretch();
        _readyFx = _readyButton.GetComponent<MenuButtonFx>();
        _readyLabel = _readyButton.GetComponentInChildren<TextMeshProUGUI>();
        if (_readyFx != null)
        {
            // ready reads as a locked-in moss plate, not the mustard used for selection
            _readyFx.fillSelected = MenuTheme.Moss;
            _readyFx.frameSelected = MenuTheme.WithAlpha(new Color(0.24f, 0.29f, 0.13f, 1f), 0.95f);
            _readyFx.labelSelected = MenuTheme.InkOnAccent;
        }
    }

    void BuildLobbyExitControls(Transform screen)
    {
        RectTransform holder = MenuWidgets.CreateRect("ExitControls", screen);
        holder.anchorMin = new Vector2(0f, 0f);
        holder.anchorMax = new Vector2(0f, 0f);
        holder.pivot = new Vector2(0f, 0f);
        holder.anchoredPosition = new Vector2(76f, 108f);
        holder.sizeDelta = new Vector2(260f, 70f);
        var layout = MenuWidgets.CreateRow(holder, "Row", 70f, 12f);
        ((RectTransform)layout).SetStretch();

        // The only way out of the lobby: there is no BACK, so you cannot leave a live session
        // running behind the root menu.
        Button leave = MenuWidgets.CreateGhostButton(layout, "LEAVE LOBBY", () =>
        {
            _modal.Open("LEAVE LOBBY", string.Empty, "LEAVE", true, () =>
            {
                if (_flow != null)
                    _flow.ReturnToMainMenu();
                else if (_session != null)
                    _session.ShutdownSession();
            });
        }, true, 56f);
        MenuWidgets.SetLayout(leave.transform, minWidth: 260f, preferredWidth: 260f, minHeight: 56f, preferredHeight: 56f);
    }

    /// <summary>
    /// One portrait plate per lobby character. Each plate doubles as the player roster: it shows
    /// who holds the character and their ready state. Exactly one player may hold each character.
    /// </summary>
    void BuildCharacterSelect(RectTransform gridHolder)
    {
        // A GridLayoutGroup owns the cell positions, so the plates cannot drift out of alignment
        // the way hand-placed ones do.
        var grid = gridHolder.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(SurvivorPlateWidth, SurvivorPlateHeight);
        grid.spacing = new Vector2(SurvivorGridSpacingX, SurvivorGridSpacingY);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 2;
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperLeft;

        int count = _session.LobbyCharacterCount;
        _characterCards = new CharacterCard[count];

        for (int i = 0; i < count; i++)
        {
            int index = i;
            MultiplayerProjectSettings.LobbyCharacter character = _session.GetLobbyCharacter(i);

            // no LayoutElement: the grid drives cell size
            RectTransform root = MenuWidgets.CreateRect("Character_" + i, gridHolder);

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
            colors.selectedColor = Color.white;
            colors.disabledColor = Color.white;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
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

    void OnReadyClicked()
    {
        if (_session != null)
            _session.SetLocalPlayerReady(!_session.IsLocalReady);
    }

    void OnStartClicked()
    {
        if (_session == null)
            return;
        if (_flow != null)
            _flow.RequestStartGameFromLobby(_selectedScene);
        else
            _session.StartGameFromLobby(_selectedScene);
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

        // crew line is for everyone, not just the host, so non-hosts can still see the gate
        if (_crewCountLabel != null)
        {
            // headcount only — the ready gate lives above START
            int slots = Mathf.Max(_session.LobbyCharacterCount, lobbyPlayers.Count);
            _crewCountLabel.text = $"{lobbyPlayers.Count}/{slots}";
            _crewCountLabel.color = MenuTheme.Mist;
        }

        bool isHost = _session.IsLobbyHost;
        if (_startSection != null && _startSection.activeSelf != isHost)
            _startSection.SetActive(isHost);
        if (_levelSelectSection != null && _levelSelectSection.activeSelf != isHost)
            _levelSelectSection.SetActive(isHost);
        if (isHost && _startButton != null)
        {
            _startButton.interactable = _session.CanHostStartGame;
            if (_lobbyGateLabel != null)
            {
                _lobbyGateLabel.text = everyoneReady ? "ALL READY" : $"{ready}/{lobbyPlayers.Count} READY";
                _lobbyGateLabel.color = everyoneReady ? MenuTheme.Moss : MenuTheme.Faint;
            }
        }

        bool localReady = _session.IsLocalReady;
        if (_readyLabel != null)
            _readyLabel.text = localReady ? "READY" : "READY UP";
        if (_readyFx != null)
            _readyFx.SetSelected(localReady);

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
