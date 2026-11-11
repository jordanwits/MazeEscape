using UnityEngine;

/// <summary>
/// Harmonica input on the local player: with a <see cref="HarmonicaItem"/> selected, holding Attack plays a track
/// instead of punching, releasing stops it, and every new hold plays the next track in order.
///
/// The hold is detected frame by frame rather than from press/release events. <see cref="HandleAttackInput"/>
/// stamps <see cref="_harmonicaHoldFrame"/> on every frame the button is down with the harmonica in hand, and
/// <see cref="TickHarmonicaInput"/> (LateUpdate, above every early-return) stops the track on the first frame
/// that stamp is missing. That one rule covers the release itself and every way Update can stop reaching
/// HandleAttackInput while the button stays down — pause menu, an overlay, a ragdoll, a Jailor grab, death —
/// so none of those can leave a teammate's copy playing.
/// </summary>
public partial class PlayerController
{
    /// <summary>Frame on which HandleAttackInput last saw Attack held with the harmonica selected.</summary>
    int _harmonicaHoldFrame = -1;

    /// <summary>True from the press that started a track until that hold ends (even if the track ran out).</summary>
    bool _harmonicaHolding;

    /// <summary>The harmonica the current hold is playing, so the stop reaches it after it is put away.</summary>
    HarmonicaItem _harmonicaPlaying;

    /// <summary>
    /// True when this avatar is the one whose eyes this machine looks through (our own player, or the player
    /// offline) — false for a teammate's body in our world. Lets held items present themselves for the
    /// first-person view without changing what everyone else sees.
    /// </summary>
    public bool IsFirstPersonViewOwner => !IsObserverPuppet;

    /// <summary>Is a harmonica the selected hotbar item? Gates Attack away from the punch.</summary>
    bool HasSelectedHarmonica()
    {
        return TryGetSelectedHarmonica(out _);
    }

    /// <summary>
    /// Called by <see cref="HandleAttackInput"/> while a harmonica is selected. A track starts only on a real
    /// press — so selecting the harmonica with the button already down, or the click that recaptures the cursor,
    /// does not start one — and the hold then continues for as long as the button stays down.
    /// </summary>
    void HandleHarmonicaAttackInput(bool pressed, bool held)
    {
        if (pressed || (held && _harmonicaHolding))
            _harmonicaHoldFrame = Time.frameCount;
    }

    /// <summary>Starts and stops the harmonica from the hold stamp. LateUpdate; a no-op on remote avatars.</summary>
    void TickHarmonicaInput()
    {
        bool wantPlay = _harmonicaHoldFrame == Time.frameCount;

        if (wantPlay && !_harmonicaHolding)
        {
            if (!TryGetSelectedHarmonica(out HarmonicaItem harmonica) || harmonica == null || harmonica.TrackCount == 0)
                return;

            _harmonicaHolding = true;
            _harmonicaPlaying = harmonica;
            int track = harmonica.TakeNextTrackIndex();
            harmonica.PlayTrack(track, holderIsLocalListener: true);

            if (IsUsingNetworkedInventory)
                _networkPlayerInventory.NotifyHarmonicaPlayback(harmonica.ItemId, true, track);
            return;
        }

        if (!wantPlay && _harmonicaHolding)
            StopLocalHarmonica();
    }

    void StopLocalHarmonica()
    {
        _harmonicaHolding = false;
        HarmonicaItem harmonica = _harmonicaPlaying;
        _harmonicaPlaying = null;
        if (harmonica == null)
            return;

        harmonica.StopTrack();
        if (IsUsingNetworkedInventory && _networkPlayerInventory != null)
            _networkPlayerInventory.NotifyHarmonicaPlayback(harmonica.ItemId, false, 0);
    }

    /// <summary>Selected-slot harmonica, resolved the same way the flashbang resolves its item.</summary>
    bool TryGetSelectedHarmonica(out HarmonicaItem harmonica)
    {
        harmonica = null;

        if (IsUsingNetworkedInventory)
        {
            int sel = _networkPlayerInventory.SelectedSlotIndex;
            if (_networkPlayerInventory.GetSlotItemTypeId(sel) != GrabbableInventoryItem.TypeIdHarmonica)
                return false;

            ulong id = _networkPlayerInventory.GetSlotItemId(sel);
            if (id != 0UL && GrabbableInventoryItem.TryGetRegistered(id, out GrabbableInventoryItem g))
                harmonica = g as HarmonicaItem;
            // The slot type is authoritative for gating input (so Attack never punches with it in hand); a null
            // instance only means the local copy has not resolved yet, and nothing plays until it does.
            return true;
        }

        if (_localSelectedSlot < 0 || _localSelectedSlot >= InventorySlotCapacity
            || _localInventorySlots[_localSelectedSlot] is not HarmonicaItem localHarmonica)
            return false;

        harmonica = localHarmonica;
        return true;
    }
}
