using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>The practice dummy (step 7): heals its room's broken tiles after a delay, through the normal rules.</summary>
    public class DummyTests
    {
        private static readonly Vec3d Post = new Vec3d(9.15, 0, 9.15);

        /// <summary>A sealed 4 x 4 tile room (inside x and z 6.3..12.3), three tiles high. Returns its tile ids (south wall first).</summary>
        private static int[] Room(BrushWorld w)
        {
            TileWalls t = w.Tiles;
            var ids = new List<int>();
            ids.AddRange(t.AddWall(new Vec3d(6.3, 0, 6.0), Dir.East, 4, 3));   // south, z 6.0..6.3
            ids.AddRange(t.AddWall(new Vec3d(6.3, 0, 12.3), Dir.East, 4, 3));  // north
            ids.AddRange(t.AddWall(new Vec3d(6.0, 0, 6.3), Dir.North, 4, 3));  // west
            ids.AddRange(t.AddWall(new Vec3d(12.3, 0, 6.3), Dir.North, 4, 3)); // east
            t.Changes.Clear();
            return ids.ToArray();
        }

        private static SculptNetHarness Arena(out int[] room)
        {
            int[] ids = null;
            var h = new SculptNetHarness(() =>
            {
                var g = new SubGrid(8, 4, 8, -16);
                g.FillBaseGround();
                g.ClearChanges();
                return g;
            }, tweak: s => new BreachConfig().ApplyBarTo(s), wrap: g =>
            {
                var w = new BrushWorld();
                ids = Room(w);
                return new CompositeTerrain(g, w);
            });
            room = ids;
            return h;
        }

        /// <summary>The south wall's bottom row, second tile: x 7.8..9.3, z 6.0..6.3, y 0..1.5.</summary>
        private const int SouthTile = 1;

        [Test]
        public void HealsABrokenTileAfterTheDelay_NotBefore()
        {
            var h = Arena(out int[] room);
            h.Server.Walls.Config.BlastHoles = false; // one whole tile, to time the dummy exactly
            var watcher = h.AddClient(new Vec3d(9, 0, 2), team: 1);
            ServerPlayer d = h.Server.AddDummy(new PracticeDummy(Post, 0, room), team: 2);
            h.RunSeconds(0.5);
            h.Server.Walls.Damage(room[SouthTile], 100, TileCause.Bullet, watcher.ServerSide.Id);
            h.RunSeconds(2.8);
            Assert.That(h.Server.Walls[room[SouthTile]].State, Is.EqualTo(TileState.Broken), "still open during the 3 s delay");
            Assert.That(h.Server.Healing.Count, Is.Zero);
            h.RunSeconds(0.5);
            Assert.That(h.Server.Walls[room[SouthTile]].State, Is.EqualTo(TileState.Patched), "then it heals it (one press, 0.2 s, like anyone)");
            Assert.That(d.State.Hand.InSculptMode, Is.False, "no heal mode any more");
            h.RunSeconds(1.0);
            Assert.That(h.Server.Walls[room[SouthTile]].State, Is.EqualTo(TileState.Patched));
            Assert.That(watcher.Client.Walls[room[SouthTile]].State, Is.EqualTo(TileState.Patched), "players see it");
            Assert.That(h.Server.BarOf(d.Id).Value, Is.LessThan(SculptConfig.BarMax), "it pays bar like anyone");
        }

        [Test]
        public void StandingInTheHoleStopsItsHeal_ItTriesAgainOnceYouLeave()
        {
            var h = Arena(out int[] room);
            var runner = h.AddClient(new Vec3d(8.55, 0, 4), team: 1);
            ServerPlayer d = h.Server.AddDummy(new PracticeDummy(Post, 0, room), team: 2);
            h.RunSeconds(0.3);
            h.Server.Walls.Damage(room[SouthTile], 100, TileCause.Bullet);
            h.Server.Walls.Damage(room[SouthTile + 4], 100, TileCause.Bullet); // the tile above too: a standing-size hole
            // Walk into the hole (x 7.8..9.3 at z 6.0..6.3) and stand there.
            runner.Script = (p, t) => new SculptTickInput { Move = new MoveInput { Yaw = 0, MoveZ = p.Client.Predicted.Position.Z < 6.15 ? 0.4 : 0 } };
            h.RunSeconds(5.0);
            Assert.That(runner.ServerSide.State.Position.Z, Is.InRange(5.7, 6.6), "the runner stands in the hole");
            Assert.That(h.Server.Walls[room[SouthTile]].State, Is.EqualTo(TileState.Broken), "a heal never closes on a player");
            Assert.That(runner.Client.HealEvents.Exists(e => e.Type == HealEventType.Cancelled && e.Result == HealResult.PlayerInside), Is.True);
            runner.Script = (p, t) => new SculptTickInput { Move = new MoveInput { Yaw = Math.PI, MoveZ = 1 } }; // back out south
            h.RunSeconds(3.5);
            Assert.That(h.Server.Walls[room[SouthTile]].State, Is.EqualTo(TileState.Patched), "healed once the hole is clear");
            Assert.That(h.Server.Walls[room[SouthTile + 4]].State, Is.EqualTo(TileState.Patched), "both, one at a time");
        }

        [Test]
        public void ItCanBeHurtAndKnockedAbout_AndWalksBackToItsPost()
        {
            var h = Arena(out int[] room);
            var a = h.AddClient(new Vec3d(9.15, 0, 7.8), team: 1); // inside the room, south of the dummy, facing it
            ServerPlayer d = h.Server.AddDummy(new PracticeDummy(Post, Math.PI, room), team: 2);
            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                i.Hand.Punch = t == start + 30;
                return i;
            };
            h.RunSeconds(1.0);
            Assert.That(d.State.Damage, Is.EqualTo(30), "a normal player to punches");
            // Shove it off its post (as a blast would).
            GrenadeRules.Knock(ref d.State, new Vec3d(7, 5, 0)); // a hard shove with lift (on the ground friction eats a small one at once)
            double most = 0;
            for (int k = 0; k < 30; k++) { h.RunMs(20); most = Math.Max(most, Math.Abs(d.State.Position.X - Post.X)); }
            Assert.That(most, Is.GreaterThan(0.6), "knocked aside");
            h.RunSeconds(3.0);
            Assert.That(new Vec3d(d.State.Position.X - Post.X, 0, d.State.Position.Z - Post.Z).Length, Is.LessThan(0.6), "back at its post");
        }

        [Test]
        public void ADummyNeverTakesTheOfflineLocalConnection()
        {
            // The Game's offline/host player sits on connection -1000; adding a dummy first used to grab it.
            var w = new BrushWorld();
            int[] room = Room(w);
            var server = new NetServer(new CompositeTerrain(BuildGround(), w), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            ServerPlayer dummy = server.AddDummy(new PracticeDummy(Post, 0, room), team: 2);
            Assert.That(dummy.ConnectionId, Is.Not.EqualTo(-1000));
            Assert.DoesNotThrow(() => server.AddPlayer(-1000, 1, new Vec3d(9, 0, 2), 0));
            Assert.That(NetServer.IsBotConnection(-1000), Is.False, "the local player is not a bot");
        }

        [Test]
        public void NothingIsEverSentToADummy_AndClientsSeeItAsAPlayer()
        {
            var w = new BrushWorld();
            int[] room = Room(w);
            var sentTo = new List<int>();
            var server = new NetServer(new CompositeTerrain(BuildGround(), w), new MovementConfig(), new NetConfig(), (c, d, r) => sentTo.Add(c));
            ServerPlayer dummy = server.AddDummy(new PracticeDummy(Post, 0, room), team: 2);
            server.AddPlayer(0, 1, new Vec3d(9, 0, 2), 0);
            for (int k = 0; k < 120; k++) server.Step();
            Assert.That(sentTo.TrueForAll(c => !NetServer.IsBotConnection(c)), Is.True);
            Assert.That(sentTo.Contains(0), Is.True);
            Assert.That(dummy.MissedInputs, Is.Zero, "it gets a command every tick");

            var h = Arena(out int[] room2);
            var watcher = h.AddClient(new Vec3d(9, 0, 2), team: 1);
            ServerPlayer d2 = h.Server.AddDummy(new PracticeDummy(Post, 0, room2), team: 2);
            h.RunSeconds(0.5);
            Assert.That(watcher.Client.TryGetRemote(d2.Id, out RemoteView v), Is.True);
            Assert.That(v.Team, Is.EqualTo(2));
            Assert.That(v.Position.X, Is.EqualTo(Post.X).Within(0.05));
        }

        [Test]
        public void OnTheBreachMap_TheDummyInTheSealedRoomHealsEveryWallYouBreak()
        {
            BrushWorld w = BreachMap.BuildBrushes(out BreachMapInfo info);
            w.Tiles.Config.BlastHoles = false; // whole tiles, so the dummy's bar covers them all in time
            var server = new NetServer(BreachMap.Terrain(BreachMap.BuildGround(), w), new MovementConfig(), new NetConfig(), (c, d, r) => { });
            server.AddDummy(new PracticeDummy(info.RoomCentre, Math.PI, info.Room), team: 2);
            for (int k = 0; k < 30; k++) server.Step();
            // One tile in each wall, bottom and top rows.
            int[] hits = { info.Room[1], info.Room[12 + 9], info.Room[24 + 2], info.Room[36 + 10] };
            foreach (int t in hits) w.Tiles.Damage(t, 1000, TileCause.Grenade);
            for (int k = 0; k < 60 * 9; k++) server.Step();
            foreach (int t in hits) Assert.That(w.Tiles[t].State, Is.EqualTo(TileState.Patched), $"tile {t}");
        }

        private static SubGrid BuildGround()
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }

        [Test]
        public void OnlyWatchedTilesCount_AndDamagedOnesAreLeftForChargingUnlessAsked()
        {
            var w = new BrushWorld();
            int[] room = Room(w);
            int[] outside = w.Tiles.AddWall(new Vec3d(6.3, 0, 3), Dir.East, 2, 1);
            var heal = new TileHealing(w.Tiles);
            var brain = new PracticeDummy(Post, 0, room);
            var mc = new MovementConfig();
            MotorState me = MotorState.At(Post);
            w.Tiles.Damage(outside[0], 100, TileCause.Bullet);
            w.Tiles.Damage(room[0], 60, TileCause.Bullet); // cracked, not broken
            brain.Next(10, me, w.Tiles, heal, 1, mc);
            Assert.That(brain.NeedsHealSince(outside[0]), Is.EqualTo(int.MinValue), "not its room");
            Assert.That(brain.NeedsHealSince(room[0]), Is.EqualTo(int.MinValue), "cracks are left for charging");
            var keen = new PracticeDummy(Post, 0, room, new DummyConfig { HealDamaged = true });
            keen.Next(10, me, w.Tiles, heal, 1, mc);
            Assert.That(keen.NeedsHealSince(room[0]), Is.EqualTo(10));
        }
    }
}
