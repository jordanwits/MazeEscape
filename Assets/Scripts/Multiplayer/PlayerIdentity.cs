using System.Collections.Generic;
using Unity.Netcode;

/// <summary>
/// Who an avatar belongs to, as the crew roster knows them: the display name and the lobby character slot of
/// its owning client. Read from <see cref="MultiplayerSessionController.LobbyPlayers"/>, which every peer keeps
/// for the whole session, so any peer can label any player.
/// </summary>
public static class PlayerIdentity
{
    /// <summary>Upper-cased roster name; the roster's placeholder for a known client; the object name offline.</summary>
    public static string DisplayName(PlayerHealth player)
    {
        if (player == null)
            return string.Empty;

        if (TryGetOwner(player, out ulong owner))
        {
            return TryGetLobbyPlayer(owner, out LobbyPlayerState state)
                ? state.DisplayName.ToUpperInvariant()
                : MultiplayerSessionController.FallbackDisplayName(owner);
        }

        return player.name.ToUpperInvariant();
    }

    /// <summary>Lobby character slot (one owner per slot), or -1 when there is no session or no roster row.</summary>
    public static int CharacterIndex(PlayerHealth player)
    {
        return player != null && TryGetOwner(player, out ulong owner) && TryGetLobbyPlayer(owner, out LobbyPlayerState state)
            ? state.CharacterIndex
            : -1;
    }

    static bool TryGetOwner(PlayerHealth player, out ulong owner)
    {
        owner = 0;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening
            || !player.TryGetComponent(out NetworkObject networkObject) || !networkObject.IsSpawned)
            return false;

        owner = networkObject.OwnerClientId;
        return true;
    }

    static bool TryGetLobbyPlayer(ulong clientId, out LobbyPlayerState state)
    {
        state = default;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.TryGetComponent(out MultiplayerSessionController session))
            return false;

        IReadOnlyList<LobbyPlayerState> lobby = session.LobbyPlayers;
        for (int i = 0; i < lobby.Count; i++)
        {
            if (lobby[i].ClientId == clientId)
            {
                state = lobby[i];
                return true;
            }
        }

        return false;
    }
}
