from duel import *
from multiprocessing import Pool
def job(a):
    label, args, kw = a
    return label, match(*args, **kw)
TH = dict(throw_windup=0.6, throw_cost=25, throw_dmg=70, throw_speed=17)
J=[]
for dis in (False, True):
  for w, D, sw in (("SMG",5.0,0.15),("PUMP",4.5,0.2)):
    cfg=default_cfg(); cfg.update(enter=0.1, lock_attacks=True, enemy_reinf_windup=0.4, swap=sw, disarm=dis, **TH)
    kw=dict(n=1200,cfg=cfg,weapon=w,D=D)
    T=dict(throw=True, cooldown_break=True)
    lab=f"disarm={dis} {w}"
    J += [(f"{lab} | thrower pro v beginner", ("pro","pro","beginner","basic"), dict(kw, A_kw=T)),
          (f"{lab} | thrower pro v mid", ("pro","pro","mid","basic"), dict(kw, A_kw=T)),
          (f"{lab} | thrower pro v non-sculptor", ("pro","pro","mid","passive"), dict(kw, A_kw=T)),
          (f"{lab} | thrower pro v pro", ("pro","pro","pro","pro"), dict(kw, A_kw=T, B_kw=dict(cooldown_break=True))),
          (f"{lab} | thrower pro v reinforcing pro", ("pro","pro","pro","pro"), dict(kw, A_kw=T, B_kw=dict(reinf=True, reinf_at=25, cooldown_break=True))),
          (f"{lab} | thrower mid v beginner", ("mid","pro","beginner","basic"), dict(kw, A_kw=T)),
          (f"{lab} | pro (no throw) v beginner", ("pro","pro","beginner","basic"), dict(kw, A_kw=dict(cooldown_break=True)))]
with Pool(4) as p:
    for label, r in p.map(job, J):
        print(f"{label:52s} winA {r['winA']:.2f} t {r['t']:.1f} bar {r['spentA']:.0f}/{r['spentB']:.0f} throwHits {r['throwA']:.2f}", flush=True)
