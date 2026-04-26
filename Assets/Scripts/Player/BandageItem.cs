using UnityEngine;

/// <summary>Consumable inventory item: use (same binding as flashlight by default) to restore health and remove from inventory.</summary>
public class BandageItem : GrabbableInventoryItem
{
    public const float HealthRestoreAmount = 50f;

    public static Sprite SharedHudSlotIcon { get; private set; }

    protected override void Awake()
    {
        _itemTypeId = TypeIdBandage;
        base.Awake();
        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
