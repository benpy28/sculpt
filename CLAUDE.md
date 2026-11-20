# SCULPT (working title)

Competitive first-person PvP shooter in Unity 6 (6000.4.1f1, URP, Universal 3D template), Windows desktop only (not WebGL).
The hook: a terrain-sculpting system with a very high skill ceiling. Players push grid-aligned blocks around mid-fight.
Accuracy and testability matter more than speed.

**Core vision (Ben, 2026-11-20, his words):** "you can quickly edit the environment around you in a way that feels fair and lets you take a defensive/attacking playstyle in a 1v1 and try to outsmart or outmechanics you opponent in a 1v1 that typically takes place in a short distance. it also gives the option to quickly take cover from long distance fire". So: quick, fair environment editing; both defensive and attacking styles; close-range 1v1s won by outsmarting or out-mechanicing the opponent; fast cover from long-range fire.

**Design pillars (from Ben):**
- High skill cap that still works at beginner level. Satisfying and fair at every level. When a choice is close, pick the option that is readable and fair for the victim and rewards mastery for the skilled player.
- Absolutely heavy focus on low latency. Input-to-response must feel instant: predict locally, keep tick work cheap, never add avoidable frames of delay.
- Sculpting must feel fair and clean: clear tells, no surprise deaths from geometry, crisp predictable results.
- Guns feel heavy and realistic: weight in recoil, sound and impact, not toy-like.
- **Project rule:** go hard early on making the feel amazing and on skill cap. Don't rush: a good base matters more than speed. Feel work is not deferred to M6; every milestone that touches movement, sculpting or guns gets tuned until it feels right.

This file is the source of truth for every thread. Update it whenever Ben changes a decision.

## Working rules

- Work milestone by milestone, one branch each. Current plan from Ben (2026-11-20): run M0, M1 and M2 back to back, then STOP after M2 with a short report and a play-test checklist (in chat, not a separate doc) so Ben can test feel. After that, stop at the end of each milestone unless Ben says otherwise. Do not start a milestone past the agreed stop point until Ben says so.
  - Update 2026-11-20 (Ben, in his words): "ima be away so just keep going with the roadmap indefinitely". Still post a report and play-test checklist at the end of every milestone.
- Hand over to a fresh thread at the end of every milestone, or sooner if context gets heavy. Update this file and the project memory first so nothing is lost.
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

> **Terrain v2 (approved by Ben 2026-11-20, built in M3.5) supersedes the single-block grid below wherever they differ.** See the "Terrain v2" section after the sculpt bar. Shape (ramp) is removed for now; the F bind is free.

### World and grid
- Blocks are 1.2 m cubes, world-axis aligned. Everything sits at 90 degrees to the grid; nothing is rotated off-grid.
- Heights in levels: +1 is jumpable. +2 is full standing cover (blocks bullets and walking) but can be mantled. +3 is a hard wall.
- Sculptable height range: -2 to +4 levels relative to base ground. The bottom bedrock layer can't be sculpted.
- Anchor blocks (spawns, landmarks, objectives) can't be sculpted.
- Faces shaded per axis: tops brightest, north/south darker, east/west darker again. Fixed sun direction. Compass strip (N/E/S/W) at the top of the HUD.

### Sculpt mode
- Toggle key (default Q). Not a hotbar slot. Usable in any movement state (sprinting, sliding, airborne).
- **Competitive timings (Ben approved 2026-11-20, from the 1v1 balance sim):** nothing is instant. Entering sculpt mode takes 6 ticks (0.1 s): you can aim during it but can't confirm until it ends. Leaving sculpt mode returns to the last-held weapon after its exit time: pistol 6, SMG 9, AR 10, pump 12 ticks. Purpose: no "sculpt then instantly shotgun"; with these numbers the earliest pump shot after a sink lands about 0.49 s after the victim sees the tell, which is above human reaction.
- Range: 5 blocks (6 m), measured from the player.
- Sculptable blocks always have a faint purple edge glow within range. **Purple is reserved ONLY for "sculptable"**; no other purple anywhere in the game.
- Entering sculpt mode: cosmetic purple ripple spreads along the ground to the range edge over ~0.1 s, sculptables light up brightly as it passes. Selection works instantly. Soft boundary at range edge, out-of-range blocks dim. Non-sculptable scenery slightly desaturated, but enemies always keep full colour and outlines and nothing is ever drawn over an enemy. Overlay is local to the sculpting player.
- Selection: crosshair with slight magnetism (sculptable closest to the crosshair within a small cone; stronger magnetism near your feet). Objects take priority over blocks under them. Players are NEVER selectable.
- Direction picking (world axes only, never diagonals):
  - Side face: LMB = push away (into that face), RMB = pull toward you (out of that face). Axis = face normal.
  - Top face: split into 4 triangles along its diagonals; the triangle under the crosshair picks the horizontal direction, highlighted with an arrow. LMB/RMB push/pull along that axis.
  - Scroll up = raise (add a block on top of the targeted column). Scroll down = sink (remove the top block of the targeted column).
  - ~~Shape (F) ramp action~~: removed for now (Ben, 2026-11-20). F is unbound.
  - Hold Reinforce (default Left Alt) while confirming = reinforced version of the action.
  - The direction press IS the confirm. No separate confirm step.
- Push/pull moves the targeted block exactly one cell along the axis.
- Always show a translucent ghost of the result in the destination cell. Red ghost if invalid.
- Hovered block: edges bright white-purple, plus destination ghost and arrow.
- Windup: 0.2 s normal (12 ticks). During windup the block's edges crackle in the sculptor's team colour and a positional hum plays that enemies can hear. The tile is LOCKED during windup (nobody else can target it).
- **Gun lock (Ben approved 2026-11-20):** your gun is locked during the windup of any action on a piece that isn't yours or your team's (attacks) and during a throw windup; you stay in sculpt mode until it lands, then pay the exit time. Raising or reinforcing your own or your team's pieces does NOT lock the gun, so defence stays fast. This was the biggest lever in the sim for making sculpt speed decide fights.
- The windup tell an enemy sees must show the action type (sink, push/pull with direction, raise, throw), not just "something is happening".
- Locked and Busy rejections cost nothing (red feedback, no bar). A confirm pressed up to 3 ticks before a tile unlocks or your own windup ends is buffered and fires on the first valid tick, so re-timing is skill, not click spam.
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
- Reinforced blocks: caster windup 0.35 s. An enemy changing a reinforced block needs 0.4 s windup (24 ticks; was 0.6 s, changed by Ben 2026-11-20 after the balance sim) and pays 2x normal cost. Rule: this windup must stay below a pro's notice + aim + normal windup (~0.5 s); at 0.6 s a reinforced defender could always counter-break first and won 92-96% of pro mirrors. Reinforcement decays to normal after 20 s. Visual: crystalline veins, heavier sound, faint shimmer.

### Terrain v2: sub-cells, pieces and throw (Ben approved 2026-11-20)
Ben's constraint: "it cannot become a cube fighting game, its meant to feel proper 3D and detailed". Gameplay stays exact and integer; the look hides the grid.
- **Sub-cells:** 0.3 m cubes, 4x4x4 per 1.2 m tile. Each stores a piece id (0 = empty). Anchors and bedrock are non-piece terrain. Chunks are 16^3 sub-cells.
- **One surface for everything:** Core derives a chamfered surface from sub-cell occupancy (45 degree cuts on steps and edges, vertices on a 0.15 m lattice, fixed tie-break rules). The same surface is used for rendering, player collision and bullets: what you see is what you hit. Visual detail (rock materials, normal maps, per-piece colour, seam cracks, debris, dust) has no collision and never blocks sight; real geometry jitter at most ~2 cm. Rule: nothing hides a player without stopping bullets, nothing stops bullets without being visible. Fallback if collision is too slow under rollback: voxel-exact collision with visual bevels <= 4 cm.
- **Heights:** levels stay 1.2 m as the design language. One sub-cell step (0.3 m) is walkable (it reads as a 45 degree slope). Jump clears up to 1.2 m, mantle up to 2.4 m, 2.7 m and up is a wall. In-between heights (0.9, 1.5, 2.1 m) are allowed (Ben: yes).
- **Pieces:** a sculpt grabs the tile you aim at, grown up to one tile out in an irregular, non-rectangular shape that follows the terrain it starts from (connected rock at about the same height). Computed at aim time, deterministic, shown as a ghost outline before confirm. Typical ~2x2 tiles, max ~3x3 tiles across (Ben confirmed the 2x2 reading). The piece moves as one rigid unit. No drag-select.
- **Actions:** push/pull moves a piece one tile (1.2 m); raise/sink moves by one level. Shape is removed.
- **Scaling with size** (N = sub-cells, reference = a 2x2-tile, one-level piece): cost = base x N/ref clamped 0.5x..2x (reinforce +15% stays flat); windup = 12 ticks at or below ref, +1 tick per extra 1/8 of ref, max 20; reinforced windups stay as minimums. Reinforcement is per piece.
- **Shove/carry:** same rules on the piece's real shape: lift onto the new surface if within one level, else shove at most one tile, else invalid. Never trapped. **Riders on a sideways move (Ben, 2026-11-20):** a player physically in the path of a pushed or pulled piece is fully shoved, but a player standing on top of a piece that moves sideways is not carried the full tile: only friction drags them, a deterministic horizontal kick along the move (starting value 7.7 m/s, which slides a standing player about 0.6 m, roughly half the travel; tunable), so they may end up partly over the edge or drop off. A rising piece still carries its rider up.
- **Support/gravity:** a piece stays up if at least a third of its underside rests on something (replaces "held only by the block directly below"). Unsupported pieces fall rigidly one level per 4 ticks, solved bottom-up in fixed order; a falling piece still waits above a player rather than crush them.
- **Throw** (own bind): one non-reinforced piece at a time, launched along the full look direction with a slight upward lift. Numbers (Ben approved 2026-11-20, re-based on the 2x2 reference piece by the balance sim; s = piece sub-cells / reference sub-cells): windup 0.40 + 0.20 x s seconds (24 + 12 x s ticks, round to nearest tick); launch speed 20 - 3 x s m/s; gravity 15 m/s^2; launch 8 degrees up; cost 12% + 13% x s; damage (30 + 40 x s) x impact-speed factor (0.6..1.2), capped at 120. So 1 tile (s 0.25): 27 ticks, 19 m/s, 15%, 40 dmg; 2x2 (s 1): 36 ticks, 17 m/s, 25%, 70 dmg; 3x3 (s 2.25): 51 ticks, 13 m/s, 41%, 120 dmg; range about 7 to 12 m. Throw direction is locked at confirm (start of windup), so the lane warning shows exactly where it will go and alert players can sidestep it; sliding, airborne and mantling players in the lane are the intended punish cases. After launch: 6 ticks of follow-through, then the weapon's exit time. The old starting formulas (15 + N/4 ticks, 22 - N/16 m/s, 10% + 0.25% N, 20 + 0.6 N) are replaced: at a 2x2 piece they gave 1.3 s, 6 m/s and 74%. Slight splash, no friendly fire. Flight is fixed-point integer, client-predicted, server confirms impact. Breaks on first contact (terrain or player), destroyed, leaves the gap at launch. **Disarm (Ben, 2026-11-20):** a throw hit over 30 damage disarms the victim: no weapons and no sculpting for up to 0.5 s, scaled by how hard the hit was, then a forced redraw of the held weapon. Long-range hits are rare; in a close duel, a mistake that lets the opponent throw a wall into you should be punished. Curve (checked in the balance sim; disarm barely changes win rates because a wall-yank hit is usually fatal anyway, so it is safe): disarm ticks = 30 x clamp((damage - 30) / 60, 0, 1), so 30 dmg = 0, 90+ dmg = 0.5 s. Throwing a piece someone stands on is allowed: they are pushed along the throw direction and drop off immediately, never ride it. Launch warning: shake and team-colour cracks, rising positional grind, directional warning for enemies in the lane (never purple).
- **Netcode:** grid deltas are piece operations (moved, raised, sunk, fell, thrown, impact) by piece id; clients rebuild the surface locally. Initial map from seed plus snapshot for late joiners. Movement prediction codes against a terrain query interface, not the grid type.

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
- Pump: 9 pellets x 11 dmg, headshot pellets 1.5x, 0.9 s (54 ticks) between shots. Falloff from 5 m, heavy past 12 m. Fairly accurate in air and sliding. Swap-cancel rule (Ben approved 2026-11-20): the 54-tick pump cycle keeps running if you swap weapons or enter sculpt mode; coming back early you wait out the remainder. Swapping to another gun after a pump shot is allowed but pays that gun's draw time.
- SMG: 15 dmg, 12 shots/s, heavy falloff past 10 m.
- AR: 25 body / 40 head, 5.5 shots/s. First shot accurate, then spread builds. Strongly penalised while sliding or airborne.
- Pistol: 30 body / 50 head, semi-auto, max 5 shots/s, accurate. Damage falloff from 25 m, down to 70% at 50 m (tunable; added 2026-11-20 because with no falloff it killed beginners mid-cover at 30 m about half the time).
- Draw times (Ben approved 2026-11-20, max 0.4 s): pistol 12 ticks (0.2 s), SMG 18 (0.3 s), AR 21 (0.35 s), pump 24 (0.4 s). Exit from sculpt mode back to a weapon is shorter (see Sculpt mode).
- Hit checks use the server's view from the player's head position.

### Game modes (Ben, 2026-11-20; not built yet)
- **5v5 (main mode):** round based, like a more action-packed Valorant. First to 7 rounds, max 3 minutes per round, one life per round.
  - Objective, the **golden ball**: it spawns in the middle of the map. The team holding it when the round timer ends wins the round; wiping out the enemy team first also wins.
  - The carrier can shoot but cannot sculpt (normal movement speed unless Ben says otherwise). A killed carrier drops the ball where they died.
  - If nobody holds it at time-out, the last team to have held it wins.
  - A dropped ball returns to the nearest living player (either team) if it lies on the floor for more than 5 s, or if players are within 3 m of it but can't reach it for 5 s (deemed unreachable).
- **Small battle royale:** 16 players, a map about 2 minutes across (~700 m at run speed, ~600 x 600 tiles), with a storm.
- Scale plan for the battle royale terrain (Claude, M3.5): sub-cell chunks are sparse already: a chunk that is all one id (air, or solid rock below the surface) stores no array, and flat base ground sits on a chunk boundary, so only chunks with real surface detail cost memory (8 KB each). Next steps when the mode is built: per-chunk palettes (most surface chunks use 2-4 ids, so 2-4 bits per sub-cell), streaming meshes by distance on clients, map generation from a seed on both sides (only piece ops travel), and interest management so each client only receives piece ops and players near it. Sim cost is per change, not per tick (support checks and falls only run where something moved), so 16 players stay cheap.

### Later (don't design out)
Riot shield + pistol (frontal block, no sprint or mantle, hip-fire pistol); special objects (bounce pad, mirror block, bridge segment, heavy block) using the same six directions plus Shape; lag compensation; replay/kill-cam and free spectator camera; matchmaking.

## Milestones
- **M0** Repo and scaffolding: git, .gitignore, .gitattributes + LFS, CLAUDE.md, folders, Core asmdef, Core.Tests with one trivial test, bootstrap scene steps, push to private GitHub. (done)
- **M1** Core grid and sculpt rules: chunked storage, block types (empty, solid, ramp with facing, anchor), reinforcement state and decay, all sculpt actions and validation, gravity, shove/carry, sculpt bar economy, windup and tile locks. Thorough tests.
- **M2** Core movement + offline playable: box-vs-grid collision incl. ramps, all movement states and tech, tests. Unity: chunk meshing with per-axis shading, test map (flat, raised blocks, ramps, 1/2/3-level walls), first-person controller driven by Core.
- **M3** Networking: FishNet, server-authoritative, prediction/reconciliation, host mode, two players same machine or LAN, simulated latency and packet loss. Smooth at 100 ms simulated ping. (done on branch m3-networking; needs Ben's Unity check)
- **M3.5** Terrain v2 (after M3, before M4): A) sub-cell storage, piece finder, surface kernel, geometry (box, ray, ground), tests; B) movement on a terrain query interface, port movement tests via a whole-tile map builder; C) sculpt system on pieces (port SculptTests/RulesTable/fuzz invariants, rewrite ramp tests, new piece/support/scaling tests); D) throw; E) Unity mesher, materials, debris. (Core done on branch m3.5-terrain-v2 with 174 tests; Unity mesher, rock shader and the generated arena done but not yet seen in the Editor; debris/dust particles move to M4 with the sculpt visuals.)
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
Tests/Game.Check/                  compile-only check of Game + Core + FishNet against the Editor DLLs (dotnet build Tests/Game.Check)
```

## Machine notes
- Project path: `E:\Unity\sculpt`. Editor: Unity 6000.4.1f1. Shell: PowerShell on Windows 11.
- `gh` CLI is not installed; push with plain git over HTTPS (Git Credential Manager handles sign-in).
- .NET 8 SDK is a user-scope install at `%LOCALAPPDATA%\Microsoft\dotnet` (the one in Program Files is runtime only). Run it by full path with `DOTNET_ROOT` set to that folder.

## Agreed rules (proposed by Claude, approved by Ben 2026-11-20; change here if he overrides)
1. ~~Support: a block is held up only by the block directly below it. No overhangs.~~ Superseded by Terrain v2: a piece stays up if at least a third of its underside is supported.
2. Height: base ground top = level 0. Column tops range -2..+4. Cells below -2 are bedrock. Raise past +4 / sink below -2 is invalid.
3. Range: eye to nearest point of the targeted block, <= 6.0 m.
4. Line of sight: clear ray from eye to the aimed point on the face.
5. (Shape removed in Terrain v2; kept for reference.) Ramps: wedge filling the cell. Clicked triangle = low edge (ramp rises away from that side). Only a column's top block can be a ramp; a block landing on a ramp flattens it.
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

### Rejection philosophy (Ben, 2026-11-20)
- Rejections must be clear: the ghost and feedback go RED, never purple (purple means sculptable only).
- Make rejections as rare as possible. Prefer a sensible outcome over a refusal; reject only for true dead ends (e.g. bedrock, anchors, no headroom).

### Rule calls from Ben after the hardening suite (2026-11-20)
- Shove is capped at one cell. A player on a moved ramp's slope is lifted onto the moved slope if that stays within one cell, otherwise shoved horizontally at most one cell, otherwise the action is invalid.
- A reinforced sink with nothing sculptable left underneath is rejected (NothingToReinforce), at no cost.
- Fixed in the same batch: a block landing on a ramp waits if flattening the ramp would overlap a player; an anchor holds what is on top of it but not what hangs under it; lifting re-checks both changed cells so a raised ramp column carries a crouched player on its lip.

### Smaller calls made by Claude in M1 (Ben: "looks good", 2026-11-20)
- A falling block never enters a cell overlapping a player: it waits above until the cell is clear.
- A stack with empty space under it falls together as one unit.
- Raise treats the column's top block as the block being changed (for enemy reinforcement cost and windup).
- Reinforced sink reinforces the newly exposed top block.
- One windup per player at a time. A new confirm during a windup is rejected (Busy).
- If anything writes to a locked cell during windup (e.g. a falling block), the action cancels at resolution and refunds.
- Pushing a block horizontally does not drag a player standing on it; they fall through movement gravity.
- Sculpt tint fades over 10 s (cosmetic, tunable).

### Smaller calls made by Claude in M2 (Ben: "looks good", 2026-11-20)
- FOV setting is horizontal degrees defined at 16:9 (Hor+ on wider screens). Default 95.
- Feel helpers: 5-tick coyote time and 6-tick jump buffer. Jump on an upward ramp adds the ramp's upward speed.
- Air mantle: pressing jump while touching a ledge 0.3 to 2.45 m above your feet mantles (forgiving catch).
- Slide needs to start from a sprint at 5 m/s or more. Steering while sliding is 1.6 rad/s; pressing back does not steer.
- After a vault you're briefly airborne, so holding crouch through it turns into a slide-land.
- Mouse look is applied every rendered frame (not per tick) for lowest latency; vsync off by default.
- Rendering is unlit with baked per-axis shading and thin dark block edges (custom URP shader `SCULPT/Voxel`).

### Netcode design and smaller calls made by Claude in M3 (tell Ben; change if he disagrees)
- FishNet (free, 4.7.4, UPM git URL pinned to the tag) is used only as the transport: connections plus opaque byte packets over Tugboat UDP. No NetworkObjects or prefabs. All netcode is pure Core (`Assets/Scripts/Core/Net`) and unit-tested over a simulated lossy link.
- Tick-aligned: a client command is tagged with the server tick it is for. The client clock runs ahead so commands arrive ~2 ticks early (server reports the worst lead over 1 s; client speeds up or slows down by up to 3%, or jumps when far off).
- Each input packet repeats the newest 8 commands, so lost packets cost nothing. A missing command is replaced by the previous one with presses cleared; a press that arrives late is applied on the next tick, never dropped.
- The client predicts with exactly the quantized command it sends and the server sends the full motor state every tick; any bitwise difference rewinds to the server state and replays. Only the drawn position is smoothed (80 ms); corrections over 2 m snap.
- Remote players are drawn 3 ticks (50 ms) behind the newest snapshot, interpolated; at most 6 ticks of horizontal extrapolation when snapshots stop. A jump over 4 m (respawn) is never interpolated.
- Server keeps 64 ticks of every player's position, and each command carries the tick the client was drawing others at, ready for lag compensation in M5.
- Offline play runs a real local server too, so every mode uses the same prediction path. Host = that plus a FishNet server. Sim ping/loss/jitter apply to the local player's link in every mode (F8/F9/F10).
- Respawn (R) is a command: the server applies it, the client predicts it. Falling below -30 m respawns on both sides.
- Spawns: the 3x3 spawn pad, centre first; teams alternate 1, 2. Remote players are team-coloured boxes the size of their collision box (blue, orange; never purple) with a dark visor showing look direction.
- Movement config is not synced yet: all players must run the same build. Live tuning in the Editor only affects that Editor's server and client.
- Terrain is not networked in M3 (each side builds the same map). Sculpt/piece deltas come in M4 on top of terrain v2.

### Terrain v2 calls made by Claude in M3.5 (tell Ben; change if he disagrees)
- Surface kernel: each 0.3 m dual cell (around a sub-cell lattice vertex) is one convex shape: the hull of its occupied sub-cell centres grown by 0.15 m (an L1 ball), cut by 26 fixed plane directions. Flat floors and walls are exact; convex edges get 45 degree chamfers 0.15 m wide and concave edges matching fillets; a lone sub-cell is a small diamond. Tie-break: diagonal contacts connect (no hairline gaps for bullets). Box tests are exact separating-axis checks, so render, collision and bullets agree.
- Ramp tiles in hand-built maps are 4-step staircases, so the slope sits 0.15 m toward the low side compared with the old wedge.
- Movement on chamfers: pressing into a wall never lifts you onto its 0.15 m base fillet (and you step back off it if you land there); ramp launch fires after at least 4 ticks of climbing when the ground ahead falls away (a crest chamfer or a drop), while plateaus and short bumps keep you grounded; slopes never trigger a vault; vault and mantle ledges are found on the 0.3 m lattice, so in-between heights work.
- Pieces: aiming at a carved piece grabs that whole piece; aiming at natural rock grabs the aimed tile's level grown up to one tile out (reach per compass sector from a hash of the tile), only columns whose rock top is within 0.3 m of the aimed one, flood-connected. Natural rock is carved into a piece on confirm and stays carved if the windup is cancelled.
- Raise moves the piece up one level and fills the level it swept through with rock (the column grows). Sink moves it down one level through the natural rock below, which it removes (the column shrinks); another piece, an anchor or bedrock below blocks a sink. Every move checks the whole swept path, so thin pieces never tunnel through thin walls.
- Height range: sub-cells from -12 up are sculptable, so a one-level slab bottoms out with its top at level -2; tops go up to +4. Rock below -12 just holds the world up.
- Reinforce applies to the piece being acted on for every action (a reinforced sink reinforces the sunk piece, so the old "nothing to reinforce" rejection is gone). It moves with the piece; any enemy change removes it.
- Gun lock: a piece's owner is the team that last sculpted it. Acting on an enemy-owned piece or throwing locks the gun; natural rock and your own or your team's pieces don't.
- In the path of a push: lifted at most 0.3 m (clipping a chamfer), else shoved at most one tile (ending on the piece's base fillet is fine), else invalid. Raise/sink: lifted up to one level, else the shortest of the four horizontal shoves up to a tile, else invalid. Riders on a sideways move get the 7.7 m/s friction kick only.
- Support: a piece under the one-third rule falls one sub-cell per tick (one level per 4 ticks), bottom-up. If something blocks it straight below it stays put. Natural rock left floating (up to 2048 sub-cells, not touching bedrock or an anchor) becomes a piece and falls the same way.
- Throw details: the flight box is the piece's bounds shrunk by 0.15 m per side; the piece is lifted out of its socket in 0.3 m steps before launch; riders get a 4 m/s kick along the throw and are ignored by it for 12 ticks; the impact-speed factor is impact speed / launch speed; splash is 40% of the damage to enemies within 1 m of the impact box; one throw in flight per player; the lane warning goes to enemies within 1.5 m of the predicted path.
- The 3-tick click buffer lives in Core (server and prediction agree): a Locked or Busy confirm is retried each tick until it fires or expires.
- MotorState gained UphillTicks (ramp launch), so the net protocol is version 2.

## Balance baseline (2026-11-20)
- 1v1 simulator and baseline spreadsheet live in `Tools/Balance/` (Python 3, run on any PC; `sculpt-1v1-balance-baseline-2026-11-20.xlsx` is the baseline to compare future analyses against). Equal aim; only sculpt speed and decisions differ.
- Baseline results with the approved numbers (SMG 5 m): pro vs beginner 75%, pro vs non-sculptor 89% (94% with throws), speed alone pro vs beginner 69%, plain vs always-reinforcing pro 50%, pure turtle broken ~100% within ~8 s. One close fight uses 20-40% bar, so costs and regen don't decide single duels; below ~20% bar going in is a big disadvantage.
- Geometry facts: 1-high + crouch is full cover; a standing peek over 1-high shows the top 0.6 m; +1/+2 height barely sees a crouched player hugging a 1-high wall. Enemy cover is only reachable when players are within ~7.8 m.

## Open questions
- Pump duels: sculpt skill matters least with the pump (pro vs beginner only ~55-63% in the sim; pump peeks over 1-high cover are mostly headshot pellets). Damage, headshot and fire-rate tweaks didn't move it. Needs Ben's play-test before tuning.
