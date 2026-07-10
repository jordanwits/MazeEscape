using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Muffles positional (3D) audio sources when solid world geometry sits between the source and the local
/// player's AudioListener, so a zombie snarling or a teammate's voice on the far side of a wall reads as
/// dampened instead of full-bright.
///
/// How it works: every gameplay <see cref="AudioSource"/> is registered here from
/// <see cref="GameAudioManager"/>'s Route* choke points (SFX / voice / music), so all current and future
/// positional sources are covered with no per-call-site wiring. Each registered source gets an
/// <see cref="AudioLowPassFilter"/>. In LateUpdate we linecast from the active listener to each source against
/// a solid-geometry mask (walls, doors, props — everything except characters and non-solid layers); the number
/// of occluders drives a target "occlusion" 0..1 which is smoothed and mapped (log-scaled) onto the filter's
/// cutoff frequency. Clear line-of-sight = fully open (transparent); one or more walls = muffled.
///
/// Deliberately muffle-ONLY: we never touch <c>source.volume</c>, because many emitters (Clown voice, footsteps,
/// impacts) drive their own volume per-clip and we must not fight them. A low-pass at a few hundred Hz already
/// reads clearly as "behind a wall" and reduces perceived loudness on its own.
///
/// Self-contained: the singleton is created on first <see cref="Register"/> and marked DontDestroyOnLoad, so it
/// survives level changes and simply prunes sources whose GameObjects were destroyed on scene unload.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)] // run after players/enemies have moved this frame so positions are current
public sealed class AudioOcclusionManager : MonoBehaviour
{
    static AudioOcclusionManager _instance;

    /// <summary>Global kill switch (e.g. an accessibility / low-spec toggle). When false, all filters relax open.</summary>
    public static bool Enabled { get; set; } = true;

    [Header("Muffling")]
    [Tooltip("Low-pass cutoff (Hz) with a clear line of sight — 22000 is fully transparent.")]
    [SerializeField, Range(2000f, 22000f)] float openCutoffHz = 22000f;
    [Tooltip("Low-pass cutoff (Hz) when fully occluded — lower = more muffled / more 'behind a wall'.")]
    [SerializeField, Range(200f, 4000f)] float occludedCutoffHz = 850f;

    [Header("Occlusion strength")]
    [Tooltip("How occluded a single wall makes a source (0..1). Higher = a single wall already muffles hard.")]
    [SerializeField, Range(0f, 1f)] float singleOccluderStrength = 0.72f;
    [Tooltip("Number of walls between listener and source that counts as fully occluded.")]
    [SerializeField, Range(1, 4)] int occludersForFull = 2;

    [Header("Response")]
    [Tooltip("Seconds to ease between occlusion states, so rounding a corner fades rather than pops.")]
    [SerializeField, Min(0.01f)] float smoothingTime = 0.12f;
    [Tooltip("Seconds between line-of-sight raycasts per source (positions interpolate in between). Casts are staggered across sources.")]
    [SerializeField, Min(0.02f)] float evalInterval = 0.08f;
    [Tooltip("Trims this much off each end of the ray so geometry hugging the listener or the source isn't miscounted as a wall.")]
    [SerializeField, Min(0f)] float raySkin = 0.3f;

    LayerMask _solidMask;
    bool _maskBuilt;
    AudioListener _listener;
    float _nextListenerScan;

    readonly List<Occludable> _items = new();
    readonly HashSet<AudioSource> _known = new(); // sources already registered (dedupe)
    static readonly RaycastHit[] _hitBuffer = new RaycastHit[8];

    sealed class Occludable
    {
        public AudioSource Source;
        public AudioLowPassFilter Filter;
        public float Current;  // smoothed occlusion 0..1
        public float Target;
        public float NextEval; // Time.time of the next raycast
    }

    /// <summary>
    /// Registers a source for wall-occlusion muffling. Safe to call repeatedly on the same source (deduped) and
    /// safe for 2D sources (they simply stay transparent — occlusion is gated on spatialBlend at tick time).
    /// No-op outside play mode.
    /// </summary>
    public static void Register(AudioSource source)
    {
        if (source == null || !Application.isPlaying)
            return;

        EnsureInstance();
        _instance.Add(source);
    }

    static void EnsureInstance()
    {
        if (_instance != null)
            return;

        var go = new GameObject("AudioOcclusionManager");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<AudioOcclusionManager>();
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(this);
            return;
        }

        _instance = this;
        BuildSolidMask();
    }

    void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    void Add(AudioSource source)
    {
        if (!_known.Add(source))
            return;

        _items.Add(new Occludable { Source = source, NextEval = Time.time + Random.value * evalInterval });
    }

    /// <summary>
    /// Solid geometry that muffles sound: everything except the character layers (so bodies never occlude) and
    /// non-physical layers (triggers/UI/water/FX). Mirrors <c>RagdollCameraCollision.BuildSolidMask</c>.
    /// </summary>
    void BuildSolidMask()
    {
        int mask = ~0;
        string[] excluded =
        {
            "Player", "Enemy", "Jailor", "Clown", "MinigameBarrier",
            "Ignore Raycast", "UI", "TransparentFX", "Water",
        };
        foreach (string n in excluded)
        {
            int l = LayerMask.NameToLayer(n);
            if (l >= 0)
                mask &= ~(1 << l);
        }
        _solidMask = mask;
        _maskBuilt = true;
    }

    void LateUpdate()
    {
        if (!_maskBuilt)
            BuildSolidMask();

        float now = Time.time;
        float dt = Time.deltaTime;
        float smooth = smoothingTime > 0.0001f ? 1f - Mathf.Exp(-dt / smoothingTime) : 1f;

        bool active = Enabled && ResolveListener(now);
        Vector3 listenerPos = active ? _listener.transform.position : Vector3.zero;

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            Occludable item = _items[i];
            AudioSource source = item.Source;

            if (source == null)
            {
                if (item.Filter != null) // source component gone but filter host might survive
                    item.Filter.cutoffFrequency = openCutoffHz;
                _known.Remove(source); // drop the dead reference so the set doesn't grow across level loads
                _items.RemoveAt(i);
                continue;
            }

            // Disabled globally, no listener, or a 2D source: relax toward open and skip the raycast.
            if (!active || source.spatialBlend <= 0.01f)
            {
                item.Target = 0f;
                item.Current = Mathf.Lerp(item.Current, 0f, smooth);
                ApplyCutoff(item);
                continue;
            }

            Vector3 toListener = listenerPos - source.transform.position;
            float dist = toListener.magnitude;

            // Beyond audible range: inaudible anyway, so don't spend a raycast — just relax open.
            if (dist > source.maxDistance * 1.05f)
            {
                item.Target = 0f;
                item.Current = Mathf.Lerp(item.Current, 0f, smooth);
                ApplyCutoff(item);
                continue;
            }

            if (now >= item.NextEval)
            {
                item.Target = EvaluateOcclusion(listenerPos, source.transform, dist);
                item.NextEval = now + evalInterval;
            }

            item.Current = Mathf.Lerp(item.Current, item.Target, smooth);
            ApplyCutoff(item);
        }
    }

    /// <summary>Counts solid colliders between listener and source (ray trimmed at both ends) and maps that to 0..1.</summary>
    float EvaluateOcclusion(Vector3 listenerPos, Transform sourceTransform, float dist)
    {
        if (dist <= raySkin * 2f + 0.05f)
            return 0f; // basically on top of each other — nothing can be between them

        Vector3 sourcePos = sourceTransform.position;
        Vector3 dir = (sourcePos - listenerPos) / dist;
        Vector3 origin = listenerPos + dir * raySkin;
        float castDist = dist - raySkin * 2f;

        int hits = Physics.RaycastNonAlloc(origin, dir, _hitBuffer, castDist, _solidMask, QueryTriggerInteraction.Ignore);
        if (hits <= 0)
            return 0f;

        // Discard hits on the source's own hierarchy: an enemy's capsule (or the client-side collision proxy)
        // can sit on the Default layer right at its own voice source, which would otherwise read as a permanent
        // self-occluder and muffle it in clear line of sight. Same defense the camera-collision code uses.
        Transform sourceRoot = sourceTransform.root;
        int occluders = 0;
        for (int i = 0; i < hits; i++)
        {
            if (_hitBuffer[i].transform.root != sourceRoot)
                occluders++;
        }

        if (occluders <= 0)
            return 0f;
        if (occluders == 1)
            return singleOccluderStrength;

        float t = Mathf.Clamp01((occluders - 1f) / Mathf.Max(1, occludersForFull - 1));
        return Mathf.Lerp(singleOccluderStrength, 1f, t);
    }

    void ApplyCutoff(Occludable item)
    {
        if (item.Filter == null)
        {
            // Lazily created: a fresh AudioLowPassFilter defaults to a dulling ~5 kHz cutoff, so open it immediately.
            item.Filter = item.Source.GetComponent<AudioLowPassFilter>();
            if (item.Filter == null)
                item.Filter = item.Source.gameObject.AddComponent<AudioLowPassFilter>();
        }

        // Interpolate in log space so the sweep sounds perceptually even.
        float cutoff = Mathf.Exp(Mathf.Lerp(Mathf.Log(openCutoffHz), Mathf.Log(occludedCutoffHz), Mathf.Clamp01(item.Current)));
        item.Filter.cutoffFrequency = Mathf.Clamp(cutoff, 10f, 22000f);
    }

    /// <summary>Caches the currently-enabled AudioListener (the local player's), re-scanning only when it goes stale.</summary>
    bool ResolveListener(float now)
    {
        if (_listener != null && _listener.isActiveAndEnabled)
            return true;

        if (now < _nextListenerScan)
            return false;
        _nextListenerScan = now + 0.5f;

        _listener = null;
        AudioListener[] listeners = FindObjectsByType<AudioListener>();
        foreach (AudioListener l in listeners)
        {
            if (l != null && l.isActiveAndEnabled)
            {
                _listener = l;
                break;
            }
        }
        return _listener != null;
    }
}
