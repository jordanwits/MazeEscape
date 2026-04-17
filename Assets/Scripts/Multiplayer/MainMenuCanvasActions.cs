using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Wires Host / Join / Quit on the menu canvas. Assign buttons in the Inspector, or leave empty
/// and use GameObject names HostButton, JoinButton, QuitButton under this canvas.
/// </summary>
[DisallowMultipleComponent]
public class MainMenuCanvasActions : MonoBehaviour
{
    [SerializeField] Button hostButton;
    [SerializeField] Button joinButton;
    [SerializeField] Button quitButton;

    MultiplayerSceneFlow _flow;
    MultiplayerSessionController _session;

    void Awake()
    {
        ResolveButtonsIfNeeded();
        if (MultiplayerBootstrap.Instance != null)
        {
            _flow = MultiplayerBootstrap.Instance.GetComponent<MultiplayerSceneFlow>();
            _session = MultiplayerBootstrap.Instance.GetComponent<MultiplayerSessionController>();
        }
        else
        {
            _flow = FindFirstObjectByType<MultiplayerSceneFlow>();
            _session = FindFirstObjectByType<MultiplayerSessionController>();
        }

        if (hostButton != null)
            hostButton.onClick.AddListener(OnHostClicked);
        if (joinButton != null)
            joinButton.onClick.AddListener(OnJoinClicked);
        if (quitButton != null)
            quitButton.onClick.AddListener(OnQuitClicked);
    }

    void OnDestroy()
    {
        if (hostButton != null)
            hostButton.onClick.RemoveListener(OnHostClicked);
        if (joinButton != null)
            joinButton.onClick.RemoveListener(OnJoinClicked);
        if (quitButton != null)
            quitButton.onClick.RemoveListener(OnQuitClicked);
    }

    void ResolveButtonsIfNeeded()
    {
        if (hostButton != null && joinButton != null && quitButton != null)
            return;

        foreach (Button b in GetComponentsInChildren<Button>(true))
        {
            if (b == null)
                continue;
            string n = b.gameObject.name;
            if (hostButton == null && n == "HostButton")
                hostButton = b;
            else if (joinButton == null && n == "JoinButton")
                joinButton = b;
            else if (quitButton == null && n == "QuitButton")
                quitButton = b;
        }
    }

    void OnHostClicked()
    {
        if (_flow == null || _session == null)
            return;

        _flow.RequestHostThenGame(_session.DefaultPort);
    }

    void OnJoinClicked()
    {
        if (_flow == null || _session == null)
            return;

        _flow.RequestJoinThenGame(_session.DefaultAddress, _session.DefaultPort);
    }

    void OnQuitClicked()
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
}
