using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Pushes a shop card's blurb into the <see cref="CarnivalStoreOverlayController"/> footer line while the
/// pointer is over that card. Separate from <see cref="MenuButtonFx"/> (which already owns the card's pointer
/// handlers for its hover visuals) because UGUI delivers pointer events to every handler on the object, so the
/// two can coexist without either one having to know about the other.
/// </summary>
[DisallowMultipleComponent]
public sealed class CarnivalStoreCardHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    CarnivalStoreOverlayController _owner;
    string _text;

    internal void Bind(CarnivalStoreOverlayController owner, string blurb, string displayName)
    {
        _owner = owner;
        _text = string.IsNullOrWhiteSpace(blurb) ? displayName : blurb;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (_owner != null)
            _owner.ShowCardBlurb(_text);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (_owner != null)
            _owner.ClearCardBlurb();
    }
}
