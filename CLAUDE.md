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

## PIVOT 2026-11-21 (Ben): destruction + healing + momentum. Supersedes the sculpt game below wherever they differ.
Ben: "sculpting everywhere was becoming a bit weird and lower skill than i thought". The crystal rework (never pushed) is dropped. Work happens on branch `claude/project-thread-95lhbi` off `m6-feel`.
- **The game:** fast first-person PvP on a dense map of buildings. Walls are destructible: attackers break walls to make their own way in, defenders heal walls to keep them out. A fight wrecks a few walls in one building, never the whole map. High-skill movement that keeps momentum through walls, slopes and stairs should beat a player who only has good aim. Simple to pick up (ages 12-25, not a puzzle game). Later 5v5 rounds or battle royale; no game modes yet.
- **Grid and scale:** everything on the 90 degree grid. Walls are square 1.5 m tiles; a storey is 3 tiles (4.5 m), buildings ~1.5x real scale. Holes must always be at least player sized, large and roughly square or round, never tiny or odd gaps you can be shot through (Ben, twice). A sliding player fits a one-tile hole; standing needs two.
- **Tiles:** states Intact, Damaged (cracks below half health), Broken (gone, no collision), Patched (healed, lower max health, clearly looks patched), Reinforced. No mesh fracture: a state swaps look and collision; short pooled debris and dust only. Wall state is plain data (tile id + state + health), every change through one function, server-authoritative (networking exists; no new networking in this task).
- **Unbreakable for now** (colour-coded): floors, ceilings, stairs, ramps, frame (columns, beams). Ben wants breakable floors, stairs, ramps and frame soon after, done well (step 8).
- **Breaking:** bullets chip (per-weapon tile damage in config); grenades break ~3x3 tiles instantly; charge: running into a Damaged or Patched tile at 80%+ of top sprint breaks it and keeps most speed (Intact/Reinforced stop you); punch (own bind, not an item): half an unreinforced tile (50) and 30 to players.
- **Healing (the sculpt key now):** aim at a Damaged or Broken tile within sculpt range, press heal: 0.8 s, 10% bar, comes back Patched (max 50). The hole stays open until the heal finishes. Can't finish with a player inside the tile (cancel + refund). Reinforce: 25% bar, health 200. Anyone can heal or reinforce any tile, one at a time. Bar refills 2%/s plus 1% per 10 damage dealt to players. Keep the sculpt bar, reinforce bind, faint glow on sculptables, strong highlight while held, crosshair selection, close range (6 m).
- **Parked:** six-direction block moving (push, pull, raise, sink, throw) is behind ONE switch: Breach Settings > Block Moves Enabled (default off; Core `SculptConfig.BlockMovesEnabled` stays true by default so the parked system's tests keep running). Nothing deleted.
- **Items:** every item (guns, grenades) is a hotbar item you pick up or drop with the interact key (Ben chose: build pickup/drop now). Only the punch has its own bind. Grenades in slot 5, carry 2. Own grenades do no damage but give a lot of momentum; enemy grenades damage and knock back less. Guns unchanged (pump, AR, pistol, SMG).
- **Movement (momentum):** keep sprint, slide, vault. Jumps keep horizontal speed; landing into a slide keeps it; slides speed up downhill, slow uphill; vaults and mantles keep speed; stairs behave like ramps; wall kick; speedometer. Ben's skill answers: beginners must still slide consistently (timing only adds); air strafe only a small direction nudge per jump, never enough to dodge bullets; chaining with a soft cap; stairs, ramps and outdoor slopes/hills are major to movement; counter-strafe: pressing back or sideways at speed gives a strong brake (only matters at speed, never an instant direction change); keep it consistent and readable.
- **Config:** all destruction/heal/bar numbers in ONE ScriptableObject (Breach Settings, `Game/Config/BreachSettings.cs`); movement numbers stay in Movement Settings.
- **Build order (each step: playable, tests pass, commit):** 1 momentum + speedometer + F6 showcase (done, see below); 2 tile walls with states and bullet damage; 3 item pickup/drop with grenades as first item; 4 grenades (self-boost, knockback), punch and charge breach; 5 heal, reinforce, new bar; 6 greybox block (3 buildings, 2 storeys, wide doors, long corridors, open stairwells, ramps, rooftop links, outdoor hills); 7 practice dummy that re-heals broken tiles after a delay; 8 breakable floors, stairs, ramps, frame.

### Step 1 calls made by Claude (2026-11-21; tell Ben, change if he disagrees)
- **Building world:** `Core/World/BrushWorld.cs`: exact axis-aligned boxes and wedges (ramps, stairs) in a 3 m spatial hash, on the same `ITerrain` interface movement, bullets and aiming use. No chamfers (a 1.5 m hole is exactly 1.5 m). `CompositeTerrain` = sub-cell ground + brushes. `TerrainHit.Brush` = brush index + 1. Tops sit on the 0.3 m lattice so vault/mantle ledge search works. No Unity colliders or prefabs: tiles will be Core data drawn from code (prefab-per-tile would break exact prediction and the "no hand-edited YAML" rule).
- **Demo map:** `BreachMap` (163 m square of flat sub-cell ground plus brushes; Bootstrap "Breach Map" tick box, on by default, overrides the old Map dropdown). The movement course runs along its west side.
- **Heights for 1.5 m tiles:** vault up to 1.55 m (one tile), mantle up to 3.05 m (two tiles). Jump apex unchanged (1.3 m).
- **Mantle:** keeps the speed you hit the ledge with (min 3 m/s as before). Mantle cancel: jump again from rise tick 8 once within 0.6 m of the top to pop over with that speed instead of the slow finish.
- **Wall kick:** jump in the air while touching a wall (not a step): you leave toward where you look (mirrored if you look into the wall) plus a 2.5 m/s push off it, 6.5 m/s up, at least 6 m/s. Within 100 ms of hitting the wall you keep the speed you hit it with; later 85%. Max 3 per airtime, 0.2 s apart, never the same wall twice in a row. Air mantle has priority when there is a ledge.
- **Counter-strafe brake:** on the ground or sliding above 6.5 m/s, a wish direction at or past 75 degrees from your motion brakes at 120 m/s^2 down to 6.5 m/s (letting go is 50). Only removes speed. At sprint speed it adds little (normal accel is already strong); it matters after slides, landings and kicks.
- **Air:** below run speed the old air control; above it only a 0.6 rad/s heading nudge (about 10 degrees per jump), never adds speed; pressing against the motion brakes lightly (3 m/s^2). Air soft cap 14 m/s (bleeds 4 m/s^2), for explosion boosts later.
- **Slide-hop tax:** a slide-land within 1 s of a slide-jump keeps 95% (a wall kick in between exempts it), so flat ground slowly costs speed and slopes/stairs are where speed is built. Without it, jump-spam on flat ground kept 14 m/s forever.
- **F6 movement showcase (dev builds):** warps you (a real command the server applies, only when `NetConfig.AllowDevWarp`) to the course start and plays `Core/Movement/ShowcaseRun.cs`: a closed-loop tool-assisted run that only produces normal inputs through the real movement code. Route: slide the 18 m stair flight to 14 m/s, slide-hop a gap, downhill landing ramp, corner over a drop turned with a wall kick, corridor hops and a kick off the wall over a 6 m gap, air mantle + mantle cancel up a 3 m ledge, sprint vault over a 1.5 m block, counter-strafe stop on the finish pad (~13 s). Any move key or F6 again hands control back. A test (`ShowcaseRunTests`) fails if a movement change breaks the route.
- **Speedometer:** dev HUD (F2) and always during the showcase: big m/s readout, bar with run / sprint / charge (80% sprint) / cap marks, 3 s peak and trace.
- **Stairs:** collide as a ramp, drawn as 0.15 m steps whose treads are centred on the slope (within 7.5 cm of what you touch) with yellow noses. Greybox colours: floors warm concrete, frame dark blue steel, stairs concrete + yellow, ramps light concrete + yellow edges, plain walls slate, finish pad green. Never purple. Shader `SCULPT/Greybox` adds faint 1.5 m tile seams.
- Protocol version 8 (new motor state fields, warp bit). Fuzz invariant "vault over more than one level" now reads the configured vault height (spec change, not a weakened test).
- Tests stay plain NUnit run with `dotnet test` (Core has no Unity references), not Unity edit-mode tests; same coverage, runs anywhere.

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
- Windup: 0.15 s normal (9 ticks; Ben 2026-11-20, was 0.2 s). During windup the block's edges crackle in the sculptor's team colour and a positional hum plays that enemies can hear. The tile is LOCKED during windup (nobody else can target it).
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
- **Actions:** push/pull moves a piece 1.5 m (5 sub-cells; Ben 2026-11-20 asked for "30% further" than one tile, 1.5 m is the nearest exact step); raise/sink moves by one level (1.2 m). Shape is removed.
- **Scaling with size** (N = sub-cells, reference = a 2x2-tile, one-level piece): cost = base x N/ref clamped 0.5x..2x (reinforce +15% stays flat); windup = 9 ticks at or below ref, +1 tick per extra 1/8 of ref, max 15; reinforced windups stay as minimums. Reinforcement is per piece.
- **Shove/carry:** same rules on the piece's real shape: lift onto the new surface if within one level, else shove at most the push distance (1.5 m), else invalid. Never trapped. **Riders on a sideways move (Ben, 2026-11-20):** a player physically in the path of a pushed or pulled piece is fully shoved, but a player standing on top of a piece that moves sideways is not carried the full tile: only friction drags them, a deterministic horizontal kick along the move (starting value 7.7 m/s, which slides a standing player about 0.6 m, roughly half the travel; tunable), so they may end up partly over the edge or drop off. A rising piece still carries its rider up.
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

### Keybinds (Ben approved 2026-11-20)
Every bind is rebindable in the Esc menu, dev keys included. "Second" means a second binding on the same action.
- Movement: W/A/S/D move, mouse look, Space jump/mantle, Left Shift sprint, Left Ctrl crouch/slide (second: C). Settings for sprint hold/toggle and crouch hold/toggle, both default hold.
- Guns: LMB fire, RMB aim (light zoom on AR and pistol; on pump and SMG it only drops sprint early, as now), R reload (ammo and reloads are in, Ben 2026-11-20), 1 pump, 2 SMG, 3 AR, 4 pistol, 5 utility (empty for now). Zoom hold/toggle setting, default hold. The mouse wheel does nothing in gun mode (so a stray scroll after leaving sculpt mode can't swap weapons). Optional "Last weapon" action, unbound by default.
- Sculpt: Q toggle sculpt mode, LMB push, RMB pull, wheel up raise, wheel down sink, E throw (confirmed by Ben), hold Mouse 4 (back side button) to reinforce, second: Left Alt. Reason: Left Alt + Shift is the Windows keyboard-layout switch and you can sculpt while sprinting.
- Menus: Esc settings. Reserved for later: Tab scoreboard, Enter chat, G drop/pass golden ball (only if that mechanic is ever added).
- Dev keys: their own "Dev" action map, own section in the settings menu, only in dev builds (Development Build or the Editor), never shipped. F1 respawn (moved off R), F2 toggle dev HUD (moved off F1), F3-F7 free (the old FOV/sensitivity/head bob/FOV kick shortcuts are removed; the Esc menu covers them), F8 sim ping, F9 sim loss, F10 sim jitter, F11 host/offline, F12 join box.
- Rebind menu: binding a key already used by another action in the same context (always / gun mode / sculpt mode) warns and offers to swap the two. LMB/RMB shared between gun and sculpt mode is intended and is not a clash.

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
- **M4** Sculpt mode networked: inputs, visuals, ghost, windup and locks, bar UI, targeted warnings, server validation, delta sync. (done on branch m4-sculpt-net with 237 Core tests; Game layer compile-checked, not yet seen in the Editor)
- **M5** Weapons networked: loadout, swaps, damage, falloff, accuracy states, health, death/respawn, hit markers. (done on branch m5-weapons with 285 Core tests, lag-compensated rewind included; ISSUE-8 and ISSUE-9 fixed; Game layer compile-checked, not yet seen in the Editor)
- **M6** Feel pass: audio, snap/overshoot, ripple, speed effects, compass, settings menu (FOV, sensitivity, rebinds, viewmodel size). (done on branch m6-feel with ISSUE-10 and ISSUE-11 fixed; Game layer compile-checked, not yet seen or heard in the Editor). Last roadmap milestone: next steps are Ben's play-test and his answers to the open questions.

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

### Netcode and sculpt calls made by Claude in M4 (tell Ben; change if he disagrees)
- Sculpt confirms ride inside the 60 Hz input commands (same tick, same 8x redundancy, late presses carried to the next tick), not a separate message: lowest latency and the server validates at exactly the tick the client predicted. Each confirm has a sequence number the server echoes.
- Server to client: one reliable message per tick (MsgType 4, World) with that tick's piece ops and sculpt events (began with action type, direction, windup, cost, warned players; rejected; completed; cancelled; landed; shoved; dragged; thrown; impact; hit). Thrown pieces fly deterministically on clients from their launch state (fixed point, same code), so there is no entity stream. Late joiners get the whole op log plus windups in progress and pieces in flight. Bar value rides in the snapshot. Protocol is version 3.
- Hand state (weapon / sculpt enter / sculpt / exit / drawing / disarmed, gun lock) lives in the predicted player state and is reconciled like movement. Enter 6 ticks, exits 6/9/10/12, draws 12/18/21/24 as approved. Pressing Q or a weapon key while the gun is locked queues the exit; it fires the tick the lock ends. Throws lock the gun for the windup plus 6 ticks of follow-through; you are not auto-kicked out of sculpt mode after a throw.
- Your own sculpts are predicted: the windup tell, hum and bar spend show on the click, and the piece moves in your view on the exact tick the server moves it, at any ping. Predictions sit in a journal on top of the server's state and are rolled back exactly when server ops arrive; if the server refuses, the piece snaps back and you get the red feedback. Other players' actions appear when the server's ops arrive.
- The client validates with a mirror of the server's sculpt system (same code), so the ghost goes red for the same reasons the server would refuse. A confirm the client thinks is invalid is still sent (the server decides; refusals are free).
- Binds (all rebindable): Q toggle, LMB push, RMB pull, wheel up raise, wheel down sink, hold Left Alt reinforce, E throw, 1-4 weapons. On a top face the triangle under the crosshair picks the axis and LMB moves the piece toward that edge; on a side face LMB pushes into the face. The hover ghost and cost flash show the LMB action. Magnetism: rings of rays at 1.5 and 3 degrees (up to 7 degrees when looking down at your feet), terrain only, so players are never selectable.
- Look: sculptable rock within 6 m has a faint purple glow on its chamfered edges (brighter in sculpt mode, with a ripple on entering); in sculpt mode out-of-range rock dims and non-sculptables desaturate. All of it is on the local terrain material only; remote players use their own material and are drawn after every overlay, so nothing is ever drawn over an enemy and enemies are never desaturated. The bar is a white arc (purple stays reserved), the cost portion flashes, red when you can't afford it.
- Tells: the piece's edges crackle in the sculptor's team colour with chevrons for the action (along the push, up for raise, down for sink); a throw shakes, cracks across the top and points along its throw, and enemies in the lane see the predicted path in the thrower's team colour. The targeted warning is a purple pulse at the bottom edge (as specified) plus an arrow toward the sculptor and a low thump from below; the throw-lane warning uses the thrower's team colour, never purple.
- Placeholders until M6: all sounds are generated in code; the move is an instant snap with dust and a thunk (the overshoot animation needs the piece drawn separately from the terrain mesh, M6); HUD is IMGUI.
- Movement still runs against the client's current terrain when replaying, so a sculpt that lands under you can cause one small correction.
- Fixed from the test-suite thread: ISSUE-6 (a throw replayed a stale shove plan and teleported bystanders) and ISSUE-7 (box overlap was not monotone at contact, so crouching at rest could pop you up 5 cm; the box test now projects the box corners).

### Weapon calls made by Claude in M5 (tell Ben; change if he disagrees)
- Guns live in Core (`Assets/Scripts/Core/Weapons`): every gun number is in `WeaponsConfig` (Game: optional Weapon Settings asset on the Bootstrap object, live in Play mode). Gun state (per-gun next-fire time, bloom, recoil, press buffer) and health/death sit in the predicted player state, so firing is predicted on the click tick and reconciled bit for bit like movement. Protocol version 4.
- Each gun has its own next-fire timer, which is the pump swap-cancel rule: the 54-tick cycle keeps running through swaps and sculpt mode, and coming back early waits out the remainder (plus the draw). Rates are kept exactly in milli-ticks (AR 5.5/s alternates 11- and 10-tick gaps).
- Pump and SMG/AR fire while held; the pistol is semi-auto (one shot per press, max 5/s). A press up to 6 ticks before a gun is ready is buffered and fires on the first ready tick (never in sculpt mode).
- Spread is a cone half-angle: base + move (scaled by speed up to run speed) + bloom, times a crouch factor on the ground, plus a slide or air add. AR: 0 base (first shot exact standing still), +3 deg sliding, +4 deg airborne, bloom 0.45/shot. Pump: a fixed 9-pellet pattern (centre, inner diamond, outer square, 2.6 deg) with only a small add in air/slide; standing still it is the same every shot. Random spread is seeded from tick and shot count, so client and server get the same pellets.
- Recoil moves the real aim (pitch up plus a fixed, learnable sideways pattern) and recovers by itself after a short delay; the camera includes it, so the crosshair is always on the next bullet's line. Pulling down against it is skill. Cosmetic shake is roll and an FOV punch only, never pitch or yaw. Crosshair gap shows the real spread; the pump shows its pellet ring.
- Damage is rounded once per victim per shot. Pump point blank: 99 body, 149 all heads (never a one-shot). Falloff curves (piecewise linear): pump 1.0 at 5 m, 0.65 at 12 m, 0.2 at 25 m; SMG 1.0 at 10 m, 0.6 at 20 m, 0.4 at 35 m; AR none; pistol 1.0 at 25 m to 0.7 at 50 m.
- Head zone is the top 0.36 m of the box (30% when crouched), full width; remote players' heads are drawn exactly that size, so what you see is what you hit. Teammates are not hit and bullets pass through them (friendly fire off, a config switch).
- Hit checks: the server fires from the shooter's current eye at targets rewound to what the shooter was drawing (the command's view tick, interpolated exactly like the client's remote view), at most 18 ticks (0.3 s) back, against the current terrain: cover sculpted on the server stops bullets from that tick even if the shooter hasn't seen it yet (favours the defender). Shots resolve after every player has moved, so two players shooting on the same tick trade.
- Hit markers, damage numbers and kills are shown only when the server confirms them (never predicted). Your own tracers, flash, sound and rock impacts are instant from prediction. Others see your shots from the combat message (MsgType 7, reliable per tick); only the attacker and victim are told the victim's health.
- Health 150, fall damage now applied (predicted), throw hits count toward health. Dead players are frozen, invisible and out of the world (their sculpt body is parked far below), and respawn at their spawn after 3 s with full health (dev arena only; 5v5 will be one life per round). R still respawns instantly (dev key; moving to F1, see Keybinds).
- No ammo or reload, and the Aim button (RMB) has no zoom: neither is in the spec (see open questions). Sounds, viewmodels and effects are placeholders generated in code until M6.

### Feel calls made by Claude in M6 (tell Ben; change if he disagrees)
- Q-tap rule (ISSUE-11, follows "no instant swaps"): leaving sculpt mode to the gun you came from costs its exit time; picking a different gun in sculpt mode, or entering sculpt mode mid-draw, costs that gun's full draw time. Protocol version 5.
- ISSUE-10: the dead can't send sculpt confirms, and a target the wire can't carry is dropped instead of crashing the client.
- Sculpt snap: a moved piece is drawn as its own mesh for 0.15 s on a damped spring (arrives the last 20% of the way, overshoots about 5 cm, settles) while the terrain mesh leaves it out. Visual only and short on purpose: the drawn piece is at most ~25 cm behind the real one on the first frame and within ~5 cm after 30 ms. Falls are not snapped (they already step per tick).
- Sounds are all generated in code (nothing licensed): windup hum per action (push/pull throbs, raise swells and bends up, sink groans and bends down, throw grinds up), a glassy extra layer and heavier landing with a crystalline ring for reinforced actions, layered landing thunk (sub drop, stone knock, gravel spill), the targeted warning is a double knock from below. A piece landing within 8 m gives a small cosmetic camera roll (never pitch or yaw). Your own footsteps (quiet when crouch-walking), jump, landing thud by drop height, slide scrape, vault/mantle grab, and a slide-land success sound (rising whoosh that snaps shut). Guns gained a bolt or slide clack under each shot. One-shots use a 48-voice pool.
- Reinforced pieces show pale ice-white crystalline veins with a slow shimmer and sparse glints (never purple), easing out over the last 3 s before reinforcement decays so you can read when it ends.
- Entry ripple now leaves an afterglow on the sculptables it passed, fading over ~0.35 s.
- Compass strip at the top: cardinals and intercardinals (north in amber), 5/15 degree ticks over a 120 degree span, heading in degrees under the marker.
- Speed lines: faint white streaks only in the outer ring of the screen (from 62% of the half-diagonal outward), above sprint speed, on by default with a toggle. The existing FOV kick (up to +8 degrees above sprint speed) and 1.2 cm head bob stay, both with toggles.
- Settings menu on Esc (replaces "Esc frees the mouse"): FOV 60-120 (default 95), sensitivity (shows degrees per count), weapon size 0.7-1.2, head bob, FOV kick, speed lines, master volume, and every bind (click, press the new key or button or scroll the wheel, Esc cancels, reset all). Movement is blocked while it is open; the game keeps running underneath.

### Gun answers from Ben (2026-11-20)
- Bloom is per gun, with a small penalty on swapping (replaces shared bloom).
- Ammo and reloads: yes. R becomes reload and respawn moves to F1 (a keybind thread is finalising the table).
- Light zoom on right mouse for the AR and pistol.
- SMG headshot multiplier 1.25x (18.75 head).
- Friendly fire stays off. Respawn delay stays 3 s (non-round modes).
- Throw on E and the top-face rule (LMB moves the piece toward the edge you aim at) are approved.
- Built in Core (Claude's numbers, flag to Ben; all in WeaponsConfig): magazines pump 5 / SMG 30 / AR 25 / pistol 12, unlimited reserve. Magazine reloads SMG 96 ticks (1.6 s), AR 114 (1.9 s), pistol 78 (1.3 s); you can't fire through them. The pump loads shell by shell: first shell after 6 + 30 ticks, then one per 30 ticks (0.5 s); firing a loaded shell interrupts it and shells already in stay. Reload starts on the reload press when the mag isn't full, or by itself once it is empty (after the fire cycle, so the pump racks first). Only with the gun out; swapping, sculpt mode or a disarm cancels it (a magazine reload loses its progress). Sprinting, sliding and jumping don't stop it. Each gun keeps its own magazine; respawn refills all. Swap bloom penalty: pump 0.3, SMG 0.3, AR 0.4, pistol 0.3 degrees, which doesn't recover during the draw and is gone a few ticks after the gun is out. Zoom: AimFovScale AR 0.8, pistol 0.87 (camera only, no accuracy change). Protocol version 6.
- Game side built (2026-11-20): R reload (late presses carried on the server like other presses), ammo panel with one pip per round and a reload progress bar, reload pose (magazine guns tip and roll, the pump dips per shell) and generated mag-out / mag-in / shell-in sounds, RMB zoom (AimFovScale, eased in ~80 ms, hold or toggle). The keybind table below is implemented: Dev action map (F1 respawn, F2 dev HUD, F8-F12) enabled only in the Editor or a Development Build, F3-F7 removed, Mouse 4 reinforce with Left Alt second, hold/toggle for sprint, crouch and zoom in the Esc menu, unbound Last Weapon action, the wheel does nothing in gun mode, and a duplicate-bind warning with a Swap button (contexts: always / guns / sculpt / dev; guns and sculpt may share keys). Bind overrides moved to a new PlayerPrefs key (old ones are dropped once, since R changed meaning).
- HUD (2026-11-20): IMGUI + GL in a shared style (UI/HudDraw): rounded translucent panels, health bottom left with quarter marks, gun and ammo bottom right, compact hotbar chips, kill feed chips (yours outlined amber), sculpt action chip under the crosshair (action, cost, or the red reason), sculpt key caps above the hotbar built from the live binds, bar % beside the arc. Dev HUD stays (F2).

### Look direction from Ben (2026-11-20)
- "I want the sculpts to be clearly purple tint and feel more like crystals than mud." Sculptable rock (pieces you can grab) gets a clear purple tint and a crystal look: faceted, glossy or translucent edges, a subtle inner glow. Plain non-sculptable terrain stays rock. Reinforced pieces must still read clearly differently (denser veins, brighter shimmer). Never obscure enemies.
- Play-test feedback (2026-11-20): far too much red "blocked" (log: mostly Locked, Busy and OutOfHeightRange on raise, move and sink, 41 refusals in one solo session). Rejections must be as rare as possible: buffer or queue the next action instead of refusing Busy, never let your own lock refuse you, pick the most sensible valid direction or piece rather than red, check why OutOfHeightRange fires so easily on the arena. The purple wash in sculpt mode is too heavy and covers nearby rock. The sculpt and HUD UI should be upgraded from debug text to a clean, modern look (dev HUD stays, toggleable). The terrain still reads stepped and blocky.

### Refusal fixes after Ben's first play-test (Claude, 2026-11-20; tell Ben)
- Cause of the red: wheel notches and clicks during your own windup were refused Busy; once the 3-tick buffer ran out the client re-refused the same click every tick (logged as "Locked"); and every refusal was shown twice (once by the client, once when the server's matching refusal arrived).
- Now: a confirm during your own windup is queued until that windup ends (plus the 3-tick buffer) and fires on the tick it lands. If it aims at the piece you are already moving, the aim follows the piece (two wheel notches = two raises of the same piece; line of sight was checked at the click). The newest queued click silently replaces an older one. A refusal already shown by the client is not shown again. Protocol version 7 (new silent result Superseded).
- Raise and sink always act on the top of the column you aim at (aiming at the side of a 2-level wall and scrolling up grows it to 3 levels instead of refusing because rock sits above the aimed level). Range is measured to what you aimed at.
- OutOfHeightRange in the log was mostly the doubling plus raising past +4; the +4 cap and -2 floor stay as true dead ends.
### Look direction 2 from Ben (2026-11-20, play-test)
- "making it more detailed and a bit less blocky and making it a grassy forest ... plains with flowers and grass would be a huge upgrade and like bird chirps and wind etc." Build our own stylised grassy plains / light forest biome: grass, flowers, trees, wind sway, ambient birdsong and wind. Original art direction and generated or self-made assets only (no copying another game's look or assets, no GPL). Readability rules still hold: foliage never hides a player unless it also stops bullets (so grass stays short or fades near players; trees are anchors or solid), purple stays only for sculptable, sculpted crystal must still read clearly.
- Plains biome built (Claude, 2026-11-20; tell Ben): trees are anchor sub-cells in Core (MapGen, own random stream so nothing else on the map moves): 7 per half placed in groves and alone, rotated into the other half, trunk 0.6-0.9 m sunk into flat low ground with a few 0.3 m root knuckles, lumpy canopy at least 3.3 m above the tree's ground and 2.7 m above anything under it, clear of spawns, plaza and pillars. They collide and stop bullets like rock (MapInfo.Trees lists the cells only so the game draws them as bark and leaves); leaf sway is at most 1.5 cm. Natural ground is drawn grass on top, a grass lip over each step edge, soil below it and rock deeper down (mesher passes the depth below the column top in uv1.y; kinds in uv1.x: 0 stone, 1 ground, 2 bark, 3 leaves; shared palette in Resources/SculptNature.hlsl). Meadow: GPU-instanced tufts (under 0.3 m) and about 3% flowers (yellow, white, red, sky blue; never purple) on exposed natural tops, per chunk, rebuilt with the chunk; squashed flat within ~1 m of any player, shrunk past ~46 m, shorter on dirt patches; a column whose contents changed since load (dug, carved, filled) stays bare for the match. Shared wind (World/GrassField.cs `Wind`) drives grass, leaves, clouds and the wind sound. Procedural sky (SCULPT/Sky) with sun and drifting clouds, haze fog 60-330 m matching the horizon, distant hills and tree lines outside the wall (World/Scenery.cs). Ambience (Audio/AmbientAudio.cs, generated): wind bed following gusts, leaf rustle, five generated bird calls sung from tree canopies within 55 m, quiet and high so it never masks footsteps or hums; own "Ambience" volume slider in Esc settings.
- Play-test fixes done (commits 80db604, 89bf20a on m6-feel): HUD GL was upside down in the D3D Game view (HudDraw.LoadPixels); piece ghosts and flying pieces meshed a 9.6 m bedrock box (ChunkMesher.OnlyId); hovering never shows red (only refused clicks do); top-face push falls back to the next free edge; dev HUD hidden by default (F2).

## Balance baseline (2026-11-20)
- 1v1 simulator and baseline spreadsheet live in `Tools/Balance/` (Python 3, run on any PC; `sculpt-1v1-balance-baseline-2026-11-20.xlsx` is the baseline to compare future analyses against). Equal aim; only sculpt speed and decisions differ.
- Baseline results with the approved numbers (SMG 5 m): pro vs beginner 75%, pro vs non-sculptor 89% (94% with throws), speed alone pro vs beginner 69%, plain vs always-reinforcing pro 50%, pure turtle broken ~100% within ~8 s. One close fight uses 20-40% bar, so costs and regen don't decide single duels; below ~20% bar going in is a big disadvantage.
- Geometry facts: 1-high + crouch is full cover; a standing peek over 1-high shows the top 0.6 m; +1/+2 height barely sees a crouched player hugging a 1-high wall. Enemy cover is only reachable when players are within ~7.8 m.

## Open questions
- Footsteps: only your own are heard right now. Should enemies hear each other's footsteps (normal for a competitive shooter, and crouch-walking would be the quiet option)?
- Pump duels: sculpt skill matters least with the pump (pro vs beginner only ~55-63% in the sim; pump peeks over 1-high cover are mostly headshot pellets). Damage, headshot and fire-rate tweaks didn't move it. Needs Ben's play-test before tuning.
