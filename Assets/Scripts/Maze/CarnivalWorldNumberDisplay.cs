using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// Drives a world-space label from a carnival controller's replicated state
/// (any <see cref="ICarnivalScoreSource"/>, e.g. <see cref="BasketballGameController"/> or
/// <see cref="RingTossGameController"/>).
/// Supports a TextMeshPro label (<see cref="TMP_Text"/> — covers both the 3D
/// <c>TextMeshPro</c> and the canvas <c>TextMeshProUGUI</c> components), a legacy 3D
/// <see cref="TextMesh"/> (no canvas needed), or a UGUI <see cref="Text"/> on a world-space
/// Canvas — whichever is wired up. The display runs purely on read-side NetworkVariable values,
/// so it works the same on every client without any RPC.
/// </summary>
[DisallowMultipleComponent]
[ExecuteAlways]
public sealed class CarnivalWorldNumberDisplay : MonoBehaviour
{
    public enum DisplayMode { Timer, Score }

    [FormerlySerializedAs("controller")]
    [SerializeField, Tooltip("Any carnival controller implementing ICarnivalScoreSource (BasketballGameController, RingTossGameController). Auto-resolved from a parent if left empty.")]
    MonoBehaviour controllerHost;
    [SerializeField] DisplayMode mode = DisplayMode.Timer;

    ICarnivalScoreSource _source;

    [Header("Targets (wire whichever applies)")]
    [SerializeField] TMP_Text tmpText;
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
        controllerHost = GetComponentInParent<ICarnivalScoreSource>(true) as MonoBehaviour;
        tmpText = GetComponentInChildren<TMP_Text>();
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
        _source = controllerHost as ICarnivalScoreSource;
        if (_source == null)
        {
            _source = GetComponentInParent<ICarnivalScoreSource>(true);
            controllerHost = _source as MonoBehaviour;
        }
        if (tmpText == null)
            tmpText = GetComponentInChildren<TMP_Text>(true);
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
        if (tmpText != null)
            tmpText.text = next;
        if (worldTextMesh != null)
            worldTextMesh.text = next;
        if (uiText != null)
            uiText.text = next;
    }

    string BuildText()
    {
        ICarnivalScoreSource source = _source ?? (controllerHost as ICarnivalScoreSource);
        if (source == null)
            return idlePlaceholder;

        if (mode == DisplayMode.Timer)
        {
            if (!source.IsActive)
                return idlePlaceholder;
            int seconds = Mathf.CeilToInt(Mathf.Max(0f, source.TimeRemaining));
            return seconds.ToString(timerFormat);
        }

        // Score: show live during round; otherwise show last finished score (or placeholder if none yet).
        if (source.IsActive)
            return source.Score.ToString(scoreFormat);
        int last = source.LastFinishedScore;
        return last > 0 ? last.ToString(scoreFormat) : idlePlaceholder;
    }
}
