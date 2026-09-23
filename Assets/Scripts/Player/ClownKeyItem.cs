using UnityEngine;

/// <summary>
/// The Clown's key to the carnival exit elevator. Level02's cab (<see cref="ElevatorFinishController"/> with
/// <c>requiresClownKeyToOpen</c>) will not answer its outside call pad without one, so this is the section's
/// gate: buy it at the prize counter for a small fortune in tickets, or take it off the Clown's body.
///
/// Deliberately NOT a subclass of <see cref="KeyItem"/>. The server's key checks in
/// <see cref="NetworkPlayerInventory"/> resolve by <c>is KeyItem</c> as well as by type id, so deriving would
/// make an ordinary jail key open the elevator and a clown key open a cell. They are separate types end to end.
/// </summary>
public class ClownKeyItem : GrabbableInventoryItem
{
    /// <summary>
    /// Slot icon taken off the first instance built on this peer, so the HUD can draw a clown-key slot whose
    /// world item has not resolved yet (a teammate's, or one still syncing). Mirrors <see cref="KeyItem"/>.
    /// </summary>
    public static Sprite SharedHudSlotIcon { get; private set; }

    protected override void Awake()
    {
        _itemTypeId = TypeIdClownKey;
        base.Awake();
        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
