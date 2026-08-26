using UnityEngine;

/// <summary>
/// The Bomber's blast: a fireball, a rolling smoke cloud, embers and a light flash, spawned locally on
/// every peer at the detonation point by <see cref="BomberAI"/>. Cosmetic only — the damage is adjudicated
/// server-side before the RPC goes out, so a peer that never sees this took exactly the same hit.
///
/// Normally this component sits on the <c>BomberExplosion</c> prefab (particles authored in the Inspector,
/// so the look can be tuned without touching code) and <see cref="Play"/> instantiates it. If no prefab is
/// wired, <see cref="Play"/> falls back to a self-built light-and-bang so a missing reference degrades to a
/// dim explosion rather than a silent, invisible one.
/// </summary>
public sealed class BomberExplosionFx : MonoBehaviour
{
    [Header("Refs (auto-resolved if left empty)")]
    [SerializeField] Light flashLight;
    [SerializeField] AudioSource audioSource;

    [Header("Flash")]
    [Tooltip("Seconds the blast light takes to fall away. The particles outlive it by design.")]
    [SerializeField, Min(0.05f)] float lightSeconds = 0.75f;
    [SerializeField, Min(0f)] float peakIntensity = 180f;
    [Tooltip("How hard the flash decays. Higher = a sharper pop.")]
    [SerializeField, Min(0.5f)] float lightFalloff = 3.4f;

    [Header("Lifetime")]
    [Tooltip("Seconds before the whole effect tears itself down. Must outlast the smoke and the bang.")]
    [SerializeField, Min(0.5f)] float lifeSeconds = 4.5f;

    static readonly Color FireColor = new Color(1f, 0.62f, 0.22f);

    float _startTime;

    /// <summary>
    /// Spawns one blast at <paramref name="point"/> on this peer. <paramref name="prefab"/> is the authored
    /// <c>BomberExplosion</c>; pass null to get the bare light-and-bang fallback.
    /// </summary>
    public static void Play(GameObject prefab, Vector3 point, AudioClip boom, float volume = 1f)
    {
        if (prefab != null)
        {
            GameObject instance = Instantiate(prefab, point, Quaternion.identity);
            var fx = instance.GetComponent<BomberExplosionFx>();
            if (fx != null)
            {
                fx.Begin(boom, volume);
                return;
            }

            // Prefab wired but missing its controller: still play the bang, and bound its lifetime so a
            // mis-authored prefab cannot leak an object per detonation.
            PlayClipOn(instance, boom, volume);
            Destroy(instance, 6f);
            return;
        }

        Play(point, boom, volume);
    }

    /// <summary>Fallback: no prefab, so build the old light-and-bang from nothing.</summary>
    public static void Play(Vector3 point, AudioClip boom, float volume = 1f)
    {
        var go = new GameObject("BomberExplosionFx");
        go.transform.position = point;

        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = FireColor;
        light.range = 20f;
        light.intensity = 180f;
        light.shadows = LightShadows.None;

        var fx = go.AddComponent<BomberExplosionFx>();
        fx.flashLight = light;
        fx.Begin(boom, volume);
    }

    void Begin(AudioClip boom, float volume)
    {
        _startTime = Time.time;

        if (flashLight == null)
            flashLight = GetComponentInChildren<Light>(true);
        if (flashLight != null)
        {
            flashLight.shadows = LightShadows.None;   // a sub-second flash should not pay for shadow maps
            peakIntensity = Mathf.Max(peakIntensity, flashLight.intensity);
            flashLight.intensity = peakIntensity;
        }

        float life = lifeSeconds;
        if (boom != null)
        {
            if (audioSource == null)
                audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
                audioSource = PlayClipOn(gameObject, boom, volume);
            else
            {
                GameAudioManager.RouteSfxSource(audioSource);
                audioSource.PlayOneShot(boom, Mathf.Clamp01(volume));
            }
            life = Mathf.Max(life, boom.length + 0.5f);   // never cut the bang off
        }

        Destroy(gameObject, life);
    }

    static AudioSource PlayClipOn(GameObject host, AudioClip boom, float volume)
    {
        if (boom == null)
            return null;

        var source = host.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 4f;
        source.maxDistance = 55f;
        GameAudioManager.RouteSfxSource(source);
        source.PlayOneShot(boom, Mathf.Clamp01(volume));
        return source;
    }

    void Update()
    {
        if (flashLight == null)
            return;

        float t = (Time.time - _startTime) / lightSeconds;
        if (t >= 1f)
        {
            // Switched off rather than destroyed: URP attaches a UniversalAdditionalLightData that
            // RequireComponents the Light, so Destroy(flashLight) is rejected with an error every time.
            flashLight.enabled = false;
            flashLight = null;
            return;
        }

        // A hard flash, then a fireball glow that dims out over the rest of the window.
        flashLight.intensity = peakIntensity * Mathf.Exp(-t * lightFalloff);
    }
}
