"""SMG at pro aim and crouch camping (2026-11-26). python variants3.py aim n -> results/variants3_<aim>.json"""
import json, sys
from variant import run
P = [("Pump", "SMG", [3, 6]), ("SMG", "AR", [3, 6, 10, 15]), ("SMG", "Pistol", [6, 10]), ("Pump", "AR", [3, 6]), ("Pistol", "AR", [10, 15, 25])]
NOCROUCH = {w: {"crouch": 1.0} for w in ("SMG", "AR", "Pistol")}
def merge(*ds):
    out = {}
    for d in ds:
        for w, kv in d.items(): out.setdefault(w, {}).update(kv)
    return out
V = {
    "live": {},
    "SMG move spread 0.35": {"SMG": {"move": 0.35}},
    "SMG move 0.35, slide 0.8, air 1.2": {"SMG": {"move": 0.35, "slide": 0.8, "air": 1.2}},
    "SMG head 1.5x (19.1)": {"SMG": {"head": 19.125}},
    "SMG move 0.35 + head 1.5x": {"SMG": {"move": 0.35, "head": 19.125}},
    "no crouch accuracy bonus": NOCROUCH,
    "SMG move 0.35 + no crouch bonus": merge({"SMG": {"move": 0.35}}, NOCROUCH),
    "SMG head 1.35x (17.2) + no crouch bonus": merge({"SMG": {"head": 17.2125}}, NOCROUCH),
    "SMG head 1.4x (17.85) + no crouch bonus": merge({"SMG": {"head": 17.85}}, NOCROUCH),
    "SMG head 1.5x (19.1) + no crouch bonus": merge({"SMG": {"head": 19.125}}, NOCROUCH),
}
if __name__ == "__main__":
    aim = sys.argv[1]; n = int(sys.argv[2]) if len(sys.argv) > 2 else 200
    names = sys.argv[3].split("|") if len(sys.argv) > 3 else list(V)
    out = {}
    for name in names:
        r = run(V[name], aim, n, P); out[name] = {"ov": V[name], "res": r}
        print(name, flush=True); [print(f"   {k:20s} {v[0]:.2f} / {v[1]:.2f}", flush=True) for k, v in r.items()]
    json.dump(out, open(f"results/variants3_{aim}.json", "w"), indent=1)
