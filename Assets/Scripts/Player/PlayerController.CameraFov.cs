using UnityEngine;

/// <summary>
/// Owner-side camera FOV kick: a subtle zoom while sprinting and a stronger one while the energy-drink
/// boost is active, easing smoothly toward the target each frame. Only the local player's view camera is
/// enabled (see <see cref="NetworkPlayerAvatar"/>), so <see cref="TickCameraFov"/> is a no-op on remote
/// avatars and whenever an overlay (blackjack/RPS) has swapped the view camera out.
/// </summary>
public partial class PlayerController
{
    [Header("Camera Zoom (FOV kick)")]
    [Tooltip("Extra field-of-view (degrees) added while sprinting — a subtle speed zoom.")]
    [SerializeField, Range(0f, 30f)] float sprintFovBonus = 6f;
    [Tooltip("Extra field-of-view (degrees) while the energy-drink boost is active. Overrides the sprint zoom (takes the larger).")]
    [SerializeField, Range(0f, 40f)] float energyDrinkFovBonus = 12f;
    [Tooltip("How quickly the FOV eases toward its target. Higher = snappier.")]
    [SerializeField, Min(0.5f)] float fovLerpSharpness = 8f;

    Camera _viewCamera;
    float _baseFieldOfView;
    bool _hasResolvedViewCamera;

    void ResolveViewCameraForFov()
    {
        if (_hasResolvedViewCamera || cameraTransform == null)
            return;

        // NOTE: never use ?? / ?. with UnityEngine.Object — they bypass Unity's overloaded null check and
        // treat a missing component as non-null. Use explicit Unity == null checks instead.
        Camera cam = cameraTransform.GetComponent<Camera>();
        if (cam == null)
            cam = cameraTransform.GetComponentInChildren<Camera>(true);

        if (cam != null)
        {
            _viewCamera = cam;
            _baseFieldOfView = _viewCamera.fieldOfView;
            _hasResolvedViewCamera = true;
        }
    }

    /// <summary>Drives the view-camera FOV toward its target. Call once per frame from LateUpdate.</summary>
    void TickCameraFov()
    {
        ResolveViewCameraForFov();

        // The local owner's view camera is the only enabled one; skip remotes and overlay-swapped views.
        if (_viewCamera == null || !_viewCamera.enabled)
            return;

        float target = _baseFieldOfView;
        bool moving = _currentHorizontalSpeed > 0.2f;
        bool canZoom = _hasLocalControl
            && !PauseMenuController.BlocksGameplayInput
            && !BlackjackOverlayController.IsInteractive
            && !SkeletonRpsOverlayController.IsInteractive;

        if (canZoom && moving)
        {
            float bonus = _isSprinting ? sprintFovBonus : 0f;
            // Energy-drink zoom is the stronger effect and dominates while sprinting through the buff.
            if (EnergyBoostActive)
                bonus = Mathf.Max(bonus, energyDrinkFovBonus);
            target += bonus;
        }

        // Framerate-independent exponential easing toward the target FOV.
        float t = 1f - Mathf.Exp(-fovLerpSharpness * Time.deltaTime);
        _viewCamera.fieldOfView = Mathf.Lerp(_viewCamera.fieldOfView, target, t);
    }
}
