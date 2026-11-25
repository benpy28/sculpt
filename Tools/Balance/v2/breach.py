"""Breaching vs walking round, and the heal war, from the live numbers (TileWalls.cs BreachConfig, Charge.cs, Hand.cs).
All times in seconds, from a sprint at 10 m/s with the gun already in hand."""
import math, random

SPRINT, RUN = 10.0, 7.0
SPRINT_TO_AIM = 0.15
CHARGE_MIN = 8.0
CELL = 0.1875
PATH_STAND = (0.7 + CELL) * (1.8 + CELL)     # cells a standing charge's box touches, m^2 (partial cells count whole)
PATH_SLIDE = (0.7 + CELL) * (1.1 + CELL)

# one hit's damaged disc (radius) and damage per cell
HITS = {"AR": (0.45, 50), "SMG": (0.35, 25), "Pistol": (0.40, 34), "Pump (point blank)": (0.55, 126), "Pump (8 m)": (0.75, 126),
        "Punch": (0.50, 50)}
SHOT_GAP = {"AR": 0.182, "SMG": 0.083, "Pistol": 0.2, "Pump (point blank)": 0.9, "Pump (8 m)": 0.9, "Punch": 0.8}

def hits_for_charge(gun, share=0.4, slide=False, patched_counts=True):
    r, _ = HITS[gun]
    path = PATH_SLIDE if slide else PATH_STAND
    # the disc can only cover the part of the path it overlaps: width-limited by the path's 1.075 m width
    re = r + CELL / 2                                          # cells the disc touches count as hit
    covered_per_hit = min(math.pi * re * re, path)
    return max(1, math.ceil(share * path / covered_per_hit))

def breach_cost(gun, share=0.4, slide=False):
    """Seconds lost against running a free straight line: leave sprint, aim delay, the shots, sprint back to charge speed."""
    n = hits_for_charge(gun, share, slide)
    shots = (n - 1) * SHOT_GAP[gun]
    slow = SPRINT_TO_AIM * (SPRINT - RUN) / 2 / SPRINT       # you coast down to run speed while the aim delay runs
    back = (SPRINT - RUN) / 60 / 2 + 0.08                      # re-press sprint (human) and accelerate back up
    return n, round(SPRINT_TO_AIM + shots + slow + back, 2)

ROUTES = [
    # name, walk-round metres, straight metres, walls in the straight line
    ("Keep ground floor: zig-zag vs straight through", 52, 27, 2),
    ("Across the divide: compound wall vs through the keep", 60, 12, 1),
    ("Into a side building room with a shut door vs its wall", 14, 6, 1),
    ("Up to the keep's first floor: stairs vs (no floor breach for climbing)", 0, 0, 0),
]

def route_table(share=0.4):
    rows = []
    for name, walk, straight, walls in ROUTES:
        if walls == 0: continue
        n, c = breach_cost("AR", share)
        t_walk = walk / SPRINT
        t_breach = straight / SPRINT + walls * c
        rows.append((name, walk, straight, walls, round(t_walk, 2), round(t_breach, 2), round(t_walk - t_breach, 2)))
    return rows

# ---------- heal war: an attacker re-opening a wall a defender keeps healing
def heal_war(attacker="AR", patched_counts=True, share=0.4, react=0.40, dist=5.0, bar=100.0, n=2000, seed=3):
    """Attacker shoots the wall from `dist` m and sprints at it; defender sees the hit after `react` s and heals (0.2 s).
    Returns how often the attacker gets through on the first try, and the bar the defender spends per attempt."""
    rng = random.Random(seed)
    through = 0
    need = hits_for_charge(attacker, share)
    for _ in range(n):
        t_shots = (need - 1) * SHOT_GAP[attacker]
        # runway: from the last shot back up to charge speed and over the distance
        t_arrive = t_shots + 0.08 + dist / 9.0
        t_heal_done = max(0.1, rng.gauss(react, 0.06)) + 0.2
        if patched_counts or t_arrive < t_heal_done:
            through += 1
    return through / n

if __name__ == "__main__":
    print("Hits needed so a charge bursts through (share 0.4 / 0.6), standing and sliding:")
    for g in HITS:
        print(f"  {g:20s} stand {hits_for_charge(g,0.4)}/{hits_for_charge(g,0.6)}  slide {hits_for_charge(g,0.4,True)}/{hits_for_charge(g,0.6,True)}   cost {breach_cost(g)} s")
    for share in (0.4, 0.6):
        print(f"\nRoutes, share {share}:")
        for r in route_table(share): print("  ", r)
    for pc in (True, False):
        for d in (1.5, 3, 5, 8):
            print(f"heal war patched_counts={pc} dist {d}: attacker through first try {heal_war(patched_counts=pc, dist=d):.2f}")
