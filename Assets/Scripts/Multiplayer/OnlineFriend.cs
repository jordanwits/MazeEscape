/// <summary>
/// One row of the invite list: a friend on the local player's platform friends list who is
/// currently logged in. Kept free of any platform types so the menu layer can render the list
/// without referencing the networking SDK.
/// </summary>
public readonly struct OnlineFriend
{
    public OnlineFriend(ulong userId, string name, bool inThisGame)
    {
        UserId = userId;
        Name = name;
        InThisGame = inThisGame;
    }

    public ulong UserId { get; }
    public string Name { get; }

    /// <summary>True when the friend already has this game open, so the invite pops up for them instantly.</summary>
    public bool InThisGame { get; }
}
