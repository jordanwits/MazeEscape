using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative flashbang throw: the flare gun's spawn step (a live <see cref="FlashbangGrenade"/>
/// network object the server simulates and every client watches through its NetworkTransform) followed by
/// the flare-ammo stack decrement — one grenade leaves the slot per throw, and only the last one empties it
/// via the usual tombstone-and-destroy-on-every-peer path.
///
/// The aim and the wind-up charge come from the throwing client (same trust model as the flare gun), but
/// the release point is clamped to the server's own copy of that player and the charge is re-clamped inside
/// <see cref="FlashbangItem.ThrowVelocity"/>, so a client cannot lob a grenade across the level.
/// </summary>
public partial class NetworkPlayerInventory
{
    float _serverNextFlashbangThrowTime;

    /// <summary>
    /// Owner-side request to throw the selected flashbang along the camera aim.
    /// <paramref name="charge01"/> is how full the wind-up was; the server re-clamps it inside
    /// <see cref="FlashbangItem.ThrowVelocity"/> so a forged value cannot exceed max range.
    /// </summary>
    public void RequestThrowSelectedFlashbang(Vector3 origin, Vector3 direction, float charge01)
    {
        if (!IsSpawned)
            return;

        if (IsServer)
        {
            ServerThrowSelectedFlashbang(origin, direction, charge01);
            return;
        }

        RequestThrowSelectedFlashbangServerRpc(origin, direction, charge01);
    }

    [ServerRpc]
    void RequestThrowSelectedFlashbangServerRpc(Vector3 origin, Vector3 direction, float charge01, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerThrowSelectedFlashbang(origin, direction, charge01);
    }

    void ServerThrowSelectedFlashbang(Vector3 origin, Vector3 direction, float charge01)
    {
        if (!IsServer)
            return;

        PlayerHealth health = playerController != null
            ? playerController.GetComponent<PlayerHealth>()
            : GetComponent<PlayerHealth>();
        if (health != null && health.IsDead)
            return;

        float now = Time.time;
        if (now < _serverNextFlashbangThrowTime)
            return;

        int sel = SelectedSlotIndex;
        if (!ServerTryResolveSelectedFlashbang(sel, out FlashbangItem flashbang))
            return;

        GameObject grenadePrefab = flashbang.GrenadePrefab;
        if (grenadePrefab == null)
        {
            Debug.LogWarning($"{name}: flashbang has no grenade prefab assigned; throw ignored.", flashbang);
            return;
        }

        // Clamp the release point to the server-known player before anything is consumed.
        Vector3 throwerHead = transform.position + Vector3.up * 1.5f;
        if ((origin - throwerHead).sqrMagnitude > 9f)
            origin = throwerHead;
        Vector3 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;

        _serverNextFlashbangThrowTime = now + FlashbangItem.ThrowCooldownSeconds * 0.9f;

        GameObject go = Object.Instantiate(grenadePrefab, origin, Quaternion.LookRotation(dir));
        if (go.TryGetComponent(out FlashbangGrenade grenade))
        {
            grenade.Launch(origin, flashbang.ThrowVelocity(dir, charge01), transform);
            if (go.TryGetComponent(out NetworkObject netObj))
                netObj.Spawn();
        }
        else
        {
            Object.Destroy(go);
            return; // nothing was thrown, so nothing gets consumed
        }

        // One grenade off the stack (flare-ammo idiom): the slot only clears when the last one goes, so a
        // player carrying three keeps the other two selected and ready.
        int inStack = Mathf.Max(1, GetSlotStackCount(sel));
        if (inStack > 1)
        {
            int remaining = inStack - 1;
            flashbang.SetStackCount(remaining);
            SetSlotStackCount(sel, (byte)remaining);
            RaiseChangedAndRefresh();
            return;
        }

        SetSlotItemId(sel, 0UL);
        SetSlotItemTypeId(sel, GrabbableInventoryItem.TypeIdNone);
        SetSlotStackCount(sel, 0);
        _selectedFlashlightLightOn.Value = false;
        ulong consumeId = flashbang.ItemId;
        ConsumedItemNetworkStore.ServerMarkConsumed(consumeId);
        ConsumeItemClientRpc(consumeId);
        Object.Destroy(flashbang.gameObject);
        SelectAfterDrop();
        RaiseChangedAndRefresh();
    }

    bool ServerTryResolveSelectedFlashbang(int sel, out FlashbangItem flashbang)
    {
        flashbang = null;
        if (GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdFlashbang)
            return false;

        ulong id = GetSlotItemId(sel);
        GrabbableInventoryItem g = null;
        bool resolved = id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out g) && g is FlashbangItem;
        if (!resolved)
        {
            Vector3 hint = playerController != null ? playerController.transform.position : transform.position;
            resolved = GrabbableInventoryItem.TryResolveForStateByType(
                id,
                hint,
                GrabbableInventoryItem.TypeIdFlashbang,
                out g)
                && g is FlashbangItem;
        }

        flashbang = g as FlashbangItem;
        return flashbang != null;
    }
}
