using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Pause-menu backdrop: a heavy scrim plus weathering, fog and vignette that push the frozen game
/// world back behind the panels. The main menu takes only <see cref="BuildMenuScrim"/> — the
/// hallway in <c>Menu.unity</c> is its backdrop, so the weathering and fog layers would only veil it.
/// </summary>
public static class MenuBackdrop
{
    /// <summary>
    /// Main menu: a single 20% ink wash, nothing else. The VHS treatment lives in the camera pass
    /// (<see cref="MenuVhsFx"/>) rather than here — a UI overlay can only tint what is behind it,
    /// it cannot smear or split the channels of the rendered hallway.
    /// </summary>
    public static void BuildMenuScrim(Transform parent)
    {
        RectTransform root = MenuWidgets.CreateStretched("Backdrop", parent);
        Image scrim = MenuWidgets.CreateImage(root, "Scrim", MenuTheme.Solid(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.20f));
        Stretch(scrim.rectTransform);
    }

    public static void Build(Transform parent)
    {
        RectTransform root = MenuWidgets.CreateStretched("Backdrop", parent);

        Image scrim = MenuWidgets.CreateImage(root, "Scrim", MenuTheme.Solid(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.88f), true);
        Stretch(scrim.rectTransform);

        // mottled wall stains: a pale layer and an ink layer offset against each other
        RawImage stainLight = MenuWidgets.CreateRawImage(root, "WallStainLight", MenuTheme.WallTexture(),
            MenuTheme.WithAlpha(new Color(0.86f, 0.82f, 0.72f, 1f), 0.028f));
        Stretch(stainLight.rectTransform);
        stainLight.uvRect = new Rect(0.13f, 0.07f, 1.35f, 0.8f);

        RawImage stainDark = MenuWidgets.CreateRawImage(root, "WallStainDark", MenuTheme.WallTexture(),
            MenuTheme.WithAlpha(MenuTheme.Ink, 0.30f));
        Stretch(stainDark.rectTransform);
        stainDark.uvRect = new Rect(0.62f, 0.41f, 1.05f, 0.62f);

        // fine grain over the whole frame so flat fills never read as vector-clean
        RawImage grain = MenuWidgets.CreateRawImage(root, "Grain", MenuTheme.GrungeTexture(),
            MenuTheme.WithAlpha(Color.white, 0.015f));
        Stretch(grain.rectTransform);
        grain.uvRect = new Rect(0f, 0f, 7f, 4f);

        // faint maze-line motif, drifting very slowly
        RawImage maze = MenuWidgets.CreateRawImage(root, "MazeMotif", MenuTheme.MazeTexture(),
            MenuTheme.WithAlpha(MenuTheme.Bone, 0.015f));
        Stretch(maze.rectTransform);
        maze.uvRect = new Rect(0f, 0f, 1.15f, 1.15f);
        var mazeScroll = maze.gameObject.AddComponent<UiUvScroll>();
        mazeScroll.velocity = new Vector2(0.0035f, 0.0016f);

        // two fog layers scrolling against each other; one carries a faint mustard warmth
        RawImage fogA = MenuWidgets.CreateRawImage(root, "FogA", MenuTheme.FogTexture(),
            MenuTheme.WithAlpha(new Color(0.62f, 0.60f, 0.54f, 1f), 0.035f));
        Stretch(fogA.rectTransform);
        fogA.uvRect = new Rect(0f, 0f, 1.6f, 1.1f);
        var fogAScroll = fogA.gameObject.AddComponent<UiUvScroll>();
        fogAScroll.velocity = new Vector2(0.013f, 0.004f);

        RawImage fogB = MenuWidgets.CreateRawImage(root, "FogB", MenuTheme.FogTexture(),
            MenuTheme.WithAlpha(new Color(0.85f, 0.70f, 0.38f, 1f), 0.022f));
        Stretch(fogB.rectTransform);
        fogB.uvRect = new Rect(0.4f, 0.3f, 2.3f, 1.5f);
        var fogBScroll = fogB.gameObject.AddComponent<UiUvScroll>();
        fogBScroll.velocity = new Vector2(-0.009f, -0.003f);
        var fogFlicker = fogB.gameObject.AddComponent<UiFlicker>();
        fogFlicker.target = fogB;
        fogFlicker.baseAlpha = 0.022f;
        fogFlicker.amplitude = 0.018f;
        fogFlicker.speed = 0.55f;

        // bottom shading grounds the layout
        Image floor = MenuWidgets.CreateImage(root, "FloorShade", MenuTheme.VerticalGradient(),
            MenuTheme.WithAlpha(MenuTheme.Ink, 0.85f));
        RectTransform floorRt = floor.rectTransform;
        floorRt.anchorMin = new Vector2(0f, 0f);
        floorRt.anchorMax = new Vector2(1f, 0.38f);
        floorRt.offsetMin = Vector2.zero;
        floorRt.offsetMax = Vector2.zero;

        Image vignette = MenuWidgets.CreateImage(root, "Vignette", MenuTheme.Vignette(),
            MenuTheme.WithAlpha(Color.black, 0.88f));
        Stretch(vignette.rectTransform);
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
}
