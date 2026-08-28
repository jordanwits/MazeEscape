using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Networked controller for a single hole-board ("skee-board" — toss balls through rimmed holes)
/// minigame station. Built to mirror <see cref="BottleBoothGameController"/>: multiple booths can run
/// independent rounds in parallel.
/// <para>
/// Lifecycle: a player presses E on <see cref="CarnivalGameStartButton"/> while <see cref="IsActive"/>
/// is false. The server spawns one ball per <see cref="ballSpawns"/> anchor. There is no countdown —
/// the round ends once <b>every</b> spawned ball has been thrown (picked up and released at least
/// once). After the last ball is thrown the server waits <see cref="resolveDelaySeconds"/> to let any
/// in-flight ball finish passing through, then pays out and despawns the balls.
/// </para>
/// Scoring: each <see cref="HoleBoardHoleTrigger"/> reports the ball passing cleanly through it and
/// adds that hole's point value (blue centre holes 10, big green holes 5, small yellow holes 40 — set
/// per trigger). The total pays out <see cref="ticketsPerPoint"/> tickets per point via a single
/// <see cref="CarnivalTicketBundle"/> popped out of <see cref="prizeChuteAnchor"/>.
/// Scoring is server-authoritative; clients only see replicated spawns/despawns and the score.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class HoleBoardGameController : NetworkBehaviour, ICarnivalGameStart, ICarnivalScoreSource
{
    const float SpawnedCounterpartSearchDistance = 3f;

    [Header("Round")]
    [Tooltip("Ball prefab spawned at each ball anchor (must be in NetworkManager.NetworkPrefabs).")]
    [SerializeField] GameObject ballPrefab;
    [Tooltip("One anchor per ball. A ball is spawned at each on Start; the round resolves once every spawned ball has been thrown.")]
    [SerializeField] Transform[] ballSpawns = new Transform[0];

    [Tooltip("Seconds to wait after the last ball is thrown before scoring + paying out, so the last in-flight ball can finish passing through a hole.")]
    [SerializeField, Min(0f)] float resolveDelaySeconds = 2f;

    [Header("Scoring")]
    [Tooltip("Tickets awarded per point scored.")]
    [SerializeField, Min(0)] int ticketsPerPoint = 1;

    [Tooltip("Hole scoring triggers on the board; each carries its own point value. Auto-filled from children on Reset.")]
    [SerializeField] HoleBoardHoleTrigger[] holeTriggers = new HoleBoardHoleTrigger[0];

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
    bool _resolving;
    float _resolveTimer;

    public bool IsActive => _isActive.Value;
    public int Score => _score.Value; // Live: points sunk so far this round.
    public int LastFinishedScore => _lastFinishedScore.Value;
    public float TimeRemaining => 0f; // No countdown.

    /// <summary>True if a start request will start a fresh round. Honored on both client and server.</summary>
    public bool CanStartNow => !_isActive.Value;

    void Reset()
    {
        holeTriggers = GetComponentsInChildren<HoleBoardHoleTrigger>(true);
    }

    /// <summary>Called by <see cref="CarnivalGameStartButton"/> when a player presses E. Routes to a ServerRpc as needed.</summary>
    public void ProcessStartRequest(PlayerController interactor)
    {
        if (interactor == null || !CanStartNow)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            Debug.LogWarning("[HoleBoardGameController] NetworkManager not listening — start Host before testing.", this);
            return;
        }

        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
        {
            Debug.LogWarning("[HoleBoardGameController] interactor has no NetworkObject — Player prefab must be a NetworkObject.", this);
            return;
        }

        if (nm.IsServer)
        {
            ServerStartRound(playerNet.NetworkObjectId);
            return;
        }

        if (!IsSpawned)
        {
            if (TryResolveSpawnedCounterpart(out HoleBoardGameController spawned))
            {
                spawned.ProcessStartRequest(interactor);
                return;
            }

            Debug.LogWarning(
                $"[HoleBoardGameController] {name} is not network-spawned on this client; wait for the server copy.",
                this);
            return;
        }

        RequestStartRoundServerRpc(playerNet.NetworkObjectId);
    }

    bool TryResolveSpawnedCounterpart(out HoleBoardGameController spawned)
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

            HoleBoardGameController candidate = netObj.GetComponent<HoleBoardGameController>();
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

        // Server-side range gate against the booth's own transform so a client can't start a round from
        // across the map. Bounds are generous to cover the start button on any normally-positioned booth.
        const float ServerMaxStartHorizontal = 8f;
        const float ServerMaxStartVertical = 3f;
        Vector3 here = transform.position;
        Vector3 there = po.transform.position;
        Vector3 flat = new Vector3(there.x - here.x, 0f, there.z - here.z);
        if (flat.sqrMagnitude > ServerMaxStartHorizontal * ServerMaxStartHorizontal)
            return;
        if (Mathf.Abs(there.y - here.y) > ServerMaxStartVertical)
            return;

        ServerStartRound(startingPlayerNetObjId);
    }

    void ServerStartRound(ulong startingPlayerNetObjId)
    {
        if (!IsServer || _isActive.Value)
            return;
        if (ballPrefab == null)
        {
            Debug.LogError($"[HoleBoardGameController] ballPrefab is not assigned on {name}.", this);
            return;
        }
        if (ballSpawns == null || ballSpawns.Length == 0)
        {
            Debug.LogError($"[HoleBoardGameController] ballSpawns anchors are not assigned on {name}.", this);
            return;
        }

        ServerDespawnAndClearProps();

        for (int i = 0; i < ballSpawns.Length; i++)
        {
            Transform anchor = ballSpawns[i];
            if (anchor == null)
                continue;

            GameObject ballGo = Instantiate(ballPrefab, anchor.position, anchor.rotation);
            NetworkObject ballNet = ballGo.GetComponent<NetworkObject>();
            if (ballNet == null)
            {
                Debug.LogError($"[HoleBoardGameController] ballPrefab '{ballPrefab.name}' has no NetworkObject.", this);
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

        if (_balls.Count == 0)
        {
            ServerDespawnAndClearProps();
            return;
        }

        _score.Value = 0;
        _resolving = false;
        _resolveTimer = 0f;
        _isActive.Value = true;
    }

    /// <summary>
    /// Server-only. Called by a <see cref="HoleBoardHoleTrigger"/> once it has confirmed a tracked ball
    /// passed cleanly through that hole (front → rear). Adds the hole's point value to the live score.
    /// </summary>
    public void ServerOnHoleScored(NetworkObject scoringBall, int points)
    {
        if (!IsServer || !_isActive.Value || points <= 0)
            return;
        if (scoringBall == null || !IsTrackedBall(scoringBall))
            return;

        _score.Value += points;
    }

    /// <summary>
    /// Server-only. True while <paramref name="ball"/> is one of the balls spawned for the round in
    /// progress. The list is empty on clients and between rounds.
    /// </summary>
    public bool IsTrackedBall(NetworkObject ball)
    {
        for (int i = 0; i < _balls.Count; i++)
        {
            if (_balls[i].net != null && _balls[i].net == ball)
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

    void ServerResolveRound()
    {
        if (!IsServer)
            return;

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

        if (holeTriggers == null)
            return;
        for (int i = 0; i < holeTriggers.Length; i++)
        {
            HoleBoardHoleTrigger trigger = holeTriggers[i];
            if (trigger != null)
                trigger.ServerClearPassTracking();
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            ServerDespawnAndClearProps();
        base.OnNetworkDespawn();
    }
}
