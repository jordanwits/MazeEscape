using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class HingeInteractDoor : NetworkBehaviour
{
    [Header("Hinge")]
    [Tooltip("Transform that swivels (the hinge pivot). If empty, looks for a child named \"Hinge\".")]
    [SerializeField] Transform hinge;
    [Tooltip("Local Euler rotation when open, relative to the closed pose (default: -90° on local Y).")]
    [SerializeField] Vector3 openLocalEuler = new Vector3(0f, -90f, 0f);
    [Tooltip("How long the hinge takes to reach its target, in real-time seconds (not affected by Time.timeScale).")]
    [SerializeField] float moveDuration = 3.5f;
    [Header("Interaction")]
    [SerializeField] float interactMaxDistance = 5f;
    [Tooltip("When enabled, the door starts locked. Use a KeyItem to unlock; opening is a separate interact.")]
    [SerializeField] bool useKeyToUnlock;
    [Tooltip("After unlocking with a key, this many seconds must pass before the door can be opened (or the open prompt shown).")]
    [SerializeField] float openAfterUnlockDelay = 0.45f;
    [Header("Audio")]
    [SerializeField] AudioClip doorUnlockClip;
    [SerializeField] AudioClip doorOpenClip;
    [SerializeField, Range(0f, 1f)] float doorUnlockVolume = 0.75f;
    [SerializeField, Range(0f, 1f)] float doorOpenVolume = 0.75f;

    readonly NetworkVariable<bool> _isLocked = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<bool> _isOpen = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    [Tooltip("Server-only: becomes false the first time the door opens, so clients hide the 'open' UI prompt. Still interactable after.")]
    readonly NetworkVariable<bool> _showOpenInteractionPrompt = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    bool _isOpenOffline;
    bool _lockedOffline = true;
    bool _openPromptOffline = true;
    /// <summary>When unlocked with a key, <see cref="Time.unscaledTime"/> must pass this before opening (local, set on all peers at unlock).</summary>
    float _mayOpenUnlockedTime;
    Quaternion _closedLocalRotation;
    Coroutine _moveRoutine;
    AudioSource _sfx;
    static readonly Dictionary<AudioClip, AudioClip> s_reversedClipCache = new();
    static readonly Dictionary<ulong, HingeInteractDoor> s_registeredDoors = new();
    ulong _cachedDoorId;
    bool _hasCachedDoorId;
    Vector3 _identityHintPosition;

    public bool IsOpen => !IsSpawned ? _isOpenOffline : _isOpen.Value;
    public bool IsBusy => _moveRoutine != null;
    public float InteractMaxDistance => interactMaxDistance;
    public bool UseKeyToUnlock => useKeyToUnlock;
    public bool IsLocked => useKeyToUnlock && (!IsSpawned ? _lockedOffline : _isLocked.Value);
    public ulong DoorId
    {
        get
        {
            if (!_hasCachedDoorId)
            {
                _cachedDoorId = ComputeStableDoorId();
                _hasCachedDoorId = true;
            }

            return _cachedDoorId;
        }
    }
    public Vector3 IdentityHintPosition => _identityHintPosition;

    /// <summary>True while unlocked and the post-key delay has not finished (cannot open or show open prompt yet).</summary>
    public bool IsPostUnlockOpenDelayActive =>
        useKeyToUnlock
        && !IsLocked
        && Time.unscaledTime < _mayOpenUnlockedTime;

    /// <summary>False after the door has been opened at least once (stays off if closed again). Used for UI only.</summary>
    public bool ShowOpenInteractionPrompt => !IsSpawned ? _openPromptOffline : _showOpenInteractionPrompt.Value;

    void OnEnable()
    {
        CacheIdentityHint();
        s_registeredDoors[DoorId] = this;
    }

    void OnDisable()
    {
        if (s_registeredDoors.TryGetValue(DoorId, out HingeInteractDoor existing) && existing == this)
            s_registeredDoors.Remove(DoorId);
    }

    void Awake()
    {
        if (hinge == null)
            hinge = FindAutoAssignedHinge();

        if (hinge == null)
        {
            Debug.LogError($"{nameof(HingeInteractDoor)}: assign a {nameof(hinge)} transform.", this);
            enabled = false;
            return;
        }

        _closedLocalRotation = hinge.localRotation;
        _lockedOffline = useKeyToUnlock;
        _mayOpenUnlockedTime = 0f;
        EnsureSfxSource();
    }

    public static bool TryResolveForSync(ulong doorId, Vector3 hintPosition, out HingeInteractDoor door)
    {
        if (s_registeredDoors.TryGetValue(doorId, out door) && door != null)
            return true;

        return TryFindNearestRegistered(hintPosition, out door);
    }

    Transform FindAutoAssignedHinge()
    {
        Transform directChild = transform.Find("Hinge");
        if (directChild != null)
            return directChild;

        Transform descendant = FindNamedTransformInHierarchy(transform, "Hinge");
        if (descendant != null && descendant != transform)
            return descendant;

        Transform parent = transform.parent;
        if (parent == null)
            return null;

        Transform siblingOrCousin = FindNamedTransformInHierarchy(parent, "Hinge");
        if (siblingOrCousin != null && siblingOrCousin != transform)
            return siblingOrCousin;

        return null;
    }

    public override void OnNetworkSpawn()
    {
        _isOpen.OnValueChanged += OnIsOpenChanged;
        _isLocked.OnValueChanged += OnIsLockedChanged;
        if (IsServer)
        {
            if (!useKeyToUnlock)
                _isLocked.Value = false;
            else
                _isLocked.Value = true;
        }
        if (hinge == null)
            return;

        if (IsServer && _isOpen.Value)
            _showOpenInteractionPrompt.Value = false;

        StartMoveToState(_isOpen.Value, true);
    }

    public override void OnNetworkDespawn()
    {
        _isOpen.OnValueChanged -= OnIsOpenChanged;
        _isLocked.OnValueChanged -= OnIsLockedChanged;
    }

    void OnIsLockedChanged(bool previous, bool current)
    {
        if (!useKeyToUnlock)
            return;
        if (previous && !current)
        {
            _mayOpenUnlockedTime = Time.unscaledTime + Mathf.Max(0f, openAfterUnlockDelay);
            PlayDoorUnlockSfx();
        }
    }

    void OnIsOpenChanged(bool previous, bool current)
    {
        if (hinge == null)
            return;
        if (previous == current)
            return;

        PlayDoorOpenSfx(current);

        StartMoveToState(current, false);
    }

    public void TryRequestToggle(Vector3 interactorPosition)
    {
        if (hinge == null || IsBusy)
            return;

        if (IsLocked)
            return;

        if (!IsInInteractRange(interactorPosition))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool localOnly = nm == null || !nm.IsListening || !IsSpawned;

        if (localOnly)
        {
            if (!_isOpenOffline && useKeyToUnlock && Time.unscaledTime < _mayOpenUnlockedTime)
                return;
            bool wasOpen = _isOpenOffline;
            _isOpenOffline = !_isOpenOffline;
            if (wasOpen != _isOpenOffline)
                PlayDoorOpenSfx(_isOpenOffline);
            if (_isOpenOffline)
                _openPromptOffline = false;
            StartMoveToState(_isOpenOffline, false);
            return;
        }

        ToggleRequestServerRpc();
    }

    /// <summary>Server-only: clear lock only. Call after the player key was consumed by inventory code.</summary>
    public void ServerUnlockFromKey()
    {
        if (!IsServer)
            return;
        if (!useKeyToUnlock || !_isLocked.Value)
            return;
        if (IsBusy)
            return;
        _isLocked.Value = false;
    }

    /// <summary>Single-player / non-network: key was removed from inventory by the player; door stays closed until open interact.</summary>
    public void ApplyLocalUnlock()
    {
        if (!useKeyToUnlock || !_lockedOffline)
            return;
        if (IsBusy)
            return;
        _lockedOffline = false;
        _mayOpenUnlockedTime = Time.unscaledTime + Mathf.Max(0f, openAfterUnlockDelay);
        PlayDoorUnlockSfx();
    }

    public void ApplyProceduralRemoteUnlock()
    {
        if (IsSpawned || !useKeyToUnlock || !_lockedOffline || IsBusy)
            return;

        _lockedOffline = false;
        _mayOpenUnlockedTime = Time.unscaledTime + Mathf.Max(0f, openAfterUnlockDelay);
        PlayDoorUnlockSfx();
    }

    public void ApplyProceduralRemoteOpenState(bool open)
    {
        if (IsSpawned || hinge == null || IsBusy)
            return;

        if (useKeyToUnlock && _lockedOffline)
            return;

        if (_isOpenOffline == open)
            return;

        _isOpenOffline = open;
        if (_isOpenOffline)
            _openPromptOffline = false;

        PlayDoorOpenSfx(open);
        StartMoveToState(open, false);
    }

    public bool IsInInteractRange(Vector3 worldPosition)
    {
        float maxSqr = interactMaxDistance * interactMaxDistance;
        return (transform.position - worldPosition).sqrMagnitude <= maxSqr;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void ToggleRequestServerRpc(RpcParams rpcParams = default)
    {
        if (IsBusy)
            return;

        ulong senderId = rpcParams.Receive.SenderClientId;
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out NetworkClient client)
            || client.PlayerObject == null)
        {
            return;
        }

        if (!IsInInteractRange(client.PlayerObject.transform.position))
            return;

        if (!_isOpen.Value)
        {
            if (useKeyToUnlock && Time.unscaledTime < _mayOpenUnlockedTime)
                return;
            _isOpen.Value = true;
            _showOpenInteractionPrompt.Value = false;
        }
        else
        {
            _isOpen.Value = false;
        }
    }

    Quaternion TargetLocalRotationForState(bool open)
    {
        return open
            ? _closedLocalRotation * Quaternion.Euler(openLocalEuler)
            : _closedLocalRotation;
    }

    void StartMoveToState(bool open, bool immediate)
    {
        if (hinge == null)
            return;

        if (_moveRoutine != null)
        {
            StopCoroutine(_moveRoutine);
            _moveRoutine = null;
        }

        Quaternion end = TargetLocalRotationForState(open);
        if (immediate)
        {
            hinge.localRotation = end;
            return;
        }

        _moveRoutine = StartCoroutine(MoveRoutine(end));
    }

    IEnumerator MoveRoutine(Quaternion end)
    {
        Quaternion start = hinge.localRotation;
        float totalDeg = Quaternion.Angle(start, end);
        if (totalDeg < 0.01f)
        {
            hinge.localRotation = end;
            _moveRoutine = null;
            yield break;
        }

        float d = Mathf.Max(0.01f, moveDuration);
        float degPerSecond = totalDeg / d;

        while (Quaternion.Angle(hinge.localRotation, end) > 0.05f)
        {
            float step = degPerSecond * Time.unscaledDeltaTime;
            hinge.localRotation = Quaternion.RotateTowards(hinge.localRotation, end, step);
            yield return null;
        }

        hinge.localRotation = end;
        _moveRoutine = null;
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
        _sfx.minDistance = 0.5f;
        _sfx.maxDistance = 30f;
        _sfx.rolloffMode = AudioRolloffMode.Linear;
        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_sfx);
    }

    void PlayDoorUnlockSfx()
    {
        if (doorUnlockClip == null)
            return;
        EnsureSfxSource();
        if (_sfx == null)
            return;
        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_sfx);
        _sfx.PlayOneShot(doorUnlockClip, Mathf.Max(0f, doorUnlockVolume));
    }

    /// <param name="opening">True when swinging toward open, false when closing (uses a reversed runtime copy of the open clip).</param>
    void PlayDoorOpenSfx(bool opening)
    {
        AudioClip clip = opening ? doorOpenClip : GetOrCreateReversedClip(doorOpenClip);
        if (clip == null)
            clip = doorOpenClip;
        if (clip == null)
            return;
        EnsureSfxSource();
        if (_sfx == null)
            return;
        if (GameAudioManager.Instance != null)
            GameAudioManager.RouteSfxSource(_sfx);
        _sfx.pitch = 1f;
        _sfx.PlayOneShot(clip, Mathf.Max(0f, doorOpenVolume));
    }

    static AudioClip GetOrCreateReversedClip(AudioClip source)
    {
        if (source == null)
            return null;

        if (s_reversedClipCache.TryGetValue(source, out AudioClip cached) && cached != null)
            return cached;

        if (source.loadState == AudioDataLoadState.Unloaded)
            source.LoadAudioData();
        if (source.loadState != AudioDataLoadState.Loaded)
            return null;

        int channels = Mathf.Max(1, source.channels);
        int sampleFrames = source.samples;
        if (sampleFrames <= 0)
            return null;

        float[] sourceData = new float[sampleFrames * channels];
        if (!source.GetData(sourceData, 0))
            return null;

        float[] reversedData = new float[sourceData.Length];
        for (int frame = 0; frame < sampleFrames; frame++)
        {
            int srcFrame = sampleFrames - 1 - frame;
            int dstIndex = frame * channels;
            int srcIndex = srcFrame * channels;
            for (int channel = 0; channel < channels; channel++)
                reversedData[dstIndex + channel] = sourceData[srcIndex + channel];
        }

        AudioClip reversed = AudioClip.Create(
            source.name + "_ReversedRuntime",
            sampleFrames,
            channels,
            source.frequency,
            false);
        reversed.SetData(reversedData, 0);
        s_reversedClipCache[source] = reversed;
        return reversed;
    }

    void CacheIdentityHint()
    {
        _identityHintPosition = transform.position;
    }

    ulong ComputeStableDoorId()
    {
        StringBuilder builder = new StringBuilder();
        builder.Append(gameObject.scene.buildIndex);
        builder.Append('|');
        builder.Append(gameObject.scene.name);

        Stack<Transform> hierarchy = new Stack<Transform>();
        Transform current = transform;
        while (current != null)
        {
            hierarchy.Push(current);
            current = current.parent;
        }

        while (hierarchy.Count > 0)
        {
            Transform next = hierarchy.Pop();
            builder.Append('/');
            builder.Append(next.name);
            builder.Append('[');
            builder.Append(next.GetSiblingIndex());
            builder.Append(']');
        }

        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        ulong hash = fnvOffset;
        string key = builder.ToString();
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= fnvPrime;
        }

        return hash;
    }

    static bool TryFindNearestRegistered(Vector3 hintPosition, out HingeInteractDoor door)
    {
        const float maxMatchDistance = 8f;
        door = null;
        float bestDistanceSquared = maxMatchDistance * maxMatchDistance;

        foreach (HingeInteractDoor candidate in s_registeredDoors.Values)
        {
            if (candidate == null)
                continue;

            float distanceSquared = (candidate.transform.position - hintPosition).sqrMagnitude;
            if (distanceSquared > bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            door = candidate;
        }

        return door != null;
    }

    static Transform FindNamedTransformInHierarchy(Transform root, string targetName)
    {
        if (root == null || string.IsNullOrEmpty(targetName))
            return null;

        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
        {
            Transform candidate = transforms[i];
            if (candidate != null && candidate.name == targetName)
                return candidate;
        }

        return null;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (doorOpenClip == null)
            doorOpenClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/DoorOpen.wav");
        if (doorUnlockClip == null)
            doorUnlockClip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/SFX/Unlock.wav");
    }
#endif
}
