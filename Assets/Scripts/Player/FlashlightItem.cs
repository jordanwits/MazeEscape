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
        base.Awake();

        _isLightOn = AreAnyLightsEnabled();
        SetLensGlowEnabled(_isLightOn);
    }

    protected override void FinalizeCachedHoldRotation()
    {
        if (!alignHeldRotationToLight || flashlightLight == null)
            return;

        Quaternion lightRotationRelativeToRoot = Quaternion.Inverse(transform.rotation) * flashlightLight.transform.rotation;
        _heldLocalRotation = Quaternion.Inverse(lightRotationRelativeToRoot);
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
                ApplyPeerVisibleLightSettings(light);

            light.enabled = enabled;
        }

        _isLightOn = enabled;
        SetLensGlowEnabled(enabled);
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

    void CacheLights()
    {
        Light[] found = GetComponentsInChildren<Light>(true);
        if (found.Length == 0)
            return;

        _lights = found;
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
