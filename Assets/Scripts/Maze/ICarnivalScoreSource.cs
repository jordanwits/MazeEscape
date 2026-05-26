/// <summary>
/// Replicated read-side state a carnival minigame controller exposes so a
/// <see cref="CarnivalWorldNumberDisplay"/> can drive its world-space TIME / SCORE labels without
/// caring which game it is. Implemented by <see cref="BasketballGameController"/> and
/// <see cref="RingTossGameController"/>.
/// </summary>
public interface ICarnivalScoreSource
{
    /// <summary>True while a round is running.</summary>
    bool IsActive { get; }

    /// <summary>Live score during the active round (may be 0 for games that only tally on resolve).</summary>
    int Score { get; }

    /// <summary>Score of the most recently finished round; shown once a round ends.</summary>
    int LastFinishedScore { get; }

    /// <summary>Seconds left for timed games; games without a countdown return 0.</summary>
    float TimeRemaining { get; }
}
