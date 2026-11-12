using UnityEngine;

/// <summary>
/// A swung hotbar weapon that wears out — the base of <see cref="SwordItem"/> and <see cref="CrowbarItem"/>.
/// Every swing that connects with something costs one hit of durability (a whiff costs nothing, and a swing
/// that catches two enemies at once still costs one); the hit that spends the last of it breaks the weapon,
/// which is then gone from the hotbar.
///
/// The count lives on this instance, like the flare gun's rounds, and is authoritative on the server online
/// (local truth offline). The instance is what moves between hands — dropped, picked up by a teammate,
/// scattered on a disconnect, parked in the carry-over pen across a section switch — so a worn weapon stays
/// worn wherever it goes. A client never reads its own copy's count: the server publishes the real one through
/// the per-slot charge NetworkVariable (see <see cref="NetworkPlayerInventory.GetSlotFlashlightBatteryNormalizedForHud"/>).
/// </summary>
public abstract class MeleeWeaponItem : GrabbableInventoryItem
{
    /// <summary>Connecting swings a fresh weapon survives; the last of them breaks it.</summary>
    public abstract int MaxDurabilityHits { get; }

    /// <summary>Hits left before the weapon breaks. Authoritative on the server online; local truth offline.</summary>
    public int HitsRemaining { get; private set; }

    /// <summary>0…1 durability left, for the HUD gauge.</summary>
    public float DurabilityNormalized =>
        MaxDurabilityHits > 0 ? Mathf.Clamp01(HitsRemaining / (float)MaxDurabilityHits) : 0f;

    protected override void Awake()
    {
        HitsRemaining = MaxDurabilityHits;
        base.Awake();
    }

    /// <summary>
    /// One connecting swing's wear. Returns true when this hit broke the weapon. Call on the authority only
    /// (server online, anyone offline) — the caller removes the broken weapon from the hotbar.
    /// </summary>
    public bool ApplyHitWear()
    {
        if (HitsRemaining > 0)
            HitsRemaining--;

        return HitsRemaining <= 0;
    }
}
