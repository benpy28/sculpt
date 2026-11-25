import random, math, duel2
from duel2 import WEAPONS, falloff, PAT_X, PAT_Y, rnd, BODY_HALF, HEAD_HALF, HEAD_H
def head_shot(D, spread_extra=0.0, pattern=2.6, hand=0.0, n=4000, seed=1):
    """Crosshair dead on the centre of the head of a standing target: average damage of one pump shot."""
    rng = random.Random(seed); W = WEAPONS["Pump"]; tot = 0; heads = 0
    k = math.tan(math.radians(1)) * D
    for _ in range(n):
        hx, hy = rng.gauss(0, hand), rng.gauss(0, hand); t = 0
        for p in range(9):
            ox = hx + PAT_X[p] * pattern; oy = hy + PAT_Y[p] * pattern
            if spread_extra > 0:
                r = math.sqrt(rng.random()) * spread_extra; a = rng.random() * 6.283
                ox += r * math.cos(a); oy += r * math.sin(a)
            x, y = ox * k, 1.8 - HEAD_H / 2 + oy * k
            if y > 1.8 or y < 0: continue
            if y >= 1.8 - HEAD_H:
                if abs(x) <= HEAD_HALF: t += W["head"]; heads += 1
            elif abs(x) <= BODY_HALF: t += W["body"]
        tot += rnd(t * falloff(W, D))
    return tot / n, heads / n
print("Pump, crosshair on the head centre of a still standing target (live pattern 2.6 deg):")
for D in (3, 5, 6, 8, 10):
    print(f"  {D} m: perfect aim {head_shot(D)[0]:.0f} dmg ({head_shot(D)[1]:.1f} head pellets) | mid hand error {head_shot(D, hand=0.5)[0]:.0f} | running shooter (+0.3) {head_shot(D, 0.3, hand=0.5)[0]:.0f} | sliding (+0.9) {head_shot(D, 0.9, hand=0.5)[0]:.0f}")
