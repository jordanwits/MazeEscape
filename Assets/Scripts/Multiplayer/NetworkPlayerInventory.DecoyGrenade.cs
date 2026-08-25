using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative decoy throw — the flashbang's path with the payload swapped: spawn a live
/// <see cref="DecoyGrenade"/> network object the server simulates and every client watches through its
/// NetworkTransform, then take one off the stack.
///
/// The aim and the wind-up charge come from the throwing client (same trust model as the flashbang and the
/// flare gun), but the release point is clamped to the server's own copy of that player and the charge is
/// re-clamped inside <see cref="DecoyGrenadeItem.ThrowVelocity"/>, so a client cannot lob a decoy across
/// the level to pull every hunter off a teammate.
/// </summary>
public partial class NetworkPlayerInventory
{
    float _serverNextDecoyThrowTime;

    /// <summary>Owner-side request to throw the selected decoy along the camera aim.</summary>
    public void RequestThrowSelectedDecoyGrenade(Vector3 origin, Vector3 direction, float charge01)
    {
        if (!IsSpawned)
            return;

        if (IsServer)
        {
            ServerThrowSelectedDecoyGrenade(origin, direction, charge01);
            return;
        }

        RequestThrowSelectedDecoyGrenadeServerRpc(origin, direction, charge01);
    }

    [ServerRpc]
    void RequestThrowSelectedDecoyGrenadeServerRpc(Vector3 origin, Vector3 direction, float charge01, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerThrowSelectedDecoyGrenade(origin, direction, charge01);
    }

    void ServerThrowSelectedDecoyGrenade(Vector3 origin, Vector3 direction, float charge01)
    {
        if (!IsServer)
            return;

        PlayerHealth health = playerController != null
            ? playerController.GetComponent<PlayerHealth>()
            : GetComponent<PlayerHealth>();
        if (health != null && health.IsDead)
            return;

        float now = Time.time;
        if (now < _serverNextDecoyThrowTime)
            return;

        int sel = SelectedSlotIndex;
        if (!ServerTryResolveSelectedDecoyGrenade(sel, out DecoyGrenadeItem decoy))
            return;

        GameObject grenadePrefab = decoy.GrenadePrefab;
        if (grenadePrefab == null)
        {
            Debug.LogWarning($"{name}: decoy grenade has no grenade prefab assigned; throw ignored.", decoy);
            return;
        }

        // Clamp the release point to the server-known player before anything is consumed.
        Vector3 throwerHead = transform.position + Vector3.up * 1.5f;
        if ((origin - throwerHead).sqrMagnitude > 9f)
            origin = throwerHead;
        Vector3 dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : transform.forward;

        _serverNextDecoyThrowTime = now + DecoyGrenadeItem.ThrowCooldownSeconds * 0.9f;

        GameObject go = Object.Instantiate(grenadePrefab, origin, Quaternion.LookRotation(dir));
        if (go.TryGetComponent(out DecoyGrenade grenade))
        {
            grenade.Launch(origin, decoy.ThrowVelocity(dir, charge01), transform);
            if (go.TryGetComponent(out NetworkObject netObj))
                netObj.Spawn();
        }
        else
        {
            Object.Destroy(go);
            return; // nothing was thrown, so nothing gets consumed
        }

        // One decoy off the stack (flare-ammo idiom): the slot only clears when the last one goes.
        int inStack = Mathf.Max(1, GetSlotStackCount(sel));
        if (inStack > 1)
        {
            int remaining = inStack - 1;
            decoy.SetStackCount(remaining);
            SetSlotStackCount(sel, (byte)remaining);
            RaiseChangedAndRefresh();
            return;
        }

        SetSlotItemId(sel, 0UL);
        SetSlotItemTypeId(sel, GrabbableInventoryItem.TypeIdNone);
        SetSlotStackCount(sel, 0);
        _selectedFlashlightLightOn.Value = false;
        ulong consumeId = decoy.ItemId;
        ConsumedItemNetworkStore.ServerMarkConsumed(consumeId);
        ConsumeItemClientRpc(consumeId);
        Object.Destroy(decoy.gameObject);
        SelectAfterDrop();
        RaiseChangedAndRefresh();
    }

    bool ServerTryResolveSelectedDecoyGrenade(int sel, out DecoyGrenadeItem decoy)
    {
        decoy = null;
        if (GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdDecoyGrenade)
            return false;

        ulong id = GetSlotItemId(sel);
        GrabbableInventoryItem g = null;
        bool resolved = id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out g) && g is DecoyGrenadeItem;
        if (!resolved)
        {
            Vector3 hint = playerController != null ? playerController.transform.position : transform.position;
            resolved = GrabbableInventoryItem.TryResolveForStateByType(
                id,
                hint,
                GrabbableInventoryItem.TypeIdDecoyGrenade,
                out g)
                && g is DecoyGrenadeItem;
        }

        decoy = g as DecoyGrenadeItem;
        return decoy != null;
    }
}
