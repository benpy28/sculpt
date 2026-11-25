import json
from multiprocessing import Pool
from variant import job, strats
BASE = {"Pump": {"interval": 0.75}, "SMG": {"interval": 1/11}, "Pistol": {"bloom": 0.8, "bmax": 2.5, "head": 37}}
V = {
 "approved (pattern 2.6, run +0.3, slide +0.6, air +0.8)": {},
 "Ben's idea: tight still (1.8), wide moving (run +1.5, slide/air +2.0)": {"pattern": 1.8, "move": 1.5, "slide": 2.0, "air": 2.0},
 "mild: tighter still (2.0), run +0.6, slide/air +1.2": {"pattern": 2.0, "move": 0.6, "slide": 1.2, "air": 1.2},
 "slide/air only: 2.2, run +0.3, slide/air +1.5": {"pattern": 2.2, "move": 0.3, "slide": 1.5, "air": 1.5},
}
if __name__ == "__main__":
    out = {}
    with Pool(14) as p:
        for name, pv in V.items():
            ov = json.loads(json.dumps(BASE)); ov["Pump"].update(pv)
            for aim in ("mid", "pro"):
                jobs = [(ov, sa, sb, D, aim, 250) for b in ("SMG", "AR", "Pistol") for D in (3, 6, 10) for sa in strats("Pump") for sb in strats(b)]
                res = p.map(job, jobs, chunksize=4)
                row = {}
                for b in ("SMG", "AR", "Pistol"):
                    for D in (3, 6, 10):
                        sub = [(sa, v) for sa, sb, d, v in res if d == D and sb[0] == b]
                        by = {}
                        for sa, v in sub: by.setdefault(sa, []).append(v)
                        best = max(by, key=lambda k: min(by[k]))
                        row[f"v {b} @{D}"] = (round(min(by[best]), 2), best[1])
                out[f"{name} | {aim}"] = row
                print(name, aim, {k: v for k, v in row.items()})
    json.dump(out, open("results/pumpvar.json", "w"))
