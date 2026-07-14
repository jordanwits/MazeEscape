using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Carnival-minigame interactions: detecting a <see cref="CarnivalGameStartButton"/> or
/// <see cref="CarnivalTicketBundle"/> in the player's aim, the corresponding prompt + E handler,
/// and a screen-space tickets counter driven by <see cref="NetworkPlayerCarnivalTickets"/>.
/// </summary>
public partial class PlayerController
{
    [Header("Carnival prompts")]
    [Tooltip("{0} is replaced with the bundle's ticket value.")]
    [SerializeField] string ticketBundlePromptFormat = "Press E to collect {0} tickets";

    [Header("Carnival HUD")]
    [Tooltip("Optional sprite for the ticket counter icon. If empty, the counter uses a text-only label.")]
    [SerializeField] Sprite ticketCounterIcon;

    NetworkPlayerCarnivalTickets _networkPlayerCarnivalTickets;
    GameObject _ticketCounterRoot;
    TMP_Text _ticketCounterText;
    int _lastDisplayedTicketCount = -1;

    // The ticket counter is only shown while the local player stands inside the Carnival Main room (where
    // tickets are earned/spent). Both conditions must hold: the HUD is visible AND we are in the room.
    bool _ticketCounterHudAllowed;
    bool _inCarnivalRoom;

    void HookupCarnivalTickets()
    {
        _networkPlayerCarnivalTickets = GetComponent<NetworkPlayerCarnivalTickets>();
        if (_networkPlayerCarnivalTickets != null)
            _networkPlayerCarnivalTickets.Changed += HandleTicketCountChanged;
        RefreshTicketCounterText(_networkPlayerCarnivalTickets != null ? _networkPlayerCarnivalTickets.TicketCount : 0);
    }

    void UnhookCarnivalTickets()
    {
        if (_networkPlayerCarnivalTickets != null)
            _networkPlayerCarnivalTickets.Changed -= HandleTicketCountChanged;
    }

    void HandleTicketCountChanged(int previous, int current)
    {
        RefreshTicketCounterText(current);
    }

    void RefreshTicketCounterText(int count)
    {
        if (_ticketCounterText == null)
            return;
        if (count == _lastDisplayedTicketCount)
            return;
        _lastDisplayedTicketCount = count;
        _ticketCounterText.text = count.ToString();
    }

    /// <summary>Poll (local owner only) whether we are inside the Carnival Main room and toggle the counter.</summary>
    void TickCarnivalRoomPresence()
    {
        if (!_hasLocalControl)
            return;

        bool inside = CarnivalMainRoomZone.IsPointInsideAny(transform.position);
        if (inside == _inCarnivalRoom)
            return;

        _inCarnivalRoom = inside;
        RefreshTicketCounterVisibility();
    }

    /// <summary>Called by <see cref="SetHudVisible"/> so the counter also respects global HUD visibility.</summary>
    void SetTicketCounterHudAllowed(bool allowed)
    {
        _ticketCounterHudAllowed = allowed;
        RefreshTicketCounterVisibility();
    }

    void RefreshTicketCounterVisibility()
    {
        if (_ticketCounterRoot != null)
            _ticketCounterRoot.SetActive(_ticketCounterHudAllowed && _inCarnivalRoom);
    }

    void CreateTicketCounterUI()
    {
        if (_ticketCounterRoot != null)
            return;

        Canvas canvas = HudKit.EnsureHudCanvas();

        GameObject root = new GameObject("TicketCounter");
        root.layer = 5;
        root.transform.SetParent(canvas.transform, false);
        RectTransform rootRect = root.AddComponent<RectTransform>();
        // Top-right corner; the top-left is the vitals cluster (PlayerVitalsHud).
        rootRect.anchorMin = new Vector2(1f, 1f);
        rootRect.anchorMax = new Vector2(1f, 1f);
        rootRect.pivot = new Vector2(1f, 1f);
        rootRect.anchoredPosition = new Vector2(-24f, -24f);
        rootRect.sizeDelta = new Vector2(150f, 44f);
        rootRect.localRotation = Quaternion.Euler(0f, 0f, -0.4f);

        HudKit.AddPlate(root, 0.72f, 0.20f);

        float leftPad;
        if (ticketCounterIcon != null)
        {
            GameObject iconGo = new GameObject("Icon");
            iconGo.layer = 5;
            iconGo.transform.SetParent(root.transform, false);
            Image icon = iconGo.AddComponent<Image>();
            icon.sprite = ticketCounterIcon;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            RectTransform iconRect = icon.rectTransform;
            iconRect.anchorMin = new Vector2(0f, 0f);
            iconRect.anchorMax = new Vector2(0f, 1f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(10f, 0f);
            iconRect.sizeDelta = new Vector2(30f, -12f);
            leftPad = 48f;
        }
        else
        {
            // mustard diamond stands in for a ticket icon
            GameObject chipGo = new GameObject("Chip");
            chipGo.layer = 5;
            chipGo.transform.SetParent(root.transform, false);
            Image chip = chipGo.AddComponent<Image>();
            chip.sprite = MenuTheme.Solid();
            chip.color = MenuTheme.WithAlpha(MenuTheme.Amber, 0.92f);
            chip.raycastTarget = false;
            RectTransform chipRect = chip.rectTransform;
            chipRect.anchorMin = new Vector2(0f, 0.5f);
            chipRect.anchorMax = new Vector2(0f, 0.5f);
            chipRect.anchoredPosition = new Vector2(18f, 0f);
            chipRect.sizeDelta = new Vector2(9f, 9f);
            chipRect.localRotation = Quaternion.Euler(0f, 0f, 45f);
            leftPad = 34f;
        }

        GameObject textGo = new GameObject("Count");
        textGo.layer = 5;
        textGo.transform.SetParent(root.transform, false);
        TextMeshProUGUI count = textGo.AddComponent<TextMeshProUGUI>();
        count.font = MenuTheme.DisplayFont;
        count.fontSize = 23f;
        count.color = MenuTheme.WithAlpha(MenuTheme.Bone, 0.96f);
        count.characterSpacing = 2f;
        count.raycastTarget = false;
        count.alignment = TextAlignmentOptions.MidlineLeft;
        count.text = "0";
        RectTransform textRect = count.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.pivot = new Vector2(0f, 0.5f);
        textRect.offsetMin = new Vector2(leftPad, 2f);
        textRect.offsetMax = new Vector2(-8f, -2f);

        _ticketCounterRoot = root;
        _ticketCounterText = count;
        _lastDisplayedTicketCount = -1;

        // Hidden until the local player walks into the Carnival Main room.
        RefreshTicketCounterVisibility();
    }

    bool TryFindInteractableCarnivalStartButton(Transform cam, out CarnivalGameStartButton button)
    {
        button = null;
        if (cam == null)
            return false;

        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        int count = TryInteractCastNonAlloc(cam, mask);
        if (count <= 0)
            return false;

        SortInteractHitsByDistance(count);
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _interactCastHitBuffer[i];
            CarnivalGameStartButton found = h.collider.GetComponentInParent<CarnivalGameStartButton>();
            if (found != null)
            {
                button = found;
                return true;
            }
        }
        return false;
    }

    bool TryFindInteractableBlackjackSeat(Transform cam, out BlackjackSeat seat)
    {
        seat = null;
        if (cam == null)
            return false;

        // Seat colliders are TRIGGERS (so they don't physically block the player walking up to the table),
        // so this cast must include triggers — unlike the start-button / ticket-bundle casts.
        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        int count = TryInteractCastNonAlloc(cam, mask, QueryTriggerInteraction.Collide);
        if (count <= 0)
            return false;

        SortInteractHitsByDistance(count);
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _interactCastHitBuffer[i];
            BlackjackSeat found = h.collider.GetComponentInParent<BlackjackSeat>();
            if (found != null)
            {
                seat = found;
                return true;
            }
        }
        return false;
    }

    bool TryFindInteractableRadio(Transform cam, out CarnivalRadio radio)
    {
        radio = null;
        if (cam == null)
            return false;

        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        int count = TryInteractCastNonAlloc(cam, mask);
        if (count <= 0)
            return false;

        SortInteractHitsByDistance(count);
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _interactCastHitBuffer[i];
            CarnivalRadio found = h.collider.GetComponentInParent<CarnivalRadio>();
            if (found != null)
            {
                radio = found;
                return true;
            }
        }
        return false;
    }

    bool TryFindInteractableTicketBundle(Transform cam, out CarnivalTicketBundle bundle)
    {
        bundle = null;
        if (cam == null)
            return false;

        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        int count = TryInteractCastNonAlloc(cam, mask);
        if (count <= 0)
            return false;

        SortInteractHitsByDistance(count);
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _interactCastHitBuffer[i];
            CarnivalTicketBundle found = h.collider.GetComponentInParent<CarnivalTicketBundle>();
            if (found != null)
            {
                bundle = found;
                return true;
            }
        }
        return false;
    }

    /// <summary>Returns true if the press was consumed by a carnival interactable (start button or ticket bundle).</summary>
    bool TryHandleCarnivalInteract()
    {
        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return false;

        if (TryFindInteractableTicketBundle(cam, out CarnivalTicketBundle bundle) && bundle != null)
        {
            bundle.RequestPickup(this);
            return true;
        }

        if (TryFindInteractableBlackjackSeat(cam, out BlackjackSeat seat) && seat != null)
        {
            seat.RequestSitOrLeave(this);
            BlackjackOverlayController.NotifySeatInteract(this, seat);
            return true;
        }

        if (TryFindInteractableCarnivalStartButton(cam, out CarnivalGameStartButton button)
            && button != null)
        {
            Debug.Log($"[PlayerController] E pressed while aiming at start button {button.name}, CanStart={button.CanStart}", button);
            if (button.CanStart)
            {
                button.RequestStart(this);
                return true;
            }
        }

        if (TryFindInteractableRadio(cam, out CarnivalRadio radio) && radio != null)
        {
            radio.RequestToggle();
            return true;
        }

        return false;
    }

    /// <summary>Returns the carnival prompt text and visibility for the current aim, or false if none applies.</summary>
    bool TryGetCarnivalPromptForCurrentAim(Transform cam, out string message)
    {
        message = null;
        if (cam == null)
            return false;

        if (TryFindInteractableTicketBundle(cam, out CarnivalTicketBundle bundle) && bundle != null)
        {
            message = string.Format(ticketBundlePromptFormat, bundle.Value);
            return true;
        }

        if (TryFindInteractableBlackjackSeat(cam, out BlackjackSeat seat) && seat != null
            && seat.TryGetPrompt(this, out string seatMessage))
        {
            message = seatMessage;
            return true;
        }

        if (TryFindInteractableCarnivalStartButton(cam, out CarnivalGameStartButton button) && button != null)
        {
            message = button.CanStart ? button.StartPromptMessage : button.InProgressPromptMessage;
            return true;
        }

        if (TryFindInteractableRadio(cam, out CarnivalRadio radio) && radio != null)
        {
            message = radio.InteractPromptMessage;
            return true;
        }

        return false;
    }

    // =========================================================================================
    // Blackjack seating: snap the avatar onto the stool + drive the looping Sit animation.
    // Position/rotation are written on the owning client; OwnerNetworkTransform replicates the pose and
    // OwnerNetworkAnimator replicates the "Seated" bool, so remote peers see the player sitting on the stool.
    // =========================================================================================
    const string SeatedAnimatorParameter = "Seated";
    bool _blackjackSeated;
    Vector3 _preSeatPosition;
    Quaternion _preSeatRotation;

    void TeleportPlayer(Vector3 worldPos, Quaternion worldRot)
    {
        // Briefly disable the CharacterController so its solver doesn't fight the direct position write.
        bool reenable = characterController != null && characterController.enabled;
        if (reenable)
            characterController.enabled = false;
        transform.SetPositionAndRotation(worldPos, worldRot);
        if (reenable)
            characterController.enabled = true;
    }

    /// <summary>Teleport the player onto the stool and start the sitting animation.</summary>
    public void EnterBlackjackSeat(Vector3 worldPos, Quaternion worldRot)
    {
        if (!_blackjackSeated)
        {
            // Remember where we stood so we can stand back up there (the seated root is raised onto the stool).
            _preSeatPosition = transform.position;
            _preSeatRotation = transform.rotation;
            _blackjackSeated = true;
        }

        _horizontalVelocity = Vector3.zero;
        CancelThrowCharge();
        TeleportPlayer(worldPos, worldRot);

        if (driveAnimator && animator != null)
            animator.SetBool(SeatedAnimatorParameter, true);

        // Seated forces HoldPose 0 so raised hold arms never override the Sit pose.
        ApplyHoldPoseAnimatorParameter();
    }

    /// <summary>Stand the player back up at their pre-sit spot (stops the sitting animation).</summary>
    public void ExitBlackjackSeat()
    {
        if (driveAnimator && animator != null)
            animator.SetBool(SeatedAnimatorParameter, false);

        if (_blackjackSeated)
        {
            TeleportPlayer(_preSeatPosition, _preSeatRotation);
            _blackjackSeated = false;
        }

        // Restore the hold pose for whatever is selected now that we're standing again.
        ApplyHoldPoseAnimatorParameter();
    }
}
