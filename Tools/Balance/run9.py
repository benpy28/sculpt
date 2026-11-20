from duel import *
from multiprocessing import Pool
def job(a):
    label, args, kw = a
    return label, match(*args, **kw)
TH = dict(throw_windup=0.6, throw_cost=25, throw_dmg=70, throw_speed=17)
REC = dict(enter=0.1, lock_attacks=True, enemy_reinf_windup=0.4, **TH)
J=[]
for name, base in (("current", dict(**TH)), ("recommended", REC)):
  for w, D, sw in (("SMG",5.0,0.15),("PUMP",4.5,0.2),("AR",7.0,0.17)):
    cfg=default_cfg(); cfg.update(base)
    if name=="recommended": cfg["swap"]=sw
    kw=dict(n=1200,cfg=cfg,weapon=w,D=D)
    T=dict(throw=True)
    J += [(f"{name} {w} | skill pro v beg", ("pro","pro","beginner","basic"), kw),
          (f"{name} {w} | speed pro v beg (same brain)", ("pro","pro","beginner","pro"), kw),
          (f"{name} {w} | speed pro v mid (same brain)", ("pro","pro","mid","pro"), kw),
          (f"{name} {w} | brain (same speed)", ("mid","pro","mid","basic"), kw),
          (f"{name} {w} | mid v beg", ("mid","basic","beginner","basic"), kw),
          (f"{name} {w} | sculptor v non-sculptor", ("mid","pro","mid","passive"), kw),
          (f"{name} {w} | plain pro v reinforcing pro", ("pro","pro","pro","pro"), dict(kw, B_kw=dict(reinf=True, reinf_at=25))),
          (f"{name} {w} | thrower mid v beginner", ("mid","pro","beginner","basic"), dict(kw, A_kw=T)),
          (f"{name} {w} | thrower pro v pro", ("pro","pro","pro","pro"), dict(kw, A_kw=T)),
          (f"{name} {w} | pro v turtle", ("pro","pro","pro","turtle"), dict(kw, n=400, B_kw=dict(wall=1, reinf=True)))]
with Pool(4) as p:
    for label, r in p.map(job, J):
        print(f"{label:48s} winA {r['winA']:.2f} t {r['t']:.1f} apm {r['apmA']:.0f}/{r['apmB']:.0f} bar {r['spentA']:.0f}/{r['spentB']:.0f} throwHits {r['throwA']:.2f} to {r['timeout']:.2f}", flush=True)
