using UnityEngine;

/// <summary>
/// Harmonica: a hotbar item you play by holding Attack (left click). Each hold plays one track from the start,
/// releasing stops it, and the next hold moves on to the next track — 1, 2, 3, then back to 1. A track that
/// runs out while the button is still down just ends; the next hold still advances.
///
/// The music plays from an AudioSource on the harmonica itself. A held item rides its holder's hand on every
/// peer, so teammates hear it coming from the player who is playing, with no extra positional sync. The holder
/// hears it flat (2D) — it is at their own mouth. Every peer also stops its own copy the moment the item is
/// stashed, dropped or carried over, all of which already replicate, so a missed stop message can never leave
/// a harmonica playing on its own.
///
/// Who plays what: the owner picks the track (<see cref="TakeNextTrackIndex"/>) and starts it locally for an
/// instant response, then <see cref="NetworkPlayerInventory"/> relays the index to everyone else.
/// <see cref="PlayTrack"/> also moves each copy's own counter past the track it was told to play, so all peers
/// stay on the same next track even when the harmonica changes hands.
///
/// While a hold lasts the player raises it to the mouth: <see cref="PlayPoseWeight"/> eases up on the same
/// start/stop messages, and <see cref="HeldItemHandSocketFollow"/> places the harmonica on the lips in the head's
/// frame (holes to the mouth) and IKs the right arm up to it. The holder's own first-person view shows it a
/// little in front of the face (<see cref="firstPersonOffsetInFace"/>), since the view camera sits at the face.
/// </summary>
public class HarmonicaItem : GrabbableInventoryItem
{
    public static Sprite SharedHudSlotIcon { get; private set; }

    [Header("Harmonica")]
    [Tooltip("Tracks in play order. Each new hold of Attack plays the next one, wrapping back to the first.")]
    [SerializeField] AudioClip[] tracks;

    [Tooltip("Loudness of the harmonica (0-1), before the Music bus slider.")]
    [SerializeField, Range(0f, 1f)] float volume = 0.85f;

    [Tooltip("Distance (metres) within which teammates hear it at full volume.")]
    [SerializeField, Min(0.01f)] float minDistance = 2f;

    [Tooltip("Teammates further than this (metres) cannot hear it. Volume fades linearly from Min Distance to here.")]
    [SerializeField, Min(0.1f)] float maxHearingDistance = 22f;

    [Tooltip("Seconds to fade out when the button is released or the harmonica is put away, so it never clicks off.")]
    [SerializeField, Min(0f)] float stopFadeSeconds = 0.12f;

    [Header("Playing pose (raised to the mouth)")]
    [Tooltip("Seconds to raise the harmonica to the mouth on a press, and to lower it again on release.")]
    [SerializeField, Min(0.01f)] float raiseSeconds = 0.22f;

    [Tooltip("The lip line relative to the head bone, in face axes (x = right, y = up, z = forward), metres. "
             + "Measured off the Survivalist head mesh; all four player characters share that rig.")]
    [SerializeField] Vector3 mouthInFace = new Vector3(-0.005f, 0.027f, 0.142f);

    [Tooltip("Head-bone-local direction the face looks along (Survivalist rig: -Y).")]
    [SerializeField] Vector3 faceForwardInHead = new Vector3(0.023f, -1f, 0f);

    [Tooltip("Head-bone-local up direction of the face (Survivalist rig: -X).")]
    [SerializeField] Vector3 faceUpInHead = new Vector3(-0.998f, -0.023f, 0.061f);

    [Tooltip("How far the mouthpiece presses into the lips (metres).")]
    [SerializeField] float lipPress = 0.003f;

    [Tooltip("Slides the harmonica along its length at the mouth (metres, + = toward the player's right), so the "
             + "fist on its right end sits beside the cheek rather than in it.")]
    [SerializeField] float lateralShift = 0.035f;

    [Tooltip("FIRST PERSON ONLY, for the player holding it: where the harmonica is shown relative to the mouth "
             + "(face axes, metres). The view camera sits at the face, so a harmonica on the lips would be out of "
             + "sight below it; this brings it into the bottom of the view. Teammates always see it at the mouth.")]
    [SerializeField] Vector3 firstPersonOffsetInFace = new Vector3(0f, 0f, 0.2f);

    [Tooltip("Direction from the fist down the forearm to the elbow while playing, in face axes (x = right, "
             + "y = up, z = forward). The fist is turned about the harmonica so the wrist stays straight along it, "
             + "and the elbow is bent toward it. Default: hanging down, a little out to the side and forward.")]
    [SerializeField] Vector3 forearmDirectionInFace = new Vector3(0.35f, -1f, 0.15f);

    AudioSource _source;
    bool _fadingOut;
    int _nextTrack;
    bool _raised;
    float _raise01;
    float _mouthpieceDepth = 0.0165f;

    /// <summary>Number of playable tracks assigned on the prefab.</summary>
    public int TrackCount => tracks != null ? tracks.Length : 0;

    /// <summary>True while a track is audible (including its release fade).</summary>
    public bool IsPlaying => _source != null && _source.isPlaying;

    /// <summary>
    /// 0 = carried at the side, 1 = up at the mouth, eased in between. Up from the press that starts a track to the
    /// release that ends the hold (even if the track ran out first); every peer follows the same start/stop messages.
    /// Read by <see cref="HeldItemHandSocketFollow"/>, which raises the arm.
    /// </summary>
    public float PlayPoseWeight => Mathf.SmoothStep(0f, 1f, _raise01);

    /// <summary>
    /// World direction from the fist toward the elbow for the playing pose, given the play rotation from
    /// <see cref="TryGetPlayPose"/>. Not normalised to the harmonica's axis; the caller projects it.
    /// </summary>
    public Vector3 PlayForearmDirection(Quaternion playRotation)
    {
        // playRotation = face * Euler(0,-90,0); undo the turn to get the face frame back.
        Quaternion face = playRotation * Quaternion.Euler(0f, 90f, 0f);
        return face * forearmDirectionInFace;
    }

    protected override void Awake()
    {
        _itemTypeId = TypeIdHarmonica;
        base.Awake();

        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;

        // How far the mouthpiece (the -X face of the root frame) sits in front of the pivot.
        if (TryGetComponent(out BoxCollider box))
            _mouthpieceDepth = -(box.center.x - box.size.x * 0.5f) * transform.localScale.x;
    }

    /// <summary>
    /// The harmonica's pose up at the mouth: on the lips, level, its length across the mouth and the holes facing
    /// it. Built in the head's frame, so it stays at the mouth however the head is turned. With
    /// <paramref name="firstPersonView"/> it is moved into view for the player holding it (see
    /// <see cref="firstPersonOffsetInFace"/>).
    /// </summary>
    public bool TryGetPlayPose(Transform head, bool firstPersonView, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.identity;
        if (head == null)
            return false;

        Vector3 forward = head.rotation * faceForwardInHead;
        Vector3 up = head.rotation * faceUpInHead;
        if (forward.sqrMagnitude < 1e-6f || up.sqrMagnitude < 1e-6f)
            return false;

        Quaternion face = Quaternion.LookRotation(forward.normalized, up.normalized);
        Vector3 local = mouthInFace
            + new Vector3(lateralShift, 0f, _mouthpieceDepth - lipPress)
            + (firstPersonView ? firstPersonOffsetInFace : Vector3.zero);
        position = head.position + face * local;
        // Root frame: +X away from the face (so the holes on -X face the mouth), +Y up, +Z to the player's left.
        rotation = face * Quaternion.Euler(0f, -90f, 0f);
        return true;
    }

    /// <summary>
    /// The track the next hold should play; advances the counter. Owner-side — the result is what gets sent to
    /// the other peers, whose <see cref="PlayTrack"/> then lands their counters on the same next track.
    /// </summary>
    public int TakeNextTrackIndex()
    {
        int count = TrackCount;
        if (count == 0)
            return 0;

        int index = Mod(_nextTrack, count);
        _nextTrack = Mod(index + 1, count);
        return index;
    }

    /// <summary>
    /// Plays <paramref name="trackIndex"/> from the beginning. <paramref name="holderIsLocalListener"/> is true on
    /// the machine of the player holding it: they hear it flat rather than from their own hand.
    /// </summary>
    public void PlayTrack(int trackIndex, bool holderIsLocalListener)
    {
        int count = TrackCount;
        if (count == 0)
            return;

        int index = Mod(trackIndex, count);
        _nextTrack = Mod(index + 1, count);
        _raised = true;

        AudioClip clip = tracks[index];
        if (clip == null)
            return;

        EnsureSource();
        _fadingOut = false;
        _source.Stop();
        _source.clip = clip;
        _source.volume = volume;
        _source.spatialBlend = holderIsLocalListener ? 0f : 1f;
        _source.time = 0f;
        _source.Play();
    }

    /// <summary>Stops the current track with a short fade and lowers the harmonica. Safe to call when nothing is playing.</summary>
    public void StopTrack()
    {
        _raised = false;
        if (_source == null || !_source.isPlaying)
            return;

        if (stopFadeSeconds <= 0f)
        {
            _source.Stop();
            _fadingOut = false;
            return;
        }

        _fadingOut = true;
    }

    void Update()
    {
        // Put away or dropped mid-hold: lower it, and stay lowered if it is brought back out before the release.
        if (!IsHeld || IsStashed)
            _raised = false;
        _raise01 = Mathf.MoveTowards(_raise01, _raised ? 1f : 0f, Time.deltaTime / raiseSeconds);

        if (_source == null || !_source.isPlaying)
        {
            _fadingOut = false;
            return;
        }

        // Put away, dropped, or parked for a section switch: those states replicate on their own, so every peer
        // silences its own copy without waiting for (or depending on) the stop message.
        if (!IsHeld || IsStashed)
            _fadingOut = true;

        if (!_fadingOut)
            return;

        _source.volume = Mathf.MoveTowards(_source.volume, 0f, volume * Time.deltaTime / Mathf.Max(0.001f, stopFadeSeconds));
        if (_source.volume <= 0.0001f)
        {
            _source.Stop();
            _fadingOut = false;
        }
    }

    void EnsureSource()
    {
        if (_source == null)
        {
            // A child, so the item's own components (collider, rigidbody, renderer caches) are left alone.
            var go = new GameObject("HarmonicaAudio");
            go.transform.SetParent(transform, false);
            _source = go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.dopplerLevel = 0f;
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.minDistance = minDistance;
            _source.maxDistance = Mathf.Max(minDistance + 0.1f, maxHearingDistance);
        }

        // Routed at play time rather than in Awake: the mixer singleton may not exist yet when maze loot is built.
        // Idempotent (the occlusion registry is a set), so re-routing every play is free.
        GameAudioManager.RouteMusicSource(_source);
    }

    static int Mod(int value, int count)
    {
        int m = value % count;
        return m < 0 ? m + count : m;
    }
}
