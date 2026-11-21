using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// One portcullis of the carnival main room (<see cref="CarnivalGateSystem"/> drives both). The leaf drops from
/// above the doorway to the floor when closed and slides back up when opened. Purely local presentation: every
/// peer moves its own copy from the replicated gate state, so it needs no NetworkObject of its own.
///
/// <para><b>Blocking.</b> The leaf carries a solid BoxCollider that moves with it, so it stops players and enemy
/// CharacterControllers alike. Enemies path on the NavMesh, which was baked with the gates up, so a static
/// <see cref="NavMeshObstacle"/> in the doorway carves the passage out while the gate is down — chasers then stop
/// at the bars instead of planning routes through the room.</para>
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalGate : MonoBehaviour
{
    [Header("Leaf")]
    [SerializeField] Transform leaf;
    [Tooltip("Leaf local position when the gate is UP (doorway clear).")]
    [SerializeField] Vector3 openLocalPosition;
    [Tooltip("Leaf local position when the gate is DOWN (doorway sealed).")]
    [SerializeField] Vector3 closedLocalPosition;
    [Tooltip("Seconds for a full drop. The drop accelerates like a falling portcullis.")]
    [SerializeField, Min(0.05f)] float closeSeconds = 1.3f;
    [Tooltip("Seconds for a full lift. Matched to the open clip's length so the sound covers the whole lift.")]
    [SerializeField, Min(0.05f)] float openSeconds = 1.35f;

    [Header("Enemy pathing")]
    [Tooltip("Carving obstacle covering the doorway; enabled while the gate blocks it.")]
    [SerializeField] NavMeshObstacle navBlocker;
    [Tooltip("While lifting, the passage reopens to pathing once the gate is this far up (0 = down, 1 = up).")]
    [SerializeField, Range(0f, 1f)] float pathReopenFraction = 0.5f;

    [Header("Audio")]
    [Tooltip("Plays the close or open clip once per move.")]
    [SerializeField] AudioSource audioSource;
    [Tooltip("Played as the grate slides down. Cut to exactly closeSeconds, loudness following the drop's speed.")]
    [SerializeField] AudioClip closeClip;
    [Tooltip("Played as the grate slides up (the close sound reversed). Cut to exactly openSeconds.")]
    [SerializeField] AudioClip openClip;
    [SerializeField, Range(0f, 1f)] float volume = 0.5f;
    [Tooltip("If a move ends before its clip does (e.g. reversed mid-travel), the clip fades out over this long.")]
    [SerializeField, Min(0.01f)] float fadeOutSeconds = 0.1f;

    // 0 = fully open (up), 1 = fully closed (down).
    float _closedness;
    float _moveFrom;
    float _moveTo;
    float _moveElapsed;
    float _moveDuration;
    bool _moving;
    float _fadeLeft;

    void Awake()
    {
        ConfigureAudio();
        _closedness = 0f;
        _moveTo = 0f;
        ApplyPose();
    }

    void ConfigureAudio()
    {
        if (audioSource == null)
            return;

        audioSource.playOnAwake = false;
        audioSource.loop = false;
        audioSource.pitch = 1f;
        audioSource.spatialBlend = 1f;
        audioSource.dopplerLevel = 0f;
        audioSource.rolloffMode = AudioRolloffMode.Linear;
        audioSource.minDistance = 3f;
        audioSource.maxDistance = 30f;
        GameAudioManager.RouteSfxSource(audioSource);
    }

    /// <summary>Drive the gate toward closed/open. <paramref name="snap"/> jumps there silently (level start, late join).</summary>
    public void SetClosed(bool closed, bool snap)
    {
        float target = closed ? 1f : 0f;
        if (snap)
        {
            _closedness = target;
            _moveTo = target;
            _moving = false;
            _fadeLeft = 0f;
            if (audioSource != null)
                audioSource.Stop();
            ApplyPose();
            return;
        }

        if (Mathf.Approximately(target, _moveTo) && (_moving || Mathf.Approximately(_closedness, target)))
            return;

        _moveFrom = _closedness;
        _moveTo = target;
        _moveElapsed = 0f;
        // A reversal mid-travel only has the remaining distance to cover.
        _moveDuration = Mathf.Max(0.05f, Mathf.Abs(target - _closedness) * (closed ? closeSeconds : openSeconds));
        _moving = true;

        StartMoveSound(closed ? closeClip : openClip, closed ? closeSeconds : openSeconds);
    }

    void StartMoveSound(AudioClip clip, float fullTravelSeconds)
    {
        _fadeLeft = 0f;
        if (audioSource == null || clip == null || !audioSource.isActiveAndEnabled)
            return;

        audioSource.Stop();
        audioSource.clip = clip;
        audioSource.volume = volume;
        // The clips are cut to the full travel time, so this is 1 as shipped; it keeps sound and motion in step
        // if closeSeconds/openSeconds are retuned. A shorter (reversed mid-travel) move fades out on arrival instead.
        audioSource.pitch = Mathf.Clamp(clip.length / Mathf.Max(0.05f, fullTravelSeconds), 0.5f, 2f);
        audioSource.Play();
    }

    void TickSlideFade()
    {
        if (_fadeLeft <= 0f || audioSource == null)
            return;

        _fadeLeft = Mathf.Max(0f, _fadeLeft - Time.deltaTime);
        audioSource.volume = volume * (_fadeLeft / fadeOutSeconds);
        if (_fadeLeft <= 0f)
            audioSource.Stop();
    }

    void Update()
    {
        TickSlideFade();

        if (!_moving)
            return;

        _moveElapsed += Time.deltaTime;
        float u = Mathf.Clamp01(_moveElapsed / _moveDuration);
        bool closing = _moveTo > _moveFrom;

        // Closing accelerates (gravity drop); opening eases in and out (winch).
        float eased = closing ? u * u : Mathf.SmoothStep(0f, 1f, u);
        _closedness = Mathf.Lerp(_moveFrom, _moveTo, eased);

        if (u >= 1f)
        {
            _closedness = _moveTo;
            _moving = false;
            _fadeLeft = fadeOutSeconds;   // arrived: fade out whatever is left of the clip
        }

        ApplyPose();
    }

    void ApplyPose()
    {
        if (leaf != null)
            leaf.localPosition = Vector3.LerpUnclamped(openLocalPosition, closedLocalPosition, _closedness);

        if (navBlocker != null)
        {
            // Carve as soon as the gate is commanded down; keep carving while lifting until it is mostly clear.
            bool block = _moveTo >= 1f || _closedness > 1f - pathReopenFraction;
            if (navBlocker.enabled != block)
                navBlocker.enabled = block;
        }
    }
}
