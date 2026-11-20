# SCULPT (working title)

Competitive first-person PvP shooter in Unity 6 (6000.4.1f1, URP, Universal 3D template), Windows desktop only (not WebGL).
The hook: a terrain-sculpting system with a very high skill ceiling. Players push grid-aligned blocks around mid-fight.
Accuracy and testability matter more than speed.

**Design pillars (from Ben):**
- High skill cap that still works at beginner level. Satisfying and fair at every level. When a choice is close, pick the option that is readable and fair for the victim and rewards mastery for the skilled player.
- Absolutely heavy focus on low latency. Input-to-response must feel instant: predict locally, keep tick work cheap, never add avoidable frames of delay.
- Sculpting must feel fair and clean: clear tells, no surprise deaths from geometry, crisp predictable results.
- Guns feel heavy and realistic: weight in recoil, sound and impact, not toy-like.
- **Project rule:** go hard early on making the feel amazing and on skill cap. Don't rush: a good base matters more than speed. Feel work is not deferred to M6; every milestone that touches movement, sculpting or guns gets tuned until it feels right.

This file is the source of truth for every thread. Update it whenever Ben changes a decision.

## Working rules

- Work milestone by milestone, one branch each. Current plan from Ben (2026-11-20): run M0, M1 and M2 back to back, then STOP after M2 with a short report and a play-test checklist (in chat, not a separate doc) so Ben can test feel. After that, stop at the end of each milestone unless Ben says otherwise. Do not start a milestone past the agreed stop point until Ben says so.
- If a thread's context gets heavy, hand over to a fresh thread: update this file and the project memory first so nothing is lost.
- If anything in the spec is ambiguous or contradictory, ask Ben. Never guess on gameplay rules. Record open questions at the bottom of this file.
- NEVER hand-edit `.unity`, `.prefab`, `.asset`, `.mat` or any other Unity YAML file. If Editor work is needed, give Ben exact click-by-click Editor steps.
- Minimise Editor work: one bootstrap scene with a single bootstrap object. Everything else (world, players, UI, cameras, lights) is created from code and ScriptableObjects.
- Moving, renaming or deleting a script: move, rename or delete its `.meta` too. Never create `.meta` files by hand; Unity generates them.
- Check compile errors and runtime logs by reading `%LOCALAPPDATA%\Unity\Editor\Editor.log` (here: `C:\Users\cpyvi\AppData\Local\Unity\Editor\Editor.log`), not by asking Ben to copy the console.
- No GPL-licensed code or assets. Don't copy other games' names, icons, art or proprietary code.
- New Input System only. Input Actions asset generated from code where possible. All binds rebindable.
- Small, focused commits with clear messages. One branch per milestone (`m0-scaffolding`, `m1-core-grid`, ...). Commit as `benpy28`.
- All tunable numbers live in ScriptableObjects or config data, never hard-coded.
- Ben prefers plain, straight answers in chat. No Markdown/PDF explainer files beyond this one.

## Architecture

1. **Core** (`Assets/Scripts/Core`, assembly `Sculpt.Core`): pure C# simulation, NO UnityEngine references (asmdef has `noEngineReferences: true`). Contains the block grid, sculpt rules, sculpt bar, gravity, reinforcement, player movement maths and collision, weapon damage maths. Use our own small vector structs and `System.Math`, never `Mathf`/`Vector3`. C# 9 and .NET Standard 2.1 APIs only, so it compiles in both Unity and plain .NET.
2. **Core.Tests** (`Tests/Core.Tests`, outside `Assets/`): .NET test project (NUnit) that compiles the Core `.cs` files via a `Compile Include` glob. `LangVersion` 9. Run with `dotnet test Tests/Core.Tests`. `Tests/Core.Check` builds the same files against `netstandard2.1` to catch non-Standard APIs. Every Core rule gets unit tests including edge cases, written alongside or before the code.
3. **Game** (`Assets/Scripts/Game`, assembly `Sculpt.Game`): thin Unity layer for rendering, input, audio, UI, effects and FishNet glue. Converts between Core types and Unity types. Entry point is `GameBootstrap` on the single object in `Assets/Scenes/Bootstrap.unity`.
4. Simulation is fixed-tick at 60 Hz. All Core timers are integer tick counts. Grid state is integers only. Server is authoritative.
5. World storage: flat arrays of block states in 16x16x16 chunks. One generated mesh per chunk, rebuilt only when that chunk changes. Never one GameObject per block.
6. Player collision: axis-aligned box vs the voxel grid, implemented in Core (no CharacterController, no Rigidbody), deterministic and prediction-friendly. Ramps are sloped surfaces handled as a height function across the block.
7. Networking: FishNet (free). Server-authoritative, client-side prediction and reconciliation for movement. Sculpt actions are commands validated by the server. Grid changes sync as small deltas. Lag compensation (FishNet Pro) comes later, so structure hit detection so rewind can be slotted in. Install FishNet using its official docs' current method (or Asset Store import by Ben). Don't guess.
8. Camera is a swappable layer (future kill-cam, replay, free spectator).

## Design spec

### World and grid
- Blocks are 1.2 m cubes, world-axis aligned. Everything sits at 90 degrees to the grid; nothing is rotated off-grid.
- Heights in levels: +1 is jumpable. +2 is full standing cover (blocks bullets and walking) but can be mantled. +3 is a hard wall.
- Sculptable height range: -2 to +4 levels relative to base ground. The bottom bedrock layer can't be sculpted.
- Anchor blocks (spawns, landmarks, objectives) can't be sculpted.
- Faces shaded per axis: tops brightest, north/south darker, east/west darker again. Fixed sun direction. Compass strip (N/E/S/W) at the top of the HUD.

### Sculpt mode
- Toggle key (default Q). Not a hotbar slot. Leaving sculpt mode returns instantly to the last-held weapon. Usable in any movement state (sprinting, sliding, airborne).
- Range: 5 blocks (6 m), measured from the player.
- Sculptable blocks always have a faint purple edge glow within range. **Purple is reserved ONLY for "sculptable"**; no other purple anywhere in the game.
- Entering sculpt mode: cosmetic purple ripple spreads along the ground to the range edge over ~0.1 s, sculptables light up brightly as it passes. Selection works instantly. Soft boundary at range edge, out-of-range blocks dim. Non-sculptable scenery slightly desaturated, but enemies always keep full colour and outlines and nothing is ever drawn over an enemy. Overlay is local to the sculpting player.
- Selection: crosshair with slight magnetism (sculptable closest to the crosshair within a small cone; stronger magnetism near your feet). Objects take priority over blocks under them. Players are NEVER selectable.
- Direction picking (world axes only, never diagonals):
  - Side face: LMB = push away (into that face), RMB = pull toward you (out of that face). Axis = face normal.
  - Top face: split into 4 triangles along its diagonals; the triangle under the crosshair picks the horizontal direction, highlighted with an arrow. LMB/RMB push/pull along that axis.
  - Scroll up = raise (add a block on top of the targeted column). Scroll down = sink (remove the top block of the targeted column).
  - Hold Shape (default F) + LMB on a top-face triangle = make the block's top a ramp facing that direction, or back to flat if it already faces that way.
  - Hold Reinforce (default Left Alt) while confirming = reinforced version of the action.
  - The direction press IS the confirm. No separate confirm step.
- Push/pull moves the targeted block exactly one cell along the axis.
- Always show a translucent ghost of the result in the destination cell. Red ghost if invalid.
- Hovered block: edges bright white-purple, plus destination ghost and arrow.
- Windup: 0.2 s normal (12 ticks). During windup the block's edges crackle in the sculptor's team colour and a positional hum plays that enemies can hear. The tile is LOCKED during windup (nobody else can target it).
- Move animation: fast snap with tiny overshoot, dust puff, deep thunk.
- Sculpted blocks carry a faint team tint that fades to neutral over time.

### Sculpt rules (all in Core, all unit-tested)
- Target cell blocked by terrain = invalid: red ghost, nothing happens, no bar spent.
- A player in the path of a sliding block is shoved one cell along the push direction. If they can't be shoved (blocked behind), the action is invalid. Nobody is ever crushed or trapped inside geometry.
- A rising block carries any player on top. A sinking block drops them.
- Gravity: unsupported blocks fall straight down one cell at a time, deterministically. Players standing on them fall too.
- Only blocks in line of sight and within range can be sculpted (server checks too).
- Warning when a block you stand on or touch is being sculpted: distinct sound from below, purple pulse at bottom screen edge, directional indicator toward the sculptor.

### Sculpt bar
- 0-100%, thin arc around the crosshair. While hovering an action, the cost portion flashes.
- Costs: horizontal push/pull 5%, sink 7%, raise 10%, shape 5%, reinforce +15% on top of the base action.
- Regen starts 1.5 s after the last sculpt, then refills 0 to 100% in 20 s.
- Reinforced blocks: caster windup 0.35 s. An enemy changing a reinforced block needs 0.6 s windup and pays 2x normal cost. Reinforcement decays to normal after 20 s. Visual: crystalline veins, heavier sound, faint shimmer.

### Movement (first person, snappy, never floaty)
- Run 6 m/s. Sprint 8.5 m/s (Left Shift), unlimited, can't shoot while sprinting, 0.15 s sprint-to-aim delay. Crouch walk 3 m/s.
- Jump clears 1 level (apex ~1.3 m).
- Slide (Left Ctrl or C from a sprint): boost to 10 m/s decaying over ~1 s on flat ground. Downhill accelerates up to 14 m/s cap. Uphill decelerates fast. Flat-ground slide boost has 1.5 s cooldown; downhill has none. Can shoot while sliding with extra spread.
- Vault: hitting a 1-level obstacle while sprinting or sliding hops over it without losing speed.
- Mantle: grab a 2-level ledge, takes 0.4 s, can't shoot during it. 3 levels can't be mantled.
- Slide-jump: jumping out of a slide carries slide speed into the air.
- Slide-land: holding crouch at the moment of landing (150 ms window) converts air speed into a slide; missing it loses the speed. Distinct success sound.
- Ramp launch: leaving a ramp edge keeps full velocity including vertical.
- Limited air control (mostly committed to your line).
- Fall damage: none below 6 levels, light above.
- FOV slider (default 95), minimal head bob with off option, subtle speed lines and FOV kick above sprint speed. Small weapon viewmodel with a size option.

### Weapons (fixed loadout, hitscan)
- Health 150, no armour. Out-of-combat regen exists as an option but is OFF for now.
- Hotbar 1-4 = Pump, SMG, AR, Pistol, always equipped. Slot 5 = empty utility slot for later. One of each weapon type max.
- Blocks stop bullets completely (no penetration). Blocks have no health; only sculpting changes them.
- Pump: 9 pellets x 11 dmg, headshot pellets 1.5x, 0.9 s between shots. Falloff from 5 m, heavy past 12 m. Fairly accurate in air and sliding. Swap-cancelling after a shot allowed.
- SMG: 15 dmg, 12 shots/s, heavy falloff past 10 m.
- AR: 25 body / 40 head, 5.5 shots/s. First shot accurate, then spread builds. Strongly penalised while sliding or airborne.
- Pistol: 30 body / 50 head, semi-auto, max 5 shots/s, accurate.
- Draw times: pistol 0.2 s, SMG 0.3 s, AR 0.4 s, pump 0.45 s.
- Hit checks use the server's view from the player's head position.

### Later (don't design out)
Riot shield + pistol (frontal block, no sprint or mantle, hip-fire pistol); special objects (bounce pad, mirror block, bridge segment, heavy block) using the same six directions plus Shape; lag compensation; replay/kill-cam and free spectator camera; matchmaking.

## Milestones
- **M0** Repo and scaffolding: git, .gitignore, .gitattributes + LFS, CLAUDE.md, folders, Core asmdef, Core.Tests with one trivial test, bootstrap scene steps, push to private GitHub. **(current)**
- **M1** Core grid and sculpt rules: chunked storage, block types (empty, solid, ramp with facing, anchor), reinforcement state and decay, all sculpt actions and validation, gravity, shove/carry, sculpt bar economy, windup and tile locks. Thorough tests.
- **M2** Core movement + offline playable: box-vs-grid collision incl. ramps, all movement states and tech, tests. Unity: chunk meshing with per-axis shading, test map (flat, raised blocks, ramps, 1/2/3-level walls), first-person controller driven by Core.
- **M3** Networking: FishNet, server-authoritative, prediction/reconciliation, host mode, two players same machine or LAN, simulated latency and packet loss. Smooth at 100 ms simulated ping.
- **M4** Sculpt mode networked: inputs, visuals, ghost, windup and locks, bar UI, targeted warnings, server validation, delta sync.
- **M5** Weapons networked: loadout, swaps, damage, falloff, accuracy states, health, death/respawn, hit markers.
- **M6** Feel pass: audio, snap/overshoot, ripple, speed effects, compass, settings menu (FOV, sensitivity, rebinds, viewmodel size).

## Repo layout
```
Assets/Scenes/Bootstrap.unity      single scene, one GameObject with GameBootstrap (made by Ben in the Editor)
Assets/Scripts/Core/               Sculpt.Core asmdef, pure C#
Assets/Scripts/Game/               Sculpt.Game asmdef, Unity layer
Tests/Core.Tests/                  NUnit tests, dotnet test
Tests/Core.Check/                  netstandard2.1 compile check of Core
```

## Machine notes
- Project path: `E:\Unity\sculpt`. Editor: Unity 6000.4.1f1. Shell: PowerShell on Windows 11.
- `gh` CLI is not installed; push with plain git over HTTPS (Git Credential Manager handles sign-in).
- .NET 8 SDK is a user-scope install at `%LOCALAPPDATA%\Microsoft\dotnet` (the one in Program Files is runtime only). Run it by full path with `DOTNET_ROOT` set to that folder.

## Agreed rules (proposed by Claude, approved by Ben 2026-11-20; change here if he overrides)
1. Support: a block is held up only by the block directly below it. No overhangs.
2. Height: base ground top = level 0. Column tops range -2..+4. Cells below -2 are bedrock. Raise past +4 / sink below -2 is invalid.
3. Range: eye to nearest point of the targeted block, <= 6.0 m.
4. Line of sight: clear ray from eye to the aimed point on the face.
5. Ramps: wedge filling the cell. Clicked triangle = low edge (ramp rises away from that side). Only a column's top block can be a ramp; a block landing on a ramp flattens it.
6. Raise on a ramp column: ramp stays on top, solid added under it. Sink removes the ramp block; new top is flat.
7. Shove: minimal displacement to clear the block, at most one cell.
8. Windup: bar charged on confirm; re-validated at windup end; if invalid, cancel and refund. Source and destination cells both locked.
9. Leaving range during windup: action still completes.
10. Reinforcement moves with the block, timer keeps running. Teammates act like the owner. Any enemy change removes it.
11. Enemy on reinforced block: 2x base cost; the +15% reinforce surcharge is not doubled.
12. Block gravity: one cell per 4 ticks.
13. Player box: 0.7 wide, 1.8 tall standing (eye 1.62), 1.1 tall crouch/slide.
14. Player gravity 30 m/s^2, jump speed ~8.8 m/s (apex 1.3 m).
15. Mantle: jump while facing a touching 2-level ledge (ground or air). Vault automatic when sprinting/sliding into 1 level. Running into 1 level just stops you.
16. Slide-land: crouch down at touchdown or pressed within 150 ms after; ignores flat-slide cooldown; keeps horizontal speed.
17. Air control: 15% of ground acceleration.
18. Fall damage: 10 per level beyond 6.

### Smaller calls made by Claude in M1 (tell Ben; change if he disagrees)
- A falling block never enters a cell overlapping a player: it waits above until the cell is clear.
- A stack with empty space under it falls together as one unit.
- Raise treats the column's top block as the block being changed (for enemy reinforcement cost and windup).
- Reinforced sink reinforces the newly exposed top block.
- One windup per player at a time. A new confirm during a windup is rejected (Busy).
- If anything writes to a locked cell during windup (e.g. a falling block), the action cancels at resolution and refunds.
- Pushing a block horizontally does not drag a player standing on it; they fall through movement gravity.
- Sculpt tint fades over 10 s (cosmetic, tunable).

## Open questions
- (none)
