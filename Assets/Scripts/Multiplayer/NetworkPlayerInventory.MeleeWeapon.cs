using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Melee weapon durability (sword, crowbar), server-authoritative. The server already adjudicates every swing
/// from its own copy of the attacker (<see cref="PlayerController.ApplyServerAuthoritativeMeleeDamage"/>); when
/// that swing connects, it calls <see cref="ServerWearSelectedMeleeWeapon"/> here, which spends one hit off the
/// weapon in the selected slot and, on the last one, breaks it: the break sound plays on every peer and the
/// weapon is removed from the slot and the world exactly the way a used bandage is.
///
/// Remaining durability reaches the HUD through the shared per-slot charge NetworkVariable, which the server's
/// <see cref="Update"/> already mirrors every frame (battery for a flashlight, rounds for the flare gun,
/// durability for a melee weapon — a slot only ever holds one of them).
/// </summary>
public partial class NetworkPlayerInventory
{
    /// <summary>
    /// Server-only. One connecting swing's wear on the melee weapon in the selected slot.
    /// <paramref name="weaponTypeId"/> is the weapon the swing was adjudicated with; the slot must still hold
    /// that type, so wear can never land on whatever else happens to be selected.
    /// </summary>
    public void ServerWearSelectedMeleeWeapon(byte weaponTypeId)
    {
        if (!IsServer || !IsSpawned || weaponTypeId == GrabbableInventoryItem.TypeIdNone)
            return;

        int sel = SelectedSlotIndex;
        if (sel < 0 || sel >= MaxSlotCount || GetSlotItemTypeId(sel) != weaponTypeId)
            return;

        ulong id = GetSlotItemId(sel);
        if (id == 0UL
            || !GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g)
            || g is not MeleeWeaponItem weapon
            || weapon.ItemTypeId != weaponTypeId)
        {
            return;
        }

        if (!weapon.ApplyHitWear())
        {
            // Publish now rather than on the next Update so the gauge drops on the hit, not a frame after.
            SetSlotFlashlightBatteryNormalized(sel, weapon.DurabilityNormalized);
            return;
        }

        ServerBreakMeleeWeaponInSlot(sel, weapon);
    }

    /// <summary>
    /// Server: the weapon in <paramref name="slot"/> just took its last hit. Plays the break on every peer, then
    /// tears it down like a consumed bandage (slot cleared, tombstoned for late joiners, destroyed everywhere).
    ///
    /// Unlike the bandage this deliberately does NOT move the selection to another item. A weapon breaks in the
    /// middle of a fight, with Attack held down; jumping to the next occupied slot would turn the very next
    /// Attack into a flare shot, a thrown flashbang or a harmonica tune. Staying on the now-empty slot keeps it
    /// a punch.
    /// </summary>
    void ServerBreakMeleeWeaponInSlot(int slot, GrabbableInventoryItem weapon)
    {
        // Sent before the teardown so every peer still has the holder in hand when the sound plays.
        PlayMeleeWeaponBreakClientRpc();

        SetSlotItemId(slot, 0UL);
        SetSlotItemTypeId(slot, GrabbableInventoryItem.TypeIdNone);
        SetSlotStackCount(slot, 0);
        SetSlotFlashlightBatteryNormalized(slot, 0f);
        if (slot == SelectedSlotIndex)
            _selectedFlashlightLightOn.Value = false;

        if (!ServerTryDespawnConsumedNetworkItem(weapon))
        {
            ulong consumeId = weapon.ItemId;
            ConsumedItemNetworkStore.ServerMarkConsumed(consumeId);
            ConsumeItemClientRpc(consumeId);
            Object.Destroy(weapon.gameObject);
        }

        RaiseChangedAndRefresh();
    }

    [ClientRpc]
    void PlayMeleeWeaponBreakClientRpc()
    {
        playerController?.PlayMeleeWeaponBreakSfx();
    }
}
