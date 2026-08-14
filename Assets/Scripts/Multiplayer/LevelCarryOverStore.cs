using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Carries per-player run state — health and the hotbar — across an elevator section switch.
///
/// A section switch rebuilds the world from scratch: player objects are spawned with
/// <c>destroyWithScene = true</c>, so every peer destroys its copy of the avatar (and with it every held
/// item, which is parented under the avatar) when the <see cref="UnityEngine.SceneManagement.LoadSceneMode.Single"/>
/// load tears the old scene down. The fresh avatar spawned in the next section therefore started at full
/// health with three empty slots.
///
/// Two halves fix that:
/// <list type="bullet">
/// <item>The <b>server</b> snapshots health + slot state per client id here
/// (<see cref="ServerCaptureAllPlayers"/>) just before the load, and
/// <see cref="MultiplayerSessionController"/> re-applies it to the newly spawned avatar.</item>
/// <item><b>Every peer</b> (server and clients alike, via the elevator's broadcast) detaches its local copies
/// of the carried item objects out of the doomed avatar hierarchy and into the
/// <see cref="LevelCarryOverPen"/> — a DontDestroyOnLoad root — so the very same
/// <see cref="GrabbableInventoryItem"/> instances survive, keeping their ids, flashlight battery and
/// glowstick stacks. Re-attachment is the normal replicated inventory path: restoring the slot
/// NetworkVariables makes each peer's <c>RefreshInventoryViewFromNetwork</c> resolve the item by id and
/// seat it on the new avatar.</item>
/// </list>
/// Items are NOT re-created from prefabs on the far side, so nothing here needs a per-type prefab table and
/// no item state is approximated.
/// </summary>
public static class LevelCarryOverStore
{
    /// <summary>
    /// Snapshot width — the maximum hotbar, not the base three, so a player who bought the 4th slot at the
    /// carnival counter carries its contents into the next section along with the unlock itself.
    /// </summary>
    public const int SlotCount = NetworkPlayerInventory.MaxSlotCount;

    public struct SlotState
    {
        public ulong ItemId;
        public byte TypeId;
        public byte StackCount;

        /// <summary>
        /// Non-zero when this slot held a network-spawned item (Flashlight, JailorKey — prefabs with a
        /// NetworkObject). Those cannot ride along in the pen: they are level NetworkObjects and the section
        /// switch despawns them on every peer. They are re-spawned from this prefab hash on the far side
        /// instead, which is why <see cref="FlashlightBatteryNormalized"/> travels with them.
        /// </summary>
        public uint NetworkPrefabHash;

        /// <summary>0…1 remaining battery, only meaningful for a flashlight slot.</summary>
        public float FlashlightBatteryNormalized;
    }

    public struct PlayerState
    {
        public bool HasValue;
        public float Health;
        public byte SelectedSlot;
        public bool FlashlightLightOn;
        /// <summary>Whether this player bought the 4th hotbar slot — the upgrade is for the whole run.</summary>
        public bool HasExtraSlot;
        public SlotState Slot0;
        public SlotState Slot1;
        public SlotState Slot2;
        public SlotState Slot3;

        public SlotState GetSlot(int index)
        {
            if (index == 0) return Slot0;
            if (index == 1) return Slot1;
            if (index == 2) return Slot2;
            return Slot3;
        }

        public void SetSlot(int index, SlotState value)
        {
            if (index == 0) Slot0 = value;
            else if (index == 1) Slot1 = value;
            else if (index == 2) Slot2 = value;
            else Slot3 = value;
        }
    }

    static readonly Dictionary<ulong, PlayerState> ServerStateByClientId = new();

    /// <summary>
    /// Server-only. Snapshots every connected player's health and hotbar. Called immediately before the
    /// synchronized level load, while the avatars (and their NetworkVariables) still exist. Stale entries are
    /// dropped so a client that left mid-run cannot resurrect an old loadout on a later reconnect.
    /// </summary>
    public static void ServerCaptureAllPlayers()
    {
        ServerStateByClientId.Clear();

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || !nm.IsServer)
            return;

        foreach (KeyValuePair<ulong, NetworkClient> pair in nm.ConnectedClients)
        {
            NetworkObject playerObject = pair.Value != null ? pair.Value.PlayerObject : null;
            if (playerObject == null)
                continue;

            ServerStateByClientId[pair.Key] = CaptureFromPlayerObject(playerObject);
        }
    }

    static PlayerState CaptureFromPlayerObject(NetworkObject playerObject)
    {
        PlayerState state = new PlayerState { HasValue = true };

        PlayerHealth health = playerObject.GetComponent<PlayerHealth>();
        if (health != null)
        {
            // A dead player is mid-respawn and will come back at full health anyway; never carry a corpse
            // (0 HP) into the next section, where nothing would revive them.
            state.Health = health.IsDead ? health.MaxHealth : health.CurrentHealth;
        }

        NetworkPlayerInventory inventory = playerObject.GetComponent<NetworkPlayerInventory>();
        if (inventory == null || !inventory.IsSpawned)
            return state;

        state.SelectedSlot = (byte)Mathf.Clamp(inventory.SelectedSlotIndex, 0, SlotCount - 1);
        state.FlashlightLightOn = inventory.SelectedFlashlightLightOn;
        state.HasExtraSlot = inventory.HasExtraSlot;
        for (int i = 0; i < SlotCount; i++)
        {
            SlotState slot = new SlotState
            {
                ItemId = inventory.GetSlotItemId(i),
                TypeId = inventory.GetSlotItemTypeId(i),
                StackCount = (byte)Mathf.Clamp(inventory.GetSlotStackCount(i), 0, byte.MaxValue)
            };

            if (slot.ItemId != 0UL
                && GrabbableInventoryItem.TryGetRegistered(slot.ItemId, out GrabbableInventoryItem item)
                && item != null)
            {
                // Network-spawned items die with the section (see SlotState.NetworkPrefabHash); record what is
                // needed to build an identical one in the next section.
                if (item.TryGetSpawnedNetworkPrefabHash(out uint prefabHash))
                    slot.NetworkPrefabHash = prefabHash;

                slot.FlashlightBatteryNormalized = item is FlashlightItem flashlight
                    ? flashlight.BatteryFractionNormalized
                    : 0f;
            }

            state.SetSlot(i, slot);
        }

        return state;
    }

    /// <summary>
    /// Looks a registered network prefab up by its <see cref="NetworkObject.PrefabIdHash"/> so a carried
    /// network item can be re-spawned in the next section from the exact prefab it came from — independent of
    /// whether that section's maze config happens to list it (the flashlight is not in every level's spawn pool).
    /// </summary>
    public static GameObject FindRegisteredNetworkPrefabByHash(uint prefabHash)
    {
        if (prefabHash == 0)
            return null;

        NetworkManager nm = NetworkManager.Singleton;
        NetworkConfig config = nm != null ? nm.NetworkConfig : null;
        if (config == null || config.Prefabs == null || config.Prefabs.NetworkPrefabsLists == null)
            return null;

        foreach (NetworkPrefabsList list in config.Prefabs.NetworkPrefabsLists)
        {
            if (list == null || list.PrefabList == null)
                continue;

            foreach (NetworkPrefab entry in list.PrefabList)
            {
                if (entry == null || entry.Prefab == null)
                    continue;

                if (entry.Prefab.TryGetComponent(out NetworkObject networkObject)
                    && networkObject.PrefabIdHash == prefabHash)
                {
                    return entry.Prefab;
                }
            }
        }

        return null;
    }

    public static bool TryGetState(ulong clientId, out PlayerState state)
    {
        return ServerStateByClientId.TryGetValue(clientId, out state) && state.HasValue;
    }

    /// <summary>Drops a client's snapshot once it has been applied to their new avatar (one restore per switch).</summary>
    public static void ConsumeState(ulong clientId)
    {
        ServerStateByClientId.Remove(clientId);
    }

    public static void ClearAll()
    {
        ServerStateByClientId.Clear();
    }

    /// <summary>
    /// Runs on EVERY peer (host directly, clients via the elevator's ClientRpc) immediately before the
    /// synchronized load: moves the local copies of all hotbar items out of the avatars that are about to be
    /// destroyed and parks them in the <see cref="LevelCarryOverPen"/>. Must happen before
    /// <c>NetworkSceneManager.LoadScene</c> queues the player despawns, or the items go down with the avatar.
    /// </summary>
    public static void HoldCarriedItemsForLevelSwitch()
    {
        NetworkPlayerInventory[] inventories =
            Object.FindObjectsByType<NetworkPlayerInventory>(FindObjectsInactive.Include);

        Transform pen = null;
        for (int i = 0; i < inventories.Length; i++)
        {
            NetworkPlayerInventory inventory = inventories[i];
            if (inventory == null || !inventory.IsSpawned)
                continue;

            for (int slot = 0; slot < SlotCount; slot++)
            {
                ulong itemId = inventory.GetSlotItemId(slot);
                if (itemId == 0UL)
                    continue;

                if (!GrabbableInventoryItem.TryGetRegistered(itemId, out GrabbableInventoryItem item) || item == null)
                    continue;

                // Heavy throwables (StarBall / rings) are spawned NetworkObjects owned by the level; they are
                // despawned with the rest of the section and are not hotbar items to begin with.
                if (item.IsNetworkSpawnedItem)
                    continue;

                pen ??= LevelCarryOverPen.EnsureRoot();
                item.PrepareForLevelCarryOver(pen);
            }
        }
    }
}
