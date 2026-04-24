using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Keeps host/client connections in the menu lobby, then lets the host start a synchronized NGO scene load.
/// Returning to the menu shuts down the session and loads the menu scene (bootstrap stays alive).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MultiplayerSessionController))]
public class MultiplayerSceneFlow : MonoBehaviour
{
    public const string MenuSceneName = "Menu";
    public const string GameSceneName = "Level01";

    [SerializeField] MultiplayerSessionController session;

    bool _sceneOpInProgress;
    SteamLobbyService _steamLobby;

    void Awake()
    {
        if (session == null)
            session = GetComponent<MultiplayerSessionController>();
        if (_steamLobby == null)
            _steamLobby = GetComponent<SteamLobbyService>();
    }

    void OnEnable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnectedFromSession;
        if (_steamLobby == null)
            _steamLobby = GetComponent<SteamLobbyService>();
        if (_steamLobby != null)
            _steamLobby.LobbyJoinRequested += OnSteamLobbyJoinRequested;
    }

    void OnDisable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectedFromSession;
        if (_steamLobby != null)
            _steamLobby.LobbyJoinRequested -= OnSteamLobbyJoinRequested;
    }

    void OnClientDisconnectedFromSession(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || clientId != nm.LocalClientId || nm.IsHost)
            return;

        Scene active = SceneManager.GetActiveScene();
        if (active.IsValid() && active.name == GameSceneName)
            SceneManager.LoadScene(MenuSceneName, LoadSceneMode.Single);
    }

    public void QuitApplication()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    public void RequestHostThenGame(ushort port)
    {
        RequestHostLobby(port);
    }

    public void RequestJoinThenGame(string address, ushort port)
    {
        RequestJoinLobby(address, port);
    }

    public void RequestSteamHostThenGame()
    {
        RequestSteamHostLobby();
    }

    public void RequestSteamJoinThenGame(ulong hostSteamId)
    {
        RequestSteamJoinLobby(hostSteamId);
    }

    public void RequestSteamLobbyJoinThenGame(ulong lobbyId)
    {
        RequestSteamLobbyJoin(lobbyId);
    }

    public void RequestHostLobby(ushort port)
    {
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        session.StartHost(port);
    }

    public void RequestJoinLobby(string address, ushort port)
    {
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        string trimmed = string.IsNullOrWhiteSpace(address) ? session.DefaultAddress : address.Trim();
        session.StartClient(trimmed, port);
    }

    public void RequestSteamHostLobby()
    {
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        session.StartSteamHost();
    }

    public void RequestSteamJoinLobby(ulong hostSteamId)
    {
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        session.StartSteamClient(hostSteamId);
    }

    public void RequestSteamLobbyJoin(ulong lobbyId)
    {
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        session.JoinSteamLobby(lobbyId);
    }

    public void RequestStartGameFromLobby()
    {
        if (_sceneOpInProgress || session == null)
            return;

        session.StartGameFromLobby();
    }

    public void ReturnToMainMenu()
    {
        if (_sceneOpInProgress)
            return;

        StopAllCoroutines();
        if (session != null && session.IsSessionActive)
            session.ShutdownSession();

        SceneManager.LoadScene(MenuSceneName, LoadSceneMode.Single);
    }

    IEnumerator LoadGameSceneThenHost(ushort port)
    {
        _sceneOpInProgress = true;
        try
        {
            yield return LoadGameSceneIfNeeded();
            if (!IsActiveSceneGameScene())
            {
                Debug.LogError(
                    $"[Multiplayer] Still not in \"{GameSceneName}\" after load — host not started. " +
                    $"Check File → Build Settings lists that scene and the name matches exactly.");
                yield break;
            }

            session.StartHost(port);
        }
        finally
        {
            _sceneOpInProgress = false;
        }
    }

    IEnumerator LoadGameSceneThenJoin(string address, ushort port)
    {
        _sceneOpInProgress = true;
        try
        {
            yield return LoadGameSceneIfNeeded();
            if (!IsActiveSceneGameScene())
            {
                Debug.LogError(
                    $"[Multiplayer] Still not in \"{GameSceneName}\" after load — client not started. " +
                    $"Check File → Build Settings lists that scene and the name matches exactly.");
                yield break;
            }

            session.StartClient(address, port);
        }
        finally
        {
            _sceneOpInProgress = false;
        }
    }

    IEnumerator LoadGameSceneThenSteamHost()
    {
        _sceneOpInProgress = true;
        try
        {
            yield return LoadGameSceneIfNeeded();
            if (!IsActiveSceneGameScene())
            {
                Debug.LogError(
                    $"[Multiplayer] Still not in \"{GameSceneName}\" after load — Steam host not started. " +
                    $"Check File → Build Settings lists that scene and the name matches exactly.");
                yield break;
            }

            session.StartSteamHost();
        }
        finally
        {
            _sceneOpInProgress = false;
        }
    }

    IEnumerator LoadGameSceneThenSteamJoin(ulong hostSteamId)
    {
        _sceneOpInProgress = true;
        try
        {
            yield return LoadGameSceneIfNeeded();
            if (!IsActiveSceneGameScene())
            {
                Debug.LogError(
                    $"[Multiplayer] Still not in \"{GameSceneName}\" after load — Steam client not started. " +
                    $"Check File → Build Settings lists that scene and the name matches exactly.");
                yield break;
            }

            session.StartSteamClient(hostSteamId);
        }
        finally
        {
            _sceneOpInProgress = false;
        }
    }

    IEnumerator LoadGameSceneThenSteamLobbyJoin(ulong lobbyId)
    {
        _sceneOpInProgress = true;
        try
        {
            yield return LoadGameSceneIfNeeded();
            if (!IsActiveSceneGameScene())
            {
                Debug.LogError(
                    $"[Multiplayer] Still not in \"{GameSceneName}\" after load — Steam lobby join not started. " +
                    $"Check File → Build Settings lists that scene and the name matches exactly.");
                yield break;
            }

            session.JoinSteamLobby(lobbyId);
        }
        finally
        {
            _sceneOpInProgress = false;
        }
    }

    static bool IsActiveSceneGameScene()
    {
        Scene active = SceneManager.GetActiveScene();
        return active.IsValid() && active.name == GameSceneName;
    }

    IEnumerator LoadGameSceneIfNeeded()
    {
        if (session == null)
            yield break;

        Scene active = SceneManager.GetActiveScene();
        if (active.IsValid() && active.name == GameSceneName)
            yield break;

        AsyncOperation load = SceneManager.LoadSceneAsync(GameSceneName, LoadSceneMode.Single);
        if (load == null)
        {
            Debug.LogError($"[Multiplayer] Build Settings must include a scene named \"{GameSceneName}\".");
            yield break;
        }

        while (!load.isDone)
            yield return null;
    }

    void OnSteamLobbyJoinRequested(ulong lobbyId)
    {
        RequestSteamLobbyJoin(lobbyId);
    }
}
