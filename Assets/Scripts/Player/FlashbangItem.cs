using UnityEngine;

/// <summary>
/// Flashbang grenade: a hotbar pickup whose whole purpose is to leave the hand. Attack winds up a throw
/// with the heavy throwable's press-charge-release gesture — but thrown flat, along the aim, rather than
/// lobbed — and one unit is consumed from the stack as a live <see cref="FlashbangGrenade"/> takes its
/// place, fusing for three seconds before blinding every player and enemy around the burst.
///
/// Only the throw itself is described here (what to spawn and how hard). The fuse, blast radius and blind
/// duration all live on the grenade prefab, so the thrown thing is self-describing and there is exactly one
/// place to tune it. The server adjudicates the throw in <see cref="NetworkPlayerInventory"/>.
/// </summary>
public class FlashbangItem : StackableInventoryItem
{
    /// <summary>Grenades per hotbar slot.</summary>
    public const int MaxStack = 3;

    [Header("Throw")]
    [Tooltip("Live grenade spawned when this is thrown (a registered network prefab carrying FlashbangGrenade).")]
    [SerializeField] GameObject grenadePrefab;
    [Tooltip("Release speed at zero charge — a tap drops it barely past your feet.")]
    [SerializeField, Min(0.5f)] float minThrowSpeed = 6f;
    [Tooltip("Release speed at full charge.")]
    [SerializeField, Min(0.5f)] float maxThrowSpeed = 16f;
    [Tooltip("Loft added on top of where the player is looking. Small on purpose — a grenade is thrown roughly along the aim, not lobbed.")]
    [SerializeField, Range(0f, 30f)] float launchLoftDegrees = 6f;
    [Tooltip("Lowest launch angle (degrees). Negative lets a steep look-down throw go downward.")]
    [SerializeField, Range(-60f, 30f)] float minLaunchAngleDegrees = -25f;
    [Tooltip("Highest launch angle (degrees) — caps how high a full look-up lob can arc.")]
    [SerializeField, Range(5f, 85f)] float maxLaunchAngleDegrees = 55f;
    [Tooltip("How far in front of the camera the grenade appears, clear of the thrower's own collider.")]
    [SerializeField, Min(0.1f)] float throwSpawnForwardOffset = 0.42f;

    /// <summary>Seconds before another flashbang can be thrown — stops one press consuming two grenades.</summary>
    public const float ThrowCooldownSeconds = 0.6f;

    public static Sprite SharedHudSlotIcon { get; private set; }

    public override int MaxStackSize => MaxStack;

    public GameObject GrenadePrefab => grenadePrefab;
    public float ThrowSpawnForwardOffset => throwSpawnForwardOffset;

    /// <summary>
    /// Release velocity, shared by the owner-predicted, offline and server throw paths so all three agree.
    /// The wind-up is the heavy throwable's (<paramref name="charge01"/> scales launch SPEED only), but the
    /// arc deliberately is not: a heavy carnival ball is lobbed at a booth, whereas a grenade is thrown
    /// essentially along the aim. So the launch angle is just the look pitch plus a few degrees of loft,
    /// clamped — aim at the horizon and it flies flat and far instead of arcing into the ceiling; look up to
    /// lob it over something.
    /// </summary>
    public Vector3 ThrowVelocity(Vector3 aimDirection, float charge01)
    {
        Vector3 f = aimDirection.sqrMagnitude > 0.0001f ? aimDirection.normalized : Vector3.forward;
        Vector3 flat = Vector3.ProjectOnPlane(f, Vector3.up);
        if (flat.sqrMagnitude < 0.0001f)
            flat = Vector3.forward;
        flat.Normalize();

        // f is unit length, so f.y is sin(lookPitch).
        float lookPitchDeg = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
        float launchAngleDeg = Mathf.Clamp(
            lookPitchDeg + launchLoftDegrees,
            minLaunchAngleDegrees,
            Mathf.Max(minLaunchAngleDegrees, maxLaunchAngleDegrees));

        // Re-clamped here rather than at the call site: the server runs this too, so a forged client charge
        // value can never exceed max range.
        float speed = Mathf.Lerp(minThrowSpeed, Mathf.Max(minThrowSpeed, maxThrowSpeed), Mathf.Clamp01(charge01));
        float angleRad = launchAngleDeg * Mathf.Deg2Rad;
        return flat * (speed * Mathf.Cos(angleRad)) + Vector3.up * (speed * Mathf.Sin(angleRad));
    }

    protected override void Awake()
    {
        _itemTypeId = TypeIdFlashbang;
        base.Awake();

        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
