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
    [Tooltip(
        "If the hinge is modeled in the OPEN position in the prefab (common for placed doors), enable this. "
        + "The true closed rotation is derived so open/close and sound match the mesh. If wrong, your door will look like it opens when the script closes it.")]
    [SerializeField] bool hingeRestPoseIsOpen;
    [Tooltip("How long the hinge takes to reach its target, in real-time seconds (not affected by Time.timeScale).")]
    [SerializeField] float moveDuration = 3.5f;
    [Header("Interaction")]
    [SerializeField] float interactMaxDistance = 5f;
    [Tooltip("When enabled, the door starts locked. Use a KeyItem to unlock; opening is a separate interact.")]
    [SerializeField] bool useKeyToUnlock;
    [Tooltip(
        "If Use Key To Unlock is on: when true, the door spawns unlocked (for jail cells). "
        + "Players can open/close freely until the Jailor seals it. Leave false for normal key doors that start locked.")]
    [SerializeField] bool jailCellStartUnlocked;
    [Tooltip("After unlocking with a key, this many seconds must pass before the door can be opened (or the open prompt shown).")]
    [SerializeField] float openAfterUnlockDelay = 0.45f;
    [Header("Audio")]
    [SerializeField] AudioClip doorUnlockClip;
    [SerializeField] AudioClip doorOpenClip;
    [Tooltip(
        "If set, this clip plays when the door closes. If empty, closing uses a reversed copy of Door Open Clip (Door B / start room can stay on that behavior). "
        + "Use on the jail door when you want a custom open clip without reversing it for close.")]
    [SerializeField] AudioClip doorCloseClip;
    [SerializeField, Range(0f, 1f)] float doorUnlockVolume = 0.75f;
    [SerializeField, Range(0f, 1f)] float doorOpenVolume = 0.75f;
    [Header("Double door (optional)")]
    [Tooltip(
        "The other door leaf. Assign both ways (A → B, B → A). One interact opens or closes both. "
        + "Each leaf keeps its own Hinge and Open Local Euler so pivots and swing directions stay independent.")]
    [SerializeField] HingeInteractDoor pairedLeaf;

    /// <summary>Other door leaf when this hinge is one half of a double door.</summary>
    public HingeInteractDoor PairedLeaf => pairedLeaf;

    [Tooltip("Optional: implements IHingeCloseValidator to block closing until conditions are met (e.g. maze exit elevator).")]
    [SerializeField] MonoBehaviour optionalCloseValidator;

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
    bool _jailorIncomingPairCall;
    bool _jailorCloseIncomingPairCall;
    bool _skipProceduralOpenPair;
    IHingeCloseValidator _runtimeCloseValidator;

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

    /// <summary>Server setup (e.g. <see cref="ElevatorFinishController"/>) assigns a close gate without inspector wiring on the door prefab.</summary>
    public void AssignRuntimeCloseValidator(IHingeCloseValidator validator) =>
        _runtimeCloseValidator = validator;

    /// <summary>Server-only: procedural / offline close path; validates and invokes <see cref="IHingeCloseValidator.ServerOnCloseAuthorized"/> when allowed.</summary>
    public bool ServerValidateProceduralClose(ulong senderClientId) =>
        ServerInvokeCloseValidator(closing: true, senderClientId);

    IHingeCloseValidator ResolveCloseValidator() =>
        _runtimeCloseValidator ?? optionalCloseValidator as IHingeCloseValidator;

    public bool TryGetElevatorFinishController(out ElevatorFinishController finish)
    {
        finish = ResolveCloseValidator() as ElevatorFinishController;
        return finish != null && finish.IsSpawned;
    }

    bool ServerInvokeCloseValidator(bool closing, ulong senderClientId)
    {
        if (!closing)
            return true;

        IHingeCloseValidator validator = ResolveCloseValidator();
        if (validator == null)
            return true;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return false;

        if (!validator.ServerValidateClose(this, senderClientId))
            return false;

        validator.ServerOnCloseAuthorized(this, senderClientId);
        return true;
    }

    /// <summary>
    /// Raised after the cell is unlocked with a player key (<see cref="ServerUnlockFromKey"/>, <see cref="ApplyLocalUnlock"/>, <see cref="ApplyProceduralRemoteUnlock"/>).
    /// Not raised when the Jailor forces the door open. Used to clear <see cref="NetworkPlayerAvatar.IsSealedInJailCell"/> for occupants.
    /// </summary>
    public event System.Action<HingeInteractDoor> OnJailUnlockedByPlayerKey;

    /// <summary>True while unlocked and the post-key delay has not finished (cannot open or show open prompt yet).</summary>
    public bool IsPostUnlockOpenDelayActive =>
        useKeyToUnlock
        && !IsLocked
        && Time.unscaledTime < _mayOpenUnlockedTime;

    /// <summary>False after the door has been opened at least once (stays off if closed again). Used for UI only.</summary>
    public bool ShowOpenInteractionPrompt => !IsSpawned ? _openPromptOffline : _showOpenInteractionPrompt.Value;

    /// <summary>Server / offline: jailor opens the door before a drop (master key). Ignores interact range.</summary>
    public void ServerJailorOpenForEntry()
    {
        ServerJailorOpenForEntryCore();
        if (pairedLeaf != null && !_jailorIncomingPairCall)
            pairedLeaf.SyncMateJailorOpenForEntry();
    }

    void SyncMateJailorOpenForEntry()
    {
        _jailorIncomingPairCall = true;
        try
        {
            ServerJailorOpenForEntryCore();
        }
        finally
        {
            _jailorIncomingPairCall = false;
        }
    }

    void ServerJailorOpenForEntryCore()
    {
        if (hinge == null)
            return;

        bool treatAsServer = !IsSpawned || IsServer;
        if (!treatAsServer)
            return;

        if (IsSpawned && IsServer)
        {
            // Must not return when IsBusy: JailorAI waits for JailorDoorIsOpenAndIdle (open and not busy).
            StopDoorMoveRoutine();
            _isLocked.Value = false;
            _mayOpenUnlockedTime = 0f;
            if (!_isOpen.Value)
                _isOpen.Value = true;
            else
                StartMoveToState(true, true);
            return;
        }

        if (IsBusy)
            StopDoorMoveRoutine();
        _lockedOffline = false;
        _mayOpenUnlockedTime = 0f;
        if (_isOpenOffline)
        {
            StartMoveToState(true, true);
            return;
        }

        _isOpenOffline = true;
        if (_isOpenOffline)
            _openPromptOffline = false;
        PlayDoorOpenSfx(true);
        StartMoveToState(true, false);
    }

    /// <summary>
    /// Server / offline: called only from <see cref="JailCellDoorTripwire"/> when the Jailor seals the cell.
    /// Applies lock (requires key) before closing so the door cannot be toggled mid-close. Player interaction uses
    /// <see cref="ToggleRequestServerRpc"/>, which only changes open state and never locks.
    /// </summary>
    public void ServerJailorCloseAndLock()
    {
        ServerJailorCloseAndLockCore();
        if (pairedLeaf != null && !_jailorCloseIncomingPairCall)
            pairedLeaf.SyncMateJailorCloseAndLock();
    }

    void SyncMateJailorCloseAndLock()
    {
        _jailorCloseIncomingPairCall = true;
        try
        {
            ServerJailorCloseAndLockCore();
        }
        finally
        {
            _jailorCloseIncomingPairCall = false;
        }
    }

    void ServerJailorCloseAndLockCore()
    {
        if (hinge == null)
            return;

        bool treatAsServer = !IsSpawned || IsServer;
        if (!treatAsServer)
            return;

        if (IsSpawned && IsServer)
        {
            // Lock first so TryRequestToggle sees IsLocked before the close finishes (tripwire-only path).
            if (useKeyToUnlock)
                _isLocked.Value = true;
            // Do not bail when IsBusy: the door may still be swinging open after JailorAI drops the player early.
            // Flipping _isOpen false triggers OnIsOpenChanged → StartMoveToState, which stops the open coroutine and closes.
            if (_isOpen.Value)
                _isOpen.Value = false;
            return;
        }

        if (useKeyToUnlock)
            _lockedOffline = true;
        if (_isOpenOffline)
        {
            _isOpenOffline = false;
            PlayDoorOpenSfx(false);
            StartMoveToState(false, false);
        }
    }

    /// <summary>Used by JailorAI to wait for hinge motion after requesting open.</summary>
    public bool JailorDoorIsOpenAndIdle() => IsOpen && !IsBusy;

    /// <summary>Used by JailorAI to wait for hinge motion after requesting close.</summary>
    public bool JailorDoorIsClosedAndIdle() => !IsOpen && !IsBusy;

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

        Quaternion restLocal = hinge.localRotation;
        Quaternion openDelta = Quaternion.Euler(openLocalEuler);
        _closedLocalRotation = hingeRestPoseIsOpen
            ? restLocal * Quaternion.Inverse(openDelta)
            : restLocal;

        _lockedOffline = useKeyToUnlock && !jailCellStartUnlocked;
        _mayOpenUnlockedTime = 0f;
        // Jail cells: unlocked and left open until the Jailor seals (must match logical _isOpen / NetworkVariable).
        if (useKeyToUnlock && jailCellStartUnlocked)
        {
            _isOpenOffline = true;
            _openPromptOffline = false;
        }

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
        // Apply server defaults before subscribing so spawn does not fire unlock/open sounds or tween from wrong pose.
        if (IsServer)
        {
            if (!useKeyToUnlock)
                _isLocked.Value = false;
            else
                _isLocked.Value = !jailCellStartUnlocked;

            // _isOpen NetworkVariable defaults to false; jail cells start open (matches Jail Cell Start Unlocked).
            if (useKeyToUnlock && jailCellStartUnlocked)
                _isOpen.Value = true;
        }

        _isOpen.OnValueChanged += OnIsOpenChanged;
        _isLocked.OnValueChanged += OnIsLockedChanged;

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
            SyncPairedLeafLocalOpen(_isOpenOffline, playSfx: false);
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
        ServerUnlockThisLeafOnlyFromKey();
        if (pairedLeaf != null && pairedLeaf.useKeyToUnlock && pairedLeaf.IsLocked && !pairedLeaf.IsBusy)
            pairedLeaf.ServerUnlockThisLeafOnlyFromKey();
    }

    void ServerUnlockThisLeafOnlyFromKey()
    {
        if (!IsServer || !useKeyToUnlock || !_isLocked.Value)
            return;
        _isLocked.Value = false;
        OnJailUnlockedByPlayerKey?.Invoke(this);
    }

    /// <summary>Single-player / non-network: key was removed from inventory by the player; door stays closed until open interact.</summary>
    public void ApplyLocalUnlock()
    {
        if (!useKeyToUnlock || !_lockedOffline)
            return;
        if (IsBusy)
            return;
        ApplyLocalUnlockThisLeafOnly();
        if (pairedLeaf != null && pairedLeaf.useKeyToUnlock && pairedLeaf.IsLocked && !pairedLeaf.IsBusy)
            pairedLeaf.ApplyLocalUnlockThisLeafOnly();
    }

    void ApplyLocalUnlockThisLeafOnly()
    {
        if (!useKeyToUnlock || !_lockedOffline)
            return;
        _lockedOffline = false;
        _mayOpenUnlockedTime = Time.unscaledTime + Mathf.Max(0f, openAfterUnlockDelay);
        PlayDoorUnlockSfx();
        OnJailUnlockedByPlayerKey?.Invoke(this);
    }

    public void ApplyProceduralRemoteUnlock()
    {
        if (IsSpawned || !useKeyToUnlock || !_lockedOffline || IsBusy)
            return;

        ApplyProceduralRemoteUnlockThisLeafOnly();
        if (pairedLeaf != null && !pairedLeaf.IsSpawned && pairedLeaf.useKeyToUnlock && pairedLeaf.IsLocked && !pairedLeaf.IsBusy)
            pairedLeaf.ApplyProceduralRemoteUnlockThisLeafOnly();
    }

    void ApplyProceduralRemoteUnlockThisLeafOnly()
    {
        if (IsSpawned || !useKeyToUnlock || !_lockedOffline)
            return;
        _lockedOffline = false;
        _mayOpenUnlockedTime = Time.unscaledTime + Mathf.Max(0f, openAfterUnlockDelay);
        PlayDoorUnlockSfx();
        OnJailUnlockedByPlayerKey?.Invoke(this);
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

        if (!_skipProceduralOpenPair && pairedLeaf != null && !pairedLeaf.IsSpawned && !pairedLeaf.IsBusy)
        {
            pairedLeaf._skipProceduralOpenPair = true;
            try
            {
                pairedLeaf.ApplyProceduralRemoteOpenState(open);
            }
            finally
            {
                pairedLeaf._skipProceduralOpenPair = false;
            }
        }
    }

    public bool IsInInteractRange(Vector3 worldPosition)
    {
        float maxSqr = interactMaxDistance * interactMaxDistance;
        if ((transform.position - worldPosition).sqrMagnitude <= maxSqr)
            return true;
        if (pairedLeaf != null)
        {
            float maxSqrPair = pairedLeaf.InteractMaxDistance * pairedLeaf.InteractMaxDistance;
            if ((pairedLeaf.transform.position - worldPosition).sqrMagnitude <= maxSqrPair)
                return true;
        }

        return false;
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
            // Player closing an open door: never lock (only JailCellDoorTripwire → ServerJailorCloseAndLock locks).
            if (!ServerInvokeCloseValidator(closing: true, senderId))
                return;
            _isOpen.Value = false;
        }

        if (pairedLeaf != null && pairedLeaf.IsSpawned)
            pairedLeaf.ServerApplyOpenFromPairedLeaf(_isOpen.Value);
    }

    /// <summary>Server only: double-door mate already toggled; mirror open state without a second RPC.</summary>
    void ServerApplyOpenFromPairedLeaf(bool open)
    {
        if (!IsServer || !IsSpawned || hinge == null)
            return;
        if (_isOpen.Value == open)
            return;
        if (open)
        {
            if (useKeyToUnlock && Time.unscaledTime < _mayOpenUnlockedTime)
                return;
            _showOpenInteractionPrompt.Value = false;
        }

        _isOpen.Value = open;
    }

    void SyncPairedLeafLocalOpen(bool open, bool playSfx)
    {
        if (pairedLeaf == null || pairedLeaf.hinge == null)
            return;
        if (pairedLeaf._isOpenOffline == open)
            return;

        pairedLeaf._isOpenOffline = open;
        if (open)
            pairedLeaf._openPromptOffline = false;
        if (playSfx)
            pairedLeaf.PlayDoorOpenSfx(open);
        pairedLeaf.StartMoveToState(open, false);
    }

    Quaternion TargetLocalRotationForState(bool open)
    {
        return open
            ? _closedLocalRotation * Quaternion.Euler(openLocalEuler)
            : _closedLocalRotation;
    }

    void StopDoorMoveRoutine()
    {
        if (_moveRoutine != null)
        {
            StopCoroutine(_moveRoutine);
            _moveRoutine = null;
        }
    }

    void StartMoveToState(bool open, bool immediate)
    {
        if (hinge == null)
            return;

        StopDoorMoveRoutine();

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

    /// <param name="opening">True when swinging toward open, false when closing.</param>
    void PlayDoorOpenSfx(bool opening)
    {
        AudioClip clip;
        if (opening)
            clip = doorOpenClip;
        else if (doorCloseClip != null)
            clip = doorCloseClip;
        else
            clip = GetOrCreateReversedClip(doorOpenClip);

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
        if (pairedLeaf == this)
        {
            Debug.LogWarning($"{nameof(HingeInteractDoor)} on '{name}': {nameof(pairedLeaf)} cannot reference itself.", this);
        }
        else if (pairedLeaf != null && pairedLeaf.pairedLeaf != this)
        {
            Debug.LogWarning(
                $"{nameof(HingeInteractDoor)} on '{name}': link the pair both ways (the other leaf's {nameof(pairedLeaf)} should reference this object).",
                this);
        }
    }
#endif
}
