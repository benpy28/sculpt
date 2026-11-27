"""Weapon x tactic payoff matrix per distance, equal aim (both 'mid' unless told otherwise), plus a mixed equilibrium.
Writes results/matrix_<aim>.json."""
import json, sys, os, itertools, random
import duel2
from multiprocessing import Pool
from duel2 import Shooter, fight

MOVES = ["still", "crouch", "cstrafe", "strafe", "slide"]
def strategies():
    out = []
    for w in ("Pump", "SMG", "AR", "Pistol"):
        taps = [None, 0.3] if w in ("AR", "Pistol") else [None]
        for m in MOVES:
            for tap in taps:
                out.append((w, m, tap))
    return out

STRATS = strategies()
DISTS = [3, 6, 10, 15, 25, 40]

def mk(s, aim):
    w, m, tap = s
    return Shooter(w, aim, m, tap=tap, fire_stopped=(m == "cstrafe"))

def job(args):
    i, j, D, aimA, aimB, n, ov = args
    for w, kv in ov.items(): duel2.WEAPONS[w].update(kv)
    rng = random.Random(hash((i, j, D)) & 0xffffffff)
    tot = 0.0
    for _ in range(n):
        r, _t = fight(mk(STRATS[i], aimA), mk(STRATS[j], aimB), D, rng)
        tot += (1 - r) if r != 0.5 else 0.5
    return i, j, D, tot / n

def equilibrium(M, iters=20000):
    """Fictitious play on a symmetric zero-sum game (row's win rate). Returns the row mix."""
    n = len(M)
    cnt = [0] * n; cnt[0] = 1
    for _ in range(iters):
        tot = sum(cnt)
        # best response of the row player to the column mix (= same mix, symmetric game)
        best = max(range(n), key=lambda r: sum(M[r][c] * cnt[c] for c in range(n) if cnt[c]))
        cnt[best] += 1
    tot = sum(cnt)
    return [c / tot for c in cnt]

if __name__ == "__main__":
    aim = sys.argv[1] if len(sys.argv) > 1 else "mid"
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 200
    ov = json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}
    tag = sys.argv[4] if len(sys.argv) > 4 else aim
    jobs = [(i, j, D, aim, aim, n, ov) for D in DISTS for i in range(len(STRATS)) for j in range(i, len(STRATS))]
    with Pool() as p:
        res = p.map(job, jobs, chunksize=8)
    M = {D: [[0.5] * len(STRATS) for _ in STRATS] for D in DISTS}
    for i, j, D, v in res:
        M[D][i][j] = v; M[D][j][i] = 1 - v
    os.makedirs("results", exist_ok=True)
    out = {"strats": STRATS, "dists": DISTS, "M": {str(D): M[D] for D in DISTS}}
    out["eq"] = {str(D): equilibrium(M[D], 4000) for D in DISTS}
    json.dump(out, open(f"results/matrix_{tag}.json", "w"))
    for D in DISTS:
        eq = out["eq"][str(D)]
        top = sorted(range(len(STRATS)), key=lambda k: -eq[k])[:5]
        print(D, [(STRATS[k], round(eq[k], 2)) for k in top if eq[k] > 0.01])
