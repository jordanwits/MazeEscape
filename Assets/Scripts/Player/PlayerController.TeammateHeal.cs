using UnityEngine;

/// <summary>
/// Bandaging a teammate. With a bandage in the selected slot and a hurt, living teammate within
/// <see cref="BandageItem.TeammateHealReach"/> (and not behind a wall), the prompt chip reads "[E] HEAL NAME" and
/// Interact asks the server to use the bandage on them (NetworkPlayerInventory.TeammateHeal). The use-item binding
/// still bandages the player themself. Teammates are found by standing near them, not by aiming at them; when two
/// qualify, the one closer to where the camera is looking wins.
/// </summary>
public partial class PlayerController
{
    const string TeammateHealPromptPrefix = "Press E to Heal ";
    const float TeammateHealSightHeight = 1.1f;

    readonly RaycastHit[] _teammateHealSightHits = new RaycastHit[16];

    bool TryFindHealableTeammate(out PlayerHealth target)
    {
        target = null;
        if (!IsUsingNetworkedInventory
            || _networkPlayerInventory == null
            || _networkPlayerInventory.GetSlotItemTypeId(_networkPlayerInventory.SelectedSlotIndex) != GrabbableInventoryItem.TypeIdBandage
            || _playerHealth == null
            || _playerHealth.IsDead)
            return false;

        Transform cam = CameraTransformForFacing;
        Vector3 facing = cam != null ? cam.forward : transform.forward;
        facing.y = 0f;
        facing = facing.sqrMagnitude > 1e-6f ? facing.normalized : transform.forward;

        Vector3 feet = transform.position;
        float bestScore = float.MaxValue;
        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth candidate = players[i];
            if (candidate == null
                || candidate == _playerHealth
                || candidate.IsDead
                || candidate.CurrentHealth >= candidate.MaxHealth
                || !candidate.isActiveAndEnabled)
                continue;

            Vector3 other = candidate.transform.position;
            if (!BandageItem.IsWithinTeammateHealReach(feet, other) || !TeammateHealSightClear(candidate))
                continue;

            // Distance, plus up to a reach's worth of penalty for standing behind the camera.
            Vector3 flat = other - feet;
            flat.y = 0f;
            float distance = flat.magnitude;
            float alignment = distance > 0.01f ? Vector3.Dot(facing, flat / distance) : 1f;
            float score = distance + (1f - alignment) * 0.5f * BandageItem.TeammateHealReach;
            if (score < bestScore)
            {
                bestScore = score;
                target = candidate;
            }
        }

        return target != null;
    }

    /// <summary>Same cover rule as a melee swing: only the world blocks, never bodies.</summary>
    bool TeammateHealSightClear(PlayerHealth candidate)
    {
        Vector3 origin = transform.position + Vector3.up * TeammateHealSightHeight;
        Vector3 aim = candidate.transform.position + Vector3.up * TeammateHealSightHeight;
        Vector3 toTarget = aim - origin;
        float distance = toTarget.magnitude;
        if (distance <= 0.01f)
            return true;

        int hitCount = Physics.RaycastNonAlloc(
            origin,
            toTarget / distance,
            _teammateHealSightHits,
            distance,
            MeleeSightBlockMask,
            QueryTriggerInteraction.Ignore);
        bool blocked = false;
        Transform targetRoot = candidate.transform;
        for (int i = 0; i < hitCount; i++)
        {
            Transform hit = _teammateHealSightHits[i].transform;
            _teammateHealSightHits[i] = default;
            if (blocked || hit == null || hit.IsChildOf(transform) || hit.IsChildOf(targetRoot))
                continue;

            blocked = true;
        }

        return !blocked;
    }

    bool TryShowTeammateHealPrompt()
    {
        if (!TryFindHealableTeammate(out PlayerHealth target))
            return false;

        SetPickupPromptVisible(true, TeammateHealPromptPrefix + PlayerIdentity.DisplayName(target));
        return true;
    }

    bool TryHealTeammateFromInteract()
    {
        if (!TryFindHealableTeammate(out PlayerHealth target))
            return false;

        _networkPlayerInventory.RequestHealTeammateWithSelectedBandage(target);
        return true;
    }
}
