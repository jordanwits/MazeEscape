using UnityEngine;

/// <summary>Simple glowstick / pickup: held at the hand point, optional point light in world; light stays on while held in hand, off while stashed in inventory.
/// </summary>
public class GlowstickItem : StackableInventoryItem
{
    public const int MaxStack = 5;

    [SerializeField] Light[] _pointLights;
    [SerializeField] bool _onWhenSelectedInHand = true;

    bool _localLightWanted = true;

    public override int MaxStackSize => MaxStack;

    protected override void Awake()
    {
        _itemTypeId = TypeIdGlowstick;
        if (_pointLights == null || _pointLights.Length == 0)
            _pointLights = GetComponentsInChildren<Light>(true);
        _localLightWanted = AnyLightEnabled();
        base.Awake();
    }

    bool AnyLightEnabled()
    {
        if (_pointLights == null)
            return false;
        foreach (Light l in _pointLights)
        {
            if (l != null && l.enabled)
                return true;
        }

        return false;
    }

    public void SetEmissiveInHand(bool inHand, bool useLogicalLightWhenInHand)
    {
        if (_pointLights == null)
            return;

        bool enable = inHand && _onWhenSelectedInHand && useLogicalLightWhenInHand;
        SetPointLightsEnabled(enable);
    }

    /// <summary>When the glowstick is dropped in the world, turn its lights on.</summary>
    public void SetWorldDroppedVisual()
    {
        if (_pointLights == null)
            return;
        SetPointLightsEnabled(true);
    }

    void SetPointLightsEnabled(bool enabled)
    {
        if (_pointLights == null)
            return;
        foreach (Light l in _pointLights)
        {
            if (l != null)
                l.enabled = enabled;
        }
    }
}
