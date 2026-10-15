using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Server-side achievement entry points that gameplay code calls at the moment something happens (an enemy dies, a
/// prisoner is freed, an AI locks onto a player). Each translates the event into awards or run bookkeeping
/// (<see cref="AchievementRunTracker"/>); gameplay never decides achievements itself. Every method is a no-op on a
/// pure client, so call sites need no authority checks of their own.
/// </summary>
public static class AchievementHooks
{
    static readonly HashSet<GameObject> s_ReplacementHunters = new();
    static ulong? s_PendingJailUnlocker;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        s_ReplacementHunters.Clear();
        s_PendingJailUnlocker = null;
        ServerCurrentDecoyThrower = null;
    }

    // ---- Enemies -----------------------------------------------------------------------------------------

    /// <summary>A player's damage landed on an enemy (melee, flare impact or burn). Breaks that player's Pacifist run.</summary>
    public static void ServerEnemyHit(PlayerHealth attacker)
    {
        if (AchievementRunTracker.IsAuthority && AchievementAwards.TryGetOwner(attacker, out ulong clientId))
            AchievementRunTracker.ServerNoteEnemyHit(clientId);
    }

    /// <summary>The lethal hit, called just before the enemy's Die() (which no longer knows who struck it).</summary>
    public static void ServerEnemyKilled(EnemyKind kind, PlayerHealth killer, Component enemy)
    {
        if (!AchievementRunTracker.IsAuthority)
            return;

        // The boss is a team fight: everyone gets him, whoever lands the last hit (or if a flare burn does).
        if (kind == EnemyKind.Mutant)
        {
            AchievementAwards.ServerAwardAll(AchievementId.TheFinalBoss);
            return;
        }

        if (killer == null)
            return;

        switch (kind)
        {
            case EnemyKind.Skeleton:
                AchievementAwards.ServerAddProgress(killer, AchievementId.BoneCollector, 1);
                break;
            case EnemyKind.Clown:
                AchievementAwards.ServerAward(killer, AchievementId.SendInTheClowns);
                if (enemy != null && s_ReplacementHunters.Contains(enemy.gameObject))
                    AchievementAwards.ServerAward(killer, AchievementId.Encore);
                break;
            case EnemyKind.SecurityGuard:
                AchievementAwards.ServerAward(killer, AchievementId.SecurityBreach);
                break;
        }
    }

    /// <summary>A hunter spawned to replace a killed one (ProceduralMazeCoordinator.HunterRespawn).</summary>
    public static void ServerNoteReplacementHunter(GameObject hunter)
    {
        if (hunter == null)
            return;

        s_ReplacementHunters.RemoveWhere(h => h == null);
        s_ReplacementHunters.Add(hunter);
    }

    /// <summary>A zombie or skeleton's poise broke into a stagger from this player's hit.</summary>
    public static void ServerPoiseBroken(PlayerHealth attacker)
    {
        if (AchievementRunTracker.IsAuthority && attacker != null)
            AchievementAwards.ServerAward(attacker, AchievementId.PoiseBreaker);
    }

    /// <summary>An enemy locked onto <paramref name="target"/> to chase or stalk them. Breaks that player's Ghost run.</summary>
    public static void ServerEnemyTargeted(PlayerHealth target)
    {
        if (AchievementRunTracker.IsAuthority && AchievementAwards.TryGetOwner(target, out ulong clientId))
            AchievementRunTracker.ServerNoteChased(clientId);
    }

    // ---- Lures ------------------------------------------------------------------------------------------

    const float MonkeyPullTeammateRadius = 20f;

    /// <summary>The decoy grenade whose pulse is calling enemies right now: its thrower, for the lured enemy to remember.</summary>
    public static PlayerHealth ServerCurrentDecoyThrower { get; private set; }

    public static void ServerBeginDecoyLure(PlayerHealth thrower) => ServerCurrentDecoyThrower = thrower;

    public static void ServerEndDecoyLure() => ServerCurrentDecoyThrower = null;

    /// <summary>
    /// A wind-up monkey's clap just pulled the Clown onto a fresh lure. A clap cannot break a live chase, so
    /// "pulling him off a teammate" means: someone other than whoever wound the monkey is near the Clown when he
    /// turns away. Credit goes to the player who wound it up.
    /// </summary>
    public static void ServerMonkeyPulledClown(PlayerHealth monkeyActivator, Vector3 clownPosition)
    {
        if (!AchievementRunTracker.IsAuthority || monkeyActivator == null || monkeyActivator.IsDead)
            return;

        var players = PlayerHealthRegistry.All;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || player == monkeyActivator || player.IsDead)
                continue;

            if ((player.transform.position - clownPosition).sqrMagnitude <= MonkeyPullTeammateRadius * MonkeyPullTeammateRadius)
            {
                AchievementAwards.ServerAward(monkeyActivator, AchievementId.MonkeyBusiness);
                return;
            }
        }
    }

    // ---- Jail --------------------------------------------------------------------------------------------

    /// <summary>
    /// Brackets a key unlock (or a skeleton-match win) so the jail zone, which hears about the release synchronously
    /// inside it, can credit whoever opened the cell.
    /// </summary>
    public static void ServerBeginJailUnlock(ulong unlockerClientId) => s_PendingJailUnlocker = unlockerClientId;

    public static void ServerEndJailUnlock() => s_PendingJailUnlocker = null;

    /// <summary>A cell released these prisoners. Jailbreak goes to the unlocker when they freed someone other than themselves.</summary>
    public static void ServerJailPrisonersFreed(List<ulong> freedClientIds)
    {
        if (!AchievementRunTracker.IsAuthority || !s_PendingJailUnlocker.HasValue || freedClientIds == null)
            return;

        ulong unlocker = s_PendingJailUnlocker.Value;
        for (int i = 0; i < freedClientIds.Count; i++)
        {
            if (freedClientIds[i] != unlocker)
            {
                AchievementAwards.ServerAward(unlocker, AchievementId.Jailbreak);
                return;
            }
        }
    }

    // ---- Players -----------------------------------------------------------------------------------------

    public static void ServerPlayerDied(ulong clientId) => AchievementRunTracker.ServerNotePlayerDied(clientId);

    /// <summary>The game-over card is going up on every peer: everyone gets Wipe, or No Way Out for a jail lock-in.</summary>
    public static void ServerTeamWipe(bool everyoneDead)
    {
        if (AchievementRunTracker.IsAuthority)
            AchievementAwards.ServerAwardAll(everyoneDead ? AchievementId.Wipe : AchievementId.NoWayOut);
    }
}
