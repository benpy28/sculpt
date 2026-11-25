"""Grenade, launcher, C4 and punch numbers vs a target, from the live config (Grenades.cs, Punch.cs, Hand.cs).
Projectiles burst on the floor where they land; the thrower aims at where the target will be (its velocity when the
thrower decided, times flight time, times `lead`), plus a range-judging error. Blast: full inside FullRadius of the chest
(1.2 m up), linear to 0 at Radius."""
import math, random
from duel2 import move_profile, TICK

CHEST = 1.2
KINDS = {
    # speed, up deg, gravity, full radius, radius, max dmg, recovery s, draw s, arm m
    "Grenade":  dict(v=20, up=6, g=15, full=1.0, rad=4.0, dmg=70, rec=0.4, draw=0.2, arm=0, carry=2),
    "Launcher": dict(v=32, up=2, g=9, full=0.75, rad=3.5, dmg=65, rec=1.1, draw=0.6, arm=8, carry=4),
    "C4":       dict(v=12, up=8, g=15, full=0.6, rad=2.5, dmg=45, rec=0.5, draw=0.2, arm=0, carry=2, box=True),
}

def blast(k, r_h, box=False):
    d = math.hypot(r_h, 0.0 if box else CHEST)
    if box: d = max(0.0, r_h - 0.35)
    if d <= k["full"]: f = 1.0
    elif d >= k["rad"]: f = 0.0
    else: f = (k["rad"] - d) / (k["rad"] - k["full"])
    return round(k["dmg"] * f)

def flight_time(k, D, h0=1.5):
    """Time to reach horizontal distance D, launched to land there (aim pitch solved by bisection)."""
    lo, hi = -40.0, 60.0
    for _ in range(60):
        p = (lo + hi) / 2
        a = math.radians(p + k["up"])
        vx, vy = k["v"] * math.cos(a), k["v"] * math.sin(a)
        # landing distance from h0
        t = (vy + math.sqrt(vy * vy + 2 * k["g"] * h0)) / k["g"]
        if vx * t < D: lo = p
        else: hi = p
    a = math.radians((lo + hi) / 2 + k["up"])
    vx, vy = k["v"] * math.cos(a), k["v"] * math.sin(a)
    return (vy + math.sqrt(vy * vy + 2 * k["g"] * h0)) / k["g"], (lo + hi) / 2

def expected(kind, D, move, n=3000, lead=0.8, decide_lag=0.15, seed=5):
    k = KINDS[kind]
    if k["arm"] and D < k["arm"]: return 0.0, 0.0
    T, pitch = flight_time(k, D)
    rng = random.Random(seed)
    tot = 0; kills70 = 0
    for _ in range(n):
        xs = move_profile(move, rng)[0]
        i0 = int(0.5 / TICK)                    # decide at 0.5 s into the target's pattern
        j = i0 - int(decide_lag / TICK)
        v = (xs[j] - xs[j - 1]) / TICK
        aim = xs[j] + v * (decide_lag + T) * lead
        land = int((0.5 + T) / TICK)
        tx = xs[min(land, len(xs) - 1)]
        err_range = rng.gauss(0, 0.4 + 0.04 * D)
        err_lat = rng.gauss(0, 0.2 + 0.015 * D)
        r = math.hypot(aim + err_lat - tx, err_range)
        d = blast(k, r, k.get("box", False))
        tot += d
    return tot / n, T

if __name__ == "__main__":
    for kind in KINDS:
        print(kind)
        for D in (5, 10, 15, 20, 30):
            row = []
            for m in ("still", "strafe", "cstrafe", "slide"):
                e, T = expected(kind, D, m, n=1500)
                row.append(f"{m} {e:4.0f}")
            print(f"  {D:3d} m (flight {T:.2f} s): " + " | ".join(row))
    print("blast by distance from feet (standing):", {k: [blast(KINDS[k], r, KINDS[k].get('box', False)) for r in (0, 0.5, 1, 1.5, 2, 3)] for k in KINDS})
