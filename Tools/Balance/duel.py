"""SCULPT 1v1 duel simulator, 60 Hz ticks. Numbers default to SculptConfig/CLAUDE.md on m2-movement.
Aim is identical for both players (same reaction and same angular error); only sculpt speed and decisions differ."""
import math, random
from dataclasses import dataclass, field
from geom import visible, HEAD, HALF_W

TICK = 1 / 60

def default_cfg():
    return dict(
        windup=0.2, reinf_windup=0.35, enemy_reinf_windup=0.6,
        cost_move=5.0, cost_sink=7.0, cost_raise=10.0, cost_reinf=15.0, enemy_mult=2.0,
        regen_delay=1.5, regen_full=20.0, reinf_dur=20.0,
        shoot_in_windup=True, swap=0.0, sculpt_range=6.0,
        # throw (Ben's new idea); off unless a policy uses it
        throw_cost=14.0, throw_windup=0.6, throw_dmg=35.0, throw_speed=20.0, throw_shoot_lock=True,
        max_time=40.0,
    )

WEAPONS = {
    # rof shots/s, body, head, base angular sigma (rad, aim+weapon), bloom per shot, bloom cap, falloff fn
    "AR":  dict(rof=5.5, body=25, head=40, sig=0.006, bloom=0.0025, cap=0.015, pellets=1, spread=0.0),
    "SMG": dict(rof=12.0, body=15, head=15, sig=0.012, bloom=0.0008, cap=0.008, pellets=1, spread=0.0),
    "PUMP": dict(rof=1 / 0.9, body=11, head=16.5, sig=0.007, bloom=0.0, cap=0.0, pellets=9, spread=0.03),
}

def falloff(w, d):
    if w == "SMG":
        return 1.0 if d <= 10 else max(0.5, 1 - 0.05 * (d - 10))
    if w == "PUMP":
        if d <= 5: return 1.0
        if d <= 12: return 1 - 0.5 * (d - 5) / 7
        return max(0.15, 0.5 - 0.04 * (d - 12))
    return 1.0

# Sculpt skill tiers (seconds). notice = seeing/hearing a sculpt tell or a change; acq = put crosshair on the
# right block/face and press; retry = how tightly they re-click on a locked tile; follow = step 1 cell behind moved cover.
SKILL = {
    "beginner": dict(notice=0.40, acq=0.45, acq_sd=0.12, retry=0.12, follow=0.45, dodge=0.40),
    "mid":      dict(notice=0.28, acq=0.28, acq_sd=0.07, retry=0.06, follow=0.35, dodge=0.32),
    "pro":      dict(notice=0.18, acq=0.14, acq_sd=0.03, retry=0.017, follow=0.28, dodge=0.26),
}
# Decision policies.
POLICY = {
    # never attacks walls, rebuilds plain, waits out threats
    "basic":   dict(break_p=0.25, smart=False, attack="sink", chain=False, counter="wait", reinf_at=999, reserve=10, turtle=False),
    "breaker": dict(break_p=0.6, smart=True, attack="sink", chain=False, counter="wait", reinf_at=999, reserve=10, turtle=False),
    "pro":     dict(break_p=0.7, smart=True, attack="best", chain=True, counter="smart", reinf_at=45, reserve=10, turtle=False),
    "turtle":  dict(break_p=0.0, smart=False, attack="sink", chain=False, counter="wait", reinf_at=25, reserve=0, turtle=True),
    "passive": dict(break_p=0.0, smart=False, attack="sink", chain=False, counter="wait", reinf_at=999, reserve=0, turtle=False),
    "nocover": dict(break_p=0.0, smart=False, attack="sink", chain=False, counter="shoot", reinf_at=999, reserve=0, turtle=False, never_build=True),
}

REACT = (0.20, 0.03)      # equal for both: see a target -> first shot
ANTICIPATE = (0.09, 0.02) # you caused it (your break just landed) -> first shot

@dataclass
class P:
    name: str
    skill: dict
    pol: dict
    hp: float = 150.0
    bar: float = 100.0
    last_spend: float = -99.0
    crouch: bool = True
    mode: str = "gun"          # gun | sculpt
    gun_ready: float = 0.0
    wall: int = 1              # wall height in blocks, in line with the enemy
    wall_k: int = 0            # wall pulled toward enemy by k cells
    wall_shifted: bool = False # knocked sideways out of line
    wall_reinf: list = field(default_factory=list)  # reinforce-until time per level (index 0 = bottom)
    wall_lock: object = None   # (owner, until) lock on the wall top
    task: str = "idle"
    t_end: float = 0.0
    stage: str = ""
    pending: dict = None
    events: list = field(default_factory=list)
    tracking: bool = False
    first_shot: float = 0.0
    next_shot: float = 0.0
    consec: int = 0
    last_shot: float = -9.0
    anticipate: bool = False
    last_peek: bool = False
    # stats
    actions: int = 0
    spent: float = 0.0
    shots: int = 0
    dmg_done: float = 0.0
    exposed_full: float = 0.0
    breaks: int = 0
    rebuilds: int = 0
    throws_hit: int = 0
    dodge_done: object = None

def nrm(rng, m, s):
    return max(0.0, rng.gauss(m, s))

class Duel:
    def __init__(self, a, b, cfg, D=5.0, weapon="SMG", seed=0):
        self.p = [a, b]
        self.cfg = cfg
        self.D = D
        self.w = weapon
        self.rng = random.Random(seed)
        self.t = 0.0
        self.log = []

    # ---------- helpers
    def other(self, x):
        return self.p[1] if x is self.p[0] else self.p[0]

    def wall_reinforced(self, x):
        return x.wall > 0 and len(x.wall_reinf) >= x.wall and x.wall_reinf[x.wall - 1] > self.t

    def in_sculpt_range(self):
        # eye to nearest point of the enemy wall block
        dx = max(0.0, self.D - 1.8)
        return math.hypot(dx, 0.42) <= self.cfg["sculpt_range"] + 1e-9

    def wall_in_line(self, x):
        return 0 if x.wall_shifted else x.wall

    def vis(self, s, t):
        return visible(round(self.D, 2), s.crouch, 0, self.wall_in_line(s), s.wall_k,
                       t.crouch, 0, self.wall_in_line(t), t.wall_k)

    def spend(self, x, c):
        x.bar -= c
        x.spent += c
        x.last_spend = self.t
        x.actions += 1

    def regen(self, x):
        c = self.cfg
        if self.t - x.last_spend > c["regen_delay"] and x.bar < 100:
            x.bar = min(100.0, x.bar + 100.0 / c["regen_full"] * TICK)

    def acq_time(self, x, scale=1.0):
        s = x.skill
        return nrm(self.rng, s["acq"] * scale, s["acq_sd"] * scale)

    def notify(self, x, kind, delay=None, data=None):
        d = x.skill["notice"] if delay is None else delay
        x.events.append((self.t + nrm(self.rng, d, d * 0.2), kind, data))

    def attack_cost(self, atk, tgt, kind):
        c = self.cfg
        enemy_r = self.wall_reinforced(tgt)
        if kind == "throw":
            return None if enemy_r else c["throw_cost"], c["throw_windup"]
        base = c["cost_sink"] if kind == "sink" else c["cost_move"]
        w = c.get("break_windup") or c["windup"]
        if enemy_r:
            base *= c["enemy_mult"]
            w = max(w, c["enemy_reinf_windup"])
        return base, w

    def choose_attack(self, x, e):
        k = x.pol["attack"]
        if k != "best":
            return k
        # pro: throw if allowed and the target is unreinforced; shift a lone 1-high plain wall when cheap; else sink
        if x.pol.get("throw") and not self.wall_reinforced(e):
            return "throw"
        if e.wall == 1 and not self.wall_reinforced(e) and x.pol.get("use_shift", True):
            return "shift" if self.rng.random() < 0.5 else "sink"
        return "sink"

    # ---------- task starters
    def start_hide(self, x, dur):
        x.task, x.t_end, x.crouch = "hide", self.t + dur, True
        self.to_gun(x)

    def start_peek(self, x, dur):
        x.task, x.t_end, x.crouch = "peek", self.t + dur, False
        self.to_gun(x)

    def to_gun(self, x):
        if x.mode != "gun":
            x.mode = "gun"
            x.gun_ready = self.t + self.cfg["swap"]

    def start_break(self, x, e):
        kind = self.choose_attack(x, e)
        cost, w = self.attack_cost(x, e, kind)
        if cost is None:
            kind = "sink"; cost, w = self.attack_cost(x, e, kind)
        x.task, x.stage = "break", "acq"
        x.crouch = False                       # you must stand to see the enemy wall over yours
        x.mode = "sculpt"
        x.t_end = self.t + max(self.cfg.get("enter", 0.0), self.acq_time(x, 1.3 if kind == "shift" else 1.0))
        x.pending = dict(kind=kind)

    def start_rebuild(self, x, prepped=False):
        x.task, x.stage = "rebuild", "acq"
        x.crouch = True
        if self.cfg.get("quick_cover"):
            # one-press cover bind: raises the piece in front of you, gun stays up
            x.t_end = self.t + nrm(self.rng, 0.06, 0.02)
            return
        x.mode = "sculpt"
        if not prepped:
            x.t_end = self.t + max(self.cfg.get("enter", 0.0), self.acq_time(x))

    # ---------- per tick logic
    def decide(self, x, e):
        t, c, pol = self.t, self.cfg, x.pol
        # perceived events
        for ev in [ev for ev in x.events if ev[0] <= t]:
            x.events.remove(ev)
            kind, data = ev[1], ev[2]
            if kind == "threat":
                if x.task in ("rebuild",):
                    continue
                if data == "throw":
                    # dodge sideways out of the path; costs your wall alignment either way
                    if pol.get("dodge", True):
                        x.task, x.t_end, x.stage = "dodge", t + nrm(self.rng, x.skill["dodge"], 0.05), ""
                        x.dodge_done = x.t_end
                        x.crouch = True; self.to_gun(x)
                    continue
                if pol["counter"] == "shoot":
                    self.start_peek(x, 0.6)
                elif pol["counter"] == "smart" and x.bar >= self.attack_cost(x, e, "sink")[0] + c["cost_raise"] \
                        and e.wall > 0 and not e.wall_lock and self.in_sculpt_range():
                    # counter-break: their wall goes too, so it becomes a shootout where they are standing in sculpt mode
                    self.start_break(x, e)
                else:
                    # wait crouched in sculpt mode with the crosshair on your own wall column, ready to rebuild
                    if data == "sink" and not pol.get("never_build"):
                        x.task, x.stage, x.crouch, x.mode = "rebuild", "acq", True, "sculpt"
                        x.t_end = t + max(c.get("enter", 0.0), self.acq_time(x))
                    else:
                        self.start_hide(x, 0.5)
            elif kind == "wall_gone":
                if x.wall == 0 and x.task != "rebuild":
                    if not pol.get("never_build") and x.bar >= c["cost_raise"]:
                        self.start_rebuild(x)
                    else:
                        self.start_peek(x, 3.0)
            elif kind == "wall_shifted":
                if x.wall_shifted and x.task != "follow":
                    x.task, x.t_end, x.crouch = "follow", t + x.skill["follow"], True
                    self.to_gun(x)
            elif kind == "enemy_rebuilt":
                if pol["chain"] and x.task in ("idle", "hide", "peek", "post") and self.in_sculpt_range() \
                        and x.bar >= self.attack_cost(x, e, "sink")[0] + pol["reserve"] and e.wall > 0:
                    self.start_break(x, e)

        # task progress
        if x.task == "break":
            if x.stage == "acq" and t >= x.t_end:
                kind = x.pending["kind"]
                cost, w = self.attack_cost(x, e, kind)
                if cost is None:
                    kind = "sink"; cost, w = self.attack_cost(x, e, kind)
                if self.wall_in_line(e) == 0 or not self.in_sculpt_range():
                    x.task = "idle"; x.pending = None
                elif e.wall_lock is not None:
                    x.t_end = t + x.skill["retry"]          # tile locked: re-click
                elif x.bar < cost:
                    x.task = "idle"; x.pending = None
                else:
                    self.spend(x, cost)
                    x.breaks += 1
                    x.pending = dict(kind=kind, done=t + w, target=e)
                    e.wall_lock = (x, t + w)
                    x.stage = "windup"
                    self.notify(e, "threat", data=kind)
                    lock_gun = (kind == "throw" and c["throw_shoot_lock"]) or not c["shoot_in_windup"] or c.get("lock_attacks", False)
                    if not lock_gun:
                        self.to_gun(x)
            elif x.stage == "windup" and t >= x.pending["done"]:
                self.resolve_attack(x, e)
                x.stage, x.t_end = "post", t + 0.5
                x.anticipate = True
                self.to_gun(x)
            elif x.stage == "post" and t >= x.t_end:
                x.task = "idle"
        elif x.task == "rebuild":
            if x.stage == "acq" and t >= x.t_end:
                reinf = x.bar >= pol["reinf_at"] and x.bar >= c["cost_raise"] + c["cost_reinf"]
                cost = c["cost_raise"] + (c["cost_reinf"] if reinf else 0)
                target_h = 2 if pol["turtle"] and x.wall >= 1 else 1
                if x.wall_shifted:
                    x.task = "idle"
                elif x.wall >= target_h:
                    x.task = "idle"   # nothing to do (attack fizzled or was a shift)
                    if not pol["turtle"]:
                        self.start_hide(x, 0.3)
                elif x.wall_lock is not None:
                    x.t_end = t + x.skill["retry"]
                elif x.bar < cost:
                    if x.bar >= c["cost_raise"]:
                        cost, reinf = c["cost_raise"], False
                        self._confirm_rebuild(x, e, cost, reinf)
                    else:
                        self.start_peek(x, 3.0)
                else:
                    self._confirm_rebuild(x, e, cost, reinf)
            elif x.stage == "windup" and t >= x.pending["done"]:
                x.wall += 1
                while len(x.wall_reinf) < x.wall:
                    x.wall_reinf.append(-1)
                x.wall_reinf[x.wall - 1] = (t + c["reinf_dur"]) if x.pending["reinf"] else -1
                x.wall_lock = None
                x.pending = None
                x.rebuilds += 1
                self.notify(e, "enemy_rebuilt")
                if pol["turtle"] and x.wall < 2 and x.bar >= c["cost_raise"]:
                    self.start_rebuild(x)
                elif self.wall_in_line(e) == 0:
                    self.start_peek(x, 0.6)     # they have no cover: stand and take the fight now
                else:
                    self.start_hide(x, 0.35)
        elif x.task == "follow" and t >= x.t_end:
            x.wall_shifted = False
            self.start_hide(x, 0.3)
        elif x.task == "disarmed":
            if t >= x.t_end:
                x.task = "idle"
            else:
                return
        elif x.task == "dodge" and t >= x.t_end:
            x.task = "dodged"
        elif x.task in ("hide", "peek") and t >= x.t_end:
            x.task = "idle"

        if x.task == "dodged":
            # after a dodge you are one cell to the side with no wall: rebuild
            x.task = "idle"

        if x.task == "idle":
            self.idle_choice(x, e)

        # turtle refreshes reinforcement before it lapses
        if pol["turtle"] and x.task == "hide" and x.wall > 0 and x.wall_reinf and \
                x.wall_reinf[x.wall - 1] - t < 1.0 and x.bar >= 22 and x.wall_lock is None:
            # reinforced sink then reinforced raise is the in-place refresh; approximate as one 25% raise after a free top removal
            x.wall -= 1
            x.wall_reinf = x.wall_reinf[:x.wall]
            self.start_rebuild(x)

    def _confirm_rebuild(self, x, e, cost, reinf):
        c = self.cfg
        self.spend(x, cost)
        w = c["reinf_windup"] if reinf else c.get("rebuild_windup") or c["windup"]
        if reinf: w = max(w, c.get("rebuild_windup") or 0)
        x.pending = dict(done=self.t + w, reinf=reinf)
        x.wall_lock = (x, self.t + w)
        x.stage = "windup"
        if c["shoot_in_windup"]:
            self.to_gun(x)

    def resolve_attack(self, x, e):
        c = self.cfg
        kind = x.pending["kind"]
        e.wall_lock = None
        if self.wall_in_line(e) == 0:
            return
        if kind == "shift" and e.wall == 1:
            e.wall_shifted = True
            e.wall_reinf = []
            self.notify(e, "wall_shifted", delay=e.skill["notice"] * 0.6)
        else:
            e.wall -= 1
            e.wall_reinf = e.wall_reinf[:e.wall]
            if kind == "throw":
                flight = 1.0 / c["throw_speed"]
                dd = getattr(e, "dodge_done", None)
                if dd is None or dd > self.t + flight:
                    self.damage(x, e, c["throw_dmg"])
                    x.throws_hit += 1
                    if c.get("disarm") and c["throw_dmg"] > 30:
                        dis = 0.2 + 0.3 * min(1.0, (c["throw_dmg"] - 30) / 70)
                        draw = {"SMG": 0.3, "PUMP": 0.4, "AR": 0.35}[self.w]
                        e.task, e.t_end, e.pending = "disarmed", self.t + dis, None
                        e.mode = "gun"; e.gun_ready = self.t + dis + draw
                        e.tracking = False
                if dd is not None:
                    e.wall = 0          # dodged a cell aside: no wall in line there
                    e.dodge_done = None
            if e.wall == 0:
                self.notify(e, "wall_gone", delay=e.skill["notice"] * 0.6)
            elif e.pol["turtle"]:
                self.notify(e, "wall_gone", delay=e.skill["notice"] * 0.6)

    def idle_choice(self, x, e):
        pol, c = x.pol, self.cfg
        if x.wall == 0 and not x.wall_shifted:
            if pol.get("cover_hp") is not None and x.hp > pol["cover_hp"]:
                self.start_peek(x, 0.1); return
            if not pol.get("never_build") and x.bar >= c["cost_raise"]:
                self.start_rebuild(x); return
            self.start_peek(x, 2.0); return
        if pol["turtle"]:
            if x.wall < 2 and x.bar >= c["cost_raise"]:
                self.start_rebuild(x); return
            self.start_hide(x, 0.5); return
        e_exposed = self.wall_in_line(e) == 0
        if e_exposed:
            self.start_peek(x, 0.6); return
        can_break = self.in_sculpt_range() and e.wall_lock is None
        if can_break:
            cost, _ = self.attack_cost(x, e, "sink")
            ok = x.bar >= cost + pol["reserve"]
            want = pol["break_p"] > 0 and self.rng.random() < pol["break_p"] * (0.5 if x.last_peek else 1.0)
            if pol["smart"]:
                want = want and e.crouch   # break when they are hidden and can't punish your stand-up
            if pol.get("cooldown_break") and self.w == "PUMP":
                want = ok and (self.t - e.last_shot) < 0.35   # their pump is cycling: punish it
            if ok and want:
                self.start_break(x, e); return
        # plain peek war
        if x.last_peek:
            x.last_peek = False
            self.start_hide(x, self.rng.uniform(0.35, 0.9))
        else:
            x.last_peek = True
            self.start_peek(x, self.rng.uniform(0.4, 0.9))

    # ---------- shooting
    def fire(self, x, e):
        t = self.t
        if x.mode != "gun" or t < x.gun_ready or x.task in ("follow", "dodge"):
            x.tracking = False
            return
        v = self.vis(x, e)
        if v is None or self.vis(e, x) is None and False:
            x.tracking = False
            return
        if not x.tracking:
            x.tracking = True
            m, s = ANTICIPATE if x.anticipate else REACT
            x.first_shot = t + nrm(self.rng, m, s)
            x.anticipate = False
        if t < x.first_shot or t < x.next_shot:
            return
        W = WEAPONS[self.w]
        if t - x.last_shot > 0.35:
            x.consec = 0
        sig = W["sig"] + min(W["cap"], W["bloom"] * x.consec)
        lo, hi, top = v
        aim = hi - 0.3 if hi - lo > 0.7 else (lo + hi) / 2
        dmg = 0.0
        ex0 = self.rng.gauss(0, sig * self.D)
        ey0 = self.rng.gauss(0, sig * self.D)
        for _ in range(W["pellets"]):
            ex = ex0 + self.rng.gauss(0, W["spread"] * self.D)
            ey = aim + ey0 + self.rng.gauss(0, W["spread"] * self.D)
            if abs(ex) < HALF_W and lo <= ey <= hi:
                dmg += W["head"] if ey >= top - HEAD else W["body"]
        dmg *= falloff(self.w, self.D)
        x.shots += 1
        x.consec += 1
        x.last_shot = t
        x.next_shot = t + 1.0 / W["rof"]
        if dmg > 0:
            self.damage(x, e, dmg)

    def damage(self, x, e, d):
        e.hp -= d
        x.dmg_done += d

    def run(self):
        a, b = self.p
        c = self.cfg
        for x in self.p:
            if x.task == "idle":
                self.start_hide(x, self.rng.uniform(0.0, 1.0))
        while self.t < c["max_time"]:
            order = self.p if self.rng.random() < 0.5 else self.p[::-1]
            for x in order:
                e = self.other(x)
                self.regen(x)
                self.decide(x, e)
            for x in order:
                e = self.other(x)
                self.fire(x, e)
                if self.vis(e, x) is not None and self.wall_in_line(x) == 0:
                    x.exposed_full += TICK
            if a.hp <= 0 or b.hp <= 0:
                break
            self.t += TICK
        if a.hp <= 0 and b.hp <= 0:
            return "draw"
        if b.hp <= 0: return "A"
        if a.hp <= 0: return "B"
        return "timeout"

def make(name, skill, pol, cfg, bar=100.0, wall=1, reinf=False, hp=150.0, **polx):
    pd = dict(POLICY[pol]); pd.update(polx)
    x = P(name, SKILL[skill], pd, hp=hp, bar=bar, wall=wall)
    x.wall_reinf = [cfg["reinf_dur"] if reinf else -1] * wall
    return x

def match(skA, polA, skB, polB, n=400, cfg=None, D=5.0, weapon="SMG", seed=1, A_kw=None, B_kw=None, start=None):
    cfg = cfg or default_cfg()
    A_kw = A_kw or {}; B_kw = B_kw or {}
    res = dict(A=0, B=0, draw=0, timeout=0)
    st = dict(t=0.0, apmA=0.0, apmB=0.0, spentA=0.0, spentB=0.0, hpA=0.0, hpB=0.0, expA=0.0, expB=0.0, throwA=0)
    for i in range(n):
        a = make("A", skA, polA, cfg, **A_kw)
        b = make("B", skB, polB, cfg, **B_kw)
        d = Duel(a, b, cfg, D=D, weapon=weapon, seed=seed * 100003 + i)
        if start: start(d)
        r = d.run()
        res[r] += 1
        T = max(d.t, 1e-3)
        st["t"] += d.t
        st["apmA"] += a.actions / T * 60; st["apmB"] += b.actions / T * 60
        st["spentA"] += a.spent; st["spentB"] += b.spent
        st["hpA"] += max(0, a.hp); st["hpB"] += max(0, b.hp)
        st["expA"] += a.exposed_full; st["expB"] += b.exposed_full
        st["throwA"] += a.throws_hit
    for k in st: st[k] /= n
    decided = res["A"] + res["B"] + res["draw"]
    st["winA"] = (res["A"] + 0.5 * res["draw"]) / n
    st["winA_decided"] = (res["A"] + 0.5 * res["draw"]) / decided if decided else float("nan")
    st["timeout"] = res["timeout"] / n
    return st


def throw_numbers(N, cfg=None, windup_base=15, windup_per=0.25, v0=22.0, v_per=1/16, angle=8.0, g=15.0,
                  cost_base=10.0, cost_per=0.25, dmg_base=20.0, dmg_per=0.6, cap=100.0, D=None, h0=1.2):
    """Coordinator's starting throw formulas (N sub-cells, 64 = one tile). Returns windup s, speed, cost, dmg, range."""
    w = (windup_base + N * windup_per) / 60
    v = v0 - N * v_per
    a = math.radians(angle)
    vx, vy = v * math.cos(a), v * math.sin(a)
    tf = (vy + math.sqrt(vy * vy + 2 * g * h0)) / g
    rng = vx * tf
    def impact(dist):
        t = dist / vx
        vyi = vy - g * t
        return math.hypot(vx, vyi), t
    out = dict(windup=w, speed=v, cost=cost_base + cost_per * N, range=rng)
    if D is not None:
        vi, t = impact(min(D, rng))
        f = max(0.6, min(1.2, vi / 18.0))
        out.update(flight=t, dmg=min(cap, (dmg_base + dmg_per * N) * f), factor=f)
    return out

def open_start(d):
    for x in d.p:
        x.crouch = False; x.task = "idle"; x.wall = 0; x.wall_reinf = []
        x.tracking = False
