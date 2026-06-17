using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives a string of point lights (a rope light) with an irregular, "bad power connection"
/// flicker: mostly lit, with random dropouts and the occasional rapid stutter burst before it
/// recovers. The pattern is deliberately non-rhythmic so it reads as a faulty electrical
/// connection rather than an animated effect.
///
/// Purely cosmetic, so it runs locally on every client (no networking) — the flicker does not
/// need to be identical across machines. Attach to the RopeLight root; by default it gathers the
/// Light components on this object and its children and flickers them together as one fixture.
/// </summary>
[DisallowMultipleComponent]
public class RopeLightFlicker : MonoBehaviour
{
    [Header("Lights")]
    [Tooltip("Include Light components found on child objects (the rope's individual bulbs). Leave on for a normal rope light.")]
    [SerializeField] bool includeChildLights = true;
    [Tooltip("Optional explicit list. When non-empty, only these lights flicker and auto-gathering is skipped.")]
    [SerializeField] List<Light> lightsOverride = new List<Light>();

    [Header("Lit / Out levels")]
    [Tooltip("Intensity multiplier applied to each light's original intensity while lit (1 = normal).")]
    [SerializeField, Range(0f, 1f)] float onLevel = 1f;
    [Tooltip("Intensity multiplier while the connection has dropped out. 0 = fully off. A small value (e.g. 0.05) gives a dim brown-out instead of a hard cut.")]
    [SerializeField, Range(0f, 1f)] float offLevel;

    [Header("Timing (seconds)")]
    [Tooltip("How long the rope stays steadily lit between glitches. Picked randomly each cycle for a non-rhythmic feel.")]
    [SerializeField] float minOnTime = 0.12f;
    [SerializeField] float maxOnTime = 2.2f;
    [Tooltip("How long a single dropout lasts when the connection cuts out.")]
    [SerializeField] float minOffTime = 0.04f;
    [SerializeField] float maxOffTime = 0.3f;

    [Header("Stutter bursts")]
    [Tooltip("Chance (0-1) that a glitch is a rapid multi-blink stutter instead of a single clean dropout.")]
    [SerializeField, Range(0f, 1f)] float stutterChance = 0.4f;
    [Tooltip("Maximum number of rapid on/off blinks in a stutter burst (a random count from 2 up to this is used).")]
    [SerializeField] int maxStutterBlinks = 5;
    [Tooltip("Duration range for each individual blink within a stutter burst.")]
    [SerializeField] float minStutterBlink = 0.02f;
    [SerializeField] float maxStutterBlink = 0.09f;

    readonly List<Light> _lights = new List<Light>();
    readonly List<float> _baseIntensities = new List<float>();
    Coroutine _routine;

    void Awake()
    {
        GatherLights();
    }

    void OnEnable()
    {
        if (_lights.Count == 0)
            return;
        _routine = StartCoroutine(FlickerLoop());
    }

    void OnDisable()
    {
        if (_routine != null)
        {
            StopCoroutine(_routine);
            _routine = null;
        }
        // Leave the rope in its normal lit state when the effect is switched off.
        SetLevel(onLevel);
    }

    void GatherLights()
    {
        _lights.Clear();
        _baseIntensities.Clear();

        if (lightsOverride != null && lightsOverride.Count > 0)
        {
            foreach (Light l in lightsOverride)
                if (l != null)
                    _lights.Add(l);
        }
        else if (includeChildLights)
        {
            // includeInactive: false — only lights that start enabled participate.
            GetComponentsInChildren(false, _lights);
        }
        else
        {
            Light self = GetComponent<Light>();
            if (self != null)
                _lights.Add(self);
        }

        foreach (Light l in _lights)
            _baseIntensities.Add(l.intensity);
    }

    IEnumerator FlickerLoop()
    {
        // Random initial offset so multiple rope lights in a level don't glitch in lock-step.
        yield return new WaitForSeconds(Random.Range(0f, maxOnTime));

        while (true)
        {
            // Steady, lit stretch.
            SetLevel(onLevel);
            yield return new WaitForSeconds(Random.Range(minOnTime, maxOnTime));

            // A glitch: either a single dropout or a rapid stutter burst.
            if (Random.value < stutterChance)
            {
                int blinks = Random.Range(2, Mathf.Max(3, maxStutterBlinks + 1));
                for (int i = 0; i < blinks; i++)
                {
                    SetLevel(offLevel);
                    yield return new WaitForSeconds(Random.Range(minStutterBlink, maxStutterBlink));
                    SetLevel(onLevel);
                    yield return new WaitForSeconds(Random.Range(minStutterBlink, maxStutterBlink));
                }
            }
            else
            {
                SetLevel(offLevel);
                yield return new WaitForSeconds(Random.Range(minOffTime, maxOffTime));
            }
        }
    }

    void SetLevel(float level)
    {
        bool lit = level > 0f;
        for (int i = 0; i < _lights.Count; i++)
        {
            Light l = _lights[i];
            if (l == null)
                continue;
            l.intensity = _baseIntensities[i] * level;
            // Fully disabling a zero-intensity light lets the renderer skip it entirely.
            l.enabled = lit;
        }
    }
}
