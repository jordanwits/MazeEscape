using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Hold-to-activate for the teleport orb. While the player holds Interact (E) and aims at a usable
/// <see cref="TeleportOrb"/>, a radial ring around the crosshair fills; when it completes, the orb's use is
/// requested (server-authoritative teleport). Releasing E, looking away, or losing control resets it. The
/// ring is a hollow border circle (MenuTheme.Ring) with a faint full-circle track behind it — no disc fill.
/// </summary>
public partial class PlayerController
{
    [Header("Teleport orb hold")]
    [Tooltip("Seconds the player must hold Interact while aiming at a teleport orb before it fires.")]
    [SerializeField] float teleportHoldSeconds = 0.9f;

    GameObject _teleportHoldRingRoot;
    Image _teleportHoldFillImage;
    TeleportOrb _teleportHoldOrb;
    float _teleportHoldTimer;
    bool _teleportHoldFired;

    void CreateTeleportHoldRing(Transform parent)
    {
        Sprite ring = MenuTheme.Ring(0.16f);
        const float diameter = 34f;

        _teleportHoldRingRoot = new GameObject("TeleportHoldRing");
        _teleportHoldRingRoot.layer = 5;
        RectTransform rootRect = _teleportHoldRingRoot.AddComponent<RectTransform>();
        rootRect.SetParent(parent, false);
        rootRect.anchorMin = rootRect.anchorMax = rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.anchoredPosition = Vector2.zero;
        rootRect.sizeDelta = new Vector2(diameter, diameter);

        // Faint full-circle track so the ring being filled is always discoverable.
        Image track = MakeRingImage("Track", rootRect, ring, diameter);
        track.color = MenuTheme.WithAlpha(MenuTheme.Amber, 0.20f);

        // Bright orange border that radially fills as the hold progresses.
        _teleportHoldFillImage = MakeRingImage("Fill", rootRect, ring, diameter);
        _teleportHoldFillImage.color = MenuTheme.WithAlpha(MenuTheme.Amber, 0.95f);
        _teleportHoldFillImage.type = Image.Type.Filled;
        _teleportHoldFillImage.fillMethod = Image.FillMethod.Radial360;
        _teleportHoldFillImage.fillOrigin = (int)Image.Origin360.Top;
        _teleportHoldFillImage.fillClockwise = true;
        _teleportHoldFillImage.fillAmount = 0f;

        _teleportHoldRingRoot.SetActive(false);
    }

    static Image MakeRingImage(string name, Transform parent, Sprite ring, float diameter)
    {
        GameObject go = new GameObject(name) { layer = 5 };
        RectTransform rect = go.AddComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(diameter, diameter);
        Image img = go.AddComponent<Image>();
        img.sprite = ring;
        img.raycastTarget = false;
        return img;
    }

    /// <summary>Ticked at the top of Update (alongside the pickup reach) so cancels always process.</summary>
    void TickTeleportHold()
    {
        // Only cast for an orb while the button is actually held and the player is in normal control.
        if (!IsInteractHeld() || !CanChargeTeleportHold())
        {
            ResetTeleportHold();
            return;
        }

        Transform cam = CameraTransformForFacing;
        if (cam == null || !TryFindInteractableTeleportOrb(cam, out TeleportOrb orb) || orb == null)
        {
            ResetTeleportHold();
            return;
        }

        if (_teleportHoldOrb != orb)
        {
            if (_teleportHoldOrb != null)
                _teleportHoldOrb.EndInteractCharge();
            _teleportHoldOrb = orb;
            _teleportHoldTimer = 0f;
            _teleportHoldFired = false;
            orb.BeginInteractCharge();      // interact SFX plays only while holding
        }

        // Hold time is matched to the orb's interact SFX length (falls back to the serialized default).
        float duration = orb.ChargeDuration > 0.01f ? orb.ChargeDuration : Mathf.Max(0.05f, teleportHoldSeconds);
        _teleportHoldTimer += Time.deltaTime;
        SetTeleportRingProgress(true, _teleportHoldTimer / duration);

        if (!_teleportHoldFired && _teleportHoldTimer >= duration)
        {
            _teleportHoldFired = true;              // one activation per hold; release to charge again
            SetTeleportRingProgress(true, 1f);
            orb.TryRequestUse(cam.position, this);
        }
    }

    bool CanChargeTeleportHold()
    {
        if (!_hasLocalControl)
            return false;
        if (_playerHealth != null && _playerHealth.IsDead)
            return false;
        if (_ragdollController != null && (_ragdollController.IsRagdolled || _ragdollController.IsGettingUp || _ragdollController.IsHeld))
            return false;
        if (_networkPlayerAvatar != null && _networkPlayerAvatar.IsCarriedByJailor)
            return false;
        if (_blackjackSeated)
            return false;
        if (IsPostJailMovementLocked)
            return false;
        if (BlackjackOverlayController.IsInteractive)
            return false;
        if (SkeletonRpsOverlayController.IsInteractive)
            return false;
        if (ProceduralMazeCoordinator.ShouldBlockLocalPlayerUntilMazeReady())
            return false;
        return true;
    }

    bool IsInteractHeld()
    {
        if (_interactAction != null)
            return _interactAction.IsPressed();
        var kb = UnityEngine.InputSystem.Keyboard.current;
        return kb != null && kb.eKey.isPressed;
    }

    void ResetTeleportHold()
    {
        if (_teleportHoldTimer == 0f && _teleportHoldOrb == null && !_teleportHoldFired
            && (_teleportHoldRingRoot == null || !_teleportHoldRingRoot.activeSelf))
        {
            return; // already idle
        }

        if (_teleportHoldOrb != null)
            _teleportHoldOrb.EndInteractCharge();

        _teleportHoldTimer = 0f;
        _teleportHoldOrb = null;
        _teleportHoldFired = false;
        SetTeleportRingProgress(false, 0f);
    }

    void SetTeleportRingProgress(bool visible, float t)
    {
        if (_teleportHoldRingRoot == null)
            return;
        if (_teleportHoldRingRoot.activeSelf != visible)
            _teleportHoldRingRoot.SetActive(visible);
        if (visible && _teleportHoldFillImage != null)
            _teleportHoldFillImage.fillAmount = Mathf.Clamp01(t);
    }
}
