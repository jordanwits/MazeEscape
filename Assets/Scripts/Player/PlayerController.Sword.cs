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
        AudioClip clip = swordSwooshClip != null ? swordSwooshClip : meleeSwooshClip;
        if (clip == null || footstepAudioSource == null)
            return;

        footstepAudioSource.PlayOneShot(clip, Mathf.Max(0f, meleeSwooshVolume));
    }

    /// <summary>
    /// True when the attacking player has a sword in the selected slot. Deliberately answered from the
    /// replicated slot type rather than the item object, so it gives the same answer on the server (which
    /// adjudicates the hit for a remote attacker) as it does on the owner.
    /// </summary>
    public bool IsSwordSelected
    {
        get
        {
            if (IsUsingNetworkedInventory)
            {
                if (_networkPlayerInventory == null)
                    return false;
                int selected = _networkPlayerInventory.SelectedSlotIndex;
                if (selected < 0 || selected >= 3)
                    return false;
                if (IsHeavyThrowableForcingInventoryStash(
                        SelfNetworkObject != null ? SelfNetworkObject.NetworkObjectId : 0UL))
                {
                    return false;   // hotbar is pocketed while carrying a heavy throwable
                }

                return _networkPlayerInventory.GetSlotItemTypeId(selected) == GrabbableInventoryItem.TypeIdSword;
            }

            if (NetworkHeavyThrowableHold.FindOfflineHeldBy(this) != null)
                return false;

            return _localSelectedSlot >= 0 && _localSelectedSlot < 3
                && _localInventorySlots[_localSelectedSlot] is SwordItem;
        }
    }

    /// <summary>Reach of the current melee: the sword's longer arc, or the bare-handed punch.</summary>
    float ActiveMeleeRange => IsSwordSelected ? swordMeleeRange : meleeRange;

    /// <summary>Half-angle of the current melee cone. A swing sweeps wider than a straight punch.</summary>
    float ActiveMeleeAngle => IsSwordSelected ? swordMeleeAngle : meleeAngle;

    /// <summary>Damage as a fraction of the target's max health, so it scales with every enemy species.</summary>
    float ActiveMeleeDamageFraction => IsSwordSelected ? swordDamageFraction : meleeDamageFraction;

    /// <summary>
    /// Seconds from the swing starting to the blade being where the damage test runs. For the sword this is
    /// DERIVED — the authored impact frame divided by the playback speed — so speeding the animation up moves
    /// the hit with it instead of leaving damage landing after the blade has already passed through.
    /// </summary>
    float ActiveMeleeHitDelay => IsSwordSelected
        ? SwordItem.SwingImpactSeconds / Mathf.Max(0.05f, swordSwingAnimSpeed)
        : meleeHitDelay;

    float ActiveMeleeCooldown => IsSwordSelected ? swordCooldown : meleeCooldown;

    float ActiveMeleeStaminaCost => IsSwordSelected ? swordStaminaCost : punchStaminaCost;

    string ActiveMeleeTrigger => IsSwordSelected ? SwordSwingTrigger : meleeTrigger;

    /// <summary>
    /// Server-side cooldown gate in <see cref="NetworkPlayerCombat"/> uses this, so a sword attacker is not
    /// rejected by the shorter punch cooldown and a punching one is not given the sword's longer licence.
    /// </summary>
    public float ActiveMeleeCooldownForServer => ActiveMeleeCooldown;

    /// <summary>Swing whoosh — the sword clip when one is assigned, otherwise the shared melee swoosh.</summary>
    AudioClip ActiveMeleeSwooshClip => IsSwordSelected && swordSwooshClip != null ? swordSwooshClip : meleeSwooshClip;

    /// <summary>
    /// Impact sound for a sword hit. Falls back to the punch clips when no sword-specific clip is assigned,
    /// so the weapon is audible before any new audio is authored.
    /// </summary>
    public void PlaySwordHitSfx()
    {
        TriggerMeleeCameraKick(meleeKickSkeletonScale);   // a blade lands heavier than a fist

        if (footstepAudioSource == null)
            return;

        AudioClip clip = swordHitClip != null ? swordHitClip : PickRandomMeleeHitClip();
        if (clip == null)
            return;

        footstepAudioSource.PlayOneShot(clip, Mathf.Max(0f, meleeHitPunchVolume));
    }
}
