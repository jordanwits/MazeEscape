using Unity.Netcode;
using UnityEngine;

public enum NetworkHeldItemKind : byte
{
    None = 0,
    Flashlight = 1,
}

[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayerInventory : NetworkBehaviour
{
    [SerializeField] PlayerController playerController;

    readonly NetworkVariable<byte> _heldKind = new NetworkVariable<byte>(
        (byte)NetworkHeldItemKind.None,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<ulong> _heldFlashlightItemId = new NetworkVariable<ulong>(
        0UL,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    readonly NetworkVariable<bool> _heldFlashlightLightOn = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public bool HasEquippedFlashlight => IsSpawned && (NetworkHeldItemKind)_heldKind.Value == NetworkHeldItemKind.Flashlight;
    public bool HasAnyEquippedItem => IsSpawned && (NetworkHeldItemKind)_heldKind.Value != NetworkHeldItemKind.None;
    public ulong HeldFlashlightItemId => _heldFlashlightItemId.Value;
    public bool HeldFlashlightLightOn => _heldFlashlightLightOn.Value;

    void Awake()
    {
        if (playerController == null)
            playerController = GetComponent<PlayerController>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
            SendFlashlightSnapshotToOwner();
    }

    public bool CanPickupFlashlight(FlashlightItem flashlight)
    {
        return flashlight != null
            && flashlight.gameObject.activeInHierarchy
            && !HasAnyEquippedItem
            && !flashlight.IsHeld;
    }

    public bool TryGetFlashlightAttachmentTargets(out Transform holdPoint, out Transform followTransform)
    {
        holdPoint = null;
        followTransform = null;
        return playerController != null && playerController.TryGetFlashlightAttachmentTargets(out holdPoint, out followTransform);
    }

    public void TryPickupFlashlight(FlashlightItem flashlight)
    {
        if (!CanPickupFlashlight(flashlight))
            return;

        if (IsServer)
        {
            ServerTryPickupFlashlight(flashlight.ItemId, flashlight.transform.position);
            return;
        }

        RequestPickupFlashlightServerRpc(flashlight.ItemId, flashlight.transform.position);
    }

    public void TryToggleHeldFlashlight()
    {
        if (!HasEquippedFlashlight)
            return;

        if (IsServer)
        {
            ServerToggleHeldFlashlight();
            return;
        }

        RequestToggleHeldFlashlightServerRpc();
    }

    public void TryDropHeldFlashlight(Vector3 dropPosition, Quaternion dropRotation, Vector3 dropForward)
    {
        if (!HasEquippedFlashlight)
            return;

        Vector3 normalizedForward = dropForward.sqrMagnitude > 0.0001f ? dropForward.normalized : transform.forward;
        if (IsServer)
        {
            ServerDropHeldFlashlight(dropPosition, dropRotation, normalizedForward);
            return;
        }

        RequestDropHeldFlashlightServerRpc(dropPosition, dropRotation, normalizedForward);
    }

    public void ServerDropHeldFlashlightOnDeath()
    {
        if (!IsServer || !HasEquippedFlashlight)
            return;

        Vector3 forward = transform.forward.sqrMagnitude > 0.0001f ? transform.forward : Vector3.forward;
        Vector3 dropPosition = transform.position + Vector3.up * 0.9f + forward * 0.6f;
        Quaternion dropRotation = transform.rotation;
        if (playerController != null && playerController.TryGetFlashlightAttachmentTargets(out Transform holdPoint, out Transform followTransform))
        {
            dropPosition = holdPoint.position;
            dropRotation = followTransform != null ? followTransform.rotation : holdPoint.rotation;
        }

        ServerDropHeldFlashlight(dropPosition, dropRotation, forward);
    }

    [ServerRpc]
    void RequestPickupFlashlightServerRpc(ulong flashlightItemId, Vector3 flashlightWorldPosition, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerTryPickupFlashlight(flashlightItemId, flashlightWorldPosition);
    }

    [ServerRpc]
    void RequestToggleHeldFlashlightServerRpc(ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerToggleHeldFlashlight();
    }

    [ServerRpc]
    void RequestDropHeldFlashlightServerRpc(Vector3 dropPosition, Quaternion dropRotation, Vector3 dropForward, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        Vector3 normalizedForward = dropForward.sqrMagnitude > 0.0001f ? dropForward.normalized : transform.forward;
        ServerDropHeldFlashlight(dropPosition, dropRotation, normalizedForward);
    }

    void ServerTryPickupFlashlight(ulong flashlightItemId, Vector3 flashlightWorldPosition)
    {
        if (!IsServer || HasAnyEquippedItem)
            return;

        if (!FlashlightItem.TryResolveRegisteredFlashlightForPickup(flashlightItemId, flashlightWorldPosition, out FlashlightItem flashlight)
            || flashlight == null
            || flashlight.IsHeld)
        {
            return;
        }

        Vector3 pickupHintPosition = flashlight.transform.position;
        Quaternion pickupHintRotation = flashlight.transform.rotation;
        ulong resolvedId = flashlight.ItemId;

        _heldKind.Value = (byte)NetworkHeldItemKind.Flashlight;
        _heldFlashlightItemId.Value = resolvedId;
        _heldFlashlightLightOn.Value = flashlight.IsLightOn;

        flashlight.ApplyNetworkHeldState(NetworkObjectId, flashlight.IsLightOn);
        ApplyFlashlightStateClientRpc(resolvedId, true, NetworkObjectId, pickupHintPosition, pickupHintRotation, flashlight.IsLightOn);
    }

    void ServerToggleHeldFlashlight()
    {
        if (!IsServer || !HasEquippedFlashlight)
            return;

        if (!TryGetHeldFlashlight(out FlashlightItem flashlight))
            return;

        bool lightEnabled = !flashlight.IsLightOn;
        _heldFlashlightLightOn.Value = lightEnabled;
        flashlight.ApplyNetworkHeldState(NetworkObjectId, lightEnabled);
        ApplyFlashlightStateClientRpc(_heldFlashlightItemId.Value, true, NetworkObjectId, flashlight.transform.position, flashlight.transform.rotation, lightEnabled);
    }

    void ServerDropHeldFlashlight(Vector3 dropPosition, Quaternion dropRotation, Vector3 dropForward)
    {
        if (!IsServer || !HasEquippedFlashlight)
            return;

        if (!TryGetHeldFlashlight(out FlashlightItem flashlight))
            return;

        Vector3 normalizedForward = dropForward.sqrMagnitude > 0.0001f ? dropForward.normalized : transform.forward;
        Vector3 finalDropPosition = dropPosition + normalizedForward * 0.35f;
        finalDropPosition.y = Mathf.Max(finalDropPosition.y, transform.position.y + 0.1f);
        Quaternion finalDropRotation = dropRotation;
        bool lightEnabled = flashlight.IsLightOn;
        ulong droppedItemId = _heldFlashlightItemId.Value;

        _heldKind.Value = (byte)NetworkHeldItemKind.None;
        _heldFlashlightItemId.Value = 0UL;
        _heldFlashlightLightOn.Value = false;

        flashlight.ApplyNetworkWorldState(finalDropPosition, finalDropRotation, lightEnabled);
        ApplyFlashlightStateClientRpc(droppedItemId, false, NetworkObjectId, finalDropPosition, finalDropRotation, lightEnabled);
    }

    bool TryGetHeldFlashlight(out FlashlightItem flashlight)
    {
        flashlight = null;
        return HasEquippedFlashlight
            && FlashlightItem.TryGetRegisteredFlashlight(_heldFlashlightItemId.Value, out flashlight)
            && flashlight != null;
    }

    void SendFlashlightSnapshotToOwner()
    {
        ClientRpcParams targetOwner = new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new[] { OwnerClientId }
            }
        };

        foreach (FlashlightItem flashlight in FlashlightItem.GetRegisteredFlashlights())
        {
            if (flashlight == null)
                continue;

            Vector3 snapshotPosition = flashlight.transform.position;
            Quaternion snapshotRotation = flashlight.transform.rotation;
            ApplyFlashlightStateClientRpc(
                flashlight.ItemId,
                flashlight.IsHeld,
                flashlight.HolderNetworkObjectId,
                snapshotPosition,
                snapshotRotation,
                flashlight.IsLightOn,
                targetOwner);
        }
    }

    [ClientRpc]
    void ApplyFlashlightStateClientRpc(
        ulong flashlightItemId,
        bool isHeld,
        ulong holderNetworkObjectId,
        Vector3 worldPosition,
        Quaternion worldRotation,
        bool lightEnabled,
        ClientRpcParams clientRpcParams = default)
    {
        if (!FlashlightItem.TryResolveRegisteredFlashlightForState(flashlightItemId, worldPosition, out FlashlightItem flashlight) || flashlight == null)
            return;

        NetworkPlayerAvatar avatar = GetComponent<NetworkPlayerAvatar>();
        if (isHeld)
        {
            avatar?.NotifyFlashlightVisualAttach(flashlight);
            flashlight.ApplyNetworkHeldState(holderNetworkObjectId, lightEnabled);
        }
        else
        {
            flashlight.ApplyNetworkWorldState(worldPosition, worldRotation, lightEnabled);
        }
    }
}
