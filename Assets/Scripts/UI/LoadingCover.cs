using System;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Full-screen card that hides a maze section load: it rises as the load starts, holds through the scene swap and
/// this peer's own maze build (the host builds in one long frame; a client waits for the seed round-trip with
/// nothing to stand on), then fades out a couple of frames after the maze exists. Shows the section's number and
/// title (<see cref="MultiplayerSceneFlow.TryGetMazeSectionTitle"/>).
///
/// Every peer runs its own: the server-side load calls raise it through <see cref="Show"/>, clients raise it on
/// NGO's Load scene event, and any maze scene that finishes loading with no cover up snaps one up opaque, so a
/// path that skips both still never shows the unbuilt level. Loading anything else (the menu) drops it at once.
///
/// Purely visual: it does not block gameplay input. Clients are already frozen by the maze-ready gate, and the
/// host's window is the cover's minimum hold. Lives on the multiplayer bootstrap, so it survives the loads it hides.
/// </summary>
[DisallowMultipleComponent]
public sealed class LoadingCover : MonoBehaviour
{
    const int CoverSortingOrder = 6000; // above the game-over card (5000), the pause menu (4000) and the HUD
    const float FadeInSeconds = 0.25f;
    const float FadeOutSeconds = 0.6f;
    /// <summary>Long enough to read the section title even when the load itself is instant.</summary>
    const float MinVisibleSeconds = 1.4f;
    /// <summary>Rendered frames held after the build, so culling, fog and the first post-hitch frame settle unseen.</summary>
    const int SettleFrames = 2;
    /// <summary>A load that never starts or a build that never comes must not leave the screen black forever.</summary>
    const float GiveUpSeconds = 30f;
    /// <summary>Fades step by at most this much time per frame, so the frame after the build hitch can't skip them.</summary>
    const float MaxFadeStep = 1f / 30f;

    enum Phase { Hidden, Loading, AwaitingBuild, Lifting }

    static LoadingCover s_instance;

    Canvas _canvas;
    CanvasGroup _group;
    TextMeshProUGUI _sectionLabel;
    TextMeshProUGUI _title;
    TextMeshProUGUI _titleMisprint;

    Phase _phase = Phase.Hidden;
    string _sceneName;
    float _shownAt;
    float _phaseStartedAt;
    int _settleFramesLeft;

    NetworkManager _networkManager;
    NetworkSceneManager _hookedSceneManager;

    /// <summary>True from the moment the cover rises until it has fully faded out.</summary>
    public static bool IsShowing => s_instance != null && s_instance._phase != Phase.Hidden;

    /// <summary>
    /// Raises the cover for a load into <paramref name="sceneName"/> (ignored for anything but a maze section).
    /// Call it right after starting the load: the load is asynchronous, so the cover is up before the swap.
    /// </summary>
    public static void Show(string sceneName)
    {
        if (s_instance != null)
            s_instance.Raise(sceneName);
    }

    void Awake()
    {
        if (s_instance != null && s_instance != this)
        {
            Destroy(this);
            return;
        }

        s_instance = this;
    }

    void OnDestroy()
    {
        if (s_instance == this)
            s_instance = null;
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
        ProceduralMazeCoordinator.LocalMazeBuilt += HandleLocalMazeBuilt;

        if (!TryGetComponent(out _networkManager))
            _networkManager = NetworkManager.Singleton;
        if (_networkManager != null)
        {
            _networkManager.OnClientStarted += HookSceneEvents;
            _networkManager.OnClientStopped += HandleClientStopped;
            HookSceneEvents();
        }
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        ProceduralMazeCoordinator.LocalMazeBuilt -= HandleLocalMazeBuilt;

        if (_networkManager != null)
        {
            _networkManager.OnClientStarted -= HookSceneEvents;
            _networkManager.OnClientStopped -= HandleClientStopped;
        }

        UnhookSceneEvents();
    }

    /// <summary>NGO builds a new <see cref="NetworkSceneManager"/> for every session, so this re-hooks per start.</summary>
    void HookSceneEvents()
    {
        NetworkSceneManager sceneManager = _networkManager != null ? _networkManager.SceneManager : null;
        if (sceneManager == _hookedSceneManager)
            return;

        UnhookSceneEvents();
        if (sceneManager == null)
            return;

        sceneManager.OnSceneEvent += HandleSceneEvent;
        _hookedSceneManager = sceneManager;
    }

    void UnhookSceneEvents()
    {
        if (_hookedSceneManager != null)
            _hookedSceneManager.OnSceneEvent -= HandleSceneEvent;
        _hookedSceneManager = null;
    }

    void HandleSceneEvent(SceneEvent sceneEvent)
    {
        // Clients also hear about every other client's progress; only this peer's own load start matters.
        if (sceneEvent.SceneEventType != SceneEventType.Load || _networkManager == null
            || sceneEvent.ClientId != _networkManager.LocalClientId)
        {
            return;
        }

        Raise(sceneEvent.SceneName);
    }

    void HandleClientStopped(bool wasHost)
    {
        UnhookSceneEvents();

        // A session that ends mid-load never delivers the scene or the seed this cover is waiting for.
        HideNow();
    }

    void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single)
            return;

        if (!MultiplayerSceneFlow.IsMazeGameplayScene(scene.name))
        {
            HideNow();
            return;
        }

        // The new scene renders for the first time at the end of this frame and has no maze yet: whatever the
        // fade-in had reached, it is fully opaque from here.
        Raise(scene.name);
        _group.alpha = 1f;
        EnterPhase(Phase.AwaitingBuild);
    }

    void HandleLocalMazeBuilt(string sceneName)
    {
        if (_phase != Phase.AwaitingBuild || !string.Equals(sceneName, _sceneName, StringComparison.OrdinalIgnoreCase))
            return;

        _settleFramesLeft = SettleFrames;
        EnterPhase(Phase.Lifting);
    }

    void Raise(string sceneName)
    {
        if (!MultiplayerSceneFlow.TryGetMazeSectionTitle(sceneName, out int number, out int count, out string title))
            return;

        // Already covering this load (the server's own Show and its Load event both arrive): nothing to restart.
        if ((_phase == Phase.Loading || _phase == Phase.AwaitingBuild)
            && string.Equals(sceneName, _sceneName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureBuilt();
        _sceneName = sceneName;
        _sectionLabel.text = $"SECTION {number} / {count}";
        _title.text = title ?? sceneName.ToUpperInvariant();
        _titleMisprint.text = _title.text;

        if (_phase == Phase.Hidden)
        {
            _group.alpha = 0f;
            _shownAt = Time.unscaledTime;
        }

        _canvas.enabled = true;
        _group.blocksRaycasts = true; // nothing behind it (a lobby button) takes a click mid-load
        EnterPhase(Phase.Loading);
    }

    void EnterPhase(Phase phase)
    {
        _phase = phase;
        _phaseStartedAt = Time.unscaledTime;
    }

    void HideNow()
    {
        _phase = Phase.Hidden;
        if (_canvas == null)
            return;

        _group.alpha = 0f;
        _group.blocksRaycasts = false;
        _canvas.enabled = false;
    }

    void Update()
    {
        if (_phase == Phase.Hidden)
            return;

        float now = Time.unscaledTime;
        float step = Mathf.Min(Time.unscaledDeltaTime, MaxFadeStep);

        switch (_phase)
        {
            case Phase.Loading:
                _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, step / FadeInSeconds);
                if (now - _phaseStartedAt > GiveUpSeconds)
                {
                    Debug.LogWarning($"[LoadingCover] \"{_sceneName}\" never finished loading; dropping the cover.", this);
                    HideNow();
                }
                break;

            case Phase.AwaitingBuild:
                _group.alpha = 1f;
                if (now - _phaseStartedAt > GiveUpSeconds)
                {
                    Debug.LogWarning($"[LoadingCover] No maze build for \"{_sceneName}\"; lifting the cover anyway.", this);
                    _settleFramesLeft = 0;
                    EnterPhase(Phase.Lifting);
                }
                break;

            case Phase.Lifting:
                if (_settleFramesLeft > 0)
                {
                    _settleFramesLeft--;
                    break;
                }

                if (now - _shownAt < MinVisibleSeconds)
                    break;

                _group.alpha = Mathf.MoveTowards(_group.alpha, 0f, step / FadeOutSeconds);
                if (_group.alpha <= 0f)
                    HideNow();
                break;
        }
    }

    void EnsureBuilt()
    {
        if (_canvas != null)
            return;

        _canvas = MenuWidgets.CreateCanvas("LoadingCover", CoverSortingOrder, transform);
        _group = _canvas.gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _group.interactable = false;

        // An opaque base under the pause menu's weathered backdrop: that one is a scrim meant to let the frozen
        // game show through, and here there must be nothing to see.
        Image solid = MenuWidgets.CreateImage(_canvas.transform, "Solid", MenuTheme.Solid(), MenuTheme.Ink, true);
        solid.rectTransform.SetStretch();
        MenuBackdrop.Build(_canvas.transform);

        Image vignette = MenuWidgets.CreateImage(_canvas.transform, "Vignette", MenuTheme.Vignette(), MenuTheme.Ink);
        vignette.rectTransform.SetStretch();

        BuildTitleBlock(_canvas.transform);
        BuildLoadingTag(_canvas.transform);
    }

    void BuildTitleBlock(Transform parent)
    {
        RectTransform block = MenuWidgets.CreateRect("TitleBlock", parent);
        block.anchorMin = new Vector2(0.5f, 0.5f);
        block.anchorMax = new Vector2(0.5f, 0.5f);
        block.pivot = new Vector2(0.5f, 0.5f);
        block.anchoredPosition = new Vector2(0f, 20f);
        block.sizeDelta = new Vector2(1400f, 220f);
        block.localRotation = Quaternion.Euler(0f, 0f, -0.6f);

        _sectionLabel = MenuWidgets.CreateText(block, "Section", string.Empty, 18f, MenuTheme.Mist,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 9f);
        RectTransform sectionRt = _sectionLabel.rectTransform;
        sectionRt.anchorMin = new Vector2(0f, 1f);
        sectionRt.anchorMax = new Vector2(1f, 1f);
        sectionRt.pivot = new Vector2(0.5f, 1f);
        sectionRt.anchoredPosition = Vector2.zero;
        sectionRt.sizeDelta = new Vector2(0f, 28f);
        _sectionLabel.textWrappingMode = TextWrappingModes.NoWrap;

        RectTransform titleRt = MenuWidgets.CreateRect("Title", block);
        titleRt.anchorMin = new Vector2(0f, 0.5f);
        titleRt.anchorMax = new Vector2(1f, 0.5f);
        titleRt.pivot = new Vector2(0.5f, 0.5f);
        titleRt.anchoredPosition = new Vector2(0f, -6f);
        titleRt.sizeDelta = new Vector2(0f, 130f);

        // Same misprint treatment as the menu and pause titles: an amber copy knocked off register behind the bone.
        _titleMisprint = MenuWidgets.CreateText(titleRt, "TitleMisprint", string.Empty, 104f,
            MenuTheme.WithAlpha(MenuTheme.Amber, 0.85f), MenuWidgets.FontKind.Display,
            TextAlignmentOptions.Center, 8f, FontStyles.Bold);
        _titleMisprint.rectTransform.SetStretch();
        _titleMisprint.rectTransform.anchoredPosition = new Vector2(7f, -5f);
        _titleMisprint.textWrappingMode = TextWrappingModes.NoWrap;

        _title = MenuWidgets.CreateText(titleRt, "Title", string.Empty, 104f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 8f, FontStyles.Bold);
        _title.rectTransform.SetStretch();
        _title.textWrappingMode = TextWrappingModes.NoWrap;

        Image tear = MenuWidgets.CreateImage(block, "Tear", MenuTheme.TornBar(), MenuTheme.WithAlpha(MenuTheme.Bone, 0.16f));
        RectTransform tearRt = tear.rectTransform;
        tearRt.anchorMin = new Vector2(0.5f, 0f);
        tearRt.anchorMax = new Vector2(0.5f, 0f);
        tearRt.pivot = new Vector2(0.5f, 0f);
        tearRt.anchoredPosition = new Vector2(0f, 18f);
        tearRt.sizeDelta = new Vector2(560f, 4f);
        tearRt.localRotation = Quaternion.Euler(0f, 0f, 0.4f);
    }

    /// <summary>A slow pulse in the corner, so a client waiting on the seed can see the game hasn't hung.</summary>
    static void BuildLoadingTag(Transform parent)
    {
        TextMeshProUGUI tag = MenuWidgets.CreateText(parent, "LoadingTag", "LOADING", 18f, MenuTheme.Faint,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.BottomRight, 9f);
        RectTransform rt = tag.rectTransform;
        rt.anchorMin = new Vector2(1f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-56f, 48f);
        rt.sizeDelta = new Vector2(320f, 28f);
        tag.textWrappingMode = TextWrappingModes.NoWrap;

        var pulse = tag.gameObject.AddComponent<UiPulse>();
        pulse.target = tag;
        pulse.minAlpha = 0.3f;
        pulse.maxAlpha = 0.9f;
        pulse.speed = 2.4f;
    }
}
