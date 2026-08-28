using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public partial class NetworkPlayerInventory : NetworkBehaviour
{
    /// <summary>Hotbar slots every player starts with.</summary>
    public const int BaseSlotCount = 3;

    /// <summary>
    /// Hotbar slots a player can reach at most — the base three plus the one sold at the carnival prize
    /// counter. The backing NetworkVariables for ALL of these always exist; the purchase only raises
    /// <see cref="SlotCapacity"/>, so nothing about the replicated layout changes when it is bought.
    /// </summary>
    public const int MaxSlotCount = 4;

    [SerializeField] PlayerController playerController;
    [Tooltip("Forward impulse when dropping (matches PlayerController drop force).")]
    [SerializeField] float dropThrowImpulse = 0.65f;

    NetworkPlayerAvatar _avatar;
    uint _runtimeDropSequence;

    // True on the server only while a synchronized Single scene switch (e.g. the elevator to the next section)
    // is tearing down players. Distinguishes those bulk player despawns from a genuine client disconnect in
    // OnNetworkDespawn so the disconnect item-scatter path does not fire during level transitions.
    static bool s_serverLevelSceneSwitchInProgress;

    readonly NetworkVariable<ulong> _slot0ItemId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<ulong> _slot1ItemId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<ulong> _slot2ItemId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<ulong> _slot3ItemId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<byte> _slot0ItemType = new NetworkVariable<byte>(
        GrabbableInventoryItem.TypeIdNone,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<byte> _slot1ItemType = new NetworkVariable<byte>(
        GrabbableInventoryItem.TypeIdNone,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<byte> _slot2ItemType = new NetworkVariable<byte>(
        GrabbableInventoryItem.TypeIdNone,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<byte> _slot3ItemType = new NetworkVariable<byte>(
        GrabbableInventoryItem.TypeIdNone,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// Whether this player bought the 4th hotbar slot at the carnival prize counter. Server-written, so a
    /// client cannot grant itself the upgrade, and replicated so every peer's HUD agrees on the row width.
    /// It rides the player object, which survives respawns, and <see cref="LevelCarryOverStore"/> carries it
    /// across a section switch — you buy it once and keep it for the run.
    /// </summary>
    readonly NetworkVariable<bool> _extraSlotUnlocked = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<byte> _selectedSlot = new NetworkVariable<byte>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<bool> _selectedFlashlightLightOn = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// 0 when the slot is empty; 1 for a single item; 1–<see cref="GrabbableInventoryItem.MaxStackSize"/> for
    /// stackable items (glowsticks, flare rounds).
    /// </summary>
    readonly NetworkVariable<byte> _slot0Stack = new NetworkVariable<byte>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> _slot1Stack = new NetworkVariable<byte>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> _slot2Stack = new NetworkVariable<byte>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    readonly NetworkVariable<byte> _slot3Stack = new NetworkVariable<byte>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>Replicated 0…1 for HUD; only meaningful when the slot holds a flashlight. Server-writes from the world object each frame.</summary>
    readonly NetworkVariable<float> _slot0FlashlightBattery = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> _slot1FlashlightBattery = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> _slot2FlashlightBattery = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);
    readonly NetworkVariable<float> _slot3FlashlightBattery = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public int SelectedSlotIndex => _selectedSlot.Value;
    public bool SelectedFlashlightLightOn => _selectedFlashlightLightOn.Value;

    /// <summary>True once this player has bought the extra hotbar slot.</summary>
    public bool HasExtraSlot => _extraSlotUnlocked.Value;

    /// <summary>
    /// How many hotbar slots this player can actually use right now. Every loop that asks "where can an item
    /// go / what can I scroll to" must use this, NOT <see cref="MaxSlotCount"/> — the 4th slot's variables
    /// exist from spawn but are off-limits until bought. Teardown loops (scatter on disconnect, clear-all)
    /// deliberately use MaxSlotCount instead, so nothing can strand in a slot that later locks.
    /// </summary>
    public int SlotCapacity => _extraSlotUnlocked.Value ? MaxSlotCount : BaseSlotCount;

    /// <summary>
    /// Server-only. Grants the bought 4th slot. Returns false when this player already owns it, which is what
    /// enforces the shop's one-per-player limit — the check and the write live on the same authority, so two
    /// rapid clicks cannot both pass.
    /// </summary>
    public bool ServerGrantExtraSlot()
    {
        if (!IsServer || _extraSlotUnlocked.Value)
            return false;

        _extraSlotUnlocked.Value = true;
        RaiseChangedAndRefresh();
        return true;
    }

    /// <summary>Server-only. Restores a previously-bought slot when rebuilding a player in the next section.</summary>
    public void ServerRestoreExtraSlot(bool unlocked)
    {
        if (IsServer)
            _extraSlotUnlocked.Value = unlocked;
    }

    public event System.Action OnInventoryChanged;

    public ulong GetSlotItemId(int index)
    {
        if (index == 0) return _slot0ItemId.Value;
        if (index == 1) return _slot1ItemId.Value;
        if (index == 2) return _slot2ItemId.Value;
        if (index == 3) return _slot3ItemId.Value;
        return 0UL;
    }

    void SetSlotItemId(int index, ulong value)
    {
        if (index == 0) _slot0ItemId.Value = value;
        else if (index == 1) _slot1ItemId.Value = value;
        else if (index == 2) _slot2ItemId.Value = value;
        else if (index == 3) _slot3ItemId.Value = value;
    }

    public byte GetSlotItemTypeId(int index)
    {
        if (index == 0) return _slot0ItemType.Value;
        if (index == 1) return _slot1ItemType.Value;
        if (index == 2) return _slot2ItemType.Value;
        if (index == 3) return _slot3ItemType.Value;
        return GrabbableInventoryItem.TypeIdNone;
    }

    void SetSlotItemTypeId(int index, byte value)
    {
        if (index == 0) _slot0ItemType.Value = value;
        else if (index == 1) _slot1ItemType.Value = value;
        else if (index == 2) _slot2ItemType.Value = value;
        else if (index == 3) _slot3ItemType.Value = value;
    }

    public int GetSlotStackCount(int index)
    {
        if (index == 0) return _slot0Stack.Value;
        if (index == 1) return _slot1Stack.Value;
        if (index == 2) return _slot2Stack.Value;
        if (index == 3) return _slot3Stack.Value;
        return 0;
    }

    void SetSlotStackCount(int index, byte value)
    {
        if (index == 0) _slot0Stack.Value = value;
        else if (index == 1) _slot1Stack.Value = value;
        else if (index == 2) _slot2Stack.Value = value;
        else if (index == 3) _slot3Stack.Value = value;
    }

    public float GetSlotFlashlightBatteryNormalizedForHud(int index)
    {
        if (index == 0) return _slot0FlashlightBattery.Value;
        if (index == 1) return _slot1FlashlightBattery.Value;
        if (index == 2) return _slot2FlashlightBattery.Value;
        if (index == 3) return _slot3FlashlightBattery.Value;
        return 0f;
    }

    /// <summary>
    /// Flare gun rounds in a slot, decoded from the shared per-slot charge variable (rounds / capacity,
    /// quantized to 1% — exact for round counts up to 33). Only meaningful when the slot holds a flare gun.
    /// </summary>
    public int GetSlotFlareRoundsForHud(int index)
    {
        return Mathf.RoundToInt(Mathf.Clamp01(GetSlotFlashlightBatteryNormalizedForHud(index)) * FlareGunItem.MaxRounds);
    }

    void SetSlotFlashlightBatteryNormalized(int index, float value)
    {
        // Quantize to 1% steps so a continuously-draining battery only replicates ~100 deltas over its
        // whole life instead of one per FixedUpdate per slot per player. The HUD reads the same value
        // and renders at integer-percent precision anyway, so this is invisible to the player.
        float quantized = Mathf.Round(Mathf.Clamp01(value) * 100f) / 100f;
        if (index == 0)
        {
            if (!Mathf.Approximately(_slot0FlashlightBattery.Value, quantized))
                _slot0FlashlightBattery.Value = quantized;
        }
        else if (index == 1)
        {
            if (!Mathf.Approximately(_slot1FlashlightBattery.Value, quantized))
                _slot1FlashlightBattery.Value = quantized;
        }
        else if (index == 2)
        {
            if (!Mathf.Approximately(_slot2FlashlightBattery.Value, quantized))
                _slot2FlashlightBattery.Value = quantized;
        }
        else if (index == 3)
        {
            if (!Mathf.Approximately(_slot3FlashlightBattery.Value, quantized))
                _slot3FlashlightBattery.Value = quantized;
        }
    }

    int GetFirstEmptySlot()
    {
        for (int i = 0; i < SlotCapacity; i++)
        {
            if (GetSlotItemId(i) == 0UL)
                return i;
        }

        return -1;
    }

    public bool IsInventoryCompletelyFull => GetFirstEmptySlot() < 0;

    public bool HasItemInSelectedSlot
    {
        get
        {
            if (!IsSpawned)
                return false;
            return GetSlotItemId(SelectedSlotIndex) != 0UL;
        }
    }

    public bool IsSelectedItemFlashlight
    {
        get
        {
            if (!IsSpawned)
                return false;
            ulong id = GetSlotItemId(SelectedSlotIndex);
            if (id == 0UL)
                return false;
            if (!GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g) || g == null)
                return false;
            return g is FlashlightItem;
        }
    }

    void Awake()
    {
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
        _avatar = GetComponent<NetworkPlayerAvatar>();
    }

    public override void OnNetworkSpawn()
    {
        SceneManager.sceneLoaded += HandleGameplaySceneLoadedForPresentationRefresh;
        _slot0ItemId.OnValueChanged += OnSlot0Changed;
        _slot1ItemId.OnValueChanged += OnSlot1Changed;
        _slot2ItemId.OnValueChanged += OnSlot2Changed;
        _slot0ItemType.OnValueChanged += OnTypeChanged;
        _slot1ItemType.OnValueChanged += OnTypeChanged;
        _slot2ItemType.OnValueChanged += OnTypeChanged;
        _selectedSlot.OnValueChanged += OnSelectedChanged;
        _selectedFlashlightLightOn.OnValueChanged += OnFlashlightLightChanged;
        _slot0Stack.OnValueChanged += OnStackChanged;
        _slot1Stack.OnValueChanged += OnStackChanged;
        _slot2Stack.OnValueChanged += OnStackChanged;
        _slot3ItemId.OnValueChanged += OnSlot3Changed;
        _slot3ItemType.OnValueChanged += OnTypeChanged;
        _slot3Stack.OnValueChanged += OnStackChanged;
        // Drives the HUD row growing a box on every peer the moment the purchase lands.
        _extraSlotUnlocked.OnValueChanged += OnExtraSlotUnlockedChanged;

        if (IsServer)
            SendItemSnapshotToOwner();

        RaiseChangedAndRefresh();
    }

    public override void OnNetworkDespawn()
    {
        // NGO has already flipped IsSpawned to false by the time this runs, and on a genuine client disconnect the
        // owning player object — with every held/stashed item parented under its avatar — is destroyed immediately
        // afterward on every machine. Scatter those items into the world first so they are not destroyed with the
        // avatar hierarchy (which would delete e.g. the jail-door key and soft-lock the run).
        if (IsServer)
            ServerHandleDisconnectTeardownDrop();

        SceneManager.sceneLoaded -= HandleGameplaySceneLoadedForPresentationRefresh;
        _slot0ItemId.OnValueChanged -= OnSlot0Changed;
        _slot1ItemId.OnValueChanged -= OnSlot1Changed;
        _slot2ItemId.OnValueChanged -= OnSlot2Changed;
        _slot0ItemType.OnValueChanged -= OnTypeChanged;
        _slot1ItemType.OnValueChanged -= OnTypeChanged;
        _slot2ItemType.OnValueChanged -= OnTypeChanged;
        _selectedSlot.OnValueChanged -= OnSelectedChanged;
        _selectedFlashlightLightOn.OnValueChanged -= OnFlashlightLightChanged;
        _slot0Stack.OnValueChanged -= OnStackChanged;
        _slot1Stack.OnValueChanged -= OnStackChanged;
        _slot2Stack.OnValueChanged -= OnStackChanged;
        _slot3ItemId.OnValueChanged -= OnSlot3Changed;
        _slot3ItemType.OnValueChanged -= OnTypeChanged;
        _slot3Stack.OnValueChanged -= OnStackChanged;
        _extraSlotUnlocked.OnValueChanged -= OnExtraSlotUnlockedChanged;
    }

    void OnExtraSlotUnlockedChanged(bool previous, bool current)
    {
        RaiseChangedAndRefresh();
    }

    void HandleGameplaySceneLoadedForPresentationRefresh(Scene scene, LoadSceneMode mode)
    {
        if (!IsSpawned)
            return;
        if (!MultiplayerSceneFlow.IsMazeGameplayScene(scene.name))
            return;

        RaiseChangedAndRefresh();
    }

    // A hotbar slot names an item by id, and that id only resolves once the matching world pickup exists on
    // THIS peer. A joining client instantiates the deterministic maze pickups when it builds the level —
    // after this inventory spawned and ran its first refresh — and nothing re-ran the refresh afterwards, so
    // late joiners saw teammates empty-handed for the rest of the run. Retry briefly while anything is
    // unresolved, then give up with one warning.
    const float UnresolvedSlotRetryIntervalSeconds = 0.5f;
    const float UnresolvedSlotRetryWindowSeconds = 15f;
    float _unresolvedSlotRetryUntil;
    float _nextUnresolvedSlotRetryTime;

    /// <summary>
    /// Re-applies the held-item view after this peer finishes building its local level geometry, which is
    /// when the deterministic world pickups finally register. Safe to call on any peer.
    /// </summary>
    public void RefreshHeldItemViewAfterLocalWorldBuild()
    {
        if (!IsSpawned)
            return;

        RaiseChangedAndRefresh();
    }

    void ArmUnresolvedHeldItemRetry()
    {
        // Arm once per episode. The retry itself refreshes the view, and on the server that writes the
        // per-slot charge NetworkVariables, whose change callbacks land straight back here — re-arming on
        // every call slid the deadline forever, so a permanently unresolvable slot retried for the rest of
        // the session and the give-up warning never printed. Cleared (below) the moment everything resolves.
        if (_unresolvedSlotRetryUntil > 0f)
            return;

        if (!IsSpawned || !HasUnresolvedHeldItemSlot())
            return;

        _unresolvedSlotRetryUntil = Time.time + UnresolvedSlotRetryWindowSeconds;
        _nextUnresolvedSlotRetryTime = Time.time + UnresolvedSlotRetryIntervalSeconds;
    }

    bool HasUnresolvedHeldItemSlot()
    {
        for (int i = 0; i < MaxSlotCount; i++)
        {
            ulong id = GetSlotItemId(i);
            if (id == 0UL)
                continue;
            if (!GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g) || g == null)
                return true;
        }

        return false;
    }

    void TickUnresolvedHeldItemRetry()
    {
        if (_unresolvedSlotRetryUntil <= 0f)
            return;

        if (!IsSpawned || !HasUnresolvedHeldItemSlot())
        {
            _unresolvedSlotRetryUntil = 0f;
            return;
        }

        if (Time.time < _nextUnresolvedSlotRetryTime)
            return;

        if (Time.time >= _unresolvedSlotRetryUntil)
        {
            _unresolvedSlotRetryUntil = 0f;
            Debug.LogWarning(
                $"[{nameof(NetworkPlayerInventory)}] Gave up resolving a held item for '{name}': a hotbar slot"
                + $" names an item that never registered on this peer within {UnresolvedSlotRetryWindowSeconds:0}s.",
                this);
            return;
        }

        _nextUnresolvedSlotRetryTime = Time.time + UnresolvedSlotRetryIntervalSeconds;
        if (playerController != null)
            playerController.RefreshInventoryViewFromNetwork();
    }

    void Update()
    {
        TickUnresolvedHeldItemRetry();

        if (!IsServer || !IsSpawned)
            return;
        float dt = Time.deltaTime;
        Vector3 resolveHint = playerController != null ? playerController.transform.position : transform.position;
        // MaxSlotCount, not SlotCapacity: this only mirrors whatever a slot already holds, and a slot must
        // still report itself empty if capacity ever shrinks under it.
        for (int i = 0; i < MaxSlotCount; i++)
        {
            ulong id = GetSlotItemId(i);
            if (id == 0UL)
            {
                SetSlotFlashlightBatteryNormalized(i, 0f);
                continue;
            }
            if (!GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g)
                && !GrabbableInventoryItem.TryResolveForState(id, resolveHint, out g))
            {
                SetSlotFlashlightBatteryNormalized(i, 0f);
                continue;
            }
            if (g is FlareGunItem flareGun)
            {
                // The per-slot charge NetworkVariable is shared: battery fraction for flashlights,
                // loaded rounds / capacity for the flare gun (a slot only ever holds one of the two).
                SetSlotFlashlightBatteryNormalized(i, flareGun.LoadedRounds / (float)FlareGunItem.MaxRounds);
                continue;
            }
            if (g is not FlashlightItem f)
            {
                SetSlotFlashlightBatteryNormalized(i, 0f);
                continue;
            }
            f.TickBattery(dt);
            SetSlotFlashlightBatteryNormalized(i, f.BatteryFractionNormalized);
        }
        UpdateFlashlightSyncFromSelected();
    }

    void OnStackChanged(byte previous, byte current) { RaiseChangedAndRefresh(); }
    void OnTypeChanged(byte previous, byte current) { RaiseChangedAndRefresh(); }

    void OnSlot0Changed(ulong previous, ulong current) { RaiseChangedAndRefresh(); }
    void OnSlot1Changed(ulong previous, ulong current) { RaiseChangedAndRefresh(); }
    void OnSlot2Changed(ulong previous, ulong current) { RaiseChangedAndRefresh(); }
    void OnSlot3Changed(ulong previous, ulong current) { RaiseChangedAndRefresh(); }
    void OnSelectedChanged(byte previous, byte current) { RaiseChangedAndRefresh(); }
    void OnFlashlightLightChanged(bool previous, bool current) { RaiseChangedAndRefresh(); }

    void RaiseChangedAndRefresh()
    {
        OnInventoryChanged?.Invoke();
        playerController?.RefreshInventoryViewFromNetwork();
        ArmUnresolvedHeldItemRetry();
    }

    public bool CanPickup(GrabbableInventoryItem item)
    {
        if (item == null
            || !item.gameObject.activeInHierarchy
            || item.IsHeld)
            return false;

        // Stackable pickups (glowsticks, flare rounds) can also go into a partly-filled slot of the same type.
        if (item.IsStackable)
        {
            if (GetFirstEmptySlot() >= 0)
                return true;
            for (int i = 0; i < SlotCapacity; i++)
            {
                if (GetSlotItemId(i) == 0UL)
                    continue;
                if (!GrabbableInventoryItem.TryGetRegistered(GetSlotItemId(i), out GrabbableInventoryItem g)
                    || g == null || g.ItemTypeId != item.ItemTypeId)
                    continue;
                if (GetSlotStackCount(i) < g.MaxStackSize)
                    return true;
            }
            return false;
        }

        if (item is HeavyThrowableHoldItem)
            return true;

        return !IsInventoryCompletelyFull;
    }

    public void TryPickupItem(GrabbableInventoryItem item)
    {
        if (!CanPickup(item))
            return;

        if (IsServer)
        {
            ServerTryPickup(item.ItemId, item.transform.position);
            return;
        }

        RequestPickupItemServerRpc(item.ItemId, item.transform.position);
    }

    [ServerRpc]
    void RequestPickupItemServerRpc(ulong itemId, Vector3 worldHint, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerTryPickup(itemId, worldHint);
    }

    void ServerTryPickup(ulong itemId, Vector3 worldHint)
    {
        if (!IsServer)
            return;

        if (!GrabbableInventoryItem.TryResolveForPickup(itemId, worldHint, out GrabbableInventoryItem item) || item == null || item.IsHeld)
            return;

        if (item is HeavyThrowableHoldItem)
            return;

        // Server-side range gate. The pickup hint above is client-supplied (it's the item world position
        // the client claims to be next to), so range must be validated against the server's known player
        // transform vs the resolved item's transform, never the hint. Generous bounds cover any legitimate
        // walk-up pickup; anything beyond is a desync or a cheat.
        const float ServerMaxPickupHorizontal = 5f;
        const float ServerMaxPickupVertical = 3f;
        Vector3 playerPos = transform.position;
        Vector3 itemPos = item.transform.position;
        Vector3 flatDelta = new Vector3(itemPos.x - playerPos.x, 0f, itemPos.z - playerPos.z);
        if (flatDelta.sqrMagnitude > ServerMaxPickupHorizontal * ServerMaxPickupHorizontal)
            return;
        if (Mathf.Abs(itemPos.y - playerPos.y) > ServerMaxPickupVertical)
            return;

        if (item is FlashlightItem f0)
        {
            int empty = GetFirstEmptySlot();
            if (empty < 0)
                return;

            // Publish the picked-up torch's OWN beam state before the slot/selection writes below. Those writes
            // raise NGO's OnValueChanged synchronously, and the refresh they trigger applies whatever
            // _selectedFlashlightLightOn currently holds to whatever is now selected — which, with this line in
            // its old place after them, was the previously-held flashlight's state. Grabbing a second torch
            // while yours was lit therefore switched the new one on by itself and started draining it, and the
            // read-back that used to live here then agreed with the wrong state so nothing corrected it.
            _selectedFlashlightLightOn.Value = f0.IsLightOn;

            _selectedSlot.Value = (byte)empty;
            SetSlotItemId(empty, item.ItemId);
            SetSlotItemTypeId(empty, item.ItemTypeId);
            SetSlotStackCount(empty, 1);
            ApplyItemStateWithTypeClientRpc(item.ItemId, item.ItemTypeId, true, NetworkObjectId, item.transform.position, item.transform.rotation, default);
            return;
        }

        if (item.IsStackable)
        {
            // Top up every same-type slot first; whatever is left needs an empty slot of its own.
            int w = item.StackCount;
            for (int i = 0; i < SlotCapacity && w > 0; i++)
            {
                ulong slotId = GetSlotItemId(i);
                if (slotId == 0UL)
                    continue;
                if (!GrabbableInventoryItem.TryGetRegistered(slotId, out GrabbableInventoryItem inSlot)
                    || inSlot == null || inSlot.ItemTypeId != item.ItemTypeId)
                    continue;
                int c = GetSlotStackCount(i);
                int space = inSlot.MaxStackSize - c;
                if (space <= 0)
                    continue;
                int add = Mathf.Min(w, space);
                inSlot.SetStackCount(c + add);
                SetSlotStackCount(i, (byte)inSlot.StackCount);
                SetSlotItemTypeId(i, inSlot.ItemTypeId);
                w -= add;
            }
            if (w <= 0)
            {
                if (!ServerTryDespawnConsumedNetworkItem(item))
                {
                    ConsumedItemNetworkStore.ServerMarkConsumed(item.ItemId);
                    RemoveWorldItemClientRpc(item.ItemId);
                    Object.Destroy(item.gameObject);
                }
                return;
            }
            int emptyG = GetFirstEmptySlot();
            if (emptyG < 0)
            {
                item.SetStackCount(w);
                return;
            }
            item.SetStackCount(w);
            _selectedSlot.Value = (byte)emptyG;
            SetSlotItemId(emptyG, item.ItemId);
            SetSlotItemTypeId(emptyG, item.ItemTypeId);
            SetSlotStackCount(emptyG, (byte)w);
            _selectedFlashlightLightOn.Value = false;
            ApplyItemStateWithTypeClientRpc(item.ItemId, item.ItemTypeId, true, NetworkObjectId, item.transform.position, item.transform.rotation, default);
            return;
        }

        int emptyOther = GetFirstEmptySlot();
        if (emptyOther < 0)
            return;
        _selectedSlot.Value = (byte)emptyOther;
        SetSlotItemId(emptyOther, item.ItemId);
        SetSlotItemTypeId(emptyOther, item.ItemTypeId);
        SetSlotStackCount(emptyOther, 1);
        _selectedFlashlightLightOn.Value = false;
        ApplyItemStateWithTypeClientRpc(item.ItemId, item.ItemTypeId, true, NetworkObjectId, item.transform.position, item.transform.rotation, default);
    }

    public void TryDropSelectedItem(Vector3 dropPosition, Quaternion dropRotation, Vector3 dropForward)
    {
        Vector3 normalizedForward = dropForward.sqrMagnitude > 0.0001f ? dropForward.normalized : transform.forward;
        if (IsServer)
        {
            ServerDropSelectedItem(dropPosition, dropRotation, normalizedForward);
            return;
        }

        RequestDropSelectedItemServerRpc(dropPosition, dropRotation, normalizedForward);
    }

    [ServerRpc]
    void RequestDropSelectedItemServerRpc(Vector3 dropPosition, Quaternion dropRotation, Vector3 dropForward, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        Vector3 normalizedForward = dropForward.sqrMagnitude > 0.0001f ? dropForward.normalized : transform.forward;
        ServerDropSelectedItem(dropPosition, dropRotation, normalizedForward);
    }

    void ServerDropSelectedItem(Vector3 dropPosition, Quaternion dropRotation, Vector3 dropForward)
    {
        if (!IsServer || !HasItemInSelectedSlot)
            return;

        int sel = SelectedSlotIndex;
        ulong id = GetSlotItemId(sel);
        if (id == 0UL)
            return;

        if (!GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem item) || item == null)
        {
            SetSlotItemId(sel, 0UL);
            SetSlotItemTypeId(sel, GrabbableInventoryItem.TypeIdNone);
            SetSlotStackCount(sel, 0);
            SelectAfterDrop();
            return;
        }

        int stackForDrop = item.IsStackable ? GetSlotStackCount(sel) : 1;

        Vector3 norm = dropForward;
        if (norm.sqrMagnitude < 0.0001f)
            norm = transform.forward;
        norm.Normalize();
        Vector3 finalDropPosition = dropPosition + norm * 0.35f;
        finalDropPosition.y = Mathf.Max(finalDropPosition.y, transform.position.y + 0.1f);
        Quaternion finalDropRotation = item.transform.rotation;
        Vector3 throwImpulse = norm * dropThrowImpulse;

        // Dropping out of a stack peels off one unit and leaves the rest in the slot.
        if (item.IsStackable && stackForDrop > 1)
        {
            int next = stackForDrop - 1;
            item.SetStackCount(next);
            SetSlotStackCount(sel, (byte)next);
            ulong templateId = item.ItemId;
            ulong droppedItemId = ComputeRuntimeDroppedItemId(templateId, ++_runtimeDropSequence);

            // Recorded rather than broadcast one-shot: the peeled unit is neither Netcode-spawned nor derivable
            // from the maze seed, so a peer that missed the message could never reconstruct it. The store
            // delivers it to everyone connected now AND to late joiners, and retries for a client whose level
            // build has not registered the template yet.
            StackedDropNetworkStore.ServerRecordPeeledDrop(
                item, droppedItemId, finalDropPosition, finalDropRotation, throwImpulse);
            return;
        }

        // Capture the beam state BEFORE the slot writes below. NGO raises OnValueChanged synchronously on the
        // writer, so SetSlotItemId cascades straight into PlayerController's detach pass, which world-states this
        // very flashlight with lightEnabled=false. Reading IsLightOn afterwards therefore returned false every
        // time, and a flashlight dropped while lit — deliberately, as a corridor landmark, or scattered from a
        // dying player — always landed dark on every peer.
        bool droppedFlashlightLightOn = item is FlashlightItem preDropFlashlight && preDropFlashlight.IsLightOn;

        SetSlotItemId(sel, 0UL);
        SetSlotItemTypeId(sel, GrabbableInventoryItem.TypeIdNone);
        SetSlotStackCount(sel, 0);
        SelectAfterDrop();
        _selectedFlashlightLightOn.Value = false;

        if (item is FlashlightItem flashlight)
        {
            bool lightOn = droppedFlashlightLightOn;
            if (IsServer && !IsClient)
                flashlight.ApplyNetworkWorldState(finalDropPosition, finalDropRotation, lightOn, throwImpulse);
            else
                flashlight.ApplyNetworkWorldState(finalDropPosition, finalDropRotation, lightOn, default);

            ApplyItemStateWithTypeClientRpc(flashlight.ItemId, flashlight.ItemTypeId, false, 0UL, finalDropPosition, finalDropRotation, throwImpulse);

            // The shared world-state RPC carries no beam state, and every peer just switched its own copy off
            // when the slot emptied — so the lit/dark decision has to be replicated explicitly or observers
            // always see a dark torch on the floor.
            ApplyDroppedFlashlightLightClientRpc(flashlight.ItemId, lightOn);
        }
        else
        {
            if (item.IsStackable)
                item.SetStackCount(Mathf.Max(1, stackForDrop));
            item.ApplyNetworkWorldState(finalDropPosition, finalDropRotation, throwImpulse);
            if (item is GlowstickItem gForVis)
                gForVis.SetWorldDroppedVisual();

            ApplyItemStateWithTypeClientRpc(item.ItemId, item.ItemTypeId, false, 0UL, finalDropPosition, finalDropRotation, default);
        }
    }

    /// <summary>
    /// Replicates the beam state of a just-dropped flashlight. Separate from the shared world-state RPC, which
    /// every item type uses and which carries no light information.
    /// </summary>
    [ClientRpc]
    void ApplyDroppedFlashlightLightClientRpc(ulong itemId, bool lightOn, ClientRpcParams clientRpcParams = default)
    {
        if (!GrabbableInventoryItem.TryGetRegistered(itemId, out GrabbableInventoryItem g) || g == null)
            return;

        if (g is FlashlightItem dropped)
            dropped.SetLightEnabled(lightOn);
    }

    ulong ComputeRuntimeDroppedItemId(ulong templateItemId, uint sequence)
    {
        return ComputeStableHash($"runtime-drop:{NetworkObjectId}:{OwnerClientId}:{templateItemId}:{sequence}");
    }

    static ulong ComputeStableHash(string key)
    {
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        ulong hash = fnvOffset;
        for (int i = 0; i < key.Length; i++)
        {
            hash ^= key[i];
            hash *= fnvPrime;
        }

        return hash;
    }

    void SelectAfterDrop()
    {
        for (int i = 0; i < SlotCapacity; i++)
        {
            if (GetSlotItemId(i) != 0UL)
            {
                _selectedSlot.Value = (byte)i;
                UpdateFlashlightSyncFromSelected();
                return;
            }
        }

        _selectedSlot.Value = 0;
    }

    public void TryCycleSelection(int delta)
    {
        if (delta == 0)
            return;

        if (IsServer)
        {
            ServerCycleSelection(delta);
            return;
        }

        RequestCycleSelectionServerRpc((sbyte)delta);
    }

    [ServerRpc]
    void RequestCycleSelectionServerRpc(sbyte delta, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerCycleSelection(delta);
    }

    void ServerCycleSelection(int delta)
    {
        if (!IsServer)
            return;
        if (delta == 0)
            return;

        int sign = delta > 0 ? 1 : -1;
        int cur = _selectedSlot.Value;
        int next = cur + sign;
        int n = SlotCapacity;
        // Clamp first: if selection was already parked on a slot that is now out of capacity, wrapping a
        // larger index through the smaller modulus would jump somewhere arbitrary.
        int wrapped = ((next % n) + n) % n;

        // Publish the INCOMING slot's beam state before moving the selection, for the same reason as the pickup
        // path: the selection write refreshes synchronously and hands the outgoing torch's state to the newly
        // selected one, so scrolling off a lit flashlight onto a dark one switched the dark one on.
        UpdateFlashlightSyncForSlot(wrapped);
        _selectedSlot.Value = (byte)wrapped;
    }

    void UpdateFlashlightSyncFromSelected() => UpdateFlashlightSyncForSlot(SelectedSlotIndex);

    /// <summary>Publishes the beam state of the flashlight in <paramref name="slotIndex"/> (false if it holds none).</summary>
    void UpdateFlashlightSyncForSlot(int slotIndex)
    {
        if (!IsServer)
            return;

        ulong id = GetSlotItemId(slotIndex);
        if (id == 0UL
            || !GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g)
            || !(g is FlashlightItem f))
        {
            _selectedFlashlightLightOn.Value = false;
            return;
        }

        _selectedFlashlightLightOn.Value = f.IsLightOn;
    }

    public void TryToggleSelectedFlashlight()
    {
        if (IsServer)
        {
            ServerToggleSelectedFlashlight();
            return;
        }

        RequestToggleSelectedFlashlightServerRpc();
    }

    [ServerRpc]
    void RequestToggleSelectedFlashlightServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerToggleSelectedFlashlight();
    }

    void ServerToggleSelectedFlashlight()
    {
        if (!IsServer || !HasItemInSelectedSlot)
            return;

        ulong id = GetSlotItemId(SelectedSlotIndex);
        if (id == 0UL)
            return;

        if (!GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g) || !(g is FlashlightItem flashlight))
            return;

        bool wasOn = flashlight.IsLightOn;
        if (flashlight.IsLightOn)
            flashlight.SetLightEnabled(false);
        else if (flashlight.HasUsableBattery)
            flashlight.SetLightEnabled(true);
        _selectedFlashlightLightOn.Value = flashlight.IsLightOn;

        if (wasOn != flashlight.IsLightOn)
            PlayFlashlightClickObserversClientRpc();
    }

    [ClientRpc]
    void PlayFlashlightClickObserversClientRpc()
    {
        playerController?.PlayFlashlightClickSfx();
    }

    public void ServerDropAllHeldOnDeath()
    {
        if (!IsServer)
            return;

        Vector3 forward = transform.forward.sqrMagnitude > 0.0001f ? transform.forward : Vector3.forward;
        // Teardown sweeps run to MaxSlotCount so nothing can strand in the bought slot.
        for (int s = 0; s < MaxSlotCount; s++)
        {
            int safety = 0;
            while (GetSlotItemId(s) != 0UL)
            {
                if (++safety > 32)
                    break;
                _selectedSlot.Value = (byte)s;
                UpdateFlashlightSyncFromSelected();
                Transform holdPoint = null;
                Transform follow = null;
                if (playerController != null)
                    playerController.TryGetFlashlightAttachmentTargets(out holdPoint, out follow);

                Vector3 pos = holdPoint != null ? holdPoint.position : transform.position + transform.forward * 0.6f;
                Quaternion rot = follow != null ? follow.rotation : transform.rotation;
                ServerDropSelectedItem(pos, rot, forward);
            }
        }
    }

    void ServerHandleDisconnectTeardownDrop()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer)
            return;

        // Only a real client disconnect should scatter this player's items. A full-session teardown (host stop /
        // return to menu) sets ShutdownInProgress and discards everything anyway, and a synchronized Single scene
        // switch (elevator to the next section) despawns every player as it rebuilds the level from scratch. In
        // both cases dropping is pointless, and the relay mirror below could even reposition a fresh next-scene
        // item via its nearest-match fallback, so skip them.
        if (nm.ShutdownInProgress || s_serverLevelSceneSwitchInProgress)
            return;

        ServerScatterHeldItemsForDisconnect();
        ServerForceReleaseHeldHeavyThrowableForDisconnect();
    }

    /// <summary>
    /// Drops every held/stashed hotbar item into the world at the disconnect spot. Applies the resting world pose
    /// directly on the server/host copy (so it survives the avatar destruction that follows this callback) and
    /// mirrors that pose to the other clients through a surviving relay inventory — this inventory is already
    /// un-spawned here, so it can no longer deliver its own RPCs (see <see cref="ResolveServerRelayInventory"/>).
    /// </summary>
    void ServerScatterHeldItemsForDisconnect()
    {
        Vector3 basePosition = transform.position;
        Vector3 forward = transform.forward.sqrMagnitude > 0.0001f ? transform.forward.normalized : Vector3.forward;

        // Teardown sweep — MaxSlotCount, so a disconnect still scatters the bought slot's item.
        for (int i = 0; i < MaxSlotCount; i++)
        {
            ulong id = GetSlotItemId(i);
            byte typeId = GetSlotItemTypeId(i);
            if (id == 0UL && typeId == GrabbableInventoryItem.TypeIdNone)
                continue;

            GrabbableInventoryItem g = null;
            bool found = id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out g) && g != null;
            if (!found && typeId != GrabbableInventoryItem.TypeIdNone)
                found = GrabbableInventoryItem.TryResolveForStateByType(id, basePosition, typeId, out g);
            if (!found || g == null)
                continue;

            // Small forward fan so multiple slots do not land exactly on top of one another.
            Vector3 dropPosition = basePosition + forward * (0.35f + 0.2f * i);
            dropPosition.y = basePosition.y + 0.1f;
            Quaternion dropRotation = g.transform.rotation;

            if (g is FlashlightItem flashlight)
            {
                flashlight.ApplyNetworkWorldState(dropPosition, dropRotation, flashlight.IsLightOn, default);
            }
            else
            {
                if (g.IsStackable)
                    g.SetStackCount(Mathf.Max(1, GetSlotStackCount(i)));
                g.ApplyNetworkWorldState(dropPosition, dropRotation, default);
                if (g is GlowstickItem glowstickVisual)
                    glowstickVisual.SetWorldDroppedVisual();
            }

            ServerBroadcastDroppedItemWorldStateViaRelay(g, dropPosition, dropRotation);
        }
    }

    /// <summary>
    /// The one carry slot that lives outside the hotbar: a heavy throwable (StarBall / ring) held via
    /// <see cref="NetworkHeavyThrowableHold"/> is a spawned NetworkObject parented under the avatar, so it too
    /// would be destroyed with the avatar hierarchy. Force it back into the world before the teardown completes.
    /// </summary>
    void ServerForceReleaseHeldHeavyThrowableForDisconnect()
    {
        NetworkHeavyThrowableHold hold = NetworkHeavyThrowableHold.FindHeldByPlayerObjectId(NetworkObjectId);
        if (hold != null)
            hold.ServerForceReleaseForHolderDisconnect();
    }

    /// <summary>
    /// Bracket a synchronized <see cref="LoadSceneMode.Single"/> level switch (e.g. the elevator to the next
    /// section) so the player-object despawns it performs are not mistaken for client disconnects by
    /// <see cref="OnNetworkDespawn"/>. Call <see cref="BeginServerLevelSceneSwitch"/> immediately before
    /// NetworkSceneManager.LoadScene and <see cref="EndServerLevelSceneSwitch"/> immediately after; the player
    /// despawns happen synchronously inside that call.
    /// </summary>
    public static void BeginServerLevelSceneSwitch()
    {
        s_serverLevelSceneSwitchInProgress = true;
    }

    public static void EndServerLevelSceneSwitch()
    {
        s_serverLevelSceneSwitchInProgress = false;
    }

    /// <summary>
    /// Server-only: re-seats the hotbar this player carried in from the previous maze section onto their newly
    /// spawned avatar. Call immediately after <c>SpawnAsPlayerObject</c> — writing the slot NetworkVariables is
    /// what makes every peer's <see cref="PlayerController.RefreshInventoryViewFromNetwork"/> resolve the
    /// parked item objects (kept alive by <see cref="LevelCarryOverPen"/>) and attach them to the new avatar.
    /// A slot whose item did not survive on this machine is restored empty rather than left pointing at an id
    /// nothing can resolve.
    /// </summary>
    public void ServerRestoreCarriedInventory(LevelCarryOverStore.PlayerState carried)
    {
        if (!IsServer || !IsSpawned || !carried.HasValue)
            return;

        // Restore the bought slot FIRST: SlotCapacity gates the selection clamp at the bottom of this method,
        // so seating four items while capacity still reads three would park selection wrong.
        ServerRestoreExtraSlot(carried.HasExtraSlot);

        for (int i = 0; i < LevelCarryOverStore.SlotCount; i++)
        {
            LevelCarryOverStore.SlotState slot = carried.GetSlot(i);

            GrabbableInventoryItem item = null;
            bool resolved = slot.ItemId != 0UL
                && GrabbableInventoryItem.TryGetRegistered(slot.ItemId, out item)
                && item != null;

            // A network-spawned item (flashlight, jailor key) was despawned with the previous section, so the
            // slot is refilled with a freshly spawned one from the same prefab rather than a surviving object.
            bool spawnedReplacement = false;
            if (!resolved && slot.NetworkPrefabHash != 0)
            {
                // Only the selected slot's flashlight can be lit — stashing forces the beam off.
                bool slotLightOn = carried.FlashlightLightOn
                    && i == Mathf.Clamp(carried.SelectedSlot, 0, LevelCarryOverStore.SlotCount - 1);
                resolved = ServerTrySpawnCarriedNetworkItem(slot, slotLightOn, out item);
                spawnedReplacement = resolved;
            }

            if (!resolved)
            {
                SetSlotItemId(i, 0UL);
                SetSlotItemTypeId(i, GrabbableInventoryItem.TypeIdNone);
                SetSlotStackCount(i, 0);
                continue;
            }

            // The replacement's id comes from its new NetworkObjectId, which every peer derives identically.
            ulong itemId = spawnedReplacement ? item.ItemId : slot.ItemId;

            SetSlotItemId(i, itemId);
            SetSlotItemTypeId(i, slot.TypeId);
            SetSlotStackCount(i, slot.StackCount);

            if (item.IsStackable)
                item.SetStackCount(Mathf.Max(1, slot.StackCount));

            if (spawnedReplacement)
            {
                // Resolve by NetworkObjectId on the receiving side: the replacement has only just spawned, so
                // peers may not have derived its item id yet and a by-position match could pick the wrong one
                // when several players walk in carrying the same kind of item.
                AttachSpawnedCarriedItemClientRpc(item.SpawnedNetworkObjectId, NetworkObjectId);
                continue;
            }

            // This item is a LOCAL object that only exists on the peers that were connected at the switch (it
            // rode the carry-over pen across on each of them). Replicate it so anyone joining later can build
            // their own copy under the same id — without this the joiner has nothing the id can resolve to and
            // every path that names it, including a later drop, silently does nothing for them.
            CarriedItemNetworkStore.ServerRecordCarriedItem(
                itemId, slot.TypeId, slot.StackCount, slot.FlashlightBatteryNormalized);

            // Same broadcast the pickup path uses, so observers attach the item to this avatar even before
            // their own slot-change refresh runs.
            ApplyItemStateWithTypeClientRpc(
                itemId,
                slot.TypeId,
                true,
                NetworkObjectId,
                item.transform.position,
                item.transform.rotation,
                default);
        }

        _selectedSlot.Value = (byte)Mathf.Clamp(carried.SelectedSlot, 0, SlotCapacity - 1);
        _selectedFlashlightLightOn.Value = carried.FlashlightLightOn;
        UpdateFlashlightSyncFromSelected();
        RaiseChangedAndRefresh();
    }

    /// <summary>
    /// Server-only: builds the replacement for a carried network-spawned item. Spawned at the avatar so it is
    /// never left stranded in the world if the hand-off below fails, then handed its carried state (battery)
    /// before anything reads it.
    /// </summary>
    bool ServerTrySpawnCarriedNetworkItem(LevelCarryOverStore.SlotState slot, bool lightOn, out GrabbableInventoryItem item)
    {
        item = null;

        GameObject prefab = LevelCarryOverStore.FindRegisteredNetworkPrefabByHash(slot.NetworkPrefabHash);
        if (prefab == null)
        {
            Debug.LogWarning(
                $"[LevelCarryOver] Carried item prefab (hash {slot.NetworkPrefabHash}) is not in the NetworkManager prefab list; "
                + "the slot arrives empty. Register it in Resources/DefaultNetworkPrefabs.",
                this);
            return false;
        }

        GameObject instance = Instantiate(prefab, transform.position, transform.rotation);
        if (!instance.TryGetComponent(out NetworkObject networkObject)
            || !instance.TryGetComponent(out GrabbableInventoryItem spawned))
        {
            Destroy(instance);
            return false;
        }

        networkObject.Spawn();
        spawned.RefreshSpawnedNetworkItemId();

        if (spawned is FlashlightItem flashlight)
        {
            // Battery first: SetLightEnabled refuses to light a dead one, which is the correct outcome for a
            // player who walked into the elevator on their last few seconds of charge.
            flashlight.ApplyCarriedBattery(slot.FlashlightBatteryNormalized);
            flashlight.SetLightEnabled(lightOn);
        }

        item = spawned;
        return true;
    }

    /// <summary>
    /// Attaches a just-spawned replacement item to its carrier on every peer, addressing it by NetworkObjectId
    /// (the one identifier that is already valid the moment the spawn message lands).
    /// </summary>
    [ClientRpc]
    void AttachSpawnedCarriedItemClientRpc(ulong itemNetworkObjectId, ulong holderNetworkObjectId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return;

        if (!nm.SpawnManager.SpawnedObjects.TryGetValue(itemNetworkObjectId, out NetworkObject itemObject)
            || itemObject == null
            || !itemObject.TryGetComponent(out GrabbableInventoryItem item))
        {
            return;
        }

        // Derive the item id from the NetworkObjectId now rather than waiting a frame, so the slot value the
        // server wrote resolves on this peer immediately.
        item.RefreshSpawnedNetworkItemId();

        if (item is FlashlightItem flashlight)
        {
            _avatar?.NotifyFlashlightVisualAttach(flashlight);
            flashlight.ApplyNetworkHeldState(holderNetworkObjectId, flashlight.IsLightOn);
        }
        else
        {
            item.ApplyNetworkHeldState(holderNetworkObjectId);
        }

        playerController?.RefreshInventoryViewFromNetwork();
    }

    /// <summary>
    /// Client-side: ask the server to re-send the world-item snapshot now that this peer's local level build has
    /// registered the deterministic pickups.
    /// </summary>
    /// <remarks>
    /// The spawn-time snapshot (<see cref="OnNetworkSpawn"/>) cannot work for a joiner: it is sent the instant the
    /// player object spawns, and the joiner only requests the maze seed AFTER that, so its pickups do not exist for
    /// at least a round trip plus a full level build. Every entry therefore hits the exact-id lookup, finds nothing
    /// and is dropped, and the only retry that exists covers hotbar SLOTS — an item lying in the world belongs to no
    /// slot, so nothing ever re-applied its pose. The joiner was left looking at every item still sitting at its
    /// original spawn marker while the real ones had been carried elsewhere: invisible where they actually are, and
    /// unpickable where they appear (the server range-checks against the true position).
    /// </remarks>
    public void RequestWorldItemSnapshotAfterLocalWorldBuild()
    {
        // The server owns the authoritative item state, so it has nothing to ask for.
        if (!IsSpawned || !IsOwner || IsServer)
            return;

        RequestItemSnapshotServerRpc();
    }

    [ServerRpc]
    void RequestItemSnapshotServerRpc()
    {
        SendItemSnapshotToOwner();
    }

    void SendItemSnapshotToOwner()
    {
        ClientRpcParams targetOwner = new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        };

        foreach (GrabbableInventoryItem g in GrabbableInventoryItem.GetRegisteredItems())
        {
            if (g == null)
                continue;

            bool held = g.IsHeld;
            ulong holder = g.HolderNetworkObjectId;
            Vector3 p = g.transform.position;
            Quaternion r = g.transform.rotation;
            ApplyItemStateWithTypeClientRpc(
                g.ItemId,
                g.ItemTypeId,
                held,
                holder,
                p,
                r,
                default,
                targetOwner);

            // The snapshot RPC carries no beam state and its handler can only re-assert the joiner's own local
            // value, which is always false on a freshly built copy. A torch dropped lit before this peer joined
            // therefore has to be corrected explicitly from the server's copy.
            if (!held && g is FlashlightItem worldFlashlight)
                ApplyDroppedFlashlightLightClientRpc(worldFlashlight.ItemId, worldFlashlight.IsLightOn, targetOwner);
        }
    }

    [ClientRpc]
    void ApplyItemStateClientRpc(
        ulong itemId,
        bool isHeld,
        ulong holderNetworkObjectId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        Vector3 worldDropImpulse,
        ClientRpcParams clientRpcParams = default)
    {
        ApplyItemStateClientRpcWithType(itemId, GrabbableInventoryItem.TypeIdNone, isHeld, holderNetworkObjectId, worldPosition, worldRotation, worldDropImpulse, clientRpcParams);
    }

    [ClientRpc]
    void ApplyItemStateWithTypeClientRpc(
        ulong itemId,
        byte itemTypeId,
        bool isHeld,
        ulong holderNetworkObjectId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        Vector3 worldDropImpulse,
        ClientRpcParams clientRpcParams = default)
    {
        ApplyItemStateClientRpcWithType(itemId, itemTypeId, isHeld, holderNetworkObjectId, worldPosition, worldRotation, worldDropImpulse, clientRpcParams);
    }

    void ApplyItemStateClientRpcWithType(
        ulong itemId,
        byte itemTypeId,
        bool isHeld,
        ulong holderNetworkObjectId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        Vector3 worldDropImpulse,
        ClientRpcParams clientRpcParams = default)
    {
        GrabbableInventoryItem g = null;
        bool found = itemTypeId != GrabbableInventoryItem.TypeIdNone
            ? GrabbableInventoryItem.TryResolveForStateByType(itemId, worldPosition, itemTypeId, out g)
            : GrabbableInventoryItem.TryResolveForState(itemId, worldPosition, out g);

        if (!found || g == null)
            return;

        if (g.ItemId != itemId)
            g.AssignNetworkItemId(itemId);

        if (isHeld)
        {
            if (g is FlashlightItem f)
            {
                _avatar?.NotifyFlashlightVisualAttach(f);
                f.ApplyNetworkHeldState(holderNetworkObjectId, f.IsLightOn);
            }
            else
            {
                g.ApplyNetworkHeldState(holderNetworkObjectId);
            }
        }
        else
        {
            if (g is FlashlightItem f2)
            {
                f2.ApplyNetworkWorldState(worldPosition, worldRotation, f2.IsLightOn, worldDropImpulse);
            }
            else
            {
                g.ApplyNetworkWorldState(worldPosition, worldRotation, worldDropImpulse);
            }

            if (g is GlowstickItem gStick)
            {
                gStick.SetWorldDroppedVisual();
            }
        }

        playerController?.RefreshInventoryViewFromNetwork();
    }

    public void RequestUseSelectedBandage()
    {
        if (!IsSpawned)
            return;

        if (IsServer)
        {
            ServerUseSelectedBandage();
            return;
        }

        RequestUseSelectedBandageServerRpc();
    }

    [ServerRpc]
    void RequestUseSelectedBandageServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerUseSelectedBandage();
    }

    void ServerUseSelectedBandage()
    {
        if (!IsServer)
            return;

        int sel = SelectedSlotIndex;
        ulong id = GetSlotItemId(sel);
        bool slotSaysBandage = GetSlotItemTypeId(sel) == GrabbableInventoryItem.TypeIdBandage;
        if (id == 0UL && !slotSaysBandage)
            return;

        GrabbableInventoryItem g = null;
        bool resolved = id != 0UL
            && GrabbableInventoryItem.TryGetRegistered(id, out g)
            && g is BandageItem;
        if (!resolved && slotSaysBandage)
        {
            Vector3 hint = playerController != null ? playerController.transform.position : transform.position;
            resolved = GrabbableInventoryItem.TryResolveForStateByType(
                id,
                hint,
                GrabbableInventoryItem.TypeIdBandage,
                out g)
                && g is BandageItem;
        }

        if (!resolved || g == null)
            return;

        PlayerHealth health = playerController != null
            ? playerController.GetComponent<PlayerHealth>()
            : GetComponent<PlayerHealth>();
        if (health == null || health.IsDead || health.CurrentHealth >= health.MaxHealth)
            return;

        health.Heal(BandageItem.HealthRestoreAmount);
        PlayBandageUseObserversClientRpc();

        SetSlotItemId(sel, 0UL);
        SetSlotItemTypeId(sel, GrabbableInventoryItem.TypeIdNone);
        SetSlotStackCount(sel, 0);
        _selectedFlashlightLightOn.Value = false;
        if (!ServerTryDespawnConsumedNetworkItem(g))
        {
            ulong consumeId = g.ItemId;
            ConsumedItemNetworkStore.ServerMarkConsumed(consumeId);
            ConsumeItemClientRpc(consumeId);
            Object.Destroy(g.gameObject);
        }
        SelectAfterDrop();
        RaiseChangedAndRefresh();
    }

    [ClientRpc]
    void PlayBandageUseObserversClientRpc()
    {
        playerController?.PlayBandageUseSfx();
    }

    public void RequestUseSelectedEnergyDrink()
    {
        if (!IsSpawned)
            return;

        if (IsServer)
        {
            ServerUseSelectedEnergyDrink();
            return;
        }

        RequestUseSelectedEnergyDrinkServerRpc();
    }

    [ServerRpc]
    void RequestUseSelectedEnergyDrinkServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerUseSelectedEnergyDrink();
    }

    void ServerUseSelectedEnergyDrink()
    {
        if (!IsServer)
            return;

        int sel = SelectedSlotIndex;
        ulong id = GetSlotItemId(sel);
        bool slotSaysDrink = GetSlotItemTypeId(sel) == GrabbableInventoryItem.TypeIdEnergyDrink;
        if (id == 0UL && !slotSaysDrink)
            return;

        GrabbableInventoryItem g = null;
        bool resolved = id != 0UL
            && GrabbableInventoryItem.TryGetRegistered(id, out g)
            && g is EnergyDrinkItem;
        if (!resolved && slotSaysDrink)
        {
            Vector3 hint = playerController != null ? playerController.transform.position : transform.position;
            resolved = GrabbableInventoryItem.TryResolveForStateByType(
                id,
                hint,
                GrabbableInventoryItem.TypeIdEnergyDrink,
                out g)
                && g is EnergyDrinkItem;
        }

        if (!resolved || g is not EnergyDrinkItem drink)
            return;

        // Movement/stamina/HUD are owner-side, so hand the effect to the owner's controller. Read the
        // buff tunables off the authoritative item instance the server just resolved.
        StartEnergyDrinkBoostOwnerClientRpc(
            drink.BoostDurationSeconds,
            drink.SpeedMultiplier,
            BuildOwnerClientRpcParams());

        // The gulp is diegetic (positional), so play it on every observer like the bandage — not owner-only.
        PlayEnergyDrinkUseObserversClientRpc();

        SetSlotItemId(sel, 0UL);
        SetSlotItemTypeId(sel, GrabbableInventoryItem.TypeIdNone);
        SetSlotStackCount(sel, 0);
        _selectedFlashlightLightOn.Value = false;
        if (!ServerTryDespawnConsumedNetworkItem(g))
        {
            ulong consumeId = g.ItemId;
            ConsumedItemNetworkStore.ServerMarkConsumed(consumeId);
            ConsumeItemClientRpc(consumeId);
            Object.Destroy(g.gameObject);
        }
        SelectAfterDrop();
        RaiseChangedAndRefresh();
    }

    ClientRpcParams BuildOwnerClientRpcParams()
    {
        return new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        };
    }

    [ClientRpc]
    void StartEnergyDrinkBoostOwnerClientRpc(float durationSeconds, float speedMultiplier, ClientRpcParams clientRpcParams = default)
    {
        playerController?.ActivateEnergyDrinkBoost(durationSeconds, speedMultiplier);
    }

    [ClientRpc]
    void PlayEnergyDrinkUseObserversClientRpc()
    {
        playerController?.PlayEnergyDrinkUseSfx();
    }

    // ----- Flare gun: fire + reload (server-authoritative) -----

    static readonly List<ulong> s_FlareFxObserverClientIds = new List<ulong>(16);

    float _serverNextFlareFireTime;
    float _serverFlareBusyUntil;

    /// <summary>Owner-side request to fire the selected flare gun toward the camera aim.</summary>
    public void RequestFireSelectedFlareGun(Vector3 origin, Vector3 direction)
    {
        if (!IsSpawned)
            return;

        if (IsServer)
        {
            ServerFireSelectedFlareGun(origin, direction);
            return;
        }

        RequestFireSelectedFlareGunServerRpc(origin, direction);
    }

    [ServerRpc]
    void RequestFireSelectedFlareGunServerRpc(Vector3 origin, Vector3 direction, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerFireSelectedFlareGun(origin, direction);
    }

    void ServerFireSelectedFlareGun(Vector3 origin, Vector3 direction)
    {
        if (!IsServer)
            return;

        PlayerHealth health = playerController != null
            ? playerController.GetComponent<PlayerHealth>()
            : GetComponent<PlayerHealth>();
        if (health != null && health.IsDead)
            return;

        float now = Time.time;
        if (now < _serverNextFlareFireTime || now < _serverFlareBusyUntil)
            return;

        if (!ServerTryResolveSelectedFlareGun(out FlareGunItem gun))
            return;

        if (!gun.TryConsumeRound())
            return;

        _serverNextFlareFireTime = now + FlareGunItem.FireCooldownSeconds * 0.9f;

        // The aim is client-supplied (camera position + forward, the same trust model as heavy-throwable
        // shots) but the origin is clamped to the server-known player so a client can't fire from across
        // the map.
        Vector3 shooterHead = transform.position + Vector3.up * 1.5f;
        if ((origin - shooterHead).sqrMagnitude > 9f)
            origin = shooterHead;
        Vector3 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;

        if (gun.ProjectilePrefab != null)
        {
            GameObject go = Object.Instantiate(gun.ProjectilePrefab, origin, Quaternion.LookRotation(dir));
            if (go.TryGetComponent(out FlareProjectile projectile))
            {
                projectile.Launch(origin, dir, transform, health);
                if (go.TryGetComponent(out NetworkObject netObj))
                    netObj.Spawn();
            }
            else
            {
                Object.Destroy(go);
            }
        }

        PlayFlareFireFxForNonOwnerClients();
    }

    bool ServerTryResolveSelectedFlareGun(out FlareGunItem gun)
    {
        gun = null;
        int sel = SelectedSlotIndex;
        if (GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdFlareGun)
            return false;

        ulong id = GetSlotItemId(sel);
        GrabbableInventoryItem g = null;
        bool resolved = id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out g) && g is FlareGunItem;
        if (!resolved)
        {
            Vector3 hint = playerController != null ? playerController.transform.position : transform.position;
            resolved = GrabbableInventoryItem.TryResolveForStateByType(id, hint, GrabbableInventoryItem.TypeIdFlareGun, out g)
                && g is FlareGunItem;
        }

        gun = g as FlareGunItem;
        return gun != null;
    }

    /// <summary>The owner predicted its own muzzle flash; every other client gets it from the server.</summary>
    void PlayFlareFireFxForNonOwnerClients()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        s_FlareFxObserverClientIds.Clear();
        foreach (ulong id in nm.ConnectedClientsIds)
        {
            if (id != OwnerClientId)
                s_FlareFxObserverClientIds.Add(id);
        }

        if (s_FlareFxObserverClientIds.Count == 0)
            return;

        PlayFlareFireFxClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = s_FlareFxObserverClientIds.ToArray() }
        });
    }

    [ClientRpc]
    void PlayFlareFireFxClientRpc(ClientRpcParams clientRpcParams = default)
    {
        if (ServerTryResolveSelectedFlareGunLocalView(out FlareGunItem gun))
            gun.PlayFireEffects();
    }

    /// <summary>
    /// Owner-side: the empty-chamber click. Purely cosmetic and owner-predicted, so it never went through the
    /// server at all — which made it inaudible to anyone else. Same narrowcast as the fire FX above.
    /// </summary>
    public void NotifyFlareDryFire()
    {
        if (!IsSpawned)
            return;

        if (IsServer)
        {
            PlayFlareDryFireFxForNonOwnerClients();
            return;
        }

        RequestFlareDryFireFxServerRpc();
    }

    [ServerRpc]
    void RequestFlareDryFireFxServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        PlayFlareDryFireFxForNonOwnerClients();
    }

    void PlayFlareDryFireFxForNonOwnerClients()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        s_FlareFxObserverClientIds.Clear();
        foreach (ulong id in nm.ConnectedClientsIds)
        {
            if (id != OwnerClientId)
                s_FlareFxObserverClientIds.Add(id);
        }

        if (s_FlareFxObserverClientIds.Count == 0)
            return;

        PlayFlareDryFireFxClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = s_FlareFxObserverClientIds.ToArray() }
        });
    }

    [ClientRpc]
    void PlayFlareDryFireFxClientRpc(ClientRpcParams clientRpcParams = default)
    {
        if (ServerTryResolveSelectedFlareGunLocalView(out FlareGunItem gun))
            gun.PlayDryFireSfx();
    }

    /// <summary>Client-side resolve of this inventory's selected flare gun (replicated slot data + local registry).</summary>
    bool ServerTryResolveSelectedFlareGunLocalView(out FlareGunItem gun)
    {
        gun = null;
        int sel = SelectedSlotIndex;
        if (GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdFlareGun)
            return false;

        ulong id = GetSlotItemId(sel);
        if (id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g))
            gun = g as FlareGunItem;
        return gun != null;
    }

    /// <summary>Owner-side request to fill the selected flare gun from carried flare rounds.</summary>
    public void RequestReloadSelectedFlareGun()
    {
        if (!IsSpawned)
            return;

        if (IsServer)
        {
            ServerReloadSelectedFlareGun();
            return;
        }

        RequestReloadSelectedFlareGunServerRpc();
    }

    [ServerRpc]
    void RequestReloadSelectedFlareGunServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerReloadSelectedFlareGun();
    }

    void ServerReloadSelectedFlareGun()
    {
        if (!IsServer)
            return;

        PlayerHealth health = playerController != null
            ? playerController.GetComponent<PlayerHealth>()
            : GetComponent<PlayerHealth>();
        if (health != null && health.IsDead)
            return;

        if (Time.time < _serverFlareBusyUntil)
            return;

        if (!ServerTryResolveSelectedFlareGun(out FlareGunItem gun))
            return;

        int needed = gun.MissingRounds;
        if (needed <= 0)
            return;

        // One reload fills the gun: draw from the FlareAmmo stacks in any hotbar slot until it is full or
        // the player runs out (selection stays on the gun either way).
        int drawn = 0;
        for (int i = 0; i < SlotCapacity && drawn < needed; i++)
        {
            ulong id = GetSlotItemId(i);
            bool slotSaysAmmo = GetSlotItemTypeId(i) == GrabbableInventoryItem.TypeIdFlareAmmo;
            if (id == 0UL && !slotSaysAmmo)
                continue;

            GrabbableInventoryItem g = null;
            bool resolved = id != 0UL
                && GrabbableInventoryItem.TryGetRegistered(id, out g)
                && g is FlareAmmoItem;
            if (!resolved && slotSaysAmmo)
            {
                Vector3 hint = playerController != null ? playerController.transform.position : transform.position;
                resolved = GrabbableInventoryItem.TryResolveForStateByType(id, hint, GrabbableInventoryItem.TypeIdFlareAmmo, out g)
                    && g is FlareAmmoItem;
            }

            if (!resolved || g == null)
                continue;

            int inStack = Mathf.Max(1, GetSlotStackCount(i));
            int take = Mathf.Min(inStack, needed - drawn);
            drawn += take;

            if (take >= inStack)
            {
                // Stack spent: free the slot and destroy the item object on every peer.
                SetSlotItemId(i, 0UL);
                SetSlotItemTypeId(i, GrabbableInventoryItem.TypeIdNone);
                SetSlotStackCount(i, 0);
                if (!ServerTryDespawnConsumedNetworkItem(g))
                {
                    ulong consumeId = g.ItemId;
                    ConsumedItemNetworkStore.ServerMarkConsumed(consumeId);
                    ConsumeItemClientRpc(consumeId);
                    Object.Destroy(g.gameObject);
                }
            }
            else
            {
                int remaining = inStack - take;
                g.SetStackCount(remaining);
                SetSlotStackCount(i, (byte)remaining);
            }
        }

        if (drawn <= 0)
            return;

        gun.TryAddRounds(drawn);
        _serverFlareBusyUntil = Time.time + FlareGunItem.ReloadDurationForRounds(drawn);

        PlayFlareReloadFxClientRpc(drawn);
        RaiseChangedAndRefresh();
    }

    /// <summary><paramref name="rounds"/> is how many rounds the server actually loaded, so every peer plays
    /// the matching reload length (one off-hand trip per round).</summary>
    [ClientRpc]
    void PlayFlareReloadFxClientRpc(int rounds)
    {
        if (ServerTryResolveSelectedFlareGunLocalView(out FlareGunItem gun))
            playerController?.PlayFlareReloadEffects(gun, rounds);
    }

    public bool ServerTryConsumeKeyItem()
    {
        if (!IsServer || !IsSpawned)
            return false;

        for (int i = 0; i < SlotCapacity; i++)
        {
            ulong id = GetSlotItemId(i);
            bool slotSaysKey = GetSlotItemTypeId(i) == GrabbableInventoryItem.TypeIdKey;
            if (id == 0UL && !slotSaysKey)
                continue;

            GrabbableInventoryItem g = null;
            bool resolvedKey = id != 0UL
                && GrabbableInventoryItem.TryGetRegistered(id, out g)
                && g is KeyItem;
            if (!slotSaysKey && !resolvedKey)
                continue;

            SetSlotItemId(i, 0UL);
            SetSlotItemTypeId(i, GrabbableInventoryItem.TypeIdNone);
            SetSlotStackCount(i, 0);
            if (g != null && !ServerTryDespawnConsumedNetworkItem(g))
            {
                ConsumedItemNetworkStore.ServerMarkConsumed(id);
                ConsumeItemClientRpc(id);
                Object.Destroy(g.gameObject);
            }
            SelectAfterDrop();
            RaiseChangedAndRefresh();
            return true;
        }

        return false;
    }

    bool ServerHasKeyItem()
    {
        if (!IsServer || !IsSpawned)
            return false;

        for (int i = 0; i < SlotCapacity; i++)
        {
            ulong id = GetSlotItemId(i);
            bool slotSaysKey = GetSlotItemTypeId(i) == GrabbableInventoryItem.TypeIdKey;
            if (id == 0UL && !slotSaysKey)
                continue;

            if (slotSaysKey)
                return true;

            if (id != 0UL
                && GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g)
                && g is KeyItem)
                return true;
        }

        return false;
    }

    [ClientRpc]
    void ConsumeItemClientRpc(ulong itemId)
    {
        DestroyRegisteredItem(itemId);
        playerController?.RefreshInventoryViewFromNetwork();
    }

    [ClientRpc]
    void RemoveWorldItemClientRpc(ulong itemId)
    {
        DestroyRegisteredItem(itemId);
        playerController?.RefreshInventoryViewFromNetwork();
    }

    /// <summary>
    /// Server-only teardown for a permanently-consumed item that is a real spawned NetworkObject (the carnival
    /// ticket key, the Jailor's key — every other pickup is a local copy each peer builds from the seed).
    /// Despawning is the only correct removal for those: NGO tears the replica down on every peer, late joiners
    /// included, whereas the tombstone-and-destroy pair asks each client to destroy a live NetworkObject it has
    /// no authority over. Returns true once the item is gone, i.e. the caller must skip its own teardown.
    /// </summary>
    static bool ServerTryDespawnConsumedNetworkItem(GrabbableInventoryItem item)
    {
        if (item == null || !item.IsNetworkSpawnedItem || !item.TryGetComponent(out NetworkObject itemObject))
            return false;

        itemObject.Despawn(true);
        return true;
    }

    static void DestroyRegisteredItem(ulong itemId)
    {
        if (itemId == 0UL)
            return;

        if (!GrabbableInventoryItem.TryGetRegistered(itemId, out GrabbableInventoryItem item) || item == null)
            return;

        // Only the server may tear down a spawned NetworkObject. A pure client that destroys its own replica
        // strands the entry in NGO's spawn table forever — the server's despawn message then finds a
        // Unity-null object and bails, and the next spawn that draws the recycled NetworkObjectId is refused
        // as "already in the spawned list" and never appears on that peer.
        NetworkManager nm = NetworkManager.Singleton;
        if (item.IsNetworkSpawnedItem && nm != null && nm.IsListening && !nm.IsServer)
            return;

        Object.Destroy(item.gameObject);
    }

    public void RequestPickupHeavyThrowable(ulong itemId, byte itemTypeId, Vector3 worldHint)
    {
        if (!IsSpawned)
            return;
        if (IsServer)
        {
            ServerTryPickupHeavyThrowable(itemId, itemTypeId, worldHint, OwnerClientId);
            return;
        }

        RequestPickupHeavyThrowableServerRpc(itemId, itemTypeId, worldHint);
    }

    public void RequestDropHeavyThrowable(ulong itemId, byte itemTypeId, Vector3 worldHint)
    {
        if (!IsSpawned)
            return;
        if (IsServer)
        {
            ServerTryDropHeavyThrowable(itemId, itemTypeId, worldHint, OwnerClientId);
            return;
        }

        RequestDropHeavyThrowableServerRpc(itemId, itemTypeId, worldHint);
    }

    public void RequestShootHeavyThrowable(ulong itemId, byte itemTypeId, Vector3 worldHint, Vector3 cameraForward, float charge01)
    {
        if (!IsSpawned)
            return;
        if (IsServer)
        {
            ServerTryShootHeavyThrowable(itemId, itemTypeId, worldHint, cameraForward, charge01, OwnerClientId);
            return;
        }

        RequestShootHeavyThrowableServerRpc(itemId, itemTypeId, worldHint, cameraForward, charge01);
    }

    [ServerRpc]
    void RequestPickupHeavyThrowableServerRpc(ulong itemId, byte itemTypeId, Vector3 worldHint, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;
        ServerTryPickupHeavyThrowable(itemId, itemTypeId, worldHint, serverRpcParams.Receive.SenderClientId);
    }

    [ServerRpc]
    void RequestDropHeavyThrowableServerRpc(ulong itemId, byte itemTypeId, Vector3 worldHint, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;
        ServerTryDropHeavyThrowable(itemId, itemTypeId, worldHint, serverRpcParams.Receive.SenderClientId);
    }

    [ServerRpc]
    void RequestShootHeavyThrowableServerRpc(ulong itemId, byte itemTypeId, Vector3 worldHint, Vector3 cameraForward, float charge01, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;
        ServerTryShootHeavyThrowable(itemId, itemTypeId, worldHint, cameraForward, charge01, serverRpcParams.Receive.SenderClientId);
    }

    void ServerTryPickupHeavyThrowable(ulong itemId, byte itemTypeId, Vector3 worldHint, ulong senderClientId)
    {
        if (!TryResolveHeavyThrowable(itemId, itemTypeId, worldHint, out NetworkHeavyThrowableHold hold))
            return;
        hold.ServerTryPickupFromRelay(NetworkObjectId, senderClientId);
    }

    void ServerTryDropHeavyThrowable(ulong itemId, byte itemTypeId, Vector3 worldHint, ulong senderClientId)
    {
        if (!TryResolveHeavyThrowable(itemId, itemTypeId, worldHint, out NetworkHeavyThrowableHold hold))
            return;
        hold.ServerTryDropFromRelay(NetworkObjectId, senderClientId);
    }

    void ServerTryShootHeavyThrowable(ulong itemId, byte itemTypeId, Vector3 worldHint, Vector3 cameraForward, float charge01, ulong senderClientId)
    {
        if (!TryResolveHeavyThrowable(itemId, itemTypeId, worldHint, out NetworkHeavyThrowableHold hold))
            return;
        hold.ServerTryShootFromRelay(NetworkObjectId, senderClientId, cameraForward, charge01);
    }

    static bool TryResolveHeavyThrowable(ulong itemId, byte itemTypeId, Vector3 worldHint, out NetworkHeavyThrowableHold hold)
    {
        hold = null;
        if (!GrabbableInventoryItem.TryResolveForStateByType(itemId, worldHint, itemTypeId, out GrabbableInventoryItem item)
            || item == null
            || item is not HeavyThrowableHoldItem
            || !item.TryGetComponent(out hold))
        {
            return false;
        }

        return hold != null;
    }

    public static void ServerBroadcastHeavyThrowableStateIfNeeded(
        GrabbableInventoryItem item,
        bool isHeld,
        ulong holderNetworkObjectId,
        Vector3 worldPosition = default,
        Quaternion worldRotation = default)
    {
        if (item == null)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;

        NetworkPlayerInventory relay = ResolveServerRelayInventory();
        if (relay == null)
            return;

        if (worldPosition == default)
            worldPosition = isHeld ? item.IdentityHintPosition : item.transform.position;
        if (worldRotation == default)
            worldRotation = item.transform.rotation;

        // Heavy throwable state on the host is already applied directly via NetworkHeavyThrowableHold
        // server paths; if the host also receives this ClientRpc it would re-run ApplyNetworkWorldState,
        // which calls EndHeldState and zeroes the rigidbody velocity (breaking throws). Target only
        // non-server clients so the host keeps its own server-side physics state.
        if (!TryBuildNonServerClientRpcTarget(nm, out ClientRpcParams clientsExcludingHost))
            return;

        relay.ApplyItemStateWithTypeClientRpc(
            item.ItemId,
            item.ItemTypeId,
            isHeld,
            holderNetworkObjectId,
            worldPosition,
            worldRotation,
            default,
            clientsExcludingHost);
    }

    static bool TryBuildNonServerClientRpcTarget(NetworkManager nm, out ClientRpcParams clientRpcParams)
    {
        clientRpcParams = default;
        if (nm == null || !nm.IsServer)
            return false;

        IReadOnlyList<ulong> connected = nm.ConnectedClientsIds;
        if (connected == null || connected.Count == 0)
            return false;

        ulong serverClientId = nm.LocalClientId;
        List<ulong> targets = new List<ulong>(connected.Count);
        for (int i = 0; i < connected.Count; i++)
        {
            ulong id = connected[i];
            if (id != serverClientId)
                targets.Add(id);
        }

        if (targets.Count == 0)
            return false;

        clientRpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = targets.ToArray()
            }
        };
        return true;
    }

    /// <summary>
    /// Mirror a disconnect-scattered hotbar item's resting world pose to every client except the host (which has
    /// already applied it directly). Routed through a surviving relay inventory because the owning inventory is
    /// mid-despawn and can no longer send its own RPCs.
    /// </summary>
    static void ServerBroadcastDroppedItemWorldStateViaRelay(GrabbableInventoryItem item, Vector3 worldPosition, Quaternion worldRotation)
    {
        if (item == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;

        NetworkPlayerInventory relay = ResolveServerRelayInventory();
        if (relay == null)
            return;

        if (!TryBuildNonServerClientRpcTarget(nm, out ClientRpcParams clientsExcludingHost))
            return;

        relay.ApplyItemStateWithTypeClientRpc(
            item.ItemId,
            item.ItemTypeId,
            false,
            0UL,
            worldPosition,
            worldRotation,
            default,
            clientsExcludingHost);
    }

    public static void ServerBroadcastRigidbodyImpactSfx(RigidbodyImpactSfx impactSfx, float volume01)
    {
        if (impactSfx == null)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;

        if (!impactSfx.TryGetComponent(out GrabbableInventoryItem item) || item == null)
            return;

        NetworkPlayerInventory relay = ResolveServerRelayInventory();
        if (relay == null)
            return;

        if (!TryBuildNonServerClientRpcTarget(nm, out ClientRpcParams clientsExcludingHost))
            return;

        relay.PlayRigidbodyImpactSfxClientRpc(
            item.ItemId,
            item.ItemTypeId,
            item.transform.position,
            Mathf.Clamp01(volume01),
            clientsExcludingHost);
    }

    [ClientRpc]
    void PlayRigidbodyImpactSfxClientRpc(
        ulong itemId,
        byte itemTypeId,
        Vector3 worldHint,
        float volume01,
        ClientRpcParams clientRpcParams = default)
    {
        bool found = itemTypeId != GrabbableInventoryItem.TypeIdNone
            ? GrabbableInventoryItem.TryResolveForStateByType(itemId, worldHint, itemTypeId, out GrabbableInventoryItem item)
            : GrabbableInventoryItem.TryResolveForState(itemId, worldHint, out item);

        if (!found || item == null)
            return;

        if (item.TryGetComponent(out RigidbodyImpactSfx impactSfx) && impactSfx != null)
            impactSfx.PlayReplicatedImpact(volume01);
    }

    public void RequestUnlockHingeDoor(HingeInteractDoor door)
    {
        if (door == null)
            return;
        if (!IsSpawned)
            return;

        if (!door.TryGetComponent(out NetworkObject doorNet) || !doorNet.IsSpawned)
        {
            ulong doorId = door.DoorId;
            Vector3 hintPosition = door.IdentityHintPosition;
            if (IsServer)
            {
                if (!TryGetConnectedPlayerPosition(OwnerClientId, out Vector3 playerPosition))
                    return;
                if (!door.IsLocked || door.IsBusy || !door.IsInInteractRange(playerPosition))
                    return;
                if (!ServerHasKeyItem())
                    return;
                door.ApplyProceduralRemoteUnlock();
                if (door.IsLocked)
                    return;
                if (!ServerTryConsumeKeyItem())
                    return;
                DoorNetworkStateStore.ServerPublish(door);
                return;
            }

            RequestUnlockProceduralHingeDoorServerRpc(doorId, hintPosition);
            return;
        }

        if (IsServer)
        {
            if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(OwnerClientId, out NetworkClient client)
                || client.PlayerObject == null)
            {
                return;
            }
            if (!door.IsInInteractRange(client.PlayerObject.transform.position))
                return;
            if (door.IsBusy)
                return;
            if (!ServerHasKeyItem())
                return;
            if (!door.ServerUnlockFromKey())
                return;
            ServerBroadcastProceduralDoorUnlockIfNeeded(door);
            if (!ServerTryConsumeKeyItem())
                return;
            return;
        }

        RequestUnlockHingeDoorServerRpc(doorNet.NetworkObjectId);
    }

    public void RequestToggleHingeDoor(HingeInteractDoor door)
    {
        if (door == null || !IsSpawned)
            return;

        if (door.TryGetComponent(out NetworkObject doorNet) && doorNet.IsSpawned)
        {
            // Spawned doors already synchronize themselves through HingeInteractDoor's RPC flow.
            door.TryRequestToggle(transform.position);
            return;
        }

        ulong doorId = door.DoorId;
        Vector3 hintPosition = door.IdentityHintPosition;
        if (IsServer)
        {
            if (!TryGetConnectedPlayerPosition(OwnerClientId, out Vector3 playerPosition))
                return;
            if (door.IsLocked || door.IsBusy || door.IsPostUnlockOpenDelayActive || !door.IsInInteractRange(playerPosition))
                return;

            bool open = !door.IsOpen;
            if (!open && !door.ServerValidateProceduralClose(OwnerClientId))
                return;

            door.ApplyProceduralRemoteOpenState(open);
            DoorNetworkStateStore.ServerPublish(door);
            return;
        }

        RequestToggleProceduralHingeDoorServerRpc(doorId, hintPosition);
    }

    [ServerRpc]
    void RequestUnlockHingeDoorServerRpc(ulong doorNetworkObjectId, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(doorNetworkObjectId, out NetworkObject doorObject)
            || doorObject == null)
        {
            return;
        }
        if (!doorObject.TryGetComponent(out HingeInteractDoor door) || !door.IsLocked || door.IsBusy)
            return;
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(serverRpcParams.Receive.SenderClientId, out NetworkClient client)
            || client.PlayerObject == null)
        {
            return;
        }
        if (!door.IsInInteractRange(client.PlayerObject.transform.position))
            return;
        if (!ServerHasKeyItem())
            return;
        if (!door.ServerUnlockFromKey())
            return;
        ServerBroadcastProceduralDoorUnlockIfNeeded(door);
        if (!ServerTryConsumeKeyItem())
            return;
    }

    [ServerRpc]
    void RequestUnlockProceduralHingeDoorServerRpc(ulong doorId, Vector3 hintPosition, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;
        if (!HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door)
            || door == null
            || !door.IsLocked
            || door.IsBusy)
            return;
        if (!TryGetConnectedPlayerPosition(serverRpcParams.Receive.SenderClientId, out Vector3 playerPosition))
            return;
        if (!door.IsInInteractRange(playerPosition))
            return;
        if (!ServerHasKeyItem())
            return;

        if (door.IsSpawned)
        {
            if (!door.ServerUnlockFromKey())
                return;
            ServerBroadcastProceduralDoorUnlockIfNeeded(door);
            if (!ServerTryConsumeKeyItem())
                return;
            return;
        }

        door.ApplyProceduralRemoteUnlock();
        if (door.IsLocked)
            return;
        if (!ServerTryConsumeKeyItem())
            return;
        DoorNetworkStateStore.ServerPublish(door);
    }

    [ServerRpc]
    void RequestToggleProceduralHingeDoorServerRpc(ulong doorId, Vector3 hintPosition, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;
        if (!HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door) || door == null)
            return;
        if (!TryGetConnectedPlayerPosition(serverRpcParams.Receive.SenderClientId, out Vector3 playerPosition))
            return;
        if (door.IsLocked || door.IsBusy || door.IsPostUnlockOpenDelayActive || !door.IsInInteractRange(playerPosition))
            return;

        if (door.IsSpawned)
        {
            if (door.ServerToggleFromRelay(serverRpcParams.Receive.SenderClientId))
                ServerBroadcastProceduralDoorOpenStateIfNeeded(door, door.IsOpen);
            return;
        }

        bool open = !door.IsOpen;
        if (!open && !door.ServerValidateProceduralClose(serverRpcParams.Receive.SenderClientId))
            return;

        door.ApplyProceduralRemoteOpenState(open);
        DoorNetworkStateStore.ServerPublish(door);
    }

    // ----- Locked-door rattle (bystanders) -----

    static readonly List<ulong> s_LockedRattleObserverClientIds = new List<ulong>(16);

    /// <summary>Server-side rate limit per player, so a mashed interact key can't flood the rattle.</summary>
    const float LockedRattleServerCooldownSeconds = 0.5f;

    float _serverNextLockedRattleTime;

    /// <summary>
    /// Owner-side: the interactor's own door already rattled locally (an instant cosmetic). Trying a locked
    /// door is a real noise in the corridor though, so tell the other peers to rattle their copy of it. Keyed
    /// like every other procedural-door relay — DoorId plus an identity hint — because these doors aren't
    /// spawned NetworkObjects.
    /// </summary>
    public void NotifyLockedDoorRattle(HingeInteractDoor door)
    {
        if (door == null || !IsSpawned)
            return;

        ulong doorId = door.DoorId;
        Vector3 hintPosition = door.IdentityHintPosition;

        if (IsServer)
        {
            ServerBroadcastLockedDoorRattle(doorId, hintPosition, OwnerClientId);
            return;
        }

        RequestLockedDoorRattleServerRpc(doorId, hintPosition);
    }

    [ServerRpc]
    void RequestLockedDoorRattleServerRpc(ulong doorId, Vector3 hintPosition, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerBroadcastLockedDoorRattle(doorId, hintPosition, serverRpcParams.Receive.SenderClientId);
    }

    void ServerBroadcastLockedDoorRattle(ulong doorId, Vector3 hintPosition, ulong senderClientId)
    {
        if (!IsServer)
            return;

        float now = Time.time;
        if (now < _serverNextLockedRattleTime)
            return;
        _serverNextLockedRattleTime = now + LockedRattleServerCooldownSeconds;

        if (!HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door)
            || door == null
            || !door.IsLocked)
            return;
        if (!TryGetConnectedPlayerPosition(senderClientId, out Vector3 playerPosition))
            return;
        if (!door.IsInInteractRange(playerPosition))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        s_LockedRattleObserverClientIds.Clear();
        foreach (ulong id in nm.ConnectedClientsIds)
        {
            if (id != senderClientId)
                s_LockedRattleObserverClientIds.Add(id);
        }

        if (s_LockedRattleObserverClientIds.Count == 0)
            return;

        PlayLockedDoorRattleClientRpc(doorId, hintPosition, new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = s_LockedRattleObserverClientIds.ToArray() }
        });
    }

    [ClientRpc]
    void PlayLockedDoorRattleClientRpc(ulong doorId, Vector3 hintPosition, ClientRpcParams clientRpcParams = default)
    {
        if (HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door) && door != null)
            door.PlayLockedNoKeyFeedback();
    }

    // Procedural maze HingeInteractDoors are built locally on every peer from the deterministic seed and are not
    // Netcode-spawned, so their NetworkVariables never go live. Their open/locked state is replicated through
    // DoorNetworkStateStore's NetworkList (persistent, late-join-safe, drop-proof) rather than the old best-effort
    // ClientRpc mirror + one-shot snapshot. These Server* entry points are kept so HingeInteractDoor's call sites
    // don't change; each simply publishes the door's current authoritative state to the store.
    public static void ServerBroadcastProceduralJailSealIfNeeded(HingeInteractDoor door)
    {
        DoorNetworkStateStore.ServerPublish(door);
    }

    /// <seealso cref="ServerBroadcastProceduralJailSealIfNeeded"/>
    public static void ServerBroadcastProceduralJailorOpenEntryIfNeeded(HingeInteractDoor door)
    {
        DoorNetworkStateStore.ServerPublish(door);
    }

    public static void ServerBroadcastProceduralDoorUnlockIfNeeded(HingeInteractDoor door)
    {
        DoorNetworkStateStore.ServerPublish(door);
    }

    public static void ServerBroadcastProceduralDoorOpenStateIfNeeded(HingeInteractDoor door, bool open)
    {
        DoorNetworkStateStore.ServerPublish(door);
    }

    /// <summary>
    /// Late-join door sync is now handled automatically by <see cref="DoorNetworkStateStore"/>'s replicated
    /// NetworkList — a joining client receives the current contents on spawn — so this explicit per-client snapshot
    /// is no longer needed. Kept as a no-op so existing callers stay valid.
    /// </summary>
    public static void ServerSendProceduralJailDoorSnapshotsToClient(ulong targetClientId)
    {
    }

    static NetworkPlayerInventory ResolveServerRelayInventory()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return null;

        if (nm.LocalClient != null && nm.LocalClient.PlayerObject != null
            && nm.LocalClient.PlayerObject.TryGetComponent(out NetworkPlayerInventory local) && local != null
            && local.IsSpawned)
            return local;

        foreach (var pair in nm.ConnectedClients)
        {
            if (pair.Value == null || pair.Value.PlayerObject == null)
                continue;
            if (pair.Value.PlayerObject.TryGetComponent(out NetworkPlayerInventory inv) && inv != null && inv.IsSpawned)
                return inv;
        }

        return null;
    }

    bool TryGetConnectedPlayerPosition(ulong clientId, out Vector3 playerPosition)
    {
        playerPosition = transform.position;
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null
            || !manager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)
            || client.PlayerObject == null)
        {
            return false;
        }

        playerPosition = client.PlayerObject.transform.position;
        return true;
    }
}
