using UnityEngine;

/// <summary>Inventory item used with <see cref="HingeInteractDoor"/> when <c>useKeyToUnlock</c> is set.</summary>
public class KeyItem : GrabbableInventoryItem
{
    /// <summary>Sprite from the first key instance (prefab slot icon), for HUD when slot type is known but the world item is not resolved.</summary>
    public static Sprite SharedHudSlotIcon { get; private set; }

    protected override void Awake()
    {
        _itemTypeId = TypeIdKey;
        base.Awake();
        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
