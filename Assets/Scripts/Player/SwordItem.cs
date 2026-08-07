using UnityEngine;

/// <summary>
/// Rusty sword: a hotbar melee weapon. Selecting it replaces the punch — Attack plays the
/// <c>SwordSwing</c> clip on the upper-body-masked "Sword Swing" animator layer and lands a heavier,
/// longer-reach hit. All of the swing's timing and damage lives on <see cref="PlayerController"/>
/// (the "Sword Melee" header) rather than here, because the server adjudicates the hit from its own copy
/// of the attacking player and must not depend on resolving this item instance; see
/// <see cref="PlayerController.ApplyMeleeDamageLocally"/>.
///
/// This item carries only what is genuinely per-item: the held pose it selects, and the fact that a sword
/// has to ride the hand's rotation instead of the fixed forward aim every other socket item uses (a blade
/// locked to a world direction would stay pointing forward through the whole swing).
/// </summary>
public class SwordItem : GrabbableInventoryItem
{
    public static Sprite SharedHudSlotIcon { get; private set; }

    /// <summary>
    /// Where the blade actually connects in the AUTHORED clip, in clip seconds. Measured, not guessed: the
    /// hand peaks at 11 m/s here (0.567s reads 5.9, 0.633s reads 10.0). Everything about the swing's timing
    /// is derived from this and the playback speed, so speeding the animation up cannot desync the hit.
    /// </summary>
    public const float SwingImpactSeconds = 0.60f;

    /// <summary>Length of the authored swing clip, in clip seconds.</summary>
    public const float SwingClipSeconds = 1.5f;

    /// <summary>
    /// Clip time the swing whoosh starts, just as the blade begins to accelerate into the cut. The SFX is
    /// fired by an animation event baked at this time by <c>SwordClipBuilder</c> rather than by a timer, so it
    /// is frame-locked to the animation on every peer and follows the playback speed for free.
    /// </summary>
    public const float SwingWhooshSeconds = 0.50f;

    /// <summary>Guard stance on the Item Hold layer (HoldPose 7, Hold_Sword state) — frame 0 of the swing.</summary>
    public override int HeldPoseIndex => 7;

    /// <summary>
    /// The blade follows the hand bone rigidly. Every other socket item is locked to a fixed player-space
    /// aim (or the camera pitch), which is right for a flashlight and wrong for a weapon that is supposed to
    /// travel through an arc.
    /// </summary>
    public override bool HeldFollowsHandRotation => true;

    protected override void Awake()
    {
        _itemTypeId = TypeIdSword;
        base.Awake();

        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
