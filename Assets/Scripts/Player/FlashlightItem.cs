using System.Collections.Generic;
using UnityEngine;

public class FlashlightItem : GrabbableInventoryItem
{
    [SerializeField] Light flashlightLight;
    [Tooltip("Lens / glow meshes that should match the spotlight on/off (Renderer.enabled).")]
    [SerializeField] Renderer[] lensGlowRenderers;
    [Tooltip("If enabled, the flashlight rotates so its Light points the same way as the hold point.")]
    [SerializeField] bool alignHeldRotationToLight = true;

    Light[] _lights;
    public bool IsLightOn => _isLightOn;
    bool _isLightOn;

    [Header("Low battery flicker")]
    [Tooltip("Below this battery fraction the beam starts stuttering and dimming (a dying-light warning) instead of holding steady until the hard cut at empty. 0 disables.")]
    [SerializeField, Range(0f, 0.5f)] float lowBatteryFlickerFraction = 0.15f;
    [Tooltip("How dim the beam can sink at the deepest flicker, as a fraction of full intensity.")]
    [SerializeField, Range(0f, 1f)] float flickerMinIntensityScale = 0.08f;

    float[] _baseLightIntensities;
    bool _baseIntensitiesCaptured;
    bool _flickerActive;
    float _flickerSeed;

    /// <summary>The flashlight mesh aims along the view (camera pitch) so it tilts up/down like the beam.</summary>
    public override bool HeldAimsAlongView => true;

    [Tooltip("Total runtime in seconds with the light on before it goes dead. New pickups start full. No recharging in this build.")]
    [SerializeField] float maxBatterySeconds = 180f;
    [SerializeField, Min(0.001f)] float minBatteryToOperate = 0.001f;
    /// <summary>Current seconds remaining; drains only while the light is on. Authoritative on server in multiplayer; local copy otherwise.</summary>
    float _batterySeconds;

    public bool HasUsableBattery => _batterySeconds > minBatteryToOperate;
    /// <summary>0 = dead, 1 = full. Used for HUD and network sync.</summary>
    public float BatteryFractionNormalized
        => maxBatterySeconds <= 0f ? 0f : Mathf.Clamp01(_batterySeconds / maxBatterySeconds);

    public static IEnumerable<FlashlightItem> GetRegisteredFlashlights()
    {
        foreach (GrabbableInventoryItem g in GetRegisteredItems())
        {
            if (g is FlashlightItem f)
                yield return f;
        }
    }

    public static bool TryGetRegisteredFlashlight(ulong itemId, out FlashlightItem flashlight)
    {
        if (TryGetRegistered(itemId, out GrabbableInventoryItem g) && g is FlashlightItem f)
        {
            flashlight = f;
            return true;
        }

        flashlight = null;
        return false;
    }

    public static bool TryResolveRegisteredFlashlightForPickup(ulong itemId, Vector3 hintPosition, out FlashlightItem flashlight)
    {
        if (!TryResolveForPickup(itemId, hintPosition, out GrabbableInventoryItem g))
        {
            flashlight = null;
            return false;
        }

        FlashlightItem f = g as FlashlightItem;
        if (f == null)
        {
            flashlight = null;
            return false;
        }

        flashlight = f;
        return true;
    }

    public static bool TryResolveRegisteredFlashlightForState(ulong itemId, Vector3 hintPosition, out FlashlightItem flashlight)
    {
        if (!TryResolveForState(itemId, hintPosition, out GrabbableInventoryItem g))
        {
            flashlight = null;
            return false;
        }

        FlashlightItem f = g as FlashlightItem;
        if (f == null)
        {
            flashlight = null;
            return false;
        }

        flashlight = f;
        return true;
    }

    protected override void Awake()
    {
        _itemTypeId = TypeIdFlashlight;

        CacheLights();
        ResolveLensGlowRenderers();
        _flickerSeed = Random.value * 97f; // decorrelate each flashlight's flicker
        base.Awake();

        _batterySeconds = maxBatterySeconds > 0f ? maxBatterySeconds : 0f;
        _isLightOn = AreAnyLightsEnabled();
        if (!HasUsableBattery)
        {
            _isLightOn = false;
            SetLightEnabled(false);
        }
        else
            SetLensGlowEnabled(_isLightOn);
    }

    protected override void FinalizeCachedHoldRotation()
    {
        if (!alignHeldRotationToLight || flashlightLight == null)
            return;

        Quaternion lightRotationRelativeToRoot = Quaternion.Inverse(transform.rotation) * flashlightLight.transform.rotation;
        _heldLocalRotation = Quaternion.Inverse(lightRotationRelativeToRoot);
    }

    /// <summary>
    /// While hand-socket held, the mesh follows the animated hand — so the BEAM alone tracks the view here:
    /// the spot light's world rotation is snapped to the camera-pitch source (owner input locally, the
    /// replicated pitch on remote avatars). Called by HeldItemHandSocketFollow after the mesh is placed.
    /// </summary>
    public void AimHeldLightAlongPitch()
    {
        if (flashlightLight == null || _heldRotationSource == null || !IsHeld || IsStashed)
            return;

        flashlightLight.transform.rotation = _heldRotationSource.rotation;
    }

    public void ToggleLight()
    {
        CacheLights();

        if (_lights == null || _lights.Length == 0)
            return;

        SetLightEnabled(!AreAnyLightsEnabled());
    }

    public void SetLightEnabled(bool enabled)
    {
        if (enabled && !HasUsableBattery)
            enabled = false;

        CacheLights();

        if (_lights == null || _lights.Length == 0)
        {
            _isLightOn = enabled;
            SetLensGlowEnabled(enabled);
            return;
        }

        for (int i = 0; i < _lights.Length; i++)
        {
            Light light = _lights[i];
            if (light == null)
                continue;

            if (enabled)
            {
                ApplyPeerVisibleLightSettings(light);
                // Clear any leftover flicker dimming so a re-selected/re-picked flashlight turns on at full brightness.
                if (_baseIntensitiesCaptured && _baseLightIntensities != null && i < _baseLightIntensities.Length)
                    light.intensity = _baseLightIntensities[i];
            }

            light.enabled = enabled;
        }

        _flickerActive = false;

        _isLightOn = enabled;
        SetLensGlowEnabled(enabled);
    }

    /// <summary>
    /// Server-only: seats the battery a player carried in from the previous maze section onto the replacement
    /// flashlight spawned for them there (see <see cref="LevelCarryOverStore"/>). A section switch despawns the
    /// original — without this, walking through the elevator would silently refill the battery.
    /// </summary>
    public void ApplyCarriedBattery(float batteryNormalized)
    {
        if (maxBatterySeconds <= 0f)
            return;

        _batterySeconds = Mathf.Clamp01(batteryNormalized) * maxBatterySeconds;
        if (!HasUsableBattery)
        {
            _isLightOn = false;
            SetLightEnabled(false);
        }
    }

    /// <summary>Called each frame on the authority (host/server or offline) while the item exists.</summary>
    public void TickBattery(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;
        if (!HasUsableBattery)
        {
            if (_isLightOn)
                SetLightEnabled(false);
            return;
        }
        if (!_isLightOn)
            return;
        if (maxBatterySeconds <= 0f)
            return;
        _batterySeconds -= deltaTime;
        if (_batterySeconds < 0f)
            _batterySeconds = 0f;
        if (!HasUsableBattery)
            SetLightEnabled(false);
    }

    public void ApplyNetworkHeldState(ulong holderNetworkObjectId, bool lightEnabled)
    {
        SetLightEnabled(lightEnabled);
        base.ApplyNetworkHeldState(holderNetworkObjectId);
    }

    public void ApplyNetworkWorldState(Vector3 worldPosition, Quaternion worldRotation, bool lightEnabled, Vector3 worldImpulse = default)
    {
        SetLightEnabled(lightEnabled);
        base.ApplyNetworkWorldState(worldPosition, worldRotation, worldImpulse);
    }

    void ResolveLensGlowRenderers()
    {
        if (lensGlowRenderers != null && lensGlowRenderers.Length > 0)
        {
            for (int i = 0; i < lensGlowRenderers.Length; i++)
            {
                if (lensGlowRenderers[i] != null)
                    return;
            }
        }

        Transform sphere = transform.Find("Sphere");
        if (sphere == null)
            return;

        Renderer r = sphere.GetComponent<Renderer>();
        if (r == null)
            return;

        lensGlowRenderers = new[] { r };
    }

    void SetLensGlowEnabled(bool enabled)
    {
        if (lensGlowRenderers == null)
            return;

        for (int i = 0; i < lensGlowRenderers.Length; i++)
        {
            Renderer renderer = lensGlowRenderers[i];
            if (renderer != null)
                renderer.enabled = enabled;
        }
    }

    /// <summary>
    /// Cosmetic dying-light flicker, driven each frame by the holder (see PlayerController) with the peer-correct
    /// battery fraction — the item's own _batterySeconds only drains on the authority, so the holder feeds the
    /// synced value. Modulates the spot INTENSITY (never light.enabled) so it never fights the on/off state.
    /// </summary>
    public void TickLowBatteryFlicker(float batteryFraction)
    {
        if (_lights == null || !_baseIntensitiesCaptured || _baseLightIntensities == null)
            return;

        bool shouldFlicker = _isLightOn
            && lowBatteryFlickerFraction > 0f
            && batteryFraction < lowBatteryFlickerFraction;

        if (!shouldFlicker)
        {
            RestoreBaseIntensities();
            return;
        }

        float severity = Mathf.Clamp01(1f - batteryFraction / lowBatteryFlickerFraction); // 0 at threshold, 1 near dead
        float speed = Mathf.Lerp(7f, 22f, severity);
        float noise = Mathf.PerlinNoise(_flickerSeed, Time.time * speed); // smooth 0..1 = electrical flicker, not strobe
        float dipDepth = Mathf.Lerp(0.30f, 0.92f, severity);
        float flick = Mathf.Lerp(1f - dipDepth, 1f, noise);

        // Occasional hard brownout as it's nearly gone.
        if (severity > 0.55f)
        {
            float blip = Mathf.PerlinNoise(_flickerSeed + 17.3f, Time.time * speed * 1.6f);
            if (blip < Mathf.Lerp(0.05f, 0.16f, severity))
                flick *= 0.1f;
        }

        float dim = Mathf.Lerp(1f, 0.6f, severity);
        float scale = Mathf.Max(flickerMinIntensityScale, dim * flick);

        for (int i = 0; i < _lights.Length && i < _baseLightIntensities.Length; i++)
        {
            Light light = _lights[i];
            if (light != null)
                light.intensity = _baseLightIntensities[i] * scale;
        }
        _flickerActive = true;
    }

    void RestoreBaseIntensities()
    {
        if (!_flickerActive || _lights == null || _baseLightIntensities == null)
            return;

        for (int i = 0; i < _lights.Length && i < _baseLightIntensities.Length; i++)
        {
            Light light = _lights[i];
            if (light != null)
                light.intensity = _baseLightIntensities[i];
        }
        _flickerActive = false;
    }

    void CacheLights()
    {
        Light[] found = GetComponentsInChildren<Light>(true);
        if (found.Length == 0)
            return;

        _lights = found;

        // Capture authored intensities once (before any flicker modulation) so the flicker can scale against them.
        if (!_baseIntensitiesCaptured)
        {
            _baseLightIntensities = new float[found.Length];
            for (int i = 0; i < found.Length; i++)
                _baseLightIntensities[i] = found[i] != null ? found[i].intensity : 0f;
            _baseIntensitiesCaptured = true;
        }
        flashlightLight = null;
        for (int i = 0; i < found.Length; i++)
        {
            ApplyPeerVisibleLightSettings(found[i]);
            if (flashlightLight == null && found[i].type == LightType.Spot)
                flashlightLight = found[i];
        }

        if (flashlightLight == null)
            flashlightLight = found[0];
    }

    static void ApplyPeerVisibleLightSettings(Light light)
    {
        if (light == null)
            return;

        light.renderMode = LightRenderMode.ForcePixel;
    }

    bool AreAnyLightsEnabled()
    {
        if (_lights == null || _lights.Length == 0)
            return false;

        for (int i = 0; i < _lights.Length; i++)
        {
            Light light = _lights[i];
            if (light != null && light.enabled)
                return true;
        }

        return false;
    }

    /// <summary>While stashed in a non-selected slot, keep the lens dark so the mesh does not light the world.</summary>
    public void ApplyInventoryStashVisual(bool stashed, bool useLogicalLightStateWhenNotStashed)
    {
        if (stashed)
            SetLightEnabled(false);
        else
            SetLightEnabled(useLogicalLightStateWhenNotStashed);
    }
}
