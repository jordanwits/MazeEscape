using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Plays collision SFX without spamming rolling micro-hits.
/// Offline: listens locally.<br/>
/// Online: collisions are evaluated on the server (authoritative <see cref="Rigidbody"/>), then replicated to
/// observers via <see cref="PlayImpactObserversClientRpc"/>.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))]
public sealed class RigidbodyImpactSfx : NetworkBehaviour
{
    const string DefaultBallClipPath = "Assets/Audio/SFX/Ball.wav";

    [SerializeField]
    AudioClip impactClip;

    [SerializeField, FormerlySerializedAs("volume")]
    [Range(0.02f, 0.55f)]
    [Tooltip("One-shot volume at the hardest impacts (still capped by impactVolumeHardCap). Quiet hits use impactVolumeQuiet.")]
    float impactVolumeMax = 0.36f;

    [SerializeField, FormerlySerializedAs("playVolumeFloor")]
    [Range(0.02f, 0.5f)]
    [Tooltip("One-shot volume for impacts that barely pass the gate (soft taps). Loud hits scale up to impactVolumeMax.")]
    float impactVolumeQuiet = 0.09f;

    [SerializeField]
    [Range(0.05f, 0.6f)]
    [Tooltip("Clamp: one-shots never exceed this.")]
    float impactVolumeHardCap = 0.42f;

    [SerializeField, Range(0f, 0.6f)]
    [Tooltip("How much collision speed adds to perceived impact strength versus normal closure.")]
    float impactSpeedWeight = 0.28f;

    [SerializeField]
    [Min(0.2f)]
    [Tooltip(
        "Combined strength (normal closure + speed × impactSpeedWeight) that maps to impactVolumeMax. Increase to require harder hits before max volume.")]
    float impactStrengthFull = 4.2f;

    [SerializeField]
    [Tooltip(
        "Default min closing speed along a contact normal (dynamic props / walls sideways hits). Rolling on static floor gets a higher computed minimum.")]
    [Min(0.05f)]
    float minNormalImpactSpeed = 0.38f;

    [SerializeField]
    [Tooltip(
        "If tangential scrape is noisy, gate those by requiring EITHER strong normal closure OR overall impact speed.")]
    [Min(0.1f)]
    float backupTotalImpactSpeed = 1.92f;

    [SerializeField]
    [Tooltip("Minimal normal closure when using the backup total-speed path (avoids scrape-only spikes).")]
    [Min(0.05f)]
    float minNormalImpactForBackupPath = 0.26f;

    [SerializeField]
    [Tooltip("Min upward component of ANY contact normal to treat collisions as predominately floor.")]
    [Range(0.25f, 0.98f)]
    float floorDominantContactNormalY = 0.58f;

    [SerializeField]
    [Tooltip(
        "When hitting static geometry with mostly floor-like normals (rolling), ignore closure below this unless the hit is unusually hard.")]
    [Min(0.05f)]
    float rollingFloorMinNormalImpact = 0.9f;

    [SerializeField]
    [Tooltip(
        "When the collision includes an enabled CharacterController (player / jailors), use these eased thresholds.")]
    [Min(0.03f)]
    float characterMinNormalImpactSpeed = 0.16f;

    [SerializeField]
    [Min(0.1f)]
    float characterBackupTotalImpactSpeed = 1.05f;

    [SerializeField]
    [Min(0.03f)]
    float characterMinNormalForBackupPath = 0.1f;

    [SerializeField]
    [Tooltip("Extra-soft tier for idle / slow creep into the ball — avoids misses when closures are shallow.")]
    [Min(0.04f)]
    float characterGentleMinNormalImpact = 0.1f;

    [SerializeField]
    [Tooltip("Companion to gentle tier: still require some lateral closing speed.")]
    [Min(0.08f)]
    float characterGentleTotalSpeed = 0.36f;

    [SerializeField]
    [Tooltip(
        "Minimum planar CharacterController speed to trigger bump SFX. CC hits often skip the ball's OnCollisionEnter.")]
    [Min(0.01f)]
    float characterBumpNotifyMinSpeed = 0.03f;

    [SerializeField]
    [Min(0.02f)]
    float cooldownSeconds = 0.18f;

    [SerializeField, Range(0.2f, 1f)]
    [Tooltip(
        "When the obstacle has a CharacterController, multiply cooldown by this (quicker repeats for walk-into jams).")]
    float characterCooldownScale = 0.62f;

    [SerializeField, Range(60, 230)]
    [Tooltip("Lower = fewer other sounds delaying this playback (spatial 3D SFX compete for voicings).")]
    int sfxPriority = 88;

    [SerializeField, Min(0.5f)] float spatialMinDistance = 0.65f;

    [SerializeField, Min(1f)] float spatialMaxDistance = 38f;

    AudioSource _audio;
    float _nextPlayTime;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        EnsureAudio();
    }

    void Awake()
    {
        EnsureAudio();
#if UNITY_EDITOR
        AutoAssignDefaultClipEditor();
#endif
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        AutoAssignDefaultClipEditor();
        impactVolumeHardCap = Mathf.Clamp(impactVolumeHardCap, 0.05f, 0.95f);
        impactVolumeMax = Mathf.Min(impactVolumeMax, impactVolumeHardCap);
        impactVolumeQuiet = Mathf.Min(impactVolumeQuiet, impactVolumeMax);
    }

    void AutoAssignDefaultClipEditor()
    {
        if (impactClip != null)
            return;

        AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(DefaultBallClipPath);
        if (clip != null)
            impactClip = clip;
    }
#endif

    void EnsureAudio()
    {
        if (_audio != null)
            return;

        _audio = GetComponent<AudioSource>();
        if (_audio == null)
            _audio = gameObject.AddComponent<AudioSource>();

        _audio.playOnAwake = false;
        _audio.loop = false;
        _audio.spatialBlend = 1f;
        _audio.priority = sfxPriority;
        _audio.dopplerLevel = 0.05f;
        _audio.minDistance = spatialMinDistance;
        _audio.maxDistance = spatialMaxDistance;
        _audio.rolloffMode = AudioRolloffMode.Linear;
    }

    float ComputeVolumeFromImpactStrength(float strength)
    {
        float denom = Mathf.Max(0.15f, impactStrengthFull);
        float t01 = Mathf.Clamp01(Mathf.SmoothStep(0f, 1f, strength / denom));
        float vMin = Mathf.Min(impactVolumeQuiet, impactVolumeMax);
        float vMax = Mathf.Max(impactVolumeQuiet, impactVolumeMax);
        float vol = Mathf.Lerp(vMin, vMax, t01);
        return Mathf.Clamp(vol, 0.01f, Mathf.Min(impactVolumeHardCap, 1f));
    }

    void ApplyImpactCooldown(bool characterHit)
    {
        float baseCd = Mathf.Max(cooldownSeconds, 0.05f);
        float cooldown = characterHit
            ? Mathf.Max(0.05f, baseCd * Mathf.Clamp(characterCooldownScale, 0.2f, 1f))
            : baseCd;
        _nextPlayTime = Time.time + cooldown;
    }

    /// <summary>
    /// CharacterController pushes often never raise <see cref="OnCollisionEnter"/> on this rigidbody — call after a
    /// validated controller bump (server for online, owning machine for offline).
    /// </summary>
    public void NotifyCharacterControllerBump(float planarCharacterSpeed)
    {
        if (!isActiveAndEnabled || impactClip == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool networked = nm != null && nm.IsListening;
        bool serverEvaluates = networked && IsAuthoritativeImpactServer(nm);

        if (networked && !serverEvaluates)
            return;

        float now = Time.time;
        if (now < _nextPlayTime)
            return;

        planarCharacterSpeed = Mathf.Max(0f, planarCharacterSpeed);
        if (planarCharacterSpeed < characterBumpNotifyMinSpeed)
            return;

        float strength = planarCharacterSpeed + planarCharacterSpeed * impactSpeedWeight;
        float volume01 = Mathf.Clamp01(ComputeVolumeFromImpactStrength(strength));

        ApplyImpactCooldown(true);

        PlayImpactNetworkAware(volume01, serverEvaluates);
    }

    void OnCollisionEnter(Collision collision)
    {
        NetworkManager nm = NetworkManager.Singleton;
        bool networked = nm != null && nm.IsListening;
        bool serverEvaluates = networked && IsAuthoritativeImpactServer(nm);

        if (networked && !serverEvaluates)
            return;

        if (!EvaluateImpactTiming(collision, out float computedVolume))
            return;

        float volume01 = Mathf.Clamp01(computedVolume);

        PlayImpactNetworkAware(volume01, serverEvaluates);

        ApplyImpactCooldown(CollisionHasEnabledCharacterParent(collision));
    }

    bool IsAuthoritativeImpactServer(NetworkManager nm)
    {
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return false;

        return !IsSpawned || IsServer;
    }

    void PlayImpactNetworkAware(float volume01, bool serverEvaluates)
    {
        if (!serverEvaluates)
        {
            PlayLocalOneShot(volume01);
            return;
        }

        if (IsSpawned)
        {
            PlayImpactObserversClientRpc(volume01);
            return;
        }

        PlayLocalOneShot(volume01);
        NetworkPlayerInventory.ServerBroadcastRigidbodyImpactSfx(this, volume01);
    }

    public void PlayReplicatedImpact(float volume01)
    {
        if (!isActiveAndEnabled || impactClip == null)
            return;

        PlayLocalOneShot(volume01);
    }

    bool EvaluateImpactTiming(Collision collision, out float normalizedVolumeOut)
    {
        normalizedVolumeOut = 1f;

        if (!isActiveAndEnabled || impactClip == null || collision.contactCount <= 0)
            return false;

        float now = Time.time;
        if (now < _nextPlayTime)
            return false;

        if (!TryGetImpactSignals(collision, out float normalImpactMax, out float speed))
            return false;

        bool hasCharacterParent = CollisionHasEnabledCharacterParent(collision);
        bool hittingStaticCollider = collision.rigidbody == null;

        if (SuppressRollingFloorMicroHit(
                collision,
                hittingStaticCollider,
                hasCharacterParent,
                collision.relativeVelocity))
            return false;

        float minNorm = hasCharacterParent ? characterMinNormalImpactSpeed : minNormalImpactSpeed;
        float minBackupNorm = hasCharacterParent ? characterMinNormalForBackupPath : minNormalImpactForBackupPath;
        float backupSpeed = hasCharacterParent ? characterBackupTotalImpactSpeed : backupTotalImpactSpeed;

        bool definiteImpact = normalImpactMax >= minNorm;

        bool allowBackupSpeedPath = !(hittingStaticCollider && !hasCharacterParent
            && CollisionIsFloorOnlyAgainstStaticWorld(collision, collision.relativeVelocity));
        if (allowBackupSpeedPath)
            definiteImpact |= speed >= backupSpeed && normalImpactMax >= minBackupNorm;
        if (!definiteImpact && hasCharacterParent)
        {
            definiteImpact = normalImpactMax >= characterGentleMinNormalImpact
                && speed >= characterGentleTotalSpeed;
        }

        if (!definiteImpact)
            return false;

        float strength = Mathf.Max(0f, normalImpactMax) + Mathf.Max(0f, speed) * impactSpeedWeight;
        normalizedVolumeOut = ComputeVolumeFromImpactStrength(strength);

        return true;
    }

    [ClientRpc]
    void PlayImpactObserversClientRpc(float volume01)
    {
        PlayLocalOneShot(volume01);
    }

    void PlayLocalOneShot(float volume01)
    {
        EnsureAudio();
        RefreshSpatialSettings();
        float vol = Mathf.Clamp(volume01, 0f, Mathf.Min(impactVolumeHardCap, 1f));

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_audio);

        if (impactClip != null && _audio != null)
            _audio.PlayOneShot(impactClip, Mathf.Max(0f, vol));
    }

    void RefreshSpatialSettings()
    {
        if (_audio == null)
            return;

        _audio.priority = sfxPriority;
        _audio.minDistance = spatialMinDistance;
        _audio.maxDistance = spatialMaxDistance;
    }

    /// <summary>
    /// True when every contact is "floor-like" (no wall/corner contact in the manifold) and closure
    /// along those normals is below the rolling threshold. Tangential rolling speed must not bypass this.
    /// </summary>
    bool SuppressRollingFloorMicroHit(
        Collision collision,
        bool hittingStaticCollider,
        bool hasCharacterParent,
        Vector3 relativeVelocity)
    {
        if (!hittingStaticCollider || hasCharacterParent)
            return false;

        if (!TryGetFloorOnlyClosure(
                collision,
                relativeVelocity,
                floorDominantContactNormalY,
                out float floorClosureMax))
            return false;

        return floorClosureMax < rollingFloorMinNormalImpact;
    }

    /// <summary>
    /// Floor-only if at least one contact is floor-like and no contact is clearly wall-like (horizontal normal).
    /// Returns max |rv·n| over floor-like contacts only (ignores tilted normals that mix horizontal motion in).
    /// </summary>
    static bool TryGetFloorOnlyClosure(
        Collision collision,
        Vector3 relativeVelocity,
        float floorNormalYMinimum,
        out float floorClosureMax)
    {
        floorClosureMax = 0f;
        if (collision.contactCount <= 0)
            return false;

        const float WallLikeNormalY = 0.38f;

        bool anyFloorLike = false;
        int n = Mathf.Min(collision.contactCount, 16);
        for (int i = 0; i < n; i++)
        {
            Vector3 nm = collision.GetContact(i).normal;
            if (nm.y < WallLikeNormalY)
                return false;

            if (nm.y < floorNormalYMinimum)
                continue;

            anyFloorLike = true;
            float c = Mathf.Abs(Vector3.Dot(relativeVelocity, nm));
            floorClosureMax = Mathf.Max(floorClosureMax, c);
        }

        return anyFloorLike;
    }

    bool CollisionIsFloorOnlyAgainstStaticWorld(Collision collision, Vector3 relativeVelocity)
    {
        return TryGetFloorOnlyClosure(collision, relativeVelocity, floorDominantContactNormalY, out _);
    }

    bool TryGetImpactSignals(Collision collision, out float normalImpactMax, out float speed)
    {
        normalImpactMax = 0f;
        speed = collision.relativeVelocity.magnitude;
        if (collision.contactCount <= 0)
            return false;

        Vector3 rv = collision.relativeVelocity;
        int n = Mathf.Min(collision.contactCount, 16);
        for (int i = 0; i < n; i++)
        {
            ContactPoint cp = collision.GetContact(i);
            normalImpactMax = Mathf.Max(normalImpactMax, Mathf.Abs(Vector3.Dot(rv, cp.normal)));
        }

        return true;
    }

    static bool CollisionHasEnabledCharacterParent(Collision collision)
    {
        if (collision.collider == null)
            return false;

        CharacterController cc = collision.collider.GetComponentInParent<CharacterController>();
        return cc != null && cc.enabled;
    }
}
