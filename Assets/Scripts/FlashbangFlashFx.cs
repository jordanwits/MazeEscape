using UnityEngine;

/// <summary>
/// The burst itself: a very bright, very short-lived point light plus the bang, spawned locally on every
/// peer at the detonation point by <see cref="FlashbangGrenade"/>. Purely cosmetic and self-building — no
/// prefab and no material — so the grenade prefab stays a single mesh; the bang clip is the one authored
/// asset, handed in by the grenade (which is the object the detonation RPC runs on, so every peer has it).
/// The actual blinding is not this: players are whited out by
/// <see cref="PlayerController.ApplyFlashbangBlind"/> and enemies by <see cref="EnemyBlindEffect"/>, both
/// decided server-side, so a peer whose light is culled is still blinded exactly the same.
/// </summary>
public sealed class FlashbangFlashFx : MonoBehaviour
{
    const float LightSeconds = 0.42f;
    const float PeakIntensity = 260f;
    const float LightRange = 26f;
    const float MinLifeSeconds = 3f;

    Light _light;
    float _startTime;

    /// <summary>Spawns one flash + bang at <paramref name="point"/> on this peer.</summary>
    public static void Play(Vector3 point, AudioClip bang, float volume = 1f)
    {
        var go = new GameObject("FlashbangFlashFx");
        go.transform.position = point;
        go.AddComponent<FlashbangFlashFx>().Begin(bang, volume);
    }

    void Begin(AudioClip bang, float volume)
    {
        _startTime = Time.time;

        _light = gameObject.AddComponent<Light>();
        _light.type = LightType.Point;
        _light.color = Color.white;
        _light.range = LightRange;
        _light.intensity = PeakIntensity;
        _light.shadows = LightShadows.None; // a one-frame flash does not need to pay for shadow maps

        float life = MinLifeSeconds;
        if (bang != null)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 3f;
            source.maxDistance = 45f;
            GameAudioManager.RouteSfxSource(source);
            source.PlayOneShot(bang, Mathf.Clamp01(volume));
            life = Mathf.Max(life, bang.length + 0.5f); // outlive the clip so the bang never cuts off
        }

        Destroy(gameObject, life);
    }

    void Update()
    {
        if (_light == null)
            return;

        float t = (Time.time - _startTime) / LightSeconds;
        if (t >= 1f)
        {
            // Switched off rather than destroyed: URP attaches a UniversalAdditionalLightData that
            // RequireComponents the Light, so Destroy(_light) is rejected with an error every time.
            _light.enabled = false;
            _light = null;
            return;
        }

        // Instant peak, then a sharp exponential fall — the eye-searing part is over in a couple of frames.
        _light.intensity = PeakIntensity * Mathf.Exp(-t * 6f);
    }
}
