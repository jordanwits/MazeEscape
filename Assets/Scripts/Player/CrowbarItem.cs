using UnityEngine;

/// <summary>
/// Crowbar: a hotbar melee weapon, the sword's heavier sibling. Selecting it replaces the punch — Attack
/// plays the <c>CrowbarSwing</c> clip (an overhead downward strike) on the same upper-body-masked "Sword Swing"
/// animator layer the sword uses, and lands a slightly harder, slightly shorter-reaching hit than the sword.
///
/// Like <see cref="SwordItem"/>, every number that decides the hit (reach, cone, damage, timing) lives on
/// <see cref="PlayerController"/> under the "Crowbar Melee" header, because the server adjudicates the swing
/// from its own copy of the attacker and answers "which weapon?" from the replicated slot type, never from
/// this instance. This class only carries what is genuinely per-item: the held pose and the hand-following
/// rotation a swung weapon needs.
/// </summary>
public class CrowbarItem : GrabbableInventoryItem
{
    public static Sprite SharedHudSlotIcon { get; private set; }

    /// <summary>
    /// Window of the Mixamo source clip ("Standing Melee Attack Downward", 2.27s at 30fps) that becomes
    /// <c>CrowbarSwing.anim</c>, in source seconds. The source opens and closes with the bar hanging at the hip,
    /// out of the first-person view, so the built clip starts as the arm is already rising into the wind-up and
    /// ends on the recovery as the hand passes back through the front of the body — close to the held guard, so
    /// leaving the swing blends home without a sweep down to the hip and back. Read by <c>CrowbarClipBuilder</c>.
    /// </summary>
    public const float SwingSourceStartSeconds = 0.20f;

    /// <inheritdoc cref="SwingSourceStartSeconds"/>
    public const float SwingSourceEndSeconds = 1.667f;

    /// <summary>Length of the built swing clip, in clip seconds.</summary>
    public const float SwingClipSeconds = SwingSourceEndSeconds - SwingSourceStartSeconds;

    /// <summary>
    /// Where the bar connects in the BUILT clip, in clip seconds. Measured on the player rig, not guessed: the
    /// hooked end comes down from above the right shoulder and sweeps across the front of the body at chest
    /// height between 0.65s (tip 1.0m out to the right) and 0.70s (tip dead ahead, 1.0m out) — the hand itself
    /// peaks at 8 m/s a moment earlier. The hit delay is derived from this and the playback speed, so retiming
    /// the swing moves the hit with it.
    /// </summary>
    public const float SwingImpactSeconds = 0.66f;

    /// <summary>
    /// Clip time the swing whoosh starts — just as the bar tips over the top of the wind-up and starts to fall
    /// (source 0.66s). Baked as an animation event by <c>CrowbarClipBuilder</c>, so it is frame-locked to the
    /// animation on every peer and follows the playback speed for free.
    /// </summary>
    public const float SwingWhooshSeconds = 0.46f;

    /// <summary>
    /// Shares the sword's ready stance (HoldPose 7, Hold_Sword_Pose): a one-handed bar held up in front of the
    /// body, where the first-person view can see it. The Mixamo clip's own stance hangs the weapon at the hip.
    /// </summary>
    public override int HeldPoseIndex => 7;

    /// <summary>The bar follows the hand bone rigidly through the swing — see <see cref="SwordItem.HeldFollowsHandRotation"/>.</summary>
    public override bool HeldFollowsHandRotation => true;

    protected override void Awake()
    {
        _itemTypeId = TypeIdCrowbar;
        base.Awake();

        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
