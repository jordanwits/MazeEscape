using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>Server-driven round phase for a blackjack table. Replicated to clients for UI.</summary>
public enum BlackjackPhase : byte
{
    Idle,         // no occupants
    Betting,      // occupants adjust bet + ready up
    Dealing,      // transient: stake bets + deal cards
    PlayerTurns,  // acting seat hits/stands
    DealerTurn,   // dealer reveals hole + draws
    Resolve,      // outcomes computed
    Payout,       // winnings credited, results held briefly
}

/// <summary>Per-seat hand status during a round.</summary>
public enum BlackjackHandStatus : byte
{
    Empty,      // not in the round (no hand)
    Playing,    // may still hit
    Stand,      // chose to stand
    Bust,       // over 21
    Blackjack,  // natural 21 (locked, paid 3:2)
    Forfeit,    // left / disconnected mid-round
}

/// <summary>Outcome of a seat versus the dealer, set at resolve. Drives the overlay result line.</summary>
public enum BlackjackSeatResult : byte
{
    None,
    Win,
    Lose,
    Push,
    Blackjack,
    Bust,
    Forfeit,
}

/// <summary>Shared tuning constants for the blackjack table.</summary>
public static class BlackjackConfig
{
    public const int SeatCount = 4;       // hard upper bound on seats authored on the prefab
    public const int MaxHandCards = 12;   // practical cap; a hand is auto-stood if it somehow reaches this
    public const int MinBet = 5;
    public const int BetStep = 5;
    public const int DeckCount = 6;       // 6-deck shoe (real-casino odds)
}

/// <summary>
/// Replicated state for a single seat. Memcpy-serialized (unmanaged, blittable). The hand cards live in a
/// <see cref="FixedList32Bytes{T}"/> (capacity well above <see cref="BlackjackConfig.MaxHandCards"/>), encoded
/// per <see cref="BlackjackCard"/>. Server is the only writer. <see cref="IEquatable{T}"/> covers every field
/// so NGO suppresses redundant replication.
/// </summary>
public struct SeatState : INetworkSerializeByMemcpy, IEquatable<SeatState>
{
    public ulong OccupantNetObjId;   // 0 = empty seat
    public int Bet;                  // multiples of BetStep; >= MinBet once ready
    public byte IsReady;             // 0/1
    public byte InRound;             // 0/1 — dealt into the current round
    public byte Status;              // BlackjackHandStatus
    public byte LastResult;          // BlackjackSeatResult (set at resolve)
    public int LastPayout;           // net tickets delta (credited - bet); for the overlay result line
    public FixedList32Bytes<byte> Cards;

    public bool IsOccupied => OccupantNetObjId != 0UL;
    public int CardCount => Cards.Length;

    public bool Equals(SeatState o) =>
        OccupantNetObjId == o.OccupantNetObjId
        && Bet == o.Bet
        && IsReady == o.IsReady
        && InRound == o.InRound
        && Status == o.Status
        && LastResult == o.LastResult
        && LastPayout == o.LastPayout
        && Cards.Equals(o.Cards);

    public override bool Equals(object o) => o is SeatState s && Equals(s);

    public override int GetHashCode() =>
        OccupantNetObjId.GetHashCode() ^ Bet ^ (Status << 8) ^ (Cards.Length << 16) ^ LastPayout;
}

/// <summary>
/// Replicated dealer state. <see cref="Cards"/> holds only the VISIBLE cards: during player turns it carries the
/// single up-card and <see cref="HoleHidden"/> is 1 (clients render one face-down placeholder); on reveal the
/// server rebuilds <see cref="Cards"/> with the full hand and clears <see cref="HoleHidden"/>. The real hole card
/// never leaves the server until then.
/// </summary>
public struct DealerState : INetworkSerializeByMemcpy, IEquatable<DealerState>
{
    public FixedList32Bytes<byte> Cards;  // visible cards only
    public byte HoleHidden;               // 1 while a face-down hole card exists but is not yet in Cards
    public byte Status;                   // BlackjackHandStatus (Playing/Stand/Bust/Blackjack)

    public int VisibleCount => Cards.Length;

    public bool Equals(DealerState o) =>
        HoleHidden == o.HoleHidden && Status == o.Status && Cards.Equals(o.Cards);

    public override bool Equals(object o) => o is DealerState s && Equals(s);

    public override int GetHashCode() => (HoleHidden << 1) ^ (Status << 8) ^ (Cards.Length << 16);
}
