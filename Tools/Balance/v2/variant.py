"""Pairwise weapon maximin under config overrides. python variant.py '<json overrides>' aim n"""
import json, sys, random
from multiprocessing import Pool
import duel2
from duel2 import Shooter, fight
MOVES = ["still", "crouch", "cstrafe", "strafe", "slide"]
def strats(w):
    taps = [None, 0.3] if w in ("AR", "Pistol") else [None]
    return [(w, m, t) for m in MOVES for t in taps]
def apply(ov):
    for w, kv in ov.items():
        duel2.WEAPONS[w].update(kv)
def job(a):
    ov, sa, sb, D, aim, n = a
    apply(ov)
    rng = random.Random(hash((sa, sb, D)) & 0xffffffff)
    tot = 0
    for _ in range(n):
        A = Shooter(sa[0], aim, sa[1], tap=sa[2], fire_stopped=sa[1] == "cstrafe")
        B = Shooter(sb[0], aim, sb[1], tap=sb[2], fire_stopped=sb[1] == "cstrafe")
        r, _ = fight(A, B, D, rng)
        tot += (1 - r) if r != 0.5 else 0.5
    return sa, sb, D, tot / n
PAIRS = [("Pump", "SMG", [3, 6]), ("Pump", "AR", [3, 6]), ("SMG", "AR", [6, 10, 15]), ("Pistol", "AR", [10, 15, 25, 40]), ("SMG", "Pistol", [6, 10])]
def run(ov, aim="mid", n=200, pairs=PAIRS):
    jobs = []
    for a, b, Ds in pairs:
        for D in Ds:
            for sa in strats(a):
                for sb in strats(b):
                    jobs.append((ov, sa, sb, D, aim, n))
    with Pool() as p:
        res = p.map(job, jobs, chunksize=4)
    out = {}
    for a, b, Ds in pairs:
        for D in Ds:
            sub = [(sa, sb, v) for sa, sb, d, v in res if d == D and sa[0] == a and sb[0] == b]
            rows = {}
            for sa, sb, v in sub: rows.setdefault(sa, []).append(v)
            mA = max(min(v) for v in rows.values())
            cols = {}
            for sa, sb, v in sub: cols.setdefault(sb, []).append(1 - v)
            mB = max(min(v) for v in cols.values())
            out[f"{a} v {b} @{D}"] = (round(mA, 2), round(mB, 2))
    return out
if __name__ == "__main__":
    ov = json.loads(sys.argv[1]) if len(sys.argv) > 1 else {}
    aim = sys.argv[2] if len(sys.argv) > 2 else "mid"
    n = int(sys.argv[3]) if len(sys.argv) > 3 else 200
    r = run(ov, aim, n)
    print(json.dumps(ov), aim)
    for k, v in r.items(): print(f"  {k:22s} {v[0]:.2f} (other side's best {v[1]:.2f})")
