"""Throw dodge / punish model. A piece flies along the look direction; the target dodges sideways after noticing the
launch tell. Lateral clearance = half piece width + player half width + splash."""
import math, random
from duel import SKILL
G = 15.0
def lateral_time(d, vmax=6.0, acc=60.0):
    t_acc = vmax / acc; d_acc = 0.5 * acc * t_acc ** 2
    return math.sqrt(2 * d / acc) if d <= d_acc else t_acc + (d - d_acc) / vmax
# target states: extra delay before a sideways move can start, and lateral speed available
STATES = {
    "alert, on foot":            (0.00, 6.0),
    "crouched behind cover":     (0.00, 3.0),
    "in sculpt mode, aiming away": (0.12, 6.0),
    "sprinting straight":        (0.05, 6.0),
    "sliding":                   (0.00, 1.6),   # steering only
    "mantling (0.4 s lock)":     (0.40, 6.0),
    "airborne (15% air control)": (0.00, 1.2),
}
def piece(s):
    """s = size relative to the 2x2-tile reference piece. Width across ~ 2.4 m * sqrt(s)."""
    return 2.4 * math.sqrt(s)
def p_hit(skill, state, windup, flight, width, splash=0.3, n=4000, seed=3):
    rng = random.Random(seed)
    sk = SKILL[skill]
    delay, v = STATES[state]
    need = width / 2 + 0.35 + splash
    hits = 0
    for _ in range(n):
        notice = max(0.05, rng.gauss(sk["notice"], sk["notice"] * 0.25))
        move = lateral_time(need, vmax=v, acc=60.0 if v >= 3 else 10.0)
        if notice + delay + move > windup + flight:
            hits += 1
    return hits / n
def proposal(s, **k):
    """Suggested throw numbers keyed on s = N / N_ref (2x2 tiles, one level). Returns windup s, speed, cost %, dmg."""
    w = k.get("w0", 0.40) + k.get("w1", 0.20) * s
    v = k.get("v0", 20.0) - k.get("v1", 3.0) * s
    cost = k.get("c0", 12.0) + k.get("c1", 13.0) * s
    dmg = min(k.get("cap", 120.0), k.get("d0", 30.0) + k.get("d1", 40.0) * s)
    return dict(windup=w, speed=v, cost=cost, dmg=dmg)
def raw(N):
    return dict(windup=(15 + N / 4) / 60, speed=22 - N / 16, cost=10 + 0.25 * N, dmg=min(120, 20 + 0.6 * N))
if __name__ == "__main__":
    print("Raw formulas at real piece sizes (N sub-cells; 2x2 ref = 256):")
    for N in (64, 128, 256, 576):
        print(N, {k: round(v, 2) for k, v in raw(N).items()})
    print("\nProposal:")
    for s in (0.25, 0.5, 1.0, 2.25):
        print(s, {k: round(v, 2) for k, v in proposal(s).items()})
    print("\nP(hit) by tier and state, proposal numbers, target at 7 m")
    for s in (0.25, 1.0, 2.25):
        pr = proposal(s); fl = 7.0 / pr["speed"]
        print(f"\n size {s}  windup {pr['windup']:.2f} flight {fl:.2f} width {piece(s):.1f} m dmg {pr['dmg']:.0f} cost {pr['cost']:.0f}%")
        for st in STATES:
            print(f"  {st:30s}", " ".join(f"{t}={p_hit(t, st, pr['windup'], fl, piece(s)):.2f}" for t in ("beginner", "mid", "pro")))
    print("\nRaw 1-tile formula (N=64) at 7 m, width 1.2 m:")
    r = raw(64); fl = 7 / r["speed"]
    for st in STATES:
        print(f"  {st:30s}", " ".join(f"{t}={p_hit(t, st, r['windup'], fl, 1.2):.2f}" for t in ("beginner", "mid", "pro")))
