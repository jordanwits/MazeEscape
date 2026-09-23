using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The Mutant's hold over the lights, run LOCALLY on every peer (one hidden instance per scene):
///   • proximity flicker around every awake Mutant,
///   • the Lights Out blackout that hides his teleport, and the wake-up stutter,
///   • the shriek's permanent blow-outs.
///
/// Works purely as an intensity MULTIPLIER on top of whatever normally drives each light (the fixture's own
/// authored value, <see cref="RopeLightFlicker"/>'s faults, the disco floor, <see cref="AlarmLightPulse"/>): it
/// remembers the last value the light's own driver wrote and scales that, so nothing it touches loses its own
/// behaviour, and releasing restores exactly what the driver wants. Never writes Light.enabled while dimming
/// (the floor stays above 0) so <c>MazeLightCuller</c> never fights it, and never touches fog or ambient.
/// Runs at order -50: after every Update writer, before <see cref="SeveranceCeilingLight"/> mirrors the spot's
/// intensity onto its glowing panel — so the panel dims in the same frame.
///
/// Blow-outs pick fixtures by distance from a centre the server broadcasts, so every peer kills the same
/// ones (maze fixtures sit at identical positions on every peer), and nothing within reach of an exit
/// elevator is ever killed.
/// </summary>
[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
public class MutantLightDisturbance : MonoBehaviour
{
    const float DimFloor = 0.002f;
    const float FlickerFloor = 0.03f;
    const float RescanSeconds = 2f;
    const float ElevatorSafeRadius = 8f;

    static MutantLightDisturbance s_Instance;

    sealed class Entry
    {
        public Light light;
        public AlarmLightPulse pulse;       // alarms are dimmed through the pulse so the bulb goes too
        public SeveranceCeilingLight fixture;
        public Object unitKey;              // spot + wash of one fixture share a key
        public float owner;
        public float written = float.NaN;
        public bool modulated;
        public float seed;
    }

    enum EventKind { Blackout, Stutter }

    struct LightEvent
    {
        public EventKind kind;
        public Vector3 a;
        public Vector3 b;
        public float radius;
        public float start;
        public float darkIn;
        public float hold;
        public float recover;
        public float End => start + darkIn + hold + recover;
    }

    readonly List<Entry> _entries = new();
    readonly Dictionary<Light, Entry> _byLight = new();
    readonly HashSet<Object> _deadUnits = new();
    readonly List<LightEvent> _events = new();
    readonly List<Vector3> _elevators = new();
    float _nextRescan;

    public static MutantLightDisturbance Ensure()
    {
        if (s_Instance != null)
            return s_Instance;

        // A plain scene object on purpose: it dies with the level (a new one comes with the next Mutant), and
        // HideFlags.DontSave would let it outlive play mode in the editor — one leaked instance per session.
        var go = new GameObject("MutantLightDisturbance");
        s_Instance = go.AddComponent<MutantLightDisturbance>();
        return s_Instance;
    }

    void OnDestroy()
    {
        ReleaseAll();
        if (s_Instance == this)
            s_Instance = null;
    }

    // ------------------------------------------------------------------
    // Events (called on every peer with identical, server-chosen parameters)
    // ------------------------------------------------------------------

    /// <summary>Everything within <paramref name="radius"/> of either point goes (near) black, holds, then stutters back.</summary>
    public static void Blackout(Vector3 a, Vector3 b, float radius, float darkIn, float hold, float recover)
    {
        Ensure()._events.Add(new LightEvent
        {
            kind = EventKind.Blackout, a = a, b = b, radius = radius, start = Time.time,
            darkIn = Mathf.Max(0.01f, darkIn), hold = Mathf.Max(0f, hold), recover = Mathf.Max(0.01f, recover)
        });
    }

    /// <summary>A hard, random stutter for <paramref name="seconds"/> — the lights failing when he wakes.</summary>
    public static void Stutter(Vector3 center, float radius, float seconds)
    {
        Ensure()._events.Add(new LightEvent
        {
            kind = EventKind.Stutter, a = center, b = center, radius = radius, start = Time.time,
            darkIn = 0.01f, hold = Mathf.Max(0.05f, seconds), recover = 0.01f
        });
    }

    /// <summary>
    /// Permanently kills up to <paramref name="maxCount"/> light fixtures nearest <paramref name="center"/>
    /// (within <paramref name="radius"/>), skipping anything near an exit elevator. Deterministic across peers.
    /// </summary>
    public static void BlowOut(Vector3 center, float radius, int maxCount)
    {
        MutantLightDisturbance d = Ensure();
        d.Rescan();

        var candidates = new List<(Object key, float dist, Vector3 pos)>();
        var seen = new HashSet<Object>();
        for (int i = 0; i < d._entries.Count; i++)
        {
            Entry e = d._entries[i];
            if (e.light == null || d._deadUnits.Contains(e.unitKey) || !seen.Add(e.unitKey))
                continue;

            Vector3 p = e.light.transform.position;
            float dist = Vector3.Distance(p, center);
            if (dist > radius || d.IsNearElevator(p))
                continue;

            candidates.Add((e.unitKey, dist, p));
        }

        // Distance first (rounded to a centimetre so float noise can't reorder peers), ties broken by
        // position — FindObjectsByType order differs between peers, positions don't.
        candidates.Sort((x, y) =>
        {
            int c = Mathf.RoundToInt(x.dist * 100f).CompareTo(Mathf.RoundToInt(y.dist * 100f));
            if (c != 0) return c;
            c = x.pos.x.CompareTo(y.pos.x);
            return c != 0 ? c : x.pos.z.CompareTo(y.pos.z);
        });
        for (int i = 0; i < candidates.Count && i < maxCount; i++)
            d.KillUnit(candidates[i].key);
    }

    // ------------------------------------------------------------------
    // Per-frame
    // ------------------------------------------------------------------

    void LateUpdate()
    {
        float now = Time.time;
        if (now >= _nextRescan)
            Rescan();

        for (int i = _events.Count - 1; i >= 0; i--)
            if (now > _events[i].End)
                _events.RemoveAt(i);

        IReadOnlyList<MutantAI> bosses = MutantAI.Registry;
        bool anyBossActive = false;
        for (int i = 0; i < bosses.Count; i++)
            if (bosses[i] != null && bosses[i].DisturbsLights)
                anyBossActive = true;

        for (int i = 0; i < _entries.Count; i++)
        {
            Entry e = _entries[i];
            if (e.light == null || _deadUnits.Contains(e.unitKey))
                continue;

            float m = 1f;
            if (anyBossActive || _events.Count > 0)
            {
                Vector3 p = e.light.transform.position;
                if (anyBossActive)
                    m = Mathf.Min(m, ProximityMultiplier(e, p, bosses, now));
                for (int k = 0; k < _events.Count; k++)
                    m = Mathf.Min(m, EventMultiplier(_events[k], e, p, now));
            }

            if (m < 0.999f)
                Modulate(e, m);
            else if (e.modulated)
                Release(e);
        }
    }

    float ProximityMultiplier(Entry e, Vector3 p, IReadOnlyList<MutantAI> bosses, float now)
    {
        float m = 1f;
        for (int i = 0; i < bosses.Count; i++)
        {
            MutantAI boss = bosses[i];
            if (boss == null || !boss.DisturbsLights)
                continue;

            float radius = boss.LightFlickerRadius;
            float d = Vector3.Distance(p, boss.transform.position + Vector3.up * 1.5f);
            if (d >= radius)
                continue;

            float ramp = 1f - d / radius;
            ramp = ramp * ramp * (3f - 2f * ramp);
            float speed = Mathf.Lerp(6f, 19f, ramp);
            float noise = Mathf.PerlinNoise(e.seed, now * speed);
            float depth = Mathf.Lerp(0.25f, 0.95f, ramp);
            float flick = Mathf.Lerp(1f - depth, 1f, noise);
            if (ramp > 0.35f && Mathf.PerlinNoise(e.seed + 17.3f, now * speed * 1.7f) < Mathf.Lerp(0.04f, 0.22f, ramp))
                flick *= 0.05f; // a hard drop-out now and then
            m = Mathf.Min(m, Mathf.Max(FlickerFloor, flick));
        }
        return m;
    }

    static float EventMultiplier(in LightEvent ev, Entry e, Vector3 p, float now)
    {
        float r2 = ev.radius * ev.radius;
        if ((p - ev.a).sqrMagnitude > r2 && (p - ev.b).sqrMagnitude > r2)
            return 1f;

        float t = now - ev.start;
        if (t < 0f)
            return 1f;

        if (ev.kind == EventKind.Stutter)
        {
            if (t > ev.hold)
                return 1f;
            float n = Mathf.PerlinNoise(e.seed * 3.1f, t * 24f);
            return n > 0.5f ? 1f : Mathf.Lerp(0.02f, 0.3f, n);
        }

        if (t < ev.darkIn)
        {
            // Dying lights stutter on the way down.
            float p01 = t / ev.darkIn;
            float n = Mathf.PerlinNoise(e.seed, t * 40f);
            return Mathf.Max(DimFloor, Mathf.Lerp(1f, DimFloor, p01) * (n > 0.3f ? 1f : 0.2f));
        }

        if (t < ev.darkIn + ev.hold)
            return DimFloor;

        float r = (t - ev.darkIn - ev.hold) / ev.recover;
        if (r >= 1f)
            return 1f;

        // Coming back like a bad ballast: ragged on/off that settles on.
        float flick = Mathf.PerlinNoise(e.seed + 5.7f, t * 30f);
        return flick < Mathf.Lerp(0.85f, 0.15f, r) ? Mathf.Lerp(DimFloor, 0.15f, r) : 1f;
    }

    void Modulate(Entry e, float m)
    {
        if (e.pulse != null)
        {
            // Alarm fixtures: dim through the pulse so the emissive bulb goes down with the light.
            e.pulse.ExternalLevel = m;
            e.modulated = true;
            return;
        }

        Light l = e.light;
        if (!e.modulated)
        {
            e.owner = l.intensity;
            e.modulated = true;
        }
        else if (!Mathf.Approximately(l.intensity, e.written))
            e.owner = l.intensity; // the light's own driver wrote since last frame

        l.intensity = e.owner * m;
        e.written = l.intensity;
    }

    void Release(Entry e)
    {
        e.modulated = false;
        e.written = float.NaN;

        if (e.pulse != null)
        {
            e.pulse.ExternalLevel = 1f;
            return;
        }

        if (e.light == null)
            return;

        e.light.intensity = e.owner;
        if (e.owner > 0f)
            e.light.enabled = true; // the culler re-culls a far one within 0.15 s
    }

    void ReleaseAll()
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].modulated && !_deadUnits.Contains(_entries[i].unitKey))
                Release(_entries[i]);
    }

    void KillUnit(Object key)
    {
        if (key == null || !_deadUnits.Add(key))
            return;

        for (int i = 0; i < _entries.Count; i++)
        {
            Entry e = _entries[i];
            if (e.unitKey != key)
                continue;

            e.modulated = false;
            if (e.pulse != null)
            {
                e.pulse.ExternalLevel = 0f;
                continue;
            }

            if (e.fixture != null)
                continue; // handled once below through the fixture's own API

            if (e.light != null)
            {
                e.light.intensity = 0f;
                e.light.enabled = false;
            }
        }

        if (key is SeveranceCeilingLight fixture)
        {
            // A faulty fixture's flicker relights it in OnDisable, so stop that FIRST, then kill.
            RopeLightFlicker flicker = fixture.GetComponent<RopeLightFlicker>();
            if (flicker != null)
                flicker.enabled = false;
            fixture.SetOutput(0f);
        }
    }

    // ------------------------------------------------------------------
    // Light discovery
    // ------------------------------------------------------------------

    void Rescan()
    {
        _nextRescan = Time.time + RescanSeconds;

        _elevators.Clear();
        ElevatorFinishController[] elevators = FindObjectsByType<ElevatorFinishController>(FindObjectsInactive.Exclude);
        for (int i = 0; i < elevators.Length; i++)
            _elevators.Add(elevators[i].transform.position);

        // Drop destroyed lights (the level was rebuilt or a flare burned out).
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].light != null)
                continue;
            _entries.RemoveAt(i);
        }
        var stale = new List<Light>();
        foreach (KeyValuePair<Light, Entry> pair in _byLight)
            if (pair.Key == null)
                stale.Add(pair.Key);
        for (int i = 0; i < stale.Count; i++)
            _byLight.Remove(stale[i]);

        Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude);
        for (int i = 0; i < lights.Length; i++)
        {
            Light l = lights[i];
            if (l == null || _byLight.ContainsKey(l) || l.type == LightType.Directional)
                continue;

            // Players' own lights (flashlights) and projectile flares are never his to take. A flashlight is an
            // item wherever it is: one lying on the floor at a rescan stays listed after it is picked up, where the
            // flicker kept dimming it, Release() switched it back on after its owner turned it off, and a shriek
            // could blow it out. A flashbang's burst is the players' weapon, going off right next to him.
            if (l.GetComponentInParent<PlayerController>() != null
                || l.GetComponentInParent<GrabbableInventoryItem>() != null
                || l.GetComponentInParent<FlareProjectile>() != null
                || l.GetComponentInParent<FlareBurnEffect>() != null
                || l.GetComponentInParent<FlashbangFlashFx>() != null)
                continue;

            var entry = new Entry
            {
                light = l,
                pulse = l.GetComponentInParent<AlarmLightPulse>(),
                fixture = l.GetComponentInParent<SeveranceCeilingLight>(),
                seed = Hash01(l.transform.position) * 100f
            };
            entry.unitKey = entry.fixture != null ? entry.fixture
                : entry.pulse != null ? entry.pulse
                : l;
            _entries.Add(entry);
            _byLight.Add(l, entry);
        }
    }

    bool IsNearElevator(Vector3 p)
    {
        for (int i = 0; i < _elevators.Count; i++)
            if ((_elevators[i] - p).sqrMagnitude < ElevatorSafeRadius * ElevatorSafeRadius)
                return true;
        return false;
    }

    static float Hash01(Vector3 p)
    {
        unchecked
        {
            int h = Mathf.RoundToInt(p.x * 100f) * 73856093 ^ Mathf.RoundToInt(p.y * 100f) * 19349663 ^ Mathf.RoundToInt(p.z * 100f) * 83492791;
            return (h & 0xFFFF) / 65535f;
        }
    }
}
