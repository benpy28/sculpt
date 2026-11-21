using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core.Net;
using Sculpt.Core.Tests.Net;

namespace Sculpt.Core.Tests
{
    /// <summary>Breakable wall tiles: the state machine, collision, bullet damage, the breach map and the network copy.</summary>
    public class TileWallsTests
    {
        private static Aabb Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
            new Aabb(new Vec3d(x0, y0, z0), new Vec3d(x1, y1, z1));

        /// <summary>A 4 x 2 wall running east from (0, 0, 10), thin in Z (10..10.3).</summary>
        private static BrushWorld Wall(out int[] ids, BreachConfig c = null)
        {
            var w = new BrushWorld(c);
            ids = w.Tiles.AddWall(new Vec3d(0, 0, 10), Dir.East, 4, 2);
            w.Tiles.Changes.Clear();
            return w;
        }

        // ------------------------------------------------------------------ state machine

        [Test]
        public void IntactCracksBelowHalfAndBreaksAtZero()
        {
            var w = Wall(out int[] t);
            TileWalls tw = w.Tiles;
            Assert.That(tw[t[0]].State, Is.EqualTo(TileState.Intact));
            Assert.That(tw[t[0]].Health, Is.EqualTo(100));
            tw.Damage(t[0], 50, TileCause.Bullet);
            Assert.That(tw[t[0]].State, Is.EqualTo(TileState.Intact), "exactly half is not below half");
            tw.Damage(t[0], 1, TileCause.Bullet);
            Assert.That(tw[t[0]].State, Is.EqualTo(TileState.Damaged));
            Assert.That(tw[t[0]].Health, Is.EqualTo(49));
            Assert.That(tw.Damage(t[0], 48, TileCause.Bullet), Is.False);
            Assert.That(tw[t[0]].Health, Is.EqualTo(1));
            Assert.That(tw.Damage(t[0], 1000, TileCause.Bullet), Is.True, "overkill breaks it");
            Assert.That(tw[t[0]].State, Is.EqualTo(TileState.Broken));
            Assert.That(tw[t[0]].Health, Is.EqualTo(0));
        }

        [Test]
        public void PatchedAndReinforcedKeepTheirStateUntilTheyBreak()
        {
            var w = Wall(out int[] t);
            TileWalls tw = w.Tiles;
            tw.Set(t[0], TileState.Patched, 50, TileCause.Heal);
            tw.Set(t[1], TileState.Reinforced, 200, TileCause.Reinforce);
            tw.Damage(t[0], 45, TileCause.Bullet);
            tw.Damage(t[1], 190, TileCause.Bullet);
            Assert.That(tw[t[0]].State, Is.EqualTo(TileState.Patched));
            Assert.That(tw[t[0]].Health, Is.EqualTo(5));
            Assert.That(tw[t[1]].State, Is.EqualTo(TileState.Reinforced));
            Assert.That(tw[t[1]].Health, Is.EqualTo(10));
            Assert.That(tw.Damage(t[0], 5, TileCause.Bullet), Is.True);
            Assert.That(tw.Damage(t[1], 10, TileCause.Bullet), Is.True);
            Assert.That(tw[t[0]].State, Is.EqualTo(TileState.Broken));
            Assert.That(tw[t[1]].State, Is.EqualTo(TileState.Broken));
        }

        [Test]
        public void BrokenTilesIgnoreDamageAndNothingHappensForZeroDamage()
        {
            var w = Wall(out int[] t);
            TileWalls tw = w.Tiles;
            tw.Damage(t[0], 0, TileCause.Bullet);
            tw.Damage(t[0], -20, TileCause.Bullet);
            Assert.That(tw[t[0]].Health, Is.EqualTo(100), "no healing through negative damage");
            Assert.That(tw.Changes, Is.Empty);
            tw.Damage(t[0], 100, TileCause.Bullet);
            int rev = tw.Revision;
            Assert.That(tw.Damage(t[0], 30, TileCause.Bullet), Is.False, "already broken");
            Assert.That(tw.Revision, Is.EqualTo(rev));
        }

        [Test]
        public void SetIsTheOneWriter_ClampsHealthLogsAndSkipsNoOps()
        {
            var w = Wall(out int[] t);
            TileWalls tw = w.Tiles;
            tw.Tick = 77;
            tw.Set(t[2], TileState.Patched, 999, TileCause.Heal, by: 3);
            Assert.That(tw[t[2]].Health, Is.EqualTo(50), "patched max is 50");
            tw.Set(t[3], TileState.Broken, 80, TileCause.Grenade);
            Assert.That(tw[t[3]].Health, Is.EqualTo(0), "broken is always 0");
            tw.Set(t[4], TileState.Intact, 0, TileCause.Heal);
            Assert.That(tw[t[4]].Health, Is.EqualTo(1), "a standing tile keeps at least 1");
            Assert.That(tw.Changes.Count, Is.EqualTo(3));
            TileChange c = tw.Changes[0];
            Assert.That((c.Tick, c.Tile, c.From, c.To, c.Health, c.Cause, c.By),
                Is.EqualTo((77, t[2], TileState.Intact, TileState.Patched, 50, TileCause.Heal, 3)));
            int rev = tw.Revision;
            tw.Set(t[2], TileState.Patched, 50, TileCause.Heal);
            Assert.That(tw.Changes.Count, Is.EqualTo(3), "same state and health: not a change");
            Assert.That(tw.Revision, Is.EqualTo(rev));
            Assert.That(tw[t[2]].Changed && tw[t[3]].Changed, Is.True);
            Assert.That(tw[t[0]].Changed, Is.False);
        }

        [Test]
        public void MaxHealthPerStateIsWhatBenAskedFor()
        {
            var c = new BreachConfig();
            Assert.That(c.MaxHealthOf(TileState.Intact), Is.EqualTo(100));
            Assert.That(c.MaxHealthOf(TileState.Damaged), Is.EqualTo(100));
            Assert.That(c.MaxHealthOf(TileState.Patched), Is.EqualTo(50));
            Assert.That(c.MaxHealthOf(TileState.Reinforced), Is.EqualTo(200));
            Assert.That(c.MaxHealthOf(TileState.Broken), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ geometry and collision

        [Test]
        public void WallTilesAre15mSquaresOnTheGridAndFaceAcrossTheWall()
        {
            var w = Wall(out int[] t);
            TileWalls tw = w.Tiles;
            Assert.That(t.Length, Is.EqualTo(8));
            Aabb b = tw.BoundsOf(t[4 + 1]); // second row, second column
            Assert.That(b.Min.X, Is.EqualTo(1.5).Within(1e-12));
            Assert.That(b.Max.X, Is.EqualTo(3.0).Within(1e-12));
            Assert.That(b.Min.Y, Is.EqualTo(1.5).Within(1e-12));
            Assert.That(b.Max.Y, Is.EqualTo(3.0).Within(1e-12));
            Assert.That(b.Min.Z, Is.EqualTo(10).Within(1e-12));
            Assert.That(b.Max.Z, Is.EqualTo(10.3).Within(1e-12));
            Assert.That(tw[t[0]].Facing, Is.EqualTo(Dir.North));
            Assert.That(w[tw[t[0]].Brush].Kind, Is.EqualTo(BrushKind.Tile));

            int[] side = tw.AddWall(new Vec3d(20, 0, 0), Dir.North, 2, 1);
            Aabb s = tw.BoundsOf(side[1]);
            Assert.That((s.Min.X, s.Max.X, s.Min.Z, s.Max.Z), Is.EqualTo((20.0, 20.3, 1.5, 3.0)));
            Assert.That(tw[side[0]].Facing, Is.EqualTo(Dir.East));
            Assert.Throws<ArgumentException>(() => tw.AddWall(Vec3d.Zero, Dir.West, 1, 1));
        }

        [Test]
        public void BrokenTilesLoseCollisionAndStopNoBullets_HealedOnesGetItBack()
        {
            var w = Wall(out int[] t);
            TileWalls tw = w.Tiles;
            Aabb inTile = Box(0.5, 0.5, 10.05, 1.0, 1.0, 10.25);
            Assert.That(w.BoxOverlapsSolid(inTile), Is.True);
            TerrainHit h = w.Raycast(new Vec3d(0.75, 0.75, 5), new Vec3d(0, 0, 1), 20);
            Assert.That(tw.TileOfHit(h), Is.EqualTo(t[0]));

            tw.Damage(t[0], 100, TileCause.Bullet);
            Assert.That(w.BoxOverlapsSolid(inTile), Is.False);
            Assert.That(w.Raycast(new Vec3d(0.75, 0.75, 5), new Vec3d(0, 0, 1), 20).Hit, Is.False, "the hole lets bullets through");
            Assert.That(w.Raycast(new Vec3d(2.25, 0.75, 5), new Vec3d(0, 0, 1), 20).Hit, Is.True, "the neighbour still stands");

            tw.Set(t[0], TileState.Patched, 50, TileCause.Heal);
            Assert.That(w.BoxOverlapsSolid(inTile), Is.True);
            Assert.That(tw.TileOfHit(w.Raycast(new Vec3d(0.75, 0.75, 5), new Vec3d(0, 0, 1), 20)), Is.EqualTo(t[0]));
        }

        [Test]
        public void OneBrokenTileFitsASliderAndTwoStackedFitAStandingPlayer()
        {
            var m = new MovementConfig();
            var w = Wall(out int[] t);
            TileWalls tw = w.Tiles;
            double hw = m.HalfWidth;
            Aabb slider = Box(2.25 - hw, 0, 10.15 - hw, 2.25 + hw, m.CrouchHeight, 10.15 + hw);
            Aabb stander = Box(2.25 - hw, 0, 10.15 - hw, 2.25 + hw, m.StandHeight, 10.15 + hw);
            tw.Damage(t[1], 100, TileCause.Bullet);
            Assert.That(w.BoxOverlapsSolid(slider), Is.False, "a sliding player fits a one-tile hole");
            Assert.That(w.BoxOverlapsSolid(stander), Is.True, "standing needs two");
            tw.Damage(t[4 + 1], 100, TileCause.Bullet);
            Assert.That(w.BoxOverlapsSolid(stander), Is.False);
        }

        [Test]
        public void ARayAlongTheSeamBetweenTwoTilesHitsOneOfThem()
        {
            var w = Wall(out int[] t);
            // x = 1.5 is the seam between tiles 0 and 1, y = 1.5 the seam between the rows.
            Assert.That(w.Raycast(new Vec3d(1.5, 0.75, 5), new Vec3d(0, 0, 1), 20).Hit, Is.True);
            Assert.That(w.Raycast(new Vec3d(1.5, 1.5, 5), new Vec3d(0, 0, 1), 20).Hit, Is.True, "four-tile corner");
            w.Tiles.Damage(t[0], 100, TileCause.Bullet);
            Assert.That(w.Tiles.TileOfHit(w.Raycast(new Vec3d(1.5, 0.75, 5), new Vec3d(0, 0, 1), 20)), Is.EqualTo(t[1]), "the standing neighbour");
        }

        [Test]
        public void TileOfBrushAndFindWorkThroughTheCompositeTerrain()
        {
            var w = Wall(out int[] t);
            int plain = w.AddBox(new Vec3d(30, 0, 0), new Vec3d(31, 1, 1), BrushKind.Wall);
            Assert.That(w.Tiles.TileOfBrush(plain), Is.EqualTo(-1));
            Assert.That(w.Tiles.TileOfHit(default), Is.EqualTo(-1));
            var g = new SubGrid(5, 1, 5, -16); // 24 m: the ray meets the wall before the edge of the ground
            g.FillBaseGround();
            var comp = new CompositeTerrain(g, w);
            Assert.That(TileWalls.Find(comp), Is.SameAs(w.Tiles));
            Assert.That(TileWalls.Find(w), Is.SameAs(w.Tiles));
            Assert.That(TileWalls.Find(g), Is.Null);
            TerrainHit h = comp.Raycast(new Vec3d(3.75, 0.75, 5), new Vec3d(0, 0, 1), 20);
            Assert.That(w.Tiles.TileOfHit(h), Is.EqualTo(t[2]), "the composite passes the brush id through");
        }

        // ------------------------------------------------------------------ bullets

        private static int ShotsToBreak(Weapon gun, TileState start)
        {
            var w = Wall(out int[] t);
            var cfg = new WeaponsConfig();
            WeaponStats st = cfg.Of(gun);
            if (start != TileState.Intact) w.Tiles.Set(t[1], start, 1000, TileCause.Built);
            var victims = new List<ShotVictim>();
            var pellets = new List<BulletHit>();
            Vec3d origin = new Vec3d(2.25, 0.75, 8.5); // 1.5 m from the wall, centred on tile 1
            for (int n = 1; n <= 100; n++)
            {
                var shot = new WeaponShot { Tick = n, ShooterId = 1, Weapon = gun, Origin = origin, Aim = new Vec3d(0, 0, 1), Spread = 0, Seed = (uint)n };
                Hitscan.Resolve(w, shot, st, cfg, 1, new List<HitTarget>(), victims, pellets);
                w.Tiles.ApplyShot(pellets, gun, 1);
                if (w.Tiles[t[1]].State == TileState.Broken) return n;
            }
            return -1;
        }

        [Test]
        public void HitsToBreakAnIntactTileArePerGunAndExact()
        {
            Assert.That(ShotsToBreak(Weapon.Ar, TileState.Intact), Is.EqualTo(8), "13 x 8 = 104");
            Assert.That(ShotsToBreak(Weapon.Smg, TileState.Intact), Is.EqualTo(15), "7 x 15 = 105");
            Assert.That(ShotsToBreak(Weapon.Pistol, TileState.Intact), Is.EqualTo(6), "17 x 6 = 102");
            Assert.That(ShotsToBreak(Weapon.Pump, TileState.Intact), Is.EqualTo(2), "9 pellets x 6 = 54 per point-blank shot");
        }

        [Test]
        public void PatchedTilesBreakFasterAndReinforcedSlower()
        {
            Assert.That(ShotsToBreak(Weapon.Ar, TileState.Patched), Is.EqualTo(4), "50 health");
            Assert.That(ShotsToBreak(Weapon.Ar, TileState.Reinforced), Is.EqualTo(16), "200 health");
        }

        [Test]
        public void ShotsOnPlainWallsGroundAndPlayersDoNoTileDamage()
        {
            var w = Wall(out _);
            w.Tiles.Changes.Clear();
            var pellets = new List<BulletHit>
            {
                new BulletHit { Kind = HitKind.Terrain, Brush = 0 },
                new BulletHit { Kind = HitKind.Player, Brush = 1 },
                new BulletHit { Kind = HitKind.None },
            };
            int plain = w.AddBox(new Vec3d(30, 0, 0), new Vec3d(31, 1, 1), BrushKind.Wall);
            pellets.Add(new BulletHit { Kind = HitKind.Terrain, Brush = plain + 1 });
            Assert.That(w.Tiles.ApplyShot(pellets, Weapon.Ar, 1), Is.EqualTo(0));
            Assert.That(w.Tiles.Changes, Is.Empty);
        }

        // ------------------------------------------------------------------ the breach map

        [Test]
        public void BreachMapShowsEveryTileStateAndTheBuildIsNotNews()
        {
            BrushWorld w = BreachMap.BuildBrushes(out BreachMapInfo info);
            TileWalls tw = w.Tiles;
            Assert.That(tw.Changes, Is.Empty);
            foreach (int[] wall in new[] { info.TestWallA, info.TestWallB })
            {
                Assert.That(wall.Length, Is.EqualTo(16));
                Assert.That(tw[wall[0]].State, Is.EqualTo(TileState.Intact));
                Assert.That(tw[wall[2]].State, Is.EqualTo(TileState.Damaged));
                Assert.That(tw[wall[3]].State, Is.EqualTo(TileState.Patched));
                Assert.That(tw[wall[4]].State, Is.EqualTo(TileState.Reinforced));
                Assert.That(tw[wall[5]].State, Is.EqualTo(TileState.Broken));
                Assert.That(tw[wall[6]].State, Is.EqualTo(TileState.Broken));
                Assert.That(tw[wall[8 + 6]].State, Is.EqualTo(TileState.Broken), "a doorway");
                foreach (int id in wall) Assert.That(tw[id].Changed, Is.False, "a late joiner builds these itself");
            }
        }

        [Test]
        public void TheMiddleRoomIsSealedUntilYouBreakIn()
        {
            BrushWorld w = BreachMap.BuildBrushes(out BreachMapInfo info);
            Assert.That(info.Room.Length, Is.EqualTo(48));
            double mid = info.SizeX / 2;
            Vec3d centre = new Vec3d(mid, 1.6, mid);
            foreach (Vec3d d in new[] { new Vec3d(1, 0, 0), new Vec3d(-1, 0, 0), new Vec3d(0, 0, 1), new Vec3d(0, 0, -1), new Vec3d(0.7, 0, 0.7).Normalized })
            {
                TerrainHit h = w.Raycast(centre, d, 20);
                Assert.That(h.Hit, Is.True, $"sealed toward {d} from {centre}");
            }
            TerrainHit up = w.Raycast(centre, new Vec3d(0, 1, 0), 20);
            Assert.That(up.Hit && w[up.Brush - 1].Kind == BrushKind.Floor, Is.True, "roofed");
            // The rays above run exactly along tile seams (the centre is on one): a seam never lets a bullet through.
            Vec3d inTile = centre + new Vec3d(0, 0, 0.75);
            TerrainHit east = w.Raycast(inTile, new Vec3d(1, 0, 0), 20);
            int tile = w.Tiles.TileOfHit(east);
            Assert.That(tile, Is.GreaterThanOrEqualTo(0));
            w.Tiles.Damage(tile, 100, TileCause.Bullet);
            TerrainHit after = w.Raycast(inTile, new Vec3d(1, 0, 0), 20);
            Assert.That(!after.Hit || after.Distance > 5, Is.True, "one broken tile opens a line out");
        }

        // ------------------------------------------------------------------ network copy

        [Test]
        public void TileMessagesRoundTripAndBadStatesAreRefused()
        {
            var changes = new List<TileChange>
            {
                new TileChange { Tile = 3, To = TileState.Damaged, Health = 40, Cause = TileCause.Bullet, By = 2 },
                new TileChange { Tile = 900, To = TileState.Broken, Health = 0, Cause = TileCause.Grenade, By = 7 },
            };
            var w = new ByteWriter(64);
            TileProtocol.Write(w, new TilesMsg { Tick = 1234, Join = true, Changes = changes });
            byte[] data = w.ToArray();
            var r = new ByteReader(data, 0, data.Length);
            var into = new List<TileChange>();
            TilesMsg m = TileProtocol.Read(ref r, into, new List<HealEvent>());
            Assert.That(m.Tick, Is.EqualTo(1234));
            Assert.That(m.Join, Is.True);
            Assert.That(into.Count, Is.EqualTo(2));
            Assert.That((into[1].Tile, into[1].To, into[1].Health, into[1].Cause, into[1].By), Is.EqualTo((900, TileState.Broken, 0, TileCause.Grenade, 7)));

            data[1 + 4 + 1 + 2 + 2] = 9; // first change's state byte
            Assert.Throws<NetFormatException>(() =>
            {
                var bad = new ByteReader(data, 0, data.Length);
                TileProtocol.Read(ref bad, into, new List<HealEvent>());
            });
            Assert.Throws<NetFormatException>(() =>
            {
                var cut = new ByteReader(data, 0, data.Length - 3);
                TileProtocol.Read(ref cut, into, new List<HealEvent>());
            });
        }

        /// <summary>Flat ground with a 4 x 2 tile wall running east from (6, 0, 12).</summary>
        private static SculptNetHarness WallArena() => new SculptNetHarness(() =>
        {
            var g = new SubGrid(8, 4, 8, -16);
            g.FillBaseGround();
            g.ClearChanges();
            return g;
        }, wrap: g =>
        {
            var w = new BrushWorld();
            w.Tiles.AddWall(new Vec3d(6, 0, 12), Dir.East, 4, 2);
            w.Tiles.Changes.Clear();
            return new CompositeTerrain(g, w);
        });

        [Test]
        public void ServerBreaksTilesWithBullets_ClientsAndLateJoinersCopyIt()
        {
            var h = WallArena();
            var a = h.AddClient(new Vec3d(7.0, 0, 7.5), team: 1, up: SculptNetHarness.Lag(40), down: SculptNetHarness.Lag(40));
            Vec3d target = new Vec3d(6.75, 0.75, 12); // tile 0's centre
            int start = -1;
            a.Script = (p, t) =>
            {
                if (start < 0) start = t;
                var i = new SculptTickInput();
                if (t == start + 1) i.Hand.WeaponKey = (byte)((int)Weapon.Pistol + 1);
                MotorState s = p.Client.Predicted;
                Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, p.Client.Movement), 0);
                Vec3d d = target - eye;
                i.Move.Yaw = Math.Atan2(d.X, d.Z);
                i.Pitch = (float)(-Math.Asin(d.Y / d.Length) * 180 / Math.PI);
                bool fire = t > start + 40 && (t - start) % 20 == 0;
                i.Move.FireHeld = fire;
                i.Move.FirePressed = fire;
                i.Move.WantsToShoot = fire;
                return i;
            };
            TileWalls server = h.Server.Walls;
            Assert.That(server, Is.Not.Null);
            h.RunSeconds(0.7 + 3 * 20 / 60.0 + 0.15); // 3 shots out, plus the 40 ms trip
            Assert.That(server[0].State, Is.EqualTo(TileState.Damaged), "3 pistol rounds: 49 left");
            Assert.That(server[0].Health, Is.EqualTo(49));
            h.RunSeconds(3 * 20 / 60.0 + 0.3);
            Assert.That(server[0].State, Is.EqualTo(TileState.Broken), "6 rounds break it");
            for (int i = 1; i < server.Count; i++) Assert.That(server[i].State, Is.EqualTo(TileState.Intact), "only the aimed tile");

            TileWalls mine = a.Client.Walls;
            Assert.That(mine, Is.Not.SameAs(server), "each side has its own copy");
            Assert.That(mine[0].State, Is.EqualTo(TileState.Broken), "the shooter's copy follows the server");
            Assert.That(a.Client.Grid.BoxOverlapsSolid(new Aabb(new Vec3d(6.5, 0.5, 12.05), new Vec3d(7.0, 1.0, 12.25))), Is.False, "and its collision too");
            bool sawBreak = false, sawCrack = false;
            foreach (TileChange c in mine.Changes)
            {
                if (c.Tile == 0 && c.To == TileState.Broken && c.Cause == TileCause.Bullet && c.By == a.ServerSide.Id) sawBreak = true;
                if (c.Tile == 0 && c.To == TileState.Damaged && c.From == TileState.Intact) sawCrack = true;
            }
            Assert.That(sawBreak && sawCrack, Is.True, "the client's change list carries cause, shooter and the old state for effects");

            var late = h.AddClient(new Vec3d(20, 0, 5), team: 2);
            h.RunSeconds(0.3);
            Assert.That(late.Client.Walls[0].State, Is.EqualTo(TileState.Broken), "a late joiner gets the changed tiles");
            Assert.That(late.Client.Walls[1].State, Is.EqualTo(TileState.Intact));
            foreach (TileChange c in late.Client.Walls.Changes) Assert.That(c.Cause, Is.EqualTo(TileCause.Server), "join state is not a fresh break (no debris)");
        }
    }
}
