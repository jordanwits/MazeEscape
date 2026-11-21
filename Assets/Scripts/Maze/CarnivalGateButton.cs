using UnityEngine;

/// <summary>
/// One button on the carnival gate control panel (Close / Open / Recharge). The player aims at it and presses E
/// (<see cref="PlayerController"/>'s carnival interact path); the press and its prompt are answered by the
/// <see cref="CarnivalGateSystem"/> above it. The cap glows while a press would do something and sinks briefly when
/// pressed — local feedback only.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalGateButton : MonoBehaviour
{
    [SerializeField] CarnivalGateSystem system;
    [SerializeField] CarnivalGateSystem.GateAction action;

    [Header("Cap")]
    [SerializeField] Renderer capRenderer;
    [SerializeField] Color litColor = Color.white;
    [SerializeField, Range(0f, 1f)] float unlitBrightness = 0.3f;
    [Tooltip("Glow strength while lit. The cap material's emission map is its wear texture, so grime glows less.")]
    [SerializeField, Min(0f)] float litEmission = 0.9f;
    [SerializeField, Min(0f)] float unlitEmission = 0.03f;
    [Tooltip("How far the cap sinks (along its local -Y, the panel normal) when pressed.")]
    [SerializeField, Min(0f)] float pressDepth = 0.03f;
    [SerializeField, Min(0.01f)] float pressSeconds = 0.18f;

    static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int s_emissionColorId = Shader.PropertyToID("_EmissionColor");

    MaterialPropertyBlock _block;
    Vector3 _capRestPosition;
    float _pressTimer;
    int _lastLit = -1;

    public CarnivalGateSystem.GateAction Action => action;

    void Awake()
    {
        if (system == null)
            system = GetComponentInParent<CarnivalGateSystem>(true);
        _block = new MaterialPropertyBlock();
        if (capRenderer != null)
            _capRestPosition = capRenderer.transform.localPosition;
    }

    public string GetPrompt(PlayerController viewer)
    {
        return system != null ? system.GetPrompt(action, viewer) : string.Empty;
    }

    public void Press(PlayerController presser)
    {
        if (system == null)
            return;

        _pressTimer = pressSeconds;
        system.LocalPress(action, presser, transform.position);
    }

    void Update()
    {
        if (capRenderer == null)
            return;

        // Press dip: down then back up over pressSeconds.
        if (_pressTimer > 0f)
        {
            _pressTimer = Mathf.Max(0f, _pressTimer - Time.deltaTime);
            float u = 1f - _pressTimer / pressSeconds;
            float dip = Mathf.Sin(u * Mathf.PI) * pressDepth;
            capRenderer.transform.localPosition = _capRestPosition - capRenderer.transform.localRotation * Vector3.up * dip;
        }

        int lit = system != null && system.IsActionLit(action) ? 1 : 0;
        if (lit == _lastLit)
            return;

        _lastLit = lit;
        Color c = lit == 1 ? litColor : litColor * unlitBrightness;
        c.a = 1f;
        Color glow = litColor * (lit == 1 ? litEmission : unlitEmission);
        glow.a = 1f;
        capRenderer.GetPropertyBlock(_block);
        _block.SetColor(s_baseColorId, c);
        _block.SetColor(s_emissionColorId, glow);
        capRenderer.SetPropertyBlock(_block);
    }
}
