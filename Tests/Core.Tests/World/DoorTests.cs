using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests.World
{
    /// <summary>Doors (Ben 2026-11-25: "they usually just get broken instantly"): an easy way in that you still have to break.</summary>
    public class DoorTests
    {
        private static readonly MovementConfig Mc = new MovementConfig();
        private static readonly NetConfig Nc = new NetConfig();

        /// <summary>Flat ground with a 4-tile wall at z 12 (x 6..12) whose middle two columns are a shut door (x 7.5..10.5, 3 m tall).</summary>
        private static CompositeTerrain Arena(out TileWalls walls, out int left, out int right)
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            var w = new BrushWorld();
            double s = w.Tiles.Config.TileSize, th = w.Tiles.Config.TileThickness;
            w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 1, 3);
            w.Tiles.AddWall(new Vec3d(10.5, 0, 12), Dir.East, 1, 3);
            w.Tiles.AddWall(new Vec3d(7.5, 3, 12), Dir.East, 2, 1);
            left = w.Tiles.AddTile(new Aabb(new Vec3d(7.5, 0, 12), new Vec3d(9, 2 * s, 12 + th)), Dir.North, TileKind.Door);
            right = w.Tiles.AddTile(new Aabb(new Vec3d(9, 0, 12), new Vec3d(10.5, 2 * s, 12 + th)), Dir.North, TileKind.Door);
            w.Tiles.Changes.Clear();
            walls = w.Tiles;
            return new CompositeTerrain(g, w);
        }

        [Test]
        public void ADoorLeaf_GoesWithOnePunchOrOneArBullet_AWallTileDoesNot()
        {
            Arena(out TileWalls w, out int left, out int right);
            BreachConfig c = w.Config;
            Assert.That(w.Damage(left, c.PunchTileDamage, TileCause.Punch), Is.True, "one punch");
            // Walls by cell made a wall bullet worth half a tile (AR 50), so at 4x any one bullet breaks a door leaf.
            Assert.That(w.Damage(right, c.ArTileDamage, TileCause.Bullet), Is.True, "one AR bullet");
            Assert.That(w[left].State, Is.EqualTo(TileState.Broken));
            Assert.That(w[left].Kind, Is.EqualTo(TileKind.Door));

            int wall = 0; // the wall tile beside the door
            Assert.That(w.Damage(wall, c.PunchTileDamage, TileCause.Punch), Is.False, "a wall tile takes two punches");
        }

        [Test]
        public void ABrokenDoor_IsAStandingSizedOpening()
        {
            CompositeTerrain t = Arena(out TileWalls w, out int left, out _);
            w.Set(left, TileState.Broken, 0, TileCause.Punch);
            var player = new Aabb(new Vec3d(8.25 - Mc.HalfWidth, 0.01, 11.8), new Vec3d(8.25 + Mc.HalfWidth, Mc.StandHeight, 12.5));
            Assert.That(t.BoxOverlapsSolid(player), Is.False);
        }

        [Test]
        public void ASprintCharge_GoesThroughAnUncrackedDoor()
        {
            CompositeTerrain t = Arena(out TileWalls w, out int left, out int right);
            var s = MotorState.At(new Vec3d(9, 0, 2));
            for (int k = 1; k <= 30 * SimTime.Per60; k++) { s.Tick = k - 1; PlayerMovement.Step(t, Mc, ref s, default, null); }
            var breaker = new ServerChargeBreaker { By = 1 };
            int start = s.Tick + 1;
            for (int k = 0; k < 150 * SimTime.Per60; k++)
            {
                InputCmd cmd = InputCmd.Quantize(new InputCmd { Move = new MoveInput { MoveZ = 1, Yaw = 0, SprintHeld = true } });
                cmd.Tick = start + k;
                NetSim.Simulate(t, Mc, Nc, ref s, cmd, s.Position, null, null, null, null, breaker);
            }
            Assert.That(w[left].State, Is.EqualTo(TileState.Broken));
            Assert.That(w[right].State, Is.EqualTo(TileState.Broken));
            Assert.That(s.Position.Z, Is.GreaterThan(13), "ran on through");
        }
    
        // ---------------------------------------------------------------- Opening and shutting (Ben 2026-11-25)

        [Test]
        public void OpenAndShut_KeepsHealth_OpenIsNotSolid_AndCantBeHurt()
        {
            CompositeTerrain t = Arena(out TileWalls w, out int left, out int right);
            w.PairDoor(left, right);
            var leaves = new List<int>();
            w.Damage(left, 5, TileCause.Bullet); // a scratch: 80 left at 4x
            int hp = w[left].Health;
            Assert.That(DoorRules.Set(w, left, true, TileCause.Door, 1, leaves), Is.EqualTo(2), "both leaves of the door");
            Assert.That(w[right].State, Is.EqualTo(TileState.DoorOpen));
            var player = new Aabb(new Vec3d(9 - Mc.HalfWidth, 0.01, 11.8), new Vec3d(9 + Mc.HalfWidth, Mc.StandHeight, 12.5));
            Assert.That(t.BoxOverlapsSolid(player), Is.False, "walk through an open door");
            Assert.That(t.Raycast(new Vec3d(9, 1.5, 10), new Vec3d(0, 0, 1), 4).Hit, Is.False, "shoot through it");
            Assert.That(w.Damage(left, 500, TileCause.Grenade), Is.False, "an open door takes no damage");
            Assert.That(new TileHealing(w).WhatCheck(left, 0, HealKind.Reinforce), Is.Not.EqualTo(HealResult.Ok), "nor a heal");
            DoorRules.Set(w, right, false, TileCause.Door, 1, leaves);
            Assert.That(w[left].State, Is.Not.EqualTo(TileState.DoorOpen));
            Assert.That(w[left].Health, Is.EqualTo(hp), "health kept");
            Assert.That(t.BoxOverlapsSolid(player), Is.True);
        }

        [Test]
        public void ShuttingIsRefused_WhileSomeoneStandsInTheDoorway()
        {
            Arena(out TileWalls w, out int left, out int right);
            w.PairDoor(left, right);
            var leaves = new List<int>();
            DoorRules.Set(w, left, true, TileCause.Door, 1, leaves);
            var inside = new List<Aabb> { new Aabb(new Vec3d(9.6, 0, 11.9), new Vec3d(10.3, 1.8, 12.6)) };
            Assert.That(DoorRules.CanSet(w, left, false, inside, leaves), Is.False);
            Assert.That(DoorRules.CanSet(w, left, false, new List<Aabb>(), leaves), Is.True);
            Assert.That(DoorRules.CanSet(w, left, true, new List<Aabb>(), leaves), Is.False, "already open: nothing to do");
        }

        [Test]
        public void ASprint_ThroughAnOpenDoor_DoesNotBreakIt()
        {
            CompositeTerrain t = Arena(out TileWalls w, out int left, out int right);
            w.PairDoor(left, right);
            DoorRules.Set(w, left, true, TileCause.Door, 1, new List<int>());
            var s = MotorState.At(new Vec3d(9, 0, 2));
            for (int k = 1; k <= 30 * SimTime.Per60; k++) { s.Tick = k - 1; PlayerMovement.Step(t, Mc, ref s, default, null); }
            var breaker = new ServerChargeBreaker { By = 1 };
            int start = s.Tick + 1;
            for (int k = 0; k < 150 * SimTime.Per60; k++)
            {
                InputCmd cmd = InputCmd.Quantize(new InputCmd { Move = new MoveInput { MoveZ = 1, Yaw = 0, SprintHeld = true } });
                cmd.Tick = start + k;
                NetSim.Simulate(t, Mc, Nc, ref s, cmd, s.Position, null, null, null, null, breaker);
            }
            Assert.That(s.Position.Z, Is.GreaterThan(13), "ran on through");
            Assert.That(w[left].State, Is.EqualTo(TileState.DoorOpen));
            Assert.That(w[right].State, Is.EqualTo(TileState.DoorOpen));
        }

        // ---------------------------------------------------------------- Networked, on the duel map

        private static SculptNetHarness Duel(out BreachMapInfo info)
        {
            DuelMap.BuildBrushes(out info);
            return new SculptNetHarness(DuelMap.BuildGround, wrap: g => DuelMap.Terrain(g, DuelMap.BuildBrushes(out _)));
        }

        /// <summary>A shut double door of the duel map that faces north (thin in Z), and a spot 1.3 m south of it.</summary>
        private static int NorthDoor(TileWalls w, GreyboxInfo b, out Vec3d front)
        {
            foreach (int d in b.Doors)
            {
                if (w[d].Facing != Dir.North || w.DoorPartner(d) < 0) continue;
                Aabb a = w.BoundsOf(d), p = w.BoundsOf(w.DoorPartner(d));
                double x = (Math.Min(a.Min.X, p.Min.X) + Math.Max(a.Max.X, p.Max.X)) / 2;
                front = new Vec3d(x, 0, a.Min.Z - 1.3);
                return d;
            }
            throw new InvalidOperationException("no north-facing door");
        }

        [Test]
        public void YourPressOpensTheDoorOnTheSameTick_TheServerAgrees_YouWalkThroughWithNoCorrection([Values(0, 100)] int pingMs)
        {
            SculptNetHarness h = Duel(out BreachMapInfo info);
            int door = NorthDoor(h.Server.Walls, info.Block, out Vec3d front);
            var lag = SculptNetHarness.Lag(pingMs / 2.0);
            var a = h.AddClient(front, team: 1, up: lag, down: lag);
            var watcher = h.AddClient(front + new Vec3d(0, 0, -6), team: 2, seed: 2);
            h.RunSeconds(1);
            int pressTick = -1;
            a.Script = (p, t) =>
            {
                var input = new SculptTickInput { Move = new MoveInput { Yaw = 0 } };
                if (pressTick < 0) { pressTick = t; input.Hand = new HandInput { Door = true, DoorTile = door, DoorOpen = true }; }
                else if (t > pressTick + 2) input.Move = new MoveInput { Yaw = 0, MoveZ = 1 };
                return input;
            };
            int correctionsBefore = a.Client.Corrections;
            h.RunMs(SculptNetHarness.TickMs * 2);
            Assert.That(a.Client.Walls[door].State, Is.EqualTo(TileState.DoorOpen), "open in your view at once");
            h.RunSeconds(1.5);
            Assert.That(h.Server.Walls[door].State, Is.EqualTo(TileState.DoorOpen), "the server opened it");
            Assert.That(h.Server.Walls[h.Server.Walls.DoorPartner(door)].State, Is.EqualTo(TileState.DoorOpen), "both leaves");
            Assert.That(watcher.Client.Walls[door].State, Is.EqualTo(TileState.DoorOpen), "everyone sees it");
            Assert.That(a.ServerSide.State.Position.Z, Is.GreaterThan(h.Server.Walls.BoundsOf(door).Max.Z + 0.5), "walked through");
            Assert.That(a.Client.Corrections, Is.EqualTo(correctionsBefore), "no correction");
            Assert.That(a.Client.Doors.PendingCount, Is.Zero);
        }

        [Test]
        public void ARefusedShut_PutsTheDoorBackInYourView()
        {
            SculptNetHarness h = Duel(out BreachMapInfo info);
            int door = NorthDoor(h.Server.Walls, info.Block, out Vec3d front);
            DoorRules.Set(h.Server.Walls, door, true, TileCause.Door, 0, new List<int>());
            var lag = SculptNetHarness.Lag(50);
            var a = h.AddClient(front, team: 1, up: lag, down: lag);
            // Someone standing in the doorway, across both leaves.
            Aabb d = h.Server.Walls.BoundsOf(door);
            var blocker = h.AddClient(new Vec3d(front.X, 0, (d.Min.Z + d.Max.Z) / 2), team: 2, seed: 3);
            h.RunSeconds(1);
            Assert.That(a.Client.Walls[door].State, Is.EqualTo(TileState.DoorOpen), "a late joiner gets the open door");
            int pressTick = -1;
            a.Script = (p, t) =>
            {
                if (pressTick >= 0) return default;
                pressTick = t;
                return new SculptTickInput { Hand = new HandInput { Door = true, DoorTile = door, DoorOpen = false } };
            };
            h.RunMs(SculptNetHarness.TickMs * 2);
            Assert.That(a.Client.Walls[door].State, Is.Not.EqualTo(TileState.DoorOpen), "shut in your view at once");
            h.RunSeconds(2);
            Assert.That(h.Server.Walls[door].State, Is.EqualTo(TileState.DoorOpen), "the server refused: someone is in the doorway");
            Assert.That(a.Client.Walls[door].State, Is.EqualTo(TileState.DoorOpen), "and your view follows");
            Assert.That(blocker.ServerSide.State.Dead, Is.False);
        }
        [Test]
        public void OpenThenShutQuickly_AtHighPing_TheOldOpenAnswerDoesNotReopenItInYourView()
        {
            SculptNetHarness h = Duel(out BreachMapInfo info);
            int door = NorthDoor(h.Server.Walls, info.Block, out Vec3d front);
            var lag = SculptNetHarness.Lag(50);
            var a = h.AddClient(front, team: 1, up: lag, down: lag);
            h.RunSeconds(1);
            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                if (t == start) return new SculptTickInput { Hand = new HandInput { Door = true, DoorTile = door, DoorOpen = true } };
                if (t == start + 2 * SimTime.Per60) return new SculptTickInput { Hand = new HandInput { Door = true, DoorTile = door, DoorOpen = false } };
                return default;
            };
            // Run until just after the shut press, then watch the client's view for a round trip and more.
            h.RunMs(SculptNetHarness.TickMs * (2 * SimTime.Per60 + 3));
            Assert.That(a.Client.Walls[door].State, Is.Not.EqualTo(TileState.DoorOpen), "shut in your view at once");
            int reopened = 0;
            for (int k = 0; k < 40 * SimTime.Per60; k++)
            {
                h.RunMs(SculptNetHarness.TickMs);
                if (a.Client.Walls[door].State == TileState.DoorOpen) reopened++;
            }
            Assert.That(h.Server.Walls[door].State, Is.Not.EqualTo(TileState.DoorOpen), "the server shut it too");
            Assert.That(reopened, Is.Zero, "the server's answer to the older open press never flicks it open again");
        }
    }
}