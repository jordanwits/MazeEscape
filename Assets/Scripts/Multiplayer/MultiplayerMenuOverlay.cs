using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
[RequireComponent(typeof(MultiplayerSessionController))]
public class MultiplayerMenuOverlay : MonoBehaviour
{
    const float WindowWidth = 360f;
    const float WindowHeight = 320f;

    public static bool BlocksGameplayInput { get; private set; }

    static int s_NextImGuiWindowId = 0x6D756C74; // "mult" — avoid IMGUI id collisions with other windows

    MultiplayerSessionController _session;
    MultiplayerSceneFlow _flow;
    Rect _windowRect = new(20f, 20f, WindowWidth, WindowHeight);
    string _addressInput;
    string _portInput;
    bool _isVisible = false;
    int _imGuiWindowId;

    void Awake()
    {
        _imGuiWindowId = s_NextImGuiWindowId++;
        _session = GetComponent<MultiplayerSessionController>();
        _addressInput = _session != null ? _session.DefaultAddress : "127.0.0.1";
        _portInput = _session != null ? _session.DefaultPort.ToString() : "7777";
        ApplyOverlayInputState();
    }

    void OnEnable()
    {
        if (_flow == null)
            TryGetComponent(out _flow);

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        BlocksGameplayInput = false;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == MultiplayerSceneFlow.GameSceneName)
            SetVisible(false);
    }

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame)
            SetVisible(!_isVisible);
    }

    void OnGUI()
    {
        if (!_isVisible || _session == null)
            return;

        _windowRect = GUI.Window(_imGuiWindowId, _windowRect, DrawWindow, "Multiplayer");
    }

    void DrawWindow(int windowId)
    {
        Scene active = SceneManager.GetActiveScene();
        bool inMenu = active.IsValid() && active.name == MultiplayerSceneFlow.MenuSceneName;

        if (inMenu)
            DrawMenuSceneWindow();
        else
            DrawInGameWindow();

        GUILayout.Space(6f);
        GUILayout.Label("F8: hide/show this panel.");
        GUI.DragWindow(new Rect(0f, 0f, WindowWidth, 24f));
    }

    void DrawMenuSceneWindow()
    {
        GUILayout.Label("Host or join loads the game scene, then starts the network session.");
        GUILayout.Space(8f);

        GUILayout.Label("Address");
        _addressInput = GUILayout.TextField(_addressInput ?? string.Empty);

        GUILayout.Space(4f);
        GUILayout.Label("Port");
        _portInput = GUILayout.TextField(_portInput ?? string.Empty);

        GUILayout.Space(10f);
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Host"))
        {
            ushort port = ParsePortOrDefault();
            if (_flow != null)
                _flow.RequestHostThenGame(port);
            else
                _session.StartHost(port);
        }

        if (GUILayout.Button("Join"))
        {
            if (_flow != null)
                _flow.RequestJoinThenGame(_addressInput, ParsePortOrDefault());
            else
                _session.StartClient(_addressInput, ParsePortOrDefault());
        }

        GUILayout.EndHorizontal();

        if (GUILayout.Button("Quit"))
        {
            if (_flow != null)
                _flow.QuitApplication();
#if UNITY_EDITOR
            else
                UnityEditor.EditorApplication.isPlaying = false;
#else
            else
                Application.Quit();
#endif
        }

        GUILayout.Space(8f);
        GUILayout.Label($"Status: {_session.CurrentStatus}");
    }

    void DrawInGameWindow()
    {
        GUILayout.Label($"Scene: {MultiplayerSceneFlow.GameSceneName}");
        GUILayout.Space(6f);
        GUILayout.Label($"Status: {_session.CurrentStatus}");

        GUILayout.Space(10f);

        if (_session.IsSessionActive)
        {
            if (GUILayout.Button("Leave session (return to menu)") && _flow != null)
                _flow.ReturnToMainMenu();
        }
        else
        {
            GUILayout.Label("No active session.");
            if (GUILayout.Button("Back to menu") && _flow != null)
                _flow.ReturnToMainMenu();
        }

        GUILayout.Space(8f);
        GUILayout.Label("From the menu scene you can Host or Join again.");
    }

    ushort ParsePortOrDefault()
    {
        return ushort.TryParse(_portInput, out ushort parsedPort)
            ? parsedPort
            : _session.DefaultPort;
    }

    void SetVisible(bool visible)
    {
        _isVisible = visible;
        ApplyOverlayInputState();
    }

    void ApplyOverlayInputState()
    {
        BlocksGameplayInput = _isVisible;

        if (!_isVisible)
            return;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
