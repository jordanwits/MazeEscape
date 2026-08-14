using UnityEngine;

/// <summary>
/// Holds a maze loot pickup exactly where its marker placed it until a player first takes it.
///
/// <see cref="ProceduralMazeCoordinator"/> already freezes each pickup once, as it spawns it. That
/// one-shot is not enough on its own: an untouched world pickup can still be handed back to world
/// physics afterwards — <see cref="GrabbableInventoryItem.ApplyNetworkWorldState"/> ends in
/// <c>EndHeldState(enableWorldPhysics: true)</c>, which turns gravity back on, and the by-type fallback
/// that resolves those states can land on a pickup nobody has touched. A boxy item settles back where it
/// was and the drift goes unnoticed; a cylindrical one (a flare round on a shelf) rolls off.
///
/// Re-asserting the frozen state each physics step is cheap (a handful of pickups per level) and makes
/// "stays where it was authored" true no matter which path re-enables physics. The component removes
/// itself the moment the item is picked up, so drop and throw physics behave normally from then on.
/// </summary>
[DisallowMultipleComponent]
public class MazeItemPickupRest : MonoBehaviour
{
    GrabbableInventoryItem _item;
    Rigidbody[] _bodies;

    void Awake()
    {
        _item = GetComponent<GrabbableInventoryItem>();
        _bodies = GetComponentsInChildren<Rigidbody>(true);
    }

    void FixedUpdate()
    {
        // Once a player has it, the item owns its own physics for good.
        if (_item != null && (_item.IsHeld || _item.HolderNetworkObjectId != 0UL))
        {
            Destroy(this);
            return;
        }

        if (_bodies == null)
            return;

        for (int i = 0; i < _bodies.Length; i++)
        {
            Rigidbody rb = _bodies[i];
            if (rb == null)
                continue;

            if (rb.isKinematic && !rb.useGravity)
                continue;

            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.isKinematic = true;
        }
    }
}
