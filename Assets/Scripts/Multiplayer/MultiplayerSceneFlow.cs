using System;
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

    /// <summary>Gameplay scenes that have a matching <c>ProceduralMazeConfig</c> under <c>Resources/MazeConfigs</c> (see <see cref="ProceduralMazeCoordinator"/>).</summary>
    public static readonly string[] MazeSectionSceneNames =
    {
        "Level01",
        "Level02",
        "Level03",
    };

    public static bool IsMazeGameplayScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
            return false;

        for (int i = 0; i < MazeSectionSceneNames.Length; i++)
        {
            if (string.Equals(MazeSectionSceneNames[i], sceneName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Returns the next entry in <see cref="MazeSectionSceneNames"/> after <paramref name="currentSceneName"/>, if any.</summary>
    public static bool TryGetNextMazeSectionScene(string currentSceneName, out string nextSceneName)
    {
        nextSceneName = null;
        if (string.IsNullOrEmpty(currentSceneName))
            return false;

        for (int i = 0; i < MazeSectionSceneNames.Length - 1; i++)
        {
            if (string.Equals(MazeSectionSceneNames[i], currentSceneName, StringComparison.OrdinalIgnoreCase))
            {
                nextSceneName = MazeSectionSceneNames[i + 1];
                return true;
            }
        }

        return false;
    }

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
            _steamLobby.LobbyJoinRequested += OnLobbyInviteAccepted;
    }

    void OnDisable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectedFromSession;
        if (_steamLobby != null)
            _steamLobby.LobbyJoinRequested -= OnLobbyInviteAccepted;
    }

    void OnClientDisconnectedFromSession(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || clientId != nm.LocalClientId || nm.IsHost)
            return;

        Scene active = SceneManager.GetActiveScene();
        if (active.IsValid() && IsMazeGameplayScene(active.name))
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

    /// <summary>Play Offline: skip the lobby entirely and load the first section as a solo run.</summary>
    public void RequestOfflineGame()
    {
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        session.StartOfflineGame(GameSceneName);
    }

    /// <summary>Host Game: open the lobby friends are invited into.</summary>
    public void RequestHostOnlineLobby()
    {
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        session.StartOnlineHost();
    }

    /// <summary>The one way into someone else's game: an invite the local player accepted.</summary>
    public void RequestJoinFromInvite(ulong lobbyId)
    {
        if (_sceneOpInProgress || session == null)
            return;

        if (session.IsSessionActive)
            return;

        StopAllCoroutines();
        session.JoinLobbyFromInvite(lobbyId);
    }

    public void RequestStartGameFromLobby(string sceneName = null)
    {
        if (_sceneOpInProgress || session == null)
            return;

        session.StartGameFromLobby(sceneName);
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

    void OnLobbyInviteAccepted(ulong lobbyId)
    {
        RequestJoinFromInvite(lobbyId);
    }
}
