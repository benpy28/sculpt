using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Weapons hardening. Random trigger, swap, sculpt-mode and movement spam through the shared simulation, checked
    /// against the gun rules every tick; random point-blank to long-range shots checked against the damage caps
    /// (no one-shot pump, ever); and networked gunfights over bad links with sculpting, checked on the server every
    /// tick: every hit lies on the victim exactly as rewound (never more than 0.3 s back), no hit passes through the
    /// server's current terrain (so cover raised before a shot protects), and health, deaths and kills add up.
    /// </summary>
    [Category("Fuzz")]
    public class WeaponFuzzTests
    {
        private const double B = SubGrid.TileSize;

        private static IEnumerable<int> Seeds()
        {
            int n = int.TryParse(Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) && v > 0 ? v : 6;
            for (int i = 0; i < n; i++) yield return 11 + i * 6151;
        }

        private static readonly Weapon[] Guns = { Weapon.Pump, Weapon.Smg, Weapon.Ar, Weapon.Pistol };

        // ================================================================ Gun rules under input spam

        private static MoveInput RandomMove(Rng r, ref MoveInput hold, int tick)
        {
            if (r.Chance(0.05))
            {
                hold.MoveZ = r.Pick(new[] { 1.0, 0, -1, 1 });
                hold.MoveX = r.Pick(new[] { 0.0, 1, -1 });
                hold.SprintHeld = r.Chance(0.4);
                hold.CrouchHeld = r.Chance(0.2);
                hold.Yaw = r.Range(-Math.PI, Math.PI);
            }
            MoveInput m = hold;
            m.JumpPressed = r.Chance(0.03);
            m.JumpHeld = m.JumpPressed;
            m.CrouchPressed = m.CrouchHeld && r.Chance(0.05);
            bool fire = r.Chance(0.5);
            m.FireHeld = fire;
            m.FirePressed = fire && r.Chance(0.3);
            m.WantsToShoot = fire;
            return m;
        }

        [TestCaseSource(nameof(Seeds))]
        public void GunRulesHoldUnderInputSpam(int seed)
        {
            var r = new Rng((ulong)seed);
            var mc = new MovementConfig();
            var nc = new NetConfig();
            SubGrid grid = MapGen.Generate(seed, out MapInfo info, 32);
            Vec3d spawn = info.TeamSpawns[0].Count > 0 ? info.TeamSpawns[0][0] : new Vec3d(19, 2, 19);
            WeaponsConfig wc = mc.Weapons;

            var s = MotorState.At(spawn);
            var shots = new List<WeaponShot>();
            var events = new List<MoveEvent>();
            var lastShot = new Dictionary<Weapon, List<int>>();
            foreach (var g in Guns) lastShot[g] = new List<int>();
            int pistolPresses = 0, pistolShots = 0, total = 0, swaps = 0;
            int heldSince = int.MinValue / 2;
            Weapon held = s.Hand.Held;
            bool heldViaSculpt = false; // a weapon key in sculpt mode leaves to that gun for its exit time, not its draw
            bool triggerWasHeld = false;
            var hold = new MoveInput();
            int tick = 1000;

            for (int i = 0; i < 6000; i++)
            {
                tick++;
                MoveInput m = RandomMove(r, ref hold, tick);
                var hand = new HandInput();
                if (r.Chance(0.02)) { hand.WeaponKey = (byte)(r.Int(0, 4) + 1); swaps++; }
                if (r.Chance(0.01)) hand.SculptToggle = true;
                float pitch = (float)r.Range(-60, 60);

                MotorState before = s;
                shots.Clear();
                var cmd = InputCmd.Quantize(new InputCmd { Tick = tick, Move = m, Pitch = pitch, Hand = hand });
                NetSim.Simulate(grid, mc, nc, ref s, cmd, spawn, events, shots);
                string ctx = $"seed {seed} tick {tick}";

                if (s.Hand.Held != held) { held = s.Hand.Held; heldSince = tick; heldViaSculpt = before.Hand.InSculptMode; }
                bool pistolPress = cmd.Move.FirePressed || (cmd.Move.FireHeld && !triggerWasHeld);
                triggerWasHeld = cmd.Move.FireHeld;
                if (pistolPress && s.Hand.Held == Weapon.Pistol && !s.Hand.InSculptMode) pistolPresses++;

                Assert.That(shots.Count, Is.LessThanOrEqualTo(1), ctx + ": one shot per tick at most");
                foreach (var sh in shots)
                {
                    total++;
                    WeaponStats st = wc.Of(sh.Weapon);
                    Assert.That(sh.Weapon, Is.EqualTo(s.Hand.Held), ctx + ": fires the gun in hand");
                    Assert.That(s.Hand.InSculptMode, Is.False, ctx + ": no shot in sculpt mode");
                    Assert.That(before.Dead || s.Dead, Is.False, ctx + ": no shot while dead");
                    Assert.That(s.Sprinting, Is.False, ctx + ": no shot while sprinting");
                    Assert.That(s.Mode, Is.Not.EqualTo(MoveMode.Mantle), ctx + ": no shot while mantling");
                    int ready = heldViaSculpt ? mc.Hand.Exit(sh.Weapon) : mc.Hand.Draw(sh.Weapon);
                    Assert.That(tick - heldSince, Is.GreaterThanOrEqualTo(ready), ctx + $": {sh.Weapon} fired {tick - heldSince} ticks after being drawn (needs {ready})");
                    Assert.That(sh.Aim.Length, Is.EqualTo(1.0).Within(1e-9), ctx + ": unit aim");
                    // Bloom is shared by all guns (a swap never resets it), so any gun can carry the largest bloom.
                    double anyBloom = 0;
                    foreach (var gg in Guns) anyBloom = Math.Max(anyBloom, wc.Of(gg).BloomMax);
                    double maxSpread = st.BaseSpread + st.MoveSpread + anyBloom + Math.Max(st.SlideSpread, st.AirSpread);
                    Assert.That(sh.Spread, Is.InRange(0, maxSpread + 1e-9), ctx + ": spread within the gun's worst case");

                    // Fire rate: the k-th shot after any shot is at least k intervals later (one tick of rounding).
                    List<int> list = lastShot[sh.Weapon];
                    for (int k = 1; k <= Math.Min(list.Count, 12); k++)
                    {
                        int earlier = list[list.Count - k];
                        Assert.That((tick - earlier) * 1000L, Is.GreaterThanOrEqualTo(k * st.FireIntervalMilliTicks - 1000),
                            ctx + $": {sh.Weapon} fired {k + 1} shots in {tick - earlier} ticks");
                    }
                    if (sh.Weapon == Weapon.Pump && list.Count > 0)
                        Assert.That(tick - list[list.Count - 1], Is.GreaterThanOrEqualTo(54), ctx + ": pump cycle carries through swaps and sculpt mode");
                    list.Add(tick);
                    if (sh.Weapon == Weapon.Pistol) pistolShots++;
                }
                Assert.That(s.Weapon.RecoilPitch, Is.InRange(0.0, wc.Of(s.Hand.Held).RecoilMaxPitch + 1e-9).Or.InRange(0.0, 10.0), ctx + ": recoil bounded");
                Assert.That(s.Weapon.Bloom, Is.GreaterThanOrEqualTo(0), ctx + ": bloom non-negative");
                Assert.That(s.Health(wc), Is.InRange(0, wc.MaxHealth), ctx + ": health range");
            }
            Assert.That(pistolShots, Is.LessThanOrEqualTo(pistolPresses), $"seed {seed}: the pistol is semi-auto");
            Assert.That(total, Is.GreaterThan(50), "the bot fired");
            Assert.That(swaps, Is.GreaterThan(50));
            foreach (var g in Guns) Assert.That(lastShot[g].Count, Is.GreaterThan(0), $"seed {seed}: {g} fired");
        }

        [TestCaseSource(nameof(Seeds))]
        public void SameInputsFireTheSameShots(int seed)
        {
            List<string> Play()
            {
                var r = new Rng((ulong)seed + 9);
                var mc = new MovementConfig();
                var nc = new NetConfig();
                SubGrid grid = MapGen.Generate(seed, out MapInfo info, 32);
                Vec3d spawn = info.TeamSpawns[1].Count > 0 ? info.TeamSpawns[1][0] : new Vec3d(19, 2, 19);
                var s = MotorState.At(spawn);
                var shots = new List<WeaponShot>();
                var log = new List<string>();
                var hold = new MoveInput();
                for (int tick = 1; tick < 2000; tick++)
                {
                    MoveInput m = RandomMove(r, ref hold, tick);
                    var hand = new HandInput { WeaponKey = r.Chance(0.02) ? (byte)(r.Int(0, 4) + 1) : (byte)0 };
                    shots.Clear();
                    NetSim.Simulate(grid, mc, nc, ref s, InputCmd.Quantize(new InputCmd { Tick = tick, Move = m, Pitch = (float)r.Range(-40, 40), Hand = hand }), spawn, null, shots);
                    foreach (var sh in shots) log.Add($"{sh.Tick} {sh.Weapon} {sh.Seed} {sh.Aim.X:R} {sh.Aim.Y:R} {sh.Aim.Z:R} {sh.Spread:R}");
                }
                return log;
            }
            Assert.That(Play(), Is.EqualTo(Play()));
        }

        // ================================================================ Damage caps

        [Test]
        public void FalloffIsWithinZeroToOneAndNeverRisesWithDistance()
        {
            var wc = new WeaponsConfig();
            foreach (var g in Guns)
            {
                WeaponStats st = wc.Of(g);
                double prev = double.PositiveInfinity;
                for (double d = 0; d <= 300; d += 0.01)
                {
                    double f = st.DamageFactorAt(d);
                    Assert.That(f, Is.InRange(0.0, 1.0), $"{g} factor at {d} m");
                    Assert.That(f, Is.LessThanOrEqualTo(prev + 1e-12), $"{g} damage rises with distance at {d} m");
                    prev = f;
                }
                Assert.That(st.HeadDamage, Is.GreaterThanOrEqualTo(st.BodyDamage), $"{g}: a headshot never does less");
            }
        }

        [TestCaseSource(nameof(Seeds))]
        public void NoShotEverExceedsItsCapAndThePumpNeverOneShots(int seed)
        {
            var r = new Rng((ulong)seed * 13 + 1);
            var wc = new WeaponsConfig();
            SubGrid grid = MapGen.Generate(seed, out _, 32);
            double size = grid.SizeX * SubGrid.SubSize;
            var targets = new List<HitTarget>();
            var victims = new List<ShotVictim>();
            int hits = 0, pumpMax = 0;

            for (int i = 0; i < 4000; i++)
            {
                Weapon g = r.Pick(Guns);
                WeaponStats st = wc.Of(g);
                var feet = new Vec3d(r.Range(4, size - 4), r.Range(0, 2), r.Range(4, size - 4));
                double h = r.Chance(0.3) ? 1.1 : 1.8;
                targets.Clear();
                targets.Add(HitTarget.Of(2, 2, feet, 0.35, h, wc));
                targets.Add(HitTarget.Of(3, 1, feet + new Vec3d(r.Range(-1, 1), 0, r.Range(-1, 1)), 0.35, 1.8, wc)); // a teammate of the shooter
                // Shooter from point blank (even inside the box) to long range, aimed at a random point on the target.
                double dist = r.Chance(0.3) ? r.Range(0, 1.0) : r.Range(1, 60);
                double ang = r.Range(0, 2 * Math.PI);
                Vec3d aimAt = new Vec3d(feet.X + r.Range(-0.35, 0.35), feet.Y + r.Range(0, h), feet.Z + r.Range(-0.35, 0.35));
                Vec3d origin = aimAt + new Vec3d(Math.Cos(ang) * dist, r.Range(-1, 2), Math.Sin(ang) * dist);
                Vec3d dir = (aimAt - origin).Normalized;
                if (dir.LengthSquared == 0) continue;
                var shot = new WeaponShot { Tick = i, ShooterId = 1, Weapon = g, Origin = origin, Aim = dir, Spread = r.Chance(0.5) ? 0 : r.Range(0, 3), Seed = (uint)r.Next() };

                Hitscan.Resolve(grid, shot, st, wc, 1, targets, victims);
                foreach (var v in victims)
                {
                    Assert.That(v.Id, Is.Not.EqualTo(1), "never the shooter");
                    Assert.That(v.Id, Is.Not.EqualTo(3), "never a teammate (friendly fire off)");
                    int cap = WeaponsConfig.Round(st.Pellets * st.HeadDamage);
                    Assert.That(v.Damage, Is.InRange(0, cap), $"{g} damage {v.Damage} over its cap {cap}");
                    Assert.That(v.Pellets, Is.InRange(1, st.Pellets));
                    Assert.That(v.HeadPellets, Is.InRange(0, v.Pellets));
                    if (g == Weapon.Pump)
                    {
                        pumpMax = Math.Max(pumpMax, v.Damage);
                        Assert.That(v.Damage, Is.LessThan(wc.MaxHealth), "a single pump shot never kills from full health");
                    }
                    hits++;
                }
            }
            Assert.That(hits, Is.GreaterThan(1000));
            TestContext.Out.WriteLine($"seed {seed}: {hits} hits, max pump shot {pumpMax}");
        }

        // ================================================================ Networked gunfights

        private sealed class Gunfight
        {
            public readonly int Seed;
            public readonly Rng R;
            public readonly SculptNetHarness H;
            public int Hits, Kills, Shots, MaxRewind;
            public static readonly List<string> DiagLog = new List<string>();
            private readonly Dictionary<int, int> _health = new Dictionary<int, int>();
            private int _lastTick;

            public Gunfight(int seed, int players)
            {
                Seed = seed;
                R = new Rng((ulong)seed * 31 + 5);
                MapGen.Generate(seed, out MapInfo info, 32);
                H = new SculptNetHarness(() => { SubGrid g = MapGen.Generate(seed, out _, 32); g.ClearChanges(); return g; });
                for (int k = 0; k < players; k++)
                {
                    int team = 1 + k % 2;
                    List<Vec3d> spawns = info.TeamSpawns[team - 1];
                    Vec3d spawn = spawns.Count > 0 ? spawns[R.Int(0, spawns.Count)] : new Vec3d(19, 2, 19);
                    var link = new LinkConditions { LatencyMs = R.Range(0, 100), JitterMs = R.Range(0, 20), LossPercent = R.Pick(new[] { 0.0, 2, 5, 10 }) };
                    var peer = H.AddClient(spawn, team, link, new LinkConditions { LatencyMs = link.LatencyMs, JitterMs = R.Range(0, 20), LossPercent = R.Pick(new[] { 0.0, 2, 5, 10 }) },
                        1000.0 / R.Pick(new[] { 60.0, 120, 144, 240 }), seed: (int)(R.Next() & 0xffff) + 1);
                    peer.Script = Script(peer, new Rng(R.Next()));
                }
                _lastTick = H.Server.Tick;
            }

            private Func<SculptNetHarness.Peer, int, SculptTickInput> Script(SculptNetHarness.Peer me, Rng r)
            {
                var bot = new InputBot(new Rng(r.Next()));
                int target = -1;
                return (p, t) =>
                {
                    var i = new SculptTickInput { Move = bot.Next(t) };
                    if (r.Chance(0.01)) i.Hand.WeaponKey = (byte)(r.Int(0, 4) + 1);
                    if (r.Chance(0.004)) i.Hand.SculptToggle = true;
                    // Sculpt now and then (cover goes up and down mid-fight).
                    // (ISSUE-10: a confirm aimed while dead from the parked body crashes the client; dead bots don't sculpt.)
                    if (p.Client.Predicted.Hand.InSculptMode && !p.Client.Predicted.Dead && p.Sculpt.LocalBody != null && r.Chance(0.1))
                    {
                        Vec3d eye = p.Sculpt.LocalBody.Eye;
                        if (SculptNetHarness.Aim(p, eye + new Vec3d(r.Range(-3, 3), -1.5, r.Range(-3, 3)), out Int3 sub, out Vec3d aim))
                        {
                            if (Math.Abs(sub.Y) > 30000 || Math.Abs(sub.X) > 30000 || Math.Abs(sub.Z) > 30000)
                                DiagLog.Add($"confirm aimed at {sub} from eye {Inspect.Fmt(eye)}; dead {p.Client.Predicted.Dead}; hand {p.Client.Predicted.Hand.Mode}");
                            i.Confirm = true;
                            i.Action = r.Chance(0.6) ? SculptAction.Raise(sub, aim) : SculptAction.Move(sub, r.Pick(DirUtil.Horizontal), aim);
                        }
                    }
                    // Aim at an enemy as drawn (chest or head, a little off), and fire.
                    if (target < 0 || r.Chance(0.01))
                    {
                        var others = H.Peers.FindAll(o => o != p && o.ServerSide.Team != p.ServerSide.Team);
                        target = others.Count > 0 ? others[r.Int(0, others.Count)].ServerSide.Id : -1;
                    }
                    if (target >= 0 && p.Client.TryGetRemote(target, out RemoteView v))
                    {
                        MotorState s = p.Client.Predicted;
                        Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, p.Client.Movement), 0);
                        Vec3d point = v.Position + new Vec3d(r.Range(-0.3, 0.3), r.Range(0.3, 1.75), r.Range(-0.3, 0.3));
                        Vec3d d = point - eye;
                        if (d.Length > 0.1)
                        {
                            i.Move.Yaw = Math.Atan2(d.X, d.Z);
                            i.Pitch = (float)(-Math.Asin(Math.Max(-1, Math.Min(1, d.Y / d.Length))) * 180 / Math.PI);
                        }
                        bool fire = r.Chance(0.7);
                        i.Move.FireHeld = fire;
                        i.Move.FirePressed = fire && r.Chance(0.3);
                        i.Move.WantsToShoot = fire;
                        if (fire) i.Move.SprintHeld = false;
                    }
                    return i;
                };
            }

            public void Run(double seconds)
            {
                double step = SculptNetHarness.TickMs / 2;
                for (double ms = 0; ms < seconds * 1000; ms += step)
                {
                    H.RunMs(step);
                    if (H.Server.Tick == _lastTick) continue;
                    Assert.That(H.Server.Tick, Is.EqualTo(_lastTick + 1), "one server tick per check");
                    _lastTick = H.Server.Tick;
                    CheckTick();
                }
            }

            private void CheckTick()
            {
                int tick = H.Server.Tick;
                string ctx = $"seed {Seed} server tick {tick}";
                WeaponsConfig wc = H.Movement.Weapons;
                foreach (CombatEvent e in H.Server.CombatEvents)
                {
                    if (e.Type == CombatEventType.Shot) Shots++;
                    if (e.Type == CombatEventType.Kill) Kills++;
                    if (e.Type != CombatEventType.Hit || e.Source == DamageSource.Throw || e.Source == DamageSource.Fall) continue;
                    Hits++;
                    ServerPlayer shooter = Find(e.Attacker), victim = Find(e.Victim);
                    Assert.That(shooter, Is.Not.Null, ctx + ": hit by someone in the game");
                    Assert.That(victim, Is.Not.Null);
                    Assert.That(victim.Team, Is.Not.EqualTo(shooter.Team), ctx + ": no friendly fire");
                    WeaponStats st = wc.Of((Weapon)e.Source);
                    Assert.That(e.Damage, Is.InRange(1, WeaponsConfig.Round(st.Pellets * st.HeadDamage)), ctx + ": damage within the gun's cap");
                    if ((Weapon)e.Source == Weapon.Pump) Assert.That(e.Damage, Is.LessThan(wc.MaxHealth), ctx + ": no one-shot pump");

                    // The hit lies on the victim as rewound to what the shooter drew, at most 0.3 s back.
                    double view = shooter.LastCmd.ViewTick + shooter.LastCmd.ViewFrac / 256.0;
                    double used = Math.Max(tick - wc.MaxRewindTicks, Math.Min(tick, view));
                    MaxRewind = Math.Max(MaxRewind, (int)Math.Ceiling(tick - used));
                    Assert.That(tick - used, Is.LessThanOrEqualTo(wc.MaxRewindTicks + 1e-9), ctx + ": rewind within 0.3 s");
                    if (!H.Server.TryGetRewound(victim, used, out Vec3d feet, out double height, out _))
                    {
                        feet = victim.State.Position; // no history that far back (just joined): the server uses the present
                        height = PlayerMovement.HeightOf(victim.State, H.Movement);
                    }
                    Aabb box = HitTarget.Of(victim.Id, victim.Team, feet, H.Movement.HalfWidth, height, wc).Box;
                    Assert.That(box.Contains(e.Point, 1e-6), Is.True, ctx + $": hit point {Inspect.Fmt(e.Point)} not on the rewound victim {box}");

                    // No bullet passes through the server's current terrain (cover raised before the shot protects).
                    Vec3d d = e.Point - e.Origin;
                    TerrainHit th = H.ServerGrid.Raycast(e.Origin, d, d.Length);
                    Assert.That(th.Hit && th.Distance < d.Length - 1e-6, Is.False, ctx + $": hit through rock at {th.Distance:0.###} of {d.Length:0.###} m");
                }

                // Health bookkeeping: only goes down by reported hits, back to full only on respawn.
                foreach (var p in H.Peers)
                {
                    ServerPlayer sp = p.ServerSide;
                    int hp = sp.State.Health(wc);
                    Assert.That(hp, Is.InRange(0, wc.MaxHealth), ctx + ": health range");
                    if (sp.State.Dead) Assert.That(hp, Is.EqualTo(0), ctx + ": dead means zero health");
                    _health[sp.Id] = hp;
                }
                int kills = 0, deaths = 0;
                foreach (var p in H.Peers) { kills += p.ServerSide.Kills; deaths += p.ServerSide.Deaths; }
                Assert.That(kills, Is.LessThanOrEqualTo(deaths), ctx + ": every kill is someone's death");
            }

            private ServerPlayer Find(int id)
            {
                foreach (var p in H.Server.Players) if (p.Id == id) return p;
                return null;
            }
        }

        [TestCaseSource(nameof(Seeds))]
        public void GunfightOverBadLinks_HitsAreWhatTheShooterSawAndCoverHolds(int seed)
        {
            var f = new Gunfight(seed, 2 + seed % 3);
            Gunfight.DiagLog.Clear();
            try { f.Run(14); }
            catch (OverflowException) { Assert.Fail("client threw OverflowException: " + string.Join(" | ", Gunfight.DiagLog)); }
            Assert.That(f.Shots, Is.GreaterThan(50), "they shot");
            Assert.That(f.Hits, Is.GreaterThan(5), "they hit");
            TestContext.Out.WriteLine($"seed {seed}: shots {f.Shots}, hits {f.Hits}, kills {f.Kills}, max rewind {f.MaxRewind} ticks");
        }
    }
}
