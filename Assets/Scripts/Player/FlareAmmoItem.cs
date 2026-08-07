using UnityEngine;

/// <summary>
/// Flare rounds. Sits in a hotbar slot like any small item and stacks up to <see cref="MaxStack"/> rounds per
/// slot; reloading the <see cref="FlareGunItem"/> draws from the stack (in any slot) until the gun is full,
/// and a stack that runs out is destroyed through the same tombstone path bandages use, so late joiners never
/// rebuild a ghost pickup.
/// </summary>
public class FlareAmmoItem : StackableInventoryItem
{
    /// <summary>Rounds that fit in one hotbar slot.</summary>
    public const int MaxStack = 10;

    /// <summary>
    /// Rounds a world loot pickup is worth — one full flare gun load. Shared by both loot spawners
    /// (<see cref="MazeChest"/> and the maze's ItemSpawn markers) so they can't drift apart.
    /// </summary>
    public const int LootStackSize = 3;

    public static Sprite SharedHudSlotIcon { get; private set; }

    public override int MaxStackSize => MaxStack;

    protected override void Awake()
    {
        _itemTypeId = TypeIdFlareAmmo;
        base.Awake();
        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
