using UnityEngine;

/// <summary>
/// Owner-side feedback for TAKING damage — the mirror of the melee-impact kick: a rough camera jolt plus a
/// universal hurt thud for every damage source (zombie, skeleton, clown, traps — the skeleton bash used to be
/// completely silent to the victim), and a low-health heartbeat built from a procedurally generated thump so
/// no audio asset is needed. Works offline and networked with no new RPCs: it watches this player's own
/// health for decreases, which already arrive on every peer (server-side TakeDamage locally, the
/// NetworkPlayerRespawn NetworkVariables remotely), and self-gates to the locally-controlled player.
/// </summary>
public partial class PlayerController
{
    [Header("Hurt feedback")]
    [Tooltip("Master switch for taking-damage feedback (camera jolt + hurt thud). The clip is Zombie Hit Clip below — now played for every damage source, not just zombies.")]
    [SerializeField] bool hurtFeedbackEnabled = true;

    [Header("Low-health heartbeat")]
    [SerializeField] bool heartbeatEnabled = true;
    [Tooltip("Heartbeat fades in below this health fraction (matches the vitals HUD critical threshold).")]
    [SerializeField, Range(0.05f, 0.9f)] float heartbeatStartHealthFraction = 0.25f;
    [SerializeField, Range(0f, 1f)] float heartbeatVolume = 0.5f;
    [Tooltip("Beats per minute right at the threshold; ramps toward Max as health approaches zero.")]
    [SerializeField, Min(20f)] float heartbeatMinBpm = 58f;
    [SerializeField, Min(20f)] float heartbeatMaxBpm = 110f;

    const string HeartbeatAudioChildName = "Player_Heartbeat";
    const float HeartbeatDubDelaySeconds = 0.22f; // lub ... dub — second, softer thump of each beat

    static AudioClip s_heartbeatThumpClip;

    float _lastSeenHealthForHurtFeedback = -1f;
    AudioSource _heartbeatSource;
    float _nextHeartbeatLubTime;
    float _pendingHeartbeatDubTime = -1f;

    /// <summary>Called every LateUpdate on all instances; everything inside self-gates to the local player.</summary>
    void TickHurtFeedback()
    {
        if (_playerHealth == null)
            return;

        float health = _playerHealth.CurrentHealth;
        if (_lastSeenHealthForHurtFeedback < 0f)
            _lastSeenHealthForHurtFeedback = health;

        if (health < _lastSeenHealthForHurtFeedback - 0.01f && hurtFeedbackEnabled && _hasLocalControl)
        {
            TriggerHurtCameraKick();
            PlayHurtSfx();
        }
        _lastSeenHealthForHurtFeedback = health;

        TickHeartbeat();
    }

    /// <summary>Universal "you got hit" thud — one clip for zombies, skeletons, clowns and traps alike.</summary>
    void PlayHurtSfx()
    {
        if (zombieHitClip == null || footstepAudioSource == null)
            return;

        footstepAudioSource.PlayOneShot(zombieHitClip, Mathf.Max(0f, zombieHitVolume));
    }

    void TickHeartbeat()
    {
        bool active = heartbeatEnabled
            && _hasLocalControl
            && _playerHealth != null
            && !_playerHealth.IsDead;

        float intensity = 0f;
        if (active)
        {
            float threshold = Mathf.Max(0.05f, heartbeatStartHealthFraction);
            float normalized = _playerHealth.HealthNormalized;
            intensity = normalized < threshold ? 1f - normalized / threshold : 0f;
        }

        if (intensity <= 0f)
        {
            _pendingHeartbeatDubTime = -1f;
            return;
        }

        EnsureHeartbeatSource();
        if (_heartbeatSource == null)
            return;

        float now = Time.time;

        if (_pendingHeartbeatDubTime > 0f && now >= _pendingHeartbeatDubTime)
        {
            _pendingHeartbeatDubTime = -1f;
            PlayHeartbeatThump(intensity, 0.65f);
        }

        if (now < _nextHeartbeatLubTime)
            return;

        float bpm = Mathf.Lerp(heartbeatMinBpm, Mathf.Max(heartbeatMinBpm, heartbeatMaxBpm), intensity);
        _nextHeartbeatLubTime = now + 60f / bpm;
        _pendingHeartbeatDubTime = now + HeartbeatDubDelaySeconds;
        PlayHeartbeatThump(intensity, 1f);
    }

    void PlayHeartbeatThump(float intensity, float accentScale)
    {
        AudioClip clip = GetHeartbeatThumpClip();
        if (clip == null)
            return;

        float volume = Mathf.Clamp01(heartbeatVolume * (0.5f + 0.5f * intensity) * accentScale);
        _heartbeatSource.PlayOneShot(clip, volume);
    }

    void EnsureHeartbeatSource()
    {
        if (_heartbeatSource != null)
            return;

        var go = new GameObject(HeartbeatAudioChildName);
        go.transform.SetParent(transform, false);
        _heartbeatSource = go.AddComponent<AudioSource>();
        _heartbeatSource.playOnAwake = false;
        _heartbeatSource.loop = false;
        _heartbeatSource.spatialBlend = 0f; // internal body sound — 2D, only this player ever hears it
        _heartbeatSource.dopplerLevel = 0f;
        GameAudioManager.RouteSfxSource(_heartbeatSource);
    }

    /// <summary>
    /// One lub/dub thump, synthesized once: a low sine that falls 58→36 Hz under a fast-decay envelope —
    /// reads as a muffled heartbeat without needing a recorded asset.
    /// </summary>
    static AudioClip GetHeartbeatThumpClip()
    {
        if (s_heartbeatThumpClip != null)
            return s_heartbeatThumpClip;

        const int sampleRate = 44100;
        const float duration = 0.28f;
        int sampleCount = (int)(sampleRate * duration);
        var data = new float[sampleCount];
        double phase = 0.0;
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)sampleRate;
            float frequency = Mathf.Lerp(58f, 36f, t / duration);
            phase += 2.0 * System.Math.PI * frequency / sampleRate;
            float envelope = Mathf.Exp(-t * 16f) * Mathf.Clamp01(t / 0.012f); // soft attack, fast decay
            data[i] = (float)System.Math.Sin(phase) * envelope * 0.85f;
        }

        s_heartbeatThumpClip = AudioClip.Create("HeartbeatThump", sampleCount, 1, sampleRate, false);
        s_heartbeatThumpClip.SetData(data, 0);
        return s_heartbeatThumpClip;
    }
}
