from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.formatting.rule import CellIsRule
wb = Workbook()
B = Font(bold=True); INP = PatternFill("solid", fgColor="FFF2CC"); HDR = PatternFill("solid", fgColor="D9E1F2")
def hdr(ws, r, vals):
    for i, v in enumerate(vals, 1):
        c = ws.cell(r, i, v); c.font = B; c.fill = HDR
# ---------- Inputs
ws = wb.active; ws.title = "Inputs"
ws["A1"] = "Yellow cells are inputs. Change them and the other tabs update. Times in seconds (ticks = seconds x 60). Bar in %."; ws["A1"].font = B
rows = [
 ("Normal windup", 0.2), ("Reinforced caster windup", 0.35), ("Enemy-reinforced windup", 0.4),
 ("Sink cost", 7), ("Push/pull cost", 5), ("Raise cost", 10), ("Reinforce surcharge", 15), ("Enemy-reinforced cost multiplier", 2),
 ("Regen delay", 1.5), ("Regen 0 to 100% time", 20),
 ("Sculpt enter time", 0.1), ("Exit to pistol", 0.1), ("Exit to SMG", 0.15), ("Exit to AR", 0.17), ("Exit to pump", 0.2),
 ("Draw pistol", 0.2), ("Draw SMG", 0.3), ("Draw AR", 0.35), ("Draw pump", 0.4),
 ("Gun locked during attack windups (1=yes)", 1),
 ("Throw windup base", 0.4), ("Throw windup per s", 0.2), ("Throw speed base m/s", 20), ("Throw speed per s", 3),
 ("Throw cost base", 12), ("Throw cost per s", 13), ("Throw dmg base", 30), ("Throw dmg per s", 40), ("Throw dmg cap", 120),
 ("Throw gravity m/s2", 15), ("Throw launch angle deg", 8), ("Throw launch height m", 1.2), ("Throw follow-through after launch", 0.1),
 ("Disarm min (at 30 dmg)", 0.2), ("Disarm max (at 100+ dmg)", 0.5), ("Splash radius m", 0.3),
 ("Human visual reaction (both players)", 0.2),
]
hdr(ws, 3, ["Setting", "Value", "Old spec (before 2026-11-20)"])
spec = {"Enemy-reinforced windup": 0.6, "Sculpt enter time": 0, "Exit to pistol": 0, "Exit to SMG": 0, "Exit to AR": 0, "Exit to pump": 0,
        "Draw AR": 0.4, "Draw pump": 0.45, "Gun locked during attack windups (1=yes)": 0}
names = {}
for i, (k, v) in enumerate(rows):
    r = 4 + i
    ws.cell(r, 1, k); c = ws.cell(r, 2, v); c.fill = INP
    ws.cell(r, 3, spec.get(k, v))
    names[k] = f"Inputs!$B${r}"
r0 = 4 + len(rows) + 1
ws.cell(r0, 1, "Skill tiers (seconds)").font = B
hdr(ws, r0 + 1, ["Tier", "Notice a tell", "Aim at a piece + click", "Re-click on unlock", "Sidestep 1 piece", "Dodge sideways"])
tiers = [("Beginner", .40, .45, .12, .45, .40), ("Mid", .28, .28, .06, .35, .32), ("Pro", .18, .14, .017, .28, .26)]
for j, t in enumerate(tiers):
    for k, v in enumerate(t):
        c = ws.cell(r0 + 2 + j, 1 + k, v)
        if k: c.fill = INP
tier_row = {t[0]: r0 + 2 + j for j, t in enumerate(tiers)}
ws.column_dimensions["A"].width = 42; ws.column_dimensions["B"].width = 12; ws.column_dimensions["C"].width = 14
for col in "DEF": ws.column_dimensions[col].width = 18
N = names
def T(tier, col): return f"Inputs!${col}${tier_row[tier]}"
# ---------- Close duel checks
ws2 = wb.create_sheet("Close duel checks")
ws2["A1"] = "Per-tier timing checks for a close fight (both behind 1-high cover, within sculpt range)."; ws2["A1"].font = B
hdr(ws2, 3, ["Check", "Beginner", "Mid", "Pro", "What it means"])
checks = [
 ("Exposure after your cover is sunk (s): notice + aim + raise windup - their windup",
  lambda t: f"=MAX(0,{T(t,'B')}+MAX({N['Sculpt enter time']},{T(t,'C')})-{N['Normal windup']})+{N['Normal windup']}",
  "How long you stand uncovered before your new piece is up. SMG does about 150 dmg per second at 5 m."),
 ("Damage taken in that window from an SMG (approx 150 dps)",
  lambda t: f"=ROUND(150*B4,0)".replace("B4", {'Beginner':'B4','Mid':'C4','Pro':'D4'}[t]), "Rough cost of a slow rebuild."),
 ("Counter-break lands before an enemy-reinforced break? (notice + aim + normal windup < enemy-reinf windup)",
  lambda t: f"=IF({T(t,'B')}+MAX({N['Sculpt enter time']},{T(t,'C')})+{N['Normal windup']}<{N['Enemy-reinforced windup']},\"YES - reinforcing is too strong\",\"no - fair\")",
  "The sim found a cliff: if a pro can always counter-break first, reinforced walls win about 92-96% of pro mirrors."),
 ("Time from the enemy's tell to their earliest pump shot after a sink (s)",
  lambda t: f"={N['Normal windup']}+IF({N['Gun locked during attack windups (1=yes)']}=1,{N['Exit to pump']},0)+0.09",
  "Should be above human reaction so 'sculpt then instantly shotgun' can't happen."),
 ("...and can you react in time? (vs your notice time)",
  lambda t: f"=IF({ {'Beginner':'B','Mid':'C','Pro':'D'}[t]}7>{T(t,'B')},\"yes\",\"no\")", "yes = you see the tell before their shot can arrive."),
]
for i, (lab, f, note) in enumerate(checks):
    r = 4 + i
    ws2.cell(r, 1, lab)
    for j, t in enumerate(("Beginner", "Mid", "Pro")):
        ws2.cell(r, 2 + j, f(t))
    ws2.cell(r, 5, note)
ws2.column_dimensions["A"].width = 70; ws2.column_dimensions["E"].width = 80
for col in "BCD": ws2.column_dimensions[col].width = 26
r = 11
ws2.cell(r, 1, "Turtle bar war (attacker breaks, defender rebuilds the same piece)").font = B
hdr(ws2, r + 1, ["Cover type", "Defender cost per rebuild", "Attacker cost per break", "Rebuilds before defender is empty", "Breaks before attacker is empty", "Who runs out first"])
ws2.cell(r + 2, 1, "Plain 1-high")
ws2.cell(r + 2, 2, f"={N['Raise cost']}"); ws2.cell(r + 2, 3, f"={N['Sink cost']}")
ws2.cell(r + 3, 1, "Reinforced 1-high")
ws2.cell(r + 3, 2, f"={N['Raise cost']}+{N['Reinforce surcharge']}"); ws2.cell(r + 3, 3, f"={N['Sink cost']}*{N['Enemy-reinforced cost multiplier']}")
for rr in (r + 2, r + 3):
    ws2.cell(rr, 4, f"=ROUNDDOWN(100/B{rr},0)"); ws2.cell(rr, 5, f"=ROUNDDOWN(100/C{rr},0)")
    ws2.cell(rr, 6, f"=IF(D{rr}<E{rr},\"Turtle runs out first (good)\",\"Attacker runs out first (turtle can't be broken by bar)\")")
ws2.cell(r + 5, 1, "Regen during a fight: only after this many seconds without sculpting")
ws2.cell(r + 5, 2, f"={N['Regen delay']}")
ws2.cell(r + 6, 1, "Bar regained per second after that")
ws2.cell(r + 6, 2, f"=100/{N['Regen 0 to 100% time']}")
# ---------- Throw table
ws3 = wb.create_sheet("Throw by size")
ws3["A1"] = "Throw numbers keyed on s = piece sub-cells / 256 (a 2x2-tile, one-level piece is s = 1). Dodge check assumes the throw direction is locked at confirm."; ws3["A1"].font = B
hdr(ws3, 3, ["Piece", "s", "Width m", "Windup s", "Ticks", "Speed m/s", "Cost %", "Damage", "Range m", "Flight to 7 m s",
             "Disarm s", "Time to dodge at 7 m", "Needed: beginner", "Needed: mid", "Needed: pro"])
pieces = [("1 tile", 0.25), ("2 tiles", 0.5), ("2x2 (reference)", 1.0), ("2x3", 1.5), ("3x3 (max)", 2.25)]
for i, (nm, s) in enumerate(pieces):
    r = 4 + i
    ws3.cell(r, 1, nm); c = ws3.cell(r, 2, s); c.fill = INP
    ws3.cell(r, 3, f"=2.4*SQRT(B{r})")
    ws3.cell(r, 4, f"={N['Throw windup base']}+{N['Throw windup per s']}*B{r}")
    ws3.cell(r, 5, f"=ROUND(D{r}*60,0)")
    ws3.cell(r, 6, f"={N['Throw speed base m/s']}-{N['Throw speed per s']}*B{r}")
    ws3.cell(r, 7, f"={N['Throw cost base']}+{N['Throw cost per s']}*B{r}")
    ws3.cell(r, 8, f"=MIN({N['Throw dmg cap']},{N['Throw dmg base']}+{N['Throw dmg per s']}*B{r})")
    a = f"RADIANS({N['Throw launch angle deg']})"; g = N['Throw gravity m/s2']; h = N['Throw launch height m']
    ws3.cell(r, 9, f"=F{r}*COS({a})*(F{r}*SIN({a})+SQRT((F{r}*SIN({a}))^2+2*{g}*{h}))/{g}")
    ws3.cell(r, 10, f"=7/(F{r}*COS({a}))")
    ws3.cell(r, 11, f"=IF(H{r}>30,{N['Disarm min (at 30 dmg)']}+({N['Disarm max (at 100+ dmg)']}-{N['Disarm min (at 30 dmg)']})*MIN(1,(H{r}-30)/70),0)")
    ws3.cell(r, 12, f"=D{r}+J{r}")
    for j, t in enumerate(("Beginner", "Mid", "Pro")):
        # notice + time to move half width + 0.35 + splash at 6 m/s (0.05 s accel)
        ws3.cell(r, 13 + j, f"={T(t,'B')}+0.05+(C{r}/2+0.35+{N['Splash radius m']})/6")
for col, wdt in zip("ABCDEFGHIJKLMNO", (16, 6, 9, 10, 7, 10, 8, 9, 9, 13, 9, 18, 16, 13, 13)):
    ws3.column_dimensions[col].width = wdt
ws3.cell(10, 1, "Green 'needed' = that tier can dodge an alert sidestep in time. Sliding, airborne and mid-mantle players can't sidestep, so those are the punish cases.")
from openpyxl.formatting.rule import FormulaRule
ws3.conditional_formatting.add("M4:O8", FormulaRule(formula=["M4<$L4"], fill=PatternFill("solid", fgColor="C6EFCE")))
ws3.conditional_formatting.add("M4:O8", FormulaRule(formula=["M4>=$L4"], fill=PatternFill("solid", fgColor="FFC7CE")))
# ---------- Long range cover
ws4 = wb.create_sheet("Long-range cover")
ws4["A1"] = "Emergency cover vs a long-range shooter (simulated, 4000 runs each). Survive = alive after raising a 1-high piece and crouching."; ws4["A1"].font = B
hdr(ws4, 3, ["Weapon", "Distance m", "TTK on a target that does nothing", "Beginner survives", "Mid survives", "Pro survives", "Beginner dmg taken", "Pro dmg taken"])
LR = [("AR",30,1.81,1.00,1.00,1.00,82,55),("AR",45,2.38,1,1,1,73,51),("AR",60,3.19,1,1,1,62,46),
      ("Pistol",30,0.98,0.50,0.86,1.00,134,95),("Pistol",45,1.60,0.81,0.94,0.99,109,81),("Pistol",60,2.17,0.98,1.00,1.00,84,60)]
for i, row in enumerate(LR):
    for j, v in enumerate(row): ws4.cell(4 + i, 1 + j, v)
ws4.cell(11, 1, "Sculpt enter time of 0, 6 or 9 ticks made no difference here: aiming at the piece takes longer than the enter animation.")
for col in "ABCDEFGH": ws4.column_dimensions[col].width = 20
# ---------- Sim results
ws5 = wb.create_sheet("Sim results")
ws5["A1"] = "Win rate of the first player, 1000-1200 simulated 1v1s each, equal aim. 'Current' = today's spec. 'Recommended' = enter 6t, weapon exits, gun locked during attacks, enemy-reinforced windup 0.4 s."; ws5["A1"].font = B
hdr(ws5, 3, ["Matchup", "SMG 5 m current", "SMG 5 m recommended", "Pump 4.5 m current", "Pump 4.5 m recommended", "AR 7 m current", "AR 7 m recommended"])
res = [
 ("Pro vs beginner (full skill gap)", .74, .75, .69, .55, .73, .73),
 ("Pro speed vs beginner speed, same decisions", .60, .69, .61, .57, .60, .70),
 ("Pro speed vs mid speed, same decisions", .52, .54, .51, .56, .53, .57),
 ("Good decisions vs basic decisions, same speed", .63, .56, .65, .54, .61, .55),
 ("Mid vs beginner", .63, .67, .55, .56, .61, .62),
 ("Mid sculptor vs mid who never sculpts", .88, .77, .66, .53, .85, .71),
 ("Pro vs mid who never sculpts", .88, .89, None, .67, None, None),
 ("Pro with throw vs mid who never sculpts", None, .94, None, .63, None, None),
 ("Plain pro vs always-reinforcing pro", .08, .50, .20, .46, .09, .50),
 ("Pro with throw vs pro", .39, .51, .37, .42, .40, .52),
 ("Mid with throw vs beginner", .67, .73, .70, .66, .67, .71),
 ("Pro vs pure turtle (kill rate in 40 s)", 1.00, 1.00, .98, .99, .89, .91),
]
for i, row in enumerate(res):
    for j, v in enumerate(row):
        ws5.cell(4 + i, 1 + j, v if v is not None else "-")
ws5.column_dimensions["A"].width = 48
for col in "BCDEFG": ws5.column_dimensions[col].width = 20
r = 18
ws5.cell(r, 1, "Other results").font = B
other = [
 ("Sculpt actions per minute in close fights: pro ~35-40, beginner ~15-20", ""),
 ("Bar used in one close fight: 15-25% now, 20-40% with recommended timings", ""),
 ("Starting a close fight on low bar vs a full-bar pro: 0% wins 14%, 10% wins 28%, 20% wins 43%, 35%+ about even", ""),
 ("Building cover after the shooting starts, open ground: SMG 9 m 2-3% win; AR 20 m pro 51%, beginner 33%", ""),
 ("Changing sink/raise/move/reinforce cost or regen speed did not change any single-fight win rate", ""),
]
for i, (a, b) in enumerate(other): ws5.cell(r + 1 + i, 1, a)
for wsx in wb.worksheets:
    for row in wsx.iter_rows():
        for c in row:
            if isinstance(c.value, str) and c.value.startswith("=") and "IF(" not in c.value[:4]:
                c.number_format = "0.00"
import sys
wb.save(sys.argv[1] if len(sys.argv) > 1 else "sculpt-1v1-balance.xlsx")
print("saved")
