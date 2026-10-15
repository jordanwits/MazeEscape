using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Server-spawned sync object for the maze exit elevator: occupancy counts, close gating, synchronized advance to the next maze scene when doors finish closing,
/// or return to menu when there is no configured next section (e.g. last level).
/// Handles both cab styles — the dungeon's pair of <see cref="HingeInteractDoor"/> leaves, and the Severance cab's
/// <see cref="ElevatorSlidingDoors"/> driven from its two <see cref="ElevatorCallButton"/> pads.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class ElevatorFinishController : NetworkBehaviour, IHingeCloseValidator
{
    [SerializeField] BoxCollider interiorVolume;
    [Tooltip("Optional: rigidbody added when volume needs trigger events; if null, occupancy uses bounds checks only.")]
    [SerializeField] bool addKinematicRigidbodyToVolume = true;
    [Tooltip(
        "Last section of the run: closing the doors ends the game and returns everyone to the main menu instead of "
        + "loading the next maze scene. Leave off for sections that advance.")]
    [SerializeField] bool endRunInsteadOfAdvancing;
    [Tooltip(
        "The cab starts locked and its outside call pad does nothing until somebody presses it while carrying a "
        + "ClownKeyItem, which is consumed. Authored on the finish PIECE (Level02's CarnivalFinish1); every peer "
        + "copies it off that piece in TryBindToFinishPiece. Once opened the cab stays unlocked for the rest of "
        + "the section.")]
    [SerializeField] bool requiresClownKeyToOpen;

    readonly NetworkVariable<int> _livingInside = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<int> _livingRequired = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>Replicated state of the sliding cab doors. The leaves themselves are local maze-piece geometry on every peer.</summary>
    readonly NetworkVariable<bool> _doorsOpen = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// Set once somebody has spent a clown key on this cab. Replicated because every peer draws its own pad
    /// prompt off it — the lock is a property of the cab, not of whoever happens to be standing at it.
    /// </summary>
    readonly NetworkVariable<bool> _clownKeyLockOpened = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>Offline mirror of <see cref="_clownKeyLockOpened"/> (play mode with no session running).</summary>
    bool _clownKeyLockOpenedOffline;

    /// <summary>
    /// Held by a living boss (Level03's <see cref="MutantAI"/>): engaged when he spawns in the exit hall, released
    /// only by his death. Set at runtime by the boss rather than authored on the piece, so a level where the boss
    /// never spawned can never be soft-locked. Replicated for the pad prompt on every peer.
    /// </summary>
    readonly NetworkVariable<bool> _bossLockEngaged = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>Offline mirror of <see cref="_bossLockEngaged"/>.</summary>
    bool _bossLockEngagedOffline;

    HingeInteractDoor _doorA;
    HingeInteractDoor _doorB;
    ElevatorSlidingDoors _slidingDoors;
    ElevatorCallButton[] _callButtons;
    bool _pendingSceneAfterDoorsIdle;
    /// <summary>Set once a close has been authorized: the run is committed and the pads stop answering.</summary>
    bool _runCommitted;
    bool _boundToFinishPiece;
    Coroutine _bindRoutine;

    public int LivingInsideDisplay => IsSpawned ? _livingInside.Value : 0;
    public int LivingRequiredDisplay => IsSpawned ? _livingRequired.Value : 0;

    /// <summary>
    /// Server-only: a close was authorized with every living player aboard and the doors are shutting, so this
    /// cab is taking the group out.
    /// </summary>
    bool ServerIsDeparting
    {
        get
        {
            if (!IsServer || !IsSpawned)
                return false;

            // Sliding cab: once committed the pads stop answering, so the doors cannot come open again.
            if (_runCommitted)
                return true;

            // Hinge cab: the authorized close only arms the advance. Each leaf is an ordinary door that can be
            // swung open again, so the cab is only really leaving while every leaf is shut or shutting —
            // otherwise a group that walked back out could never be called wiped.
            return _pendingSceneAfterDoorsIdle
                && _doorA != null && !_doorA.IsOpen
                && (_doorB == null || !_doorB.IsOpen);
        }
    }

    /// <summary>
    /// Server-only: whether any exit cab in the level is already departing. Death is permanent for the section,
    /// but a cab that left with the living aboard brings everyone back in the next one, so
    /// <see cref="TeamWipeDirector"/> must not call a wipe on a death that lands during the departure.
    /// </summary>
    public static bool ServerAnyCabDeparting()
    {
        ElevatorFinishController[] cabs = FindObjectsByType<ElevatorFinishController>();
        for (int i = 0; i < cabs.Length; i++)
        {
            if (cabs[i] != null && cabs[i].ServerIsDeparting)
                return true;
        }

        return false;
    }

    /// <summary>No session at all (single-player play mode): this instance drives the elevator locally.</summary>
    static bool IsOfflineSession
    {
        get
        {
            NetworkManager nm = NetworkManager.Singleton;
            return nm == null || !nm.IsListening;
        }
    }

    /// <summary>Only the server decides, except with no session at all, where the local peer is all there is.</summary>
    bool HasElevatorAuthority => IsSpawned ? IsServer : IsOfflineSession;

    /// <summary>Whether this instance is the one the cab pads should be talking to.</summary>
    public bool ElevatorButtonsResponsive => _slidingDoors != null && (IsSpawned || IsOfflineSession);

    bool DoorsOpenState => IsSpawned ? _doorsOpen.Value : _slidingDoors != null && _slidingDoors.IsOpen;

    /// <summary>
    /// True while this cab still wants a clown key. Drives the pad's prompt on every peer; the pad stays
    /// pressable either way so a locked cab answers ("Locked") instead of reading as scenery, exactly as a
    /// keyed <see cref="HingeInteractDoor"/> does. The real gate is server-side in
    /// <see cref="ServerTryPassClownKeyLock"/>.
    /// </summary>
    public bool IsClownKeyLockEngaged =>
        requiresClownKeyToOpen && !(IsSpawned ? _clownKeyLockOpened.Value : _clownKeyLockOpenedOffline);

    /// <summary>True while a living boss holds this cab shut (see <see cref="ServerSetBossLock"/>).</summary>
    public bool IsBossLockEngaged => IsSpawned ? _bossLockEngaged.Value : _bossLockEngagedOffline;

    /// <summary>Server (or offline): the boss engages the lock on spawn and releases it on death.</summary>
    public void ServerSetBossLock(bool engaged)
    {
        if (IsSpawned)
        {
            if (IsServer)
                _bossLockEngaged.Value = engaged;
            return;
        }

        if (IsOfflineSession)
            _bossLockEngagedOffline = engaged;
    }

    public bool CanRequestDoorsOpen => ElevatorButtonsResponsive && !_runCommitted && !DoorsOpenState;

    public bool CanRequestDoorsClose => ElevatorButtonsResponsive && !_runCommitted && DoorsOpenState;

    void Awake()
    {
        if (interiorVolume == null)
            interiorVolume = GetComponent<BoxCollider>();

        if (interiorVolume != null)
            interiorVolume.isTrigger = true;

        if (addKinematicRigidbodyToVolume && interiorVolume != null)
        {
            Rigidbody body = interiorVolume.GetComponent<Rigidbody>();
            if (body == null)
                body = interiorVolume.gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
        }
    }

    void Start()
    {
        // With no session this in-piece copy is the elevator's brain. In a session it must stay inert: the client's
        // maze build makes its own copy of this object alongside the replicated one, and only the replicated one may
        // bind the pads (see ElevatorCallButton.AssignController).
        if (IsOfflineSession)
            BeginBindingToFinishPiece();
    }

    public override void OnNetworkSpawn()
    {
        // Bind doors on clients too so localized prompts can resolve this controller (counts replicate via Netvars).
        BeginBindingToFinishPiece();

        _doorsOpen.OnValueChanged += OnDoorsOpenChanged;
        if (_slidingDoors != null)
            _slidingDoors.SetOpen(_doorsOpen.Value, immediate: true);
    }

    public override void OnNetworkDespawn()
    {
        _doorsOpen.OnValueChanged -= OnDoorsOpenChanged;

        if (_bindRoutine != null)
        {
            StopCoroutine(_bindRoutine);
            _bindRoutine = null;
        }
    }

    /// <summary>
    /// Server-side, called by <see cref="ProceduralMazeCoordinator"/> on the freshly instantiated replica before it
    /// spawns: copies the settings that are authored per finish piece off that piece's own (never-spawned) copy.
    /// The cab volume is re-adopted from the same source on every peer in <see cref="TryBindToFinishPiece"/>; doing
    /// it here too matters because the server starts counting occupants in <see cref="FixedUpdate"/> the moment it
    /// spawns, and <see cref="endRunInsteadOfAdvancing"/> has no other route onto the replica at all.
    /// </summary>
    public void AdoptAuthoredSettingsFromPieceCopy(ElevatorFinishController pieceCopy)
    {
        if (pieceCopy == null || pieceCopy == this)
            return;

        endRunInsteadOfAdvancing = pieceCopy.endRunInsteadOfAdvancing;
        requiresClownKeyToOpen = pieceCopy.requiresClownKeyToOpen;

        if (interiorVolume != null && pieceCopy.interiorVolume != null)
        {
            interiorVolume.size = pieceCopy.interiorVolume.size;
            interiorVolume.center = pieceCopy.interiorVolume.center;
        }
    }

    void OnDoorsOpenChanged(bool previous, bool current)
    {
        if (_slidingDoors == null || previous == current)
            return;

        _slidingDoors.SetOpen(current, immediate: false);
    }

    /// <summary>
    /// Binds hinge leaves, sliding doors and call pads from the finish piece. A client can receive this spawn before
    /// its own maze build has placed the piece, so the lookup retries until the marker shows up instead of leaving the
    /// cab dead on that peer.
    /// </summary>
    void BeginBindingToFinishPiece()
    {
        if (_boundToFinishPiece || _bindRoutine != null)
            return;

        if (TryBindToFinishPiece())
            return;

        _bindRoutine = StartCoroutine(CoBindToFinishPiece());
    }

    IEnumerator CoBindToFinishPiece()
    {
        const float retryInterval = 0.5f;
        const float giveUpAfterSeconds = 60f;

        float waited = 0f;
        while (waited < giveUpAfterSeconds)
        {
            yield return new WaitForSecondsRealtime(retryInterval);
            waited += retryInterval;

            if (TryBindToFinishPiece())
            {
                _bindRoutine = null;
                yield break;
            }
        }

        _bindRoutine = null;
        Debug.LogWarning(
            "[ElevatorFinish] ElevatorFinishSpawnMarker not found (parent chain or nearest in scene); door gating will not work.",
            this);
    }

    bool TryBindToFinishPiece()
    {
        ElevatorFinishSpawnMarker marker = TryResolveSpawnMarkerForThisSync();
        if (marker == null)
            return false;

        HingeInteractDoor[] doors = CollectCabHingeDoors(marker);
        foreach (HingeInteractDoor d in doors)
            d.AssignRuntimeCloseValidator(this);

        if (HasElevatorAuthority)
            CacheDoorPairsForIdleCheck(doors);

        AdoptAuthoredPieceStateFromPieceCopy(marker);

        _slidingDoors = marker.GetComponentInChildren<ElevatorSlidingDoors>(true);

        ElevatorCallButton[] buttons = marker.GetComponentsInChildren<ElevatorCallButton>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] != null)
                buttons[i].AssignController(this);
        }
        // Kept so the press fan-out can find this peer's copy of the pad somebody else pushed.
        _callButtons = buttons;

        // A peer that binds late (maze built after this spawn arrived) has to adopt the state as it stands now.
        if (_slidingDoors != null && IsSpawned)
            _slidingDoors.SetOpen(_doorsOpen.Value, immediate: true);

        _boundToFinishPiece = true;
        return true;
    }

    /// <summary>
    /// The cab's own hinge leaves, and only those. Closing an elevator leaf ends the run, so every door this
    /// returns gets gated on "everyone aboard" — which is wrong for any other door that happens to live in the
    /// same finish piece. Level03's exit hall has an ordinary entry door 33m up the corridor; gating that one
    /// left a player unable to close it and staring at the elevator's occupancy count instead of a door prompt.
    /// A cab's leaves hang off the cab itself (3.5m out on the dungeon/carnival cabs), so proximity to this
    /// sync object separates them cleanly without needing anything authored on the doors.
    /// </summary>
    HingeInteractDoor[] CollectCabHingeDoors(ElevatorFinishSpawnMarker marker)
    {
        const float cabLeafRadius = 8f;

        HingeInteractDoor[] all = marker.GetComponentsInChildren<HingeInteractDoor>(true);
        List<HingeInteractDoor> cabDoors = new(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            HingeInteractDoor door = all[i];
            if (door == null)
                continue;
            if (Vector3.Distance(door.transform.position, transform.position) > cabLeafRadius)
                continue;

            cabDoors.Add(door);
        }

        return cabDoors.ToArray();
    }

    /// <summary>
    /// Every peer's replica — the server's included — is instantiated from the registered ElevatorFinishSync prefab,
    /// so it carries that prefab's defaults rather than the overrides authored on the finish piece. The piece each
    /// peer just built holds an inert copy with the authored values — take them from it so "am I aboard" and "does
    /// this cab want a key" mean the same thing on every peer.
    ///
    /// <see cref="requiresClownKeyToOpen"/> is copied here as well as in
    /// <see cref="AdoptAuthoredSettingsFromPieceCopy"/> (which only ever runs on the server, pre-spawn) because
    /// clients draw the pad's locked/unlock prompt off it. <see cref="endRunInsteadOfAdvancing"/> deliberately is
    /// not: only the server ever reads that one.
    /// </summary>
    void AdoptAuthoredPieceStateFromPieceCopy(ElevatorFinishSpawnMarker marker)
    {
        ElevatorFinishController[] copies = marker.GetComponentsInChildren<ElevatorFinishController>(true);
        for (int i = 0; i < copies.Length; i++)
        {
            ElevatorFinishController copy = copies[i];
            if (copy == null || copy == this || copy.IsSpawned)
                continue;

            requiresClownKeyToOpen = copy.requiresClownKeyToOpen;

            if (interiorVolume != null && copy.interiorVolume != null)
            {
                interiorVolume.size = copy.interiorVolume.size;
                interiorVolume.center = copy.interiorVolume.center;
            }

            return;
        }
    }

    void CacheDoorPairsForIdleCheck(HingeInteractDoor[] doors)
    {
        if (doors.Length >= 2)
        {
            _doorA = doors[0];
            _doorB = doors[1];
        }
        else if (doors.Length == 1 && doors[0].PairedLeaf != null)
        {
            _doorA = doors[0];
            _doorB = doors[0].PairedLeaf;
        }
        else
        {
            _doorA = doors.Length > 0 ? doors[0] : null;
            _doorB = null;
        }
    }


    /// <summary>
    /// Sync may be a scene root (cannot parent under MG_Finish without Netcode reparent errors). Prefer hierarchy; otherwise nearest marker in the same scene by anchor position.
    /// </summary>
    ElevatorFinishSpawnMarker TryResolveSpawnMarkerForThisSync()
    {
        ElevatorFinishSpawnMarker fromParents = GetComponentInParent<ElevatorFinishSpawnMarker>();
        if (fromParents != null)
            return fromParents;

        Scene ourScene = gameObject.scene;
        if (!ourScene.IsValid())
            return null;

        ElevatorFinishSpawnMarker best = null;
        float bestSqr = 900f; // max 30m from anchor — one finish room per generated maze
        Vector3 here = transform.position;

        ElevatorFinishSpawnMarker[] all = FindObjectsByType<ElevatorFinishSpawnMarker>(FindObjectsInactive.Include);
        for (int i = 0; i < all.Length; i++)
        {
            ElevatorFinishSpawnMarker m = all[i];
            if (m == null || m.gameObject.scene != ourScene)
                continue;

            Vector3 p = m.transform.position;
            float sqr = (p - here).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = m;
            }
        }

        return best;
    }

    void FixedUpdate()
    {
        if (!IsServer || !IsSpawned || interiorVolume == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
            return;

        Bounds b = interiorVolume.bounds;
        int required = 0;
        int inside = 0;

        foreach (System.Collections.Generic.KeyValuePair<ulong, NetworkClient> pair in nm.ConnectedClients)
        {
            if (pair.Value.PlayerObject == null)
                continue;

            PlayerHealth health = pair.Value.PlayerObject.GetComponent<PlayerHealth>();
            if (health == null || health.IsDead)
                continue;

            required++;

            Vector3 p = pair.Value.PlayerObject.transform.position;
            if (ServerIsPlayerPositionInsideVolume(b, p))
                inside++;
        }

        if (_livingInside.Value != inside)
            _livingInside.Value = inside;
        if (_livingRequired.Value != required)
            _livingRequired.Value = required;
    }

    /// <summary>
    /// Server: the living players inside the cab right now. Recounted at departure rather than read from the
    /// FixedUpdate counters — a hinge cab's leaves can be reopened and a death during departure is allowed.
    /// </summary>
    System.Collections.Generic.List<ulong> ServerCollectPlayersAboard()
    {
        var aboard = new System.Collections.Generic.List<ulong>();
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || interiorVolume == null)
            return aboard;

        Bounds b = interiorVolume.bounds;
        foreach (System.Collections.Generic.KeyValuePair<ulong, NetworkClient> pair in nm.ConnectedClients)
        {
            NetworkObject playerObject = pair.Value.PlayerObject;
            if (playerObject == null
                || !playerObject.TryGetComponent(out PlayerHealth health)
                || health.IsDead
                || !ServerIsPlayerPositionInsideVolume(b, playerObject.transform.position))
                continue;

            aboard.Add(pair.Key);
        }

        return aboard;
    }

    static bool ServerIsPlayerPositionInsideVolume(Bounds b, Vector3 feetPosition)
    {
        Vector3 sample = feetPosition + Vector3.up * 0.85f;
        return b.Contains(sample);
    }

    /// <summary>True when a player standing at <paramref name="feetPosition"/> counts as aboard the cab.</summary>
    public bool IsPositionInsideInterior(Vector3 feetPosition) =>
        interiorVolume != null && ServerIsPlayerPositionInsideVolume(interiorVolume.bounds, feetPosition);

    /// <summary>
    /// Occupancy for the local "close the doors" prompt. With no session there is only the local player, so the
    /// replicated counters are not running and the caller's own position is the whole answer.
    /// </summary>
    public void GetOccupancyForPrompt(Vector3 localPlayerFeetPosition, out int inside, out int required)
    {
        if (IsSpawned)
        {
            inside = _livingInside.Value;
            required = _livingRequired.Value;
            return;
        }

        required = 1;
        inside = IsPositionInsideInterior(localPlayerFeetPosition) ? 1 : 0;
    }

    // ---- sliding cab: call pads ----

    /// <summary>
    /// Outside pad: call the elevator. Server (or the local peer offline) decides. A cab that still wants a
    /// clown key refuses unless <paramref name="interactor"/> is carrying one, which is then spent.
    /// </summary>
    public void RequestDoorsOpenFromButton(Vector3 interactorPosition, PlayerController interactor)
    {
        if (_slidingDoors == null)
            return;

        if (!IsSpawned)
        {
            if (!IsOfflineSession)
                return;
            if (_runCommitted || _slidingDoors.IsOpen)
                return;
            if (IsBossLockEngaged)
                return;
            // Offline the presser is the only player there is, so their own hotbar is the whole answer.
            if (IsClownKeyLockEngaged)
            {
                if (interactor == null || !interactor.TryConsumeLocalClownKeyForElevator())
                    return;

                _clownKeyLockOpenedOffline = true;
            }

            _slidingDoors.SetOpen(true, immediate: false);
            return;
        }

        RequestDoorsOpenRpc();
    }

    /// <summary>Inside pad: send the elevator away. Only lands with every living player aboard.</summary>
    public void RequestDoorsCloseFromButton(Vector3 interactorPosition)
    {
        if (_slidingDoors == null)
            return;

        if (!IsSpawned)
        {
            if (!IsOfflineSession)
                return;
            if (_runCommitted || !_slidingDoors.IsOpen)
                return;
            if (!IsPositionInsideInterior(interactorPosition))
                return;

            AuthorizeDoorsClose();
            return;
        }

        RequestDoorsCloseRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RequestDoorsOpenRpc(RpcParams rpcParams = default)
    {
        if (_slidingDoors == null || _runCommitted || _doorsOpen.Value)
            return;

        if (!ServerTryGetSenderFeetPosition(rpcParams.Receive.SenderClientId, out Vector3 feet))
            return;
        if (interiorVolume == null || interiorVolume.bounds.SqrDistance(feet) > 36f)
            return;
        // Sealed while the boss lives — checked before the clown key so a key is never spent on a sealed cab.
        if (_bossLockEngaged.Value)
            return;
        // The key is spent LAST, after every other reason to refuse has been ruled out, so a press that could
        // not have opened the doors anyway never eats it.
        if (!ServerTryPassClownKeyLock(rpcParams.Receive.SenderClientId))
            return;

        _doorsOpen.Value = true;
        FanOutCallButtonPress(ElevatorCallButton.ElevatorButtonAction.OpenDoors, rpcParams.Receive.SenderClientId);
    }

    /// <summary>
    /// Server-only clown-key gate. Returns true when the cab does not want a key (or has already been opened
    /// with one), otherwise consumes a <see cref="ClownKeyItem"/> from the presser's hotbar and unlatches the
    /// cab for everybody. Read-then-consume happens in this one server call, so two presses arriving together
    /// cannot both pass and spend two keys.
    /// </summary>
    bool ServerTryPassClownKeyLock(ulong presserClientId)
    {
        if (!IsClownKeyLockEngaged)
            return true;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null
            || !nm.ConnectedClients.TryGetValue(presserClientId, out NetworkClient client)
            || client.PlayerObject == null
            || !client.PlayerObject.TryGetComponent(out NetworkPlayerInventory inventory))
        {
            return false;
        }

        if (!inventory.ServerHasClownKeyItem() || !inventory.ServerTryConsumeClownKeyItem())
            return false;

        _clownKeyLockOpened.Value = true;
        return true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void RequestDoorsCloseRpc(RpcParams rpcParams = default)
    {
        if (_slidingDoors == null || _runCommitted || !_doorsOpen.Value)
            return;

        if (!ServerTryGetSenderFeetPosition(rpcParams.Receive.SenderClientId, out Vector3 feet))
            return;
        if (!IsPositionInsideInterior(feet))
            return;
        // Nobody gets left behind: every living player has to be aboard.
        if (_livingRequired.Value <= 0 || _livingInside.Value != _livingRequired.Value)
            return;

        AuthorizeDoorsClose();
        FanOutCallButtonPress(ElevatorCallButton.ElevatorButtonAction.CloseDoors, rpcParams.Receive.SenderClientId);
    }

    /// <summary>
    /// The pad's press feedback for everyone except the person who pressed it (they already ran it locally the
    /// moment they hit interact). Server-only; fired from the two pad entry points once the press is validated.
    /// </summary>
    void FanOutCallButtonPress(ElevatorCallButton.ElevatorButtonAction action, ulong presserClientId)
    {
        if (!IsSpawned || !IsServer)
            return;

        PlayCallButtonPressRpc((byte)action, RpcTarget.Not(presserClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void PlayCallButtonPressRpc(byte buttonAction, RpcParams rpcParams = default)
    {
        if (_callButtons == null)
            return;

        var action = (ElevatorCallButton.ElevatorButtonAction)buttonAction;
        for (int i = 0; i < _callButtons.Length; i++)
        {
            ElevatorCallButton button = _callButtons[i];
            if (button != null && button.Action == action)
            {
                button.PlayPressFeedback();
                return;
            }
        }
    }

    void AuthorizeDoorsClose()
    {
        if (_runCommitted)
            return;

        _runCommitted = true;
        _pendingSceneAfterDoorsIdle = true;

        if (IsSpawned)
            _doorsOpen.Value = false;
        else
            _slidingDoors.SetOpen(false, immediate: false);
    }

    bool ServerTryGetSenderFeetPosition(ulong senderClientId, out Vector3 feetPosition)
    {
        feetPosition = Vector3.zero;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null
            || !nm.ConnectedClients.TryGetValue(senderClientId, out NetworkClient client)
            || client.PlayerObject == null)
        {
            return false;
        }

        feetPosition = client.PlayerObject.transform.position;
        return true;
    }

    // ---- hinge cab (dungeon / carnival) ----

    /// <inheritdoc />
    public bool ServerValidateClose(HingeInteractDoor door, ulong senderClientId)
    {
        if (!IsSpawned)
        {
            // Offline play mode: the replicated counters are not running and there is exactly one player, so
            // their own position is the whole answer — the same rule GetOccupancyForPrompt already uses for the
            // local prompt.
            return IsOfflineSession
                && TryGetOfflineLocalPlayerFeetPosition(out Vector3 feetPosition)
                && IsPositionInsideInterior(feetPosition);
        }

        if (!IsServer)
            return false;

        return _livingInside.Value == _livingRequired.Value && _livingRequired.Value > 0;
    }

    /// <summary>Offline there is one living player; find them so the close gate has a position to test.</summary>
    static bool TryGetOfflineLocalPlayerFeetPosition(out Vector3 feetPosition)
    {
        feetPosition = Vector3.zero;

        IReadOnlyList<PlayerHealth> players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || player.IsDead)
                continue;

            feetPosition = player.transform.position;
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void ServerOnCloseAuthorized(HingeInteractDoor door, ulong senderClientId)
    {
        // Offline counts as authority here, exactly as Update's own gate does — otherwise the single-player
        // finish elevator arms nothing, and CompleteElevatorSequenceAfterDoorsIdle's !IsSpawned branch (written
        // for precisely this case) is unreachable.
        if (!HasElevatorAuthority)
            return;

        if (_pendingSceneAfterDoorsIdle)
            return;

        _pendingSceneAfterDoorsIdle = true;
    }

    void Update()
    {
        if (!_pendingSceneAfterDoorsIdle || !HasElevatorAuthority)
            return;

        if (!TryAllElevatorDoorsClosedAndIdle())
            return;

        _pendingSceneAfterDoorsIdle = false;
        CompleteElevatorSequenceAfterDoorsIdle();
    }

    void CompleteElevatorSequenceAfterDoorsIdle()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        string currentName = activeScene.IsValid() ? activeScene.name : string.Empty;
        string nextScene = null;
        bool hasNextSection =
            !endRunInsteadOfAdvancing && MultiplayerSceneFlow.TryGetNextMazeSectionScene(currentName, out nextScene);

        if (!IsSpawned)
        {
            // No session: this is a play-mode run of a single section.
            if (hasNextSection)
            {
                SceneManager.LoadScene(nextScene, LoadSceneMode.Single);
                LoadingCover.Show(nextScene);
            }
            else
                RequestReturnToMainMenuLocal();
            return;
        }

        // While the avatars still exist: the despawn sweep and the load below tear them down.
        AchievementRunTracker.ServerSectionCompleted(currentName, !hasNextSection, ServerCollectPlayersAboard());

        if (hasNextSection)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.SceneManager != null)
            {
                // Health and the hotbar survive the section switch. Snapshot the server-side state and get every
                // peer to lift its local copies of the carried items out of the avatars that the load is about to
                // destroy. This has to happen while this elevator is still spawned (the despawn sweep below
                // un-spawns it) and before LoadScene queues the player despawns, so the RPC is delivered first.
                LevelCarryOverStore.ServerCaptureAllPlayers();
                LevelCarryOverStore.HoldCarriedItemsForLevelSwitch();
                HoldCarriedItemsForLevelSwitchClientRpc();

                // Despawn all runtime-spawned level content BEFORE the synchronized scene switch. A
                // LoadSceneMode.Single load migrates dynamically-spawned NetworkObjects (destroyWithScene=false)
                // into the next scene rather than destroying them, so without this the previous section's Jailor,
                // traps and props would bleed into the next section.
                if (nm.TryGetComponent(out ProceduralMazeCoordinator mazeCoordinator))
                    mazeCoordinator.ServerDespawnAllLevelNetworkObjects();

                // Bracket the synchronized load: it despawns every player as it tears down this scene, and those
                // despawns must not be mistaken for client disconnects (which scatter the player's held items).
                NetworkPlayerInventory.BeginServerLevelSceneSwitch();
                SceneEventProgressStatus status;
                try
                {
                    status = nm.SceneManager.LoadScene(nextScene, LoadSceneMode.Single);
                }
                finally
                {
                    NetworkPlayerInventory.EndServerLevelSceneSwitch();
                }
                if (status == SceneEventProgressStatus.Started)
                {
                    LoadingCover.Show(nextScene);
                    return;
                }

                Debug.LogError(
                    $"[ElevatorFinish] Could not start synchronized load of \"{nextScene}\" (status={status}). Falling back to main menu.",
                    this);
            }
            else
                Debug.LogError("[ElevatorFinish] NetworkManager.SceneManager missing; falling back to main menu.", this);
        }

        ServerReturnEveryoneToMainMenuAfterElevator();
    }

    /// <summary>
    /// Every peer keeps its own local copies of hotbar items (they are seed-built, not network-spawned), so each
    /// one has to park its copies itself before the scene tears down. The server already ran this directly.
    /// </summary>
    [ClientRpc]
    void HoldCarriedItemsForLevelSwitchClientRpc()
    {
        if (IsServer)
            return;

        LevelCarryOverStore.HoldCarriedItemsForLevelSwitch();
    }

    void ServerReturnEveryoneToMainMenuAfterElevator()
    {
        ReturnAllPlayersToMainMenuClientRpc();
        StartCoroutine(ServerHostReturnToMainMenuAfterRpc());
    }

    /// <summary>
    /// Netcode gameplay→menu transition: shutdown session and use Unity <see cref="SceneManager"/> Single load so procedural maze objects do not linger in a bad state.
    /// </summary>
    [ClientRpc]
    void ReturnAllPlayersToMainMenuClientRpc()
    {
        RequestReturnToMainMenuLocal();
    }

    IEnumerator ServerHostReturnToMainMenuAfterRpc()
    {
        yield return null;
        RequestReturnToMainMenuLocal();
    }

    /// <summary>
    /// This peer leaves the session and loads the main menu. Also the exit of a lost run
    /// (<see cref="TeamWipeSequence"/>).
    /// </summary>
    public static void RequestReturnToMainMenuLocal()
    {
        MultiplayerSceneFlow flow = FindAnyObjectByType<MultiplayerSceneFlow>(FindObjectsInactive.Include);
        if (flow != null)
        {
            flow.ReturnToMainMenu();
            return;
        }

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening)
            nm.Shutdown();

        SceneManager.LoadScene(MultiplayerSceneFlow.MenuSceneName, LoadSceneMode.Single);
    }

    bool TryAllElevatorDoorsClosedAndIdle()
    {
        if (_slidingDoors != null)
            return _slidingDoors.IsClosedAndIdle;

        if (_doorA != null && (_doorA.IsOpen || _doorA.IsBusy))
            return false;
        if (_doorB != null && (_doorB.IsOpen || _doorB.IsBusy))
            return false;
        return _doorA != null;
    }
}
