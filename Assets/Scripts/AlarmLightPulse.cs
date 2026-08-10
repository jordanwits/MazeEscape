using UnityEngine;

/// <summary>
/// Drives an alarm fixture: a red point light that swells and fades on a slow, steady beat, with
/// the emissive bulb glowing in lock-step so the fixture itself reads as the source of the light.
/// The waveform is a raised cosine raised to <see cref="flashSharpness"/> — a soft ramp up to a
/// bright peak and a long dim trough, the way a rotating-beacon alarm sweeps past.
///
/// Purely cosmetic, so it runs locally on every client with no networking. The phase is taken from
/// <see cref="Time.time"/>, so every alarm light in the level pulses together (and peers stay
/// roughly in step for free); give an individual fixture a <see cref="phaseOffset"/> if you want it
/// to lag behind the rest.
///
/// Plays nicely with <see cref="MazeLightCuller"/>: this only ever writes <c>Light.intensity</c>,
/// never <c>Light.enabled</c>, so the culler still switches the fixture off at distance.
/// Disabling this component leaves the fixture dark (the alarm is not running).
/// </summary>
[DisallowMultipleComponent]
public class AlarmLightPulse : MonoBehaviour
{
    [Header("Fixture")]
    [Tooltip("The point light to pulse. Leave empty to use the first Light found on this object or its children.")]
    [SerializeField] Light alarmLight;
    [Tooltip("Emissive bulb renderer, brightened in sync with the light. Leave empty to auto-find a renderer named 'Bulb' in the children. Optional — leave unassigned for a light-only fixture.")]
    [SerializeField] Renderer bulbRenderer;

    [Header("Colour")]
    [Tooltip("Alarm colour, applied to both the light and the bulb's emission.")]
    [SerializeField] Color alarmColor = new Color(1f, 0.06f, 0.03f, 1f);

    [Header("Light levels")]
    [Tooltip("Light intensity at the top of the pulse.")]
    [SerializeField, Min(0f)] float peakIntensity = 3f;
    [Tooltip("Light intensity in the trough. A small value keeps a faint red ember between flashes; 0 goes fully dark.")]
    [SerializeField, Min(0f)] float minIntensity = 0.08f;

    [Header("Bulb emission")]
    [Tooltip("Emission multiplier at the top of the pulse. Above ~1 the bulb starts to bloom.")]
    [SerializeField, Min(0f)] float peakEmission = 4f;
    [Tooltip("Emission multiplier in the trough.")]
    [SerializeField, Min(0f)] float minEmission = 0.15f;

    [Header("Timing")]
    [Tooltip("Seconds for one full flash cycle (dark to bright and back). Larger = slower alarm.")]
    [SerializeField, Min(0.05f)] float period = 2.2f;
    [Tooltip("Shape of the flash. 1 = an even throb; higher values give a short bright pulse with a long dim gap between flashes.")]
    [SerializeField, Range(0.25f, 8f)] float flashSharpness = 2.5f;
    [Tooltip("Fraction of a cycle (0-1) this fixture lags behind the shared clock. Leave at 0 to pulse in step with every other alarm light.")]
    [SerializeField, Range(0f, 1f)] float phaseOffset;

    static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    MaterialPropertyBlock _mpb;

    void Awake()
    {
        ResolveFixture();

        if (alarmLight != null)
            alarmLight.color = alarmColor;
    }

    void OnEnable()
    {
        Apply(Wave());
    }

    void Update()
    {
        Apply(Wave());
    }

    void OnDisable()
    {
        // Alarm off: the fixture goes dark rather than freezing mid-flash.
        Apply(0f);
    }

    void ResolveFixture()
    {
        if (alarmLight == null)
            alarmLight = GetComponentInChildren<Light>(true);

        if (bulbRenderer == null)
        {
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name == "Bulb")
                {
                    bulbRenderer = r;
                    break;
                }
            }
        }
    }

    /// <summary>Current pulse level, 0 (trough) to 1 (peak).</summary>
    float Wave()
    {
        float cycles = Time.time / Mathf.Max(0.05f, period) - phaseOffset;
        float phase = cycles - Mathf.Floor(cycles);
        float raisedCosine = 0.5f - 0.5f * Mathf.Cos(phase * Mathf.PI * 2f);
        return Mathf.Pow(raisedCosine, flashSharpness);
    }

    void Apply(float level)
    {
        if (alarmLight != null)
            alarmLight.intensity = Mathf.Lerp(minIntensity, peakIntensity, level);

        if (bulbRenderer == null)
            return;

        // A property block keeps the pulse on this fixture instead of editing the shared
        // ElevatorButton material (also used by the elevator call pads).
        _mpb ??= new MaterialPropertyBlock();
        bulbRenderer.GetPropertyBlock(_mpb);
        // .linear because _EmissionColor is consumed in linear space, while the inspector colour is authored in gamma.
        _mpb.SetColor(EmissionColorId, alarmColor.linear * Mathf.Lerp(minEmission, peakEmission, level));
        bulbRenderer.SetPropertyBlock(_mpb);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (minIntensity > peakIntensity)
            minIntensity = peakIntensity;
        if (minEmission > peakEmission)
            minEmission = peakEmission;
    }
#endif
}
