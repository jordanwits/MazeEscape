using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Esc-toggled in-game pause menu (replaces the old F8 IMGUI overlay): Resume, Settings
/// (audio / environment light / voice), Leave Session, Quit. Lives on the multiplayer
/// bootstrap object so it persists across scene loads; only active in maze gameplay scenes.
/// Gameplay scripts poll <see cref="BlocksGameplayInput"/> to mute player input while open.
/// </summary>
[DisallowMultipleComponent]
public sealed class PauseMenuController : MonoBehaviour
{
    public static bool BlocksGameplayInput { get; private set; }

    MultiplayerSessionController _session;
    MultiplayerSceneFlow _flow;

    GameObject _root;
    MenuModal _modal;
    MenuScreenFader _settingsFader;
    MenuSettingsPanel _settingsPanel;
    TextMeshProUGUI _statusLabel;
    TextMeshProUGUI _sceneLabel;
    TMP_Text _leaveLabel;
    GameObject _ownEventSystem;
    bool _isOpen;
    bool _settingsOpen;

    void Awake()
    {
        TryGetComponent(out _session);
        TryGetComponent(out _flow);
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (BlocksGameplayInput && _isOpen)
            BlocksGameplayInput = false;
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

        bool togglePressed =
            (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            || (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);

        if (togglePressed)
        {
            if (!_isOpen)
                Open();
            else if (_modal != null && _modal.gameObject.activeSelf)
                _modal.Close();
            else if (_settingsOpen)
                ShowSettings(false);
            else
                Close(false);
        }

        if (_isOpen)
            RefreshDynamic();
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
        _isOpen = true;
        BlocksGameplayInput = true;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        MenuTheme.ApplyCursor();

        EnsureEventSystem();
        RefreshStatics();
        RefreshDynamic();
    }

    public void Close(bool instant)
    {
        _isOpen = false;
        BlocksGameplayInput = false;
        _settingsOpen = false;

        if (_root != null)
            _root.SetActive(false);
        if (_modal != null)
            _modal.Close();

        if (_ownEventSystem != null)
        {
            Destroy(_ownEventSystem);
            _ownEventSystem = null;
        }
        // PlayerController re-locks the cursor on its own once BlocksGameplayInput is false.
    }

    void EnsureEventSystem()
    {
        if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null)
            return;
        _ownEventSystem = new GameObject("EventSystem (Pause)");
        _ownEventSystem.transform.SetParent(transform, false);
        _ownEventSystem.AddComponent<EventSystem>();
        _ownEventSystem.AddComponent<InputSystemUIInputModule>();
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

        MenuWidgets.CreateNavButton(nav, "RESUME", () => Close(false), out _);
        MenuWidgets.CreateNavButton(nav, "SETTINGS", () => ShowSettings(!_settingsOpen), out _);

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

        _settingsFader.Hide(true);
        _modal = MenuModal.Create(root);

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
        _settingsOpen = show;
        if (_settingsFader == null)
            return;
        if (show)
        {
            if (_settingsPanel != null)
                _settingsPanel.SyncFromManagers();
            _settingsFader.Show(instant);
        }
        else
        {
            _settingsFader.Hide(instant);
        }
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
    }

    void RefreshDynamic()
    {
        if (_statusLabel == null || _session == null)
            return;
        _statusLabel.text = _session.CurrentTransportLabel.ToUpperInvariant() + "   —   " + _session.CurrentStatus;
    }
}
