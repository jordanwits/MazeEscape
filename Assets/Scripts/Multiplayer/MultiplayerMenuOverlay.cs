using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
[RequireComponent(typeof(MultiplayerSessionController))]
public class MultiplayerMenuOverlay : MonoBehaviour
{
    const float WindowWidth = 360f;
    const float WindowHeight = 560f;

    public static bool BlocksGameplayInput { get; private set; }

    static int s_NextImGuiWindowId = 0x6D756C74; // "mult" — avoid IMGUI id collisions with other windows

    MultiplayerSessionController _session;
    MultiplayerSceneFlow _flow;
    Rect _windowRect = new(20f, 20f, WindowWidth, WindowHeight);
    Vector2 _scrollPosition;
    string _addressInput;
    string _portInput;
    string _steamHostIdInput;
    string _steamLobbyIdInput;
    bool _isVisible = false;
    bool _showPlaytestChecklist;
    int _imGuiWindowId;

    void Awake()
    {
        _imGuiWindowId = s_NextImGuiWindowId++;
        _session = GetComponent<MultiplayerSessionController>();
        _addressInput = _session != null ? _session.DefaultAddress : "127.0.0.1";
        _portInput = _session != null ? _session.DefaultPort.ToString() : "7777";
        _steamHostIdInput = string.Empty;
        _steamLobbyIdInput = string.Empty;
        ApplyOverlayInputState();
    }

    void OnEnable()
    {
        if (_flow == null)
            TryGetComponent(out _flow);

        if (_session != null)
            _session.LobbyStateChanged += HandleLobbyStateChanged;

        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        if (_session != null)
            _session.LobbyStateChanged -= HandleLobbyStateChanged;

        SceneManager.sceneLoaded -= OnSceneLoaded;
        BlocksGameplayInput = false;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == MultiplayerSceneFlow.GameSceneName)
            SetVisible(false);
    }

    void HandleLobbyStateChanged()
    {
        Scene active = SceneManager.GetActiveScene();
        if (active.IsValid()
            && active.name == MultiplayerSceneFlow.MenuSceneName
            && _session.IsSessionActive
            && _session.LobbyPlayers.Count > 0)
        {
            SetVisible(true);
        }
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

        _scrollPosition = GUILayout.BeginScrollView(_scrollPosition, GUILayout.Width(WindowWidth - 12f), GUILayout.Height(WindowHeight - 34f));

        if (inMenu)
            DrawMenuSceneWindow();
        else
            DrawInGameWindow();

        DrawPlaytestChecklist();

        GUILayout.Space(6f);
        GUILayout.Label("F8: hide/show this panel.");
        GUILayout.EndScrollView();
        GUI.DragWindow(new Rect(0f, 0f, WindowWidth, 24f));
    }

    void DrawMenuSceneWindow()
    {
        if (_session.IsSessionActive)
        {
            DrawLobbyWindow();
            return;
        }

        GUILayout.Label("Host or join creates a menu lobby. Ready up, then the host starts the shared level load.");
        GUILayout.Space(8f);

        GUILayout.Label("Direct IP / LAN");
        GUILayout.Label("Address");
        _addressInput = GUILayout.TextField(_addressInput ?? string.Empty);

        GUILayout.Space(4f);
        GUILayout.Label("Port");
        _portInput = GUILayout.TextField(_portInput ?? string.Empty);

        GUILayout.Space(10f);
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Host Lobby"))
        {
            ushort port = ParsePortOrDefault();
            if (_flow != null)
                _flow.RequestHostLobby(port);
            else
                _session.StartHost(port);
        }

        if (GUILayout.Button("Join Lobby"))
        {
            if (_flow != null)
                _flow.RequestJoinLobby(_addressInput, ParsePortOrDefault());
            else
                _session.StartClient(_addressInput, ParsePortOrDefault());
        }

        GUILayout.EndHorizontal();

        DrawSteamWindowControls();

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

    void DrawLobbyWindow()
    {
        GUILayout.Label("Lobby");
        GUILayout.Label($"Transport: {_session.CurrentTransportLabel}");
        GUILayout.Space(6f);
        GUILayout.Label($"Status: {_session.CurrentStatus}");

        if (_session.CurrentTransportMode == MultiplayerTransportMode.SteamP2P)
            DrawSteamIdentityControls();

        GUILayout.Space(10f);
        DrawLobbyPlayers();

        GUILayout.Space(8f);
        bool localReady = GUILayout.Toggle(_session.IsLocalReady, "Ready");
        if (localReady != _session.IsLocalReady)
            _session.SetLocalPlayerReady(localReady);

        GUILayout.Space(8f);
        if (_session.IsLobbyHost)
        {
            GUI.enabled = _session.CanHostStartGame;
            if (GUILayout.Button("Start Game"))
            {
                if (_flow != null)
                    _flow.RequestStartGameFromLobby();
                else
                    _session.StartGameFromLobby();
            }
            GUI.enabled = true;

            if (!_session.AreAllLobbyPlayersReady)
                GUILayout.Label("Waiting for every player to ready up.");
        }
        else
        {
            GUILayout.Label("Waiting for host to start after everyone is ready.");
        }

        GUILayout.Space(10f);
        if (GUILayout.Button("Leave Lobby"))
        {
            if (_flow != null)
                _flow.ReturnToMainMenu();
            else
                _session.ShutdownSession();
        }
    }

    void DrawLobbyPlayers()
    {
        IReadOnlyList<LobbyPlayerState> players = _session.LobbyPlayers;
        GUILayout.Label($"Players: {players.Count}");

        if (players.Count == 0)
        {
            GUILayout.Label("Waiting for lobby state...");
            return;
        }

        for (int i = 0; i < players.Count; i++)
        {
            LobbyPlayerState player = players[i];
            string role = player.IsHost ? "Host" : "Client";
            string ready = player.IsReady ? "Ready" : "Not Ready";
            GUILayout.Label($"{role} {player.ClientId}: {ready}");
        }
    }

    void DrawInGameWindow()
    {
        GUILayout.Label($"Scene: {MultiplayerSceneFlow.GameSceneName}");
        GUILayout.Label($"Transport: {_session.CurrentTransportLabel}");
        GUILayout.Space(6f);
        GUILayout.Label($"Status: {_session.CurrentStatus}");

        if (_session.CurrentTransportMode == MultiplayerTransportMode.SteamP2P)
            DrawSteamIdentityControls();

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

    void DrawPlaytestChecklist()
    {
        GUILayout.Space(8f);
        _showPlaytestChecklist = GUILayout.Toggle(_showPlaytestChecklist, "Show online playtest checklist");
        if (!_showPlaytestChecklist)
            return;

        string[] steps = OnlinePlaytestChecklist.Steps;
        for (int i = 0; i < steps.Length; i++)
            GUILayout.Label($"{i + 1}. {steps[i]}");
    }

    void DrawSteamWindowControls()
    {
        GUILayout.Space(12f);
        GUILayout.Label("Steam P2P");
        DrawSteamIdentityControls();

        bool steamReady = _session.IsSteamReady;
        GUI.enabled = steamReady;
        if (GUILayout.Button("Host Steam Lobby"))
        {
            if (_flow != null)
                _flow.RequestSteamHostLobby();
            else
                _session.StartSteamHost();
        }

        GUILayout.Label("Host SteamID64");
        _steamHostIdInput = GUILayout.TextField(_steamHostIdInput ?? string.Empty);
        if (GUILayout.Button("Join Steam Host ID") && TryParseUlong(_steamHostIdInput, out ulong hostSteamId))
        {
            if (_flow != null)
                _flow.RequestSteamJoinLobby(hostSteamId);
            else
                _session.StartSteamClient(hostSteamId);
        }

        GUILayout.Label("Lobby ID");
        _steamLobbyIdInput = GUILayout.TextField(_steamLobbyIdInput ?? string.Empty);
        if (GUILayout.Button("Join Steam Lobby ID") && TryParseUlong(_steamLobbyIdInput, out ulong lobbyId))
        {
            if (_flow != null)
                _flow.RequestSteamLobbyJoin(lobbyId);
            else
                _session.JoinSteamLobby(lobbyId);
        }
        GUI.enabled = true;
    }

    void DrawSteamIdentityControls()
    {
        GUILayout.Label($"Steam: {_session.SteamStatus}");

        if (_session.LocalSteamId != 0UL)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Your ID: {_session.LocalSteamId}");
            if (GUILayout.Button("Copy", GUILayout.Width(64f)))
                GUIUtility.systemCopyBuffer = _session.LocalSteamId.ToString();
            GUILayout.EndHorizontal();
        }

        if (_session.CurrentSteamLobbyId != 0UL)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Lobby: {_session.CurrentSteamLobbyId}");
            if (GUILayout.Button("Copy", GUILayout.Width(64f)))
                GUIUtility.systemCopyBuffer = _session.CurrentSteamLobbyId.ToString();
            GUILayout.EndHorizontal();

            if (GUILayout.Button("Invite Steam Friends"))
                _session.OpenSteamInviteDialog();
        }
    }

    ushort ParsePortOrDefault()
    {
        return ushort.TryParse(_portInput, out ushort parsedPort)
            ? parsedPort
            : _session.DefaultPort;
    }

    static bool TryParseUlong(string input, out ulong value)
    {
        return ulong.TryParse((input ?? string.Empty).Trim(), out value) && value != 0UL;
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
