public static class OnlinePlaytestChecklist
{
    public static readonly string[] Steps =
    {
        "Two Windows machines use different Steam accounts and the same build.",
        "Steam is running before the game starts; Steam status shows ready in F8.",
        "Host starts a Steam lobby from Menu > F8 > Host Steam Lobby.",
        "Friend joins through Steam invite, lobby ID, or host SteamID64.",
        "Every player toggles Ready in the lobby, then only the host can press Start Game.",
        "All players load into Level01 together and spawn at valid start positions.",
        "Maze layout, doors, keys, chests, traps, and finish trigger match for both players.",
        "Flashlight pickup, battery, toggle, drop, and remote light visuals sync.",
        "Glowstick and key inventory pickup/drop/consume behavior syncs.",
        "Zombies move only from server authority and replicate health/death/animation.",
        "Player death, ragdoll, item drop, respawn, and HUD recovery work for host and client.",
        "Client can leave and return to menu; host shutdown disconnects client cleanly.",
        "Direct IP mode still hosts and joins on LAN for transport fallback debugging."
    };
}
