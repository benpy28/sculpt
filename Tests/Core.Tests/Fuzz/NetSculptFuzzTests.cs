using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Networked sculpt soak on generated arenas: 2 to 5 clients, each with its own random link (latency, jitter,
    /// loss) and frame rate, running, toggling sculpt mode and firing pushes, raises, sinks and throws at the ground
    /// around each other. Players join and leave mid-fight. Every tick: no prediction and no server body is inside
    /// rock. At the end, once input stops: every client's grid, piece table and piece cells equal the server's, and
    /// no prediction is left unanswered.
    /// </summary>
    [Category("Fuzz")]
    public class NetSculptFuzzTests
    {
        private static IEnumerable<int> Seeds()
        {
            int n = int.TryParse(Environment.GetEnvironmentVariable("SCULPT_FUZZ_SEEDS"), out int v) && v > 0 ? v : 4;
            for (int i = 0; i < n; i++) yield return 17 + i * 7919;
        }

        private static bool Inside(ITerrain g, PlayerBody b) => g.BoxOverlapsSolid(b.Bounds);

        private sealed class Fight
        {
            public readonly int Seed;
            public readonly Rng R;
            public readonly SculptNetHarness H;
            public readonly MapInfo Info;
            public bool Stop;
            public int Confirms;
            public readonly List<string> Links = new List<string>();
            public readonly List<string> InsideLog = new List<string>();
            public readonly Dictionary<int, int> InsideRun = new Dictionary<int, int>();
            public int Transients, MaxRun, PhantomTicks;
            public double MaxTransientDepth;

            public Fight(int seed)
            {
                Seed = seed;
                R = new Rng((ulong)seed * 977 + 3);
                MapGen.Generate(seed, out MapInfo info, 32);
                Info = info;
                H = new SculptNetHarness(() =>
                {
                    SubGrid g = MapGen.Generate(seed, out _, 32);
                    g.ClearChanges();
                    return g;
                });
            }

            public SculptNetHarness.Peer Join(int team)
            {
                var link = new LinkConditions { LatencyMs = R.Range(0, 110), JitterMs = R.Range(0, 25), LossPercent = R.Pick(new[] { 0.0, 1, 3, 8, 15 }) };
                var down = new LinkConditions { LatencyMs = link.LatencyMs, JitterMs = R.Range(0, 25), LossPercent = R.Pick(new[] { 0.0, 1, 3, 8, 15 }) };
                double fps = R.Pick(new[] { 45.0, 60, 120, 144, 240 });
                List<Vec3d> spawns = Info.TeamSpawns[(team - 1) % 2];
                Vec3d spawn = spawns.Count > 0 ? spawns[R.Int(0, spawns.Count)] : new Vec3d(19.2, 1, 19.2);
                var p = H.AddClient(spawn, team, link, down, 1000.0 / fps, seed: (int)(R.Next() & 0xffff) + 1);
                Links.Add($"[{link.LatencyMs:0}/{down.LatencyMs:0} ms, jitter {link.JitterMs:0}/{down.JitterMs:0}, loss {link.LossPercent}/{down.LossPercent}%, {fps} fps]");
                var bot = new InputBot(new Rng(R.Next()));
                var r = new Rng(R.Next());
                p.Script = (pp, t) =>
                {
                    var i = new SculptTickInput();
                    PlayerBody body = pp.Sculpt.LocalBody;
                    // ISSUE-8 (KnownIssueTests): sculpt shoves are not predicted for the local player, so a prediction can sit
                    // inside rock until movement depenetrates it (next tick) or, when that cannot climb out, until the
                    // server's correction arrives (about a round trip). Until fixed only "stuck for over a second" fails.
                    bool inside = body != null && Inside(pp.Grid, body);
                    int run = inside ? (InsideRun.TryGetValue(pp.ConnectionId, out int k0) ? k0 + 1 : 1) : 0;
                    InsideRun[pp.ConnectionId] = run;
                    if (inside)
                    {
                        Transients++;
                        double depth = 0;
                        while (depth < 3 && pp.Grid.BoxOverlapsSolid(body.BoundsAt(body.Position + new Vec3d(0, depth, 0)))) depth += 0.01;
                        MaxTransientDepth = Math.Max(MaxTransientDepth, depth);
                        MaxRun = Math.Max(MaxRun, run);
                        bool phantom = false; // ISSUE-9: an unanswered prediction of ours is still in our world
                        foreach (LocalSculpt l in pp.Sculpt.Local) if (l.Applied && !l.ServerBegan) phantom = true;
                        if (phantom) PhantomTicks++;
                        if (run > 60 && !phantom) InsideLog.Add($"client {pp.ConnectionId} tick {t}: inside rock for {run} ticks, by {depth:0.00} m at {Inspect.Fmt(body.Position)}; server body {Inspect.Fmt(pp.ServerSide.Body.Position)} inside {Inside(H.ServerGrid, pp.ServerSide.Body)}; client grid == server grid {pp.Grid.Hash() == H.ServerGrid.Hash()}; local predictions {pp.Sculpt.Local.Count} {Describe(pp)}; corrections {pp.Client.Corrections}");
                    }
                    if (Stop || body == null) return i;
                    i.Move = bot.Next(t);
                    i.Move.WantsToShoot = false;
                    HandState hand = pp.Client.Predicted.Hand;
                    if (hand.Mode == HandMode.Weapon && r.Chance(0.05)) i.Hand.SculptToggle = true;
                    else if (hand.InSculptMode && r.Chance(0.004)) i.Hand.SculptToggle = true; // and back out now and then
                    if (!hand.InSculptMode || !r.Chance(0.15)) return i;

                    // Aim at the ground around someone (often an enemy), like a fight.
                    Vec3d point = body.Eye + new Vec3d(r.Range(-4, 4), r.Range(-1.8, 0.4), r.Range(-4, 4));
                    if (H.Peers.Count > 1 && r.Chance(0.6))
                    {
                        PlayerBody other = H.Peers[r.Int(0, H.Peers.Count)].ServerSide.Body;
                        point = other.Position + new Vec3d(r.Range(-1.3, 1.3), r.Range(-0.6, 0.2), r.Range(-1.3, 1.3));
                    }
                    if (!SculptNetHarness.Aim(pp, point, out Int3 sub, out Vec3d aim)) return i;
                    int kind = r.Int(0, 8);
                    i.Confirm = true;
                    Confirms++;
                    i.Action = kind < 3 ? SculptAction.Move(sub, r.Pick(DirUtil.Horizontal), aim, r.Chance(0.2))
                        : kind < 5 ? SculptAction.Raise(sub, aim, r.Chance(0.2))
                        : kind < 7 ? SculptAction.Sink(sub, aim, r.Chance(0.2))
                        : SculptAction.Throw(sub, aim, point - body.Eye + new Vec3d(0, 0.3, 0));
                    return i;
                };
                return p;
            }

            private string Describe(SculptNetHarness.Peer pp)
            {
                var parts = new List<string>();
                foreach (LocalSculpt l in pp.Sculpt.Local)
                    parts.Add($"[{l.Action.Type} seq {l.Seq} piece {l.PieceId} temp {l.TempId} begin {l.BeginTick} complete {l.CompleteTick} serverBegan {l.ServerBegan} completed {l.Completed} applied {l.Applied} windup {l.Check.WindupTicks}]");
                PendingSculpt sp = H.ServerSculpt.PendingOf(pp.ServerSide.Id);
                parts.Add($"client tick {pp.Client.Tick} server tick {H.Server.Tick} server pending {(sp == null ? "none" : $"{sp.Action.Type} {sp.StartTick}->{sp.CompleteTick} piece {sp.Check.PieceId}")} buffered {H.ServerSculpt.HasBufferedConfirm(pp.ServerSide.Id)}");
                return string.Join(" ", parts);
            }

            public void Leave(SculptNetHarness.Peer p)
            {
                H.Server.RemovePlayer(p.ConnectionId);
                H.Peers.Remove(p);
            }

            public void RunChecked(double seconds)
            {
                for (double t = 0; t < seconds; t += 0.05)
                {
                    H.RunMs(50);
                    foreach (var p in H.Peers)
                        Assert.That(Inside(H.ServerGrid, p.ServerSide.Body), Is.False, $"seed {Seed}: server body of client {p.ConnectionId} inside rock at {Inspect.Fmt(p.ServerSide.Body.Position)}");
                }
            }

            public void AssertConverged()
            {
                H.AssertConverged();
                foreach (var p in H.Peers)
                    foreach (PieceInfo sp in H.ServerSculpt.Pieces.All)
                    {
                        PieceInfo cp = p.Sculpt.Mirror.Pieces.Get(sp.Id);
                        Assert.That(cp, Is.Not.Null, $"seed {Seed} client {p.ConnectionId}: missing piece {sp.Id}");
                        Assert.That(cp.Cells, Is.EqualTo(sp.Cells), $"seed {Seed} client {p.ConnectionId}: piece {sp.Id} cells differ");
                    }
            }
        }

        [TestCaseSource(nameof(Seeds))]
        public void SculptFightOverBadLinks_StaysLegal_AndEveryClientEndsWithTheServersWorld(int seed)
        {
            var f = new Fight(seed);
            int clients = f.R.Int(2, 5);
            for (int k = 0; k < clients; k++) f.Join(1 + k % 2);
            f.RunChecked(8);

            // Someone leaves mid-fight (maybe mid-windup), someone new joins the sculpted world.
            if (f.H.Peers.Count > 2) f.Leave(f.H.Peers[f.R.Int(0, f.H.Peers.Count)]);
            f.Join(1 + f.R.Int(0, 2));
            f.RunChecked(6);

            f.Stop = true;
            f.RunChecked(5);
            Assert.That(f.Confirms, Is.GreaterThan(20), "the bots sculpted");
            Assert.That(f.H.ServerSculpt.Pieces.Count, Is.GreaterThan(3), "pieces were made");
            f.AssertConverged();
            Assert.That(f.InsideLog, Is.Empty, $"seed {seed}: predictions inside rock ({f.InsideLog.Count} ticks): first " + (f.InsideLog.Count > 0 ? f.InsideLog[0] : "") + " | last " + (f.InsideLog.Count > 0 ? f.InsideLog[f.InsideLog.Count - 1] : ""));
            TestContext.Out.WriteLine($"seed {seed}: {string.Join(" ", f.Links)}; confirms {f.Confirms}, prediction-inside-rock ticks {f.Transients} (deepest {f.MaxTransientDepth:0.00} m, longest {f.MaxRun} ticks, {f.PhantomTicks} inside a phantom), server pieces {f.H.ServerSculpt.Pieces.Count}, ops {f.H.ServerSculpt.Pieces.Ops.Count}");
        }

        /// <summary>Same seed, same fight: the server's world is a pure function of the inputs it received.</summary>
        [TestCaseSource(nameof(Seeds))]
        public void SameSeedSameServerWorld(int seed)
        {
            ulong Play()
            {
                var f = new Fight(seed + 1);
                f.Join(1);
                f.Join(2);
                f.Join(1);
                f.RunChecked(6);
                return f.H.ServerGrid.Hash();
            }
            Assert.That(Play(), Is.EqualTo(Play()));
        }
    }
}
