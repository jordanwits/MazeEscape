using UnityEngine;

/// <summary>Simple glowstick / pickup: held at the hand point, optional point light in world; light stays on while held in hand, off while stashed in inventory.
/// </summary>
public class GlowstickItem : GrabbableInventoryItem
{
    public const int MaxStack = 5;

    [Tooltip("How many stick units this pickup represents (chests typically spawn 5).")]
    [SerializeField] int _stackCount = 1;
    [SerializeField] Light[] _pointLights;
    [SerializeField] bool _onWhenSelectedInHand = true;

    bool _localLightWanted = true;

    public int StackCount => _stackCount;

    /// <summary>Clamps to <see cref="MaxStack"/>; inventory also tracks stack on the server.</summary>
    public void SetStackCount(int count)
    {
        _stackCount = Mathf.Clamp(count, 1, MaxStack);
    }

    public int AddToStackClamped(int delta)
    {
        int next = Mathf.Clamp(_stackCount + delta, 1, MaxStack);
        int applied = next - _stackCount;
        _stackCount = next;
        return applied;
    }

    protected override void Awake()
    {
        _itemTypeId = TypeIdGlowstick;
        if (_pointLights == null || _pointLights.Length == 0)
            _pointLights = GetComponentsInChildren<Light>(true);
        _localLightWanted = AnyLightEnabled();
        _stackCount = Mathf.Clamp(_stackCount, 1, MaxStack);
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
