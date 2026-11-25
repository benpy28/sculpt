"""Builds sculpt-1v1-balance-2026-11-25.xlsx from the sim results."""
import json, math
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.formatting.rule import ColorScaleRule
import duel2, ttk, tools, breach

B = Font(bold=True); INP = PatternFill("solid", fgColor="FFF2CC"); HDR = PatternFill("solid", fgColor="D9E1F2")
WRAP = Alignment(wrap_text=True, vertical="top")
wb = Workbook()
def hdr(ws, r, vals, c0=1):
    for i, v in enumerate(vals):
        c = ws.cell(r, c0 + i, v); c.font = B; c.fill = HDR
def scale(ws, rng):
    ws.conditional_formatting.add(rng, ColorScaleRule(start_type="num", start_value=0, start_color="F8696B",
                                                      mid_type="num", mid_value=0.5, mid_color="FFFFFF", end_type="num", end_value=1, end_color="63BE7B"))
def widths(ws, w):
    for k, v in w.items(): ws.column_dimensions[k].width = v

# ---------- Read me
ws = wb.active; ws.title = "Read me"
lines = [
 ("SCULPT 1v1 balance, 2026-11-25 (after the destruction + momentum pivot)", True),
 ("Numbers: live defaults on branch claude/project-thread-m8m4r9 (protocol 24, 120 Hz). Compare with balance/baselines/sculpt-1v1-balance-baseline-2026-11-20.xlsx (the old sculpt game; most of it no longer applies).", False),
 ("Sim: Tools in balance/sim-2026-11-25 (duel2.py, matrix.py, variant.py, ladder.py, tools.py, breach.py, ttk.py). Python 3, no Unity needed.", False),
 ("How the gun sim works: open ground, both see each other at once, fixed distance, sideways movement. Aim = reaction time, tracking that lags the target by 0.11-0.20 s and extrapolates its velocity, hand error, recoil the player fails to pull down, plus the gun's real spread cone, bloom, recoil and the pump's 9-pellet pattern. Body 0.7 m wide, 1.8 m (1.1 crouched/sliding), head 0.6 x 0.33 m.", False),
 ("Aim tiers (Inputs tab): beginner, mid, pro. 'Equal aim' tables use mid for both players.", False),
 ("", False),
 ("Main findings", True),
 ("1. SMG is the best gun from 0 to ~10 m and the pump loses to it even at 3 m (36-41%). Pump 2-shot TTK (0.9 s) is no faster than the SMG's 12 shots (0.92 s), and a pump miss costs 0.9 s.", False),
 ("2. The pistol beats the AR from 10 to 25 m (57-66%) at mid aim, and at pro aim it is the best gun at every range (4 headshots = 0.6 s). The AR only wins past ~30 m at mid aim.", False),
 ("3. Standing still loses almost every fight; strafing while shooting is the best movement at every range. Slides are for getting places, not for dodging (a steady slide is easy to track).", False),
 ("4. Breaching is always worth it: one AR/pistol/pump hit and a sprint gets you through a plain wall for ~0.3 s, against 2-4.5 s for walking round. The divide only slows players who don't know the charge rule.", False),
 ("5. Healing never stops a charge: patched cells count as 'hit', so a healed wall can be sprinted through with no shot at all. Heal-spam is not a problem (it loses tempo: 0.2 s + 0.2 s gun lock + 10% bar vs one bullet to undo).", False),
 ("6. C4 and the launcher each cost a gun slot and are weak in a 1v1 (launcher ~10-30 dmg per round vs a moving player, 4 rounds; C4 only matters against reinforced walls). Punch locks the gun for 0.8 s: useless in a fight.", False),
 ("7. Skill cap is high and beginners are not helpless vs mids: see Skill ladder. Tactics (moving, weapon choice) beat raw aim from 10 m out; raw aim wins up close.", False),
 ("", False),
 ("Recommended changes (Ben chooses): see the Proposals tab.", True),
]
for i, (t, b) in enumerate(lines, 1):
    c = ws.cell(i, 1, t); c.alignment = WRAP
    if b: c.font = B
ws.column_dimensions["A"].width = 150

# ---------- Inputs
ws = wb.create_sheet("Inputs")
ws["A1"] = "Live numbers used (yellow = what the formulas on the TTK tab read; change them to try ideas). Seconds, metres, damage."; ws["A1"].font = B
hdr(ws, 3, ["Gun", "Pellets", "Body dmg", "Head dmg", "Seconds between shots", "Base spread deg", "Move spread deg", "Bloom/shot", "Bloom cap", "Slide add", "Air add", "Mag", "Draw s"])
rowof = {}
for i, (k, w) in enumerate(duel2.WEAPONS.items()):
    r = 4 + i; rowof[k] = r
    vals = [k, w["pellets"], w["body"], w["head"], round(w["interval"], 4), w["base"], w["move"], w["bloom"], w["bmax"], w["slide"], w["air"], w["mag"], w["draw"]]
    for j, v in enumerate(vals):
        c = ws.cell(r, 1 + j, v)
        if j in (1, 2, 3, 4): c.fill = INP
ws.cell(9, 1, "Falloff: pump 1.0 at 5 m, 0.65 at 12, 0.2 at 25; SMG 1.0 at 10, 0.6 at 20, 0.4 at 35; AR none; pistol 1.0 at 25 to 0.7 at 50.")
ws.cell(10, 1, "Health 150. Head 0.33 m tall x 0.6 m wide. Damage rounded once per victim per shot.")
hdr(ws, 12, ["Aim tier", "Hand error deg", "Tracking lag s", "Lead share", "Recoil pulled down", "Reaction s", "Aims this far below the top m", "Waits for a steady target up to s"])
for i, (k, a) in enumerate(duel2.AIM.items()):
    for j, v in enumerate([k, a["sig"], a["lag"], a["lead"], a["comp"], a["react"][0], a["aim_off"], a["hold"]]):
        ws.cell(13 + i, 1 + j, v)
hdr(ws, 17, ["Movement", "Value"])
mv = [("Run / sprint m/s", "7 / 10"), ("Crouch walk", 3.5), ("Ground accel/decel m/s^2", 60), ("Slide entry boost", "off (slides keep your speed)"),
      ("Slide friction m/s^2 (10 -> 4 m/s in 1.3 s)", 4.5), ("Slide free steer / carve max rad/s", "1.6 / 4.5"), ("Carve cost per extra radian", "8% of speed"),
      ("Counter-brake m/s^2 above 7 m/s", 120), ("Sprint to aim s", 0.15), ("Charge speed (80% of sprint)", 8), ("Charge keeps", "85%")]
for i, (k, v) in enumerate(mv): ws.cell(18 + i, 1, k); ws.cell(18 + i, 2, v)
hdr(ws, 31, ["Walls, heal, tools", "Value"])
wv = [("Plain / patched / reinforced cell health", "100 / 50 / 200"), ("Hit on a wall: AR / SMG / pistol / pump / punch", "50 r0.45 / 25 r0.35 / 34 r0.4 / 14 per pellet summed r0.55-0.9 / 50 r0.5"),
      ("Charge bursts through if hit share of the wall in your way >=", 0.4), ("Patched cells count as hit for a charge", "yes (Charge.cs CellWeak)"),
      ("Heal Q: time / bar / gun lock / radius", "0.2 s / 10% / 0.2 s / 0.95 m (whole hole up to 2.3 m^2)"), ("Reinforce Alt+Q", "0.8 s / 25%"),
      ("Bar refill", "2%/s + 1% per 10 damage dealt"), ("Doors", "4x damage, a charge always goes through"),
      ("Grenade", "70 max within 1 m of chest, 0 at 4 m; impact fuse; 20 m/s; carry 2; own = 15 m/s boost"),
      ("Launcher", "65 max within 0.75 m, 0 at 3.5 m; arms after 8 m; 32 m/s; 1.1 s/round; 4 rounds; 0.6 s draw; costs a gun slot"),
      ("C4", "45 max (to the box), 0 at 2.5 m; 220 to walls (breaks reinforced); arm 0.25 s, 8 s fuse; carry 2; costs a gun slot"),
      ("Punch", "30 to players, 50 to walls; lands 0.2 s after press; gun locked 0.8 s")]
for i, (k, v) in enumerate(wv): ws.cell(32 + i, 1, k); ws.cell(32 + i, 2, v)
widths(ws, {"A": 58, "B": 18, "C": 12, "D": 12, "E": 20, "F": 14, "G": 14, "H": 12, "I": 10})

# ---------- TTK (formulas)
ws = wb.create_sheet("TTK")
ws["A1"] = "Perfect accuracy, no falloff (formulas from Inputs). Shots to kill and time to kill = (shots - 1) x seconds between shots."; ws["A1"].font = B
hdr(ws, 3, ["Gun", "Body shots", "Body TTK s", "Head shots", "Head TTK s", "DPS body"])
for i, k in enumerate(duel2.WEAPONS):
    r = 4 + i; R = rowof[k]
    ws.cell(r, 1, k)
    ws.cell(r, 2, f"=CEILING(150/ROUND(Inputs!B{R}*Inputs!C{R},0),1)")
    ws.cell(r, 3, f"=ROUND((B{r}-1)*Inputs!E{R},2)")
    ws.cell(r, 4, f"=CEILING(150/ROUND(Inputs!B{R}*Inputs!D{R},0),1)")
    ws.cell(r, 5, f"=ROUND((D{r}-1)*Inputs!E{R},2)")
    ws.cell(r, 6, f"=ROUND(Inputs!B{R}*Inputs!C{R}/Inputs!E{R},0)")
ws.cell(10, 1, "With falloff (sim values): damage per shot x shots = TTK").font = B
hdr(ws, 11, ["Gun", "Hits"] + [f"{D} m" for D in ttk.DISTS])
for i, row in enumerate(ttk.table()):
    ws.cell(12 + i, 1, row[0]); ws.cell(12 + i, 2, row[1])
    for j, (d, n, t) in enumerate(row[2:]): ws.cell(12 + i, 3 + j, f"{d} x{n} = {t}s")
widths(ws, {"A": 10, "B": 12, "C": 16, "D": 16, "E": 16, "F": 16, "G": 16, "H": 16, "I": 16, "J": 16, "K": 16})

# ---------- Weapon matrix
def matrix_tab(name, path, title):
    d = json.load(open(path)); S = [tuple(s) for s in d["strats"]]; Wn = ["Pump", "SMG", "AR", "Pistol"]
    ws = wb.create_sheet(name)
    ws["A1"] = title; ws["A1"].font = B
    ws["A2"] = "Row gun's win rate when each side picks its best tactic against the other (movement x fire mode; maximin). 0.5 = even."
    r = 4
    for D in d["dists"]:
        M = d["M"][str(D)]
        ws.cell(r, 1, f"{D} m").font = B
        hdr(ws, r, Wn, 2)
        for a_i, a in enumerate(Wn):
            ws.cell(r + 1 + a_i, 1, a).font = B
            ia = [i for i, s in enumerate(S) if s[0] == a]
            for b_i, b in enumerate(Wn):
                ib = [j for j, s in enumerate(S) if s[0] == b]
                ws.cell(r + 1 + a_i, 2 + b_i, round(max(min(M[i][j] for j in ib) for i in ia), 2))
        scale(ws, f"B{r+1}:E{r+4}")
        eq = d["eq"][str(D)]
        top = sorted(range(len(S)), key=lambda k: -eq[k])
        ws.cell(r, 7, "Best play (mixed equilibrium)").font = B
        for k_i, k in enumerate([k for k in top if eq[k] > 0.02][:4]):
            s = S[k]; ws.cell(r + 1 + k_i, 7, f"{s[0]}, {s[1]}" + (f", tap {s[2]} s" if s[2] else "")); ws.cell(r + 1 + k_i, 8, round(eq[k], 2))
        r += 7
    widths(ws, {"A": 10, "B": 10, "C": 10, "D": 10, "E": 10, "G": 30})
matrix_tab("Guns (equal mid aim)", "results/matrix_mid.json", "Gun vs gun, both players mid aim, live numbers")
try: matrix_tab("Guns (equal pro aim)", "results/matrix_pro.json", "Gun vs gun, both players pro aim, live numbers")
except FileNotFoundError: pass
matrix_tab("Guns (proposed, mid)", "results/matrix_mid_proposed3.json", "Gun vs gun, mid aim, proposed: pump 0.75 s, SMG 11/s, pistol bloom 0.8 cap 2.5, pistol head 37 (this run also had crouch accuracy off, which barely mattered)")
matrix_tab("Guns (proposed, pro)", "results/matrix_pro_proposed2.json", "Gun vs gun, pro aim, proposed: pump 0.75 s, SMG 11/s, pistol bloom 0.8 cap 2.5, pistol head 37")

# ---------- Tactics
ws = wb.create_sheet("Movement")
d = json.load(open("results/matrix_mid.json")); S = [tuple(s) for s in d["strats"]]
ws["A1"] = "Each movement's win rate against the best mixed play at that range (mid aim, live numbers), best gun/fire mode for that movement."; ws["A1"].font = B
moves = ["still", "crouch", "cstrafe", "strafe", "slide"]
hdr(ws, 3, ["Distance", "Gun"] + ["Stand still", "Crouch", "Counter-strafe (shoot when stopped)", "Strafe while shooting", "Slide + carve"])
r = 4
for D in d["dists"]:
    M = d["M"][str(D)]; eq = d["eq"][str(D)]
    vs = [sum(M[i][j] * eq[j] for j in range(len(S))) for i in range(len(S))]
    for w in ["Pump", "SMG", "AR", "Pistol"]:
        ws.cell(r, 1, f"{D} m"); ws.cell(r, 2, w)
        for k, m in enumerate(moves):
            ws.cell(r, 3 + k, round(max(vs[i] for i, s in enumerate(S) if s[0] == w and s[1] == m), 2))
        r += 1
scale(ws, f"C4:G{r-1}")
ws.cell(r + 1, 1, "Time for a mid-aim shooter to kill a target that doesn't shoot back (s, mean over 300; 6 = not within 6 s)").font = B
hdr(ws, r + 2, ["Gun", "Distance", "still", "counter-strafe", "strafe", "slide", "jump"])
rr = r + 3
for w, tap in (("Pump", None), ("SMG", None), ("AR", 0.3), ("Pistol", None)):
    for D in (3, 8, 15, 25):
        ws.cell(rr, 1, w + (f" (tap {tap})" if tap else "")); ws.cell(rr, 2, D)
        for k, m in enumerate(("still", "cstrafe", "strafe", "slide", "jump")):
            ws.cell(rr, 3 + k, round(duel2.ttk_vs(w, "mid", m, D, tap=tap, n=300)[0], 2))
        rr += 1
widths(ws, {"A": 16, "B": 10, "C": 12, "D": 12, "E": 30, "F": 22, "G": 16})

# ---------- Variants
ws = wb.create_sheet("Gun variants")
v = json.load(open("results/variants2.json"))
ws["A1"] = "Tuning ideas tried (mid aim, 300 fights per pairing, maximin). Value = row gun's win rate vs the column gun. About +-4% noise."; ws["A1"].font = B
keys = list(next(iter(v.values()))["res"].keys())
hdr(ws, 3, ["Change"] + keys)
for i, (name, x) in enumerate(v.items()):
    ws.cell(4 + i, 1, name)
    for j, k in enumerate(keys): ws.cell(4 + i, 2 + j, x["res"][k][0])
scale(ws, f"B4:{chr(65+len(keys))}{3+len(v)}")
ws.column_dimensions["A"].width = 50
for j in range(len(keys)): ws.column_dimensions[chr(66 + j)].width = 13

# ---------- Skill ladder
ws = wb.create_sheet("Skill ladder")
ws["A1"] = "Win rate of the first player. Beginner = beginner aim, sprays, half the time stands still, never uses the pistol. Mid/pro = their aim, strafes, best gun for the range."; ws["A1"].font = B
r = 3
for tag, title in (("base", "Live numbers"), ("var", "With the proposed gun changes (pump 0.75 s, SMG 11/s, pistol bloom 0.8/2.5, head 37)")):
    try: L = json.load(open(f"results/ladder_{tag}.json"))
    except FileNotFoundError: continue
    ws.cell(r, 1, title).font = B
    hdr(ws, r + 1, ["Matchup"] + [f"{D} m" for D in L["dists"]])
    for i, (k, vals) in enumerate(L["res"].items()):
        ws.cell(r + 2 + i, 1, k)
        for j, x in enumerate(vals): ws.cell(r + 2 + i, 2 + j, x)
    scale(ws, f"B{r+2}:G{r+1+len(L['res'])}")
    r += 3 + len(L["res"]) + 1
ws.cell(r, 1, "Old baseline (2026-11-20, sculpt game): pro vs beginner 75%, pro vs non-sculptor 89% (94% with throws).")
ws.column_dimensions["A"].width = 58

# ---------- Tools
ws = wb.create_sheet("Grenades C4 launcher")
ws["A1"] = "Expected damage per throw/round against a target (thrower leads the target; range-judging error grows with distance)."; ws["A1"].font = B
hdr(ws, 3, ["Item", "Distance m", "Flight s", "vs standing still", "vs strafing", "vs counter-strafing", "vs sliding"])
r = 4
for kind in tools.KINDS:
    for D in (5, 10, 15, 20, 30):
        if kind == "C4" and D > 15: continue
        vals = [tools.expected(kind, D, m, n=1500) for m in ("still", "strafe", "cstrafe", "slide")]
        ws.cell(r, 1, kind); ws.cell(r, 2, D); ws.cell(r, 3, round(vals[0][1], 2))
        for j, (e, _) in enumerate(vals): ws.cell(r, 4 + j, round(e))
        r += 1
r += 1
ws.cell(r, 1, "Blast damage on a standing player by how far from their feet it lands").font = B
hdr(ws, r + 1, ["Item", "0 m", "0.5 m", "1 m", "1.5 m", "2 m", "3 m"])
for i, k in enumerate(tools.KINDS):
    ws.cell(r + 2 + i, 1, k)
    for j, x in enumerate((0, 0.5, 1, 1.5, 2, 3)): ws.cell(r + 2 + i, 2 + j, tools.blast(tools.KINDS[k], x, tools.KINDS[k].get("box", False)))
r += 6
notes = ["Gun time lost to a grenade: draw 0.2 s + throw + redraw the gun (pistol 0.2 .. pump 0.4 s) = about 0.5-0.7 s, in which an SMG deals ~50-70 at 5 m. So a grenade is a fair trade up close and a poor one past 10 m against a moving player.",
         "Launcher: needs 3 good rounds (2.2 s+) to kill even a still player; vs a moving player its 4 rounds average ~40-120 total. Dud inside 8 m. Costs a gun slot.",
         "C4: can never kill a healthy player (45 max, 90 with both). Its job (breaking reinforced walls) is also done by 2 pump shots or 4 AR bullets, so in a 1v1 it rarely beats the gun it replaced.",
         "Punch: 30 damage, but the gun stays locked for 0.8 s; a pump does 85 at 2 m. Only useful when out of ammo. On doors any gun one-shots a leaf and a sprint goes through anyway."]
for i, t in enumerate(notes): c = ws.cell(r + i, 1, t); c.alignment = WRAP
widths(ws, {"A": 14, "B": 12, "C": 10, "D": 16, "E": 14, "F": 18, "G": 12})

# ---------- Breaching
ws = wb.create_sheet("Breaching and heal")
ws["A1"] = "How many hits before a sprint (>= 8 m/s) or slide bursts through a plain wall, and the time it costs vs running a free line."; ws["A1"].font = B
hdr(ws, 3, ["Hit", "Standing, share 0.4 (live)", "Sliding, 0.4", "Standing, share 0.6", "Sliding, 0.6", "Seconds lost (live)"])
for i, g in enumerate(breach.HITS):
    vals = [g, breach.hits_for_charge(g, 0.4), breach.hits_for_charge(g, 0.4, True), breach.hits_for_charge(g, 0.6), breach.hits_for_charge(g, 0.6, True), breach.breach_cost(g)[1]]
    for j, x in enumerate(vals): ws.cell(4 + i, 1 + j, x)
r = 4 + len(breach.HITS) + 1
ws.cell(r, 1, "Duel map routes (lengths estimated from the layout in CLAUDE.md; sprint 10 m/s)").font = B
hdr(ws, r + 1, ["Route", "Walk-round m", "Straight m", "Walls", "Walk s", "Breach s", "Breach saves s"])
for i, row in enumerate(breach.route_table(0.4)):
    for j, x in enumerate(row): ws.cell(r + 2 + i, 1 + j, x)
r += 6
ws.cell(r, 1, "Heal war: attacker shoots the wall, sprints at it from X m; the defender (inside, within 6 m) notices in ~0.4 s and heals (0.2 s)").font = B
hdr(ws, r + 1, ["Attacker shoots from", "Gets through (live: patched counts as hit)", "Gets through if patched does NOT count"])
for i, dd in enumerate((1.5, 3, 5, 8)):
    ws.cell(r + 2 + i, 1, f"{dd} m"); ws.cell(r + 2 + i, 2, breach.heal_war(patched_counts=True, dist=dd)); ws.cell(r + 2 + i, 3, breach.heal_war(patched_counts=False, dist=dd))
r += 7
for i, t in enumerate([
    "Bar: 10 heals from full, then one per 5 s (2%/s), plus 1% per 10 damage dealt. Each heal costs the healer 0.2 s of gun lock; undoing it costs the attacker one AR or pump shot (patched cells have 50 health).",
    "So heal-spam can't stall a breach; it only buys ~0.2-0.3 s of cover, about the same as stepping out of the hole. Not a problem.",
    "Reinforce (0.8 s, 25%) is the only thing that stops a charge. Its 1.9 m spot covers one lane; a 6 m wall needs 3-4 presses (75-100% bar) to block every lane, so a defender picks where the attacker comes in, never whether."]):
    c = ws.cell(r + i, 1, t); c.alignment = WRAP
widths(ws, {"A": 60, "B": 26, "C": 30, "D": 18, "E": 14, "F": 18, "G": 16})

# ---------- Proposals
ws = wb.create_sheet("Proposals")
ws["A1"] = "Proposed number changes (nothing changed in the game). Ben picks."; ws["A1"].font = B
hdr(ws, 3, ["#", "Change", "Now", "Proposed", "Why / what the sim shows"])
P = [
 ("Pump fire cycle", "0.9 s (54/60)", "0.75 s (45/60)", "Pump loses to SMG at 3 m (36-41%) because its 2-shot kill is no faster than the SMG. At 0.75 s it wins 3 m (56%) and still loses at 6 m."),
 ("SMG fire rate", "12/s", "11/s", "Keeps the SMG the 5-10 m gun but stops it beating the pump at point blank. TTK 0.92 -> 1.0 s."),
 ("Pistol bloom per shot / cap", "0.5 / 2.0 deg", "0.8 / 2.5 deg", "Pistol beats the AR 57-66% at 10-25 m. With more bloom spam-firing is punished, it's even with the AR at 10-25 m and the AR wins at range. Tapping stays accurate."),
 ("Pistol head damage", "42.5 (4 heads kill)", "37 (5 heads kill)", "At pro aim the pistol wins 75-89% against every gun from 6 to 25 m. With 37 the AR takes over from 10 m and the pump owns 3-6 m."),
 ("Patched cells count as 'hit' for a charge", "yes", "no", "Right now a healed wall can be sprinted through with no shot. With this, healing really keeps people out until they shoot it again (one bullet), and a quick heal beats a lazy 'shoot from 8 m then charge'."),
 ("Charge hit share", "0.4", "keep 0.4 (0.6 if breaching feels too free)", "0.4 = one AR/pistol/pump hit; 0.6 = two AR hits. Breaching saves 2-4.5 s over walking round either way."),
 ("C4 and launcher slot", "replace a gun", "share slot 5 with grenades (pick one kind)", "Costing a gun makes both a bad trade in a 1v1. In slot 5 they're a real choice vs grenades."),
 ("Launcher rounds per pickup", "4", "6", "4 rounds average well under 150 damage vs a moving player; 6 makes it a threat at 15-30 m without changing up-close."),
 ("Punch gun lock", "0.8 s (whole swing)", "0.35 s (hit + 0.15 s)", "With an 0.8 s lock a punch is never worth it in a fight. Keep 0.8 s between punches."),
]
for i, row in enumerate(P):
    ws.cell(4 + i, 1, i + 1)
    for j, x in enumerate(row): c = ws.cell(4 + i, 2 + j, x); c.alignment = WRAP
widths(ws, {"A": 4, "B": 36, "C": 20, "D": 30, "E": 90})

# ---------- vs baseline
ws = wb.create_sheet("vs baseline 11-20")
hdr(ws, 1, ["Metric", "2026-11-20 (sculpt game)", "2026-11-25 (live)"])
cmp = [("Pro vs weaker player, close range", "75% vs beginner sculptor (SMG 5 m)", "pro vs mid 73-89%, pro vs beginner ~100%"),
       ("Same aim, better decisions", "69% (sculpt speed alone)", "72-86% (movement + gun choice alone)"),
       ("Pump", "weak spot (pro edge only 55-63%)", "still the weak spot: loses to SMG at 3 m"),
       ("Defence", "pure turtle broken in ~8 s", "defender can't seal: plain walls cost one shot, heals don't stop charges"),
       ("Reinforce", "was too strong (92-96%), fixed", "only thing that stops a charge; 1.9 m spot, easy to go round"),
       ("Long-range safety", "raise cover in time vs AR at 30-60 m", "AR needs ~2.5-4 s to kill a strafer at 25-40 m: plenty of time to reach cover")]
for i, row in enumerate(cmp):
    for j, x in enumerate(row): ws.cell(2 + i, 1 + j, x).alignment = WRAP
widths(ws, {"A": 34, "B": 44, "C": 60})

wb.save("sculpt-1v1-balance-2026-11-25.xlsx")
print("saved")
