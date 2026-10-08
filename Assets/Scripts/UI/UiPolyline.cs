using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A path drawn as one flat quad per segment, revealed up to <see cref="RevealTime"/>: every point carries a
/// timestamp, and only the stretch walked by that time is drawn — the last segment partially, so the line
/// grows smoothly between samples. Points flagged as a break start a new stroke (no segment is drawn into
/// them). Square caps overlap at the joints, which reads as continuous at hairline widths.
///
/// Rebuilds its mesh whenever the reveal moves, so keep the point count modest (thin by on-screen distance
/// before handing it over — a UI mesh tops out at 65k vertices, four per segment here).
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class UiPolyline : MaskableGraphic
{
    readonly List<Vector2> _points = new();
    readonly List<float> _times = new();
    readonly List<bool> _breakBefore = new();
    float _width = 4f;
    float _revealTime = float.PositiveInfinity;

    public int PointCount => _points.Count;
    public float StartTime => _times.Count > 0 ? _times[0] : 0f;
    public float EndTime => _times.Count > 0 ? _times[_times.Count - 1] : 0f;

    /// <summary>Points in this graphic's local space, with matching timestamps and break flags.</summary>
    public void SetPath(List<Vector2> points, List<float> times, List<bool> breakBefore, float width)
    {
        _points.Clear();
        _points.AddRange(points);
        _times.Clear();
        _times.AddRange(times);
        _breakBefore.Clear();
        _breakBefore.AddRange(breakBefore);
        _width = Mathf.Max(0.5f, width);
        SetVerticesDirty();
    }

    public float RevealTime
    {
        get => _revealTime;
        set
        {
            if (Mathf.Approximately(_revealTime, value))
                return;
            _revealTime = value;
            SetVerticesDirty();
        }
    }

    /// <summary>
    /// Where the drawn line currently ends. False until the first point is reached; <paramref name="complete"/>
    /// once the reveal has passed the last point.
    /// </summary>
    public bool TryGetTip(out Vector2 tip, out bool complete)
    {
        tip = default;
        complete = false;
        int n = _points.Count;
        if (n == 0 || _revealTime < _times[0])
            return false;

        for (int i = 1; i < n; i++)
        {
            if (_times[i] <= _revealTime)
                continue;

            float span = _times[i] - _times[i - 1];
            float f = span > 0f ? Mathf.Clamp01((_revealTime - _times[i - 1]) / span) : 1f;
            // A break means the player was elsewhere in between: stay put, then appear at the far side.
            tip = _breakBefore[i] ? _points[i - 1] : Vector2.Lerp(_points[i - 1], _points[i], f);
            return true;
        }

        tip = _points[n - 1];
        complete = true;
        return true;
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        int n = _points.Count;
        for (int i = 1; i < n; i++)
        {
            float t0 = _times[i - 1];
            if (t0 > _revealTime)
                break;
            if (_breakBefore[i])
                continue;

            Vector2 a = _points[i - 1];
            Vector2 b = _points[i];
            float t1 = _times[i];
            if (t1 > _revealTime)
            {
                float span = t1 - t0;
                b = Vector2.Lerp(a, b, span > 0f ? (_revealTime - t0) / span : 1f);
                AddSegment(vh, a, b);
                break;
            }

            AddSegment(vh, a, b);
        }
    }

    void AddSegment(VertexHelper vh, Vector2 a, Vector2 b)
    {
        Vector2 delta = b - a;
        float length = delta.magnitude;
        if (length < 0.01f)
            return;

        float half = _width * 0.5f;
        Vector2 dir = delta / length;
        Vector2 normal = new Vector2(-dir.y, dir.x) * half;
        a -= dir * half;
        b += dir * half;

        Color32 c = color;
        int index = vh.currentVertCount;
        vh.AddVert(a - normal, c, Vector4.zero);
        vh.AddVert(a + normal, c, Vector4.zero);
        vh.AddVert(b + normal, c, Vector4.zero);
        vh.AddVert(b - normal, c, Vector4.zero);
        vh.AddTriangle(index, index + 1, index + 2);
        vh.AddTriangle(index, index + 2, index + 3);
    }
}
