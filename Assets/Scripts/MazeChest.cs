using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[DisallowMultipleComponent]
public class MazeChest : NetworkBehaviour
{
    const string LootAnchor1Name = "LootAnchor";
    const string LootAnchor2Name = "LootAnchor2";

    [Header("Lid")]
    [SerializeField] Transform lidTransform;
    [SerializeField] Vector3 openLocalEuler = new Vector3(0f, 0f, -90f);
    [SerializeField] float openDuration = 0.45f;

    [Header("Loot")]
    [SerializeField] GameObject[] lootPrefabs;

    [Header("Interaction")]
    [SerializeField] float interactMaxDistance = 5f;
    [Tooltip("Extra world-space offset when spawning loot so rigidbodies do not start overlapping solid colliders.")]
    [SerializeField] Vector3 lootSpawnWorldOffset = new Vector3(0f, 0.08f, 0f);
    [Tooltip("When the chest opens, these colliders are disabled so items can be picked up via raycast and are not trapped inside a wrapper volume. If empty, uses the BoxCollider on this object (if any).")]
    [SerializeField] Collider[] collidersToDisableWhenOpen;

    readonly NetworkVariable<bool> _opened = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<int> _lootSeed = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    Quaternion _closedLocalRotation;
    bool _openedOffline;
    bool _lootSpawned;
    Coroutine _lidRoutine;
    int _configuredLootSeed;
    Transform[] _lootAnchors;
    Collider[] _collidersToDisableWhenOpenResolved;

    public bool IsOpened => IsSpawned ? _opened.Value : _openedOffline;

    public float InteractMaxDistance => interactMaxDistance;

    public void ConfigureFromMaze(int lootSeed)
    {
        _configuredLootSeed = lootSeed;
    }

    void Awake()
    {
        if (lidTransform != null)
            _closedLocalRotation = lidTransform.localRotation;

        ResolveCollidersToDisableWhenOpen();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
            _lootSeed.Value = _configuredLootSeed;

        _opened.OnValueChanged += OnOpenedChanged;

        CacheLootAnchors();

        if (_opened.Value)
            PlayOpenAnimationAndSpawnLoot(true);
    }

    public override void OnNetworkDespawn()
    {
        _opened.OnValueChanged -= OnOpenedChanged;
    }

    void Start()
    {
        CacheLootAnchors();
    }

    void OnOpenedChanged(bool previous, bool current)
    {
        if (!current || previous)
            return;

        PlayOpenAnimationAndSpawnLoot(false);
    }

    public void TryRequestOpen(Vector3 interactorPosition)
    {
        if (IsOpened)
            return;

        if (!IsInInteractRange(interactorPosition))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool localOnly = nm == null || !nm.IsListening || !IsSpawned;

        if (localOnly)
        {
            _openedOffline = true;
            PlayOpenAnimationAndSpawnLoot(false);
            return;
        }

        OpenChestServerRpc();
    }

    public bool IsInInteractRange(Vector3 worldPosition)
    {
        float maxSqr = interactMaxDistance * interactMaxDistance;
        return (transform.position - worldPosition).sqrMagnitude <= maxSqr;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    void OpenChestServerRpc(RpcParams rpcParams = default)
    {
        if (_opened.Value)
            return;

        ulong senderId = rpcParams.Receive.SenderClientId;
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out NetworkClient client)
            || client.PlayerObject == null)
        {
            return;
        }

        if (!IsInInteractRange(client.PlayerObject.transform.position))
            return;

        _opened.Value = true;
    }

    void PlayOpenAnimationAndSpawnLoot(bool immediate)
    {
        DisableInteractionCollidersForOpenedChest();

        if (lidTransform == null)
        {
            SpawnLootIfNeeded();
            return;
        }

        if (_lidRoutine != null)
        {
            StopCoroutine(_lidRoutine);
            _lidRoutine = null;
        }

        if (immediate)
        {
            lidTransform.localRotation = _closedLocalRotation * Quaternion.Euler(openLocalEuler);
            SpawnLootIfNeeded();
            return;
        }

        _lidRoutine = StartCoroutine(OpenLidRoutine());
    }

    void ResolveCollidersToDisableWhenOpen()
    {
        if (collidersToDisableWhenOpen != null && collidersToDisableWhenOpen.Length > 0)
        {
            _collidersToDisableWhenOpenResolved = collidersToDisableWhenOpen;
            return;
        }

        BoxCollider box = GetComponent<BoxCollider>();
        _collidersToDisableWhenOpenResolved = box != null ? new[] { box } : Array.Empty<Collider>();
    }

    void DisableInteractionCollidersForOpenedChest()
    {
        if (_collidersToDisableWhenOpenResolved == null || _collidersToDisableWhenOpenResolved.Length == 0)
            ResolveCollidersToDisableWhenOpen();

        for (int i = 0; i < _collidersToDisableWhenOpenResolved.Length; i++)
        {
            Collider c = _collidersToDisableWhenOpenResolved[i];
            if (c != null)
                c.enabled = false;
        }
    }

    IEnumerator OpenLidRoutine()
    {
        Quaternion start = lidTransform.localRotation;
        Quaternion end = _closedLocalRotation * Quaternion.Euler(openLocalEuler);
        float duration = Mathf.Max(0.01f, openDuration);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            lidTransform.localRotation = Quaternion.Slerp(start, end, t);
            yield return null;
        }

        lidTransform.localRotation = end;
        SpawnLootIfNeeded();
        _lidRoutine = null;
    }

    void SpawnLootIfNeeded()
    {
        if (_lootSpawned)
            return;

        if (lootPrefabs == null || lootPrefabs.Length == 0)
            return;

        CacheLootAnchors();
        if (_lootAnchors == null || _lootAnchors.Length == 0)
            return;

        int seed = IsSpawned ? _lootSeed.Value : _configuredLootSeed;
        System.Random rng = new System.Random(seed);

        for (int i = 0; i < _lootAnchors.Length; i++)
        {
            Transform anchor = _lootAnchors[i];
            if (anchor == null)
                continue;

            int idx = rng.Next(0, lootPrefabs.Length);
            GameObject prefab = lootPrefabs[idx];
            if (prefab == null)
                continue;

            Vector3 spawnPos = anchor.position + lootSpawnWorldOffset;
            GameObject instance = Instantiate(prefab, spawnPos, anchor.rotation);
            instance.transform.SetParent(null, true);
            if (instance.TryGetComponent(out GlowstickItem glow))
                glow.SetStackCount(GlowstickItem.MaxStack);
            ApplyChestLootAtRest(instance);
        }

        _lootSpawned = true;
    }

    /// <summary>
    /// Loot stays in the chest until picked up: kinematic with no gravity so it does not roll away.
    /// <see cref="FlashlightItem.Pickup"/> / drop already enable normal physics when the player drops the item.
    /// </summary>
    static void ApplyChestLootAtRest(GameObject root)
    {
        if (root == null)
            return;

        Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < bodies.Length; i++)
        {
            Rigidbody rb = bodies[i];
            if (rb == null)
                continue;

            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.isKinematic = true;
        }
    }

    void CacheLootAnchors()
    {
        if (_lootAnchors != null && _lootAnchors.Length > 0)
            return;

        List<Transform> found = new List<Transform>(2);
        Transform[] all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == null)
                continue;

            string n = t.name;
            if (string.Equals(n, LootAnchor1Name, StringComparison.Ordinal)
                || string.Equals(n, LootAnchor2Name, StringComparison.Ordinal))
            {
                found.Add(t);
            }
        }

        found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        _lootAnchors = found.ToArray();
    }
}
