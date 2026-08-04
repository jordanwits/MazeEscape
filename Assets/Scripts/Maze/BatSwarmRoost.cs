using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A colony of decorative bats roosting in a dead-end maze cell. Fires once when the local player
/// looks into the dead end from close range: the swarm bursts out past them and flees down the only
/// corridor the cell has, disappearing into the distance fog.
///
/// <para><b>Deliberately not networked.</b> The bats are cosmetic, so there is no NetworkObject and no
/// RPC here. <see cref="ProceduralMazeCoordinator"/> places roosts from the maze seed, which means
/// every peer has an identical set of roosts in identical cells; each client then fires its own when
/// <em>its</em> player walks in. Two players entering the same dead end 30 seconds apart each get the
/// scare, and nothing crosses the wire.</para>
///
/// <para>Triggering is polled rather than done with a physics trigger volume — no collider to author,
/// no layer/Rigidbody assumptions about the player rig, and it costs a handful of distance checks per
/// second. The line-of-sight and view-cone gates exist because a swarm that erupts behind your back or
/// through a wall is just noise; the scare only lands if you see it happen.</para>
/// </summary>
[DisallowMultipleComponent]
public class BatSwarmRoost : MonoBehaviour
{
    struct PendingLaunch
    {
        public DecorativeBat Bat;
        public float DueTime;
    }

    [Header("Swarm")]
    [Tooltip("Bat prefab (must carry DecorativeBat). Instanced up front and pooled — the swarm never "
        + "allocates during the scare itself.")]
    [SerializeField] GameObject batPrefab;
    [Tooltip("How many bats are in this colony.")]
    [SerializeField, Min(1)] int swarmSize = 8;
    [Tooltip("Radius in metres over which the bats are scattered around the roost point before launch, "
        + "so they don't all erupt from one pixel.")]
    [SerializeField] float perchSpread = 0.7f;
    [Tooltip("Seconds over which the colony leaves. A little stagger reads as a cascade of bats; zero "
        + "makes them launch as one solid volley.")]
    [SerializeField] float launchSpreadSeconds = 0.4f;

    [Header("Trigger")]
    [Tooltip("Metres from the roost at which the swarm can fire.")]
    [SerializeField] float triggerRadius = 6f;
    [Tooltip("The roost must be within this many degrees of the player's view direction. Prevents the "
        + "swarm erupting behind them where the whole effect is wasted.")]
    [SerializeField] float viewConeDegrees = 75f;
    [Tooltip("Require unobstructed line of sight from the player's eye to the roost before firing.")]
    [SerializeField] bool requireLineOfSight = true;
    [Tooltip("Geometry that blocks line of sight.")]
    [SerializeField] LayerMask lineOfSightMask = ~0;
    [Tooltip("Seconds before a spent roost can fire again. 0 = never — a one-shot scare per level build.")]
    [SerializeField, Min(0f)] float rearmSeconds;
    [Tooltip("Seconds between trigger checks. The player can't cross the trigger radius in this time, "
        + "so there's no reason to poll every frame.")]
    [SerializeField] float pollInterval = 0.08f;

    [Header("Audio")]
    [Tooltip("Screech layer (BatChirp), played at the roost on launch. Optional — leave empty to drop "
        + "this layer.")]
    [SerializeField] AudioClip chirpClip;
    [Tooltip("Volume of the screech layer.")]
    [SerializeField, Range(0f, 1f)] float chirpVolume = 0.8f;
    [Tooltip("Wingbeat layer (WingsFlap), started at the same instant as the screech. It gets its own "
        + "AudioSource so the two can be balanced and pitched against each other instead of being mixed "
        + "into one fixed blend.")]
    [SerializeField] AudioClip wingsClip;
    [Tooltip("Volume of the wingbeat layer.")]
    [SerializeField, Range(0f, 1f)] float wingsVolume = 0.9f;
    [Tooltip("Metres at which the swarm audio falls silent.")]
    [SerializeField] float audioRange = 22f;
    [Tooltip("Random pitch spread applied per layer each time a roost fires. Both clips run about 7 s, so "
        + "without this the level's eight roosts audibly replay the same recording.")]
    [SerializeField, Range(0f, 0.5f)] float pitchJitter = 0.12f;
    [Tooltip("Seconds the swarm audio plays before being faded out and stopped. The source clips run about "
        + "7 s each — far longer than the swarm is on screen — so this trims them to the actual event "
        + "rather than requiring the wav files be re-cut.")]
    [SerializeField, Min(0.1f)] float audioDuration = 2.5f;
    [Tooltip("Length of the fade at the end of Audio Duration, so the layers duck out instead of cutting dead.")]
    [SerializeField, Min(0f)] float audioFadeSeconds = 0.6f;
    [Tooltip("How tightly the emitter chases the swarm. Higher = follows the bats more closely; lower lags "
        + "behind and smooths out the jumps as individual bats despawn.")]
    [SerializeField, Min(0.1f)] float audioFollowSharpness = 6f;

    readonly List<DecorativeBat> _pool = new();
    readonly List<PendingLaunch> _pending = new();

    Camera _viewer;
    Transform _audioEmitter;
    AudioSource _chirpSource;
    AudioSource _wingsSource;
    bool _audioPlaying;
    float _audioElapsed;
    float _nextPoll;
    float _nextCameraCheck;
    bool _spent;
    float _rearmAt;

    void Start()
    {
        BuildPool();
        BuildAudio();
    }

    void BuildPool()
    {
        if (batPrefab == null)
        {
            Debug.LogWarning($"{nameof(BatSwarmRoost)} on '{name}' has no bat prefab — roost is inert.", this);
            enabled = false;
            return;
        }

        // Pre-warmed: instantiating a swarm mid-scare is exactly the wrong moment for a frame spike.
        for (int i = 0; i < swarmSize; i++)
        {
            GameObject instance = Instantiate(batPrefab, transform.position, Quaternion.identity, transform);
            instance.name = $"Bat{i:00}";

            DecorativeBat bat = instance.GetComponent<DecorativeBat>();
            if (bat == null)
            {
                Debug.LogWarning($"{nameof(BatSwarmRoost)} bat prefab '{batPrefab.name}' is missing "
                    + $"{nameof(DecorativeBat)} — roost is inert.", this);
                Destroy(instance);
                enabled = false;
                return;
            }

            instance.SetActive(false);
            _pool.Add(bat);
        }
    }

    void BuildAudio()
    {
        if (chirpClip == null && wingsClip == null)
            return;

        // The sources live on their own child, NOT on the roost: the pooled bats are parented to the
        // roost transform, so moving the roost to chase the swarm would drag every bat with it.
        GameObject emitter = new GameObject("SwarmAudio");
        emitter.transform.SetParent(transform, false);
        _audioEmitter = emitter.transform;

        _chirpSource = CreateAudioLayer(chirpClip, chirpVolume);
        _wingsSource = CreateAudioLayer(wingsClip, wingsVolume);
    }

    /// <summary>
    /// Builds one spatialised source per layer. Two sources rather than one pre-mixed clip is what lets
    /// the screech and the wingbeat be balanced and pitched independently while still firing as a single
    /// sound. Returns null for an unassigned clip, which simply drops that layer.
    /// </summary>
    AudioSource CreateAudioLayer(AudioClip clip, float volume)
    {
        if (clip == null)
            return null;

        AudioSource source = _audioEmitter.gameObject.AddComponent<AudioSource>();
        source.clip = clip;
        source.playOnAwake = false;
        source.loop = false;
        source.volume = volume;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 1.5f;
        source.maxDistance = Mathf.Max(2f, audioRange);

        // Routing through the manager is what opts the source into the SFX bus and wall occlusion.
        GameAudioManager.RouteSfxSource(source);
        return source;
    }

    /// <summary>Starts both layers on the same frame from the roost position, and arms the follow/fade.</summary>
    void StartSwarmAudio()
    {
        if (_audioEmitter == null)
            return;

        _audioEmitter.position = transform.position;
        _audioPlaying = true;
        _audioElapsed = 0f;

        PlayAudioLayer(_chirpSource, chirpVolume);
        PlayAudioLayer(_wingsSource, wingsVolume);
    }

    void PlayAudioLayer(AudioSource source, float volume)
    {
        if (source == null)
            return;

        source.volume = volume; // reset: the fade below drives this down over the tail
        source.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
        source.Play();
    }

    /// <summary>
    /// Drags the emitter along behind the flock and cuts the audio at <see cref="audioDuration"/>.
    ///
    /// Both source clips are ~7 s of roost ambience, but the swarm is past the player in a couple of
    /// seconds, so the clips are trimmed here rather than in the wav files. Following the flock centroid
    /// is what makes the sound sweep past the player with the bats instead of staying behind at the perch
    /// — the whole point of the effect is that it goes over your shoulder.
    /// </summary>
    void UpdateSwarmAudio()
    {
        if (!_audioPlaying)
            return;

        float dt = Time.deltaTime;
        _audioElapsed += dt;

        // Exponential smoothing, so tracking is frame-rate independent and the emitter doesn't snap
        // when individual bats drop out of the average as they despawn.
        if (TryGetFlyingCentroid(out Vector3 centroid))
        {
            _audioEmitter.position = Vector3.Lerp(
                _audioEmitter.position, centroid, 1f - Mathf.Exp(-audioFollowSharpness * dt));
        }

        float remaining = audioDuration - _audioElapsed;
        float fade = audioFadeSeconds > 0f
            ? Mathf.Clamp01(remaining / audioFadeSeconds)
            : (remaining > 0f ? 1f : 0f);

        if (_chirpSource != null)
            _chirpSource.volume = chirpVolume * fade;
        if (_wingsSource != null)
            _wingsSource.volume = wingsVolume * fade;

        if (remaining > 0f)
            return;

        if (_chirpSource != null)
            _chirpSource.Stop();
        if (_wingsSource != null)
            _wingsSource.Stop();
        _audioPlaying = false;
    }

    /// <summary>Average position of the bats currently in the air; false once the swarm has landed.</summary>
    bool TryGetFlyingCentroid(out Vector3 centroid)
    {
        centroid = Vector3.zero;
        int flying = 0;

        for (int i = 0; i < _pool.Count; i++)
        {
            DecorativeBat bat = _pool[i];
            if (bat == null || !bat.IsFlying)
                continue;

            centroid += bat.transform.position;
            flying++;
        }

        if (flying == 0)
            return false;

        centroid /= flying;
        return true;
    }

    void Update()
    {
        // These two run before the spent-roost bail below on purpose: a one-shot roost is "spent" from
        // the instant it fires, and bailing first would strand its staggered launches and leave the
        // audio pinned at the perch at full volume forever.
        FlushPendingLaunches();
        UpdateSwarmAudio();

        if (_spent && (rearmSeconds <= 0f || Time.time < _rearmAt))
            return;

        float now = Time.time;
        if (now < _nextPoll)
            return;
        _nextPoll = now + Mathf.Max(0.02f, pollInterval);

        Camera viewer = ResolveViewer();
        if (viewer == null)
            return;

        if (!ShouldFire(viewer))
            return;

        Fire(viewer);
    }

    bool ShouldFire(Camera viewer)
    {
        Vector3 eye = viewer.transform.position;
        Vector3 toRoost = transform.position - eye;

        float distanceSqr = toRoost.sqrMagnitude;
        if (distanceSqr > triggerRadius * triggerRadius)
            return false;

        float distance = Mathf.Sqrt(distanceSqr);
        if (distance < 0.01f)
            return true;

        Vector3 direction = toRoost / distance;
        if (Vector3.Angle(viewer.transform.forward, direction) > viewConeDegrees)
            return false;

        if (requireLineOfSight
            && Physics.Raycast(eye, direction, distance - 0.15f, lineOfSightMask, QueryTriggerInteraction.Ignore))
            return false;

        return true;
    }

    void Fire(Camera viewer)
    {
        _spent = true;
        _rearmAt = Time.time + rearmSeconds;

        Transform viewerTransform = viewer.transform;

        // A dead-end cell has exactly one opening, and the coordinator points our +Z down it — which is
        // both where the player came from and the only way out for the bats.
        Vector3 fleeDirection = transform.forward;

        float now = Time.time;
        int launched = 0;

        for (int i = 0; i < _pool.Count; i++)
        {
            DecorativeBat bat = _pool[i];
            if (bat == null || bat.IsFlying)
                continue;

            Vector2 disc = Random.insideUnitCircle * perchSpread;
            Vector3 origin = transform.position + new Vector3(disc.x, Random.Range(-0.25f, 0.25f), disc.y);

            bat.transform.position = origin;
            bat.Launch(this, origin, fleeDirection, viewerTransform, Random.value * Mathf.PI * 2f);

            // Stagger by re-parking the ones that shouldn't have left yet. Cheaper than a coroutine
            // per bat and keeps the launch order deterministic within a frame.
            float delay = swarmSize > 1
                ? launchSpreadSeconds * (launched / (float)Mathf.Max(1, swarmSize - 1))
                : 0f;

            if (delay > 0.001f)
            {
                bat.gameObject.SetActive(false);
                _pending.Add(new PendingLaunch
                {
                    Bat = bat,
                    DueTime = now + delay,
                });
            }

            launched++;
        }

        StartSwarmAudio();
    }

    void FlushPendingLaunches()
    {
        if (_pending.Count == 0)
            return;

        float now = Time.time;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            PendingLaunch pending = _pending[i];
            if (now < pending.DueTime)
                continue;

            if (pending.Bat != null)
                pending.Bat.gameObject.SetActive(true);

            _pending.RemoveAt(i);
        }
    }

    /// <summary>Called by a bat when it finishes its flight; it is already deactivated and pooled.</summary>
    public void NotifyBatFinished(DecorativeBat bat)
    {
        if (bat != null)
            bat.transform.SetPositionAndRotation(transform.position, Quaternion.identity);
    }

    /// <summary>Set by <see cref="ProceduralMazeCoordinator"/> when it places the roost from the maze seed.</summary>
    public void ConfigureSwarmSize(int size)
    {
        swarmSize = Mathf.Max(1, size);
    }

    Camera ResolveViewer()
    {
        if (_viewer != null && _viewer.isActiveAndEnabled && _viewer.gameObject.activeInHierarchy)
            return _viewer;

        float now = Time.unscaledTime;
        if (now < _nextCameraCheck)
            return null;
        _nextCameraCheck = now + 0.5f;

        // Camera.main is null in this project (PlayerView is Untagged) — fall back to the enabled
        // Game camera, which on a client is the local player's view.
        Camera cam = Camera.main;
        if (cam == null)
        {
            Camera[] cameras = Camera.allCameras; // enabled cameras only
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].isActiveAndEnabled && cameras[i].cameraType == CameraType.Game)
                {
                    cam = cameras[i];
                    break;
                }
            }
        }

        _viewer = cam;
        return _viewer;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.8f, 0.5f, 0.15f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, triggerRadius);
        Gizmos.color = new Color(0.9f, 0.75f, 0.3f, 0.9f);
        Gizmos.DrawRay(transform.position, transform.forward * 3f);
    }
}
