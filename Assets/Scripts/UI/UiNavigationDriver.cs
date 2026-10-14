using UnityEngine;

/// <summary>
/// Per-frame hook for <see cref="UiEventSystem"/>, living on the shared EventSystem object. Update runs early so
/// the back press (pad B / Esc) is spent before <see cref="PauseMenuController"/> and the overlays read the same
/// keys; LateUpdate runs after the EventSystem has moved the selection, so the screen's selection and navigation
/// are settled before any widget reads them.
/// </summary>
[DefaultExecutionOrder(-200)]
[DisallowMultipleComponent]
public sealed class UiNavigationDriver : MonoBehaviour
{
    void Update()
    {
        UiEventSystem.EarlyTick();
    }

    void LateUpdate()
    {
        UiEventSystem.LateTick();
    }
}
