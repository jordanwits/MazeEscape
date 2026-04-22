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
            SetSlotStackCount(empty, 1);
            _selectedFlashlightLightOn.Value = f0.IsLightOn;
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
                w -= add;
            }
            if (w <= 0)
            {
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
            SetSlotStackCount(emptyG, (byte)w);
            _selectedFlashlightLightOn.Value = false;
            return;
        }

        int emptyOther = GetFirstEmptySlot();
        if (emptyOther < 0)
            return;
        _selectedSlot.Value = (byte)emptyOther;
        SetSlotItemId(emptyOther, item.ItemId);
        SetSlotStackCount(emptyOther, 1);
        _selectedFlashlightLightOn.Value = false;
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
            SpawnSingleGlowstickDropClientRpc(templateId, finalDropPosition, finalDropRotation, throwImpulse);
            return;
        }

        SetSlotItemId(sel, 0UL);
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

            ApplyItemStateClientRpc(flashlight.ItemId, false, 0UL, finalDropPosition, finalDropRotation, throwImpulse);
        }
        else
        {
            if (item is GlowstickItem gsz)
                gsz.SetStackCount(Mathf.Max(1, stackForDrop));
            item.ApplyNetworkWorldState(finalDropPosition, finalDropRotation, throwImpulse);
            if (item is GlowstickItem gForVis)
                gForVis.SetWorldDroppedVisual();

            ApplyItemStateClientRpc(item.ItemId, false, 0UL, finalDropPosition, finalDropRotation, default);
        }
    }

    [ClientRpc]
    void SpawnSingleGlowstickDropClientRpc(ulong templateGlowstickItemId, Vector3 worldPosition, Quaternion worldRotation, Vector3 throwImpulse)
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

        dropped.SetStackCount(1);
        dropped.ApplyNetworkWorldState(worldPosition, worldRotation, throwImpulse);
        dropped.SetWorldDroppedVisual();
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

        if (flashlight.IsLightOn)
            flashlight.SetLightEnabled(false);
        else if (flashlight.HasUsableBattery)
            flashlight.SetLightEnabled(true);
        _selectedFlashlightLightOn.Value = flashlight.IsLightOn;
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
                if (playerController == null
                    || !playerController.TryGetFlashlightAttachmentTargets(out Transform holdPoint, out Transform follow))
                {
                    break;
                }
                Vector3 pos = holdPoint.position;
                Quaternion rot = follow != null ? follow.rotation : holdPoint.rotation;
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
            ApplyItemStateClientRpc(
                g.ItemId,
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
        if (!GrabbableInventoryItem.TryResolveForState(itemId, worldPosition, out GrabbableInventoryItem g) || g == null)
            return;

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
}
