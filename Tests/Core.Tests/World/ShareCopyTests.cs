using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Sculpt.Core.Tests
{
    /// <summary>
    /// Map v3 groundwork: the server's, the client's and the drawn copy of a map share everything fixed and copy tile
    /// state page by page on first write. A shared copy must behave exactly like a fresh build, and damage in one copy
    /// must never show in another.
    /// </summary>
    public class ShareCopyTests
    {
        [Test]
        public void CowList_CopiesNeverSeeEachOthersWrites()
        {
            var rng = new Random(7);
            var a = new CowList<int>();
            var ra = new List<int>();
            for (int i = 0; i < 5000; i++) { a.Add(i); ra.Add(i); }
            var lists = new List<CowList<int>> { a };
            var refs = new List<List<int>> { ra };
            for (int round = 0; round < 4000; round++)
            {
                int k = rng.Next(lists.Count);
                int op = rng.Next(10);
                if (op == 0 && lists.Count < 6)
                {
                    lists.Add(lists[k].ShareCopy());
                    refs.Add(new List<int>(refs[k]));
                }
                else if (op == 1)
                {
                    int v = rng.Next();
                    lists[k].Add(v);
                    refs[k].Add(v);
                }
                else
                {
                    int i = rng.Next(refs[k].Count), v = rng.Next();
                    lists[k][i] = v;
                    refs[k][i] = v;
                }
            }
            for (int k = 0; k < lists.Count; k++)
            {
                Assert.That(lists[k].Count, Is.EqualTo(refs[k].Count));
                for (int i = 0; i < refs[k].Count; i++) Assert.That(lists[k][i], Is.EqualTo(refs[k][i]), $"list {k} item {i}");
            }
        }

        [Test]
        public void CowList_ACopyOwnsNoPagesUntilItWrites()
        {
            var a = new CowList<byte>();
            a.GrowTo(10 * CowList<byte>.PageSize, 7);
            CowList<byte> b = a.ShareCopy();
            Assert.That(a.OwnPages, Is.EqualTo(0));
            Assert.That(b.OwnPages, Is.EqualTo(0));
            b[3 * CowList<byte>.PageSize + 5] = 9;
            Assert.That(b.OwnPages, Is.EqualTo(1));
            Assert.That(a[3 * CowList<byte>.PageSize + 5], Is.EqualTo(7));
            var buf = new byte[64];
            b.CopyTo(3 * CowList<byte>.PageSize, buf, 0, 64);
            Assert.That(buf[5], Is.EqualTo(9));
            Assert.Throws<ArgumentOutOfRangeException>(() => b.CopyTo(CowList<byte>.PageSize - 10, buf, 0, 64), "a run across two pages");
        }

        /// <summary>Everything a copy says about its tiles and shapes: state, health, cells, collision.</summary>
        private static string Fingerprint(BrushWorld w)
        {
            TileWalls t = w.Tiles;
            long h = 17;
            for (int i = 0; i < t.Count; i++)
            {
                WallTile x = t[i];
                h = h * 31 + (int)x.State;
                h = h * 31 + x.Health;
                h = h * 31 + (long)(x.Open ^ x.Patch * 3 ^ x.Steel * 5);
                if (t.HasCells(i))
                    for (int c = 0; c < Brush.CellCount; c++) h = h * 31 + t.CellHealth(i, c);
            }
            for (int i = 0; i < w.Count; i++)
            {
                Brush b = w[i];
                h = h * 31 + (b.Solid ? 1 : 0);
                h = h * 31 + (long)b.Open;
                h = h * 31 + (b.Solids?.Length ?? -1);
            }
            return h.ToString("x");
        }

        /// <summary>A fixed fight on the walls: shots by area, blasts, whole-tile damage and heals, the same every run.</summary>
        private static void Fight(BrushWorld w, int seed)
        {
            var rng = new Random(seed);
            TileWalls t = w.Tiles;
            for (int k = 0; k < 120; k++)
            {
                int tile = rng.Next(t.Count);
                Aabb b = t.BoundsOf(tile);
                var at = new Vec3d(b.Min.X + (b.Max.X - b.Min.X) * rng.NextDouble(), b.Min.Y + (b.Max.Y - b.Min.Y) * rng.NextDouble(), b.Min.Z + (b.Max.Z - b.Min.Z) * rng.NextDouble());
                switch (rng.Next(4))
                {
                    case 0: t.Damage(tile, 50, at, TileCause.Bullet, 1, 0.45); break;
                    case 1: t.Damage(tile, 150, at, TileCause.Grenade, 1, 2.2); break;
                    case 2: t.Damage(tile, 30, TileCause.Punch, 2); break;
                    default:
                        if (TileWalls.Gone(t[tile].State) && t[tile].State != TileState.DoorOpen)
                            t.Set(tile, TileState.Patched, 50, TileCause.Heal, 2);
                        break;
                }
            }
        }

        [Test]
        public void ASharedCopy_PlaysExactlyLikeAFreshBuild_AndCopiesStayApart()
        {
            BrushWorld template = DuelMap.BuildBrushes(out _);
            template.Tiles.Prepare();
            string built = Fingerprint(template);
            BrushWorld a = template.ShareCopy(), b = template.ShareCopy();
            BrushWorld fresh = DuelMap.BuildBrushes(out _);
            fresh.Tiles.Prepare();
            // The same fight on a shared copy and on a fresh build comes out the same.
            Fight(a, 3);
            Fight(fresh, 3);
            Assert.That(Fingerprint(a), Is.EqualTo(Fingerprint(fresh)), "a shared copy plays like a fresh build");
            Assert.That(Fingerprint(a), Is.Not.EqualTo(built), "the fight changed something");
            // The others never see it.
            Assert.That(Fingerprint(template), Is.EqualTo(built), "the template is untouched");
            Assert.That(Fingerprint(b), Is.EqualTo(built), "the other copy is untouched");
            // And b fights its own fight from the built state.
            Fight(b, 4);
            BrushWorld fresh4 = DuelMap.BuildBrushes(out _);
            fresh4.Tiles.Prepare();
            Fight(fresh4, 4);
            Assert.That(Fingerprint(b), Is.EqualTo(Fingerprint(fresh4)));
            Assert.That(Fingerprint(a), Is.EqualTo(Fingerprint(fresh)), "a didn't change when b fought");
        }

        [Test]
        public void ACopyOfACopy_StartsFromWhereItWas()
        {
            BrushWorld template = DuelMap.BuildBrushes(out _);
            BrushWorld a = template.ShareCopy();
            Fight(a, 5);
            string mid = Fingerprint(a);
            BrushWorld c = a.ShareCopy();
            Assert.That(Fingerprint(c), Is.EqualTo(mid));
            Fight(a, 6);
            Assert.That(Fingerprint(c), Is.EqualTo(mid), "the copy keeps the state it was made from");
        }

        [Test]
        public void RaysAndBoxes_AgreeWithAFreshBuild_AfterTheSameDamage()
        {
            BrushWorld w = DuelMap.BuildBrushes(out BreachMapInfo info).ShareCopy();
            BrushWorld f = DuelMap.BuildBrushes(out _);
            Fight(w, 8);
            Fight(f, 8);
            ITerrain tw = DuelMap.Terrain(DuelMap.BuildGround().ShareCopy(), w), tf = DuelMap.Terrain(DuelMap.BuildGround(), f);
            var rng = new Random(11);
            for (int i = 0; i < 3000; i++)
            {
                var o = new Vec3d(rng.NextDouble() * info.SizeX, 0.2 + rng.NextDouble() * 9, rng.NextDouble() * info.SizeZ);
                var d = new Vec3d(rng.NextDouble() - 0.5, (rng.NextDouble() - 0.5) * 0.4, rng.NextDouble() - 0.5);
                TerrainHit x = tw.Raycast(o, d, 80), y = tf.Raycast(o, d, 80);
                Assert.That((x.Hit, x.Brush), Is.EqualTo((y.Hit, y.Brush)), $"ray {i}");
                if (x.Hit) Assert.That(x.Distance, Is.EqualTo(y.Distance).Within(1e-9));
                var box = new Aabb(o, o + new Vec3d(0.7, 1.8, 0.7));
                Assert.That(tw.BoxOverlapsSolid(box), Is.EqualTo(tf.BoxOverlapsSolid(box)), $"box {i}");
            }
        }

        [Test]
        public void ACopiedWorld_CantGainBrushes()
        {
            BrushWorld w = DuelMap.BuildBrushes(out _);
            w.ShareCopy();
            Assert.Throws<InvalidOperationException>(() => w.AddBox(new Vec3d(0, 0, 0), new Vec3d(1, 1, 1), BrushKind.Wall));
        }

        [Test]
        public void SubGrid_SharedCopiesStayApart()
        {
            SubGrid g = DuelMap.BuildGround();
            SubGrid a = g.ShareCopy(), b = a.ShareCopy();
            Assert.That(a.SameCells(g) && b.SameCells(g));
            var at = new Int3(10, -2, 10);
            ushort before = g.Get(at);
            ushort other = before == SubId.Empty ? SubId.Natural : SubId.Empty;
            a.Set(at, other);
            Assert.That(a.Get(at), Is.EqualTo(other));
            Assert.That(g.Get(at), Is.EqualTo(before));
            Assert.That(b.Get(at), Is.EqualTo(before));
            var at2 = new Int3(11, -2, 10);
            ushort before2 = g.Get(at2);
            ushort other2 = before2 == SubId.Empty ? SubId.Natural : SubId.Empty;
            g.Set(at2, other2);
            Assert.That(g.Get(at2), Is.EqualTo(other2));
            Assert.That(a.Get(at2), Is.EqualTo(before2), "a write to the source never shows in its copies");
            Assert.That(b.Get(at2), Is.EqualTo(before2));
        }

        [Test]
        public void TheBattleRoyaleMap_CopiesOwnNothingUntilAFight()
        {
            BrushWorld w = BrMap.BuildBrushes(out _, out _);
            w.Tiles.Prepare();
            BrushWorld a = w.ShareCopy();
            Assert.That(a.OwnBrushPages, Is.EqualTo(0));
            Assert.That(a.Tiles.OwnTilePages, Is.EqualTo(0));
            Fight(a, 9); // 120 hits spread over the whole map: still only a small share of the pages
            int pages = (w.Count + CowList<Brush>.PageSize - 1) / CowList<Brush>.PageSize;
            Assert.That(a.OwnBrushPages, Is.LessThan(pages / 2), "pages copied after scattered damage");
        }
    }
}
