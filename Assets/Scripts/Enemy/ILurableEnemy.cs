using UnityEngine;

/// <summary>
/// An enemy that can be pulled toward a noise it hears — implemented by the three hunters a
/// <see cref="DecoyGrenade"/> is meant to bait away from the player (Clown, Jailor, Security Guard).
///
/// Deliberately separate from <see cref="IBlindableEnemy"/>: a flashbang is a thing that happens TO an
/// enemy and is applied as a bolt-on component, whereas a lure is a request the AI is free to refuse.
/// The AI keeps ownership of its own state machine here — the decoy asks, it decides.
/// </summary>
public interface ILurableEnemy
{
    /// <summary>
    /// True while this enemy is actively hunting a player (chasing, swinging, grabbing, hauling one to
    /// jail). A decoy must never pull an enemy off a live pursuit — that would turn a distraction tool
    /// into a get-out-of-jail-free card, and the whole point is to bait a hunter that has not found you
    /// yet, or has just lost you.
    /// </summary>
    bool IsPursuingPlayer { get; }

    /// <summary>Where this enemy currently is, for the earshot test.</summary>
    Vector3 LureListenPosition { get; }

    /// <summary>
    /// Heard something at <paramref name="worldPoint"/> — go look. Implementations run this server-side
    /// only and are expected to re-check their own state; repeated calls to the same point should refresh
    /// interest rather than restart the approach from scratch.
    /// </summary>
    void LureToNoise(Vector3 worldPoint);
}
