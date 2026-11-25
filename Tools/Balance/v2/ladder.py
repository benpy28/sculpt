"""Skill ladder: whole players (aim + tactics) and split tests (only aim differs / only tactics differ)."""
import json, sys, random
from multiprocessing import Pool
import duel2
from duel2 import Shooter, fight
DISTS = [3, 6, 10, 15, 25, 40]
def best_weapon(D, knows_pistol):
    if D < 9: return "SMG"
    if D < 30 and knows_pistol: return "Pistol"
    return "AR" if D >= 9 else "SMG"
def tactics(kind, D, rng):
    if kind == "beginner":     # sensible weapon (never the pistol), sprays, half the time stands still
        w = "SMG" if D < 12 else "AR"
        return w, rng.choice(("still", "strafe")), None
    w = best_weapon(D, True)
    tap = 0.3 if (D >= 15 and w in ("AR", "Pistol")) else None
    if kind == "mid": return w, rng.choice(("strafe", "strafe", "crouch")), tap
    return w, "strafe", tap
def job(a):
    ov, A, B, D, n = a
    for w, kv in ov.items(): duel2.WEAPONS[w].update(kv)
    rng = random.Random(hash((str(A), str(B), D)) & 0xffffffff)
    tot = 0
    for _ in range(n):
        wa, ma, ta = tactics(A[1], D, rng); wb, mb, tb = tactics(B[1], D, rng)
        r, _ = fight(Shooter(wa, A[0], ma, tap=ta), Shooter(wb, B[0], mb, tap=tb), D, rng)
        tot += (1 - r) if r != 0.5 else 0.5
    return str(A), str(B), D, tot / n
MATCHES = [
    (("pro", "pro"), ("beginner", "beginner"), "pro vs beginner"),
    (("pro", "pro"), ("mid", "mid"), "pro vs mid"),
    (("mid", "mid"), ("beginner", "beginner"), "mid vs beginner"),
    (("mid", "pro"), ("mid", "beginner"), "same aim, pro vs beginner tactics"),
    (("pro", "beginner"), ("mid", "mid"), "pro aim + beginner tactics vs mid player"),
    (("pro", "beginner"), ("beginner", "pro"), "pro aim/beginner tactics vs beginner aim/pro tactics"),
]
if __name__ == "__main__":
    ov = json.loads(sys.argv[1]) if len(sys.argv) > 1 else {}
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 800
    jobs = [(ov, A, B, D, n) for A, B, _ in MATCHES for D in DISTS]
    with Pool(14) as p: res = p.map(job, jobs)
    out = {}
    for (A, B, name) in MATCHES:
        out[name] = [round(v, 3) for a, b, D, v in res if a == str(A) and b == str(B)]
        print(f"{name:55s}", out[name])
    json.dump({"dists": DISTS, "res": out, "ov": ov}, open(f"results/ladder_{'base' if not ov else 'var'}.json", "w"))
