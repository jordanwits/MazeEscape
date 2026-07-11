using TMPro;
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
            bool stashAllForBall = IsHeavyThrowableForcingInventoryStash(holderId);
            for (int i = 0; i < 3; i++)
            {
                ulong id = _networkPlayerInventory.GetSlotItemId(i);
                byte itemType = _networkPlayerInventory.GetSlotItemTypeId(i);
                if (id == 0UL && itemType == GrabbableInventoryItem.TypeIdNone)
                    continue;

                GrabbableInventoryItem g = null;
                bool found = GrabbableInventoryItem.TryGetRegistered(id, out g) && g != null;
                if (!found && itemType != GrabbableInventoryItem.TypeIdNone)
                {
                    Vector3 hintPos = transform.position;
                    found = GrabbableInventoryItem.TryResolveForStateByType(id, hintPos, itemType, out g);
                }
                if (!found || g == null)
                    continue;

                if (g.ItemId != id && id != 0UL)
                    g.AssignNetworkItemId(id);

                int selected = _networkPlayerInventory.SelectedSlotIndex;
                bool isStash = stashAllForBall || (i != selected);
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

            DetachItemsNoLongerInNetworkInventory(holderId);
            ApplyHoldPoseAnimatorParameter();
            RefreshInventorySlotHud();
            return;
        }

        RefreshLocalInventoryView();
    }

    static readonly int HoldPoseHash = Animator.StringToHash("HoldPose");
    NetworkObject _selfNetworkObjectCache;
    NetworkObject SelfNetworkObject => _selfNetworkObjectCache != null
        ? _selfNetworkObjectCache
        : (_selfNetworkObjectCache = GetComponent<NetworkObject>());

    /// <summary>
    /// Drives the "Item Hold" animator layer: 0 empty hands, 1 one-hand hotbar item, 2 two-hand heavy carry.
    /// Owner-only in network sessions — OwnerNetworkAnimator replicates the int, so a non-owner writing it
    /// locally would fight the replicated value. Forced to 0 while blackjack-seated so hold arms never
    /// override the Sit pose.
    /// </summary>
    public void ApplyHoldPoseAnimatorParameter()
    {
        if (!driveAnimator || animator == null)
            return;

        if (IsUsingNetworkedInventory && _networkPlayerAvatar != null && _networkPlayerAvatar.IsSpawned && !_networkPlayerAvatar.IsOwner)
            return;

        int pose = 0;
        if (!_blackjackSeated)
        {
            if (IsUsingNetworkedInventory)
            {
                NetworkObject thisPlayer = SelfNetworkObject;
                ulong holderId = thisPlayer != null ? thisPlayer.NetworkObjectId : 0UL;
                NetworkHeavyThrowableHold heavy = holderId != 0UL
                    ? NetworkHeavyThrowableHold.FindHeldByPlayerObjectId(holderId) : null;
                if (heavy != null)
                {
                    // Socket-held heavy items (rings) are one-handed; chest-carried ones (StarBall) are two-handed.
                    pose = heavy.HeldItem != null && heavy.HeldItem.HeldAttachToHandSocket ? 1 : 2;
                }
                else
                {
                    int selected = _networkPlayerInventory.SelectedSlotIndex;
                    if (selected >= 0 && selected < 3
                        && (_networkPlayerInventory.GetSlotItemId(selected) != 0UL
                            || _networkPlayerInventory.GetSlotItemTypeId(selected) != GrabbableInventoryItem.TypeIdNone))
                    {
                        pose = 1;
                    }
                }
            }
            else
            {
                NetworkHeavyThrowableHold heavy = NetworkHeavyThrowableHold.FindOfflineHeldBy(this);
                if (heavy != null)
                    pose = heavy.HeldItem != null && heavy.HeldItem.HeldAttachToHandSocket ? 1 : 2;
                else if (_localSelectedSlot >= 0 && _localSelectedSlot < 3 && _localInventorySlots[_localSelectedSlot] != null)
                    pose = 1;
            }
        }

        animator.SetInteger(HoldPoseHash, pose);
    }

    /// <summary>
    /// While carrying a heavy throwable (StarBall, ring toss), hotbar items stay in the stash (pocket).
    /// </summary>
    bool IsHeavyThrowableForcingInventoryStash(ulong playerNetworkObjectId)
    {
        if (playerNetworkObjectId == 0UL)
            return false;

        return NetworkHeavyThrowableHold.FindHeldByPlayerObjectId(playerNetworkObjectId) != null;
    }

    void DetachItemsNoLongerInNetworkInventory(ulong holderId)
    {
        if (holderId == 0UL || _networkPlayerInventory == null)
            return;

        foreach (GrabbableInventoryItem g in GrabbableInventoryItem.GetRegisteredItems())
        {
            if (g == null || g.HolderNetworkObjectId != holderId)
                continue;
            // Heavy throwables are held via NetworkHeavyThrowableHold + replicated holder id, not hotbar slots.
            // Without this, any inventory refresh (e.g. scroll changing SelectedSlot) "orphans" it on
            // clients and desyncs from server holder state — pickup then fails on the server.
            if (g is HeavyThrowableHoldItem)
                continue;
            if (IsItemStillInNetworkInventory(g.ItemId))
                continue;

            if (g is FlashlightItem flashlight)
            {
                flashlight.ApplyNetworkWorldState(g.transform.position, g.transform.rotation, false, default);
                continue;
            }

            g.ApplyNetworkWorldState(g.transform.position, g.transform.rotation, default);
            if (g is GlowstickItem glowstick)
                glowstick.SetWorldDroppedVisual();
        }
    }

    bool IsItemStillInNetworkInventory(ulong itemId)
    {
        if (itemId == 0UL || _networkPlayerInventory == null)
            return false;

        for (int i = 0; i < 3; i++)
        {
            if (_networkPlayerInventory.GetSlotItemId(i) == itemId)
                return true;
        }

        return false;
    }

    void RefreshLocalInventoryView()
    {
        if (flashlightHoldPoint == null)
            return;
        EnsureInventoryStashRoot();
        Transform follow = flashlightFollowsCameraPitch ? CameraTransformForFacing : flashlightHoldPoint;
        bool stashAllForBall = NetworkHeavyThrowableHold.FindOfflineHeldBy(this) != null;
        for (int i = 0; i < 3; i++)
        {
            GrabbableInventoryItem g = _localInventorySlots[i];
            if (g == null)
                continue;

            bool inHand = !stashAllForBall && (i == _localSelectedSlot);
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

        ApplyHoldPoseAnimatorParameter();
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

        if (item is HeavyThrowableHoldItem)
            return true;

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
                byte itemType = _networkPlayerInventory.GetSlotItemTypeId(i);
                if (id == 0UL && itemType == GrabbableInventoryItem.TypeIdNone)
                {
                    icon.sprite = null;
                    icon.enabled = false;
                    SetSlotStackText(i, 0, false);
                }
                else
                {
                    GrabbableInventoryItem g = null;
                    bool found = GrabbableInventoryItem.TryGetRegistered(id, out g) && g != null;
                    if (!found && itemType != GrabbableInventoryItem.TypeIdNone)
                    {
                        Vector3 hintPos = transform.position;
                        found = GrabbableInventoryItem.TryResolveForStateByType(id, hintPos, itemType, out g);
                    }

                    if (found && g != null)
                    {
                        icon.sprite = g.GetEffectiveSlotIconForHud();
                        icon.color = Color.white;
                        icon.enabled = true;
                        bool isGlow = g is GlowstickItem;
                        SetSlotStackText(i, isGlow ? _networkPlayerInventory.GetSlotStackCount(i) : 0, isGlow);
                    }
                    else
                    {
                        icon.sprite = GrabbableInventoryItem.GetPlaceholderSlotIcon(itemType);
                        icon.color = Color.white;
                        icon.enabled = icon.sprite != null;
                        bool isGlow = itemType == GrabbableInventoryItem.TypeIdGlowstick;
                        SetSlotStackText(i, isGlow ? _networkPlayerInventory.GetSlotStackCount(i) : 0, isGlow);
                    }
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
        TMP_Text t = _inventorySlotCountTexts[index];
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
        if (PauseMenuController.BlocksGameplayInput)
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
        if (TryHandleCarnivalInteract())
            return;

        Transform cam = CameraTransformForFacing;
        if (cam != null && TryFindInteractableMazeChest(cam, out MazeChest mazeChest))
        {
            mazeChest.TryRequestOpen(cam.position);
            return;
        }

        // Teleport orbs are hold-to-activate (see TickTeleportHold): a tap just consumes the press so it
        // doesn't fall through to a gated item pickup.
        if (cam != null && TryFindInteractableTeleportOrb(cam, out _))
        {
            return;
        }

        if (cam != null && TryFindInteractableHingeDoor(cam, out HingeInteractDoor hingeDoor) && hingeDoor != null)
        {
            if (hingeDoor.IsLocked)
            {
                if (PlayerHasKeyInInventory())
                {
                    if (IsUsingNetworkedInventory)
                    {
                        if (_networkPlayerInventory != null)
                            _networkPlayerInventory.RequestUnlockHingeDoor(hingeDoor);
                    }
                    else
                    {
                        TryUnlockHingeDoorWithKeyLocal(hingeDoor);
                    }
                }
                return;
            }

            if (IsUsingNetworkedInventory
                && _networkPlayerInventory != null
                && (!hingeDoor.TryGetComponent(out NetworkObject doorNet) || !doorNet.IsSpawned))
            {
                _networkPlayerInventory.RequestToggleHingeDoor(hingeDoor);
                return;
            }

            hingeDoor.TryRequestToggle(cam.position);
            return;
        }

        if (cam != null && TryFindInteractableSkeletonRps(cam, out SkeletonRpsChallenge rpsChallenge) && rpsChallenge != null)
        {
            rpsChallenge.RequestChallengeInteract(this);
            return;
        }

        // Grabbable items go through the gated reach (arm extends, grant fires at the apex).
        // Chest/door/carnival interactions above stay instant.
        BeginGatedPickup();
    }

    void TryPickupNetwork()
    {
        if (_networkPlayerInventory == null
            || !TryFindInteractableGrabbable(out GrabbableInventoryItem g))
        {
            return;
        }

        if (g is HeavyThrowableHoldItem && g.TryGetComponent(out NetworkHeavyThrowableHold heavyHold))
        {
            heavyHold.RequestPickupFromInteract(this);
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
        if (g is HeavyThrowableHoldItem && g.TryGetComponent(out NetworkHeavyThrowableHold heavyHold))
        {
            heavyHold.TryPickupOffline(this);
            return;
        }

        TryPickupItemLocal(g);
    }

    void HandleDropInput()
    {
        if (TryDropHeldHeavyThrowable())
            return;

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

    bool TryShootHeldHeavyThrowable(float charge01)
    {
        Vector3 f = CameraTransformForFacing != null ? CameraTransformForFacing.forward : transform.forward;

        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && NetworkManager.Singleton.LocalClient != null
            && NetworkManager.Singleton.LocalClient.PlayerObject != null)
        {
            ulong id = NetworkManager.Singleton.LocalClient.PlayerObject.NetworkObjectId;
            NetworkHeavyThrowableHold hold = NetworkHeavyThrowableHold.FindHeldByPlayerObjectId(id);
            if (hold != null)
            {
                hold.RequestShootFromOwningClient(f, this, charge01);
                return true;
            }
        }
        else
        {
            NetworkHeavyThrowableHold offline = NetworkHeavyThrowableHold.FindOfflineHeldBy(this);
            if (offline != null)
            {
                offline.RequestShootFromOwningClient(f, this, charge01);
                return true;
            }
        }

        return false;
    }

    bool TryDropHeldHeavyThrowable()
    {
        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && NetworkManager.Singleton.LocalClient != null
            && NetworkManager.Singleton.LocalClient.PlayerObject != null)
        {
            ulong id = NetworkManager.Singleton.LocalClient.PlayerObject.NetworkObjectId;
            NetworkHeavyThrowableHold hold = NetworkHeavyThrowableHold.FindHeldByPlayerObjectId(id);
            if (hold != null)
            {
                hold.RequestDropFromOwningClient(this);
                return true;
            }
        }
        else
        {
            NetworkHeavyThrowableHold offline = NetworkHeavyThrowableHold.FindOfflineHeldBy(this);
            if (offline != null)
            {
                offline.RequestDropFromOwningClient(this);
                return true;
            }
        }

        return false;
    }

    void HandleFlashlightToggleInput()
    {
        if (IsUsingNetworkedInventory)
        {
            if (_networkPlayerInventory == null
                || !_networkPlayerInventory.HasItemInSelectedSlot)
            {
                return;
            }

            int sel = _networkPlayerInventory.SelectedSlotIndex;
            if (_networkPlayerInventory.GetSlotItemTypeId(sel) == GrabbableInventoryItem.TypeIdBandage)
            {
                _networkPlayerInventory.RequestUseSelectedBandage();
                return;
            }

            if (!HasSelectedFlashlightInWorld())
            {
                return;
            }

            ulong id = _networkPlayerInventory.GetSlotItemId(sel);
            if (!GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g) || g is not FlashlightItem)
                return;

            _networkPlayerInventory.TryToggleSelectedFlashlight();

            return;
        }

        if (_localInventorySlots[_localSelectedSlot] is BandageItem)
        {
            TryUseSelectedBandageLocal();
            return;
        }

        if (TryGetSelectedLocalFlashlight(out FlashlightItem f))
        {
            bool wasOn = f.IsLightOn;
            f.ToggleLight();
            if (f.IsLightOn != wasOn)
                PlayFlashlightClickSfx();
        }
    }

    void TryUseSelectedBandageLocal()
    {
        if (_localInventorySlots[_localSelectedSlot] is not BandageItem b)
            return;
        if (_playerHealth == null || _playerHealth.IsDead || _playerHealth.CurrentHealth >= _playerHealth.MaxHealth)
            return;

        int slot = _localSelectedSlot;
        _localInventorySlots[slot] = null;
        _localSlotStacks[slot] = 0;
        SelectAfterDropLocal();
        _playerHealth.Heal(BandageItem.HealthRestoreAmount);
        Object.Destroy(b.gameObject);
        PlayBandageUseSfx();
        RefreshLocalInventoryView();
    }

    public void PlayFlashlightClickSfx()
    {
        if (flashlightClickClip == null || footstepAudioSource == null)
            return;

        footstepAudioSource.PlayOneShot(flashlightClickClip, Mathf.Max(0f, flashlightClickVolume));
    }

    public void PlayBandageUseSfx()
    {
        if (bandageUseClip == null || footstepAudioSource == null)
            return;

        footstepAudioSource.PlayOneShot(bandageUseClip, Mathf.Max(0f, bandageUseVolume));
    }

    void TryUnlockHingeDoorWithKeyLocal(HingeInteractDoor door)
    {
        if (door == null || !door.IsLocked)
            return;
        Transform cam = CameraTransformForFacing;
        if (cam == null || !door.IsInInteractRange(cam.position))
            return;
        for (int i = 0; i < 3; i++)
        {
            if (_localInventorySlots[i] is not KeyItem k)
                continue;
            _localInventorySlots[i] = null;
            _localSlotStacks[i] = 0;
            SelectAfterDropLocal();
            Destroy(k.gameObject);
            RefreshLocalInventoryView();
            door.ApplyLocalUnlock();
            return;
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

        if (IsHoldingHeavyThrowable())
            return false;

        return TryFindInteractableGrabbable(out _);
    }

    bool IsHoldingHeavyThrowable()
    {
        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsListening
            && NetworkManager.Singleton.LocalClient?.PlayerObject != null)
        {
            ulong id = NetworkManager.Singleton.LocalClient.PlayerObject.NetworkObjectId;
            return NetworkHeavyThrowableHold.FindHeldByPlayerObjectId(id) != null;
        }
        return NetworkHeavyThrowableHold.FindOfflineHeldBy(this) != null;
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
            return TryFindInteractableGrabbableInViewFallback(cam, mask, out grabbable);
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

            if (!PassHeavyThrowableInteractPromptHint(g))
                continue;

            grabbable = g;
            return true;
        }

        return TryFindInteractableGrabbableInViewFallback(cam, mask, out grabbable);
    }

    /// <summary>
    /// Heavy throwables bypass hotbar distance rules in <see cref="NetworkPlayerInventory.CanPickup"/>; match server
    /// pickup range on the client so the E prompt is not always on.
    /// </summary>
    bool PassHeavyThrowableInteractPromptHint(GrabbableInventoryItem g)
    {
        if (g != null && g.TryGetComponent(out NetworkHeavyThrowableHold hold))
            return hold.IsWithinPickupProximity(transform.position);
        return true;
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
