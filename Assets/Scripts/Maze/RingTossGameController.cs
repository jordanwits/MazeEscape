using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Networked controller for a single ring-toss minigame station. Built to mirror
/// <see cref="BasketballGameController"/>: multiple booths can run independent rounds in parallel.
/// <para>
/// Lifecycle: a player presses E on <see cref="CarnivalGameStartButton"/> while <see cref="IsActive"/>
/// is false. The server spawns one ring per <see cref="ringSpawns"/> slot (one colour each) at its
/// anchor and watches them. There is no countdown — the round ends once <b>every</b> ring has been
/// thrown (picked up and released at least once). After all rings are thrown the server waits
/// <see cref="resolveDelaySeconds"/> to let the last ring settle, then scores each ring against the
/// <see cref="RingTossPeg"/>s (a ring threaded onto a peg awards that peg's points), despawns the
/// rings, and — if the total is positive — pops a single <see cref="CarnivalTicketBundle"/> out of
/// <see cref="prizeChuteAnchor"/> worth total × <see cref="ticketsPerPoint"/>.
/// </para>
/// Scoring is server-authoritative; clients only see replicated spawns/despawns and the final score.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public sealed class RingTossGameController : NetworkBehaviour, ICarnivalGameStart, ICarnivalScoreSource
{
    const float SpawnedCounterpartSearchDistance = 3f;

    [System.Serializable]
    public struct RingSpawnSlot
    {
        [Tooltip("Networked ring prefab to spawn (must be in NetworkManager.NetworkPrefabs).")]
        public GameObject ringPrefab;
        [Tooltip("Empty anchor where this ring spawns.")]
        public Transform spawnAnchor;
    }

    [Header("Round")]
    [Tooltip("One slot per colour. The round resolves once all of these rings have been thrown.")]
    [SerializeField] RingSpawnSlot[] ringSpawns = new RingSpawnSlot[0];

    [Tooltip("Seconds to wait after the last ring is thrown before scoring + paying out, so the ring can settle on a peg.")]
    [SerializeField, Min(0f)] float resolveDelaySeconds = 2f;

    [Tooltip("Tickets awarded per point scored.")]
    [SerializeField, Min(0)] int ticketsPerPoint = 1;

    [Header("Pegs")]
    [Tooltip("Scoring pegs. Auto-filled from children on Reset; each carries its own point value.")]
    [SerializeField] RingTossPeg[] pegs = new RingTossPeg[0];

    [Header("Payout")]
    [SerializeField] Transform prizeChuteAnchor;
    [SerializeField] GameObject ticketBundlePrefab;
    [Tooltip("Pops the ticket bundle out of the chute when it spawns (local space of the chute anchor).")]
    [SerializeField] Vector3 ticketBundleSpawnImpulse = new Vector3(0f, 1.5f, 0.5f);

    [Header("Ring out-of-bounds safety")]
    [Tooltip("If a not-held ring drops below this world Y during a round, the server resets it to its spawn anchor. Set comfortably below the floor.")]
    [SerializeField] float ringOutOfBoundsYThreshold = -50f;

    readonly NetworkVariable<bool> _isActive = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<int> _lastFinishedScore = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<int> _score = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    sealed class TrackedRing
    {
        public NetworkObject net;
        public NetworkHeavyThrowableHold hold;
        public Transform spawnAnchor;
        public bool wasHeld;
        public bool thrown;
    }

    readonly List<TrackedRing> _rings = new List<TrackedRing>();
    bool _resolving;
    float _resolveTimer;

    public bool IsActive => _isActive.Value;
    public int Score => _score.Value; // Live: pegs the rings are currently resting on, recomputed each server tick.
    public int LastFinishedScore => _lastFinishedScore.Value;
    public float TimeRemaining => 0f; // No countdown.

    /// <summary>True if a start request will start a fresh round. Honored on both client and server.</summary>
    public bool CanStartNow => !_isActive.Value;

    void Reset()
    {
        pegs = GetComponentsInChildren<RingTossPeg>(true);
    }

    /// <summary>Called by <see cref="CarnivalGameStartButton"/> when a player presses E. Routes to a ServerRpc as needed.</summary>
    public void ProcessStartRequest(PlayerController interactor)
    {
        if (interactor == null || !CanStartNow)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            Debug.LogWarning("[RingTossGameController] NetworkManager not listening — start Host before testing.", this);
            return;
        }

        NetworkObject playerNet = interactor.GetComponent<NetworkObject>();
        if (playerNet == null)
        {
            Debug.LogWarning("[RingTossGameController] interactor has no NetworkObject — Player prefab must be a NetworkObject.", this);
            return;
        }

        if (nm.IsServer)
        {
            ServerStartRound(playerNet.NetworkObjectId);
            return;
        }

        if (!IsSpawned)
        {
            if (TryResolveSpawnedCounterpart(out RingTossGameController spawned))
            {
                spawned.ProcessStartRequest(interactor);
                return;
            }

            Debug.LogWarning(
                $"[RingTossGameController] {name} is not network-spawned on this client; wait for the server copy.",
                this);
            return;
        }

        RequestStartRoundServerRpc(playerNet.NetworkObjectId);
    }

    bool TryResolveSpawnedCounterpart(out RingTossGameController spawned)
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

            RingTossGameController candidate = netObj.GetComponent<RingTossGameController>();
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
        if (ringSpawns == null || ringSpawns.Length == 0)
        {
            Debug.LogError($"[RingTossGameController] no ring spawn slots assigned on {name}.", this);
            return;
        }

        ServerDespawnAndClearRings();

        for (int i = 0; i < ringSpawns.Length; i++)
        {
            RingSpawnSlot slot = ringSpawns[i];
            if (slot.ringPrefab == null || slot.spawnAnchor == null)
            {
                Debug.LogError($"[RingTossGameController] ring spawn slot {i} is missing a prefab or anchor on {name}.", this);
                continue;
            }

            GameObject ringGo = Instantiate(slot.ringPrefab, slot.spawnAnchor.position, slot.spawnAnchor.rotation);
            NetworkObject ringNet = ringGo.GetComponent<NetworkObject>();
            if (ringNet == null)
            {
                Debug.LogError($"[RingTossGameController] ring prefab '{slot.ringPrefab.name}' has no NetworkObject.", this);
                Destroy(ringGo);
                continue;
            }
            ringNet.Spawn();

            _rings.Add(new TrackedRing
            {
                net = ringNet,
                hold = ringGo.GetComponent<NetworkHeavyThrowableHold>(),
                spawnAnchor = slot.spawnAnchor,
            });
        }

        if (_rings.Count == 0)
            return;

        _score.Value = 0;
        _resolving = false;
        _resolveTimer = 0f;
        _isActive.Value = true;
    }

    void FixedUpdate()
    {
        if (!IsServer || !_isActive.Value)
            return;

        ServerTickRingSafety();
        ServerTickThrowTracking();

        // Live score: which pegs the rings are resting on right now. Ticks up as rings land and back
        // down if one is knocked off. NetworkVariable only replicates when the value actually changes.
        _score.Value = ServerComputeScore();

        if (!_resolving)
        {
            if (AllRingsThrown())
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
        for (int i = 0; i < _rings.Count; i++)
        {
            TrackedRing r = _rings[i];
            if (r.thrown || r.net == null || !r.net.IsSpawned)
                continue;

            ulong holder = r.hold != null ? r.hold.HolderNetworkObjectId : 0UL;
            if (holder != 0UL)
                r.wasHeld = true;
            else if (r.wasHeld)
                r.thrown = true; // released after being held → thrown
        }
    }

    bool AllRingsThrown()
    {
        for (int i = 0; i < _rings.Count; i++)
        {
            TrackedRing r = _rings[i];
            // A ring that vanished (shouldn't happen — we reset OOB rings) is treated as resolved so the
            // round can never deadlock.
            if (r.net == null || !r.net.IsSpawned)
                continue;
            if (!r.thrown)
                return false;
        }
        return true;
    }

    void ServerTickRingSafety()
    {
        for (int i = 0; i < _rings.Count; i++)
        {
            TrackedRing r = _rings[i];
            if (r.net == null || !r.net.IsSpawned || r.spawnAnchor == null)
                continue;
            // Only reset when the server owns the body and no client is mid-throw; fighting an
            // owner-authority rigidbody would jitter.
            if (!r.net.IsOwnedByServer)
                continue;
            if (r.hold != null && r.hold.HolderNetworkObjectId != 0UL)
                continue;

            Transform t = r.net.transform;
            if (t.position.y > ringOutOfBoundsYThreshold)
                continue;

            t.position = r.spawnAnchor.position;
            t.rotation = r.spawnAnchor.rotation;
            Rigidbody rb = r.net.GetComponent<Rigidbody>();
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

        // FixedUpdate already set _score this tick from the rings' resting positions.
        int total = _score.Value;
        _lastFinishedScore.Value = total;

        ServerDespawnAndClearRings();
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

    int ServerComputeScore()
    {
        int total = 0;
        if (pegs == null)
            return 0;

        for (int i = 0; i < _rings.Count; i++)
        {
            TrackedRing r = _rings[i];
            if (r.net == null || !r.net.IsSpawned)
                continue;
            // Skip a ring that someone grabbed again during the settle delay — its position is a hand.
            if (r.hold != null && r.hold.HolderNetworkObjectId != 0UL)
                continue;

            Vector3 ringCentre = r.net.transform.position;
            RingTossPeg best = null;
            float bestDistance = float.PositiveInfinity;
            for (int p = 0; p < pegs.Length; p++)
            {
                RingTossPeg peg = pegs[p];
                if (peg == null)
                    continue;
                float d = peg.HorizontalCaptureDistance(ringCentre);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = peg;
                }
            }

            if (best != null)
                total += best.Points;
        }

        return total;
    }

    void ServerDespawnAndClearRings()
    {
        for (int i = 0; i < _rings.Count; i++)
        {
            TrackedRing r = _rings[i];
            if (r.net != null && r.net.IsSpawned)
                r.net.Despawn(true);
        }
        _rings.Clear();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            ServerDespawnAndClearRings();
        base.OnNetworkDespawn();
    }
}
