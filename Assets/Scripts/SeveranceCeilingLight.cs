using UnityEngine;

/// <summary>
/// One Severance ceiling fixture: the emissive <c>LightPanel</c> quad and the realtime spot light
/// that actually illuminates the cell, kept in sync as a single unit.
///
/// The panel material is <c>Universal Render Pipeline/Unlit</c> with an over-1 base colour, so the
/// quad renders at a fixed blazing white no matter what the lighting does. On its own that is fine
/// while every fixture is lit, but the moment a light flickers or dies the panel would keep glowing
/// and give the whole thing away. This component closes that gap: it mirrors the light's current
/// output onto the panel through a <see cref="MaterialPropertyBlock"/>, so a light that drops out
/// takes its panel dark with it.
///
/// **It deliberately tracks <c>Light.intensity</c> and NOT <c>Light.enabled</c>.** Those two flags
/// mean different things here. <see cref="MazeLightCuller"/> switches distant lights off with
/// <c>enabled</c> purely to save shading cost — a panel you can see from across the level must stay
/// lit even though its light is culled. Flicker effects scale <c>intensity</c> instead (see
/// <see cref="RopeLightFlicker.SetLevel"/>), which is the signal that the fixture itself is failing.
/// Reading intensity therefore follows the fixture and ignores the culler, which is exactly right.
///
/// Purely cosmetic and entirely local — no networking. Add <see cref="RopeLightFlicker"/> alongside
/// this component to make a fixture flicker or die; it gathers the child light automatically and
/// the panel follows for free.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshRenderer))]
public class SeveranceCeilingLight : MonoBehaviour
{
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    [Tooltip("The realtime light for this fixture. Left empty, the first Light in this object's children is used.")]
    [SerializeField] Light fixtureLight;

    [Tooltip("Panel brightness when the light is at its authored intensity. 1 = the material's authored colour.")]
    [SerializeField, Min(0f)] float litPanelLevel = 1f;

    [Tooltip("Panel brightness floor. Above 0 the glass keeps a faint grey sheen when the tube is dead, " +
             "which reads more like a real fixture than a pure black rectangle.")]
    [SerializeField, Range(0f, 1f)] float deadPanelLevel = 0.02f;

    Renderer _panel;
    MaterialPropertyBlock _mpb;

    // A fixture is more than one light: the shadow-casting spot that lights the room, plus an
    // unshadowed wash aimed at the ceiling. They must die together, so SetOutput drives all of them.
    readonly System.Collections.Generic.List<Light> _lights = new System.Collections.Generic.List<Light>();
    readonly System.Collections.Generic.List<float> _baseIntensities = new System.Collections.Generic.List<float>();

    Color _baseColor = Color.white;
    Color _emissionColor = Color.black;
    float _referenceIntensity = 1f;
    float _lastApplied = -1f;

    /// <summary>Current output 0-1, where 1 is the fixture's authored intensity.</summary>
    public float Output { get; private set; } = 1f;

    void Awake()
    {
        _panel = GetComponent<Renderer>();

        if (fixtureLight == null)
            fixtureLight = GetComponentInChildren<Light>(true);

        // Capture the authored look once. sharedMaterial (not material) so this never instantiates a
        // per-renderer material copy — the MaterialPropertyBlock is what makes each panel independent.
        Material source = _panel != null ? _panel.sharedMaterial : null;
        if (source != null)
        {
            if (source.HasProperty(BaseColorId))
                _baseColor = source.GetColor(BaseColorId);
            if (source.HasProperty(EmissionColorId))
                _emissionColor = source.GetColor(EmissionColorId);
        }

        // The intensity the fixture was authored at is the "fully lit" reference every later reading
        // is measured against, so a flicker driver scaling intensity maps straight onto panel level.
        if (fixtureLight != null && fixtureLight.intensity > 0f)
            _referenceIntensity = fixtureLight.intensity;

        GetComponentsInChildren(true, _lights);
        for (int i = 0; i < _lights.Count; i++)
            _baseIntensities.Add(_lights[i].intensity);

        _mpb = new MaterialPropertyBlock();
        ApplyPanelLevel(litPanelLevel);
    }

    void LateUpdate()
    {
        if (fixtureLight == null)
            return;

        // Runs after any flicker coroutine has set intensity for this frame, so the panel never
        // trails the bulb by a frame. Only a float compare in the steady state — the property block
        // is pushed on actual change, not every frame.
        Output = Mathf.Clamp01(fixtureLight.intensity / _referenceIntensity);
        ApplyPanelLevel(Mathf.Lerp(deadPanelLevel, litPanelLevel, Output));
    }

    void ApplyPanelLevel(float level)
    {
        if (_panel == null || _mpb == null)
            return;
        if (Mathf.Abs(level - _lastApplied) < 0.001f)
            return;

        _lastApplied = level;
        _panel.GetPropertyBlock(_mpb);
        _mpb.SetColor(BaseColorId, _baseColor * level);
        _mpb.SetColor(EmissionColorId, _emissionColor * level);
        _panel.SetPropertyBlock(_mpb);
    }

    /// <summary>
    /// Force the fixture to a given output 0-1, driving both the bulb and the panel. Provided for
    /// scripted one-offs (a light that dies when the player walks under it); ambient flicker is
    /// better handled by adding a <see cref="RopeLightFlicker"/>, which this component follows.
    /// </summary>
    public void SetOutput(float output)
    {
        output = Mathf.Clamp01(output);
        Output = output;

        for (int i = 0; i < _lights.Count; i++)
        {
            Light l = _lights[i];
            if (l == null)
                continue;
            l.intensity = _baseIntensities[i] * output;
            // A zero-intensity light still costs the renderer a slot; drop it entirely when dead.
            l.enabled = output > 0f;
        }

        ApplyPanelLevel(Mathf.Lerp(deadPanelLevel, litPanelLevel, output));
    }
}
