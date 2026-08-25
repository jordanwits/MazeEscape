using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Flashbang input and the whiteout it leaves behind. Two unrelated halves that both belong to the local
/// player: Attack with a <see cref="FlashbangItem"/> selected winds up a throw (the same press-charge-release
/// gesture and charge gauge as a heavy throwable — see <see cref="HandleAttackInput"/>), and
/// <see cref="ApplyFlashbangBlind"/> — called on whoever owns this player's view when a burst catches
/// them — floods the screen white and eases it back to clear over the blind duration.
///
/// The overlay is a plain full-screen Image on the shared HUD canvas rather than a post-process: it has to
/// sit over the HUD as well as the world, it must survive the HUD being hidden, and a blinded player should
/// still be able to see the pause menu (its canvas draws above this one).
/// </summary>
public partial class PlayerController
{
    /// <summary>Fraction of the blind spent at full white before the fade starts.</summary>
    const float FlashbangHoldFraction = 0.18f;

    /// <summary>
    /// Set when the throw charge that is currently winding up belongs to a flashbang rather than a carried
    /// heavy throwable, so the release routes to the right one. Read/written by
    /// <see cref="HandleAttackInput"/> and cleared by <see cref="CancelThrowCharge"/>.
    /// </summary>
    bool _chargingFlashbangThrow;

    float _nextFlashbangThrowTime;

    GameObject _flashbangOverlayRoot;
    Image _flashbangOverlay;
    float _flashbangBlindEndTime;
    float _flashbangBlindDuration;
    float _flashbangBlindStrength;

    /// <summary>True while this player's view is still washed out by a flashbang.</summary>
    public bool IsFlashbangBlinded => Time.time < _flashbangBlindEndTime;

    // ----- throwing -----

    /// <summary>
    /// Is a flashbang the selected hotbar item? Gates the wind-up in <see cref="HandleAttackInput"/> so a
    /// grenade in hand charges a throw instead of punching.
    /// </summary>
    bool HasSelectedFlashbang()
    {
        return TryGetSelectedFlashbang(out _);
    }

    /// <summary>
    /// Charge released with a flashbang selected: lob it along the camera aim.
    /// <paramref name="charge01"/> scales the launch speed exactly as it does for a heavy throwable.
    /// </summary>
    void ThrowSelectedFlashbang(float charge01)
    {
        if (!TryGetSelectedFlashbang(out FlashbangItem flashbang))
            return;

        if (Time.time < _nextFlashbangThrowTime)
            return;

        Transform cam = CameraTransformForFacing;
        if (cam == null)
            return;

        _nextFlashbangThrowTime = Time.time + FlashbangItem.ThrowCooldownSeconds;

        Vector3 aim = cam.forward;
        float forwardOffset = flashbang != null ? flashbang.ThrowSpawnForwardOffset : 0.42f;
        Vector3 origin = cam.position + aim.normalized * forwardOffset;
        // Never release below the thrower's feet — matches the heavy-throwable release clamp.
        origin.y = Mathf.Max(origin.y, transform.position.y + 0.1f);

        if (IsUsingNetworkedInventory)
        {
            _networkPlayerInventory.RequestThrowSelectedFlashbang(origin, aim, charge01);
            return;
        }

        ThrowFlashbangOffline(flashbang, origin, aim, charge01);
    }

    void ThrowFlashbangOffline(FlashbangItem flashbang, Vector3 origin, Vector3 aim, float charge01)
    {
        if (flashbang == null || flashbang.GrenadePrefab == null)
            return;

        GameObject go = Object.Instantiate(flashbang.GrenadePrefab, origin, Quaternion.LookRotation(aim));
        if (!go.TryGetComponent(out FlashbangGrenade grenade))
        {
            Object.Destroy(go);
            return; // nothing was thrown, so nothing gets consumed
        }

        grenade.Launch(origin, flashbang.ThrowVelocity(aim, charge01), transform);

        // One grenade off the stack; the slot only clears when the last one goes.
        int slot = _localSelectedSlot;
        int inStack = Mathf.Max(1, _localSlotStacks[slot]);
        if (inStack > 1)
        {
            int remaining = inStack - 1;
            flashbang.SetStackCount(remaining);
            _localSlotStacks[slot] = remaining;
        }
        else
        {
            _localInventorySlots[slot] = null;
            _localSlotStacks[slot] = 0;
            SelectAfterDropLocal();
            Object.Destroy(flashbang.gameObject);
        }

        RefreshLocalInventoryView();
    }

    /// <summary>Selected-slot flashbang, resolved the same way the flare gun resolves its own item.</summary>
    bool TryGetSelectedFlashbang(out FlashbangItem flashbang)
    {
        flashbang = null;

        if (IsUsingNetworkedInventory)
        {
            int sel = _networkPlayerInventory.SelectedSlotIndex;
            if (_networkPlayerInventory.GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdFlashbang)
                return false;

            ulong id = _networkPlayerInventory.GetSlotItemId(sel);
            if (id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g))
                flashbang = g as FlashbangItem;
            // The slot type is authoritative for gating input; a null instance just means the local copy has
            // not resolved yet, and the server does its own resolve before spawning anything.
            return true;
        }

        if (_localSelectedSlot < 0 || _localSelectedSlot >= InventorySlotCapacity
            || _localInventorySlots[_localSelectedSlot] is not FlashbangItem localFlashbang)
            return false;

        flashbang = localFlashbang;
        return true;
    }

    // ----- being blinded -----

    /// <summary>
    /// Called on the machine that owns this player's view when a flashbang catches them.
    /// <paramref name="strength"/> is 0..1 — 1 is a dead-on whiteout, lower values a partial wash from
    /// distance or from having your back turned. A second flash never shortens an existing one.
    /// </summary>
    public void ApplyFlashbangBlind(float seconds, float strength)
    {
        if (seconds <= 0f)
            return;

        strength = Mathf.Clamp01(strength);
        if (strength <= 0.02f)
            return;

        float end = Time.time + seconds;
        if (end >= _flashbangBlindEndTime)
        {
            _flashbangBlindEndTime = end;
            _flashbangBlindDuration = seconds;
        }

        _flashbangBlindStrength = Mathf.Max(_flashbangBlindStrength, strength);

        EnsureFlashbangOverlay();
        TickFlashbangBlind();
    }

    /// <summary>Drives the whiteout alpha; called every LateUpdate, cheap no-op when nothing is blinded.</summary>
    void TickFlashbangBlind()
    {
        if (_flashbangOverlay == null)
            return;

        float remaining = _flashbangBlindEndTime - Time.time;
        if (remaining <= 0f || _flashbangBlindDuration <= 0f)
        {
            _flashbangBlindStrength = 0f;
            if (_flashbangOverlayRoot.activeSelf)
                _flashbangOverlayRoot.SetActive(false);
            return;
        }

        if (!_flashbangOverlayRoot.activeSelf)
            _flashbangOverlayRoot.SetActive(true);

        // Full white for the first slice, then a quadratic ease-out: shapes come back quickly, but a milky
        // haze hangs on right to the end of the blind.
        float progress = 1f - remaining / _flashbangBlindDuration;
        float alpha;
        if (progress <= FlashbangHoldFraction)
        {
            alpha = _flashbangBlindStrength;
        }
        else
        {
            float fade = (progress - FlashbangHoldFraction) / (1f - FlashbangHoldFraction);
            float eased = 1f - fade;
            alpha = _flashbangBlindStrength * eased * eased;
        }

        Color color = _flashbangOverlay.color;
        color.a = Mathf.Clamp01(alpha);
        _flashbangOverlay.color = color;
    }

    void EnsureFlashbangOverlay()
    {
        if (_flashbangOverlay != null)
            return;

        Canvas canvas = HudKit.EnsureHudCanvas();

        _flashbangOverlayRoot = new GameObject("FlashbangBlind", typeof(RectTransform));
        _flashbangOverlayRoot.layer = 5;
        var rect = (RectTransform)_flashbangOverlayRoot.transform;
        rect.SetParent(canvas.transform, false);
        // Above every other HUD element — a flashbang whites out the readouts too.
        rect.SetAsLastSibling();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        _flashbangOverlay = _flashbangOverlayRoot.AddComponent<Image>();
        _flashbangOverlay.raycastTarget = false;
        _flashbangOverlay.color = new Color(1f, 1f, 1f, 0f);
        _flashbangOverlayRoot.SetActive(false);
    }

    /// <summary>Called from <c>OnDestroy</c> so a destroyed avatar cannot leave a white pane on the canvas.</summary>
    void DestroyFlashbangOverlay()
    {
        if (_flashbangOverlayRoot != null)
            Destroy(_flashbangOverlayRoot);
        _flashbangOverlayRoot = null;
        _flashbangOverlay = null;
        _flashbangBlindEndTime = 0f;
        _flashbangBlindStrength = 0f;
    }
}
