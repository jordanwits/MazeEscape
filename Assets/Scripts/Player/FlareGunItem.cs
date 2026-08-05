using System.Collections;
using UnityEngine;

/// <summary>
/// Break-action flare gun. Holds up to <see cref="MaxRounds"/> flare rounds; firing spawns a
/// <see cref="FlareProjectile"/> (server-authoritative online, local offline) and reloading consumes a
/// <see cref="FlareAmmoItem"/> from the hotbar. Round count lives on this instance and is authoritative on
/// the server (mirrors the flashlight battery model — replicated to the owner through the per-slot charge
/// NetworkVariable, and it survives section switches with the item via the carry-over pen).
/// The reload visual (barrel tilt open, shell into the chamber, snap shut) is scripted here and timed to
/// match the player's FlareGun_Reload animation clip.
/// </summary>
public class FlareGunItem : GrabbableInventoryItem
{
    public const int MaxRounds = 3;
    /// <summary>Min seconds between shots; shared by the owner-side input gate and the server validation.</summary>
    public const float FireCooldownSeconds = 0.8f;
    /// <summary>Total reload duration. Matches the FlareGun_Reload player clip (51 frames @ 30fps).</summary>
    public const float ReloadDurationSeconds = 1.7f;

    // Reload visual timeline (seconds, matching the player clip's key moments).
    const float BarrelOpenStart = 0.05f;
    const float BarrelOpenDuration = 0.35f;
    const float HandShellShowTime = 0.25f;
    const float ShellInsertStart = 0.90f;
    const float ShellInsertEnd = 1.15f;
    const float BarrelCloseStart = 1.35f;
    const float BarrelCloseDuration = 0.30f;

    [Header("Flare Gun")]
    [Tooltip("Projectile prefab spawned when firing (must be a registered network prefab for online play).")]
    [SerializeField] GameObject projectilePrefab;
    [Tooltip("Muzzle tip: projectile spawn reference and muzzle flash location.")]
    [SerializeField] Transform muzzle;

    [Header("Reload Visual")]
    [Tooltip("Pivot at the barrel hinge. Rotated to barrelOpenLocalEuler while the action is broken open.")]
    [SerializeField] Transform barrelPivot;
    [Tooltip("Local euler of the barrel pivot when fully broken open.")]
    [SerializeField] Vector3 barrelOpenLocalEuler = new Vector3(0f, 0f, -32f);
    [Tooltip("Shell mesh inside the chamber that slides in during the reload.")]
    [SerializeField] GameObject chamberShell;
    [Tooltip("Chamber shell local position at the start of the slide (just behind the breech).")]
    [SerializeField] Vector3 shellInsertStartLocalPos;
    [Tooltip("Chamber shell local position when seated in the chamber.")]
    [SerializeField] Vector3 shellSeatedLocalPos;
    [Tooltip("Shell visual parented to the reloading player's left hand while they bring the round up.")]
    [SerializeField] GameObject handShell;
    [Tooltip("Local position of the hand shell relative to the left hand bone.")]
    [SerializeField] Vector3 handShellLocalPos = new Vector3(0.04f, -0.03f, 0.02f);
    [SerializeField] Vector3 handShellLocalEuler = new Vector3(0f, 90f, 0f);

    [Header("Fire Effects")]
    [SerializeField] ParticleSystem muzzleFlash;
    [SerializeField] Light muzzleLight;
    [SerializeField] AudioSource gunAudioSource;
    [SerializeField] AudioClip fireClip;
    [SerializeField, Range(0f, 1f)] float fireVolume = 0.9f;
    [SerializeField] AudioClip reloadClip;
    [SerializeField, Range(0f, 1f)] float reloadVolume = 0.85f;
    [SerializeField] AudioClip dryFireClip;
    [SerializeField, Range(0f, 1f)] float dryFireVolume = 0.7f;

    /// <summary>Rounds in the gun. Authoritative on the server online; local truth offline.</summary>
    public int LoadedRounds { get; private set; } = MaxRounds;

    public GameObject ProjectilePrefab => projectilePrefab;
    public Transform Muzzle => muzzle;
    public bool IsReloadVisualActive => _reloadRoutine != null;

    Coroutine _reloadRoutine;
    Coroutine _muzzleLightRoutine;
    Transform _handShellHome;

    /// <summary>Pistol aim pose on the Item Hold layer (HoldPose 6, Hold_FlareGun state).</summary>
    public override int HeldPoseIndex => 6;

    /// <summary>Barrel tracks the camera pitch like the flashlight beam.</summary>
    public override bool HeldAimsAlongView => true;

    protected override void Awake()
    {
        _itemTypeId = TypeIdFlareGun;
        base.Awake();

        if (muzzleLight != null)
            muzzleLight.enabled = false;
        if (chamberShell != null)
            chamberShell.SetActive(false);
        if (handShell != null)
        {
            _handShellHome = handShell.transform.parent;
            handShell.SetActive(false);
        }
    }

    /// <summary>Consume one round for a shot. Call on the authority (server, or anyone offline).</summary>
    public bool TryConsumeRound()
    {
        if (LoadedRounds <= 0)
            return false;

        LoadedRounds--;
        return true;
    }

    /// <summary>Load one round from a consumed ammo item. Call on the authority.</summary>
    public bool TryAddRound()
    {
        if (LoadedRounds >= MaxRounds)
            return false;

        LoadedRounds++;
        return true;
    }

    public void PlayFireEffects()
    {
        if (muzzleFlash != null)
            muzzleFlash.Play();

        if (muzzleLight != null)
        {
            if (_muzzleLightRoutine != null)
                StopCoroutine(_muzzleLightRoutine);
            _muzzleLightRoutine = StartCoroutine(MuzzleLightFlash());
        }

        if (gunAudioSource != null && fireClip != null)
            gunAudioSource.PlayOneShot(fireClip, fireVolume);
    }

    public void PlayDryFireSfx()
    {
        if (gunAudioSource != null && dryFireClip != null)
            gunAudioSource.PlayOneShot(dryFireClip, dryFireVolume);
    }

    IEnumerator MuzzleLightFlash()
    {
        muzzleLight.enabled = true;
        float baseIntensity = muzzleLight.intensity;
        float t = 0f;
        const float duration = 0.12f;
        while (t < duration)
        {
            t += Time.deltaTime;
            muzzleLight.intensity = baseIntensity * Mathf.Clamp01(1f - t / duration);
            yield return null;
        }

        muzzleLight.intensity = baseIntensity;
        muzzleLight.enabled = false;
        _muzzleLightRoutine = null;
    }

    /// <summary>
    /// Runs the gun-side reload animation (barrel tilts open, a shell rides the holder's left hand in and
    /// slides into the chamber, barrel snaps shut). Runs identically on every peer; the holder's body
    /// animation comes from the replicated FlareGun_Reload clip. <paramref name="holderAnimator"/> supplies
    /// the left hand bone for the hand shell (null just skips that part).
    /// </summary>
    public void PlayReloadVisual(Animator holderAnimator)
    {
        if (_reloadRoutine != null)
            StopCoroutine(_reloadRoutine);
        _reloadRoutine = StartCoroutine(ReloadVisualRoutine(holderAnimator));

        if (gunAudioSource != null && reloadClip != null)
            gunAudioSource.PlayOneShot(reloadClip, reloadVolume);
    }

    IEnumerator ReloadVisualRoutine(Animator holderAnimator)
    {
        Transform leftHand = null;
        if (holderAnimator != null && holderAnimator.isHuman)
            leftHand = holderAnimator.GetBoneTransform(HumanBodyBones.LeftHand);

        Quaternion barrelClosed = Quaternion.identity;
        Quaternion barrelOpen = Quaternion.Euler(barrelOpenLocalEuler);
        bool handShellShown = false;
        bool chamberShellShown = false;

        float t = 0f;
        while (t < ReloadDurationSeconds)
        {
            // If the gun leaves the hand mid-reload (drop, death, level switch) end the visual cleanly.
            if (!IsHeld)
                break;

            t += Time.deltaTime;

            if (barrelPivot != null)
            {
                float open;
                if (t < BarrelOpenStart)
                    open = 0f;
                else if (t < BarrelOpenStart + BarrelOpenDuration)
                    open = Mathf.SmoothStep(0f, 1f, (t - BarrelOpenStart) / BarrelOpenDuration);
                else if (t < BarrelCloseStart)
                    open = 1f;
                else
                    open = Mathf.SmoothStep(1f, 0f, (t - BarrelCloseStart) / BarrelCloseDuration);
                barrelPivot.localRotation = Quaternion.SlerpUnclamped(barrelClosed, barrelOpen, open);
            }

            if (handShell != null && leftHand != null)
            {
                bool wantHandShell = t >= HandShellShowTime && t < ShellInsertStart;
                if (wantHandShell && !handShellShown)
                {
                    handShell.transform.SetParent(leftHand, false);
                    handShell.transform.localPosition = handShellLocalPos;
                    handShell.transform.localRotation = Quaternion.Euler(handShellLocalEuler);
                    handShell.SetActive(true);
                    handShellShown = true;
                }
                else if (!wantHandShell && handShellShown)
                {
                    ReturnHandShell();
                    handShellShown = false;
                }
            }

            if (chamberShell != null)
            {
                if (t >= ShellInsertStart && t <= ShellInsertEnd)
                {
                    if (!chamberShellShown)
                    {
                        chamberShell.SetActive(true);
                        chamberShellShown = true;
                    }

                    float slide = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ShellInsertStart, ShellInsertEnd, t));
                    chamberShell.transform.localPosition = Vector3.Lerp(shellInsertStartLocalPos, shellSeatedLocalPos, slide);
                }
                else if (t > ShellInsertEnd && chamberShellShown && t >= BarrelCloseStart + BarrelCloseDuration)
                {
                    // Seated round disappears into the closed action.
                    chamberShell.SetActive(false);
                    chamberShellShown = false;
                }
            }

            yield return null;
        }

        if (barrelPivot != null)
            barrelPivot.localRotation = barrelClosed;
        if (chamberShell != null)
            chamberShell.SetActive(false);
        ReturnHandShell();
        _reloadRoutine = null;
    }

    void ReturnHandShell()
    {
        if (handShell == null)
            return;

        handShell.SetActive(false);
        if (_handShellHome != null)
            handShell.transform.SetParent(_handShellHome, false);
    }
}
