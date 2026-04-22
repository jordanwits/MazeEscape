using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Unity.Netcode;

public partial class PlayerController
{
    public Transform InventoryStashRoot => inventoryStashRoot;
    public NetworkPlayerInventory NetworkPlayerInventory => _networkPlayerInventory;

    /// <summary>
    /// True when the Netcode hotbar is active. Prefer this for slot icons, battery fill, and
    /// input routing — <c>UseNetworkedFlashlightFlow</c> also needs a listening manager + avatar
    /// and can disagree with <see cref="NetworkPlayerInventory.IsSpawned"/>.
    /// </summary>
    bool IsUsingNetworkedInventory => _networkPlayerInventory != null && _networkPlayerInventory.IsSpawned;

    void EnsureInventoryStashRoot()
    {
        if (inventoryStashRoot != null)
            return;

        Transform found = transform.Find("InventoryStash");
        if (found == null)
        {
            GameObject stashGo = new GameObject("InventoryStash");
            found = stashGo.transform;
            found.SetParent(transform, false);
            found.localPosition = new Vector3(0f, 0.25f, 0.15f);
        }

        inventoryStashRoot = found;
    }

    /// <summary>Used for remote inventory replication and hold/stash.</summary>
    public bool TryGetInventoryAttachmentTargets(out Transform holdPoint, out Transform followTransform, out Transform stashPoint)
    {
        holdPoint = null;
        followTransform = null;
        stashPoint = inventoryStashRoot;
        if (!TryGetFlashlightAttachmentTargets(out holdPoint, out followTransform))
            return false;
        EnsureInventoryStashRoot();
        stashPoint = inventoryStashRoot;
        return stashPoint != null;
    }

    public void RefreshInventoryViewFromNetwork()
    {
        if (!isActiveAndEnabled)
            return;

        if (IsUsingNetworkedInventory)
        {
            NetworkObject thisPlayer = GetComponent<NetworkObject>();
            if (thisPlayer == null)
                return;
            ulong holderId = thisPlayer.NetworkObjectId;
            for (int i = 0; i < 3; i++)
            {
                ulong id = _networkPlayerInventory.GetSlotItemId(i);
                if (id == 0UL)
                    continue;

                if (!GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g) || g == null)
                    continue;

                int selected = _networkPlayerInventory.SelectedSlotIndex;
                bool isStash = i != selected;
                g.StashOverrideParent = isStash ? inventoryStashRoot : null;
                g.SetStashViewStateForInventory(isStash);
                g.ApplyNetworkHeldState(holderId);
                if (g is FlashlightItem f)
                {
                    f.ApplyInventoryStashVisual(isStash, _networkPlayerInventory.SelectedFlashlightLightOn);
                }

                if (g is GlowstickItem gs)
                {
                    gs.SetStackCount(_networkPlayerInventory.GetSlotStackCount(i));
                    gs.SetEmissiveInHand(!isStash, true);
                }
            }

            RefreshInventorySlotHud();
            return;
        }

        RefreshLocalInventoryView();
    }

    void RefreshLocalInventoryView()
    {
        if (flashlightHoldPoint == null)
            return;
        EnsureInventoryStashRoot();
        Transform follow = flashlightFollowsCameraPitch ? CameraTransformForFacing : flashlightHoldPoint;
        for (int i = 0; i < 3; i++)
        {
            GrabbableInventoryItem g = _localInventorySlots[i];
            if (g == null)
                continue;

            bool inHand = i == _localSelectedSlot;
            g.StashOverrideParent = inHand ? null : inventoryStashRoot;
            g.SetStashViewStateForInventory(!inHand);
            if (inHand)
                g.Pickup(flashlightHoldPoint, follow);
            else
                g.StashInInventory(inventoryStashRoot);
            if (g is FlashlightItem f2)
            {
                f2.ApplyInventoryStashVisual(!inHand, f2.IsLightOn);
            }

            if (g is GlowstickItem gs2)
            {
                gs2.SetStackCount(_localSlotStacks[i]);
                gs2.SetEmissiveInHand(inHand, true);
            }
        }

        RefreshInventorySlotHud();
    }

    int GetLocalFirstEmptySlot()
    {
        for (int i = 0; i < 3; i++)
        {
            if (_localInventorySlots[i] == null)
                return i;
        }

        return -1;
    }

    bool IsLocalInventoryCompletelyFull => GetLocalFirstEmptySlot() < 0;

    bool CanPickupLocal(GrabbableInventoryItem item)
    {
        if (item == null || item.IsHeld)
            return false;

        if (item is GlowstickItem gs)
        {
            int w = gs.StackCount;
            if (GetLocalFirstEmptySlot() >= 0)
                return true;
            for (int i = 0; i < 3; i++)
            {
                if (_localInventorySlots[i] is not GlowstickItem)
                    continue;
                if (_localSlotStacks[i] < GlowstickItem.MaxStack && w > 0)
                    return true;
            }
            return false;
        }

        return !IsLocalInventoryCompletelyFull;
    }

    void TryPickupItemLocal(GrabbableInventoryItem g)
    {
        if (g == null || g.IsHeld || !CanPickupLocal(g))
            return;

        if (g is FlashlightItem)
        {
            int slot = GetLocalFirstEmptySlot();
            if (slot < 0)
                return;
            _localInventorySlots[slot] = g;
            _localSlotStacks[slot] = 1;
            _localSelectedSlot = slot;
            RefreshLocalInventoryView();
            SetPickupPromptVisible(false);
            return;
        }

        if (g is GlowstickItem pickup)
        {
            int w = pickup.StackCount;
            for (int i = 0; i < 3 && w > 0; i++)
            {
                if (_localInventorySlots[i] is not GlowstickItem inSlot)
                    continue;
                int c = _localSlotStacks[i];
                int space = GlowstickItem.MaxStack - c;
                if (space <= 0)
                    continue;
                int add = Mathf.Min(w, space);
                inSlot.SetStackCount(c + add);
                _localSlotStacks[i] = inSlot.StackCount;
                w -= add;
            }
            if (w <= 0)
            {
                Destroy(pickup.gameObject);
                RefreshLocalInventoryView();
                SetPickupPromptVisible(false);
                return;
            }
            int empty = GetLocalFirstEmptySlot();
            if (empty < 0)
            {
                pickup.SetStackCount(w);
                return;
            }
            pickup.SetStackCount(w);
            _localInventorySlots[empty] = pickup;
            _localSlotStacks[empty] = w;
            _localSelectedSlot = empty;
            RefreshLocalInventoryView();
            SetPickupPromptVisible(false);
            return;
        }

        int slotOther = GetLocalFirstEmptySlot();
        if (slotOther < 0)
            return;
        _localInventorySlots[slotOther] = g;
        _localSlotStacks[slotOther] = 1;
        _localSelectedSlot = slotOther;
        RefreshLocalInventoryView();
        SetPickupPromptVisible(false);
    }

    void TryDropSelectedLocal()
    {
        if (IsLocalInventoryCompletelyEmpty())
            return;

        GrabbableInventoryItem g = _localInventorySlots[_localSelectedSlot];
        if (g == null)
            return;

        int slot = _localSelectedSlot;
        Vector3 f = CameraTransformForFacing != null ? CameraTransformForFacing.forward : transform.forward;
        Vector3 imp = f * dropForce;
        Vector3 dropPos = flashlightHoldPoint != null
            ? flashlightHoldPoint.position + f.normalized * 0.35f
            : transform.position + f.normalized * 0.75f;
        dropPos.y = Mathf.Max(dropPos.y, transform.position.y + 0.1f);
        Quaternion dropRot = flashlightHoldPoint != null ? flashlightHoldPoint.rotation : transform.rotation;

        if (g is GlowstickItem gsInv && _localSlotStacks[slot] > 1)
        {
            int next = _localSlotStacks[slot] - 1;
            _localSlotStacks[slot] = next;
            gsInv.SetStackCount(next);

            GameObject d = Object.Instantiate(gsInv.gameObject, dropPos, dropRot);
            d.transform.SetParent(null, true);
            if (d.TryGetComponent(out GlowstickItem dropped) && dropped != null)
            {
                dropped.SetStackCount(1);
                dropped.StashOverrideParent = null;
                dropped.ApplyNetworkWorldState(dropPos, dropRot, imp);
                dropped.SetWorldDroppedVisual();
            }
            else
            {
                Destroy(d);
            }

            RefreshLocalInventoryView();
            return;
        }

        if (g is GlowstickItem gs)
            gs.SetStackCount(Mathf.Max(1, _localSlotStacks[slot]));

        _localInventorySlots[slot] = null;
        _localSlotStacks[slot] = 0;
        SelectAfterDropLocal();
        g.StashOverrideParent = null;
        g.Drop(imp);
        RefreshLocalInventoryView();
    }

    bool IsLocalInventoryCompletelyEmpty()
    {
        for (int i = 0; i < 3; i++)
        {
            if (_localInventorySlots[i] != null)
                return false;
        }

        return true;
    }

    void SelectAfterDropLocal()
    {
        for (int i = 0; i < 3; i++)
        {
            if (_localInventorySlots[i] != null)
            {
                _localSelectedSlot = i;
                return;
            }
        }

        _localSelectedSlot = 0;
    }

    void TryCycleLocalSelection(int sign)
    {
        if (sign == 0)
            return;
        // Always move selection across all three indices so you can "equip" an empty row:
        // nothing is shown at the hold point until you scroll to a slot that has an item.
        int step = sign > 0 ? 1 : -1;
        int next = _localSelectedSlot + step;
        _localSelectedSlot = ((next % 3) + 3) % 3;
        RefreshLocalInventoryView();
    }

    void RefreshInventorySlotHud()
    {
        if (_inventorySlotIconImages == null || _inventorySlotIconImages.Length < 3)
            return;
        for (int i = 0; i < 3; i++)
        {
            Image icon = _inventorySlotIconImages[i];
            if (icon == null)
                continue;
            if (IsUsingNetworkedInventory)
            {
                ulong id = _networkPlayerInventory.GetSlotItemId(i);
                if (id == 0UL)
                {
                    icon.sprite = null;
                    icon.enabled = false;
                    SetSlotStackText(i, 0, false);
                }
                else if (GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g) && g != null)
                {
                    icon.sprite = g.GetEffectiveSlotIconForHud();
                    icon.color = Color.white;
                    icon.enabled = true;
                    bool isGlow = g is GlowstickItem;
                    SetSlotStackText(i, isGlow ? _networkPlayerInventory.GetSlotStackCount(i) : 0, isGlow);
                }
            }
            else
            {
                GrabbableInventoryItem g = _localInventorySlots[i];
                if (g == null)
                {
                    icon.sprite = null;
                    icon.enabled = false;
                    SetSlotStackText(i, 0, false);
                }
                else
                {
                    icon.sprite = g.GetEffectiveSlotIconForHud();
                    icon.color = Color.white;
                    icon.enabled = true;
                    bool isGlow = g is GlowstickItem;
                    SetSlotStackText(i, isGlow ? _localSlotStacks[i] : 0, isGlow);
                }
            }

            if (_inventorySlotBorderImages != null && i < _inventorySlotBorderImages.Length && _inventorySlotBorderImages[i] != null)
            {
                int sel = IsUsingNetworkedInventory
                    ? _networkPlayerInventory.SelectedSlotIndex
                    : _localSelectedSlot;
                _inventorySlotBorderImages[i].color = i == sel ? _inventorySelectedBorderColor : _inventoryDefaultBorderColor;
            }
        }
    }

    void SetSlotStackText(int index, int count, bool showForGlowstick)
    {
        if (_inventorySlotCountTexts == null || index < 0 || index >= _inventorySlotCountTexts.Length)
            return;
        Text t = _inventorySlotCountTexts[index];
        if (t == null)
            return;
        if (!showForGlowstick)
        {
            t.enabled = false;
            t.text = string.Empty;
            return;
        }
        t.enabled = true;
        t.text = count.ToString();
    }

    void HandleInventoryScrollInUpdate()
    {
        if (MultiplayerMenuOverlay.BlocksGameplayInput)
            return;
        if (IsUsingNetworkedInventory
            && _networkPlayerAvatar != null
            && _networkPlayerAvatar.IsSpawned
            && !_networkPlayerAvatar.IsOwner)
        {
            return;
        }
        if (!_hasLocalControl)
            return;
        if (Mouse.current == null)
            return;

        float y = Mouse.current.scroll.ReadValue().y;
        if (Mathf.Abs(y) < 0.01f)
            return;
        int sign = y < 0f ? 1 : -1;
        if (IsUsingNetworkedInventory)
            _networkPlayerInventory.TryCycleSelection(sign);
        else
            TryCycleLocalSelection(sign);
    }

    void HandlePickupInput()
    {
        Transform cam = CameraTransformForFacing;
        if (cam != null && TryFindInteractableMazeChest(cam, out MazeChest mazeChest))
        {
            mazeChest.TryRequestOpen(cam.position);
            return;
        }

        if (IsUsingNetworkedInventory)
        {
            TryPickupNetwork();
            return;
        }

        TryPickupLocal();
    }

    void TryPickupNetwork()
    {
        if (_networkPlayerInventory == null
            || !TryFindInteractableGrabbable(out GrabbableInventoryItem g))
        {
            return;
        }

        if (!_networkPlayerInventory.CanPickup(g))
            return;

        _networkPlayerInventory.TryPickupItem(g);
    }

    void TryPickupLocal()
    {
        if (flashlightHoldPoint == null)
            return;
        if (!TryFindInteractableGrabbable(out GrabbableInventoryItem g) || g == null)
            return;
        TryPickupItemLocal(g);
    }

    void HandleDropInput()
    {
        if (IsUsingNetworkedInventory)
        {
            if (_networkPlayerInventory == null
                || !_networkPlayerInventory.HasItemInSelectedSlot)
            {
                return;
            }

            Vector3 f = CameraTransformForFacing != null ? CameraTransformForFacing.forward : transform.forward;
            Vector3 dropPosition = flashlightHoldPoint != null ? flashlightHoldPoint.position : transform.position + f * 0.75f;
            Quaternion dropRotation = flashlightHoldPoint != null ? flashlightHoldPoint.rotation : transform.rotation;
            _networkPlayerInventory.TryDropSelectedItem(dropPosition, dropRotation, f);
            return;
        }

        TryDropSelectedLocal();
    }

    void HandleFlashlightToggleInput()
    {
        if (IsUsingNetworkedInventory)
        {
            if (_networkPlayerInventory == null
                || !HasSelectedFlashlightInWorld())
            {
                return;
            }
            _networkPlayerInventory?.TryToggleSelectedFlashlight();
            return;
        }

        if (TryGetSelectedLocalFlashlight(out FlashlightItem f))
        {
            f.ToggleLight();
        }
    }

    bool HasSelectedFlashlightInWorld()
    {
        if (_networkPlayerInventory == null || !_networkPlayerInventory.IsSpawned)
            return false;
        ulong id = _networkPlayerInventory.GetSlotItemId(_networkPlayerInventory.SelectedSlotIndex);
        if (id == 0UL)
            return false;
        if (!GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g))
            return false;
        return g is FlashlightItem;
    }

    bool TryGetSelectedLocalFlashlight(out FlashlightItem flashlight)
    {
        if (_localInventorySlots[_localSelectedSlot] is FlashlightItem f2)
        {
            flashlight = f2;
            return true;
        }

        flashlight = null;
        return false;
    }

    bool ShouldShowPickupPrompt()
    {
        if (flashlightHoldPoint == null)
            return false;

        return TryFindInteractableGrabbable(out _);
    }

    bool TryFindInteractableGrabbable(out GrabbableInventoryItem grabbable)
    {
        grabbable = null;
        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return false;
        int mask = interactMask.value == 0 ? Physics.DefaultRaycastLayers : interactMask.value;
        int count = TryInteractCastNonAlloc(cam, mask);
        if (count <= 0)
        {
            return false;
        }

        SortInteractHitsByDistance(count);
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _interactCastHitBuffer[i];
            if (InteractHitBelongsToOpenedChest(h))
            {
                continue;
            }

            GrabbableInventoryItem g = h.collider.GetComponentInParent<GrabbableInventoryItem>();
            if (g == null)
            {
                continue;
            }

            if (IsUsingNetworkedInventory)
            {
                if (_networkPlayerInventory == null
                    || !_networkPlayerInventory.CanPickup(g))
                {
                    continue;
                }
            }
            else
            {
                if (g.IsHeld || !CanPickupLocal(g))
                    continue;
            }

            grabbable = g;
            return true;
        }

        return TryFindInteractableGrabbableInViewFallback(cam, mask, out grabbable);
    }

    void TickLocalFlashlightBatteries()
    {
        bool anyLightStateChanged = false;
        for (int i = 0; i < 3; i++)
        {
            if (_localInventorySlots[i] is not FlashlightItem f)
                continue;
            bool onBefore = f.IsLightOn;
            f.TickBattery(Time.deltaTime);
            if (onBefore != f.IsLightOn)
                anyLightStateChanged = true;
        }
        if (anyLightStateChanged)
            RefreshLocalInventoryView();
    }

    void UpdateInventoryFlashlightBatteryHud()
    {
        if (_inventorySlotFlashlightBatteryFillImages == null
            || _inventorySlotFlashlightBatteryFillRects == null
            || _inventorySlotFlashlightBatteryBarRoots == null
            || _inventorySlotFlashlightBatteryFillImages.Length < 3
            || _inventorySlotFlashlightBatteryFillRects.Length < 3)
            return;
        for (int i = 0; i < 3; i++)
        {
            bool show = false;
            float t = 0f;
            if (IsUsingNetworkedInventory)
            {
                ulong id = _networkPlayerInventory.GetSlotItemId(i);
                if (id != 0UL
                    && GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g)
                    && g is FlashlightItem)
                {
                    show = true;
                    t = _networkPlayerInventory.GetSlotFlashlightBatteryNormalizedForHud(i);
                }
            }
            else if (_localInventorySlots[i] is FlashlightItem fl)
            {
                show = true;
                t = fl.BatteryFractionNormalized;
            }
            GameObject barRoot = _inventorySlotFlashlightBatteryBarRoots[i];
            if (barRoot != null)
                barRoot.SetActive(show);
            if (show
                && i < _inventorySlotFlashlightBatteryFillImages.Length
                && i < _inventorySlotFlashlightBatteryFillRects.Length
                && _inventorySlotFlashlightBatteryFillImages[i] != null
                && _inventorySlotFlashlightBatteryFillRects[i] != null)
            {
                float normalized = Mathf.Clamp01(t);
                RectTransform fillRect = _inventorySlotFlashlightBatteryFillRects[i];
                fillRect.anchorMax = new Vector2(normalized, 1f);
            }
        }
    }
}
