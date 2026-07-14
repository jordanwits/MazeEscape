using UnityEngine;

/// <summary>
/// Consumable inventory item: use (same binding as the flashlight/bandage) to grant a short
/// unlimited-stamina + movement-speed boost, then remove itself from the inventory. Tunables live
/// on the prefab so the item is self-describing; <see cref="PlayerController.ActivateEnergyDrinkBoost"/>
/// runs the actual owner-side effect.
/// </summary>
public class EnergyDrinkItem : GrabbableInventoryItem
{
    [Header("Energy Drink Effect")]
    [Tooltip("Seconds the unlimited-stamina + speed boost lasts after drinking.")]
    [SerializeField] float boostDurationSeconds = 5f;
    [Tooltip("Movement speed multiplier while the boost is active (1.25 = +25% faster).")]
    [SerializeField, Min(1f)] float speedMultiplier = 1.25f;

    public float BoostDurationSeconds => boostDurationSeconds;
    public float SpeedMultiplier => speedMultiplier;

    public static Sprite SharedHudSlotIcon { get; private set; }

    protected override void Awake()
    {
        _itemTypeId = TypeIdEnergyDrink;
        base.Awake();
        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
