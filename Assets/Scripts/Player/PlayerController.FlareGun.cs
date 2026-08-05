using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Flare gun input + presentation on the player. Attack fires the selected flare gun instead of punching;
/// the use-item binding (same one as flashlight/bandage) reloads it from a <see cref="FlareAmmoItem"/> in
/// any hotbar slot. Firing and reloading are validated server-side in <see cref="NetworkPlayerInventory"/>;
/// this partial only gates input, plays owner-predicted effects, and runs the reload presentation every
/// peer shares (body animation trigger from the owner + the gun's own barrel/shell visual).
/// </summary>
public partial class PlayerController
{
    const string FlareReloadTriggerName = "FlareReload";

    float _nextFlareFireTime;
    float _flareBusyUntil;

    /// <summary>
    /// Attack pressed while a flare gun is selected: fire (or auto-reload when empty, or dry-click).
    /// Returns false when no flare gun is selected so the press falls through to melee.
    /// </summary>
    bool TryHandleFlareGunAttackPress()
    {
        if (!TryGetSelectedFlareGunState(out FlareGunItem gun, out int rounds))
            return false;

        // Mid-reload (or a stale item resolve): swallow the press so it never punches with a gun in hand.
        if (Time.time < _flareBusyUntil || (gun != null && gun.IsReloadVisualActive))
            return true;

        if (rounds <= 0)
        {
            if (HasFlareAmmoInInventory())
                RequestFlareReloadFromInput();
            else if (gun != null)
                gun.PlayDryFireSfx();
            return true;
        }

        if (Time.time < _nextFlareFireTime)
            return true;

        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return true;

        Vector3 forward = cam.forward;
        Vector3 origin = cam.position + forward * 0.35f;
        _nextFlareFireTime = Time.time + FlareGunItem.FireCooldownSeconds;

        if (IsUsingNetworkedInventory)
        {
            _networkPlayerInventory.RequestFireSelectedFlareGun(origin, forward);
            // Owner-predicted muzzle flash/sound; the server RPC covers the other peers.
            gun?.PlayFireEffects();
            return true;
        }

        if (gun != null && gun.TryConsumeRound())
        {
            SpawnFlareProjectileOffline(gun, origin, forward);
            gun.PlayFireEffects();
        }
        else
        {
            gun?.PlayDryFireSfx();
        }

        return true;
    }

    void SpawnFlareProjectileOffline(FlareGunItem gun, Vector3 origin, Vector3 direction)
    {
        if (gun == null || gun.ProjectilePrefab == null)
            return;

        GameObject go = Object.Instantiate(gun.ProjectilePrefab, origin, Quaternion.LookRotation(direction));
        if (go.TryGetComponent(out FlareProjectile projectile))
            projectile.Launch(origin, direction, transform, _playerHealth);
        else
            Object.Destroy(go);
    }

    /// <summary>Use-item binding with the flare gun selected: reload one round from carried flare ammo.</summary>
    void RequestFlareReloadFromInput()
    {
        if (Time.time < _flareBusyUntil)
            return;

        if (!TryGetSelectedFlareGunState(out FlareGunItem gun, out int rounds))
            return;

        if (rounds >= FlareGunItem.MaxRounds || !HasFlareAmmoInInventory())
            return;

        if (IsUsingNetworkedInventory)
        {
            // Small optimistic block so mashing the key doesn't spam RPCs; the authoritative block is set
            // when the server's reload FX callback lands.
            _flareBusyUntil = Time.time + 0.3f;
            _networkPlayerInventory.RequestReloadSelectedFlareGun();
            return;
        }

        if (gun == null)
            return;

        for (int i = 0; i < 3; i++)
        {
            if (_localInventorySlots[i] is not FlareAmmoItem ammo)
                continue;

            _localInventorySlots[i] = null;
            _localSlotStacks[i] = 0;
            Object.Destroy(ammo.gameObject);
            gun.TryAddRound();
            PlayFlareReloadEffects(gun);
            RefreshLocalInventoryView();
            return;
        }
    }

    /// <summary>
    /// Reload presentation on every peer (invoked by the inventory's reload ClientRpc, or directly
    /// offline): the animation owner fires the replicated FlareReload trigger, and the gun runs its
    /// barrel-tilt + shell-insert visual locally.
    /// </summary>
    public void PlayFlareReloadEffects(FlareGunItem gun)
    {
        _flareBusyUntil = Time.time + FlareGunItem.ReloadDurationSeconds + 0.1f;

        bool networkActive = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        bool ownsBodyAnimation = !networkActive
            || (_networkPlayerAvatar != null && _networkPlayerAvatar.IsSpawned && _networkPlayerAvatar.IsOwner);
        if (ownsBodyAnimation)
        {
            if (_networkPlayerAvatar != null)
                _networkPlayerAvatar.TriggerAnimation(FlareReloadTriggerName);
            else if (animator != null)
                animator.SetTrigger(FlareReloadTriggerName);
        }

        gun?.PlayReloadVisual(animator);
    }

    /// <summary>Selected-slot flare gun plus its peer-correct round count (replicated online, local offline).</summary>
    bool TryGetSelectedFlareGunState(out FlareGunItem gun, out int rounds)
    {
        gun = null;
        rounds = 0;

        if (IsUsingNetworkedInventory)
        {
            int sel = _networkPlayerInventory.SelectedSlotIndex;
            if (_networkPlayerInventory.GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdFlareGun)
                return false;

            ulong id = _networkPlayerInventory.GetSlotItemId(sel);
            if (id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g))
                gun = g as FlareGunItem;
            rounds = _networkPlayerInventory.GetSlotFlareRoundsForHud(sel);
            return true;
        }

        if (_localSelectedSlot < 0 || _localSelectedSlot >= 3
            || _localInventorySlots[_localSelectedSlot] is not FlareGunItem localGun)
            return false;

        gun = localGun;
        rounds = localGun.LoadedRounds;
        return true;
    }

    bool HasFlareAmmoInInventory()
    {
        if (IsUsingNetworkedInventory)
        {
            if (_networkPlayerInventory == null)
                return false;
            for (int i = 0; i < 3; i++)
            {
                if (_networkPlayerInventory.GetSlotItemTypeId(i) == GrabbableInventoryItem.TypeIdFlareAmmo)
                    return true;
            }

            return false;
        }

        for (int i = 0; i < 3; i++)
        {
            if (_localInventorySlots[i] is FlareAmmoItem)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Rounds of the flare gun currently in hand for the vitals-cluster gauge (the same gauge the
    /// flashlight battery uses; a slot can only hold one of the two). 0..1 = rounds / capacity.
    /// </summary>
    bool TryGetHeldFlareGunRoundsForHud(out float normalized)
    {
        normalized = 0f;

        if (IsUsingNetworkedInventory)
        {
            NetworkObject self = SelfNetworkObject;
            ulong holderId = self != null ? self.NetworkObjectId : 0UL;
            if (IsHeavyThrowableForcingInventoryStash(holderId))
                return false;
            int selected = _networkPlayerInventory.SelectedSlotIndex;
            if (selected < 0 || selected >= 3
                || _networkPlayerInventory.GetSlotItemTypeId(selected) != GrabbableInventoryItem.TypeIdFlareGun)
                return false;
            normalized = Mathf.Clamp01(_networkPlayerInventory.GetSlotFlareRoundsForHud(selected) / (float)FlareGunItem.MaxRounds);
            return true;
        }

        if (NetworkHeavyThrowableHold.FindOfflineHeldBy(this) != null)
            return false;
        if (_localSelectedSlot < 0 || _localSelectedSlot >= 3
            || _localInventorySlots[_localSelectedSlot] is not FlareGunItem gun)
            return false;
        normalized = Mathf.Clamp01(gun.LoadedRounds / (float)FlareGunItem.MaxRounds);
        return true;
    }
}
