using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// One portcullis of the carnival main room (<see cref="CarnivalGateSystem"/> drives both). The leaf drops from
/// above the doorway to the floor when closed and winches back up when opened. Purely local presentation: every
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
    [Tooltip("Seconds for a full lift.")]
    [SerializeField, Min(0.05f)] float openSeconds = 3f;

    [Header("Enemy pathing")]
    [Tooltip("Carving obstacle covering the doorway; enabled while the gate blocks it.")]
    [SerializeField] NavMeshObstacle navBlocker;
    [Tooltip("While lifting, the passage reopens to pathing once the gate is this far up (0 = down, 1 = up).")]
    [SerializeField, Range(0f, 1f)] float pathReopenFraction = 0.5f;

    [Header("Audio")]
    [SerializeField] AudioSource audioSource;
    [SerializeField] AudioClip slamClip;
    [Tooltip("Seconds before landing to start the slam clip, so its peak lines up with the impact.")]
    [SerializeField, Min(0f)] float slamLeadSeconds = 0.25f;
    [SerializeField] AudioClip liftClip;
    [SerializeField, Range(0f, 1f)] float volume = 0.9f;

    // 0 = fully open (up), 1 = fully closed (down).
    float _closedness;
    float _moveFrom;
    float _moveTo;
    float _moveElapsed;
    float _moveDuration;
    bool _moving;
    bool _slamPlayed;

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
        _slamPlayed = false;

        if (!closed)
            PlayClip(liftClip);
    }

    void Update()
    {
        if (!_moving)
            return;

        _moveElapsed += Time.deltaTime;
        float u = Mathf.Clamp01(_moveElapsed / _moveDuration);
        bool closing = _moveTo > _moveFrom;

        // Closing accelerates (gravity drop); opening eases in and out (winch).
        float eased = closing ? u * u : Mathf.SmoothStep(0f, 1f, u);
        _closedness = Mathf.Lerp(_moveFrom, _moveTo, eased);

        if (closing && !_slamPlayed && _moveDuration - _moveElapsed <= slamLeadSeconds)
        {
            _slamPlayed = true;
            PlayClip(slamClip);
        }

        if (u >= 1f)
        {
            _closedness = _moveTo;
            _moving = false;
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

    void PlayClip(AudioClip clip)
    {
        if (clip == null || audioSource == null || !audioSource.isActiveAndEnabled)
            return;
        audioSource.PlayOneShot(clip, volume);
    }
}
