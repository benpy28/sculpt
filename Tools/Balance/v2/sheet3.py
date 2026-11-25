"""After the balance pass: before/after workbook (sculpt-1v1-balance-2026-11-25-after.xlsx)."""
import json
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill, Alignment
from openpyxl.formatting.rule import ColorScaleRule
import breach
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
txt = [("SCULPT 1v1 balance after the 2026-11-25 balance pass (Ben approved). Same sim as sculpt-1v1-balance-2026-11-25.xlsx (the 'before').", True),
 ("Applied: pump 0.75 s cycle; pump slide/air spread 1.5/1.5 (were 0.6/0.8; pattern and running accuracy unchanged); SMG 11/s; pistol bloom 0.8 cap 2.5, head 37; a fresh heal stops a sprint until hit again; grenades/C4/launcher share slot 5; launcher 6 rounds; punch gun lock 0.35 s.", False),
 ("Result: every gun now has a range it wins at mid aim (pump 3 m, SMG 6 m, pistol 10-15 m, AR 25 m+). At pro aim the pump owns 3-6 m and the AR wins from 10 m; the pistol is no longer best everywhere. Skill ladder barely moved.", False),
 ("Watch: at pro aim the SMG has no range of its own (AR beats it from 6 m), and crouching still is a bit too strong.", False)]
for i, (t, b) in enumerate(txt, 1):
    c = ws.cell(i, 1, t); c.alignment = WRAP
    if b: c.font = B
ws.column_dimensions["A"].width = 150
for aim, bp, ap in (("mid", "results/matrix_mid.json", "results/matrix_final_mid.json"), ("pro", "results/matrix_pro.json", "results/matrix_final_pro.json")):
    ws = wb.create_sheet(f"Guns {aim} aim before-after")
    ws["A1"] = f"Row gun's win rate vs column gun ({aim} aim, both pick their best tactic). Left = before, right = after."; ws["A1"].font = B
    dists, b = pairs(bp); _, a = pairs(ap); r = 3
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
ws = wb.create_sheet("Skill ladder before-after")
L0 = json.load(open("results/ladder_base.json")); L1 = json.load(open("results/ladder_final.json"))
hdr(ws, 1, ["Matchup", "when"] + [f"{D} m" for D in L0["dists"]]); r = 2
for k in L0["res"]:
    for when, L in (("before", L0), ("after", L1)):
        ws.cell(r, 1, k); ws.cell(r, 2, when)
        for j, x in enumerate(L["res"][k]): ws.cell(r, 3 + j, x)
        r += 1
scale(ws, f"C2:H{r-1}"); ws.column_dimensions["A"].width = 58
ws = wb.create_sheet("Heal and breach before-after")
hdr(ws, 1, ["Attacker shoots the wall from", "Gets through a heal, before (patched counted as hit)", "After (a fresh heal stops a sprint)"])
for i, d in enumerate((1.5, 3, 5, 8)):
    ws.cell(2 + i, 1, f"{d} m"); ws.cell(2 + i, 2, breach.heal_war(patched_counts=True, dist=d)); ws.cell(2 + i, 3, breach.heal_war(patched_counts=False, dist=d))
ws.cell(7, 1, "Defender notices in ~0.4 s and heals in 0.2 s. After the pass, shooting and sprinting in from 3 m or closer still beats the heal (timing skill); a lazy shot from 5-8 m then a sprint gets stopped. Breaching plain walls is still always worth it (saves 2-4.5 s over walking round).").alignment = WRAP
ws.column_dimensions["A"].width = 60; ws.column_dimensions["B"].width = 44; ws.column_dimensions["C"].width = 36
ws = wb.create_sheet("Items before-after")
rows = [("Grenades / C4 / launcher", "C4 and launcher replace a gun", "all share slot 5; picking one swaps out the other kind, never a gun"),
        ("Launcher rounds", "4", "6"), ("Punch gun lock", "0.8 s", "0.35 s (one punch every 0.8 s)"),
        ("Pump at room range (~8 m), crosshair on the head", "about 65 (5 of 9 pellets fit the head)", "same (pattern unchanged, Ben's pick)"),
        ("Pump sliding / in the air", "+0.6 / +0.8 deg", "+1.5 / +1.5 deg")]
hdr(ws, 1, ["What", "Before", "After"])
for i, rw in enumerate(rows):
    for j, x in enumerate(rw): ws.cell(2 + i, 1 + j, x).alignment = WRAP
for c, w in zip("ABC", (44, 40, 60)): ws.column_dimensions[c].width = w
wb.save("sculpt-1v1-balance-2026-11-25-after.xlsx"); print("saved")
