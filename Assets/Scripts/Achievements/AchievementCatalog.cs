using System.Collections.Generic;

/// <summary>
/// The display data for every <see cref="AchievementId"/>: the Steam API name (what the Steamworks partner site must
/// define, see Docs/Achievements.md), the title and description shown in the unlock toast, whether Steam should hide
/// it until unlocked, and — for lifetime-counter achievements — the Steam stat and the count that unlocks it.
/// </summary>
public static class AchievementCatalog
{
    public readonly struct Entry
    {
        public readonly AchievementId Id;
        public readonly string ApiName;
        public readonly string Title;
        public readonly string Description;
        public readonly bool Hidden;
        /// <summary>Steam stat name for a lifetime counter, or null for a one-shot achievement.</summary>
        public readonly string StatName;
        public readonly int ProgressTarget;

        public Entry(AchievementId id, string apiName, string title, string description, bool hidden = false, string statName = null, int progressTarget = 0)
        {
            Id = id;
            ApiName = apiName;
            Title = title;
            Description = description;
            Hidden = hidden;
            StatName = statName;
            ProgressTarget = progressTarget;
        }
    }

    static readonly Entry[] s_Entries =
    {
        new(AchievementId.Detour, "ACH_DETOUR", "Detour", "Escape the dungeon."),
        new(AchievementId.StepRightUp, "ACH_STEP_RIGHT_UP", "Step Right Up", "Escape the carnival."),
        new(AchievementId.ClockingOut, "ACH_CLOCKING_OUT", "Clocking Out", "Escape the offices and finish a run."),
        new(AchievementId.PerfectAttendance, "ACH_PERFECT_ATTENDANCE", "Perfect Attendance", "Finish a full run without anyone on the team dying."),
        new(AchievementId.LoneSurvivor, "ACH_LONE_SURVIVOR", "Lone Survivor", "Finish a full run solo."),
        new(AchievementId.FullCrew, "ACH_FULL_CREW", "Full Crew", "Finish a full run with four players."),

        new(AchievementId.RockBeatsBones, "ACH_ROCK_BEATS_BONES", "Rock Beats Bones", "Beat the jail skeleton at rock-paper-scissors."),
        new(AchievementId.Jailbreak, "ACH_JAILBREAK", "Jailbreak", "Free a jailed teammate."),
        new(AchievementId.SpecialDelivery, "ACH_SPECIAL_DELIVERY", "Special Delivery", "Get carried to the jail by the Jailor."),
        new(AchievementId.BoneCollector, "ACH_BONE_COLLECTOR", "Bone Collector", "Crumble 10 skeletons.", statName: "STAT_SKELETONS_CRUMBLED", progressTarget: 10),
        new(AchievementId.PoiseBreaker, "ACH_POISE_BREAKER", "Poise Breaker", "Stagger a zombie or a skeleton."),
        new(AchievementId.CloseShave, "ACH_CLOSE_SHAVE", "Close Shave", "Get through a swinging axe without being hit."),

        new(AchievementId.SendInTheClowns, "ACH_SEND_IN_THE_CLOWNS", "Send in the Clowns", "Kill the Clown."),
        new(AchievementId.Encore, "ACH_ENCORE", "Encore", "Kill the Clown that comes back."),
        new(AchievementId.WinnerWinner, "ACH_WINNER_WINNER", "Winner Winner", "Win a hand of blackjack."),
        new(AchievementId.Blackjack, "ACH_BLACKJACK", "Blackjack!", "Get a natural 21."),
        new(AchievementId.HighRoller, "ACH_HIGH_ROLLER", "High Roller", "Earn 50 tickets in a single run."),
        new(AchievementId.RetailTherapy, "ACH_RETAIL_THERAPY", "Retail Therapy", "Buy something from the prize shop."),
        new(AchievementId.MonkeyBusiness, "ACH_MONKEY_BUSINESS", "Monkey Business", "Pull the Clown off a teammate with the wind-up monkey."),
        new(AchievementId.DontBlink, "ACH_DONT_BLINK", "Don't Blink", "Walk past a RatBot without being caught."),
        new(AchievementId.WrongTarget, "ACH_WRONG_TARGET", "Wrong Target", "Lure a Bomber onto a decoy grenade and let it blow up there."),
        new(AchievementId.NothingButNet, "ACH_NOTHING_BUT_NET", "Nothing But Net", "Win the basketball booth."),

        new(AchievementId.Limbo, "ACH_LIMBO", "Limbo", "Make it through a laser sweep without being hit."),
        new(AchievementId.SecurityBreach, "ACH_SECURITY_BREACH", "Security Breach", "Kill a security guard."),
        new(AchievementId.TheFinalBoss, "ACH_THE_FINAL_BOSS", "The Final Boss", "Kill the Mutant."),
        new(AchievementId.DiscoInferno, "ACH_DISCO_INFERNO", "Disco Inferno", "Find the disco room."),
        new(AchievementId.TouchGrass, "ACH_TOUCH_GRASS", "Touch Grass", "Find the indoor park."),

        new(AchievementId.LookAway, "ACH_LOOK_AWAY", "Look Away", "Blind an enemy with a flashbang."),
        new(AchievementId.FriendlyFire, "ACH_FRIENDLY_FIRE", "Friendly Fire", "Blind a teammate with a flashbang.", hidden: true),
        new(AchievementId.SignalFire, "ACH_SIGNAL_FIRE", "Signal Fire", "Hit an enemy with a flare."),
        new(AchievementId.BladeRunner, "ACH_BLADE_RUNNER", "Blade Runner", "Kill an enemy with the sword."),
        new(AchievementId.FieldMedic, "ACH_FIELD_MEDIC", "Field Medic", "Bandage a teammate."),
        new(AchievementId.Wired, "ACH_WIRED", "Wired", "Drink 3 energy drinks in one section."),
        new(AchievementId.NowYouSeeMe, "ACH_NOW_YOU_SEE_ME", "Now You See Me", "Use a teleport orb."),

        new(AchievementId.Wipe, "ACH_WIPE", "Wipe", "Watch the whole team die."),
        new(AchievementId.NoWayOut, "ACH_NO_WAY_OUT", "No Way Out", "End up jailed with no way out.", hidden: true),
        new(AchievementId.LastOneStanding, "ACH_LAST_ONE_STANDING", "Last One Standing", "Be the only one alive when the team reaches the elevator."),

        new(AchievementId.Pacifist, "ACH_PACIFIST", "Pacifist", "Finish a section without hitting an enemy."),
        new(AchievementId.Ghost, "ACH_GHOST", "Ghost", "Finish a section without any enemy chasing you."),
        new(AchievementId.Speedrunner, "ACH_SPEEDRUNNER", "Speedrunner", "Finish a section under its par time."),
    };

    static Dictionary<AchievementId, Entry> s_ById;

    public static IReadOnlyList<Entry> All => s_Entries;

    public static bool TryGet(AchievementId id, out Entry entry)
    {
        if (s_ById == null)
        {
            s_ById = new Dictionary<AchievementId, Entry>(s_Entries.Length);
            for (int i = 0; i < s_Entries.Length; i++)
                s_ById[s_Entries[i].Id] = s_Entries[i];
        }

        return s_ById.TryGetValue(id, out entry);
    }
}
