using Unity.Netcode;

/// <summary>
/// Server-side entry point for achievements. Gameplay is server-authoritative, so the server is where almost every
/// achievement is decided; a Steam achievement, though, can only be unlocked by the earning player's own game. These
/// calls route each award to the right client: the host's own awards unlock in place, a remote client's travel as a
/// named message (<see cref="AchievementNetworkRelay"/>) — not an RPC on the avatar, because many awards land right
/// as a section ends and the avatars are torn down. With no session running (offline dev scenes) the one local
/// player gets it directly.
///
/// Achievements decided on the owning client (entering a room, dodging an owner-detected laser) call
/// <see cref="Achievements.Unlock"/> directly instead.
/// </summary>
public static class AchievementAwards
{
    static bool SessionRunning(out NetworkManager nm)
    {
        nm = NetworkManager.Singleton;
        return nm != null && nm.IsListening;
    }

    public static void ServerAward(ulong clientId, AchievementId id)
    {
        if (!SessionRunning(out NetworkManager nm))
        {
            Achievements.Unlock(id);
            return;
        }

        if (!nm.IsServer)
            return;

        if (clientId == nm.LocalClientId)
            Achievements.Unlock(id);
        else
            AchievementNetworkRelay.ServerSend(nm, clientId, AchievementNetworkRelay.KindUnlock, id, 0);
    }

    public static void ServerAward(PlayerHealth player, AchievementId id)
    {
        if (TryGetOwner(player, out ulong clientId))
            ServerAward(clientId, id);
    }

    /// <summary>Every connected client — team moments (a wipe) where dead and living players alike earn it.</summary>
    public static void ServerAwardAll(AchievementId id)
    {
        if (!SessionRunning(out NetworkManager nm))
        {
            Achievements.Unlock(id);
            return;
        }

        if (!nm.IsServer)
            return;

        foreach (ulong clientId in nm.ConnectedClientsIds)
            ServerAward(clientId, id);
    }

    public static void ServerAddProgress(ulong clientId, AchievementId id, int amount)
    {
        if (amount <= 0)
            return;

        if (!SessionRunning(out NetworkManager nm))
        {
            Achievements.AddProgress(id, amount);
            return;
        }

        if (!nm.IsServer)
            return;

        if (clientId == nm.LocalClientId)
            Achievements.AddProgress(id, amount);
        else
            AchievementNetworkRelay.ServerSend(nm, clientId, AchievementNetworkRelay.KindProgress, id, amount);
    }

    public static void ServerAddProgress(PlayerHealth player, AchievementId id, int amount)
    {
        if (TryGetOwner(player, out ulong clientId))
            ServerAddProgress(clientId, id, amount);
    }

    /// <summary>The owner of a spawned player object, by id (e.g. a blackjack seat's occupant).</summary>
    public static bool TryGetOwnerOfNetworkObject(ulong networkObjectId, out ulong clientId)
    {
        clientId = 0;
        if (!SessionRunning(out NetworkManager nm))
            return true;

        if (!nm.IsServer
            || !nm.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out NetworkObject networkObject)
            || networkObject == null)
            return false;

        clientId = networkObject.OwnerClientId;
        return true;
    }

    /// <summary>
    /// The client that owns <paramref name="player"/>'s avatar. Offline there is no owner, but the only player is the
    /// local one, so it reports the local client id (0) and the award still lands.
    /// </summary>
    public static bool TryGetOwner(PlayerHealth player, out ulong clientId)
    {
        clientId = 0;
        if (player == null)
            return false;

        if (!SessionRunning(out _))
            return true;

        if (!player.TryGetComponent(out NetworkObject networkObject) || !networkObject.IsSpawned)
            return false;

        clientId = networkObject.OwnerClientId;
        return true;
    }
}
