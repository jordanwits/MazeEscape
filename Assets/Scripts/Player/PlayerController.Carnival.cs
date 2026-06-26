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
    Text _ticketCounterText;
    int _lastDisplayedTicketCount = -1;

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

    void CreateTicketCounterUI()
    {
        if (_ticketCounterRoot != null)
            return;

        Canvas canvas = FindAnyObjectByType<Canvas>();
        if (canvas == null)
        {
            GameObject canvasGo = new GameObject("TicketCounterCanvas");
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGo.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();
        }

        GameObject root = new GameObject("TicketCounter");
        root.transform.SetParent(canvas.transform, false);
        RectTransform rootRect = root.AddComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0f, 1f);
        rootRect.anchorMax = new Vector2(0f, 1f);
        rootRect.pivot = new Vector2(0f, 1f);
        rootRect.anchoredPosition = new Vector2(24f, -24f);
        rootRect.sizeDelta = new Vector2(180f, 48f);

        Image bg = root.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.55f);
        bg.raycastTarget = false;

        if (ticketCounterIcon != null)
        {
            GameObject iconGo = new GameObject("Icon");
            iconGo.transform.SetParent(root.transform, false);
            Image icon = iconGo.AddComponent<Image>();
            icon.sprite = ticketCounterIcon;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            RectTransform iconRect = icon.rectTransform;
            iconRect.anchorMin = new Vector2(0f, 0f);
            iconRect.anchorMax = new Vector2(0f, 1f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(8f, 0f);
            iconRect.sizeDelta = new Vector2(36f, -8f);
        }

        GameObject textGo = new GameObject("Count");
        textGo.transform.SetParent(root.transform, false);
        Text count = textGo.AddComponent<Text>();
        count.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        count.fontSize = 28;
        count.color = new Color(1f, 0.93f, 0.6f, 0.98f);
        count.raycastTarget = false;
        count.alignment = TextAnchor.MiddleLeft;
        count.text = "0";
        RectTransform textRect = count.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.pivot = new Vector2(0f, 0.5f);
        float leftPad = ticketCounterIcon != null ? 52f : 14f;
        textRect.offsetMin = new Vector2(leftPad, 2f);
        textRect.offsetMax = new Vector2(-8f, -2f);

        _ticketCounterRoot = root;
        _ticketCounterText = count;
        _lastDisplayedTicketCount = -1;
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
    }
}
