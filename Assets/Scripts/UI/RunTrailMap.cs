using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Top-down map of the section that was just lost: the maze footprint (<see cref="MazeFootprint"/>) with every
/// player's recorded path (<see cref="RunTrailRecorder"/>) traced over it in their own colour. The trace is a
/// replay — all lines grow together against the clock the paths were recorded on, compressed into
/// <see cref="Play"/>'s duration — so who went where, and when, reads at a glance. A dot leads each line;
/// where a player died the dot becomes an X. A teleporter use leaves a blue portal mark at the orb and another
/// where the player came out, explaining the gap in their line.
///
/// Built by <see cref="TeamWipeSequence"/> on its card; owns the footprint texture it generates.
/// </summary>
[DisallowMultipleComponent]
public sealed class RunTrailMap : MonoBehaviour
{
    const float LineWidth = 4f;
    /// <summary>Distance between neighbouring players' lanes — a little over a line, so each stays its own colour.</summary>
    const float LaneSpacing = 5.5f;
    const float HeadSize = 14f;
    const float DeathMarkSize = 18f;
    const float TeleporterMarkSize = 20f;
    /// <summary>Recorded points closer than this on screen add nothing but vertices.</summary>
    const float MinPointSpacingPixels = 2.5f;
    /// <summary>Four vertices per segment against the 65k-vertex ceiling of one UI mesh, with headroom.</summary>
    const int MaxPointsPerTrail = 12000;
    const int FootprintTextureTargetPixels = 560;
    const float FramePadding = 18f;

    sealed class Lane
    {
        public UiPolyline Line;
        public RectTransform Head;
        public RectTransform DeathMark;
        public bool EndedByDeath;
        public RunTrailRecorder.Trail Trail;
        public Vector2 LaneOffset;
        public readonly List<TeleporterUse> TeleporterUses = new();
    }

    /// <summary>One teleporter use: a marker where it was used and one where the player came out.</summary>
    sealed class TeleporterUse
    {
        public RectTransform Departure;
        public RectTransform Arrival;
        public float DepartureTime;
        public float ArrivalTime;
    }

    readonly List<Lane> _lanes = new();
    Texture2D _footprintTexture;
    float _replayStart;
    float _replayEnd;
    float _playSeconds;
    float _elapsed;
    bool _playing;

    /// <summary>True once <see cref="Play"/> has run to the end (or there was nothing to trace).</summary>
    public bool IsFinished => !_playing && _elapsed > 0f;

    /// <summary>Whether there is any path to draw at all.</summary>
    public bool HasTrails => _lanes.Count > 0;

    /// <summary>
    /// Builds the map centred in <paramref name="parent"/>, aspect-fitted inside <paramref name="maxSize"/>. Uses
    /// the footprint only if it is the section being shown; otherwise the paths are framed on their own.
    /// </summary>
    public static RunTrailMap Create(Transform parent, Vector2 maxSize, IReadOnlyList<RunTrailRecorder.Trail> trails,
        MazeFootprint footprint)
    {
        RectTransform root = MenuWidgets.CreateRect("RunTrailMap", parent);
        root.anchorMin = new Vector2(0.5f, 0.5f);
        root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0.5f, 0.5f);

        RunTrailMap map = root.gameObject.AddComponent<RunTrailMap>();
        map.Build(root, maxSize, trails, footprint);
        return map;
    }

    /// <summary>Starts the replay; the lines take <paramref name="seconds"/> to reach their ends.</summary>
    public void Play(float seconds)
    {
        _playSeconds = Mathf.Max(0.01f, seconds);
        _elapsed = 0f;
        _playing = _lanes.Count > 0;
        if (!_playing)
            _elapsed = _playSeconds;
        ApplyReveal(_replayStart);
    }

    void Update()
    {
        if (!_playing)
            return;

        _elapsed += Time.unscaledDeltaTime;
        float t = Mathf.Clamp01(_elapsed / _playSeconds);
        ApplyReveal(Mathf.Lerp(_replayStart, _replayEnd, t));
        if (t >= 1f)
            _playing = false;
    }

    void OnDestroy()
    {
        if (_footprintTexture != null)
            Destroy(_footprintTexture);
    }

    void ApplyReveal(float revealTime)
    {
        for (int i = 0; i < _lanes.Count; i++)
        {
            Lane lane = _lanes[i];
            lane.Line.RevealTime = revealTime;

            bool hasTip = lane.Line.TryGetTip(out Vector2 tip, out bool complete);
            bool dead = complete && lane.EndedByDeath;
            // A player who left (rather than died) simply stops; only a death gets the X.
            bool showHead = hasTip && !complete;

            SetActive(lane.Head, showHead);
            SetActive(lane.DeathMark, dead);
            if (showHead)
                lane.Head.anchoredPosition = tip;
            if (dead)
                lane.DeathMark.anchoredPosition = tip;

            // The teleporter mark appears as the player reaches the orb, its partner as they come out.
            for (int t = 0; t < lane.TeleporterUses.Count; t++)
            {
                TeleporterUse use = lane.TeleporterUses[t];
                SetActive(use.Departure, revealTime >= use.DepartureTime);
                SetActive(use.Arrival, revealTime >= use.ArrivalTime);
            }
        }
    }

    static void SetActive(Component component, bool active)
    {
        if (component.gameObject.activeSelf != active)
            component.gameObject.SetActive(active);
    }

    // ---------------------------------------------------------------- build

    void Build(RectTransform root, Vector2 maxSize, IReadOnlyList<RunTrailRecorder.Trail> trails, MazeFootprint footprint)
    {
        bool useFootprint = footprint != null && footprint.GetFloorBounds().width > 0;
        // A metre of margin, or the outermost walls sit on the texture's edge and filter down to a faint pixel.
        Rect footprintWorld = default;
        if (useFootprint)
        {
            const float margin = 1f;
            Rect bounds = footprint.GetWorldRect();
            footprintWorld = Rect.MinMaxRect(bounds.xMin - margin, bounds.yMin - margin,
                bounds.xMax + margin, bounds.yMax + margin);
        }

        // Frame the maze AND every recorded point. The two should agree, but a path must never be drawn off the
        // plate: the whole point of the card is everywhere each player went.
        bool hasTrailPoints = TryGetTrailWorldRect(trails, out Rect trailWorld);
        Rect world;
        if (useFootprint && hasTrailPoints)
            world = Union(footprintWorld, trailWorld);
        else if (useFootprint)
            world = footprintWorld;
        else if (hasTrailPoints)
            world = trailWorld;
        else
            world = new Rect(-10f, -10f, 20f, 20f);

        // Aspect-fit the world rectangle inside the space the card gave us, inside a framed plate.
        Vector2 inner = maxSize - Vector2.one * (FramePadding * 2f);
        float scale = Mathf.Min(inner.x / Mathf.Max(0.01f, world.width), inner.y / Mathf.Max(0.01f, world.height));
        Vector2 mapSize = new Vector2(world.width, world.height) * scale;
        root.sizeDelta = mapSize + Vector2.one * (FramePadding * 2f);
        HudKit.AddPlate(root.gameObject, 0.8f, 0.22f);

        RectTransform area = MenuWidgets.CreateRect("Area", root);
        area.anchorMin = new Vector2(0.5f, 0.5f);
        area.anchorMax = new Vector2(0.5f, 0.5f);
        area.pivot = new Vector2(0.5f, 0.5f);
        area.sizeDelta = mapSize;

        if (useFootprint)
        {
            _footprintTexture = BuildFootprintTexture(footprint, footprintWorld);
            RawImage image = MenuWidgets.CreateRawImage(area, "Footprint", _footprintTexture, Color.white);
            RectTransform imageRt = image.rectTransform;
            imageRt.anchorMin = new Vector2(
                (footprintWorld.xMin - world.xMin) / world.width, (footprintWorld.yMin - world.yMin) / world.height);
            imageRt.anchorMax = new Vector2(
                (footprintWorld.xMax - world.xMin) / world.width, (footprintWorld.yMax - world.yMin) / world.height);
            imageRt.offsetMin = Vector2.zero;
            imageRt.offsetMax = Vector2.zero;
        }

        _replayStart = float.PositiveInfinity;
        _replayEnd = float.NegativeInfinity;

        var points = new List<Vector2>();
        var times = new List<float>();
        var breaks = new List<bool>();

        // Lowest colour slot first, so the stacking (and the legend) is the same on every peer.
        var ordered = new List<RunTrailRecorder.Trail>();
        for (int i = 0; i < trails.Count; i++)
        {
            if (trails[i].Points.Count > 0)
                ordered.Add(trails[i]);
        }
        ordered.Sort((a, b) => a.ColorIndex.CompareTo(b.ColorIndex));

        // Players move as a group most of the time, and identical routes would draw exactly on top of each other
        // — the first player's line simply vanishes under the last. So each player rides their own lane, like a
        // transit map: a fixed diagonal shift separates parallel runs both across and along the corridors. The
        // band of lanes has to fit inside one corridor on screen.
        float cellPixels = useFootprint ? footprint.CellSize * mapSize.x / world.width : float.PositiveInfinity;
        float laneSpacing = ordered.Count > 1
            ? Mathf.Min(LaneSpacing, cellPixels * 0.55f / (ordered.Count - 1))
            : 0f;

        for (int i = 0; i < ordered.Count; i++)
        {
            RunTrailRecorder.Trail trail = ordered[i];
            float lane = (i - (ordered.Count - 1) * 0.5f) * laneSpacing;
            Vector2 laneOffset = new(lane, -lane);
            ProjectAndThin(trail, world, mapSize, laneOffset, points, times, breaks);
            Color color = PlayerColor(trail.ColorIndex);

            UiPolyline line = new GameObject("Trail_" + trail.DisplayName, typeof(RectTransform)).AddComponent<UiPolyline>();
            line.gameObject.layer = 5;
            line.rectTransform.SetParent(area, false);
            line.rectTransform.SetStretch();
            line.raycastTarget = false;
            line.color = color;
            line.SetPath(points, times, breaks, LineWidth);

            _replayStart = Mathf.Min(_replayStart, line.StartTime);
            _replayEnd = Mathf.Max(_replayEnd, line.EndTime);

            _lanes.Add(new Lane
            {
                Line = line,
                EndedByDeath = trail.EndedByDeath,
                Trail = trail,
                LaneOffset = laneOffset,
            });
        }

        // Teleporter marks above every line; heads and X marks above those.
        for (int i = 0; i < _lanes.Count; i++)
        {
            Lane lane = _lanes[i];
            RunTrailRecorder.Trail trail = lane.Trail;
            for (int t = 0; t < trail.TeleporterArrivals.Count; t++)
            {
                int arrival = trail.TeleporterArrivals[t];
                if (arrival < 1 || arrival >= trail.Points.Count)
                    continue;

                RectTransform departure = CreateTeleporterMark(area);
                departure.anchoredPosition = Project(trail.Points[arrival - 1], world, mapSize) + lane.LaneOffset;
                RectTransform landing = CreateTeleporterMark(area);
                landing.anchoredPosition = Project(trail.Points[arrival], world, mapSize) + lane.LaneOffset;

                lane.TeleporterUses.Add(new TeleporterUse
                {
                    Departure = departure,
                    Arrival = landing,
                    DepartureTime = trail.Times[arrival - 1],
                    ArrivalTime = trail.Times[arrival],
                });
            }
        }

        // Markers above every line, so a dot is never buried under a teammate's trail.
        for (int i = 0; i < _lanes.Count; i++)
        {
            Color color = _lanes[i].Line.color;
            _lanes[i].Head = CreateHead(area, color);
            _lanes[i].DeathMark = CreateDeathMark(area, color);
        }

        if (_lanes.Count == 0)
        {
            _replayStart = 0f;
            _replayEnd = 0f;
        }
        else if (_replayEnd <= _replayStart)
        {
            _replayEnd = _replayStart + 0.01f;
        }

        ApplyReveal(_replayStart);
    }

    /// <summary>
    /// World X/Z → local position in a centred rect of <paramref name="mapSize"/>, shifted onto this player's
    /// lane, dropping points that would land within a couple of pixels of the last kept one. Breaks and the
    /// final point always survive — the final point is where the X goes.
    /// </summary>
    static void ProjectAndThin(RunTrailRecorder.Trail trail, Rect world, Vector2 mapSize, Vector2 laneOffset,
        List<Vector2> points, List<float> times, List<bool> breaks)
    {
        points.Clear();
        times.Clear();
        breaks.Clear();

        int count = trail.Points.Count;

        // A very long run could still blow the UI mesh budget at the normal spacing; widen it to fit.
        float pathPixels = 0f;
        for (int i = 1; i < count; i++)
            pathPixels += (Project(trail.Points[i], world, mapSize) - Project(trail.Points[i - 1], world, mapSize)).magnitude;
        float spacing = Mathf.Max(MinPointSpacingPixels, pathPixels / MaxPointsPerTrail);
        float minSqr = spacing * spacing;

        for (int i = 0; i < count; i++)
        {
            Vector2 local = Project(trail.Points[i], world, mapSize) + laneOffset;

            bool isBreak = trail.BreakBefore[i];
            bool isLast = i == count - 1;
            if (points.Count > 0 && !isBreak && !isLast && (local - points[points.Count - 1]).sqrMagnitude < minSqr)
                continue;

            points.Add(local);
            times.Add(trail.Times[i]);
            breaks.Add(points.Count > 1 && isBreak);
        }
    }

    static Vector2 Project(Vector3 p, Rect world, Vector2 mapSize)
    {
        return new Vector2(
            ((p.x - world.xMin) / world.width - 0.5f) * mapSize.x,
            ((p.z - world.yMin) / world.height - 0.5f) * mapSize.y);
    }

    static Rect Union(Rect a, Rect b)
    {
        return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin),
            Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
    }

    /// <summary>The recorded paths' world X/Z extent, with a little air around them. False when nothing was recorded.</summary>
    static bool TryGetTrailWorldRect(IReadOnlyList<RunTrailRecorder.Trail> trails, out Rect rect)
    {
        const float padding = 3f;
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (int t = 0; t < trails.Count; t++)
        {
            List<Vector3> points = trails[t].Points;
            for (int i = 0; i < points.Count; i++)
            {
                minX = Mathf.Min(minX, points[i].x);
                minZ = Mathf.Min(minZ, points[i].z);
                maxX = Mathf.Max(maxX, points[i].x);
                maxZ = Mathf.Max(maxZ, points[i].z);
            }
        }

        rect = minX > maxX ? default : Rect.MinMaxRect(minX - padding, minZ - padding, maxX + padding, maxZ + padding);
        return minX <= maxX;
    }

    public static Color PlayerColor(int colorIndex)
    {
        Color[] palette = MenuTheme.PlayerColors;
        return palette[((colorIndex % palette.Length) + palette.Length) % palette.Length];
    }

    // ---------------------------------------------------------------- footprint texture

    /// <summary>
    /// The footprint drawn over exactly <paramref name="world"/> (the footprint's own world rectangle): floor cells
    /// filled, a wall on every closed face (2px, centred on the cell edge, so a wall two cells share is drawn
    /// once), the pieces that spill past the grid (<see cref="MazeFootprint.ExtraFloorAreas"/>) as outlined floor,
    /// start and exit tinted. A closed face that opens onto one of those spill areas gets no wall — on the ground
    /// that is the same room carrying on.
    /// </summary>
    static Texture2D BuildFootprintTexture(MazeFootprint footprint, Rect world)
    {
        float cellSize = footprint.CellSize;
        float pixelsPerMeter = Mathf.Clamp(FootprintTextureTargetPixels / Mathf.Max(world.width, world.height),
            8f / cellSize, 40f / cellSize);
        int width = Mathf.Max(1, Mathf.CeilToInt(world.width * pixelsPerMeter));
        int height = Mathf.Max(1, Mathf.CeilToInt(world.height * pixelsPerMeter));
        var pixels = new Color32[width * height];
        var canvas = new FootprintCanvas(pixels, width, height, world, pixelsPerMeter);

        Color32 floor = MenuTheme.WithAlpha(MenuTheme.Stroke, 0.9f);
        Color32 startFloor = MenuTheme.WithAlpha(Color.Lerp(MenuTheme.Stroke, MenuTheme.Moss, 0.45f), 0.95f);
        Color32 exitFloor = MenuTheme.WithAlpha(Color.Lerp(MenuTheme.Stroke, MenuTheme.Amber, 0.5f), 0.95f);
        Color32 wall = MenuTheme.WithAlpha(MenuTheme.Mist, 0.75f);

        Vector2 startCorner = footprint.CellCornerWorldXZ(footprint.Start.x, footprint.Start.y);
        Rect startCell = new(startCorner, Vector2.one * cellSize);

        // Spill areas first: grid cells inside them draw over, keeping their own walls and tints.
        IReadOnlyList<Rect> spills = footprint.ExtraFloorAreas;
        for (int i = 0; i < spills.Count; i++)
        {
            Rect area = spills[i];
            canvas.FillWorld(area, area.Overlaps(startCell) ? startFloor : floor);
            canvas.OutlineWorld(area, wall);
        }

        RectInt cells = footprint.GetFloorBounds();
        for (int gy = cells.yMin; gy < cells.yMax; gy++)
        {
            for (int gx = cells.xMin; gx < cells.xMax; gx++)
            {
                if (!footprint.IsFloor(gx, gy))
                    continue;

                Vector2Int cell = new(gx, gy);
                Color32 fill = cell == footprint.Start ? startFloor : cell == footprint.Exit ? exitFloor : floor;
                canvas.FillWorld(new Rect(footprint.CellCornerWorldXZ(gx, gy), Vector2.one * cellSize), fill);
            }
        }

        for (int gy = cells.yMin; gy < cells.yMax; gy++)
        {
            for (int gx = cells.xMin; gx < cells.xMax; gx++)
            {
                MazeFaceMask open = footprint.GetOpenings(gx, gy);
                if (open == MazeFaceMask.None)
                    continue;

                Vector2 corner = footprint.CellCornerWorldXZ(gx, gy);
                float left = corner.x;
                float bottom = corner.y;
                float right = left + cellSize;
                float top = bottom + cellSize;

                if (WallStands(footprint, gx, gy, open, MazeFaceMask.North, 0, 1))
                    canvas.HorizontalWall(left, right, top, wall);
                if (WallStands(footprint, gx, gy, open, MazeFaceMask.South, 0, -1))
                    canvas.HorizontalWall(left, right, bottom, wall);
                if (WallStands(footprint, gx, gy, open, MazeFaceMask.East, 1, 0))
                    canvas.VerticalWall(bottom, top, right, wall);
                if (WallStands(footprint, gx, gy, open, MazeFaceMask.West, -1, 0))
                    canvas.VerticalWall(bottom, top, left, wall);
            }
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "MazeFootprint",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }

    /// <summary>A closed face draws a wall unless the far side is a spill area rather than another floor cell.</summary>
    static bool WallStands(MazeFootprint footprint, int gx, int gy, MazeFaceMask open, MazeFaceMask face, int dx, int dy)
    {
        if ((open & face) != 0)
            return false;
        if (footprint.IsFloor(gx + dx, gy + dy))
            return true;

        Vector2 corner = footprint.CellCornerWorldXZ(gx, gy);
        Vector2 center = corner + Vector2.one * (footprint.CellSize * 0.5f);
        Vector2 across = center + new Vector2(dx, dy) * (footprint.CellSize * 0.75f);
        return !footprint.IsInExtraFloorArea(across);
    }

    /// <summary>World X/Z drawing onto the footprint's pixel buffer (texture y up = world +Z).</summary>
    readonly struct FootprintCanvas
    {
        readonly Color32[] _pixels;
        readonly int _width;
        readonly int _height;
        readonly Rect _world;
        readonly float _pixelsPerMeter;

        public FootprintCanvas(Color32[] pixels, int width, int height, Rect world, float pixelsPerMeter)
        {
            _pixels = pixels;
            _width = width;
            _height = height;
            _world = world;
            _pixelsPerMeter = pixelsPerMeter;
        }

        int PixelX(float worldX) => Mathf.RoundToInt((worldX - _world.xMin) * _pixelsPerMeter);
        int PixelY(float worldZ) => Mathf.RoundToInt((worldZ - _world.yMin) * _pixelsPerMeter);

        public void FillWorld(Rect area, Color32 color)
        {
            int x0 = PixelX(area.xMin);
            int y0 = PixelY(area.yMin);
            Fill(x0, y0, PixelX(area.xMax) - x0, PixelY(area.yMax) - y0, color);
        }

        public void OutlineWorld(Rect area, Color32 color)
        {
            HorizontalWall(area.xMin, area.xMax, area.yMin, color);
            HorizontalWall(area.xMin, area.xMax, area.yMax, color);
            VerticalWall(area.yMin, area.yMax, area.xMin, color);
            VerticalWall(area.yMin, area.yMax, area.xMax, color);
        }

        /// <summary>2px line centred on world Z <paramref name="z"/>, overrunning its ends by a pixel to close corners.</summary>
        public void HorizontalWall(float xMin, float xMax, float z, Color32 color)
        {
            int x0 = PixelX(xMin) - 1;
            Fill(x0, PixelY(z) - 1, PixelX(xMax) + 1 - x0, 2, color);
        }

        /// <summary>2px line centred on world X <paramref name="x"/>.</summary>
        public void VerticalWall(float zMin, float zMax, float x, Color32 color)
        {
            int y0 = PixelY(zMin) - 1;
            Fill(PixelX(x) - 1, y0, 2, PixelY(zMax) + 1 - y0, color);
        }

        void Fill(int x, int y, int w, int h, Color32 color)
        {
            int x0 = Mathf.Max(0, x);
            int y0 = Mathf.Max(0, y);
            int x1 = Mathf.Min(_width, x + w);
            int y1 = Mathf.Min(_height, y + h);
            for (int py = y0; py < y1; py++)
            {
                int row = py * _width;
                for (int px = x0; px < x1; px++)
                    _pixels[row + px] = color;
            }
        }
    }

    // ---------------------------------------------------------------- markers + legend

    static RectTransform CreateHead(RectTransform parent, Color color)
    {
        Image ring = MenuWidgets.CreateImage(parent, "Head", MenuTheme.Circle(), MenuTheme.Ink);
        RectTransform rt = ring.rectTransform;
        rt.sizeDelta = Vector2.one * (HeadSize + 4f);

        Image dot = MenuWidgets.CreateImage(rt, "Dot", MenuTheme.Circle(), color);
        dot.rectTransform.sizeDelta = Vector2.one * HeadSize;

        rt.gameObject.SetActive(false);
        return rt;
    }

    /// <summary>Blue portal swirl on a dark disc, so it reads over floor and line alike.</summary>
    static RectTransform CreateTeleporterMark(RectTransform parent)
    {
        Image backing = MenuWidgets.CreateImage(parent, "Teleporter", MenuTheme.Circle(), MenuTheme.WithAlpha(MenuTheme.Ink, 0.9f));
        RectTransform rt = backing.rectTransform;
        rt.sizeDelta = Vector2.one * (TeleporterMarkSize + 5f);

        Image icon = MenuWidgets.CreateImage(rt, "Icon", MenuTheme.PortalIcon(), MenuTheme.TeleporterBlue);
        icon.rectTransform.sizeDelta = Vector2.one * TeleporterMarkSize;

        rt.gameObject.SetActive(false);
        return rt;
    }

    static RectTransform CreateDeathMark(RectTransform parent, Color color)
    {
        RectTransform root = MenuWidgets.CreateRect("Died", parent);
        root.sizeDelta = Vector2.one * DeathMarkSize;

        for (int i = 0; i < 2; i++)
        {
            float angle = i == 0 ? 45f : -45f;
            Image outline = MenuWidgets.CreateImage(root, "Outline" + i, MenuTheme.Solid(), MenuTheme.Ink);
            outline.rectTransform.sizeDelta = new Vector2(DeathMarkSize + 3f, 6f);
            outline.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        for (int i = 0; i < 2; i++)
        {
            float angle = i == 0 ? 45f : -45f;
            Image bar = MenuWidgets.CreateImage(root, "Bar" + i, MenuTheme.Solid(), color);
            bar.rectTransform.sizeDelta = new Vector2(DeathMarkSize, 3f);
            bar.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        root.gameObject.SetActive(false);
        return root;
    }

    /// <summary>One swatch + name per player who left a trail, in colour-slot order; a teleporter key if one was used.</summary>
    public static RectTransform CreateLegend(Transform parent, IReadOnlyList<RunTrailRecorder.Trail> trails)
    {
        RectTransform row = MenuWidgets.CreateRect("Legend", parent);
        var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 34f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        var ordered = new List<RunTrailRecorder.Trail>(trails);
        ordered.Sort((a, b) => a.ColorIndex.CompareTo(b.ColorIndex));
        bool anyTeleporter = false;
        for (int i = 0; i < ordered.Count; i++)
        {
            RunTrailRecorder.Trail trail = ordered[i];
            if (trail.Points.Count == 0)
                continue;
            anyTeleporter |= trail.TeleporterArrivals.Count > 0;

            RectTransform entry = CreateLegendEntry(row);
            Image swatch = MenuWidgets.CreateImage(entry, "Swatch", MenuTheme.Solid(), PlayerColor(trail.ColorIndex));
            LayoutElement swatchSize = swatch.gameObject.AddComponent<LayoutElement>();
            swatchSize.minWidth = swatchSize.preferredWidth = 26f;
            swatchSize.minHeight = swatchSize.preferredHeight = 5f;

            TextMeshProUGUI name = CreateLegendLabel(entry, trail.DisplayName ?? string.Empty, MenuTheme.Bone);
            name.richText = false; // persona names are user-authored
        }

        // Only when somebody actually used one.
        if (anyTeleporter)
        {
            RectTransform entry = CreateLegendEntry(row);
            Image icon = MenuWidgets.CreateImage(entry, "Icon", MenuTheme.PortalIcon(), MenuTheme.TeleporterBlue);
            LayoutElement iconSize = icon.gameObject.AddComponent<LayoutElement>();
            iconSize.minWidth = iconSize.preferredWidth = 18f;
            iconSize.minHeight = iconSize.preferredHeight = 18f;
            CreateLegendLabel(entry, "TELEPORTER", MenuTheme.Mist);
        }

        return row;
    }

    static RectTransform CreateLegendEntry(RectTransform row)
    {
        RectTransform entry = MenuWidgets.CreateRect("Entry", row);
        var entryLayout = entry.gameObject.AddComponent<HorizontalLayoutGroup>();
        entryLayout.spacing = 10f;
        entryLayout.childAlignment = TextAnchor.MiddleCenter;
        entryLayout.childControlWidth = true;
        entryLayout.childControlHeight = true;
        entryLayout.childForceExpandWidth = false;
        entryLayout.childForceExpandHeight = false;
        return entry;
    }

    static TextMeshProUGUI CreateLegendLabel(RectTransform entry, string text, Color color)
    {
        TextMeshProUGUI label = MenuWidgets.CreateText(entry, "Name", text, 16f, color,
            MenuWidgets.FontKind.Display, TextAlignmentOptions.Left, 3f);
        label.textWrappingMode = TextWrappingModes.NoWrap;
        return label;
    }
}
