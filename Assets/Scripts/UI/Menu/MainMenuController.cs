using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Builds and drives the entire main-menu UI at runtime (Menu scene): left-rail navigation with
/// the title block, and contextual right-side panels for Play (LAN + Steam host/join),
/// the lobby (survivor select, ready-up, start), settings, and the playtest briefing.
/// </summary>
[DisallowMultipleComponent]
public sealed class MainMenuController : MonoBehaviour
{
    enum MenuScreen { Play, HowTo, Settings, Lobby }

    const float RightPanelMargin = -150f;
    const float CardWidth = 720f;

    MultiplayerSessionController _session;
    MultiplayerSceneFlow _flow;

    MenuModal _modal;
    MenuToast _toast;
    TextMeshProUGUI _statusLabel;

    MenuScreenFader _playFader;
    MenuScreenFader _howToFader;
    MenuScreenFader _settingsFader;
    MenuScreenFader _lobbyFader;
    MenuScreen _current = MenuScreen.Play;

    MenuButtonFx _navPlayFx;
    MenuButtonFx _navHowToFx;
    MenuButtonFx _navSettingsFx;

    // play screen
    TMP_InputField _hostPortInput;
    TMP_InputField _joinAddressInput;
    TMP_InputField _joinPortInput;
    TMP_InputField _steamHostIdInput;
    TMP_InputField _steamLobbyIdInput;
    TextMeshProUGUI _steamStatusLabel;
    TextMeshProUGUI _steamSelfIdLabel;
    GameObject _steamSelfRow;
    Button _steamHostButton;
    Button _steamJoinHostButton;
    Button _steamJoinLobbyButton;

    struct CharacterCard
    {
        public Button Button;
        public Image Background;
        public Image Outline;
        public Image Portrait;
        public TextMeshProUGUI Name;
        public TextMeshProUGUI Owner;
        public GameObject YouTag;
        public Image Ledge;
    }

    // lobby screen
    TextMeshProUGUI _lobbyTransportLabel;
    TextMeshProUGUI _lobbyStatusLabel;
    TextMeshProUGUI _lobbyGateLabel;
    RectTransform _playerListRoot;
    CharacterCard[] _characterCards;
    Button _readyButton;
    MenuButtonFx _readyFx;
    TextMeshProUGUI _readyLabel;
    Button _startButton;
    GameObject _startSection;
    MenuSegmented _levelSelect;
    string _selectedScene = MultiplayerSceneFlow.GameSceneName;
    TextMeshProUGUI _lobbySteamIdValue;
    TextMeshProUGUI _lobbySteamLobbyValue;
    GameObject _lobbySteamIdRow;
    GameObject _lobbySteamLobbyRow;
    GameObject _inviteButtonRoot;

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
        TuneSceneCamera();

        BuildUi();

        if (_session != null)
        {
            _session.StatusChanged += OnStatusChanged;
            _session.LobbyStateChanged += OnLobbyStateChanged;
        }

        bool inLobby = _session != null && _session.IsSessionActive;
        ShowScreen(inLobby ? MenuScreen.Lobby : MenuScreen.Play, true);
        RefreshLobby();
        RefreshSteamWidgets();
    }

    void OnDestroy()
    {
        if (_session != null)
        {
            _session.StatusChanged -= OnStatusChanged;
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

    static void TuneSceneCamera()
    {
        Camera cam = Camera.main;
        if (cam == null)
            cam = FindAnyObjectByType<Camera>();
        if (cam == null)
            return;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = MenuTheme.Ink;
    }

    // ================================================================ build

    void BuildUi()
    {
        Canvas canvas = MenuWidgets.CreateCanvas("MainMenuUI", 100, transform);
        Transform root = canvas.transform;

        MenuBackdrop.Build(root, false);
        BuildTitleBlock(root);
        BuildNav(root);
        BuildFooter(root);

        _playFader = BuildPlayScreen(root);
        _howToFader = BuildHowToScreen(root);
        _settingsFader = BuildSettingsScreen(root);
        _lobbyFader = BuildLobbyScreen(root);

        _toast = MenuToast.Create(root);
        _modal = MenuModal.Create(root);
    }

    void BuildTitleBlock(Transform root)
    {
        RectTransform block = MenuWidgets.CreateRect("TitleBlock", root);
        block.anchorMin = new Vector2(0f, 1f);
        block.anchorMax = new Vector2(0f, 1f);
        block.pivot = new Vector2(0f, 1f);
        block.anchoredPosition = new Vector2(150f, -140f);
        block.sizeDelta = new Vector2(620f, 420f);

        Image glow = MenuWidgets.CreateImage(block, "TitleGlow", MenuTheme.SoftGlow(), MenuTheme.WithAlpha(MenuTheme.Amber, 0.08f));
        RectTransform glowRt = glow.rectTransform;
        glowRt.anchorMin = new Vector2(0f, 1f);
        glowRt.anchorMax = new Vector2(0f, 1f);
        glowRt.pivot = new Vector2(0.35f, 0.6f);
        glowRt.anchoredPosition = new Vector2(170f, -160f);
        glowRt.sizeDelta = new Vector2(980f, 760f);
        var glowFlicker = glow.gameObject.AddComponent<UiFlicker>();
        glowFlicker.target = glow;
        glowFlicker.baseAlpha = 0.08f;
        glowFlicker.amplitude = 0.035f;
        glowFlicker.speed = 0.9f;

        // misprint: a mustard pass sits offset behind the bone pass, like a slipped screen print
        TextMeshProUGUI misprint = MenuWidgets.CreateText(block, "TitleMisprint", "DETOUR", 118f,
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.85f), MenuWidgets.FontKind.Display,
            TextAlignmentOptions.Left, 4f, FontStyles.Bold);
        SetTop(misprint.rectTransform, -30f, 130f);
        misprint.rectTransform.anchoredPosition += new Vector2(9f, -7f);

        TextMeshProUGUI title = MenuWidgets.CreateText(block, "Title", "DETOUR", 118f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 4f, FontStyles.Bold);
        SetTop(title.rectTransform, -30f, 130f);
        var titleFlicker = title.gameObject.AddComponent<UiFlicker>();
        titleFlicker.target = title;
        titleFlicker.baseAlpha = 1f;
        titleFlicker.amplitude = 0.05f;
        titleFlicker.speed = 0.8f;

        // torn mustard strike under the wordmark
        Image strike = MenuWidgets.CreateImage(block, "Strike", MenuTheme.TornBar(), MenuTheme.WithAlpha(MenuTheme.Amber, 0.92f));
        RectTransform strikeRt = strike.rectTransform;
        strikeRt.anchorMin = new Vector2(0f, 1f);
        strikeRt.anchorMax = new Vector2(0f, 1f);
        strikeRt.pivot = new Vector2(0f, 1f);
        strikeRt.anchoredPosition = new Vector2(8f, -168f);
        strikeRt.sizeDelta = new Vector2(300f, 9f);
        strikeRt.localRotation = Quaternion.Euler(0f, 0f, -0.6f);
    }

    static void SetTop(RectTransform rt, float y, float height)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(4f, y);
        rt.sizeDelta = new Vector2(0f, height);
    }

    void BuildNav(Transform root)
    {
        RectTransform nav = MenuWidgets.CreateRect("Nav", root);
        nav.anchorMin = new Vector2(0f, 0f);
        nav.anchorMax = new Vector2(0f, 1f);
        nav.pivot = new Vector2(0f, 1f);
        nav.anchoredPosition = new Vector2(150f, -400f);
        nav.sizeDelta = new Vector2(430f, 320f);
        MenuWidgets.AddVertical(nav.gameObject, new RectOffset(0, 0, 0, 0), 12f);

        MenuWidgets.CreateNavButton(nav, "PLAY", () =>
        {
            if (_session != null && _session.IsSessionActive)
                ShowScreen(MenuScreen.Lobby);
            else
                ShowScreen(MenuScreen.Play);
        }, out _navPlayFx);

        MenuWidgets.CreateNavButton(nav, "BRIEFING", () => ShowScreen(MenuScreen.HowTo), out _navHowToFx);
        MenuWidgets.CreateNavButton(nav, "SETTINGS", () => ShowScreen(MenuScreen.Settings), out _navSettingsFx);

        MenuWidgets.CreateNavButton(nav, "QUIT", () =>
        {
            _modal.Open("QUIT TO DESKTOP", string.Empty, "QUIT", true, QuitApplication);
        }, out _);
    }

    void BuildFooter(Transform root)
    {
        RectTransform footer = MenuWidgets.CreateRect("Footer", root);
        footer.anchorMin = new Vector2(0f, 0f);
        footer.anchorMax = new Vector2(0f, 0f);
        footer.pivot = new Vector2(0f, 0f);
        footer.anchoredPosition = new Vector2(150f, 42f);
        footer.sizeDelta = new Vector2(700f, 56f);

        _statusLabel = MenuWidgets.CreateText(footer, "Status", string.Empty, 14.5f, MenuTheme.Mist,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.BottomLeft);
        RectTransform statusRt = _statusLabel.rectTransform;
        statusRt.anchorMin = Vector2.zero;
        statusRt.anchorMax = Vector2.one;
        statusRt.offsetMin = new Vector2(0f, 24f);
        statusRt.offsetMax = Vector2.zero;
        _statusLabel.textWrappingMode = TextWrappingModes.NoWrap;
        _statusLabel.overflowMode = TextOverflowModes.Ellipsis;

        TextMeshProUGUI version = MenuWidgets.CreateText(footer, "Version",
            $"DETOUR — PRE-ALPHA {Application.version}", 12.5f, MenuTheme.Faint,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.BottomLeft, 3f);
        RectTransform versionRt = version.rectTransform;
        versionRt.anchorMin = Vector2.zero;
        versionRt.anchorMax = Vector2.one;
        versionRt.offsetMin = Vector2.zero;
        versionRt.offsetMax = new Vector2(0f, -32f);
    }

    RectTransform CreateScreenRoot(Transform root, string name, out MenuScreenFader fader)
    {
        RectTransform screen = MenuWidgets.CreateRect(name, root);
        screen.anchorMin = new Vector2(1f, 0.5f);
        screen.anchorMax = new Vector2(1f, 0.5f);
        screen.pivot = new Vector2(1f, 0.5f);
        screen.anchoredPosition = new Vector2(RightPanelMargin, 0f);
        screen.sizeDelta = new Vector2(CardWidth, 200f);
        screen.gameObject.AddComponent<CanvasGroup>();
        fader = screen.gameObject.AddComponent<MenuScreenFader>();
        fader.Hide(true);
        return screen;
    }

    // ---------------------------------------------------------------- play screen

    MenuScreenFader BuildPlayScreen(Transform root)
    {
        RectTransform screen = CreateScreenRoot(root, "Screen_Play", out MenuScreenFader fader);
        RectTransform card = MenuWidgets.CreateCard(screen, "Card", CardWidth);
        CenterCard(card);

        MenuWidgets.CreateText(card, "Title", "PLAY", 34f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 6f);

        MenuWidgets.CreateSection(card, "DIRECT IP");

        RectTransform hostRow = MenuWidgets.CreateRow(card, "HostRow", 48f, 12f);
        _hostPortInput = MenuWidgets.CreateInputField(hostRow, "HostPort", "PORT", 140f, TMP_InputField.ContentType.IntegerNumber);
        MenuWidgets.CreatePrimaryButton(hostRow, "HOST", OnHostLanClicked, 48f);

        RectTransform joinRow = MenuWidgets.CreateRow(card, "JoinRow", 48f, 12f);
        _joinAddressInput = MenuWidgets.CreateInputField(joinRow, "Address", "ADDRESS", 300f);
        MenuWidgets.SetLayout(_joinAddressInput, flexibleWidth: 1f, minHeight: 48f, preferredHeight: 48f);
        _joinPortInput = MenuWidgets.CreateInputField(joinRow, "JoinPort", "PORT", 110f, TMP_InputField.ContentType.IntegerNumber);
        Button joinButton = MenuWidgets.CreateGhostButton(joinRow, "JOIN", OnJoinLanClicked, false, 48f);
        MenuWidgets.SetLayout(joinButton.transform, minWidth: 130f, preferredWidth: 130f, minHeight: 48f, preferredHeight: 48f);

        MenuWidgets.CreateSection(card, "STEAM");

        _steamStatusLabel = MenuWidgets.CreateText(card, "SteamStatus", "CHECKING…", 13.5f, MenuTheme.Faint,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 2f);

        RectTransform steamHostRow = MenuWidgets.CreateRow(card, "SteamHostRow", 48f, 12f);
        _steamHostButton = MenuWidgets.CreatePrimaryButton(steamHostRow, "HOST STEAM LOBBY", OnHostSteamClicked, 48f);

        RectTransform steamJoinRow = MenuWidgets.CreateRow(card, "SteamJoinRow", 48f, 12f);
        _steamHostIdInput = MenuWidgets.CreateInputField(steamJoinRow, "SteamHostId", "FRIEND'S STEAMID64", 300f);
        MenuWidgets.SetLayout(_steamHostIdInput, flexibleWidth: 1f, minHeight: 48f, preferredHeight: 48f);
        _steamJoinHostButton = MenuWidgets.CreateGhostButton(steamJoinRow, "JOIN", OnJoinSteamHostClicked, false, 48f);
        MenuWidgets.SetLayout(_steamJoinHostButton.transform, minWidth: 130f, preferredWidth: 130f, minHeight: 48f, preferredHeight: 48f);

        RectTransform steamLobbyRow = MenuWidgets.CreateRow(card, "SteamLobbyRow", 48f, 12f);
        _steamLobbyIdInput = MenuWidgets.CreateInputField(steamLobbyRow, "SteamLobbyId", "LOBBY ID", 300f);
        MenuWidgets.SetLayout(_steamLobbyIdInput, flexibleWidth: 1f, minHeight: 48f, preferredHeight: 48f);
        _steamJoinLobbyButton = MenuWidgets.CreateGhostButton(steamLobbyRow, "JOIN", OnJoinSteamLobbyClicked, false, 48f);
        MenuWidgets.SetLayout(_steamJoinLobbyButton.transform, minWidth: 130f, preferredWidth: 130f, minHeight: 48f, preferredHeight: 48f);

        RectTransform selfRow = MenuWidgets.CreateRow(card, "SteamSelfRow", 36f, 12f);
        _steamSelfRow = selfRow.gameObject;
        _steamSelfIdLabel = MenuWidgets.CreateText(selfRow, "SelfId", string.Empty, 14.5f, MenuTheme.Mist,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.MidlineLeft);
        MenuWidgets.SetLayout(_steamSelfIdLabel, flexibleWidth: 1f);
        MenuWidgets.CreateMiniButton(selfRow, "COPY", () =>
        {
            if (_session != null && _session.LocalSteamId != 0UL)
            {
                GUIUtility.systemCopyBuffer = _session.LocalSteamId.ToString();
                _toast.Show("SteamID copied.");
            }
        });

        if (_session != null)
        {
            _hostPortInput.text = _session.DefaultPort.ToString();
            _joinPortInput.text = _session.DefaultPort.ToString();
            _joinAddressInput.text = _session.DefaultAddress;
        }

        return fader;
    }

    // ---------------------------------------------------------------- briefing screen

    MenuScreenFader BuildHowToScreen(Transform root)
    {
        RectTransform screen = CreateScreenRoot(root, "Screen_HowTo", out MenuScreenFader fader);
        RectTransform card = MenuWidgets.CreateCard(screen, "Card", CardWidth);
        CenterCard(card);

        MenuWidgets.CreateText(card, "Title", "BRIEFING", 34f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 6f);
        MenuWidgets.CreateSpacer(card, 6f);

        string[] steps = OnlinePlaytestChecklist.Steps;
        for (int i = 0; i < steps.Length; i++)
        {
            RectTransform row = MenuWidgets.CreateRow(card, "Step" + (i + 1), 24f, 10f, TextAnchor.UpperLeft);
            var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            rowLayout.childAlignment = TextAnchor.UpperLeft;
            var rowLe = row.GetComponent<LayoutElement>();
            rowLe.minHeight = 24f;
            rowLe.preferredHeight = -1f;
            rowLe.flexibleHeight = 0f;

            TextMeshProUGUI num = MenuWidgets.CreateText(row, "Num", (i + 1).ToString("00"), 14f,
                MenuTheme.WithAlpha(MenuTheme.Amber, 0.85f), MenuWidgets.FontKind.Display, TextAlignmentOptions.TopLeft, 2f);
            MenuWidgets.SetLayout(num, minWidth: 30f, preferredWidth: 30f);

            TextMeshProUGUI body = MenuWidgets.CreateText(row, "Text", steps[i], 14.5f, MenuTheme.Mist,
                MenuWidgets.FontKind.Body, TextAlignmentOptions.TopLeft);
            body.lineSpacing = 4f;
            MenuWidgets.SetLayout(body, flexibleWidth: 1f);
        }

        return fader;
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
        return fader;
    }

    // ---------------------------------------------------------------- lobby screen

    MenuScreenFader BuildLobbyScreen(Transform root)
    {
        RectTransform screen = CreateScreenRoot(root, "Screen_Lobby", out MenuScreenFader fader);
        RectTransform card = MenuWidgets.CreateCard(screen, "Card", CardWidth);
        CenterCard(card);

        RectTransform titleRow = MenuWidgets.CreateRow(card, "TitleRow", 44f, 16f);
        TextMeshProUGUI title = MenuWidgets.CreateText(titleRow, "Title", "LOBBY", 34f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineLeft, 6f);
        MenuWidgets.SetLayout(title, flexibleWidth: 1f);
        _lobbyTransportLabel = MenuWidgets.CreateText(titleRow, "Transport", "DIRECT IP", 13.5f,
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.9f), MenuWidgets.FontKind.Display, TextAlignmentOptions.MidlineRight, 5f);
        MenuWidgets.SetLayout(_lobbyTransportLabel, minWidth: 140f, preferredWidth: 140f);

        _lobbyStatusLabel = MenuWidgets.CreateText(card, "Status", string.Empty, 14.5f, MenuTheme.Faint);

        if (_session != null && _session.LobbyCharacterCount > 0)
        {
            BuildCharacterSelect(card);
        }
        else
        {
            MenuWidgets.CreateSection(card, "PLAYERS");
            _playerListRoot = MenuWidgets.CreateRect("PlayerList", card);
            MenuWidgets.AddVertical(_playerListRoot.gameObject, new RectOffset(0, 0, 0, 0), 8f);
        }

        MenuWidgets.CreateSpacer(card, 4f);

        RectTransform steamIdRow = MenuWidgets.CreateRow(card, "SteamIdRow", 34f, 12f);
        _lobbySteamIdRow = steamIdRow.gameObject;
        _lobbySteamIdValue = MenuWidgets.CreateText(steamIdRow, "Value", string.Empty, 14.5f, MenuTheme.Mist,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.MidlineLeft);
        MenuWidgets.SetLayout(_lobbySteamIdValue, flexibleWidth: 1f);
        MenuWidgets.CreateMiniButton(steamIdRow, "COPY", () =>
        {
            if (_session != null && _session.LocalSteamId != 0UL)
            {
                GUIUtility.systemCopyBuffer = _session.LocalSteamId.ToString();
                _toast.Show("SteamID copied.");
            }
        });

        RectTransform steamLobbyRow = MenuWidgets.CreateRow(card, "SteamLobbyRow", 34f, 12f);
        _lobbySteamLobbyRow = steamLobbyRow.gameObject;
        _lobbySteamLobbyValue = MenuWidgets.CreateText(steamLobbyRow, "Value", string.Empty, 14.5f, MenuTheme.Mist,
            MenuWidgets.FontKind.Body, TextAlignmentOptions.MidlineLeft);
        MenuWidgets.SetLayout(_lobbySteamLobbyValue, flexibleWidth: 1f);
        MenuWidgets.CreateMiniButton(steamLobbyRow, "COPY", () =>
        {
            if (_session != null && _session.CurrentSteamLobbyId != 0UL)
            {
                GUIUtility.systemCopyBuffer = _session.CurrentSteamLobbyId.ToString();
                _toast.Show("Lobby ID copied.");
            }
        });

        Button invite = MenuWidgets.CreateGhostButton(card, "INVITE STEAM FRIENDS", () =>
        {
            if (_session != null)
                _session.OpenSteamInviteDialog();
        }, false, 46f);
        _inviteButtonRoot = invite.gameObject;

        MenuWidgets.CreateSpacer(card, 8f);

        _readyButton = MenuWidgets.CreateGhostButton(card, "READY UP", OnReadyClicked, false, 54f);
        _readyFx = _readyButton.GetComponent<MenuButtonFx>();
        _readyLabel = _readyButton.GetComponentInChildren<TextMeshProUGUI>();
        if (_readyFx != null)
        {
            // ready reads as a locked-in moss plate, not the mustard used for selection
            _readyFx.fillSelected = MenuTheme.Moss;
            _readyFx.frameSelected = MenuTheme.WithAlpha(new Color(0.24f, 0.29f, 0.13f, 1f), 0.95f);
            _readyFx.labelSelected = MenuTheme.InkOnAccent;
        }

        var startSection = MenuWidgets.CreateRect("StartSection", card);
        _startSection = startSection.gameObject;
        MenuWidgets.AddVertical(startSection.gameObject, new RectOffset(0, 0, 0, 0), 8f);

        BuildLevelSelect(startSection);

        _startButton = MenuWidgets.CreatePrimaryButton(startSection, "START", OnStartClicked, 58f);
        MenuWidgets.CreateSpacer(startSection, 8f);
        _lobbyGateLabel = MenuWidgets.CreateText(startSection, "Gate", string.Empty,
            14f, MenuTheme.Faint, MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 4f);

        MenuWidgets.CreateSpacer(card, 6f);

        MenuWidgets.CreateGhostButton(card, "LEAVE LOBBY", () =>
        {
            _modal.Open("LEAVE LOBBY", string.Empty, "LEAVE", true, () =>
            {
                if (_flow != null)
                    _flow.ReturnToMainMenu();
                else if (_session != null)
                    _session.ShutdownSession();
            });
        }, true, 48f);

        return fader;
    }

    /// <summary>
    /// Host-only maze-section picker shown above START. The chosen scene is handed to the session
    /// on start; every entry maps 1:1 to <see cref="MultiplayerSceneFlow.MazeSectionSceneNames"/>.
    /// </summary>
    void BuildLevelSelect(Transform startSection)
    {
        string[] scenes = MultiplayerSceneFlow.MazeSectionSceneNames;
        if (scenes == null || scenes.Length <= 1)
            return;

        MenuWidgets.CreateSection(startSection, "START LEVEL");

        var labels = new string[scenes.Length];
        for (int i = 0; i < scenes.Length; i++)
            labels[i] = (i + 1).ToString();

        _levelSelect = MenuWidgets.CreateSegmented(startSection, labels, 46f);

        int startIndex = System.Array.IndexOf(scenes, _selectedScene);
        if (startIndex < 0)
            startIndex = 0;
        _selectedScene = scenes[startIndex];
        _levelSelect.Set(startIndex, false);
        _levelSelect.Changed += index =>
        {
            if (index >= 0 && index < scenes.Length)
                _selectedScene = scenes[index];
        };

        MenuWidgets.CreateSpacer(startSection, 4f);
    }

    /// <summary>
    /// One portrait plate per lobby character. Each plate doubles as the player roster: it shows
    /// who holds the character and their ready state. Exactly one player may hold each character.
    /// </summary>
    void BuildCharacterSelect(RectTransform card)
    {
        MenuWidgets.CreateSection(card, "SURVIVORS");

        int count = _session.LobbyCharacterCount;
        RectTransform row = MenuWidgets.CreateRow(card, "Characters", 216f, 12f, TextAnchor.UpperLeft);
        _characterCards = new CharacterCard[count];

        for (int i = 0; i < count; i++)
        {
            int index = i;
            MultiplayerProjectSettings.LobbyCharacter character = _session.GetLobbyCharacter(i);

            RectTransform root = MenuWidgets.CreateRect("Character_" + i, row);
            MenuWidgets.SetLayout(root, flexibleWidth: 1f, minHeight: 212f, preferredHeight: 212f);

            RectTransform plate = MenuWidgets.CreateStretched("Plate", root);
            float tilt = ((Mathf.Abs(("survivor" + i).GetHashCode()) % 100) / 100f - 0.5f) * 0.8f;
            plate.localRotation = Quaternion.Euler(0f, 0f, tilt);

            Image bg = MenuWidgets.CreateImage(plate, "Bg", MenuTheme.RoundedRect(2), MenuTheme.WithAlpha(MenuTheme.Tile, 0.92f), true);
            bg.rectTransform.SetStretch();

            MenuWidgets.CreateGrunge(plate, MenuTheme.WithAlpha(Color.white, 0.05f));

            Image portrait = MenuWidgets.CreateImage(plate, "Portrait", character != null ? character.Portrait : null, Color.white);
            RectTransform portraitRt = portrait.rectTransform;
            portraitRt.anchorMin = new Vector2(0f, 0f);
            portraitRt.anchorMax = new Vector2(1f, 1f);
            portraitRt.offsetMin = new Vector2(5f, 52f);
            portraitRt.offsetMax = new Vector2(-5f, -8f);
            portrait.type = Image.Type.Simple;
            portrait.preserveAspect = true;

            Image shade = MenuWidgets.CreateImage(plate, "Shade", MenuTheme.VerticalGradient(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.9f));
            RectTransform shadeRt = shade.rectTransform;
            shadeRt.anchorMin = new Vector2(0f, 0f);
            shadeRt.anchorMax = new Vector2(1f, 0f);
            shadeRt.pivot = new Vector2(0.5f, 0f);
            shadeRt.anchoredPosition = new Vector2(0f, 48f);
            shadeRt.sizeDelta = new Vector2(-8f, 48f);

            TextMeshProUGUI name = MenuWidgets.CreateText(plate, "Name",
                character != null ? character.DisplayName : ("SURVIVOR " + (i + 1)), 16.5f, MenuTheme.Bone,
                MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 4f);
            RectTransform nameRt = name.rectTransform;
            nameRt.anchorMin = new Vector2(0f, 0f);
            nameRt.anchorMax = new Vector2(1f, 0f);
            nameRt.pivot = new Vector2(0.5f, 0f);
            nameRt.anchoredPosition = new Vector2(0f, 27f);
            nameRt.sizeDelta = new Vector2(0f, 24f);

            TextMeshProUGUI owner = MenuWidgets.CreateText(plate, "Owner", "OPEN", 11.5f, MenuTheme.Faint,
                MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 3f);
            owner.richText = true;
            RectTransform ownerRt = owner.rectTransform;
            ownerRt.anchorMin = new Vector2(0f, 0f);
            ownerRt.anchorMax = new Vector2(1f, 0f);
            ownerRt.pivot = new Vector2(0.5f, 0f);
            ownerRt.anchoredPosition = new Vector2(0f, 8f);
            ownerRt.sizeDelta = new Vector2(0f, 18f);

            Image outline = MenuWidgets.CreateImage(plate, "Outline", MenuTheme.RoundedOutline(2, 2f), MenuTheme.WithAlpha(MenuTheme.Bone, 0.30f));
            outline.rectTransform.SetStretch();

            // torn ledge under the plate — lights up mustard under your pick
            Image ledge = MenuWidgets.CreateImage(plate, "Ledge", MenuTheme.TornBar(), Color.clear);
            RectTransform ledgeRt = ledge.rectTransform;
            ledgeRt.anchorMin = new Vector2(0.05f, 0f);
            ledgeRt.anchorMax = new Vector2(0.72f, 0f);
            ledgeRt.pivot = new Vector2(0f, 1f);
            ledgeRt.anchoredPosition = new Vector2(0f, -2.5f);
            ledgeRt.sizeDelta = new Vector2(0f, 5f);

            // "YOU" tab pinned to the top-left corner, like a slapped-on sticker
            RectTransform tag = MenuWidgets.CreateRect("YouTag", plate);
            tag.anchorMin = new Vector2(0f, 1f);
            tag.anchorMax = new Vector2(0f, 1f);
            tag.pivot = new Vector2(0f, 1f);
            tag.anchoredPosition = new Vector2(-4f, 7f);
            tag.sizeDelta = new Vector2(52f, 24f);
            tag.localRotation = Quaternion.Euler(0f, 0f, -3.5f);
            Image tagBg = MenuWidgets.CreateImage(tag, "Bg", MenuTheme.RoughChip(), MenuTheme.Amber);
            tagBg.rectTransform.SetStretch();
            TextMeshProUGUI tagText = MenuWidgets.CreateText(tag, "Text", "YOU", 12.5f, MenuTheme.InkOnAccent,
                MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 3f);
            tagText.rectTransform.SetStretch();
            tag.gameObject.SetActive(false);

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
                Owner = owner,
                YouTag = tag.gameObject,
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
        string mossHex = ColorUtility.ToHtmlStringRGB(MenuTheme.Moss);
        string faintHex = ColorUtility.ToHtmlStringRGB(MenuTheme.Faint);

        for (int i = 0; i < _characterCards.Length; i++)
        {
            CharacterCard cardUi = _characterCards[i];

            bool taken = false;
            bool mine = false;
            bool ownerReady = false;
            bool ownerIsHost = false;
            ulong ownerId = 0;
            for (int p = 0; p < players.Count; p++)
            {
                if (players[p].CharacterIndex != i)
                    continue;
                taken = true;
                ownerId = players[p].ClientId;
                mine = ownerId == localClientId;
                ownerReady = players[p].IsReady;
                ownerIsHost = players[p].IsHost;
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

            if (cardUi.YouTag != null && cardUi.YouTag.activeSelf != mine)
                cardUi.YouTag.SetActive(mine);

            if (cardUi.Ledge != null)
                cardUi.Ledge.color = mine ? MenuTheme.WithAlpha(MenuTheme.Amber, 0.9f) : Color.clear;

            if (cardUi.Owner != null)
            {
                if (taken)
                {
                    string dotHex = ownerReady ? mossHex : faintHex;
                    string who = mine ? "HELD" : "P" + ownerId;
                    if (ownerIsHost)
                        who += " · HOST";
                    cardUi.Owner.text = $"<color=#{dotHex}>■</color>  <color=#{faintHex}>{who}</color>";
                }
                else
                {
                    cardUi.Owner.text = "OPEN";
                    cardUi.Owner.color = MenuTheme.WithAlpha(MenuTheme.Faint, 0.8f);
                }
            }

            if (cardUi.Button != null)
                cardUi.Button.interactable = selectionOpen && !taken;
        }
    }

    static void CenterCard(RectTransform cardContent)
    {
        // CreateCard returns the content rect; its parent is the card root we place.
        var cardRoot = (RectTransform)cardContent.parent;
        cardRoot.anchorMin = new Vector2(1f, 0.5f);
        cardRoot.anchorMax = new Vector2(1f, 0.5f);
        cardRoot.pivot = new Vector2(1f, 0.5f);
        cardRoot.anchoredPosition = Vector2.zero;
        cardRoot.sizeDelta = new Vector2(CardWidth, cardRoot.sizeDelta.y);
    }

    // ================================================================ actions

    void OnHostLanClicked()
    {
        if (_session == null)
        {
            _toast.Show("Multiplayer is unavailable in this scene.");
            return;
        }
        ushort port = ParsePort(_hostPortInput != null ? _hostPortInput.text : null);
        if (_flow != null)
            _flow.RequestHostLobby(port);
        else
            _session.StartHost(port);
    }

    void OnJoinLanClicked()
    {
        if (_session == null)
        {
            _toast.Show("Multiplayer is unavailable in this scene.");
            return;
        }
        string address = _joinAddressInput != null ? _joinAddressInput.text : null;
        ushort port = ParsePort(_joinPortInput != null ? _joinPortInput.text : null);
        if (_flow != null)
            _flow.RequestJoinLobby(address, port);
        else
            _session.StartClient(address, port);
    }

    void OnHostSteamClicked()
    {
        if (_session == null)
            return;
        if (_flow != null)
            _flow.RequestSteamHostLobby();
        else
            _session.StartSteamHost();
    }

    void OnJoinSteamHostClicked()
    {
        if (_session == null)
            return;
        if (!TryParseUlong(_steamHostIdInput != null ? _steamHostIdInput.text : null, out ulong hostId))
        {
            _toast.Show("Enter a valid SteamID64 first.");
            return;
        }
        if (_flow != null)
            _flow.RequestSteamJoinLobby(hostId);
        else
            _session.StartSteamClient(hostId);
    }

    void OnJoinSteamLobbyClicked()
    {
        if (_session == null)
            return;
        if (!TryParseUlong(_steamLobbyIdInput != null ? _steamLobbyIdInput.text : null, out ulong lobbyId))
        {
            _toast.Show("Enter a valid lobby ID first.");
            return;
        }
        if (_flow != null)
            _flow.RequestSteamLobbyJoin(lobbyId);
        else
            _session.JoinSteamLobby(lobbyId);
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

    ushort ParsePort(string text)
    {
        if (ushort.TryParse((text ?? string.Empty).Trim(), out ushort port) && port != 0)
            return port;
        return _session != null ? _session.DefaultPort : (ushort)7777;
    }

    static bool TryParseUlong(string input, out ulong value)
    {
        return ulong.TryParse((input ?? string.Empty).Trim(), out value) && value != 0UL;
    }

    // ================================================================ state

    void ShowScreen(MenuScreen screen, bool instant = false)
    {
        _current = screen;
        ToggleFader(_playFader, screen == MenuScreen.Play, instant);
        ToggleFader(_howToFader, screen == MenuScreen.HowTo, instant);
        ToggleFader(_settingsFader, screen == MenuScreen.Settings, instant);
        ToggleFader(_lobbyFader, screen == MenuScreen.Lobby, instant);

        SetNavActive(_navPlayFx, screen == MenuScreen.Play || screen == MenuScreen.Lobby);
        SetNavActive(_navHowToFx, screen == MenuScreen.HowTo);
        SetNavActive(_navSettingsFx, screen == MenuScreen.Settings);
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

    static void SetNavActive(MenuButtonFx fx, bool active)
    {
        if (fx != null)
            fx.SetSelected(active);
    }

    void OnStatusChanged(string status)
    {
        if (_statusLabel != null)
            _statusLabel.text = status;
        if (_toast != null)
            _toast.Show(status);
    }

    void OnLobbyStateChanged()
    {
        if (_session != null && _session.IsSessionActive && _session.LobbyPlayers.Count > 0
            && _current != MenuScreen.Lobby)
        {
            ShowScreen(MenuScreen.Lobby);
        }
        RefreshLobby();
    }

    void Update()
    {
        // session ended while looking at the lobby -> fall back to the play screen
        if (_current == MenuScreen.Lobby && (_session == null || !_session.IsSessionActive))
            ShowScreen(MenuScreen.Play);

        RefreshSteamWidgets();
        RefreshLobbyDynamic();
    }

    void RefreshSteamWidgets()
    {
        if (_session == null || _steamStatusLabel == null)
            return;

        _steamStatusLabel.text = _session.SteamStatus.ToUpperInvariant();

        bool steamReady = _session.IsSteamReady;
        if (_steamHostButton != null)
            _steamHostButton.interactable = steamReady;
        if (_steamJoinHostButton != null)
            _steamJoinHostButton.interactable = steamReady;
        if (_steamJoinLobbyButton != null)
            _steamJoinLobbyButton.interactable = steamReady;

        bool hasId = _session.LocalSteamId != 0UL;
        if (_steamSelfRow != null && _steamSelfRow.activeSelf != hasId)
            _steamSelfRow.SetActive(hasId);
        if (hasId && _steamSelfIdLabel != null)
            _steamSelfIdLabel.text = "YOUR STEAMID64:  " + _session.LocalSteamId;
    }

    void RefreshLobbyDynamic()
    {
        if (_session == null || _current != MenuScreen.Lobby)
            return;

        if (_lobbyStatusLabel != null)
            _lobbyStatusLabel.text = _session.CurrentStatus;

        bool isHost = _session.IsLobbyHost;
        if (_startSection != null && _startSection.activeSelf != isHost)
            _startSection.SetActive(isHost);
        if (isHost && _startButton != null)
        {
            _startButton.interactable = _session.CanHostStartGame;
            if (_lobbyGateLabel != null)
            {
                IReadOnlyList<LobbyPlayerState> players = _session.LobbyPlayers;
                int readyCount = 0;
                for (int i = 0; i < players.Count; i++)
                {
                    if (players[i].IsReady)
                        readyCount++;
                }
                bool allReady = _session.AreAllLobbyPlayersReady && players.Count > 0;
                _lobbyGateLabel.text = allReady ? "ALL READY" : $"{readyCount}/{players.Count} READY";
                _lobbyGateLabel.color = allReady ? MenuTheme.Moss : MenuTheme.Faint;
            }
        }

        bool ready = _session.IsLocalReady;
        if (_readyLabel != null)
            _readyLabel.text = ready ? "READY" : "READY UP";
        if (_readyFx != null)
            _readyFx.SetSelected(ready);

        bool steam = _session.CurrentTransportMode == MultiplayerTransportMode.SteamP2P;
        bool hasSelfId = steam && _session.LocalSteamId != 0UL;
        bool hasLobbyId = steam && _session.CurrentSteamLobbyId != 0UL;
        if (_lobbySteamIdRow != null && _lobbySteamIdRow.activeSelf != hasSelfId)
            _lobbySteamIdRow.SetActive(hasSelfId);
        if (_lobbySteamLobbyRow != null && _lobbySteamLobbyRow.activeSelf != hasLobbyId)
            _lobbySteamLobbyRow.SetActive(hasLobbyId);
        if (_inviteButtonRoot != null && _inviteButtonRoot.activeSelf != hasLobbyId)
            _inviteButtonRoot.SetActive(hasLobbyId);
        if (hasSelfId && _lobbySteamIdValue != null)
            _lobbySteamIdValue.text = "YOUR STEAMID64:  " + _session.LocalSteamId;
        if (hasLobbyId && _lobbySteamLobbyValue != null)
            _lobbySteamLobbyValue.text = "LOBBY ID:  " + _session.CurrentSteamLobbyId;

        if (_lobbyTransportLabel != null)
            _lobbyTransportLabel.text = _session.CurrentTransportLabel.ToUpperInvariant();
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
        }
        hash = hash * 31 + (_session.IsGameStartRequested ? 1 : 0);
        hash = hash * 31 + (_session.CanSelectCharactersNow ? 2 : 0);
        if (hash == _renderedLobbyHash)
            return;
        _renderedLobbyHash = hash;

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

            Image bg = MenuWidgets.CreateImage(row, "Bg", MenuTheme.RoundedRect(2), MenuTheme.WithAlpha(MenuTheme.Tile, 0.88f));
            bg.rectTransform.SetStretch();
            Image frame = MenuWidgets.CreateImage(row, "Frame", MenuTheme.RoundedOutline(2, 1.6f), MenuTheme.WithAlpha(MenuTheme.Bone, 0.18f));
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
