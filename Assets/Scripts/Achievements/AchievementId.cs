/// <summary>
/// Every achievement, in catalog order. Sent over the wire as a byte (see <see cref="AchievementNetworkRelay"/>),
/// so values are explicit and must never be renumbered once shipped — append new ones at the end.
/// Names, Steam API names and descriptions live in <see cref="AchievementCatalog"/>.
/// </summary>
public enum AchievementId : byte
{
    // Progression
    Detour = 0,
    StepRightUp = 1,
    ClockingOut = 2,
    PerfectAttendance = 3,
    LoneSurvivor = 4,
    FullCrew = 5,

    // Dungeon (Level01)
    RockBeatsBones = 6,
    Jailbreak = 7,
    SpecialDelivery = 8,
    BoneCollector = 9,
    PoiseBreaker = 10,
    CloseShave = 11,

    // Carnival (Level02)
    SendInTheClowns = 12,
    Encore = 13,
    WinnerWinner = 14,
    Blackjack = 15,
    HighRoller = 16,
    RetailTherapy = 17,
    MonkeyBusiness = 18,
    DontBlink = 19,
    WrongTarget = 20,
    NothingButNet = 21,

    // Severance offices (Level03)
    Limbo = 22,
    SecurityBreach = 23,
    TheFinalBoss = 24,
    DiscoInferno = 25,
    TouchGrass = 26,

    // Items and combat
    LookAway = 27,
    FriendlyFire = 28,
    SignalFire = 29,
    BladeRunner = 30,
    FieldMedic = 31,
    Wired = 32,
    NowYouSeeMe = 33,

    // Co-op and death
    Wipe = 34,
    NoWayOut = 35,
    LastOneStanding = 36,

    // Skill challenges
    Pacifist = 37,
    Ghost = 38,
    Speedrunner = 39,
}
