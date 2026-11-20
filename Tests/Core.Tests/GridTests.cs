using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests
{
    public class GridTests
    {
        [Test]
        public void NewGridIsEmpty()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            Assert.That(g.Get(5, 5, 5).IsEmpty, Is.True);
            Assert.That(g.SizeX, Is.EqualTo(16));
        }

        [Test]
        public void SetThenGetAcrossChunksAndNegativeY()
        {
            var g = new VoxelGrid(2, 2, 2, -16);
            var cells = new[] { new Int3(0, -16, 0), new Int3(15, -1, 15), new Int3(16, 0, 16), new Int3(31, 15, 31), new Int3(17, -3, 2) };
            foreach (var c in cells) g.Set(c, Block.Solid);
            foreach (var c in cells) Assert.That(g.Get(c).Kind, Is.EqualTo(BlockKind.Solid), c.ToString());
            Assert.That(g.Get(16, -1, 16).IsEmpty, Is.True);
        }

        [Test]
        public void BlockStateRoundTripsAllFields()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            var b = Block.Ramp(Dir.South);
            b.ReinforceTeam = 2;
            b.ReinforceUntilTick = 1234;
            b.TintTeam = 1;
            b.TintTick = 99;
            g.Set(3, 4, 5, b);
            Block r = g.Get(3, 4, 5);
            Assert.That(r.IsRamp && r.RampLow == Dir.South && r.ReinforceTeam == 2 && r.ReinforceUntilTick == 1234 && r.TintTeam == 1 && r.TintTick == 99);
        }

        [Test]
        public void OutOfBoundsSidesAndBottomAreAnchorAboveIsEmpty()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            Assert.That(g.Get(-1, 3, 3).Kind, Is.EqualTo(BlockKind.Anchor));
            Assert.That(g.Get(16, 3, 3).Kind, Is.EqualTo(BlockKind.Anchor));
            Assert.That(g.Get(3, 3, -1).Kind, Is.EqualTo(BlockKind.Anchor));
            Assert.That(g.Get(3, -1, 3).Kind, Is.EqualTo(BlockKind.Anchor));
            Assert.That(g.Get(3, 16, 3).IsEmpty, Is.True);
            Assert.Throws<ArgumentOutOfRangeException>(() => g.Set(new Int3(3, 16, 3), Block.Solid));
        }

        [Test]
        public void MinYMustBeChunkAligned() =>
            Assert.Throws<ArgumentException>(() => new VoxelGrid(1, 1, 1, -3));

        [Test]
        public void SetMarksOnlyItsChunkWhenInterior()
        {
            var g = new VoxelGrid(2, 1, 2, 0);
            g.Set(5, 5, 5, Block.Solid);
            var dirty = new List<int>();
            g.DrainDirtyChunks(dirty);
            Assert.That(dirty, Is.EqualTo(new[] { g.ChunkIndex(new Int3(0, 0, 0)) }));
            Assert.That(g.HasDirtyChunks, Is.False);
        }

        [Test]
        public void SetOnChunkBorderMarksNeighbour()
        {
            var g = new VoxelGrid(2, 1, 2, 0);
            g.Set(15, 5, 5, Block.Solid);
            var dirty = new List<int>();
            g.DrainDirtyChunks(dirty);
            Assert.That(dirty, Is.EquivalentTo(new[] { g.ChunkIndex(new Int3(0, 0, 0)), g.ChunkIndex(new Int3(1, 0, 0)) }));
        }

        [Test]
        public void ChangeLogRecordsWritesInOrder()
        {
            var g = new VoxelGrid(1, 1, 1, 0);
            g.Set(1, 1, 1, Block.Solid);
            g.Set(2, 1, 1, Block.Solid);
            var changes = new List<Int3>();
            g.DrainChanges(changes);
            Assert.That(changes, Is.EqualTo(new[] { new Int3(1, 1, 1), new Int3(2, 1, 1) }));
            changes.Clear();
            g.DrainChanges(changes);
            Assert.That(changes, Is.Empty);
        }

        [Test]
        public void ChunkIndexRoundTrips()
        {
            var g = new VoxelGrid(3, 2, 4, -16);
            for (int i = 0; i < g.ChunkCount; i++)
                Assert.That(g.ChunkIndex(g.ChunkCoordFromIndex(i)), Is.EqualTo(i));
            Assert.That(g.ChunkOrigin(new Int3(1, 1, 2)), Is.EqualTo(new Int3(16, 0, 32)));
            Assert.That(g.ChunkCoordOf(new Int3(17, -1, 33)), Is.EqualTo(new Int3(1, 0, 2)));
        }

        [Test]
        public void BaseGroundTopIsCellMinusOne()
        {
            var w = new TestWorld();
            Assert.That(w.At(3, -1, 3).Kind, Is.EqualTo(BlockKind.Solid));
            Assert.That(w.At(3, 0, 3).IsEmpty, Is.True);
            Assert.That(w.Grid.TopOfStack(new Int3(3, -5, 3)), Is.EqualTo(new Int3(3, -1, 3)));
        }

        [Test]
        public void CellAtAndBoundsAgree()
        {
            Assert.That(VoxelGrid.CellAt(new Vec3d(0.0, -0.01, 1.19)), Is.EqualTo(new Int3(0, -1, 0)));
            Assert.That(VoxelGrid.CellAt(new Vec3d(1.2, 0, -0.01)), Is.EqualTo(new Int3(1, 0, -1)));
            Aabb b = VoxelGrid.CellBounds(new Int3(1, -1, 2));
            Assert.That(b.Min.X, Is.EqualTo(1.2).Within(1e-12));
            Assert.That(b.Max.Y, Is.EqualTo(0).Within(1e-12));
            Assert.That(b.Max.Z, Is.EqualTo(3.6).Within(1e-12));
        }

        [Test]
        public void DirOppositeAndVectors()
        {
            foreach (Dir d in Enum.GetValues(typeof(Dir)))
            {
                Assert.That(d.Opposite().Opposite(), Is.EqualTo(d));
                Assert.That(d.ToInt3() + d.Opposite().ToInt3(), Is.EqualTo(Int3.Zero));
                Assert.That(DirUtil.FromInt3(d.ToInt3()), Is.EqualTo(d));
            }
            Assert.That(Dir.Up.IsHorizontal(), Is.False);
            Assert.That(Dir.North.IsHorizontal(), Is.True);
        }
    }
}
