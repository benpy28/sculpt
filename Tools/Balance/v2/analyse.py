import json,sys
d=json.load(open(sys.argv[1]))
S=[tuple(s) for s in d["strats"]]; W=["Pump","SMG","AR","Pistol"]
for D in d["dists"]:
    M=d["M"][str(D)]; eq=d["eq"][str(D)]
    print(f"\n== {D} m")
    # weapon vs weapon: row weapon best strategy vs column weapon's best reply (maximin)
    print("  wpn-vs-wpn maximin:", end=" ")
    for a in W:
        ia=[i for i,s in enumerate(S) if s[0]==a]
        row=[]
        for b in W:
            ib=[j for j,s in enumerate(S) if s[0]==b]
            row.append(max(min(M[i][j] for j in ib) for i in ia))
        print(a, [round(x,2) for x in row], end="  ")
    print()
    # vs equilibrium mix
    vs=[sum(M[i][j]*eq[j] for j in range(len(S))) for i in range(len(S))]
    for a in W:
        best=max((i for i,s in enumerate(S) if s[0]==a), key=lambda i: vs[i])
        print(f"  {a:6s} best {S[best][1]:8s} tap {S[best][2]}  vs eq {vs[best]:.2f}")
    # movement value: per weapon, each move's best vs eq
    for a in W:
        print("   ",a, {m: round(max(vs[i] for i,s in enumerate(S) if s[0]==a and s[1]==m),2) for m in ["still","crouch","cstrafe","strafe","slide"]})
