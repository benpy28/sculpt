"""Balance pass 2 (2026-11-26): before/after workbook sculpt-1v1-balance-2026-11-26-pass2.xlsx.
'before' = live numbers after pass 1 with the corrected aim model (results/matrix_fix_*.json, ladder_live.json),
'after' = pass 2 numbers (matrix_final_*.json, ladder_final.json)."""
import json
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.formatting.rule import ColorScaleRule
B = Font(bold=True); HDR = PatternFill("solid", fgColor="D9E1F2"); WRAP = Alignment(wrap_text=True, vertical="top")
W = ["Pump", "SMG", "AR", "Pistol"]
def hdr(ws, r, vals, c0=1):
    for i, v in enumerate(vals): c = ws.cell(r, c0 + i, v); c.font = B; c.fill = HDR
def scale(ws, rng):
    ws.conditional_formatting.add(rng, ColorScaleRule(start_type="num", start_value=0, start_color="F8696B", mid_type="num", mid_value=0.5, mid_color="FFFFFF", end_type="num", end_value=1, end_color="63BE7B"))
def pairs(path):
    d = json.load(open(path)); S = [tuple(s) for s in d["strats"]]; out = {}
    for D in d["dists"]:
        M = d["M"][str(D)]
        for a in W:
            ia = [i for i, s in enumerate(S) if s[0] == a]
            for b in W:
                ib = [j for j, s in enumerate(S) if s[0] == b]
                out[(D, a, b)] = round(max(min(M[i][j] for j in ib) for i in ia), 2)
        eq = d["eq"][str(D)]
        top = sorted(range(len(S)), key=lambda k: -eq[k])[0]
        out[(D, "best")] = f"{S[top][0]}, {S[top][1]}"
    return d["dists"], out
wb = Workbook(); ws = wb.active; ws.title = "Read me"
txt = [("SCULPT 1v1 balance pass 2 (2026-11-26): SMG at pro aim and crouch camping. Same sim as the 2026-11-25 sheets (Tools/Balance/v2), now on the live numbers incl. the first-shot kick.", True),
 ("Sim fix: pros aimed at a crouched player's head as a share of the box height, which put their aim near the top edge of the head and made crouching look much stronger than it is. The aim point now sits a fixed distance below the top (crouching folds the legs, the head and chest keep their place). With that fix alone, crouching stops being a winning tactic at mid aim; at pro aim crouching still was only as good as standing still, plus its 0.75-0.8x accuracy bonus.", False),
 ("Changes: SMG head 1.5x = 19.1 (was 1.25x = 15.9; 8 heads to kill, was 10; body unchanged), SMG falloff from 7 m with 0.6 at 18 m (was 10 / 20 m), no crouch accuracy bonus on any gun (SMG/pistol were 0.8x, AR 0.75x). Pump unchanged (option 1).", False),
 ("Result, pro aim: pump 3 m, SMG 6 m (was no range), AR 10 m and out. Mid aim: pump 3 m, SMG 6 m, SMG/pistol/AR close at 10-15 m, AR from 25 m (same as before). Skill ladder about the same.", False)]
for i, (t, b) in enumerate(txt, 1):
    c = ws.cell(i, 1, t); c.alignment = WRAP
    if b: c.font = B
ws.column_dimensions["A"].width = 150
for aim in ("mid", "pro"):
    ws = wb.create_sheet(f"Guns {aim} aim before-after")
    ws["A1"] = f"Row gun's win rate vs column gun ({aim} aim, both pick their best tactic). Left = before pass 2, right = after."; ws["A1"].font = B
    dists, b = pairs(f"results/matrix_fix_{aim}.json"); _, a = pairs(f"results/matrix_final_{aim}.json"); r = 3
    for D in dists:
        ws.cell(r, 1, f"{D} m").font = B
        hdr(ws, r, ["before"] + W, 1); hdr(ws, r, ["after"] + W, 7)
        for i, x in enumerate(W):
            ws.cell(r + 1 + i, 1, x).font = B; ws.cell(r + 1 + i, 7, x).font = B
            for j, y in enumerate(W):
                ws.cell(r + 1 + i, 2 + j, b[(D, x, y)]); ws.cell(r + 1 + i, 8 + j, a[(D, x, y)])
        ws.cell(r + 5, 1, "best play: " + b[(D, "best")]); ws.cell(r + 5, 7, "best play: " + a[(D, "best")])
        scale(ws, f"B{r+1}:E{r+4}"); scale(ws, f"H{r+1}:K{r+4}")
        r += 7
ws = wb.create_sheet("Crouch vs moving")
ws["A1"] = "Each tactic's best score against the equilibrium mix (all guns), before and after. 'crouch' = crouched and still while shooting."; ws["A1"].font = B
hdr(ws, 2, ["aim", "dist", "when", "still", "crouch", "cstrafe", "strafe", "slide"]); r = 3
for aim in ("mid", "pro"):
    for when, path in (("old sim", f"results/matrix_base_{aim}.json"), ("before", f"results/matrix_fix_{aim}.json"), ("after", f"results/matrix_final_{aim}.json")):
        d = json.load(open(path)); S = [tuple(s) for s in d["strats"]]
        for D in d["dists"]:
            M = d["M"][str(D)]; eq = d["eq"][str(D)]
            vs = [sum(M[i][j] * eq[j] for j in range(len(S))) for i in range(len(S))]
            row = [aim, f"{D} m", when] + [round(max(vs[i] for i, s in enumerate(S) if s[1] == m), 2) for m in ("still", "crouch", "cstrafe", "strafe", "slide")]
            for j, x in enumerate(row): ws.cell(r, 1 + j, x)
            r += 1
scale(ws, f"D3:H{r-1}")
ws = wb.create_sheet("Skill ladder before-after")
L0 = json.load(open("results/ladder_live.json")); L1 = json.load(open("results/ladder_final.json"))
hdr(ws, 1, ["Matchup", "when"] + [f"{D} m" for D in L0["dists"]]); r = 2
for k in L0["res"]:
    for when, L in (("before", L0), ("after", L1)):
        ws.cell(r, 1, k); ws.cell(r, 2, when)
        for j, x in enumerate(L["res"][k]): ws.cell(r, 3 + j, x)
        r += 1
scale(ws, f"C2:H{r-1}"); ws.column_dimensions["A"].width = 58
ws = wb.create_sheet("Numbers")
rows = [("SMG head damage", "15.94 (1.25x, 10 heads)", "19.125 (1.5x, 8 heads)"), ("SMG falloff", "1.0 to 10 m, 0.6 at 20 m, 0.4 at 35 m", "1.0 to 7 m, 0.6 at 18 m, 0.4 at 35 m"),
        ("Crouch accuracy factor", "SMG 0.8, AR 0.75, pistol 0.8, pump 1", "1 on every gun"), ("Duel bot: SMG to AR switch", "26 m (back under 18 m)", "16 m (back under 12 m)"), ("Protocol", "30", "31")]
hdr(ws, 1, ["What", "Before", "After"])
for i, rw in enumerate(rows):
    for j, x in enumerate(rw): ws.cell(2 + i, 1 + j, x).alignment = WRAP
for c, w in zip("ABC", (34, 44, 44)): ws.column_dimensions[c].width = w
wb.save("../sculpt-1v1-balance-2026-11-26-pass2.xlsx"); print("saved")
