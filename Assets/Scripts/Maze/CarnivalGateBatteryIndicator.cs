using TMPro;
using UnityEngine;

/// <summary>
/// Wall-mounted battery gauge above a carnival gate doorway, driven every frame by <see cref="CarnivalGateSystem"/>.
/// Each bar is a dark slot with a lit fill on top; the bar currently draining shrinks from the right, so the gauge
/// shows the drain continuously, not just whole-bar steps. A label under the bars reads OPEN, or SEALED with the
/// time left while the gates are closed.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalGateBatteryIndicator : MonoBehaviour
{
    [Tooltip("Lit bar fills, left (first to fill) to right (first to drain). Their scale is changed at runtime.")]
    [SerializeField] Renderer[] barFills;
    [SerializeField] TMP_Text statusLabel;

    [Header("Colours")]
    [SerializeField] Color highColor = new Color(0.25f, 1f, 0.35f);
    [SerializeField] Color midColor = new Color(1f, 0.72f, 0.12f);
    [SerializeField] Color lowColor = new Color(1f, 0.18f, 0.14f);
    [Tooltip("More than this many bars left shows the high colour.")]
    [SerializeField, Min(0f)] float highAboveBars = 3f;
    [Tooltip("More than this many bars left (and not high) shows the mid colour.")]
    [SerializeField, Min(0f)] float midAboveBars = 1f;

    static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");

    MaterialPropertyBlock _block;
    Vector3[] _fullScale;
    Vector3[] _fullPosition;
    string _lastLabel;
    Color _lastLabelColor;

    void Awake()
    {
        _block = new MaterialPropertyBlock();
        int n = barFills != null ? barFills.Length : 0;
        _fullScale = new Vector3[n];
        _fullPosition = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            if (barFills[i] == null)
                continue;
            _fullScale[i] = barFills[i].transform.localScale;
            _fullPosition[i] = barFills[i].transform.localPosition;
        }
    }

    /// <param name="charge01">Live battery charge.</param>
    /// <param name="closed">Gates closed (draining).</param>
    /// <param name="secondsLeft">Closed time the charge still buys.</param>
    /// <param name="canClose">Enough charge to close the gates (shown as a warning while open).</param>
    /// <param name="blinkOn">Shared blink phase so both gauges flash together.</param>
    public void Show(float charge01, bool closed, float secondsLeft, bool canClose, bool blinkOn)
    {
        if (barFills == null || _fullScale == null)
            return;

        int n = barFills.Length;
        float barsLeft = Mathf.Clamp01(charge01) * n;
        Color color = barsLeft > highAboveBars ? highColor : barsLeft > midAboveBars ? midColor : lowColor;

        // The last bar flashes while the gates are closed on it.
        bool hide = closed && barsLeft <= 1f && !blinkOn;

        for (int i = 0; i < n; i++)
        {
            Renderer fill = barFills[i];
            if (fill == null)
                continue;

            // Hidden bars collapse to zero scale rather than toggling Renderer.enabled, which WorldRenderCuller owns.
            float f = hide ? 0f : Mathf.Clamp01(barsLeft - i);
            Transform t = fill.transform;
            if (f <= 0.001f)
            {
                t.localScale = Vector3.zero;
                continue;
            }

            // Shrink toward the bar's left edge along local X.
            Vector3 s = _fullScale[i];
            t.localScale = new Vector3(s.x * f, s.y, s.z);
            t.localPosition = _fullPosition[i] - new Vector3(s.x * (1f - f) * 0.5f, 0f, 0f);

            fill.GetPropertyBlock(_block);
            _block.SetColor(s_baseColorId, color);
            fill.SetPropertyBlock(_block);
        }

        if (statusLabel == null)
            return;

        string label;
        Color labelColor;
        if (closed)
        {
            int whole = Mathf.CeilToInt(Mathf.Max(0f, secondsLeft));
            label = $"SEALED {whole / 60}:{whole % 60:00}";
            labelColor = color;
        }
        else if (canClose)
        {
            label = "OPEN";
            labelColor = Color.white;
        }
        else if (charge01 <= 0.0001f)
        {
            label = "BATTERY DEAD";
            labelColor = blinkOn ? lowColor : Color.white;
        }
        else
        {
            // Some charge left, but less than one press costs: still can't close.
            label = "LOW BATTERY";
            labelColor = blinkOn ? lowColor : Color.white;
        }

        if (label != _lastLabel)
        {
            _lastLabel = label;
            statusLabel.text = label;
        }

        if (labelColor != _lastLabelColor)
        {
            _lastLabelColor = labelColor;
            statusLabel.color = labelColor;
        }
    }
}
