using UnityEngine;

/// <summary>
/// Gated pickup: pressing E starts a short arm reach toward the aimed item; the actual (unchanged)
/// pickup grant fires when the hand arrives at the apex. If the item was taken, destroyed or left range
/// mid-reach the reach whiffs — the arm settles back and nothing is granted. Chest/door/carnival
/// interactions are NOT gated; heavy-throwable carriers keep the legacy instant grant (hands stay braced).
/// PlayerItemHoldIK renders the arm extension from <see cref="PickupReachTargetItemId"/> locally and from
/// NetworkPlayerAvatar.ReachTargetItemId on remote peers.
/// </summary>
public partial class PlayerController
{
    enum PickupReachState { None, Reaching, Settling }

    [Header("Gated pickup reach")]
    [Tooltip("Seconds from pressing E until the hand 'arrives' and the pickup is actually granted.")]
    [SerializeField] float reachApexSeconds = 0.32f;
    [Tooltip("Seconds after the apex before another pickup can start (arm settles back).")]
    [SerializeField] float reachSettleSeconds = 0.22f;
    [SerializeField] float reachRetryLockoutSeconds = 0.1f;

    PickupReachState _reachState;
    ulong _reachItemId;
    float _reachTimer;
    float _reachLockoutUntil;

    /// <summary>True while a gated-pickup reach (including the settle tail) is in flight.</summary>
    public bool PickupReachActive => _reachState != PickupReachState.None;

    /// <summary>Item id the local player is currently reaching for (0 = none). Read by PlayerItemHoldIK.</summary>
    public ulong PickupReachTargetItemId => _reachState == PickupReachState.Reaching ? _reachItemId : 0UL;

    void BeginGatedPickup()
    {
        if (_reachState != PickupReachState.None || Time.time < _reachLockoutUntil)
            return;

        // Hands stay braced on a carried heavy throwable: keep the legacy instant grant, no reach.
        if (IsHoldingHeavyThrowable())
        {
            if (IsUsingNetworkedInventory)
                TryPickupNetwork();
            else
                TryPickupLocal();
            return;
        }

        if (!TryFindInteractableGrabbable(out GrabbableInventoryItem g) || g == null)
            return;

        _reachItemId = g.ItemId;
        _reachState = PickupReachState.Reaching;
        _reachTimer = 0f;
        if (_networkPlayerAvatar != null)
            _networkPlayerAvatar.PublishReachTarget(_reachItemId);
    }

    /// <summary>Ticked at the very top of Update so cancels still process while local control is lost.</summary>
    void TickPickupReach()
    {
        if (_reachState == PickupReachState.None)
            return;

        bool cancel =
            !_hasLocalControl
            || (_ragdollController != null && (_ragdollController.IsRagdolled || _ragdollController.IsGettingUp || _ragdollController.IsHeld))
            || (_networkPlayerAvatar != null && _networkPlayerAvatar.IsCarriedByJailor)
            || (_playerHealth != null && _playerHealth.IsDead)
            || _blackjackSeated
            || IsPostJailMovementLocked;
        if (cancel)
        {
            AbortPickupReach();
            return;
        }

        _reachTimer += Time.deltaTime;

        if (_reachState == PickupReachState.Reaching && _reachTimer >= reachApexSeconds)
        {
            _reachState = PickupReachState.Settling;
            TryGrantReachedPickup();
        }

        if (_reachState == PickupReachState.Settling && _reachTimer >= reachApexSeconds + reachSettleSeconds)
            EndPickupReach();
    }

    void AbortPickupReach() => EndPickupReach();

    void EndPickupReach()
    {
        _reachState = PickupReachState.None;
        _reachItemId = 0UL;
        _reachLockoutUntil = Time.time + reachRetryLockoutSeconds;
        if (_networkPlayerAvatar != null)
            _networkPlayerAvatar.PublishReachTarget(0UL);
    }

    /// <summary>
    /// Apex re-validation + grant through the exact same paths the old instant pickup used. The server
    /// still re-validates range and exact item id, so contested pickups whiff safely on the loser.
    /// </summary>
    void TryGrantReachedPickup()
    {
        if (!GrabbableInventoryItem.TryGetRegistered(_reachItemId, out GrabbableInventoryItem g)
            || g == null
            || !g.gameObject.activeInHierarchy
            || g.IsHeld)
        {
            return; // whiff: gone or grabbed by someone else mid-reach
        }

        Transform cam = CameraTransformForFacing;
        if (cam != null)
        {
            Vector3 aim = g.GetInteractAimPointClosestTo(cam.position);
            if ((aim - cam.position).magnitude > interactDistance + 0.75f)
                return; // whiff: walked away during the reach (lenient client check; server is authoritative)
        }

        if (g is HeavyThrowableHoldItem && g.TryGetComponent(out NetworkHeavyThrowableHold heavyHold))
        {
            if (IsUsingNetworkedInventory)
                heavyHold.RequestPickupFromInteract(this);
            else
                heavyHold.TryPickupOffline(this);
            return;
        }

        if (IsUsingNetworkedInventory)
        {
            if (_networkPlayerInventory != null && _networkPlayerInventory.CanPickup(g))
                _networkPlayerInventory.TryPickupItem(g);
            return;
        }

        TryPickupItemLocal(g);
    }
}
