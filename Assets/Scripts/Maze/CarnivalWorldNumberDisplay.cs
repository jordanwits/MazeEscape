using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Drives a world-space label from a <see cref="BasketballGameController"/>'s replicated state.
/// Supports either a 3D <see cref="TextMesh"/> (no canvas needed) or a <see cref="Text"/> placed
/// on a world-space Canvas — whichever is wired up. The display runs purely on read-side
/// NetworkVariable values, so it works the same on every client without any RPC.
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]
public sealed class CarnivalWorldNumberDisplay : MonoBehaviour
{
    public enum DisplayMode { Timer, Score }

    [SerializeField] BasketballGameController controller;
    [SerializeField] DisplayMode mode = DisplayMode.Timer;

    [Header("Targets (wire either one)")]
    [SerializeField] TextMesh worldTextMesh;
    [SerializeField] Text uiText;

    [Header("Format")]
    [SerializeField] string idlePlaceholder = "--";
    [Tooltip("C# numeric format for the timer (seconds). Default '00' pads to two digits.")]
    [SerializeField] string timerFormat = "00";
    [Tooltip("C# numeric format for the score.")]
    [SerializeField] string scoreFormat = "0";

    string _lastText;

    void Reset()
    {
        controller = GetComponentInParent<BasketballGameController>();
        worldTextMesh = GetComponentInChildren<TextMesh>();
        uiText = GetComponentInChildren<Text>();
    }

    void Awake()
    {
        SyncRefs();
        ForceValidFont();
    }

    void OnEnable()
    {
        SyncRefs();
        ForceValidFont();
    }

    void SyncRefs()
    {
        if (controller == null)
            controller = GetComponentInParent<BasketballGameController>(true);
        if (worldTextMesh == null)
            worldTextMesh = GetComponentInChildren<TextMesh>(true);
        if (uiText == null)
            uiText = GetComponentInChildren<Text>(true);
    }

    /// <summary>
    /// Built-in TextMesh assignments from YAML can point at stale fileIDs whose font texture isn't bound
    /// in modern Unity, leaving each character as a blank quad. Re-bind LegacyRuntime.ttf so the glyphs
    /// render correctly. Matches PlayerController's existing pattern.
    /// </summary>
    void ForceValidFont()
    {
        if (worldTextMesh == null)
            return;

        Font runtimeFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (runtimeFont == null)
            return;

        if (worldTextMesh.font != runtimeFont)
            worldTextMesh.font = runtimeFont;

        MeshRenderer mr = worldTextMesh.GetComponent<MeshRenderer>();
        if (mr != null && runtimeFont.material != null && mr.sharedMaterial != runtimeFont.material)
            mr.sharedMaterial = runtimeFont.material;
    }

    void Update()
    {
        if (worldTextMesh != null && worldTextMesh.font == null)
            ForceValidFont();

        string next = BuildText();
        if (next == _lastText)
            return;

        _lastText = next;
        if (worldTextMesh != null)
            worldTextMesh.text = next;
        if (uiText != null)
            uiText.text = next;
    }

    string BuildText()
    {
        if (controller == null)
            return idlePlaceholder;

        if (mode == DisplayMode.Timer)
        {
            if (!controller.IsActive)
                return idlePlaceholder;
            int seconds = Mathf.CeilToInt(Mathf.Max(0f, controller.TimeRemaining));
            return seconds.ToString(timerFormat);
        }

        // Score: show live during round; otherwise show last finished score (or placeholder if none yet).
        if (controller.IsActive)
            return controller.Score.ToString(scoreFormat);
        int last = controller.LastFinishedScore;
        return last > 0 ? last.ToString(scoreFormat) : idlePlaceholder;
    }
}
