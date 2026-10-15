using UnityEngine;

/// <summary>
/// Consumable inventory item: use (same binding as flashlight by default) to restore health and remove from inventory.
/// Next to a hurt teammate, Interact uses it on them instead (PlayerController.TeammateHeal).
/// </summary>
public class BandageItem : GrabbableInventoryItem
{
    public const float HealthRestoreAmount = 50f;

    /// <summary>How close (horizontally, feet to feet) a teammate must be for the "Heal NAME" prompt.</summary>
    public const float TeammateHealReach = 2.2f;

    /// <summary>Teammates on another floor of a stair or a ledge are out of reach, however close in plan.</summary>
    const float TeammateHealMaxHeightDelta = 1.6f;

    public static Sprite SharedHudSlotIcon { get; private set; }

    protected override void Awake()
    {
        _itemTypeId = TypeIdBandage;
        base.Awake();
        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }

    public static bool IsWithinTeammateHealReach(Vector3 healerFeet, Vector3 targetFeet, float slack = 0f)
    {
        Vector3 delta = targetFeet - healerFeet;
        if (Mathf.Abs(delta.y) > TeammateHealMaxHeightDelta + slack)
            return false;

        delta.y = 0f;
        float reach = TeammateHealReach + slack;
        return delta.sqrMagnitude <= reach * reach;
    }
}
