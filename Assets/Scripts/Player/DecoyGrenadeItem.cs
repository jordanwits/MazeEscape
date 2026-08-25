using UnityEngine;

/// <summary>
/// Decoy grenade: the flashbang's twin, thrown with the identical press-charge-release gesture, but it
/// makes noise instead of light. Where a flashbang is a panic button that blinds whatever is already on
/// top of you, a decoy is a bait — it lands, starts squawking, and drags the Clown, the Jailor and the
/// Security Guard toward it so you can slip out the other way.
///
/// Only the throw is described here (what to spawn and how hard). The fuse, how long it keeps calling,
/// how far it can be heard and how often it re-pings all live on the grenade prefab, so the thrown thing
/// is self-describing and there is one place to tune it. The server adjudicates the throw in
/// <see cref="NetworkPlayerInventory"/>.
/// </summary>
public class DecoyGrenadeItem : StackableInventoryItem
{
    /// <summary>Decoys per hotbar slot.</summary>
    public const int MaxStack = 3;

    [Header("Throw")]
    [Tooltip("Live decoy spawned when this is thrown (a registered network prefab carrying DecoyGrenade).")]
    [SerializeField] GameObject grenadePrefab;
    [Tooltip("Release speed at zero charge — a tap drops it barely past your feet.")]
    [SerializeField, Min(0.5f)] float minThrowSpeed = 6f;
    [Tooltip("Release speed at full charge.")]
    [SerializeField, Min(0.5f)] float maxThrowSpeed = 16f;
    [Tooltip("Loft added on top of where the player is looking. Small on purpose — thrown roughly along the aim, not lobbed.")]
    [SerializeField, Range(0f, 30f)] float launchLoftDegrees = 6f;
    [Tooltip("Lowest launch angle (degrees). Negative lets a steep look-down throw go downward.")]
    [SerializeField, Range(-60f, 30f)] float minLaunchAngleDegrees = -25f;
    [Tooltip("Highest launch angle (degrees) — caps how high a full look-up lob can arc.")]
    [SerializeField, Range(5f, 85f)] float maxLaunchAngleDegrees = 55f;
    [Tooltip("How far in front of the camera the decoy appears, clear of the thrower's own collider.")]
    [SerializeField, Min(0.1f)] float throwSpawnForwardOffset = 0.42f;

    /// <summary>Seconds before another decoy can be thrown — stops one press consuming two.</summary>
    public const float ThrowCooldownSeconds = 0.6f;

    public static Sprite SharedHudSlotIcon { get; private set; }

    public override int MaxStackSize => MaxStack;

    public GameObject GrenadePrefab => grenadePrefab;
    public float ThrowSpawnForwardOffset => throwSpawnForwardOffset;

    /// <summary>
    /// Release velocity, shared by the owner-predicted, offline and server throw paths so all three agree.
    /// Deliberately identical to <see cref="FlashbangItem.ThrowVelocity"/>: the two grenades should feel the
    /// same in the hand, so the muscle memory for one carries to the other. See the flashbang for why the
    /// arc is look-pitch-plus-loft rather than the heavy throwable's two-angle lob.
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
        _itemTypeId = TypeIdDecoyGrenade;
        base.Awake();

        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;
    }
}
