using UnityEngine;

/// <summary>
/// Maps blackjack card bytes (see <see cref="BlackjackCard"/>) to Kenney card sprites. The sprites aren't in a
/// Resources folder, so this asset holds direct references; populate it via the editor tool
/// (Tools ▸ Blackjack ▸ Populate Card Sprites). Assigned to the table's <see cref="BlackjackTableView"/>.
/// </summary>
[CreateAssetMenu(menuName = "Blackjack/Card Sprites", fileName = "BlackjackCardSprites")]
public sealed class BlackjackCardSprites : ScriptableObject
{
    [Tooltip("52 sprites indexed by the card byte (suit*13 + rankIndex).")]
    public Sprite[] cards = new Sprite[BlackjackCard.DeckSize];
    public Sprite back;
    public Sprite empty;

    /// <summary>Sprite for a card byte, or null for <see cref="BlackjackCard.None"/> / out of range.</summary>
    public Sprite Get(byte card)
    {
        if (card == BlackjackCard.None || cards == null || card >= cards.Length)
            return null;
        return cards[card];
    }
}
