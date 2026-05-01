using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayerInventory : NetworkBehaviour
{
    [SerializeField] PlayerController playerController;
    [Tooltip("Forward impulse when dropping (matches PlayerController drop force).")]
    [SerializeField] float dropThrowImpulse = 0.65f;

    NetworkPlayerAvatar _avatar;
    uint _runtimeDropSequence;

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

    readonly NetworkVariable<byte> _selectedSlot = new NetworkVariable<byte>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<bool> _selectedFlashlightLightOn = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>0 when slot is empty; 1 for flashlight; 1–<see cref="GlowstickItem.MaxStack"/> for glowstick stacks.</summary>
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

    public int SelectedSlotIndex => _selectedSlot.Value;
    public bool SelectedFlashlightLightOn => _selectedFlashlightLightOn.Value;

    public event System.Action OnInventoryChanged;

    public ulong GetSlotItemId(int index)
    {
        if (index == 0) return _slot0ItemId.Value;
        if (index == 1) return _slot1ItemId.Value;
        if (index == 2) return _slot2ItemId.Value;
        return 0UL;
    }

    void SetSlotItemId(int index, ulong value)
    {
        if (index == 0) _slot0ItemId.Value = value;
        else if (index == 1) _slot1ItemId.Value = value;
        else if (index == 2) _slot2ItemId.Value = value;
    }

    public byte GetSlotItemTypeId(int index)
    {
        if (index == 0) return _slot0ItemType.Value;
        if (index == 1) return _slot1ItemType.Value;
        if (index == 2) return _slot2ItemType.Value;
        return GrabbableInventoryItem.TypeIdNone;
    }

    void SetSlotItemTypeId(int index, byte value)
    {
        if (index == 0) _slot0ItemType.Value = value;
        else if (index == 1) _slot1ItemType.Value = value;
        else if (index == 2) _slot2ItemType.Value = value;
    }

    public int GetSlotStackCount(int index)
    {
        if (index == 0) return _slot0Stack.Value;
        if (index == 1) return _slot1Stack.Value;
        if (index == 2) return _slot2Stack.Value;
        return 0;
    }

    void SetSlotStackCount(int index, byte value)
    {
        if (index == 0) _slot0Stack.Value = value;
        else if (index == 1) _slot1Stack.Value = value;
        else if (index == 2) _slot2Stack.Value = value;
    }

    public float GetSlotFlashlightBatteryNormalizedForHud(int index)
    {
        if (index == 0) return _slot0FlashlightBattery.Value;
        if (index == 1) return _slot1FlashlightBattery.Value;
        if (index == 2) return _slot2FlashlightBattery.Value;
        return 0f;
    }

    void SetSlotFlashlightBatteryNormalized(int index, float value)
    {
        if (index == 0) _slot0FlashlightBattery.Value = value;
        else if (index == 1) _slot1FlashlightBattery.Value = value;
        else if (index == 2) _slot2FlashlightBattery.Value = value;
    }

    int GetFirstEmptySlot()
    {
        for (int i = 0; i < 3; i++)
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

        if (IsServer)
            SendItemSnapshotToOwner();

        RaiseChangedAndRefresh();
    }

    public override void OnNetworkDespawn()
    {
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
    }

    void Update()
    {
        if (!IsServer || !IsSpawned)
            return;
        float dt = Time.deltaTime;
        Vector3 resolveHint = playerController != null ? playerController.transform.position : transform.position;
        for (int i = 0; i < 3; i++)
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
    void OnSelectedChanged(byte previous, byte current) { RaiseChangedAndRefresh(); }
    void OnFlashlightLightChanged(bool previous, bool current) { RaiseChangedAndRefresh(); }

    void RaiseChangedAndRefresh()
    {
        OnInventoryChanged?.Invoke();
        playerController?.RefreshInventoryViewFromNetwork();
    }

    public bool CanPickup(GrabbableInventoryItem item)
    {
        if (item == null
            || !item.gameObject.activeInHierarchy
            || item.IsHeld)
            return false;

        if (item is GlowstickItem gs)
        {
            int w = gs.StackCount;
            if (GetFirstEmptySlot() >= 0)
                return true;
            for (int i = 0; i < 3; i++)
            {
                if (GetSlotItemId(i) == 0UL)
                    continue;
                if (!GrabbableInventoryItem.TryGetRegistered(GetSlotItemId(i), out GrabbableInventoryItem g)
                    || g is not GlowstickItem)
                    continue;
                if (GetSlotStackCount(i) < GlowstickItem.MaxStack && w > 0)
                    return true;
            }
            return false;
        }

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

        if (item is FlashlightItem f0)
        {
            int empty = GetFirstEmptySlot();
            if (empty < 0)
                return;
            _selectedSlot.Value = (byte)empty;
            SetSlotItemId(empty, item.ItemId);
            SetSlotItemTypeId(empty, item.ItemTypeId);
            SetSlotStackCount(empty, 1);
            _selectedFlashlightLightOn.Value = f0.IsLightOn;
            ApplyItemStateWithTypeClientRpc(item.ItemId, item.ItemTypeId, true, NetworkObjectId, item.transform.position, item.transform.rotation, default);
            return;
        }

        if (item is GlowstickItem pickup)
        {
            int w = pickup.StackCount;
            for (int i = 0; i < 3 && w > 0; i++)
            {
                ulong slotId = GetSlotItemId(i);
                if (slotId == 0UL)
                    continue;
                if (!GrabbableInventoryItem.TryGetRegistered(slotId, out GrabbableInventoryItem inSlotG) || inSlotG is not GlowstickItem inSlot)
                    continue;
                int c = GetSlotStackCount(i);
                int space = GlowstickItem.MaxStack - c;
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
                RemoveWorldItemClientRpc(pickup.ItemId);
                Object.Destroy(pickup.gameObject);
                return;
            }
            int emptyG = GetFirstEmptySlot();
            if (emptyG < 0)
            {
                pickup.SetStackCount(w);
                return;
            }
            pickup.SetStackCount(w);
            _selectedSlot.Value = (byte)emptyG;
            SetSlotItemId(emptyG, pickup.ItemId);
            SetSlotItemTypeId(emptyG, pickup.ItemTypeId);
            SetSlotStackCount(emptyG, (byte)w);
            _selectedFlashlightLightOn.Value = false;
            ApplyItemStateWithTypeClientRpc(pickup.ItemId, pickup.ItemTypeId, true, NetworkObjectId, pickup.transform.position, pickup.transform.rotation, default);
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

        int stackForDrop = item is GlowstickItem ? GetSlotStackCount(sel) : 1;

        Vector3 norm = dropForward;
        if (norm.sqrMagnitude < 0.0001f)
            norm = transform.forward;
        norm.Normalize();
        Vector3 finalDropPosition = dropPosition + norm * 0.35f;
        finalDropPosition.y = Mathf.Max(finalDropPosition.y, transform.position.y + 0.1f);
        Quaternion finalDropRotation = item.transform.rotation;
        Vector3 throwImpulse = norm * dropThrowImpulse;

        if (item is GlowstickItem glowStack && stackForDrop > 1)
        {
            int next = stackForDrop - 1;
            glowStack.SetStackCount(next);
            SetSlotStackCount(sel, (byte)next);
            ulong templateId = glowStack.ItemId;
            ulong droppedItemId = ComputeRuntimeDroppedItemId(templateId, ++_runtimeDropSequence);
            SpawnSingleGlowstickDropClientRpc(templateId, droppedItemId, finalDropPosition, finalDropRotation, throwImpulse);
            return;
        }

        SetSlotItemId(sel, 0UL);
        SetSlotItemTypeId(sel, GrabbableInventoryItem.TypeIdNone);
        SetSlotStackCount(sel, 0);
        SelectAfterDrop();
        _selectedFlashlightLightOn.Value = false;

        if (item is FlashlightItem flashlight)
        {
            bool lightOn = flashlight.IsLightOn;
            if (IsServer && !IsClient)
                flashlight.ApplyNetworkWorldState(finalDropPosition, finalDropRotation, lightOn, throwImpulse);
            else
                flashlight.ApplyNetworkWorldState(finalDropPosition, finalDropRotation, lightOn, default);

            ApplyItemStateWithTypeClientRpc(flashlight.ItemId, flashlight.ItemTypeId, false, 0UL, finalDropPosition, finalDropRotation, throwImpulse);
        }
        else
        {
            if (item is GlowstickItem gsz)
                gsz.SetStackCount(Mathf.Max(1, stackForDrop));
            item.ApplyNetworkWorldState(finalDropPosition, finalDropRotation, throwImpulse);
            if (item is GlowstickItem gForVis)
                gForVis.SetWorldDroppedVisual();

            ApplyItemStateWithTypeClientRpc(item.ItemId, item.ItemTypeId, false, 0UL, finalDropPosition, finalDropRotation, default);
        }
    }

    [ClientRpc]
    void SpawnSingleGlowstickDropClientRpc(ulong templateGlowstickItemId, ulong droppedItemId, Vector3 worldPosition, Quaternion worldRotation, Vector3 throwImpulse)
    {
        if (!GrabbableInventoryItem.TryGetRegistered(templateGlowstickItemId, out GrabbableInventoryItem template)
            || template is not GlowstickItem)
            return;

        GameObject d = Object.Instantiate(template.gameObject, worldPosition, worldRotation);
        d.transform.SetParent(null, true);
        if (!d.TryGetComponent(out GlowstickItem dropped) || dropped == null)
        {
            Object.Destroy(d);
            return;
        }

        dropped.AssignNetworkItemId(droppedItemId);
        dropped.SetStackCount(1);
        dropped.ApplyNetworkWorldState(worldPosition, worldRotation, throwImpulse);
        dropped.SetWorldDroppedVisual();
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
        for (int i = 0; i < 3; i++)
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
        int n = 3;
        int wrapped = ((next % n) + n) % n;
        _selectedSlot.Value = (byte)wrapped;
        UpdateFlashlightSyncFromSelected();
    }

    void UpdateFlashlightSyncFromSelected()
    {
        if (!IsServer)
            return;

        ulong id = GetSlotItemId(SelectedSlotIndex);
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
        for (int s = 0; s < 3; s++)
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
        ulong consumeId = g.ItemId;
        ConsumeItemClientRpc(consumeId);
        Object.Destroy(g.gameObject);
        SelectAfterDrop();
        RaiseChangedAndRefresh();
    }

    [ClientRpc]
    void PlayBandageUseObserversClientRpc()
    {
        playerController?.PlayBandageUseSfx();
    }

    public bool ServerTryConsumeKeyItem()
    {
        if (!IsServer || !IsSpawned)
            return false;

        for (int i = 0; i < 3; i++)
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
            if (g != null)
            {
                ConsumeItemClientRpc(id);
                Object.Destroy(g.gameObject);
            }
            SelectAfterDrop();
            RaiseChangedAndRefresh();
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

    static void DestroyRegisteredItem(ulong itemId)
    {
        if (itemId == 0UL)
            return;

        if (!GrabbableInventoryItem.TryGetRegistered(itemId, out GrabbableInventoryItem item) || item == null)
            return;

        Object.Destroy(item.gameObject);
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
                if (!door.IsLocked || !door.IsInInteractRange(playerPosition))
                    return;
                if (!ServerTryConsumeKeyItem())
                    return;
                door.ApplyProceduralRemoteUnlock();
                ApplyProceduralDoorUnlockClientRpc(doorId, hintPosition);
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
            if (!ServerTryConsumeKeyItem())
                return;
            door.ServerUnlockFromKey();
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
            ApplyProceduralDoorOpenStateClientRpc(doorId, hintPosition, open);
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
        if (!doorObject.TryGetComponent(out HingeInteractDoor door) || !door.IsLocked)
            return;
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(serverRpcParams.Receive.SenderClientId, out NetworkClient client)
            || client.PlayerObject == null)
        {
            return;
        }
        if (!door.IsInInteractRange(client.PlayerObject.transform.position))
            return;
        if (!ServerTryConsumeKeyItem())
            return;
        door.ServerUnlockFromKey();
    }

    [ServerRpc]
    void RequestUnlockProceduralHingeDoorServerRpc(ulong doorId, Vector3 hintPosition, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;
        if (!HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door) || door == null || !door.IsLocked)
            return;
        if (!TryGetConnectedPlayerPosition(serverRpcParams.Receive.SenderClientId, out Vector3 playerPosition))
            return;
        if (!door.IsInInteractRange(playerPosition))
            return;
        if (!ServerTryConsumeKeyItem())
            return;

        door.ApplyProceduralRemoteUnlock();
        ApplyProceduralDoorUnlockClientRpc(door.DoorId, door.IdentityHintPosition);
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

        bool open = !door.IsOpen;
        if (!open && !door.ServerValidateProceduralClose(serverRpcParams.Receive.SenderClientId))
            return;

        door.ApplyProceduralRemoteOpenState(open);
        ApplyProceduralDoorOpenStateClientRpc(door.DoorId, door.IdentityHintPosition, open);
    }

    [ClientRpc]
    void ApplyProceduralDoorUnlockClientRpc(ulong doorId, Vector3 hintPosition)
    {
        if (!HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door) || door == null)
            return;

        door.ApplyProceduralRemoteUnlock();
    }

    [ClientRpc]
    void ApplyProceduralDoorOpenStateClientRpc(ulong doorId, Vector3 hintPosition, bool open)
    {
        if (!HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door) || door == null)
            return;

        door.ApplyProceduralRemoteOpenState(open);
    }

    /// <summary>
    /// Procedural maze <see cref="HingeInteractDoor"/> copies are identical on host and clients but not Netcode-spawned,
    /// so NetworkVariables never sync — mirror jury-rig jailor-driven state via ClientRpc after the server applies local changes.
    /// </summary>
    public static void ServerBroadcastProceduralJailSealIfNeeded(HingeInteractDoor door)
    {
        if (door == null)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;

        NetworkObject doorNo = door.GetComponent<NetworkObject>();
        if (doorNo != null && doorNo.IsSpawned)
            return;

        NetworkPlayerInventory relay = ResolveServerRelayInventory();
        if (relay == null)
            return;

        relay.ProceduralJailSealFromJailDoorClientRpc(door.DoorId, door.IdentityHintPosition);
    }

    /// <seealso cref="ServerBroadcastProceduralJailSealIfNeeded"/>
    public static void ServerBroadcastProceduralJailorOpenEntryIfNeeded(HingeInteractDoor door)
    {
        if (door == null)
            return;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;

        NetworkObject doorNo = door.GetComponent<NetworkObject>();
        if (doorNo != null && doorNo.IsSpawned)
            return;

        NetworkPlayerInventory relay = ResolveServerRelayInventory();
        if (relay == null)
            return;

        relay.ProceduralJailorOpenForEntryMirrorClientRpc(door.DoorId, door.IdentityHintPosition);
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

    [ClientRpc]
    void ProceduralJailSealFromJailDoorClientRpc(ulong doorId, Vector3 hintPosition)
    {
        if (!HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door) || door == null)
            return;

        door.ApplyProceduralRemoteJailSealFromServer();
    }

    [ClientRpc]
    void ProceduralJailorOpenForEntryMirrorClientRpc(ulong doorId, Vector3 hintPosition)
    {
        if (!HingeInteractDoor.TryResolveForSync(doorId, hintPosition, out HingeInteractDoor door) || door == null)
            return;

        door.ApplyProceduralRemoteJailorOpenForEntryIncludingPaired();
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
