using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Scrolls a vertical <see cref="ScrollRect"/> so the pad/keyboard selection stays in view. Only acts while
/// <see cref="UiEventSystem.IsNavigating"/>: the mouse scrolls with the wheel and the bar, and must not be
/// fought by a selection it never makes.
/// </summary>
[DisallowMultipleComponent]
public sealed class UiScrollFollowSelection : MonoBehaviour
{
    /// <summary>Room kept between the selection and the viewport edge, so a section header above it shows too.</summary>
    const float EdgePadding = 60f;
    const float FollowSharpness = 20f;

    public ScrollRect scrollRect;
    public RectTransform viewport;
    public RectTransform content;

    void LateUpdate()
    {
        if (!UiEventSystem.IsNavigating || viewport == null || content == null)
            return;

        EventSystem eventSystem = EventSystem.current;
        GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
        if (selected == null || !selected.activeInHierarchy || !selected.transform.IsChildOf(content))
            return;
        if (selected.transform is not RectTransform target)
            return;

        Bounds bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, target);
        Rect view = viewport.rect;
        float padding = Mathf.Min(EdgePadding, view.height * 0.25f);

        // Positive: the selection pokes out above the view; negative: below it.
        float overshoot = 0f;
        if (bounds.max.y > view.yMax - padding)
            overshoot = bounds.max.y - (view.yMax - padding);
        else if (bounds.min.y < view.yMin + padding)
            overshoot = bounds.min.y - (view.yMin + padding);
        if (Mathf.Abs(overshoot) < 0.5f)
            return;

        // Content is pinned to the viewport's top edge: raising anchoredPosition.y scrolls down the list.
        float maxY = Mathf.Max(0f, content.rect.height - view.height);
        Vector2 position = content.anchoredPosition;
        float goal = Mathf.Clamp(position.y - overshoot, 0f, maxY);
        float eased = Mathf.Lerp(position.y, goal, 1f - Mathf.Exp(-FollowSharpness * Time.unscaledDeltaTime));
        position.y = Mathf.Abs(goal - eased) < 0.5f ? goal : eased;
        content.anchoredPosition = position;

        if (scrollRect != null)
            scrollRect.velocity = Vector2.zero;
    }
}
