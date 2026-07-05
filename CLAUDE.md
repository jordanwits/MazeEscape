# Maze Escape

Co-op first-person horror escape game: up to 4 players spawn into a procedurally generated maze,
scavenge keys/items, avoid enemies, and reach the exit elevator across four themed sections
(Level01 dungeon-maze with zombies/Jailor/skeletons, Level02 carnival with a Clown and ticket
minigames, Level03/04 further maze sections). Unity **6000.4.1f1**, URP 17.4.0, new Input System.
Multiplayer is Netcode for GameObjects 2.11.0 — Steam lobbies (Steamworks.NET + community
SteamNetworkingSockets transport) or direct IP/LAN (UnityTransport fallback).

## Working with the project

- Open with Unity Hub, editor 6000.4.1f1. No asmdefs — all code compiles into Assembly-CSharp.
- The editor is driven via **MCP for Unity** (`com.coplaydev.unity-mcp` package). After editing
  scripts, trigger `refresh_unity` and read the console for compile errors. **Newly created .cs
  files need `refresh_unity` scope=`all`** — scope=`scripts` misses them.
- If the UnityMCP tools aren't registered in the session, drive the server over HTTP JSON-RPC at
  `http://127.0.0.1:8080/mcp` — helper script `unity_mcp.py` at repo root (`python unity_mcp.py
  list|call|resource`).
- Testing: `com.unity.test-framework` is installed but there are **no test assemblies** — no
  automated tests exist. Verification is play mode in-editor plus built-client playtests.
  Multiplayer Play Mode is NOT installed; online testing = two machines/Steam accounts (steps in
  `Assets/Scripts/Multiplayer/OnlinePlaytestChecklist.cs`) or direct-IP LAN. `steam_appid.txt` is
  480 (Spacewar placeholder app id).

## Project layout

- `Assets/Scenes/` — `Menu.unity` (entry: lobby, character select, level select), `Level01–04`
  (in build). `Staging.unity`, `Dev_IKTest.unity` are dev scenes, not in the build.
- `Assets/Scripts/` — all game code, global namespace. Subfolders by system: `Player/`, `Enemy/`,
  `Multiplayer/`, `Maze/` (carnival minigames incl. `Blackjack/`), `Performance/`, `UI/` +
  `UI/Menu/`, `Audio/`, `Display/`. Maze generation and door/trap scripts sit at the folder root
  (`ProceduralMazeCoordinator.cs`, `ProceduralMazeConfig.cs`, `MazePieceDefinition.cs`,
  `MazePieceResolver.cs`, `HingeInteractDoor.cs`, `ElevatorFinishController.cs`, traps).
- `Assets/Prefabs/` — `Characters/`, `Enemies/`, `Items/`, `Maze Components/` (maze piece
  prefabs), `MG_Components/`, `Multiplayer/` (`DoorStateStore.prefab`).
- `Assets/Resources/` — `DefaultNetworkPrefabs.asset` (**the live NGO prefab list** — loaded by
  `MultiplayerBootstrap`; the copy at `Assets/DefaultNetworkPrefabs.asset` is not the one used),
  `MultiplayerProjectSettings.asset` (lobby character roster), `MazeConfigs/` (per-level configs
  `MazeSection_Level01–04` + legacy fallback `ProceduralMazeConfig.asset`).
- Most other top-level `Assets/` folders are imported asset-store packs (AllSkyFree, Decrepit
  Dungeon LITE, Survivalist, Bridge Playing Cards, ...) with their own demo scenes.
- `Docs/Performance-Optimization.md` — perf notes.

## Architecture

- **Network stack is code-assembled, not scene-authored.** `Multiplayer/MultiplayerBootstrap.cs`
  (DontDestroyOnLoad singleton) adds NetworkManager, both transports, `SteamworksBootstrap`,
  `SteamLobbyService`, `MultiplayerSessionController` (host/join, transport pick),
  `MultiplayerSceneFlow`, `ProceduralMazeCoordinator`, `PauseMenuController`, audio/display
  managers — all at runtime in `EnsureCoreComponents`.
- **Server-authoritative gameplay.** The server runs enemy AI and adjudicates all hits — the
  single trap/melee entry point is `NetworkPlayerRagdoll.RequestTrapHitFromServer` (server-only).
  Clients own only their player movement/animation via `OwnerNetworkTransform` /
  `OwnerNetworkAnimator`.
- **The maze itself is never network-spawned.** The host picks a seed, replicates it through
  `NetworkPlayerAvatar` RPCs, and every peer deterministically builds the level locally:
  `ProceduralMazeCoordinator` generates the cell layout, resolves piece prefabs through
  `MazePieceResolver`/`MazePieceDefinition`, and bakes NavMesh at runtime (Unity.AI.Navigation).
  Per-level tuning lives in the `ProceduralMazeConfig` assets under `Resources/MazeConfigs/`.
- **Enemy slots are generic.** Configs expose four `MazeEnemySpawn` slots (`enemy1..enemy4`,
  prefab + count); each level fills them differently (Enemy 2 = Jailor on L01/03/04, Clown on
  L02). Code accessors keep role names (`MazeHunterPrefab`, `MazeSkeletonPrefab`, ...).
- **Door state replication.** Because procedural doors aren't spawned NetworkObjects, their
  open/locked state replicates via `Multiplayer/DoorNetworkStateStore.cs` — a `NetworkList`
  keyed by `HingeInteractDoor.DoorId` on the server-spawned `DoorStateStore.prefab` (which also
  hosts `ConsumedItemNetworkStore` and `CarnivalRadioNetworkStore`). Do NOT nest NetworkObjects
  inside maze piece prefabs — nested spawns are silently dropped on clients (unregistered
  `GlobalObjectIdHash`; see `MultiplayerBootstrap.EnsureNestedPrefabHashOverrides`).
- **Level advance.** `ElevatorFinishController` calls
  `ProceduralMazeCoordinator.ServerDespawnAllLevelNetworkObjects()` (dynamically spawned
  NetworkObjects otherwise survive `LoadSceneMode.Single`), then loads the next scene through
  NGO's NetworkSceneManager.
- **Enemies.** Per-species AI in `Scripts/Enemy/` (`ZombieAI`, `JailorAI`, `ClownAI`,
  `SkeletonAI`, `WindupMonkeyAI`) runs server-side on NavMesh; visuals replicate via the
  `Network*Avatar` components in `Scripts/Multiplayer/`.
- **Player.** `Player/PlayerController.cs` + partials (`.Inventory`, `.Carnival`,
  `.PickupReach`) — first-person controller with hotbar inventory (`NetworkPlayerInventory` +
  item scripts), ragdoll (`NetworkPlayerRagdoll`), health, IK. Four selectable Survivalist
  characters, one owner each (roster in `Resources/MultiplayerProjectSettings.asset`).
- **UI is 100% runtime-built — no authored canvases in any scene.** The "plate" design system:
  `UI/Menu/MenuTheme.cs` (palette, fonts, procedural sprites), `MenuWidgets.CreatePlate` with
  `PlateStyle {Nav, Primary, Ghost, Danger}` is THE button builder, `MenuButtonFx` handles
  states; in-game HUD goes through `UI/HudKit.cs` (`EnsureHudCanvas`, `HudPrompt`). Build new UI
  with these, not ad-hoc canvases or styles.
- **Rendering perf.** `Performance/WorldRenderCuller.cs` (view-cone bucketed culling of static
  MeshRenderers, one per level), `MazeLightCuller.cs` (radius-based on purpose), and
  `MazeDistanceFog.cs` (fog masks the cull edge). Add `WorldRenderCullIgnore` to props that must
  always render.

## Conventions & gotchas

- **Search scope:** only look in `Assets/`, `ProjectSettings/`, `Packages/manifest.json`,
  `Docs/`. Never search or index `Library/`, `Temp/`, `Logs/`, `obj/`, or `.claude/worktrees/`
  (worktrees hold stale duplicate script copies that poison results).
- **`Camera.main` is null in gameplay** — the player camera "PlayerView" is deliberately
  Untagged. Resolve the local camera from the player rig (see `WorldRenderCuller.ResolveViewpoint`
  or `RagdollCameraCollision` for the fallback pattern); never gate a feature on `Camera.main`.
- Scripts use the global namespace (no `namespace` blocks); one class per file, file = class name.
- Register any new networked prefab in `Assets/Resources/DefaultNetworkPrefabs.asset` — the
  root-level copy of that asset is ignored at runtime.
- Auto-memory (separate from this file) holds evolving gotchas; check it before deep debugging.
