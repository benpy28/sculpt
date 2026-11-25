"""Perfect-accuracy shots and time to kill (150 health) by distance, live numbers."""
import math
from duel2 import WEAPONS, falloff, rnd
DISTS = [2, 5, 7, 10, 15, 20, 25, 35, 50]
def per_shot(w, D, head):
    W = WEAPONS[w]; dmg = (W["head"] if head else W["body"]) * W["pellets"]
    return rnd(dmg * falloff(W, D))
def table():
    rows = []
    for w in WEAPONS:
        for head in (False, True):
            r = [w, "all head" if head else "all body"]
            for D in DISTS:
                d = per_shot(w, D, head); n = math.ceil(150 / d) if d > 0 else 99
                r.append((d, n, round((n - 1) * WEAPONS[w]["interval"], 2)))
            rows.append(r)
    return rows
if __name__ == "__main__":
    for r in table():
        print(f"{r[0]:7s} {r[1]:9s}", "  ".join(f"{D}m:{d}x{n}={t}s" for D, (d, n, t) in zip(DISTS, r[2:])))
