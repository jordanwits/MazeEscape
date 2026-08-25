using UnityEngine;

/// <summary>
/// Decoy grenade input for the local player. Structurally the throwing half of
/// <see cref="PlayerController"/>'s flashbang partial and nothing else — a decoy never does anything TO
/// the thrower, so there is no screen effect to mirror; it lands, shouts, and the enemies deal with it.
///
/// Attack with a <see cref="DecoyGrenadeItem"/> selected winds up the same press-charge-release throw as a
/// flashbang or a heavy throwable (see <see cref="HandleAttackInput"/>), so all three grenades feel the
/// same in the hand.
/// </summary>
public partial class PlayerController
{
    /// <summary>
    /// Set when the throw charge currently winding up belongs to a decoy rather than a flashbang or a
    /// carried heavy throwable, so the release routes to the right one.
    /// </summary>
    bool _chargingDecoyThrow;

    float _nextDecoyThrowTime;

    /// <summary>
    /// Is a decoy grenade the selected hotbar item? Gates the wind-up in <see cref="HandleAttackInput"/>
    /// so a decoy in hand charges a throw instead of punching.
    /// </summary>
    bool HasSelectedDecoyGrenade()
    {
        return TryGetSelectedDecoyGrenade(out _);
    }

    /// <summary>
    /// Charge released with a decoy selected: throw it along the camera aim.
    /// <paramref name="charge01"/> scales the launch speed exactly as it does for a flashbang.
    /// </summary>
    void ThrowSelectedDecoyGrenade(float charge01)
    {
        if (!TryGetSelectedDecoyGrenade(out DecoyGrenadeItem decoy))
            return;

        if (Time.time < _nextDecoyThrowTime)
            return;

        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return;

        _nextDecoyThrowTime = Time.time + DecoyGrenadeItem.ThrowCooldownSeconds;

        Vector3 aim = cam.forward;
        float forwardOffset = decoy != null ? decoy.ThrowSpawnForwardOffset : 0.42f;
        Vector3 origin = cam.position + aim.normalized * forwardOffset;
        // Never release below the thrower's feet — matches the heavy-throwable release clamp.
        origin.y = Mathf.Max(origin.y, transform.position.y + 0.1f);

        if (IsUsingNetworkedInventory)
        {
            _networkPlayerInventory.RequestThrowSelectedDecoyGrenade(origin, aim, charge01);
            return;
        }

        ThrowDecoyGrenadeOffline(decoy, origin, aim, charge01);
    }

    void ThrowDecoyGrenadeOffline(DecoyGrenadeItem decoy, Vector3 origin, Vector3 aim, float charge01)
    {
        if (decoy == null || decoy.GrenadePrefab == null)
            return;

        GameObject go = Object.Instantiate(decoy.GrenadePrefab, origin, Quaternion.LookRotation(aim));
        if (!go.TryGetComponent(out DecoyGrenade grenade))
        {
            Object.Destroy(go);
            return; // nothing was thrown, so nothing gets consumed
        }

        grenade.Launch(origin, decoy.ThrowVelocity(aim, charge01), transform);

        // One decoy off the stack; the slot only clears when the last one goes.
        int slot = _localSelectedSlot;
        int inStack = Mathf.Max(1, _localSlotStacks[slot]);
        if (inStack > 1)
        {
            int remaining = inStack - 1;
            decoy.SetStackCount(remaining);
            _localSlotStacks[slot] = remaining;
        }
        else
        {
            _localInventorySlots[slot] = null;
            _localSlotStacks[slot] = 0;
            SelectAfterDropLocal();
            Object.Destroy(decoy.gameObject);
        }

        RefreshLocalInventoryView();
    }

    /// <summary>Selected-slot decoy, resolved the same way the flashbang resolves its own item.</summary>
    bool TryGetSelectedDecoyGrenade(out DecoyGrenadeItem decoy)
    {
        decoy = null;

        if (IsUsingNetworkedInventory)
        {
            int sel = _networkPlayerInventory.SelectedSlotIndex;
            if (_networkPlayerInventory.GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdDecoyGrenade)
                return false;

            ulong id = _networkPlayerInventory.GetSlotItemId(sel);
            if (id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g))
                decoy = g as DecoyGrenadeItem;
            // The slot type is authoritative for gating input; a null instance just means the local copy has
            // not resolved yet, and the server does its own resolve before spawning anything.
            return true;
        }

        if (_localSelectedSlot < 0 || _localSelectedSlot >= InventorySlotCapacity
            || _localInventorySlots[_localSelectedSlot] is not DecoyGrenadeItem localDecoy)
            return false;

        decoy = localDecoy;
        return true;
    }
}
