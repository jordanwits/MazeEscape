using UnityEngine;

/// <summary>
/// A single flare round. Sits in a hotbar slot like any small item; consumed (from any slot) when the
/// player reloads the <see cref="FlareGunItem"/> — the server destroys it through the same tombstone path
/// bandages use, so late joiners never rebuild a ghost pickup.
/// </summary>
public class FlareAmmoItem : GrabbableInventoryItem
{
    public static Sprite SharedHudSlotIcon { get; private set; }

    protected override void Awake()
    {
        _itemTypeId = TypeIdFlareAmmo;
        base.Awake();
        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
