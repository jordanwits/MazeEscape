using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Loads the gameplay scene before starting NGO host/client so the player spawns in the right level.
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

    void Awake()
    {
        if (session == null)
            session = GetComponent<MultiplayerSessionController>();
    }

    void OnEnable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnectedFromSession;
    }

    void OnDisable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectedFromSession;
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
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        StartCoroutine(LoadGameSceneThenHost(port));
    }

    public void RequestJoinThenGame(string address, ushort port)
    {
        if (_sceneOpInProgress || session == null)
            return;

        StopAllCoroutines();
        string trimmed = string.IsNullOrWhiteSpace(address) ? session.DefaultAddress : address.Trim();
        StartCoroutine(LoadGameSceneThenJoin(trimmed, port));
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
}
