using System;

/// <summary>
/// Stateless blackjack card helpers. A card is encoded as a byte <c>0..51 = suit*13 + rankIndex</c>.
/// <see cref="None"/> (255) means "no card" / empty slot.
/// Suits: 0 clubs, 1 diamonds, 2 hearts, 3 spades.
/// Rank index 0..12 maps to 2,3,4,5,6,7,8,9,10,J,Q,K,A.
/// Pure C# (no Unity dependencies) so the evaluation logic is edit-mode testable.
/// </summary>
public static class BlackjackCard
{
    public const byte None = 255;
    public const int DeckSize = 52;

    public static int Suit(byte card) => card / 13;
    public static int RankIndex(byte card) => card % 13;
    public static bool IsAce(byte card) => card != None && card % 13 == 12;

    /// <summary>Blackjack value of a single card: 2..10 for pips, 10 for J/Q/K, 11 for an Ace (soft; reduced in <see cref="Evaluate"/>).</summary>
    public static int CardValue(byte card)
    {
        int r = card % 13;
        if (r <= 8) return r + 2;   // 2..10
        if (r <= 11) return 10;     // J, Q, K
        return 11;                  // Ace
    }

    /// <summary>
    /// Evaluates a hand. <paramref name="total"/> is the best total (aces drop from 11 to 1 to avoid busting).
    /// <paramref name="isSoft"/> is true when an ace still counts as 11 in the final total. <paramref name="isBlackjack"/>
    /// is true only for a natural: exactly two cards totalling 21.
    /// </summary>
    public static void Evaluate(byte[] cards, int count, out int total, out bool isSoft, out bool isBlackjack)
    {
        total = 0;
        int aces = 0;
        int realCount = 0;
        for (int i = 0; i < count; i++)
        {
            byte c = cards[i];
            if (c == None)
                continue;
            total += CardValue(c);
            if (IsAce(c))
                aces++;
            realCount++;
        }

        // Reduce aces from 11 to 1 while the hand would bust.
        int softAces = aces;
        while (total > 21 && softAces > 0)
        {
            total -= 10;
            softAces--;
        }

        isSoft = softAces > 0 && total <= 21;
        isBlackjack = realCount == 2 && total == 21;
    }

    /// <summary>Convenience overload for the small fixed hand buffers used by the controller.</summary>
    public static int HandTotal(byte[] cards, int count)
    {
        Evaluate(cards, count, out int total, out _, out _);
        return total;
    }

    // --- Sprite-name helpers (match the Kenney pack file names: card_<suit>_<rank>). ---

    public static string SuitName(int suit) => suit switch
    {
        0 => "clubs",
        1 => "diamonds",
        2 => "hearts",
        3 => "spades",
        _ => "clubs",
    };

    /// <summary>Rank token used in the sprite file name: "02".."10", "J", "Q", "K", "A".</summary>
    public static string RankToken(int rankIndex) => rankIndex switch
    {
        9 => "J",
        10 => "Q",
        11 => "K",
        12 => "A",
        _ => (rankIndex + 2).ToString("00"), // 0 -> "02" ... 8 -> "10"
    };

    /// <summary>The Kenney sprite name for a card byte, e.g. <c>card_hearts_A</c>, <c>card_spades_10</c>.</summary>
    public static string SpriteName(byte card) => $"card_{SuitName(Suit(card))}_{RankToken(RankIndex(card))}";
}
