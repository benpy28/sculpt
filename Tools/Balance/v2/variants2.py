import json
from variant import run
P = [("Pump","SMG",[3,6]),("Pump","AR",[3,6]),("SMG","AR",[6,10,15]),("Pistol","AR",[10,15,25,40]),("SMG","Pistol",[6,10])]
V = {
 "base": {},
 "AR short bursts (bloom cap 1.8, recovers after 0.1 s)": {"AR":{"bmax":1.8,"bdel":0.1}},
 "Pistol bloom 0.8/shot, cap 2.5": {"Pistol":{"bloom":0.8,"bmax":2.5}},
 "AR bursts + pistol bloom": {"AR":{"bmax":1.8,"bdel":0.1},"Pistol":{"bloom":0.8,"bmax":2.5}},
 "AR bursts + pistol 4/s + bloom 0.8": {"AR":{"bmax":1.8,"bdel":0.1},"Pistol":{"bloom":0.8,"bmax":2.5,"interval":0.25}},
 "Pump 0.75 s + SMG 11/s": {"Pump":{"interval":0.75},"SMG":{"interval":1/11}},
 "Pump 0.75 s + SMG 10/s": {"Pump":{"interval":0.75},"SMG":{"interval":0.1}},
}
if __name__ == "__main__":
  out = {}
  for name, ov in V.items():
    r = run(ov, "mid", 300, P); out[name] = {"ov": ov, "res": r}
    print(name); [print(f"   {k:20s} {v[0]:.2f}") for k, v in r.items()]
  json.dump(out, open("results/variants2.json", "w"))
