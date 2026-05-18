using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Networked controller for a single basketball minigame station. Multiple stations can run in
/// parallel — each prefab instance is its own independent round.
/// <para>
/// Lifecycle: any player presses E on <see cref="CarnivalGameStartButton"/> while
/// <see cref="IsActive"/> is false. Server spawns a basketball at <see cref="ballSpawnAnchor"/>,
/// starts the countdown, and accepts hoop-trigger scores until the timer hits zero. On end the ball
/// despawns and (if score &gt; 0) a single <see cref="CarnivalTicketBundle"/> pops out of
/// <see cref="prizeChuteAnchor"/> with value = score × <see cref="ticketsPerBasket"/>.
/// </para>
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class BasketballGameController : NetworkBehaviour, ICarnivalGameStart
{
    const float SpawnedCounterpartSearchDistance = 3f;

    [Header("Round")]
    [SerializeField, Min(1f)] float roundDurationSeconds = 45f;
    [SerializeField, Min(1)] int ticketsPerBasket = 2;
    [Tooltip("Minimum gap between two scores so a single rebounding shot can't count multiple times.")]
    [SerializeField, Min(0f)] float scoreCooldownSeconds = 0.4f;

    [Header("Anchors")]
    [SerializeField] Transform ballSpawnAnchor;
    [SerializeField] Transform prizeChuteAnchor;

    [Header("Prefabs (must be in NetworkManager.NetworkPrefabs)")]
    [SerializeField] GameObject basketballPrefab;
    [SerializeField] GameObject ticketBundlePrefab;

    [Header("Ball out-of-bounds safety")]
    [Tooltip("If the ball drops below this world Y while not held during an active round, the server resets it to the ball spawn anchor with zero velocity. Set to a value comfortably below the floor.")]
    [SerializeField] float ballOutOfBoundsYThreshold = -50f;
    [Tooltip("Optional: pops the ticket bundle upward when it spawns so it visibly emerges from the chute.")]
    [SerializeField] Vector3 ticketBundleSpawnImpulse = new Vector3(0f, 1.5f, 0.5f);

    readonly NetworkVariable<bool> _isActive = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<float> _timeRemaining = new NetworkVariable<float>(
        0f,
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

    NetworkObject _spawnedBallNet;
    float _scoreCooldownAccumulator;

    public bool IsActive => _isActive.Value;
    public float TimeRemaining => _timeRemaining.Value;
    public int Score => _score.Value;
    public int LastFinishedScore => _lastFinishedScore.Value;
    public ulong SpawnedBallNetworkObjectId =>
        _spawnedBallNet != null && _spawnedBallNet.IsSpawned ? _spawnedBallNet.NetworkObjectId : 0UL;

    /// <summary>True if a start request will start a fresh round. Honored on both client and server.</summary>
    public bool CanStartNow => !_isActive.Value;

    /// <summary>Called by <see cref="CarnivalGameStartButton"/> when a player presses E. Routes to a ServerRpc as needed.</summary>
    public void ProcessStartRequest(PlayerController interactor)
    {
        Debug.Log($"[BasketballGameController] ProcessStartRequest on {name}, CanStartNow={CanStartNow}", this);
        if (interactor == null || !CanStartNow)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            Debug.LogWarning($"[BasketballGameController] NetworkManager not listening — start Host (or Server+Client) before testing.", this);
            return;
        }

        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
        {
            Debug.LogWarning($"[BasketballGameController] interactor has no NetworkObject — Player prefab must be a NetworkObject.", this);
            return;
        }

        if (nm.IsServer)
        {
            ServerStartRound(playerNet.NetworkObjectId);
            return;
        }

        if (!IsSpawned)
        {
            if (TryResolveSpawnedCounterpart(out BasketballGameController spawned))
            {
                spawned.ProcessStartRequest(interactor);
                return;
            }

            Debug.LogWarning(
                $"[BasketballGameController] {name} is not network-spawned on this client; wait for the server maze copy or ensure the station was spawned by the server.",
                this);
            return;
        }

        RequestStartRoundServerRpc(playerNet.NetworkObjectId);
    }

    bool TryResolveSpawnedCounterpart(out BasketballGameController spawned)
    {
        spawned = null;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return false;

        float maxDistanceSq = SpawnedCounterpartSearchDistance * SpawnedCounterpartSearchDistance;
        foreach (System.Collections.Generic.KeyValuePair<ulong, NetworkObject> pair in nm.SpawnManager.SpawnedObjects)
        {
            NetworkObject netObj = pair.Value;
            if (netObj == null || netObj == NetworkObject || !netObj.IsSpawned)
                continue;

            BasketballGameController candidate = netObj.GetComponent<BasketballGameController>();
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
        Debug.Log($"[BasketballGameController] ServerStartRound on {name}, IsServer={IsServer}, IsActive={_isActive.Value}", this);
        if (!IsServer || _isActive.Value)
            return;
        if (basketballPrefab == null)
        {
            Debug.LogError($"[BasketballGameController] basketballPrefab is not assigned on {name}.", this);
            return;
        }
        if (ballSpawnAnchor == null)
        {
            Debug.LogError($"[BasketballGameController] ballSpawnAnchor is not assigned on {name}.", this);
            return;
        }

        GameObject ballGo = Instantiate(basketballPrefab, ballSpawnAnchor.position, ballSpawnAnchor.rotation);
        NetworkObject ballNet = ballGo.GetComponent<NetworkObject>();
        if (ballNet == null)
        {
            Debug.LogError($"[BasketballGameController] basketballPrefab has no NetworkObject component.", this);
            Destroy(ballGo);
            return;
        }
        ballNet.Spawn();
        _spawnedBallNet = ballNet;
        Debug.Log($"[BasketballGameController] Ball spawned at {ballSpawnAnchor.position} with NetId={ballNet.NetworkObjectId}", this);

        _score.Value = 0;
        _timeRemaining.Value = roundDurationSeconds;
        _scoreCooldownAccumulator = 0f;
        _isActive.Value = true;
    }

    /// <summary>Server-only. Called by <see cref="BasketballHoopTrigger"/> on a valid downward ball-through-hoop event.</summary>
    public void ServerOnBasketScored(NetworkObject scoringBall)
    {
        if (!IsServer || !_isActive.Value)
            return;
        if (_scoreCooldownAccumulator > 0f)
            return;
        if (scoringBall == null || _spawnedBallNet == null)
            return;
        if (scoringBall.NetworkObjectId != _spawnedBallNet.NetworkObjectId)
            return;

        _score.Value++;
        _scoreCooldownAccumulator = scoreCooldownSeconds;
    }

    void FixedUpdate()
    {
        if (!IsServer)
            return;

        if (_scoreCooldownAccumulator > 0f)
            _scoreCooldownAccumulator = Mathf.Max(0f, _scoreCooldownAccumulator - Time.fixedDeltaTime);

        if (!_isActive.Value)
            return;

        ServerTickBallSafety();

        float remaining = _timeRemaining.Value - Time.fixedDeltaTime;
        if (remaining <= 0f)
        {
            _timeRemaining.Value = 0f;
            ServerEndRound();
            return;
        }
        _timeRemaining.Value = remaining;
    }

    void ServerTickBallSafety()
    {
        if (_spawnedBallNet == null || !_spawnedBallNet.IsSpawned)
            return;
        if (ballSpawnAnchor == null)
            return;

        // Only reset when no client owns the ball — fighting an in-flight owner-authority body
        // would cause snap/jitter. After settle, NetworkHeavyThrowableHold hands ownership back
        // to the server; that's our window to teleport.
        if (!_spawnedBallNet.IsOwnedByServer)
            return;

        Transform ballT = _spawnedBallNet.transform;
        if (ballT.position.y > ballOutOfBoundsYThreshold)
            return;

        ballT.position = ballSpawnAnchor.position;
        ballT.rotation = ballSpawnAnchor.rotation;
        Rigidbody rb = _spawnedBallNet.GetComponent<Rigidbody>();
        if (rb != null && !rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    void ServerEndRound()
    {
        if (!IsServer)
            return;

        _isActive.Value = false;
        int finalScore = _score.Value;
        _lastFinishedScore.Value = finalScore;

        if (_spawnedBallNet != null && _spawnedBallNet.IsSpawned)
            _spawnedBallNet.Despawn(true);
        _spawnedBallNet = null;

        if (finalScore <= 0 || ticketBundlePrefab == null || prizeChuteAnchor == null)
            return;

        GameObject bundleGo = Instantiate(
            ticketBundlePrefab,
            prizeChuteAnchor.position,
            prizeChuteAnchor.rotation);
        NetworkObject bundleNet = bundleGo.GetComponent<NetworkObject>();
        CarnivalTicketBundle bundle = bundleGo.GetComponent<CarnivalTicketBundle>();
        if (bundleNet == null || bundle == null)
        {
            Destroy(bundleGo);
            return;
        }
        bundleNet.Spawn();
        bundle.ServerSetValue(finalScore * ticketsPerBasket);

        if (ticketBundleSpawnImpulse.sqrMagnitude > 0.0001f)
        {
            Rigidbody rb = bundleGo.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                Vector3 worldImpulse = prizeChuteAnchor.TransformDirection(ticketBundleSpawnImpulse);
                rb.AddForce(worldImpulse, ForceMode.VelocityChange);
            }
        }
    }
}
