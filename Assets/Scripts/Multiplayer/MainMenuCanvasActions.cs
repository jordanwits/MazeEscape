using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

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
    [SerializeField] AudioClip menuButtonClickClip;
    [SerializeField, Range(0f, 1f)] float menuButtonClickVolume = 0.75f;

    MultiplayerSceneFlow _flow;
    MultiplayerSessionController _session;
    AudioSource _menuUiAudioSource;

#if UNITY_EDITOR
    void OnValidate()
    {
        if (menuButtonClickClip == null)
            menuButtonClickClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Click.wav");
    }
#endif

    void Awake()
    {
        ResolveButtonsIfNeeded();
        EnsureMenuClickAudio();
        WireMenuButtonClickSounds();

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

        foreach (Button b in GetComponentsInChildren<Button>(true))
        {
            if (b != null)
                b.onClick.RemoveListener(PlayMenuButtonClick);
        }
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

    void EnsureMenuClickAudio()
    {
        if (_menuUiAudioSource == null)
            _menuUiAudioSource = GetComponent<AudioSource>();
        if (_menuUiAudioSource == null)
            _menuUiAudioSource = gameObject.AddComponent<AudioSource>();

        _menuUiAudioSource.playOnAwake = false;
        _menuUiAudioSource.loop = false;
        _menuUiAudioSource.spatialBlend = 0f;
        _menuUiAudioSource.dopplerLevel = 0f;
    }

    void WireMenuButtonClickSounds()
    {
        foreach (Button b in GetComponentsInChildren<Button>(true))
        {
            if (b == null)
                continue;
            b.onClick.AddListener(PlayMenuButtonClick);
        }
    }

    void PlayMenuButtonClick()
    {
        if (menuButtonClickClip == null || _menuUiAudioSource == null)
            return;

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_menuUiAudioSource);

        _menuUiAudioSource.PlayOneShot(menuButtonClickClip, Mathf.Max(0f, menuButtonClickVolume));
    }

    void OnHostClicked()
    {
        if (_flow == null || _session == null)
            return;

        _flow.RequestHostLobby(_session.DefaultPort);
    }

    void OnJoinClicked()
    {
        if (_flow == null || _session == null)
            return;

        _flow.RequestJoinLobby(_session.DefaultAddress, _session.DefaultPort);
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
