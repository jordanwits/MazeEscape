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

    HingeInteractDoor _doorA;
    HingeInteractDoor _doorB;
    ElevatorSlidingDoors _slidingDoors;
    bool _pendingSceneAfterDoorsIdle;
    /// <summary>Set once a close has been authorized: the run is committed and the pads stop answering.</summary>
    bool _runCommitted;
    bool _boundToFinishPiece;
    Coroutine _bindRoutine;

    public int LivingInsideDisplay => IsSpawned ? _livingInside.Value : 0;
    public int LivingRequiredDisplay => IsSpawned ? _livingRequired.Value : 0;

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

        AdoptInteriorVolumeFromPieceCopy(marker);

        _slidingDoors = marker.GetComponentInChildren<ElevatorSlidingDoors>(true);

        ElevatorCallButton[] buttons = marker.GetComponentsInChildren<ElevatorCallButton>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] != null)
                buttons[i].AssignController(this);
        }

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
    /// A client's replica is instantiated from the registered ElevatorFinishSync prefab, so it carries that prefab's
    /// default cab volume rather than the per-piece override the server is counting occupants with. The piece the
    /// client just built holds an inert copy with the authored volume — take the size from it so "am I aboard" means
    /// the same thing on every peer.
    /// </summary>
    void AdoptInteriorVolumeFromPieceCopy(ElevatorFinishSpawnMarker marker)
    {
        if (interiorVolume == null)
            return;

        ElevatorFinishController[] copies = marker.GetComponentsInChildren<ElevatorFinishController>(true);
        for (int i = 0; i < copies.Length; i++)
        {
            ElevatorFinishController copy = copies[i];
            if (copy == null || copy == this || copy.IsSpawned || copy.interiorVolume == null)
                continue;

            interiorVolume.size = copy.interiorVolume.size;
            interiorVolume.center = copy.interiorVolume.center;
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

    /// <summary>Outside pad: call the elevator. Server (or the local peer offline) decides.</summary>
    public void RequestDoorsOpenFromButton(Vector3 interactorPosition)
    {
        if (_slidingDoors == null)
            return;

        if (!IsSpawned)
        {
            if (!IsOfflineSession)
                return;
            if (_runCommitted || _slidingDoors.IsOpen)
                return;

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

        _doorsOpen.Value = true;
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
        if (!IsServer)
            return false;

        return _livingInside.Value == _livingRequired.Value && _livingRequired.Value > 0;
    }

    /// <inheritdoc />
    public void ServerOnCloseAuthorized(HingeInteractDoor door, ulong senderClientId)
    {
        if (!IsServer)
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
                SceneManager.LoadScene(nextScene, LoadSceneMode.Single);
            else
                RequestReturnToMainMenuLocal();
            return;
        }

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
                    return;

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

    static void RequestReturnToMainMenuLocal()
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
