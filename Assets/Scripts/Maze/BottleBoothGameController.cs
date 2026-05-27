using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Networked controller for a single bottle-booth ("knock-the-bottles-off-the-shelf") minigame
/// station. Built to mirror <see cref="RingTossGameController"/>: multiple booths can run
/// independent rounds in parallel.
/// <para>
/// Lifecycle: a player presses E on <see cref="CarnivalGameStartButton"/> while <see cref="IsActive"/>
/// is false. The server spawns one bottle per <see cref="bottleSpawns"/> anchor and one ball per
/// <see cref="ballSpawns"/> anchor. There is no countdown — the round ends once <b>every</b> spawned
/// ball has been thrown (picked up and released at least once). After the last ball is thrown the
/// server waits <see cref="resolveDelaySeconds"/> to let the last knocked bottle finish falling, then
/// pays out and despawns the props.
/// </para>
/// Scoring: each bottle that falls through the <see cref="CarnivalBottleKnockoffZone"/> under the shelf
/// is worth <see cref="pointsPerBottle"/>; knocking <b>all</b> bottles off adds <see cref="allBottlesBonus"/>
/// on top. The total pays out <see cref="ticketsPerPoint"/> tickets per point via a single
/// <see cref="CarnivalTicketBundle"/> popped out of <see cref="prizeChuteAnchor"/>.
/// Scoring is server-authoritative; clients only see replicated spawns/despawns and the score.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class BottleBoothGameController : NetworkBehaviour, ICarnivalGameStart, ICarnivalScoreSource
{
    const float SpawnedCounterpartSearchDistance = 3f;

    [Header("Round")]
    [Tooltip("Bottle prefab spawned at each bottle anchor (must be in NetworkManager.NetworkPrefabs).")]
    [SerializeField] GameObject bottlePrefab;
    [Tooltip("One anchor per bottle. A bottle is spawned at each on Start.")]
    [SerializeField] Transform[] bottleSpawns = new Transform[0];

    [Tooltip("Ball prefab spawned at each ball anchor (must be in NetworkManager.NetworkPrefabs).")]
    [SerializeField] GameObject ballPrefab;
    [Tooltip("One anchor per ball. The round resolves once every spawned ball has been thrown.")]
    [SerializeField] Transform[] ballSpawns = new Transform[0];

    [Tooltip("Seconds to wait after the last ball is thrown before scoring + paying out, so the last knocked bottle can finish falling.")]
    [SerializeField, Min(0f)] float resolveDelaySeconds = 2f;

    [Header("Scoring")]
    [Tooltip("Points awarded for each bottle knocked off the shelf.")]
    [SerializeField, Min(0)] int pointsPerBottle = 1;
    [Tooltip("Extra points awarded if EVERY spawned bottle is knocked off (added on top of the per-bottle points).")]
    [SerializeField, Min(0)] int allBottlesBonus = 15;
    [Tooltip("Tickets awarded per point scored.")]
    [SerializeField, Min(0)] int ticketsPerPoint = 2;

    [Tooltip("Trigger volume under the shelf; reports each bottle that falls through it. Auto-resolved from children on Reset.")]
    [SerializeField] CarnivalBottleKnockoffZone knockoffZone;

    [Header("Payout")]
    [SerializeField] Transform prizeChuteAnchor;
    [SerializeField] GameObject ticketBundlePrefab;
    [Tooltip("Pops the ticket bundle out of the chute when it spawns (local space of the chute anchor).")]
    [SerializeField] Vector3 ticketBundleSpawnImpulse = new Vector3(0f, 1.5f, 0.5f);

    [Header("Ball out-of-bounds safety")]
    [Tooltip("If a not-held ball drops below this world Y during a round, the server resets it to its spawn anchor. Set comfortably below the floor.")]
    [SerializeField] float ballOutOfBoundsYThreshold = -50f;

    readonly NetworkVariable<bool> _isActive = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<int> _score = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<int> _lastFinishedScore = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    sealed class TrackedBall
    {
        public NetworkObject net;
        public NetworkHeavyThrowableHold hold;
        public Transform spawnAnchor;
        public bool wasHeld;
        public bool thrown;
    }

    readonly List<TrackedBall> _balls = new List<TrackedBall>();
    readonly List<NetworkObject> _bottles = new List<NetworkObject>();
    readonly HashSet<ulong> _knockedBottleIds = new HashSet<ulong>();
    int _spawnedBottleCount;
    bool _resolving;
    float _resolveTimer;

    public bool IsActive => _isActive.Value;
    public int Score => _score.Value; // Live: knocked-off bottles tallied so far this round.
    public int LastFinishedScore => _lastFinishedScore.Value;
    public float TimeRemaining => 0f; // No countdown.

    /// <summary>True if a start request will start a fresh round. Honored on both client and server.</summary>
    public bool CanStartNow => !_isActive.Value;

    void Reset()
    {
        knockoffZone = GetComponentInChildren<CarnivalBottleKnockoffZone>(true);
    }

    /// <summary>Called by <see cref="CarnivalGameStartButton"/> when a player presses E. Routes to a ServerRpc as needed.</summary>
    public void ProcessStartRequest(PlayerController interactor)
    {
        if (interactor == null || !CanStartNow)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            Debug.LogWarning("[BottleBoothGameController] NetworkManager not listening — start Host before testing.", this);
            return;
        }

        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
        {
            Debug.LogWarning("[BottleBoothGameController] interactor has no NetworkObject — Player prefab must be a NetworkObject.", this);
            return;
        }

        if (nm.IsServer)
        {
            ServerStartRound(playerNet.NetworkObjectId);
            return;
        }

        if (!IsSpawned)
        {
            if (TryResolveSpawnedCounterpart(out BottleBoothGameController spawned))
            {
                spawned.ProcessStartRequest(interactor);
                return;
            }

            Debug.LogWarning(
                $"[BottleBoothGameController] {name} is not network-spawned on this client; wait for the server copy.",
                this);
            return;
        }

        RequestStartRoundServerRpc(playerNet.NetworkObjectId);
    }

    bool TryResolveSpawnedCounterpart(out BottleBoothGameController spawned)
    {
        spawned = null;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return false;

        float maxDistanceSq = SpawnedCounterpartSearchDistance * SpawnedCounterpartSearchDistance;
        foreach (KeyValuePair<ulong, NetworkObject> pair in nm.SpawnManager.SpawnedObjects)
        {
            NetworkObject netObj = pair.Value;
            if (netObj == null || netObj == NetworkObject || !netObj.IsSpawned)
                continue;

            BottleBoothGameController candidate = netObj.GetComponent<BottleBoothGameController>();
            if (candidate == null)
                continue;
            if ((netObj.transform.position - transform.position).sqrMagnitude > maxDistanceSq)
                continue;

            spawned = candidate;
            return true;
        }

        return false;
    }

    [ServerRpc(RequireOwnership = false)]
    void RequestStartRoundServerRpc(ulong startingPlayerNetObjId, ServerRpcParams rpcParams = default)
    {
        if (!IsServer)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null
            || !nm.SpawnManager.SpawnedObjects.TryGetValue(startingPlayerNetObjId, out NetworkObject po)
            || po == null)
            return;
        if (po.OwnerClientId != rpcParams.Receive.SenderClientId)
            return;

        ServerStartRound(startingPlayerNetObjId);
    }

    void ServerStartRound(ulong startingPlayerNetObjId)
    {
        if (!IsServer || _isActive.Value)
            return;
        if (bottlePrefab == null || ballPrefab == null)
        {
            Debug.LogError($"[BottleBoothGameController] bottlePrefab or ballPrefab is not assigned on {name}.", this);
            return;
        }
        if (bottleSpawns == null || bottleSpawns.Length == 0 || ballSpawns == null || ballSpawns.Length == 0)
        {
            Debug.LogError($"[BottleBoothGameController] bottleSpawns/ballSpawns anchors are not assigned on {name}.", this);
            return;
        }

        ServerDespawnAndClearProps();

        for (int i = 0; i < bottleSpawns.Length; i++)
        {
            Transform anchor = bottleSpawns[i];
            if (anchor == null)
                continue;

            GameObject bottleGo = Instantiate(bottlePrefab, anchor.position, anchor.rotation);
            NetworkObject bottleNet = bottleGo.GetComponent<NetworkObject>();
            if (bottleNet == null)
            {
                Debug.LogError($"[BottleBoothGameController] bottlePrefab '{bottlePrefab.name}' has no NetworkObject.", this);
                Destroy(bottleGo);
                continue;
            }
            bottleNet.Spawn();
            _bottles.Add(bottleNet);
        }

        for (int i = 0; i < ballSpawns.Length; i++)
        {
            Transform anchor = ballSpawns[i];
            if (anchor == null)
                continue;

            GameObject ballGo = Instantiate(ballPrefab, anchor.position, anchor.rotation);
            NetworkObject ballNet = ballGo.GetComponent<NetworkObject>();
            if (ballNet == null)
            {
                Debug.LogError($"[BottleBoothGameController] ballPrefab '{ballPrefab.name}' has no NetworkObject.", this);
                Destroy(ballGo);
                continue;
            }
            ballNet.Spawn();

            _balls.Add(new TrackedBall
            {
                net = ballNet,
                hold = ballGo.GetComponent<NetworkHeavyThrowableHold>(),
                spawnAnchor = anchor,
            });
        }

        if (_balls.Count == 0 || _bottles.Count == 0)
        {
            ServerDespawnAndClearProps();
            return;
        }

        _spawnedBottleCount = _bottles.Count;
        _knockedBottleIds.Clear();
        _score.Value = 0;
        _resolving = false;
        _resolveTimer = 0f;
        _isActive.Value = true;
    }

    /// <summary>
    /// Server-only. Called by <see cref="CarnivalBottleKnockoffZone"/> when a bottle falls through the
    /// trigger volume under the shelf. Each tracked bottle counts once.
    /// </summary>
    public void ServerOnBottleKnockedOff(NetworkObject bottle)
    {
        if (!IsServer || !_isActive.Value || bottle == null)
            return;
        if (!IsTrackedBottle(bottle))
            return;
        _knockedBottleIds.Add(bottle.NetworkObjectId);
    }

    bool IsTrackedBottle(NetworkObject bottle)
    {
        for (int i = 0; i < _bottles.Count; i++)
        {
            if (_bottles[i] != null && _bottles[i] == bottle)
                return true;
        }
        return false;
    }

    void FixedUpdate()
    {
        if (!IsServer || !_isActive.Value)
            return;

        ServerTickBallSafety();
        ServerTickThrowTracking();

        // Live score: knocked-off bottles tallied so far (+ all-bottles bonus once every bottle is down).
        // NetworkVariable only replicates when the value actually changes.
        _score.Value = ServerComputeScore();

        if (!_resolving)
        {
            if (AllBallsThrown())
            {
                _resolving = true;
                _resolveTimer = resolveDelaySeconds;
            }
            return;
        }

        _resolveTimer -= Time.fixedDeltaTime;
        if (_resolveTimer <= 0f)
            ServerResolveRound();
    }

    void ServerTickThrowTracking()
    {
        for (int i = 0; i < _balls.Count; i++)
        {
            TrackedBall b = _balls[i];
            if (b.thrown || b.net == null || !b.net.IsSpawned)
                continue;

            ulong holder = b.hold != null ? b.hold.HolderNetworkObjectId : 0UL;
            if (holder != 0UL)
                b.wasHeld = true;
            else if (b.wasHeld)
                b.thrown = true; // released after being held → thrown
        }
    }

    bool AllBallsThrown()
    {
        for (int i = 0; i < _balls.Count; i++)
        {
            TrackedBall b = _balls[i];
            // A ball that vanished (shouldn't happen — we reset OOB balls) is treated as resolved so the
            // round can never deadlock.
            if (b.net == null || !b.net.IsSpawned)
                continue;
            if (!b.thrown)
                return false;
        }
        return true;
    }

    void ServerTickBallSafety()
    {
        for (int i = 0; i < _balls.Count; i++)
        {
            TrackedBall b = _balls[i];
            if (b.net == null || !b.net.IsSpawned || b.spawnAnchor == null)
                continue;
            // Only reset when the server owns the body and no client is mid-throw; fighting an
            // owner-authority rigidbody would jitter.
            if (!b.net.IsOwnedByServer)
                continue;
            if (b.hold != null && b.hold.HolderNetworkObjectId != 0UL)
                continue;

            Transform t = b.net.transform;
            if (t.position.y > ballOutOfBoundsYThreshold)
                continue;

            t.position = b.spawnAnchor.position;
            t.rotation = b.spawnAnchor.rotation;
            Rigidbody rb = b.net.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }

    int ServerComputeScore()
    {
        int knocked = _knockedBottleIds.Count;
        int total = knocked * pointsPerBottle;
        if (_spawnedBottleCount > 0 && knocked >= _spawnedBottleCount)
            total += allBottlesBonus;
        return total;
    }

    void ServerResolveRound()
    {
        if (!IsServer)
            return;

        // FixedUpdate already set _score this tick from the knocked-off tally.
        int total = _score.Value;
        _lastFinishedScore.Value = total;

        ServerDespawnAndClearProps();
        _resolving = false;
        _isActive.Value = false;

        if (total <= 0 || ticketBundlePrefab == null || prizeChuteAnchor == null)
            return;

        GameObject bundleGo = Instantiate(ticketBundlePrefab, prizeChuteAnchor.position, prizeChuteAnchor.rotation);
        NetworkObject bundleNet = bundleGo.GetComponent<NetworkObject>();
        CarnivalTicketBundle bundle = bundleGo.GetComponent<CarnivalTicketBundle>();
        if (bundleNet == null || bundle == null)
        {
            Destroy(bundleGo);
            return;
        }
        bundleNet.Spawn();
        bundle.ServerSetValue(total * ticketsPerPoint);

        if (ticketBundleSpawnImpulse.sqrMagnitude > 0.0001f)
        {
            Rigidbody rb = bundleGo.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
                rb.AddForce(prizeChuteAnchor.TransformDirection(ticketBundleSpawnImpulse), ForceMode.VelocityChange);
        }
    }

    void ServerDespawnAndClearProps()
    {
        for (int i = 0; i < _balls.Count; i++)
        {
            TrackedBall b = _balls[i];
            if (b.net != null && b.net.IsSpawned)
                b.net.Despawn(true);
        }
        _balls.Clear();

        for (int i = 0; i < _bottles.Count; i++)
        {
            NetworkObject bottle = _bottles[i];
            if (bottle != null && bottle.IsSpawned)
                bottle.Despawn(true);
        }
        _bottles.Clear();

        _knockedBottleIds.Clear();
        _spawnedBottleCount = 0;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            ServerDespawnAndClearProps();
        base.OnNetworkDespawn();
    }
}
