using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-authoritative teammate heal: the owner, holding a bandage next to a hurt living teammate, presses Interact
/// (see PlayerController.TeammateHeal) and the server wraps that teammate instead of the user. Same bandage
/// resolution and consume path as the self-heal, so the slot, the tombstone and the wrap SFX behave identically.
/// The teammate's new health replicates through their own <see cref="NetworkPlayerRespawn"/> health variable.
///
/// The client picks the target; the server re-checks that it is a different, living, damaged player within reach
/// of its own copy of both avatars (with slack for owner-transform interpolation), so a forged id cannot heal
/// across the level.
/// </summary>
public partial class NetworkPlayerInventory
{
    /// <summary>Extra reach the server allows over <see cref="BandageItem.TeammateHealReach"/>: both avatars are interpolated copies.</summary>
    const float TeammateHealServerSlack = 1.25f;

    /// <summary>Owner-side request to use the selected bandage on the teammate whose avatar is <paramref name="target"/>.</summary>
    public void RequestHealTeammateWithSelectedBandage(PlayerHealth target)
    {
        if (!IsSpawned || target == null || !target.TryGetComponent(out NetworkObject targetObject) || !targetObject.IsSpawned)
            return;

        if (IsServer)
        {
            ServerHealTeammateWithSelectedBandage(targetObject.NetworkObjectId);
            return;
        }

        RequestHealTeammateServerRpc(targetObject.NetworkObjectId);
    }

    [ServerRpc]
    void RequestHealTeammateServerRpc(ulong targetNetworkObjectId, ServerRpcParams serverRpcParams = default)
    {
        if (serverRpcParams.Receive.SenderClientId != OwnerClientId)
            return;

        ServerHealTeammateWithSelectedBandage(targetNetworkObjectId);
    }

    void ServerHealTeammateWithSelectedBandage(ulong targetNetworkObjectId)
    {
        if (!IsServer || NetworkManager == null)
            return;

        PlayerHealth self = playerController != null
            ? playerController.GetComponent<PlayerHealth>()
            : GetComponent<PlayerHealth>();
        if (self == null || self.IsDead)
            return;

        if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkObjectId, out NetworkObject targetObject)
            || targetObject == null
            || !targetObject.TryGetComponent(out PlayerHealth target)
            || target == self
            || target.IsDead
            || target.CurrentHealth >= target.MaxHealth)
            return;

        if (!BandageItem.IsWithinTeammateHealReach(self.transform.position, target.transform.position, TeammateHealServerSlack))
            return;

        if (!ServerTryResolveSelectedBandage(out int sel, out GrabbableInventoryItem g))
            return;

        target.Heal(BandageItem.HealthRestoreAmount);
        ServerConsumeSelectedBandage(sel, g);
        AchievementAwards.ServerAward(OwnerClientId, AchievementId.FieldMedic);
    }
}
