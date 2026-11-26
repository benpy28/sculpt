"""SCULPT 1v1 gun duel simulator v2 (2026-11-25, after the destruction + momentum pivot).
Numbers are the live defaults (WeaponConfig.cs on claude/project-thread-an1npa, incl. the balance pass and the first-shot kick) (WeaponConfig.cs, MovementConfig.cs, Hand.cs).
120 Hz ticks. Open ground, both players see each other at t=0, fixed distance D, lateral motion only.

Aim model (same for both players unless a tier says otherwise):
  * first shot no earlier than a reaction time;
  * the shooter's crosshair follows where the target WAS `lag` seconds ago, extrapolated by the velocity it had then
    times `lead` (so steady motion is tracked well and direction changes, drops into a slide and jumps cost accuracy);
  * plus Gaussian hand error `sig` (degrees), plus recoil the player fails to pull down (1 - comp);
  * plus the gun's real spread cone (uniform disc, BaseSpread + MoveSpread*speed/run + bloom, crouch/slide/air rules)
    and the pump's fixed 9-pellet pattern.
Target: 0.7 m wide body box, 1.8 m standing / 1.1 m crouched or sliding; head 0.6 wide x top 0.33 m; at head height a shot
past the head's sides misses. Damage rounded once per victim per shot. 150 health."""
import math, random

TICK = 1 / 120
HP = 150
BODY_HALF = 0.35
HEAD_HALF = 0.30
HEAD_H = 0.33
STAND_H = 1.8
LOW_H = 1.1
RUN, SPRINT = 7.0, 10.0
ACCEL = 60.0
SLIDE_FRICTION, SLIDE_END = 4.5, 4.0
CARVE_FREE, CARVE_MAX, CARVE_LOSS = 1.6, 4.5, 0.08
GRAV, JUMP_V = 30.0, math.sqrt(2 * 30.0 * 1.3)

def W():
    return {
        "Pump": dict(pellets=9, body=9.4, head=14.1, interval=0.75, auto=True, fo=([5, 12, 25], [1, .65, .2]),
                     base=0.0, move=0.3, crouch=1.0, slide=1.5, air=1.5, pattern=2.6, bloom=0.0, bmax=0.0, brec=4, bdel=0.0,
                     rp=3.5, rfs=0.5, ry=0.6, rmax=6, rrec=11, rdel=8 / 60, mag=5, reload=0.6, shell=0.5, draw=0.4),
        "SMG": dict(pellets=1, body=12.75, head=19.125, interval=1 / 11, auto=True, fo=([7, 18, 35], [1, .6, .4]),
                    base=0.9, move=0.7, crouch=1.0, slide=1.2, air=1.8, pattern=0, bloom=0.12, bmax=1.6, brec=8, bdel=7 / 60,
                    rp=0.28, rfs=0.25, ry=0.2, rmax=5, rrec=10, rdel=7 / 60, mag=30, reload=1.6, draw=0.3),
        "AR": dict(pellets=1, body=21.5, head=34, interval=10.909 / 60, auto=True, fo=([0], [1]),
                   base=0.0, move=0.9, crouch=1.0, slide=3.0, air=4.0, pattern=0, bloom=0.45, bmax=3.0, brec=6, bdel=13 / 60,
                   rp=0.7, rfs=0.6, ry=0.3, rmax=7, rrec=9, rdel=13 / 60, mag=25, reload=1.9, draw=0.35),
        "Pistol": dict(pellets=1, body=25.5, head=37, interval=0.2, auto=False, fo=([25, 50], [1, .7]),
                       base=0.1, move=0.5, crouch=1.0, slide=1.5, air=2.0, pattern=0, bloom=0.8, bmax=2.5, brec=8, bdel=14 / 60,
                       rp=1.4, rfs=0.45, ry=0.25, rmax=6, rrec=14, rdel=4 / 60, mag=12, reload=1.3, draw=0.2),
    }

WEAPONS = W()
PAT_X = [0, .45, 0, -.45, 0, .75, -.75, -.75, .75]
PAT_Y = [0, 0, .45, 0, -.45, .75, .75, -.75, -.75]
YAW_PAT = [0.0, 0.4, 0.7, 0.3, -0.4, -0.9, -0.6, 0.2, 0.8, 0.5, -0.3, -0.7]

def falloff(w, d):
    ds, fs = w["fo"]
    if d <= ds[0]: return fs[0]
    for i in range(1, len(ds)):
        if d <= ds[i]:
            t = (d - ds[i - 1]) / (ds[i] - ds[i - 1]); return fs[i - 1] + (fs[i] - fs[i - 1]) * t
    return fs[-1]

def rnd(v):
    return int(math.floor(v + 0.5))

# Aim tiers. sig: hand error (deg). lag: tracking delay (s). lead: share of velocity extrapolated. comp: recoil pulled down.
# react: first-shot reaction (mean, sd). aim_off: how far below the target's top they aim (0.15 = head, 0.55 = chest).
AIM = {
    "beginner": dict(sig=0.85, lag=0.20, lead=0.65, comp=0.35, react=(0.30, 0.05), aim_off=0.60, hold=0.0),
    "mid":      dict(sig=0.50, lag=0.15, lead=0.80, comp=0.65, react=(0.22, 0.03), aim_off=0.40, hold=0.12),
    "pro":      dict(sig=0.30, lag=0.12, lead=0.92, comp=0.85, react=(0.18, 0.02), aim_off=0.18, hold=0.20),
}

N_TICKS = int(6.0 / TICK)

# ---------------- movement profiles: per-tick lateral x, feet height y, box height h, speed, mode
# mode: 0 standing ground, 1 crouched ground, 2 slide, 3 air, 4 sprint (no shooting)

def _approach(v, target, a):
    if v < target: return min(target, v + a * TICK)
    return max(target, v - a * TICK)

def move_profile(kind, rng, n=N_TICKS):
    xs, ys, hs, sp, md = [0.0] * n, [0.0] * n, [STAND_H] * n, [0.0] * n, [0] * n
    if kind == "still":
        return xs, ys, hs, sp, md
    if kind == "crouch":
        return xs, ys, [LOW_H] * n, sp, [1] * n
    x = 0.0; v = 0.0
    if kind == "strafe":      # holds A/D while shooting, reverses every 0.4-0.9 s
        d = rng.choice((-1, 1)); t_sw = rng.uniform(0.4, 0.9)
        for i in range(n):
            t = i * TICK
            if t >= t_sw: d = -d; t_sw = t + rng.uniform(0.4, 0.9)
            v = _approach(v, d * RUN, ACCEL); x += v * TICK
            xs[i] = x; sp[i] = abs(v)
        return xs, ys, hs, sp, md
    if kind == "cstrafe":     # moves, counter-strafes to a stop, shoots, moves on in a random direction
        st = "move"; d = rng.choice((-1, 1)); t_end = rng.uniform(0.2, 0.45)
        for i in range(n):
            t = i * TICK
            if st == "move" and t >= t_end: st = "stop"
            if st == "stop" and abs(v) < 1e-6:
                st = "dwell"; t_end = t + rng.uniform(0.07, 0.16)
            if st == "dwell" and t >= t_end:
                st = "move"; d = rng.choice((-1, 1)) if rng.random() < 0.5 else -d; t_end = t + rng.uniform(0.2, 0.45)
            v = _approach(v, d * RUN if st == "move" else 0.0, ACCEL)
            x += v * TICK; xs[i] = x; sp[i] = abs(v)
        return xs, ys, hs, sp, md
    if kind == "jump":        # strafe-jump spam: air lateral speed fixed (air nudge only), ~0.59 s airtime
        d = rng.choice((-1, 1)); y = 0.0; vy = JUMP_V; air = True; v = d * RUN
        for i in range(n):
            if air:
                vy -= GRAV * TICK; y += vy * TICK
                if y <= 0: y = 0; air = False; land = i
            else:
                if i - land >= 3:   # 25 ms on the ground, pick a side, jump again
                    d = rng.choice((-1, 1)) if rng.random() < 0.6 else d
                    v = _approach(v, d * RUN, ACCEL * 3)
                    air = True; vy = JUMP_V
                else:
                    v = _approach(v, d * RUN, ACCEL)
            x += v * TICK; xs[i] = x; ys[i] = y; sp[i] = abs(v); md[i] = 3 if air else 0
        return xs, ys, hs, sp, md
    if kind == "slide":
        # Slide from sprint speed (no entry boost) and carve: heading psi, 0 = straight across the shooter's view to the
        # right, pi = to the left. Carves turn toward a new heading at 1.2-4.5 rad/s, paying the carve tax above 1.6 rad/s.
        # At 4 m/s you stand up, sprint 0.12 s (no shooting) in a new direction, and slide again.
        psi = rng.choice((0.0, math.pi)) + rng.uniform(-0.4, 0.4); spd = SPRINT; st = "slide"; t_end = 0.0
        target = psi; rate = 2.0
        for i in range(n):
            t = i * TICK
            if st == "slide":
                if abs(target - psi) < 0.03:
                    if rng.random() < 0.4:   # carve right round to the other side of the view
                        base = math.pi if math.cos(psi) > 0 else 0.0
                        target = base + rng.uniform(-0.5, 0.5)
                        target += 2 * math.pi * round((psi - target) / (2 * math.pi))
                        rate = rng.uniform(3.0, CARVE_MAX)
                    else:
                        target = psi + rng.uniform(-0.8, 0.8); rate = rng.uniform(1.0, 2.5)
                dpsi = max(-rate * TICK, min(rate * TICK, target - psi))
                tr = abs(dpsi) / TICK
                if tr > CARVE_FREE: spd *= 1 - CARVE_LOSS * (tr - CARVE_FREE) * TICK
                psi += dpsi
                spd -= SLIDE_FRICTION * TICK
                x += spd * math.cos(psi) * TICK
                xs[i] = x; hs[i] = LOW_H; sp[i] = spd; md[i] = 2
                if spd <= SLIDE_END:
                    st = "sprint"; t_end = t + 0.12
                    psi = rng.choice((0.0, math.pi)) + rng.uniform(-0.5, 0.5); target = psi
            else:
                spd = min(SPRINT, spd + ACCEL * TICK)
                x += spd * math.cos(psi) * TICK
                xs[i] = x; sp[i] = spd; md[i] = 4
                if t >= t_end: st = "slide"
        return xs, ys, hs, sp, md
    raise ValueError(kind)

# ---------------- fight

class Shooter:
    def __init__(self, weapon, aim, move, tap=None, fire_stopped=False, rng=None, hp=HP, delay=0.0):
        self.w = WEAPONS[weapon] if isinstance(weapon, str) else weapon
        self.wname = weapon
        self.aim = AIM[aim] if isinstance(aim, str) else aim
        self.move = move
        self.tap = tap
        self.fire_stopped = fire_stopped
        self.hp = hp
        self.delay = delay

def fight(A, B, D, rng, max_t=6.0):
    """Returns 0 if A wins, 1 if B wins, 0.5 trade/draw, plus time."""
    ps = [A, B]
    mv = [move_profile(p.move, rng) for p in ps]
    hp = [A.hp, B.hp]
    st = []
    for p in ps:
        r = max(0.1, rng.gauss(*p.aim["react"])) + p.delay
        st.append(dict(next=r, last=-9.0, bloom=0.0, rec=0.0, recy=0.0, streak=0, mag=p.w["mag"], reload_until=0.0))
    n = min(N_TICKS, int(max_t / TICK))
    for i in range(n):
        t = i * TICK
        dmg = [0, 0]
        for k in (0, 1):
            p, s, me, tg = ps[k], st[k], mv[k], mv[1 - k]
            w = p.w
            # recovery of bloom/recoil
            since = t - s["last"]
            if since > w["bdel"] and s["bloom"] > 0: s["bloom"] = max(0.0, s["bloom"] - w["brec"] * TICK)
            if since > w["rdel"]:
                if s["rec"] > 0: s["rec"] = max(0.0, s["rec"] - w["rrec"] * TICK)
                if s["recy"] != 0:
                    s["recy"] = math.copysign(max(0.0, abs(s["recy"]) - w["rrec"] * TICK), s["recy"])
                if since > 0.3: s["streak"] = 0
            if t < s["next"] or t < s["reload_until"]: continue
            if me[4][i] == 4: continue
            if p.fire_stopped and me[3][i] > 1.2 and me[4][i] in (0, 1): continue
            if a_hold(p, s, tg, i, t): continue
            if s["mag"] <= 0:
                s["reload_until"] = t + w.get("reload", 1.5); s["mag"] = w["mag"]; continue
            dmg[1 - k] += shoot(p, s, me, tg, i, D, rng)
            s["mag"] -= 1
            s["last"] = t
            gap = w["interval"]
            if p.tap: gap = max(gap, p.tap)
            s["next"] = t + gap
        hp[0] -= dmg[0]; hp[1] -= dmg[1]
        if hp[0] <= 0 or hp[1] <= 0:
            if hp[0] <= 0 and hp[1] <= 0: return 0.5, t
            return (1 if hp[0] <= 0 else 0), t
    return 0.5, max_t

def a_hold(p, s, tg, i, t):
    h = p.aim.get("hold", 0.0)
    if h <= 0: return False
    w = p.w
    single = w["pellets"] > 1 or not w["auto"] or (p.tap and p.tap > w["interval"])
    if not single: return False
    if t - s["next"] >= h: return False
    lag = max(2, int(round(p.aim["lag"] / TICK)))
    j = max(1, i - lag)
    v0 = (tg[0][j] - tg[0][j - 1]) / TICK; v1 = (tg[0][i] - tg[0][i - 1]) / TICK
    y0 = (tg[1][j] - tg[1][j - 1]) / TICK; y1 = (tg[1][i] - tg[1][i - 1]) / TICK
    return abs(v1 - v0) > 1.5 or abs(y1 - y0) > 1.5 or tg[2][j] != tg[2][i]

def shoot(p, s, me, tg, i, D, rng):
    w, a = p.w, p.aim
    # where the shooter thinks the target is
    lag = max(1, int(round(a["lag"] / TICK)))
    j = max(1, i - lag)
    tx, ty, th = tg[0][i], tg[1][i], tg[2][i]
    vx = (tg[0][j] - tg[0][j - 1]) / TICK
    vy = (tg[1][j] - tg[1][j - 1]) / TICK
    ex = tg[0][j] + vx * a["lag"] * a["lead"]
    etop = tg[1][j] + tg[2][j] + vy * a["lag"] * a["lead"] * 0.5
    # The aim point sits a fixed distance below the top of the target: crouching folds the legs and torso, so the head
    # and upper chest keep their place relative to the top of the box (the old model scaled the offset with box height,
    # which put a pro's aim near the top edge of a crouched head and made crouching look stronger than it is).
    aim_y = etop - min(a["aim_off"], tg[2][j] - 0.1)
    # own movement shakes your aim a little (mouse already compensates most of it)
    own_v = me[3][i]
    hand = a["sig"] + math.degrees(own_v / max(D, 1) * 0.015)
    # gun spread
    spd = min(1.0, own_v / RUN)
    spread = w["base"] + w["move"] * spd + s["bloom"]
    mode = me[4][i]
    if mode == 2: spread += w["slide"]
    elif mode == 3: spread += w["air"]
    elif mode == 1: spread *= w["crouch"]
    hx = rng.gauss(0, hand); hy = rng.gauss(0, hand)
    rec_y = s["rec"] * (1 - a["comp"]); rec_x = s["recy"] * (1 - a["comp"])
    total = 0.0
    k = math.tan(math.radians(1)) * D   # metres per degree at D (small angles)
    for pel in range(w["pellets"]):
        ox, oy = hx + rec_x, hy + rec_y
        if w["pellets"] > 1:
            ox += PAT_X[pel] * w["pattern"]; oy += PAT_Y[pel] * w["pattern"]
        if spread > 0:
            r = math.sqrt(rng.random()) * spread; th_ = rng.random() * 2 * math.pi
            ox += r * math.cos(th_); oy += r * math.sin(th_)
        px = ex + ox * k; py = aim_y + oy * k
        dx = px - tx; top = ty + th
        if py < ty or py > top: continue
        if py >= top - HEAD_H:
            if abs(dx) <= HEAD_HALF: total += w["head"]
        elif abs(dx) <= BODY_HALF:
            total += w["body"]
    # recoil and bloom from this shot
    s["bloom"] = min(w["bmax"], s["bloom"] + w["bloom"])
    first = s["streak"] == 0 and s["rec"] == 0
    s["rec"] = min(w["rmax"], s["rec"] + w["rp"] + (w.get("rfs", 0.0) if first else 0.0))
    s["recy"] += w["ry"] * YAW_PAT[s["streak"] % len(YAW_PAT)]
    s["streak"] += 1
    if total <= 0: return 0
    return rnd(total * falloff(w, D))

def winrate(mkA, mkB, D, n=600, seed=1):
    rng = random.Random(seed)
    tot = 0.0; ts = 0.0; draws = 0
    for _ in range(n):
        r, t = fight(mkA(), mkB(), D, rng)
        tot += 1 - r if r != 0.5 else 0.5
        if r == 0.5: draws += 1
        ts += t
    return tot / n, ts / n, draws / n

def ttk_vs(weapon, aim, move_target, D, tap=None, shooter_move="still", fire_stopped=False, n=500, seed=3, target_hp=HP):
    """Time for one shooter to kill a target that does not shoot back."""
    rng = random.Random(seed)
    out = []
    for _ in range(n):
        A = Shooter(weapon, aim, shooter_move, tap=tap, fire_stopped=fire_stopped)
        B = Shooter("Pistol", aim, move_target, hp=target_hp)
        B.delay = 99  # never shoots
        r, t = fight(A, B, D, rng, max_t=6.0)
        out.append(t if r == 0 else 6.0)
    out.sort()
    return sum(out) / n, out[n // 2]
