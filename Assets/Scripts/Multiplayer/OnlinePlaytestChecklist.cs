/// <summary>
/// The run-through the BRIEFING screen lists, in order. Kept platform-neutral: joining is
/// invite-only, so there is no id, address, or port for anyone to read out.
/// </summary>
public static class OnlinePlaytestChecklist
{
    public static readonly string[] Steps =
    {
        "Two Windows machines, two different accounts, the same build.",
        "Sign in and let the game reach the menu before inviting anyone.",
        "Host opens a game from Menu > Play > Host Game.",
        "Host picks Invite Friends in the lobby and invites everyone by name.",
        "Friends accept the invite and land in the lobby with no ids to type.",
        "Every player toggles Ready, then only the host can press Start.",
        "All players load into Level01 together and spawn at valid start positions.",
        "Maze layout, doors, keys, chests, traps, and finish trigger match for both players.",
        "Flashlight pickup, battery, toggle, drop, and remote light visuals sync.",
        "Glowstick and key inventory pickup/drop/consume behavior syncs.",
        "Zombies move only from server authority and replicate health/death/animation.",
        "Player death, ragdoll, item drop, respawn, and HUD recovery work for host and client.",
        "Client can leave and return to menu; host shutdown disconnects client cleanly.",
        "Play Offline still loads Level01 solo with the network unreachable from outside."
    };
}
