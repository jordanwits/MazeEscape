using UnityEngine;

/// <summary>
/// A pickup that fills one hotbar slot but represents several units — glowsticks, flare rounds. The slot's
/// unit count is authoritative on the server in <see cref="NetworkPlayerInventory"/>'s per-slot stack
/// NetworkVariable (and in <c>PlayerController._localSlotStacks</c> offline); this instance mirrors it so the
/// world copy that gets dropped back out carries the right count on every peer.
/// </summary>
public abstract class StackableInventoryItem : GrabbableInventoryItem
{
    [Tooltip("How many units this pickup represents (chest loot typically spawns a full stack).")]
    [SerializeField] protected int _stackCount = 1;

    public override int StackCount => _stackCount;

    /// <summary>Clamps to <see cref="GrabbableInventoryItem.MaxStackSize"/>.</summary>
    public override void SetStackCount(int count)
    {
        _stackCount = Mathf.Clamp(count, 1, MaxStackSize);
    }

    /// <summary>Adds to the stack up to the cap; returns how much actually fit.</summary>
    public int AddToStackClamped(int delta)
    {
        int next = Mathf.Clamp(_stackCount + delta, 1, MaxStackSize);
        int applied = next - _stackCount;
        _stackCount = next;
        return applied;
    }

    protected override void Awake()
    {
        _stackCount = Mathf.Clamp(_stackCount, 1, MaxStackSize);
        base.Awake();
    }
}
