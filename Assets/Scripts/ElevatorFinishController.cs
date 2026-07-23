using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Server-spawned sync object for the maze exit elevator: occupancy counts, close gating, synchronized advance to the next maze scene when doors finish closing,
/// or return to menu when there is no configured next section (e.g. last level).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class ElevatorFinishController : NetworkBehaviour, IHingeCloseValidator
{
    [SerializeField] BoxCollider interiorVolume;
    [Tooltip("Optional: rigidbody added when volume needs trigger events; if null, occupancy uses bounds checks only.")]
    [SerializeField] bool addKinematicRigidbodyToVolume = true;

    readonly NetworkVariable<int> _livingInside = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<int> _livingRequired = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    HingeInteractDoor _doorA;
    HingeInteractDoor _doorB;
    bool _pendingSceneAfterDoorsIdle;

    public int LivingInsideDisplay => IsSpawned ? _livingInside.Value : 0;
    public int LivingRequiredDisplay => IsSpawned ? _livingRequired.Value : 0;

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

    public override void OnNetworkSpawn()
    {
        // Bind doors on clients too so localized prompts can resolve this controller (counts replicate via Netvars).
        if (!TryGetDoorsNearMarker(out HingeInteractDoor[] doors))
            return;

        foreach (HingeInteractDoor d in doors)
        {
            if (d != null)
                d.AssignRuntimeCloseValidator(this);
        }

        if (IsServer)
            ServerCacheDoorPairsForIdleCheck(doors);
    }

    bool TryGetDoorsNearMarker(out HingeInteractDoor[] doors)
    {
        doors = System.Array.Empty<HingeInteractDoor>();
        ElevatorFinishSpawnMarker marker = TryResolveSpawnMarkerForThisSync();
        if (marker == null)
        {
            Debug.LogWarning(
                "[ElevatorFinish] ElevatorFinishSpawnMarker not found (parent chain or nearest in scene); door gating will not work.",
                this);
            return false;
        }

        doors = marker.GetComponentsInChildren<HingeInteractDoor>(true);
        return true;
    }

    void ServerCacheDoorPairsForIdleCheck(HingeInteractDoor[] doors)
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
        if (!IsServer || !_pendingSceneAfterDoorsIdle)
            return;

        if (!ServerTryBothDoorsClosedAndIdle())
            return;

        _pendingSceneAfterDoorsIdle = false;
        ServerCompleteElevatorSequenceAfterDoorsIdle();
    }

    void ServerCompleteElevatorSequenceAfterDoorsIdle()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        string currentName = activeScene.IsValid() ? activeScene.name : string.Empty;

        if (MultiplayerSceneFlow.TryGetNextMazeSectionScene(currentName, out string nextScene))
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

    bool ServerTryBothDoorsClosedAndIdle()
    {
        if (_doorA != null && (_doorA.IsOpen || _doorA.IsBusy))
            return false;
        if (_doorB != null && (_doorB.IsOpen || _doorB.IsBusy))
            return false;
        return _doorA != null;
    }
}
