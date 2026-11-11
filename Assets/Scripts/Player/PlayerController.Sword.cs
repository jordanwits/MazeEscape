using UnityEngine;

/// <summary>
/// Sword melee on the player. With a <see cref="SwordItem"/> in the selected hotbar slot, Attack swings the
/// blade instead of throwing a punch: a different animator trigger on its own upper-body-masked layer, a
/// longer wind-up, more reach and much more damage.
///
/// The swing reuses the punch's whole authority path — <see cref="TryMelee"/> triggers it, the delayed
/// <see cref="ApplyMeleeDamage"/> asks <see cref="NetworkPlayerCombat"/> for the hit, and the server runs
/// <see cref="ApplyMeleeDamageLocally"/>. Only the numbers differ, and they are read from the same
/// serialized fields on both sides, so the server never has to resolve the sword instance to adjudicate:
/// it just checks the replicated slot type on its own copy of the attacker.
/// </summary>
public partial class PlayerController
{
    /// <summary>Animator trigger for the swing; a state on the "Sword Swing" layer, masked to exclude the legs.</summary>
    const string SwordSwingTrigger = "SwordSwing";

    /// <summary>
    /// Float parameter the SwordSwing state uses as its speed multiplier. Pushed from
    /// <see cref="swordSwingAnimSpeed"/> so the animation and the derived hit delay always agree.
    /// </summary>
    static readonly int SwordSwingSpeedHash = Animator.StringToHash("SwordSwingSpeed");

    /// <summary>
    /// Keeps the animator's swing-speed multiplier equal to the serialized value. Written on every peer (not
    /// owner-gated) because it is a constant read from the shared prefab, so there is nothing for the
    /// replicated value to disagree with — and a non-owner needs it to play the swing at the right pace.
    /// </summary>
    void PushSwordSwingSpeedToAnimator()
    {
        if (animator == null)
            return;

        animator.SetFloat(SwordSwingSpeedHash, Mathf.Max(0.05f, swordSwingAnimSpeed));
    }

    /// <summary>
    /// Swing whoosh, fired by an animation event baked into SwordSwing.anim at
    /// <see cref="SwordItem.SwingWhooshSeconds"/>. Every peer plays its own clip, so the sound is locked to the
    /// blade on all of them, and changing the playback speed moves the sound with the animation automatically.
    /// Public and named exactly as the event expects — renaming it silently kills the sound.
    /// </summary>
    public void OnSwordSwingWhoosh()
    {
        PlaySelfOrBodyOneShot(swordSwooshClip != null ? swordSwooshClip : meleeSwooshClip, meleeSwooshVolume);
    }

    /// <summary>
    /// Type id of the melee weapon in the selected slot — <see cref="GrabbableInventoryItem.TypeIdSword"/>,
    /// <see cref="GrabbableInventoryItem.TypeIdCrowbar"/>, or <see cref="GrabbableInventoryItem.TypeIdNone"/> for
    /// bare fists (anything else in hand still punches). Deliberately answered from the replicated slot type
    /// rather than the item object, so it gives the same answer on the server (which adjudicates the hit for a
    /// remote attacker) as it does on the owner.
    /// </summary>
    public byte SelectedMeleeWeaponTypeId
    {
        get
        {
            byte typeId = GrabbableInventoryItem.TypeIdNone;
            if (IsUsingNetworkedInventory)
            {
                if (_networkPlayerInventory == null)
                    return GrabbableInventoryItem.TypeIdNone;
                int selected = _networkPlayerInventory.SelectedSlotIndex;
                if (selected < 0 || selected >= InventorySlotCapacity)
                    return GrabbableInventoryItem.TypeIdNone;
                if (IsHeavyThrowableForcingInventoryStash(
                        SelfNetworkObject != null ? SelfNetworkObject.NetworkObjectId : 0UL))
                {
                    return GrabbableInventoryItem.TypeIdNone;   // hotbar is pocketed while carrying a heavy throwable
                }

                typeId = _networkPlayerInventory.GetSlotItemTypeId(selected);
            }
            else
            {
                if (NetworkHeavyThrowableHold.FindOfflineHeldBy(this) != null)
                    return GrabbableInventoryItem.TypeIdNone;

                if (_localSelectedSlot < 0 || _localSelectedSlot >= InventorySlotCapacity)
                    return GrabbableInventoryItem.TypeIdNone;

                GrabbableInventoryItem item = _localInventorySlots[_localSelectedSlot];
                typeId = item != null ? item.ItemTypeId : GrabbableInventoryItem.TypeIdNone;
            }

            return typeId == GrabbableInventoryItem.TypeIdSword || typeId == GrabbableInventoryItem.TypeIdCrowbar
                ? typeId
                : GrabbableInventoryItem.TypeIdNone;
        }
    }

    /// <summary>True when the attacking player has a sword in the selected slot. See <see cref="SelectedMeleeWeaponTypeId"/>.</summary>
    public bool IsSwordSelected => SelectedMeleeWeaponTypeId == GrabbableInventoryItem.TypeIdSword;

    /// <summary>True when a swung weapon (sword or crowbar) replaces the punch, i.e. the swing is not bare-handed.</summary>
    public bool IsMeleeWeaponSelected => SelectedMeleeWeaponTypeId != GrabbableInventoryItem.TypeIdNone;

    /// <summary>Reach of the current melee: a weapon's longer arc, or the bare-handed punch.</summary>
    float ActiveMeleeRange => SelectedMeleeWeaponTypeId switch
    {
        GrabbableInventoryItem.TypeIdSword => swordMeleeRange,
        GrabbableInventoryItem.TypeIdCrowbar => crowbarMeleeRange,
        _ => meleeRange
    };

    /// <summary>Half-angle of the current melee cone. A swing sweeps wider than a straight punch.</summary>
    float ActiveMeleeAngle => SelectedMeleeWeaponTypeId switch
    {
        GrabbableInventoryItem.TypeIdSword => swordMeleeAngle,
        GrabbableInventoryItem.TypeIdCrowbar => crowbarMeleeAngle,
        _ => meleeAngle
    };

    /// <summary>Damage as a fraction of the target's max health, so it scales with every enemy species.</summary>
    float ActiveMeleeDamageFraction => SelectedMeleeWeaponTypeId switch
    {
        GrabbableInventoryItem.TypeIdSword => swordDamageFraction,
        GrabbableInventoryItem.TypeIdCrowbar => crowbarDamageFraction,
        _ => meleeDamageFraction
    };

    /// <summary>
    /// Seconds from the swing starting to the weapon being where the damage test runs. For a weapon this is
    /// DERIVED — the authored impact frame divided by the playback speed — so speeding the animation up moves
    /// the hit with it instead of leaving damage landing after the blade has already passed through.
    /// </summary>
    float ActiveMeleeHitDelay => SelectedMeleeWeaponTypeId switch
    {
        GrabbableInventoryItem.TypeIdSword => SwordItem.SwingImpactSeconds / Mathf.Max(0.05f, swordSwingAnimSpeed),
        GrabbableInventoryItem.TypeIdCrowbar => CrowbarItem.SwingImpactSeconds / Mathf.Max(0.05f, crowbarSwingAnimSpeed),
        _ => meleeHitDelay
    };

    float ActiveMeleeCooldown => SelectedMeleeWeaponTypeId switch
    {
        GrabbableInventoryItem.TypeIdSword => swordCooldown,
        GrabbableInventoryItem.TypeIdCrowbar => crowbarCooldown,
        _ => meleeCooldown
    };

    float ActiveMeleeStaminaCost => SelectedMeleeWeaponTypeId switch
    {
        GrabbableInventoryItem.TypeIdSword => swordStaminaCost,
        GrabbableInventoryItem.TypeIdCrowbar => crowbarStaminaCost,
        _ => punchStaminaCost
    };

    string ActiveMeleeTrigger => SelectedMeleeWeaponTypeId switch
    {
        GrabbableInventoryItem.TypeIdSword => SwordSwingTrigger,
        GrabbableInventoryItem.TypeIdCrowbar => CrowbarSwingTrigger,
        _ => meleeTrigger
    };

    /// <summary>
    /// Server-side cooldown gate in <see cref="NetworkPlayerCombat"/> uses this, so a sword attacker is not
    /// rejected by the shorter punch cooldown and a punching one is not given the sword's longer licence.
    /// </summary>
    public float ActiveMeleeCooldownForServer => ActiveMeleeCooldown;

    /// <summary>
    /// The same gate dates each swing from its start, which it takes as the hit's arrival minus this delay — the
    /// client starts its own cooldown when the swing starts, not when the hit lands.
    /// </summary>
    public float ActiveMeleeHitDelayForServer => ActiveMeleeHitDelay;

    /// <summary>Swing whoosh — the weapon's own clip when one is assigned, otherwise the shared melee swoosh.</summary>
    AudioClip ActiveMeleeSwooshClip => SelectedMeleeWeaponTypeId switch
    {
        GrabbableInventoryItem.TypeIdSword when swordSwooshClip != null => swordSwooshClip,
        GrabbableInventoryItem.TypeIdCrowbar => CrowbarSwooshClip,
        _ => meleeSwooshClip
    };

    /// <summary>
    /// Impact sound for a sword hit. Falls back to the punch clips when no sword-specific clip is assigned,
    /// so the weapon is audible before any new audio is authored.
    /// </summary>
    public void PlaySwordHitSfx()
    {
        TriggerMeleeCameraKick(meleeKickSkeletonScale);   // a blade lands heavier than a fist

        PlaySelfOrBodyOneShot(swordHitClip != null ? swordHitClip : PickRandomMeleeHitClip(), meleeHitPunchVolume);
    }
}
