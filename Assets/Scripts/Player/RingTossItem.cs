using UnityEngine;

/// <summary>
/// Ring toss projectile; behaves like <see cref="StarBallItem"/> — same hold / throw pipeline.
/// Assign <see cref="GrabbableInventoryItem.ItemTypeId"/> per-color in the inspector (defaults are fine).
/// </summary>
[DisallowMultipleComponent]
public sealed class RingTossItem : HeavyThrowableHoldItem { }
