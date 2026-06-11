using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds the layered menu atmosphere: base fill, drifting maze motif, two counter-scrolling
/// fog layers, bottom shading and vignette. The pause variant swaps the opaque base for a scrim
/// so the frozen game world reads through underneath.
/// </summary>
public static class MenuBackdrop
{
    public static void Build(Transform parent, bool pauseVariant)
    {
        RectTransform root = MenuWidgets.CreateStretched("Backdrop", parent);

        if (pauseVariant)
        {
            Image scrim = MenuWidgets.CreateImage(root, "Scrim", MenuTheme.Solid(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.86f), true);
            Stretch(scrim.rectTransform);
        }
        else
        {
            Image baseFill = MenuWidgets.CreateImage(root, "Base", MenuTheme.Solid(), MenuTheme.Bg, true);
            Stretch(baseFill.rectTransform);
        }

        // faint maze-line motif, drifting very slowly
        RawImage maze = MenuWidgets.CreateRawImage(root, "MazeMotif", MenuTheme.MazeTexture(),
            MenuTheme.WithAlpha(MenuTheme.Bone, pauseVariant ? 0.02f : 0.032f));
        Stretch(maze.rectTransform);
        maze.uvRect = new Rect(0f, 0f, 1.15f, 1.15f);
        var mazeScroll = maze.gameObject.AddComponent<UiUvScroll>();
        mazeScroll.velocity = new Vector2(0.0035f, 0.0016f);

        // two fog layers scrolling against each other; one carries a faint amber warmth
        RawImage fogA = MenuWidgets.CreateRawImage(root, "FogA", MenuTheme.FogTexture(),
            MenuTheme.WithAlpha(new Color(0.55f, 0.60f, 0.72f, 1f), pauseVariant ? 0.04f : 0.065f));
        Stretch(fogA.rectTransform);
        fogA.uvRect = new Rect(0f, 0f, 1.6f, 1.1f);
        var fogAScroll = fogA.gameObject.AddComponent<UiUvScroll>();
        fogAScroll.velocity = new Vector2(0.013f, 0.004f);

        RawImage fogB = MenuWidgets.CreateRawImage(root, "FogB", MenuTheme.FogTexture(),
            MenuTheme.WithAlpha(new Color(0.85f, 0.66f, 0.42f, 1f), pauseVariant ? 0.025f : 0.04f));
        Stretch(fogB.rectTransform);
        fogB.uvRect = new Rect(0.4f, 0.3f, 2.3f, 1.5f);
        var fogBScroll = fogB.gameObject.AddComponent<UiUvScroll>();
        fogBScroll.velocity = new Vector2(-0.009f, -0.003f);
        var fogFlicker = fogB.gameObject.AddComponent<UiFlicker>();
        fogFlicker.target = fogB;
        fogFlicker.baseAlpha = pauseVariant ? 0.025f : 0.04f;
        fogFlicker.amplitude = 0.02f;
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
            MenuTheme.WithAlpha(Color.black, pauseVariant ? 0.88f : 0.93f));
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
