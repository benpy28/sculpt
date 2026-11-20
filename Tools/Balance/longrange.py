"""Emergency cover vs long-range fire. Victim is standing in the open, gets hit, then raises a 1-high piece and crouches.
Shooter uses the better of spraying and tapping. Equal aim model from duel.py."""
import random, math
from duel import SKILL, WEAPONS
from geom import HEAD, HALF_W
PISTOL = dict(rof=5.0, body=30, head=50, sig=0.005, bloom=0.0012, cap=0.006)
def shot(rng, D, sig, crouch):
    top = 1.1 if crouch else 1.8
    aim = top - 0.35 if not crouch else 0.65
    ex = rng.gauss(0, sig * D); ey = aim + rng.gauss(0, sig * D)
    if abs(ex) < HALF_W and 0 <= ey <= top:
        return "head" if ey >= top - HEAD else "body"
    return None
def run(weapon, D, tier, enter, windup=0.2, n=4000, tap=None, seed=7, react_hit=True):
    W = PISTOL if weapon == "PISTOL" else WEAPONS[weapon]
    rng = random.Random(seed)
    sk = SKILL[tier] if tier else None
    surv = 0; dmg_tot = 0; ttk = []
    for _ in range(n):
        interval = 1 / W["rof"] if not tap else max(1 / W["rof"], tap)
        t = 0.0; hp = 150.0; consec = 0; last = -9; cover_t = None; crouch_t = None
        while t < 6.0:
            if t - last > 0.35: consec = 0
            sig = W["sig"] + min(W["cap"], W["bloom"] * consec)
            crouch = crouch_t is not None and t >= crouch_t
            covered = cover_t is not None and t >= cover_t
            if covered: break
            r = shot(rng, D, sig, crouch)
            consec += 1; last = t
            if r:
                hp -= W["head"] if r == "head" else W["body"]
                if sk and cover_t is None:
                    notice = max(0.1, rng.gauss(sk["notice"], sk["notice"] * 0.2))
                    acq = max(0.03, rng.gauss(sk["acq"] * 0.7, sk["acq_sd"]))   # feet magnetism: tile in front
                    cover_t = t + notice + max(enter, acq) + windup
                    crouch_t = t + notice  # crouch as soon as you react
            if hp <= 0:
                ttk.append(t); break
            t += interval
        if hp > 0:
            surv += 1
        dmg_tot += 150 - max(hp, 0)
    return surv / n, dmg_tot / n, (sum(ttk) / len(ttk) if ttk else float("nan"))
if __name__ == "__main__":
    for weapon in ("AR", "PISTOL"):
        for D in (30, 45, 60):
            best = None
            for tap in (None, 0.25, 0.36, 0.5):
                s, d, k = run(weapon, D, None, 0, tap=tap)
                if best is None or k < best[1]: best = (tap, k, d)
            tap = best[0]
            print(f"\n{weapon} {D} m: shooter's best fire mode tap={tap}, time-to-kill on a target that does nothing {best[1]:.2f} s")
            for enter in (0.0, 0.1, 0.15):
                row = []
                for tier in ("beginner", "mid", "pro"):
                    s, d, _ = run(weapon, D, tier, enter, tap=tap)
                    row.append(f"{tier} survive {s:.2f} (dmg {d:.0f})")
                print(f"  enter {int(enter*60):2d} ticks: " + " | ".join(row))
