using UnityEngine;

/// <summary>
/// Crowbar melee on the player. With a <see cref="CrowbarItem"/> in the selected hotbar slot, Attack brings the
/// bar down overhead instead of throwing a punch: its own trigger and state on the sword's upper-body-masked
/// "Sword Swing" layer, a longer wind-up than the sword, slightly less reach and slightly more damage.
///
/// Everything else is shared with the sword — <see cref="SelectedMeleeWeaponTypeId"/> picks the profile, and the
/// punch's authority path (<see cref="TryMelee"/> → <see cref="NetworkPlayerCombat"/> →
/// <see cref="ApplyMeleeDamageLocally"/>) runs unchanged, so the server adjudicates a crowbar hit from the
/// replicated slot type exactly as it does a sword hit.
/// </summary>
public partial class PlayerController
{
    /// <summary>Animator trigger for the swing; a state on the "Sword Swing" layer, masked to exclude the legs.</summary>
    const string CrowbarSwingTrigger = "CrowbarSwing";

    /// <summary>Float parameter the CrowbarSwing state uses as its speed multiplier (<see cref="crowbarSwingAnimSpeed"/>).</summary>
    static readonly int CrowbarSwingSpeedHash = Animator.StringToHash("CrowbarSwingSpeed");

    /// <summary>True when the attacking player has a crowbar in the selected slot. See <see cref="SelectedMeleeWeaponTypeId"/>.</summary>
    public bool IsCrowbarSelected => SelectedMeleeWeaponTypeId == GrabbableInventoryItem.TypeIdCrowbar;

    /// <summary>Swing whoosh: the crowbar's own clip, else the sword's, else the shared melee swoosh.</summary>
    AudioClip CrowbarSwooshClip =>
        crowbarSwooshClip != null ? crowbarSwooshClip
        : swordSwooshClip != null ? swordSwooshClip
        : meleeSwooshClip;

    /// <summary>
    /// Keeps the animator's crowbar swing-speed multiplier equal to the serialized value — on every peer, for the
    /// same reason as <see cref="PushSwordSwingSpeedToAnimator"/>: a non-owner must play the swing at the same pace.
    /// </summary>
    void PushCrowbarSwingSpeedToAnimator()
    {
        if (animator == null)
            return;

        animator.SetFloat(CrowbarSwingSpeedHash, Mathf.Max(0.05f, crowbarSwingAnimSpeed));
    }

    /// <summary>
    /// Swing whoosh, fired by an animation event baked into CrowbarSwing.anim at
    /// <see cref="CrowbarItem.SwingWhooshSeconds"/>, so every peer hears it in step with its own animation.
    /// Public and named exactly as the event expects — renaming it silently kills the sound.
    /// </summary>
    public void OnCrowbarSwingWhoosh()
    {
        PlaySelfOrBodyOneShot(CrowbarSwooshClip, meleeSwooshVolume);
    }

    /// <summary>
    /// Metallic impact for a crowbar hit (<see cref="crowbarHitClip"/>, CrowbarHit.wav on the player prefabs). Falls
    /// back to the punch clips at the punch volume if the clip is ever unassigned. Kicks the view like the sword:
    /// it lands heavy.
    /// </summary>
    public void PlayCrowbarHitSfx()
    {
        TriggerMeleeCameraKick(meleeKickSkeletonScale);

        if (crowbarHitClip != null)
            PlaySelfOrBodyOneShot(crowbarHitClip, crowbarHitVolume);
        else
            PlaySelfOrBodyOneShot(PickRandomMeleeHitClip(), meleeHitPunchVolume);
    }
}
