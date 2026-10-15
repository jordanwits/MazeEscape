# Achievements

40 achievements, defined in code in `Assets/Scripts/Achievements/AchievementCatalog.cs`. Every unlock is saved
in PlayerPrefs (`achievement.<API name>`), so LAN games and the Editor keep a record. It is also sent to Steam when
Steamworks is running. Steam only accepts an unlock whose API name exists on the app, so **each row below has to
be created on the Steamworks partner site** (App Admin → Stats & Achievements). Use the same API name, and publish.
Until then (on the Spacewar placeholder, app 480) Steam rejects the names, and the game shows its own
"ACHIEVEMENT UNLOCKED" card instead of Steam's popup.

## How it is wired

- The server decides almost every achievement (gameplay is server-authoritative). `AchievementAwards.ServerAward`
  routes each unlock to the player who earned it. The host unlocks in place. A remote client gets an NGO named
  message (`AchievementNetworkRelay`), which does not depend on the avatar, so awards still arrive when they land
  during a section switch.
- Event hooks live in `AchievementHooks` (kills, jail, lures, team wipe). Section and run rules live in
  `AchievementRunTracker` (deaths, team size, tickets, energy drinks, enemy hits, chases, the section clock).
- Owner-side exceptions call `Achievements.Unlock` directly, because the owning client is where these are detected:
  Limbo (laser hits are detected by the owner) and the two room finds (`AchievementRoomZone` on the
  SeveranceRoom2/3 prefabs).
- A "full run" (Perfect Attendance, Lone Survivor, Full Crew) must start at Level01. The lobby's level picker can
  start a run later.
- Speedrunner par is per section: `ProceduralMazeConfig` → Achievements → `speedrunParSeconds` (default 480 s,
  timed from when the host's maze finishes building). Tune it after playtests.

## Steamworks entries

| API name | Title | Description | Hidden |
|---|---|---|---|
| ACH_DETOUR | Detour | Escape the dungeon. | |
| ACH_STEP_RIGHT_UP | Step Right Up | Escape the carnival. | |
| ACH_CLOCKING_OUT | Clocking Out | Escape the offices and finish a run. | |
| ACH_PERFECT_ATTENDANCE | Perfect Attendance | Finish a full run without anyone on the team dying. | |
| ACH_LONE_SURVIVOR | Lone Survivor | Finish a full run solo. | |
| ACH_FULL_CREW | Full Crew | Finish a full run with four players. | |
| ACH_ROCK_BEATS_BONES | Rock Beats Bones | Beat the jail skeleton at rock-paper-scissors. | |
| ACH_JAILBREAK | Jailbreak | Free a jailed teammate. | |
| ACH_SPECIAL_DELIVERY | Special Delivery | Get carried to the jail by the Jailor. | |
| ACH_BONE_COLLECTOR | Bone Collector | Crumble 10 skeletons. | |
| ACH_POISE_BREAKER | Poise Breaker | Stagger a zombie or a skeleton. | |
| ACH_CLOSE_SHAVE | Close Shave | Get through a swinging axe without being hit. | |
| ACH_SEND_IN_THE_CLOWNS | Send in the Clowns | Kill the Clown. | |
| ACH_ENCORE | Encore | Kill the Clown that comes back. | |
| ACH_WINNER_WINNER | Winner Winner | Win a hand of blackjack. | |
| ACH_BLACKJACK | Blackjack! | Get a natural 21. | |
| ACH_HIGH_ROLLER | High Roller | Earn 50 tickets in a single run. | |
| ACH_RETAIL_THERAPY | Retail Therapy | Buy something from the prize shop. | |
| ACH_MONKEY_BUSINESS | Monkey Business | Pull the Clown off a teammate with the wind-up monkey. | |
| ACH_DONT_BLINK | Don't Blink | Walk past a RatBot without being caught. | |
| ACH_WRONG_TARGET | Wrong Target | Lure a Bomber onto a decoy grenade and let it blow up there. | |
| ACH_NOTHING_BUT_NET | Nothing But Net | Win the basketball booth. | |
| ACH_LIMBO | Limbo | Make it through a laser sweep without being hit. | |
| ACH_SECURITY_BREACH | Security Breach | Kill a security guard. | |
| ACH_THE_FINAL_BOSS | The Final Boss | Kill the Mutant. | |
| ACH_DISCO_INFERNO | Disco Inferno | Find the disco room. | |
| ACH_TOUCH_GRASS | Touch Grass | Find the indoor park. | |
| ACH_LOOK_AWAY | Look Away | Blind an enemy with a flashbang. | |
| ACH_FRIENDLY_FIRE | Friendly Fire | Blind a teammate with a flashbang. | yes |
| ACH_SIGNAL_FIRE | Signal Fire | Hit an enemy with a flare. | |
| ACH_BLADE_RUNNER | Blade Runner | Kill an enemy with the sword. | |
| ACH_FIELD_MEDIC | Field Medic | Bandage a teammate. | |
| ACH_WIRED | Wired | Drink 3 energy drinks in one section. | |
| ACH_NOW_YOU_SEE_ME | Now You See Me | Use a teleport orb. | |
| ACH_WIPE | Wipe | Watch the whole team die. | |
| ACH_NO_WAY_OUT | No Way Out | End up jailed with no way out. | yes |
| ACH_LAST_ONE_STANDING | Last One Standing | Be the only one alive when the team reaches the elevator. | |
| ACH_PACIFIST | Pacifist | Finish a section without hitting an enemy. | |
| ACH_GHOST | Ghost | Finish a section without any enemy chasing you. | |
| ACH_SPEEDRUNNER | Speedrunner | Finish a section under its par time. | |

**Stat** (int, "increment only", set by the game client): `STAT_SKELETONS_CRUMBLED`. It counts toward
Bone Collector. The game unlocks the achievement itself at 10, so Steam's progress-stat link is optional and only
adds the progress bar.

## Interpretation notes

- **Monkey Business:** a clap can never break a live chase (by design), so it counts as "the Clown turns to a fresh
  monkey lure while a teammate other than the player who wound the monkey is within 20 m of him". The player who
  wound it up (the nearest player when it activated) gets it.
- **The Final Boss** goes to the whole team. Every other kill achievement goes to the player who landed the
  killing blow (melee or flare, including flare burn).
- **Nothing But Net:** 5+ baskets in one round (`achievementWinScore` on the booth), credited to whoever started
  the round.
- **High Roller** counts tickets earned (bundle pickups plus blackjack net winnings), not the balance.
- **Ghost** breaks when any enemy acquires you as its target (sight, a heard sprint, a hit), including RatBot stalking.
- **Close Shave** is on the carnival AxeTrap (Level02), the only level that places it.
