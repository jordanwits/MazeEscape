using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Two-leaf sliding doors on an exit elevator cab (Severance / Level03). Purely local: the cab is part of a
/// procedurally built maze piece, so every peer owns its own copy of these transforms and the spawned
/// <see cref="ElevatorFinishController"/> replicates the open/closed state and calls <see cref="SetOpen"/> on all
/// of them. Nothing here is networked — do not add a NetworkObject to a maze piece (nested spawns are dropped).
/// </summary>
[DisallowMultipleComponent]
public class ElevatorSlidingDoors : MonoBehaviour
{
    [Header("Leaves")]
    [Tooltip(
        "The two door panels. Each must be an unrotated child whose local X runs across the doorway. "
        + "Each leaf retracts away from the midpoint between the two closed panels.")]
    [SerializeField] Transform leafA;
    [SerializeField] Transform leafB;

    [Header("Motion")]
    [Tooltip("How much of its own width each leaf retracts. Keep just under 1 so a sliver stays in the jamb instead of collapsing to zero size.")]
    [SerializeField, Range(0.1f, 1f)] float openFraction = 0.94f;
    [Tooltip(
        "On: the leaf keeps its outer edge pinned in the jamb and narrows as it opens, so it reads as sliding into a "
        + "door pocket this cab does not actually model. Off: the leaf slides its full travel and will poke out past the cab sides.")]
    [SerializeField] bool retractIntoJamb = true;
    [Tooltip("Seconds for a full open, matched to the open clip. Real time, so pausing does not stretch it.")]
    [SerializeField, Min(0.05f)] float openDuration = 1.15f;
    [Tooltip("Seconds for a full close, matched to the close clip.")]
    [SerializeField, Min(0.05f)] float closeDuration = 0.95f;

    [Header("Audio")]
    [SerializeField] AudioClip doorOpenClip;
    [SerializeField] AudioClip doorCloseClip;
    [SerializeField, Range(0f, 1f)] float doorVolume = 0.8f;

    struct Leaf
    {
        public Transform Transform;
        /// <summary>+1 when this leaf retracts toward local +X.</summary>
        public float Sign;
        public Vector3 ClosedLocalPosition;
        public Vector3 ClosedLocalScale;
        /// <summary>Mesh-space bounds so an off-centre pivot still lands the jamb edge correctly.</summary>
        public float MeshCenterX;
        public float MeshWidth;
    }

    Leaf[] _leaves = System.Array.Empty<Leaf>();
    bool _open;
    /// <summary>0 = closed, 1 = fully open.</summary>
    float _t;
    AudioSource _sfx;

    /// <summary>Target state: true once an open has been requested, false from the moment a close starts.</summary>
    public bool IsOpen => _open;

    /// <summary>True while the leaves are still travelling toward <see cref="IsOpen"/>.</summary>
    public bool IsBusy => _t != (_open ? 1f : 0f);

    /// <summary>Closed and settled — the elevator run may complete.</summary>
    public bool IsClosedAndIdle => !_open && _t == 0f;

    void Awake()
    {
        CacheLeaves();
        ApplyPose();
    }

    void CacheLeaves()
    {
        Transform[] source = { leafA, leafB };
        int count = 0;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != null)
                count++;
        }

        if (count == 0)
        {
            Debug.LogError($"{nameof(ElevatorSlidingDoors)} on '{name}': assign both door leaves.", this);
            _leaves = System.Array.Empty<Leaf>();
            enabled = false;
            return;
        }

        // Leaves retract away from the seam they meet at, so the direction never has to be authored by hand.
        float seamX = 0f;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] != null)
                seamX += source[i].localPosition.x;
        }
        seamX /= count;

        _leaves = new Leaf[count];
        int next = 0;
        for (int i = 0; i < source.Length; i++)
        {
            Transform t = source[i];
            if (t == null)
                continue;

            float meshCenterX = 0f;
            float meshWidth = 1f;
            if (t.TryGetComponent(out Renderer renderer))
            {
                Bounds local = renderer.localBounds;
                meshCenterX = local.center.x;
                meshWidth = Mathf.Max(0.0001f, local.size.x);
            }

            float delta = t.localPosition.x - seamX;
            _leaves[next++] = new Leaf
            {
                Transform = t,
                Sign = delta >= 0f ? 1f : -1f,
                ClosedLocalPosition = t.localPosition,
                ClosedLocalScale = t.localScale,
                MeshCenterX = meshCenterX,
                MeshWidth = meshWidth,
            };
        }
    }

    /// <summary>
    /// Drives the leaves toward <paramref name="open"/>. Safe to call mid-motion (the travel reverses from where it
    /// is). <paramref name="immediate"/> snaps, for a late joiner adopting the replicated state.
    /// </summary>
    public void SetOpen(bool open, bool immediate)
    {
        bool changed = _open != open;
        _open = open;

        if (immediate)
        {
            _t = open ? 1f : 0f;
            ApplyPose();
            return;
        }

        if (changed)
            PlayDoorSfx(open);
    }

    void Update()
    {
        float target = _open ? 1f : 0f;
        if (_t == target)
            return;

        float duration = Mathf.Max(0.05f, _open ? openDuration : closeDuration);
        _t = Mathf.MoveTowards(_t, target, Time.unscaledDeltaTime / duration);
        ApplyPose();
    }

    void ApplyPose()
    {
        float travel = _t * openFraction;

        for (int i = 0; i < _leaves.Length; i++)
        {
            Leaf leaf = _leaves[i];
            if (leaf.Transform == null)
                continue;

            float closedScaleX = leaf.ClosedLocalScale.x;
            float closedCenterX = leaf.ClosedLocalPosition.x + closedScaleX * leaf.MeshCenterX;
            float closedHalfWidth = 0.5f * closedScaleX * leaf.MeshWidth;

            float scaleX;
            float centerX;
            if (retractIntoJamb)
            {
                // Outer edge stays parked in the jamb while the leaf narrows: reads as retracting into a pocket.
                float jambEdgeX = closedCenterX + leaf.Sign * closedHalfWidth;
                scaleX = closedScaleX * (1f - travel);
                centerX = jambEdgeX - leaf.Sign * 0.5f * scaleX * leaf.MeshWidth;
            }
            else
            {
                scaleX = closedScaleX;
                centerX = closedCenterX + leaf.Sign * travel * closedScaleX * leaf.MeshWidth;
            }

            Vector3 scale = leaf.ClosedLocalScale;
            scale.x = scaleX;
            leaf.Transform.localScale = scale;

            Vector3 position = leaf.ClosedLocalPosition;
            position.x = centerX - scaleX * leaf.MeshCenterX;
            leaf.Transform.localPosition = position;
        }
    }

    void PlayDoorSfx(bool opening)
    {
        AudioClip clip = opening ? doorOpenClip : doorCloseClip;
        if (clip == null)
            return;

        EnsureSfxSource();
        if (_sfx == null)
            return;

        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_sfx);
        _sfx.PlayOneShot(clip, Mathf.Max(0f, doorVolume));
    }

    void EnsureSfxSource()
    {
        if (_sfx != null)
            return;

        _sfx = GetComponent<AudioSource>();
        if (_sfx == null)
            _sfx = gameObject.AddComponent<AudioSource>();
        _sfx.playOnAwake = false;
        _sfx.loop = false;
        _sfx.spatialBlend = 1f;
        _sfx.minDistance = 1.5f;
        _sfx.maxDistance = 30f;
        _sfx.rolloffMode = AudioRolloffMode.Linear;
        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_sfx);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (doorOpenClip == null)
            doorOpenClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/General/ElevatorDoorOpen1.wav");
        if (doorCloseClip == null)
            doorCloseClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/General/ElevatorDoorClose1.wav");
    }
#endif
}
