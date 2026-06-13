# MazeEscape — Performance Optimization Guide

**Engine:** Unity 6 (6000.4.1f1) · **Pipeline:** Universal Render Pipeline (URP) 17.4 · **Net:** Netcode for GameObjects 2.x
**Target:** runs well across a wide range of PCs (low-end integrated GPUs → high-end), also Android/iOS.

This document records (1) what was implemented in code, (2) the recommended editor-side work that still needs a
human + a profiling pass, and (3) what **not** to change. The hard rule throughout: **never alter fundamental
gameplay, networking, or simulation** — every change here is a rendering/CPU/memory knob only.

---

## 1. What was implemented (done, in code)

### Player-facing graphics settings — `Assets/Scripts/Display/GameGraphicsSettings.cs`
A singleton (same pattern as `GameAudioManager` / `GameDisplayBrightness`) that auto-attaches to the
`MultiplayerBootstrap` object and persists to `PlayerPrefs`. It exposes **Low / Medium / High / Ultra** presets
plus independent display controls, all applied at runtime. On **first launch it auto-detects** a starting tier
from `SystemInfo` (VRAM / RAM / CPU cores) so weak machines don't open at Ultra.

The in-game UI lives in the existing shared settings panel (`MenuSettingsPanel`, used by both the main menu and the
pause menu) under a new **GRAPHICS** section. Because the panel got taller, it is now wrapped in a scroll view
(`MenuWidgets.CreateScrollView`) and a new stepper widget (`MenuWidgets.CreateStepper`) was added for the
resolution selector (the menu had no dropdown widget).

**Controls exposed to the player**

| Control | Effect | API used |
|---|---|---|
| Quality Preset | Applies the whole tier table below | `QualitySettings` + URP asset |
| Render Scale (50–100%) | Renders below native then upscales — **the strongest GPU lever** | `UniversalRenderPipelineAsset.renderScale` |
| Resolution | Window/output resolution | `Screen.SetResolution(...)` |
| Display Mode | Fullscreen / Borderless / Windowed | `Screen.fullScreenMode` |
| V-Sync | Off / On | `QualitySettings.vSyncCount` |
| Frame Rate Limit | 30 / 60 / 120 / 144 / Uncapped | `Application.targetFrameRate` |

**Tier table** (`GameGraphicsSettings.Tiers`). Render scale is the headline difference; the shadow rows are tuned
but **currently inert** (the game uses ambient-only lighting with no shadow-casting lights) so they cost nothing
and are ready if a shadow-casting light is ever added — they are intentionally *not* exposed as player sliders.

| Setting | Low | Medium | High | Ultra |
|---|---|---|---|---|
| Render scale | 0.70 | 0.85 | 1.00 | 1.00 |
| Upscaler | FSR | FSR | Auto | Auto |
| MSAA | Off | Off | Off | 2× |
| Texture mip limit | Half | Full | Full | Full |
| Anisotropic | Off | Per-tex | Per-tex | Force |
| LOD bias | 0.5 | 0.8 | 1.0 | 1.5 |
| Realtime reflection probes | Off | Off | On | On |
| Soft particles | Off | Off | On | On |
| Shadow distance / cascades* | 25 / 1 | 40 / 2 | 50 / 4 | 75 / 4 |

\* inert today (no active shadow-casting light).

> **Editor note:** at runtime the system mutates the *active* URP asset (`PC_RPAsset` on standalone). In a build
> this only affects the in-memory copy for the session (re-applied from `PlayerPrefs` each launch). **In the
> Editor, those property writes can linger on the asset until a reimport** — a dev-only cosmetic side effect, not
> something that ships. If you want the asset's on-disk defaults to stay put, don't "Save Project" right after
> changing tiers in Play mode, or reimport `Assets/Settings/PC_RPAsset.asset`.

### Gameplay-safe AI CPU win — `ClownAI.cs`, `JailorAI.cs`, `ZombieAI.cs`
Each enemy ran its target-acquisition scan (`Physics.OverlapSphere` + line-of-sight rays) **every frame**. Those
methods already early-out the instant the enemy holds a live target, so the scan only ever runs during *search*.
A new serialized field **`sensingInterval` (default 0.1s)** now paces just that search scan; movement, chasing,
losing a target, grabbing and carrying all stay per-frame. Net effect: up to 100 ms of extra latency on *first*
spotting a player (imperceptible) in exchange for a real CPU saving on AI-heavy scenes. Agents are staggered so
they don't all scan on the same frame. **Set `sensingInterval = 0` in the inspector to restore exact
per-frame behaviour** if you ever notice a difference.

### Verified already-optimal (no change needed)
- **`Camera.main`** is not a hot path — the player camera is intentionally untagged and the code already caches
  camera references in fields; `Camera.main` only appears as setup-time / null-returning fallbacks.
- **No per-frame `Debug.Log`** — all logging is `LogWarning`/`LogError` in setup/error paths, guarded by
  once-flags.
- Physics queries already use the **NonAlloc** buffer APIs with pre-sized arrays, and AI uses **registries**
  (`PlayerHealthRegistry`, `ZombieAIRegistry`) instead of `FindObjectOfType`. SRP Batcher is **on** in both URP
  assets. Good baseline.

---

## 2. Editor-side optimizations (need Unity + a profiling pass)

These require the editor and/or per-asset judgement, and a couple can be *net-negative on weak hardware*, so they
were intentionally **not flipped blind**. Each is one-time work with clear payoff. Ordered by ROI.

### 2.1 GPU Resident Drawer + GPU Occlusion Culling — **the marquee Unity 6 win for this game**
A procedurally-built maze = hundreds of repeated wall/floor/prop `MeshRenderer`s, with walls hiding most of the
level. GPU Resident Drawer auto-instances those repeated static meshes on the GPU, and GPU Occlusion Culling skips
rooms the camera can't see — **no bake required, works with runtime-spawned geometry** (baked occlusion can't).
The Forward+ prerequisite is **already met** (`PC_Renderer.asset` → Rendering Mode = Forward+).

**Enable (PC tier):**
1. `Assets/Settings/PC_RPAsset.asset` → **Rendering → GPU Resident Drawer = Instanced Drawing**.
2. Tick **GPU Occlusion Culling** (appears once the drawer is on).
3. `Project Settings → Graphics → BatchRendererGroup Variants = Keep All` (so the instancing shader variants
   survive a build — otherwise it works in the Editor but silently fails in a player).
4. Leave it **off** for the Mobile tier asset.

**You MUST then profile on your weakest target GPU.** It trades CPU for GPU-driven work and the research flags it
can *lose* on very weak integrated GPUs. Watch the Frame Debugger / Profiler and look for: skinned characters
(player/clown/jailor) rendering correctly (the drawer only handles static `MeshRenderer`s — skinned meshes are
unaffected, which is fine), no missing props, and an actual draw-call drop. **To revert:** set GPU Resident Drawer
back to *Disabled*. Consider keeping it enabled only for High/Ultra if low-end loses.

### 2.2 Texture import — biggest memory/VRAM saver (no LODs means this matters more)
Source art is heavy (e.g. `AllSkyFree` skyboxes up to 77 MB each ≈ 320 MB total; 40 MB brick normal map; `Free
Pack` ≈ 1.1 GB). Per texture (`Inspector`): **Max Size** (drop world textures to 1024/512 where full res isn't
visible), **Compression** = Normal/High, **Generate Mip Maps = on** for 3D-world textures (kills corridor
shimmer), then **Mip Streaming = on** under Advanced. Globally: `Project Settings → Quality → Textures` → enable
**Texture Streaming** + a **Memory Budget (MB)**. (`QualitySettings.streamingMipmapsActive` /
`globalTextureMipmapLimit` are the runtime hooks; the Low tier already drops the top mip via the mip-limit field.)
Also **delete unused asset packs / unused skyboxes** to cut build size and import time.

### 2.3 Audio import — easy RAM/size win
The ambience loop is an 8.3 MB uncompressed WAV; several SFX are multi-MB PCM. Per clip (`Inspector`):
- Music / long ambience (e.g. `Ambiance_Cave_Dark_Loop`) → **Vorbis + Load Type = Streaming** (1–2 streaming
  clips per scene max).
- Short, frequent SFX (footsteps, UI) → **Decompress On Load** (PCM/ADPCM).
- Medium one-shots → **Vorbis + Compressed In Memory**.
No audible change at reasonable Vorbis quality; large RAM/build savings.

### 2.4 LOD Groups (higher effort)
There are **no LOD Groups** on ~170 models, so high-poly props render at full detail at any distance, and the tier
`lodBias` knobs currently do nothing. Adding `LODGroup`s (or an automatic LOD tool) to the heaviest props/dungeon
kit lets distant geometry drop to cheaper meshes. Highest effort here; do it after 2.1–2.3 and only if profiling
shows you're vertex/geometry bound.

### 2.5 GPU instancing on materials (mostly redundant with SRP Batcher / GPU RD)
SRP Batcher already batches by shader variant, so "hundreds of materials" is *not* itself a problem. Only enable
**GPU Instancing** on a material (`Inspector → Enable GPU Instancing`) for cases SRP Batcher can't help and GPU RD
isn't covering. Don't bulk-toggle it blindly.

---

## 3. What NOT to do (would risk gameplay/visuals in a multiplayer game)

- **Don't change the Netcode Tick Rate or throttle fast-changing NetworkVariables** to "save bandwidth" — it
  affects responsiveness/hit-registration. Client-side interpolation already hides the tick gap. Leave it.
- **Don't switch the rendering path away from Forward+** — it's required for GPU RD and suits the (mostly
  unlit/ambient) maze.
- **Don't lower the Fixed Timestep** (Project Settings → Time) — risks physics tunneling/jitter and changes feel.
  Leave at 50 Hz.
- **Don't enable `Physics.autoSyncTransforms`** — it's an expensive global; the default (off) is correct.
- **Don't set `sensingInterval` above ~0.15s** on the enemies — beyond that, target acquisition starts to feel
  laggy. 0.1s is the sweet spot; 0 restores original.

---

## 4. How to verify the changes

1. **Open the project in Unity 6** (6000.4.1f1) and let it compile — these C# changes were written but **not
   compiled here**, so confirm a clean Console first. New/changed files:
   - `Assets/Scripts/Display/GameGraphicsSettings.cs` (new)
   - `Assets/Scripts/UI/Menu/MenuSettingsPanel.cs` (GRAPHICS section + scroll)
   - `Assets/Scripts/UI/Menu/MenuWidgets.cs` (`CreateScrollView`, `CreateStepper`, `MenuStepper`, `ScrollViewHeightClamp`)
   - `Assets/Scripts/Multiplayer/MultiplayerBootstrap.cs` (attaches the manager)
   - `Assets/Scripts/Enemy/ClownAI.cs`, `JailorAI.cs`, `ZombieAI.cs` (`sensingInterval`)
2. **Enter Play in the main menu** → open **Settings** → the new **GRAPHICS** section appears; the panel scrolls.
   Change the Quality Preset and Render Scale and confirm the image softens/sharpens; change Resolution / Display
   Mode / V-Sync / FPS limit and confirm they apply. Re-open Settings and confirm values persisted.
3. **In a level**, open the pause menu (Esc) → Settings → same controls work mid-session.
4. **Confirm gameplay is unchanged:** the Clown/Jailor/Zombie still detect, chase, grab, swing and knock back
   exactly as before. If anything about detection feels off, set `sensingInterval = 0` on that enemy prefab.
5. Use the **Stats** overlay / **Profiler** to confirm FPS scales with the quality preset and render scale, and
   (after 2.1) that draw calls drop.
