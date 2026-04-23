using UnityEngine;

/// <summary>Inventory item used with <see cref="HingeInteractDoor"/> when <c>useKeyToUnlock</c> is set.</summary>
public class KeyItem : GrabbableInventoryItem
{
    protected override void Awake()
    {
        _itemTypeId = TypeIdKey;
        base.Awake();
    }
}
