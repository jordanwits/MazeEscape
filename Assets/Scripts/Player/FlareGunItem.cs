using System.Collections;
using UnityEngine;

/// <summary>
/// Break-action flare gun. Holds up to <see cref="MaxRounds"/> flare rounds; firing spawns a
/// <see cref="FlareProjectile"/> (server-authoritative online, local offline). One reload fills the gun,
/// drawing as many rounds as it needs from the <see cref="FlareAmmoItem"/> stacks in the hotbar (a single
/// reload animation covers the whole load). Round count lives on this instance and is authoritative on
/// the server (mirrors the flashlight battery model — replicated to the owner through the per-slot charge
/// NetworkVariable, and it survives section switches with the item via the carry-over pen).
/// The reload visual (barrel tilt open, shell into the chamber, snap shut) is scripted here and timed to
/// match the player's FlareGun_Reload animation clip.
/// </summary>
public class FlareGunItem : GrabbableInventoryItem
{
    public const int MaxRounds = 3;

    public static Sprite SharedHudSlotIcon { get; private set; }

    /// <summary>Min seconds between shots; shared by the owner-side input gate and the server validation.</summary>
    public const float FireCooldownSeconds = 0.8f;

    // Reload timeline. One reload loads the whole gun, so the off hand makes one fetch-and-insert trip per
    // round: a lead-in while the barrel breaks open, then N identical arm cycles, then a settle while it
    // snaps shut. These MUST match the frame constants in FlareReloadClipBuilder, which splices the
    // FlareGun_Reload_N player clips out of the authored single-round clip on exactly these boundaries.
    /// <summary>Barrel breaks open; the off hand is still at rest.</summary>
    public const float ReloadLeadInSeconds = 16f / 60f;
    /// <summary>One round: hand drops to the pouch, brings the round up, inserts it, returns.</summary>
    public const float ReloadCycleSeconds = 76f / 60f;
    /// <summary>Hand comes to rest and the barrel snaps shut.</summary>
    public const float ReloadTailSeconds = 10f / 60f;

    /// <summary>
    /// Total reload duration for a given round count — matches the length of the FlareGun_Reload_N clip the
    /// animator plays (1.70s / 2.97s / 4.23s). Drives both the owner input gate and the server's busy gate.
    /// </summary>
    public static float ReloadDurationForRounds(int rounds)
    {
        return ReloadLeadInSeconds
            + ReloadCycleSeconds * Mathf.Clamp(rounds, 1, MaxRounds)
            + ReloadTailSeconds;
    }

    // Gun-side visual timings. The barrel opens once and stays open for the whole reload; the shell beats
    // repeat every cycle and are given as offsets from the start of the cycle they belong to.
    const float BarrelOpenStart = 0.05f;
    const float BarrelOpenDuration = 0.35f;
    const float CycleHandShellShow = 0.25f - ReloadLeadInSeconds;
    const float CycleShellInsertStart = 0.90f - ReloadLeadInSeconds;
    const float CycleShellInsertEnd = 1.15f - ReloadLeadInSeconds;
    const float BarrelCloseLeadSeconds = 0.35f;
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

        if (_slotIcon != null)
            SharedHudSlotIcon = _slotIcon;

        // Puts the gun on the Sfx bus and opts it into wall occlusion (it is a 3D source on the held prop).
        if (gunAudioSource != null)
            GameAudioManager.RouteSfxSource(gunAudioSource);

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

    /// <summary>Rounds still missing from a full load — how many a reload should draw from carried ammo.</summary>
    public int MissingRounds => Mathf.Max(0, MaxRounds - LoadedRounds);

    /// <summary>
    /// Load up to <paramref name="count"/> rounds from consumed ammo; returns how many actually fit. One
    /// reload fills the gun, so this takes a count rather than loading a single round. Call on the authority.
    /// </summary>
    public int TryAddRounds(int count)
    {
        int added = Mathf.Min(Mathf.Max(0, count), MissingRounds);
        LoadedRounds += added;
        return added;
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
    /// Runs the gun-side reload animation: the barrel tilts open once, then for each of
    /// <paramref name="rounds"/> rounds a shell rides the holder's left hand up and slides into the chamber,
    /// and finally the barrel snaps shut. Runs identically on every peer and is timed against the same phase
    /// constants as the FlareGun_Reload_N body clip the animator plays, so the shell always reaches the
    /// breech as the hand does. <paramref name="holderAnimator"/> supplies the left hand bone for the hand
    /// shell (null just skips that part).
    /// </summary>
    public void PlayReloadVisual(Animator holderAnimator, int rounds)
    {
        if (_reloadRoutine != null)
            StopCoroutine(_reloadRoutine);
        _reloadRoutine = StartCoroutine(ReloadVisualRoutine(holderAnimator, Mathf.Clamp(rounds, 1, MaxRounds)));

        if (gunAudioSource != null && reloadClip != null)
            gunAudioSource.PlayOneShot(reloadClip, reloadVolume);
    }

    IEnumerator ReloadVisualRoutine(Animator holderAnimator, int rounds)
    {
        Transform leftHand = null;
        if (holderAnimator != null && holderAnimator.isHuman)
            leftHand = holderAnimator.GetBoneTransform(HumanBodyBones.LeftHand);

        Quaternion barrelClosed = Quaternion.identity;
        Quaternion barrelOpen = Quaternion.Euler(barrelOpenLocalEuler);
        bool handShellShown = false;
        bool chamberShellShown = false;

        float total = ReloadDurationForRounds(rounds);
        float barrelCloseStart = total - BarrelCloseLeadSeconds;

        float t = 0f;
        while (t < total)
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
                else if (t < barrelCloseStart)
                    open = 1f;
                else
                    open = Mathf.SmoothStep(1f, 0f, (t - barrelCloseStart) / BarrelCloseDuration);
                barrelPivot.localRotation = Quaternion.SlerpUnclamped(barrelClosed, barrelOpen, open);
            }

            // Which arm cycle the playhead is in, and how far into it — the shell beats repeat per cycle.
            int cycle = Mathf.Clamp(Mathf.FloorToInt((t - ReloadLeadInSeconds) / ReloadCycleSeconds), 0, rounds - 1);
            float cycleStart = ReloadLeadInSeconds + cycle * ReloadCycleSeconds;
            float insertStart = cycleStart + CycleShellInsertStart;
            float insertEnd = cycleStart + CycleShellInsertEnd;

            if (handShell != null && leftHand != null)
            {
                // Carried in the off hand from the moment it reaches for the round until it feeds the breech.
                bool wantHandShell = t >= cycleStart + CycleHandShellShow && t < insertStart;
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
                if (t >= insertStart && t <= insertEnd)
                {
                    if (!chamberShellShown)
                    {
                        chamberShell.SetActive(true);
                        chamberShellShown = true;
                    }

                    float slide = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(insertStart, insertEnd, t));
                    chamberShell.transform.localPosition = Vector3.Lerp(shellInsertStartLocalPos, shellSeatedLocalPos, slide);
                }
                else if (t > insertEnd && chamberShellShown)
                {
                    // Between cycles the round just loaded stays seated in the chamber; it only disappears
                    // into the action once the barrel has swung shut on the last one.
                    chamberShell.transform.localPosition = shellSeatedLocalPos;
                    if (t >= barrelCloseStart + BarrelCloseDuration)
                    {
                        chamberShell.SetActive(false);
                        chamberShellShown = false;
                    }
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

    /// <summary>
    /// The reload animation parents <see cref="handShell"/> onto the holder's left-hand bone, i.e. inside the
    /// avatar. A section switch parks only the GUN in the carry-over pen and destroys the avatar, so a reload
    /// still running at that moment took the shell down with it — and every later reload silently skipped the
    /// shell for the rest of the run, on every peer, because it is guarded on the now-null reference.
    /// </summary>
    protected override void OnBeforeLevelCarryOver()
    {
        if (_reloadRoutine != null)
        {
            StopCoroutine(_reloadRoutine);
            _reloadRoutine = null;
        }

        if (chamberShell != null)
            chamberShell.SetActive(false);

        ReturnHandShell();
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
