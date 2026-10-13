using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Peer-local end of a lost run (see <see cref="TeamWipeDirector"/>): a short beat for the last body to land,
/// an "ALL DEAD" card over everything ("NO WAY OUT" when someone is left alive but locked in the jail for good)
/// — with a map of the section that replays where every player walked
/// (<see cref="RunTrailMap"/>) — then the session ends and this peer returns to the main menu, the same exit
/// <see cref="ElevatorFinishController"/> takes after the final section.
///
/// Every peer draws the map from its own recording (<see cref="RunTrailRecorder"/>) and its own build of the
/// maze, and every peer runs the same timeline, so nothing about the card is networked.
///
/// Lives in the gameplay scene, so <see cref="IsRunning"/> clears by itself when the menu load tears it down.
/// </summary>
[DisallowMultipleComponent]
public sealed class TeamWipeSequence : MonoBehaviour
{
    const float LastBodyLandSeconds = 1.4f;
    const float CardFadeInSeconds = 0.9f;
    /// <summary>How long the paths take to draw, however long the section actually lasted.</summary>
    const float TraceSeconds = 5f;
    /// <summary>Time to take in the finished map before leaving.</summary>
    const float CardHoldSeconds = 3.5f;
    // The host shuts the session down, so it leaves last: clients that are still loading out when the host
    // stops would otherwise be dropped mid-card (they still land in the menu, just without the hold).
    const float HostExtraHoldSeconds = 1f;
    const int CardSortingOrder = 5000; // above the pause menu (4000) and the HUD

    // Layout on the 1920x1080 reference canvas: title band on top, map in the middle, legend underneath.
    const float TitleTop = 40f;
    const float TitleHeight = 130f;
    static readonly Vector2 MapMaxSize = new(1280f, 640f);
    const float MapCenterY = -20f;
    const float LegendBottom = 70f;

    static TeamWipeSequence s_instance;

    CanvasGroup _cardGroup;
    RunTrailMap _map;
    bool _everyoneDead = true;

    public static bool IsRunning => s_instance != null;

    /// <summary>
    /// Starts this peer's wipe sequence once; repeated calls are ignored. <paramref name="everyoneDead"/> is false
    /// when someone is still alive but locked in the jail with no way out, which changes the card's title.
    /// </summary>
    public static void Begin(bool everyoneDead = true)
    {
        if (s_instance != null)
            return;

        var go = new GameObject(nameof(TeamWipeSequence));
        s_instance = go.AddComponent<TeamWipeSequence>();
        s_instance._everyoneDead = everyoneDead;
    }

    void Start()
    {
        StartCoroutine(Run());
    }

    void OnDestroy()
    {
        if (s_instance == this)
            s_instance = null;
    }

    IEnumerator Run()
    {
        yield return new WaitForSecondsRealtime(LastBodyLandSeconds);

        BuildCard();
        for (float t = 0f; t < CardFadeInSeconds; t += Time.unscaledDeltaTime)
        {
            _cardGroup.alpha = Mathf.SmoothStep(0f, 1f, t / CardFadeInSeconds);
            yield return null;
        }
        _cardGroup.alpha = 1f;

        // A fixed wait rather than waiting on the map: every peer must leave on the same clock, and the host
        // (which ends the session) last of all.
        if (_map != null && _map.HasTrails)
        {
            _map.Play(TraceSeconds);
            yield return new WaitForSecondsRealtime(TraceSeconds);
        }

        float hold = CardHoldSeconds;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening && nm.IsServer)
            hold += HostExtraHoldSeconds;
        yield return new WaitForSecondsRealtime(hold);

        ElevatorFinishController.RequestReturnToMainMenuLocal();
    }

    void BuildCard()
    {
        // The card replaces the game from here on; the spectator panel and the rest of the HUD sit exactly where
        // the legend goes and would show through the dim.
        HudKit.SetHudCanvasVisible(false);

        Canvas canvas = MenuWidgets.CreateCanvas("TeamWipeCard", CardSortingOrder, transform);
        _cardGroup = canvas.gameObject.AddComponent<CanvasGroup>();
        _cardGroup.alpha = 0f;
        _cardGroup.blocksRaycasts = false;
        _cardGroup.interactable = false;

        Image dim = MenuWidgets.CreateImage(canvas.transform, "Dim", MenuTheme.Solid(),
            MenuTheme.WithAlpha(MenuTheme.Ink, 0.9f));
        dim.rectTransform.SetStretch();

        Image vignette = MenuWidgets.CreateImage(canvas.transform, "Vignette", MenuTheme.Vignette(), MenuTheme.Ink);
        vignette.rectTransform.SetStretch();

        BuildTitle(canvas.transform);

        // The recorder froze the moment the wipe began; the footprint is only the right map if this peer's last
        // build was this section.
        Scene active = SceneManager.GetActiveScene();
        MazeFootprint footprint = MazeFootprint.Current;
        if (footprint != null && (!active.IsValid() || footprint.SceneName != active.name))
            footprint = null;

        IReadOnlyList<RunTrailRecorder.Trail> trails = RunTrailRecorder.CurrentTrails;
        if (footprint == null && trails.Count == 0)
            return;

        RectTransform mapSlot = MenuWidgets.CreateRect("MapSlot", canvas.transform);
        mapSlot.anchorMin = new Vector2(0.5f, 0.5f);
        mapSlot.anchorMax = new Vector2(0.5f, 0.5f);
        mapSlot.pivot = new Vector2(0.5f, 0.5f);
        mapSlot.anchoredPosition = new Vector2(0f, MapCenterY);
        mapSlot.sizeDelta = MapMaxSize;
        mapSlot.localRotation = Quaternion.Euler(0f, 0f, 0.3f);
        _map = RunTrailMap.Create(mapSlot, MapMaxSize, trails, footprint);

        RectTransform legend = RunTrailMap.CreateLegend(canvas.transform, trails);
        legend.anchorMin = new Vector2(0.5f, 0f);
        legend.anchorMax = new Vector2(0.5f, 0f);
        legend.pivot = new Vector2(0.5f, 0f);
        legend.anchoredPosition = new Vector2(0f, LegendBottom);
        legend.sizeDelta = new Vector2(1600f, 30f);
    }

    void BuildTitle(Transform parent)
    {
        RectTransform block = MenuWidgets.CreateRect("Block", parent);
        block.anchorMin = new Vector2(0.5f, 1f);
        block.anchorMax = new Vector2(0.5f, 1f);
        block.pivot = new Vector2(0.5f, 1f);
        block.anchoredPosition = new Vector2(0f, -TitleTop);
        block.sizeDelta = new Vector2(1200f, TitleHeight);
        block.localRotation = Quaternion.Euler(0f, 0f, -0.6f);

        // A prisoner left alive in the jail with nobody to free them ends the run too, but they are not dead.
        string titleText = _everyoneDead ? "ALL DEAD" : "NO WAY OUT";

        // Same misprint treatment as the pause title, in blood instead of amber.
        TextMeshProUGUI misprint = MenuWidgets.CreateText(block, "TitleMisprint", titleText, 104f,
            MenuTheme.WithAlpha(MenuTheme.Blood, 0.85f), MenuWidgets.FontKind.Display,
            TextAlignmentOptions.Center, 8f, FontStyles.Bold);
        misprint.rectTransform.SetStretch();
        misprint.rectTransform.anchoredPosition = new Vector2(7f, -5f);
        misprint.textWrappingMode = TextWrappingModes.NoWrap;

        TextMeshProUGUI title = MenuWidgets.CreateText(block, "Title", titleText, 104f, MenuTheme.Bone,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 8f, FontStyles.Bold);
        title.rectTransform.SetStretch();
        title.textWrappingMode = TextWrappingModes.NoWrap;

        Scene active = SceneManager.GetActiveScene();
        TextMeshProUGUI section = MenuWidgets.CreateText(parent, "Section",
            active.IsValid() ? active.name.ToUpperInvariant() : string.Empty, 16f, MenuTheme.Mist,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Center, 9f);
        RectTransform sectionRt = section.rectTransform;
        sectionRt.anchorMin = new Vector2(0.5f, 1f);
        sectionRt.anchorMax = new Vector2(0.5f, 1f);
        sectionRt.pivot = new Vector2(0.5f, 1f);
        sectionRt.anchoredPosition = new Vector2(0f, -(TitleTop + TitleHeight + 2f));
        sectionRt.sizeDelta = new Vector2(600f, 26f);
    }
}
