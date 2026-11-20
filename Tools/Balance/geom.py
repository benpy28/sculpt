"""Side-view line-of-sight geometry for a 1v1 lane. Units metres. Numbers from CLAUDE.md / MovementConfig."""
from functools import lru_cache
B = 1.2           # block size
HALF_W = 0.35     # player half width
STAND_H, STAND_EYE = 1.8, 1.62
CROUCH_H, CROUCH_EYE = 1.1, 0.95
HEAD = 0.25       # top 25 cm of the box counts as head

@lru_cache(maxsize=None)
def visible(D, s_crouch, s_floor, s_wall, s_wall_k, t_crouch, t_floor, t_wall, t_wall_k):
    """Visible vertical span (lo, hi, top) of the target's front face seen from the shooter's eye, or None.
    *_wall = wall height in blocks (0 = none or knocked out of line), *_wall_k = cells the wall sits further
    toward the enemy than normal (0 = adjacent cell). Floors are in blocks (pillar height)."""
    eye = s_floor * B + (CROUCH_EYE if s_crouch else STAND_EYE)
    feet = t_floor * B
    top = feet + (CROUCH_H if t_crouch else STAND_H)
    xt = D - HALF_W
    rects = []
    if s_wall > 0:
        rects.append((0.6 + B * s_wall_k, 1.8 + B * s_wall_k, s_wall * B))
    if t_wall > 0:
        rects.append((D - 1.8 - B * t_wall_k, D - 0.6 - B * t_wall_k, t_wall * B))
    if s_floor > 0:
        rects.append((HALF_W, 0.6, s_floor * B))   # lip of own pillar
    if t_floor > 0:
        rects.append((D - 0.6, xt, t_floor * B))
    lo = hi = None
    n = 90
    for i in range(n + 1):
        y = feet + (top - feet) * i / n
        ok = True
        for a, b, h in rects:
            a, b = max(a, 1e-6), min(b, xt)
            if a >= b:
                continue
            ya = eye + (y - eye) * a / xt
            yb = eye + (y - eye) * b / xt
            if ya < h or yb < h:
                ok = False
                break
        if ok:
            lo = y if lo is None else lo
            hi = y
    if lo is None or hi - lo < 0.04:
        return None
    return (lo, hi, top)

if __name__ == "__main__":
    # Quick facts used in the write-up
    for D in (4, 5, 6, 8, 15, 25):
        print(D, "stand vs stand behind 1-high:", visible(D, False, 0, 1, 0, False, 0, 1, 0),
              " crouched target:", visible(D, False, 0, 1, 0, True, 0, 1, 0),
              " from +1 pillar vs crouched:", visible(D, False, 1, 0, 0, True, 0, 1, 0),
              " from +2 pillar:", visible(D, False, 2, 0, 0, True, 0, 1, 0))
    for D in (4, 5, 6, 8, 12, 20):
        print(D, "pulled wall (k=1) crouched target visible:", visible(D, False, 0, 1, 0, True, 0, 1, 1))
