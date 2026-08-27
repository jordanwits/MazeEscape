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

    /// <summary>
    /// The prize counter is a wide booth, so the aim cast alone would offer the shop from across the room —
    /// <see cref="CarnivalStore.CanOfferShop"/> gates it on the counter's own interact radius.
    /// </summary>
    bool TryFindInteractableCarnivalStore(Transform cam, out CarnivalStore store)
    {
        store = null;
        if (cam == null)
            return false;

        // Purchases are dispensed ONTO this counter, and the booth's collider sits right behind/under them, so
        // an aimed item would always lose: the hit walk below skips the item (no CarnivalStore on it) and lands
        // on the counter regardless of which is nearer. While anything is actually pickable, the counter steps
        // aside — the same rule an opened chest uses for its own loot (see InteractHitBelongsToOpenedChest).
        if (TryFindInteractableGrabbable(out _))
            return false;

        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        int count = TryInteractCastNonAlloc(cam, mask);
        if (count <= 0)
            return false;

        SortInteractHitsByDistance(count);
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _interactCastHitBuffer[i];
            CarnivalStore found = h.collider.GetComponentInParent<CarnivalStore>();
            if (found != null && found.CanOfferShop(transform.position))
            {
                store = found;
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

        if (TryFindInteractableCarnivalStore(cam, out CarnivalStore store) && store != null)
        {
            store.RequestShopInteract(this);
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

        if (TryFindInteractableCarnivalStore(cam, out CarnivalStore store) && store != null)
        {
            message = store.InteractPromptMessage;
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
    bool _pendingBlackjackSeatExit;
    Vector3 _preSeatPosition;
    Quaternion _preSeatRotation;

    OwnerNetworkTransform _ownerNetworkTransformCache;
    OwnerNetworkTransform SelfOwnerNetworkTransform => _ownerNetworkTransformCache != null
        ? _ownerNetworkTransformCache
        : (_ownerNetworkTransformCache = GetComponent<OwnerNetworkTransform>());

    /// <summary>
    /// Moves the player and flags the jump as a teleport for observers. Returns false when the move was NOT
    /// applied because this peer is not the transform authority — a caller must not book its pose state as
    /// done in that case.
    /// </summary>
    bool TeleportPlayer(Vector3 worldPos, Quaternion worldRot)
    {
        OwnerNetworkTransform netTransform = SelfOwnerNetworkTransform;
        bool hasNetworkedTransform = netTransform != null && netTransform.IsSpawned;

        if (hasNetworkedTransform && !netTransform.CanCommitToTransform)
        {
            // Not the transform authority (the server drives the pose while the Jailor carries this player),
            // so a direct write here would only fight the interpolator.
            netTransform.SnapObserverToLatestNetworkState();
            return false;
        }

        // Briefly disable the CharacterController so its solver doesn't fight the direct position write.
        bool reenable = characterController != null && characterController.enabled;
        if (reenable)
            characterController.enabled = false;
        transform.SetPositionAndRotation(worldPos, worldRot);
        if (reenable)
            characterController.enabled = true;

        // Sitting down / standing up is a jump, not motion: without the teleport flag observers interpolate
        // it and see the player slide across the floor onto the stool.
        if (hasNetworkedTransform)
            netTransform.Teleport(worldPos, worldRot, Vector3.one);

        return true;
    }

    /// <summary>Teleport the player onto the stool and start the sitting animation.</summary>
    public void EnterBlackjackSeat(Vector3 worldPos, Quaternion worldRot)
    {
        // Captured before the move; only committed once it actually lands. A dropped teleport must not mark
        // the player seated — the body never reached the stool, and the sit pose would play on their feet.
        Vector3 preSeatPosition = transform.position;
        Quaternion preSeatRotation = transform.rotation;

        _horizontalVelocity = Vector3.zero;
        CancelThrowCharge();
        if (!TeleportPlayer(worldPos, worldRot))
            return;

        if (!_blackjackSeated)
        {
            // Remember where we stood so we can stand back up there (the seated root is raised onto the stool).
            _preSeatPosition = preSeatPosition;
            _preSeatRotation = preSeatRotation;
            _blackjackSeated = true;
        }

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
            // Give up the seat state only once the restoring move lands. Clearing it after a dropped teleport
            // (the server owns the pose while the Jailor carries this player) consumed the pre-seat restore
            // and left the player pinned at the stool pose with movement locked, with nothing to retry from —
            // the overlay drives this exit exactly once.
            if (TeleportPlayer(_preSeatPosition, _preSeatRotation))
            {
                _blackjackSeated = false;
                _pendingBlackjackSeatExit = false;
            }
            else
            {
                _pendingBlackjackSeatExit = true;
            }
        }

        // Restore the hold pose for whatever is selected now that we're standing again.
        ApplyHoldPoseAnimatorParameter();
    }

    /// <summary>
    /// Re-attempts a seat exit whose restoring teleport was dropped for lack of transform authority. Ticked
    /// from Update ahead of the local-control gate, because the drop happens precisely while control is off
    /// (the Jailor carry) — the retry lands on the first frame authority comes back.
    /// </summary>
    void TickPendingBlackjackSeatExit()
    {
        if (!_pendingBlackjackSeatExit)
            return;

        if (!_blackjackSeated)
        {
            _pendingBlackjackSeatExit = false;
            return;
        }

        ExitBlackjackSeat();
    }
}
