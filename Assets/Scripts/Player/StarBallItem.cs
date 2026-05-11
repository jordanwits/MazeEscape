using UnityEngine;

/// <summary>
/// Carnival StarBall; held at the hold point like other grabbables but never uses hotbar slots
/// (handled by <see cref="NetworkHeavyThrowableHold"/>).
/// </summary>
[DisallowMultipleComponent]
public sealed class StarBallItem : HeavyThrowableHoldItem
{
    protected override void Awake()
    {
        _itemTypeId = TypeIdStarBall;
        base.Awake();
    }
}
