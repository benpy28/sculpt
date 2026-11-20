using System;
using System.Collections.Generic;
using NUnit.Framework;
using Sculpt.Core;

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// SplitMix64. Our own generator so fixed seeds give the same sequence on every .NET runtime,
    /// which System.Random does not promise.
    /// </summary>
    internal sealed class Rng
    {
        private ulong _s;

        public Rng(ulong seed) { _s = seed; }

        public ulong Next()
        {
            ulong z = (_s += 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Integer in [lo, hiExclusive).</summary>
        public int Int(int lo, int hiExclusive) => lo + (int)(Next() % (ulong)(hiExclusive - lo));

        /// <summary>Double in [0, 1).</summary>
        public double Double() => (Next() >> 11) * (1.0 / (1UL << 53));

        public double Range(double lo, double hi) => lo + (hi - lo) * Double();

        public bool Chance(double p) => Double() < p;

        public T Pick<T>(T[] items) => items[Int(0, items.Length)];
    }

    /// <summary>Random arena terrain: steps, pits, ramps on tops, anchor pillars (anchors never float).</summary>
    internal static class TerrainGen
    {
        private static readonly int[] Tops = { -3, -2, -2, -1, -1, -1, -1, -1, 0, 0, 0, 1, 1, 2, 3 };

        public static void Build(VoxelGrid g, Rng r, int regionMin, int regionMax, double rampChance = 0.2, double anchorChance = 0.05)
        {
            Dir[] horiz = DirUtil.Horizontal;
            for (int x = regionMin; x <= regionMax; x++)
                for (int z = regionMin; z <= regionMax; z++)
                {
                    // Column top cell: -3 (pit at level -2) .. 3 (level +4), weighted toward ground level.
                    int top = r.Pick(Tops);
                    for (int y = top + 1; y <= -1; y++) g.Set(x, y, z, Block.Empty);
                    for (int y = 0; y <= top; y++) g.Set(x, y, z, Block.Solid);

                    if (top >= -2 && r.Chance(anchorChance))
                        for (int y = -2; y <= top; y++) g.Set(x, y, z, Block.Anchor);
                    else if (top >= -2 && r.Chance(rampChance))
                        g.Set(x, top, z, Block.Ramp(r.Pick(horiz)));
                }
        }
    }

    /// <summary>Read-only inspection helpers shared by the fuzz and invariant tests.</summary>
    internal static class Inspect
    {
        public const double B = VoxelGrid.BlockSize;

        /// <summary>FNV-1a over every field of every cell in a Y band of the grid.</summary>
        public static ulong HashGrid(VoxelGrid g, int yMin, int yMaxInclusive)
        {
            ulong h = 14695981039346656037UL;
            for (int y = yMin; y <= yMaxInclusive; y++)
                for (int z = 0; z < g.SizeZ; z++)
                    for (int x = 0; x < g.SizeX; x++)
                        h = Mix(h, Pack(g.Get(x, y, z)));
            return h;
        }

        /// <summary>Every field of a block in one value.</summary>
        public static ulong Pack(Block b) =>
            (ulong)b.Kind | ((b.IsRamp ? 1UL : 0UL) << 2) | ((ulong)b.RampLow << 3);

        public static ulong Mix(ulong h, ulong v)
        {
            h ^= v;
            h *= 0x100000001B3UL;
            return h ^ (h >> 29);
        }

        public static ulong MixDouble(ulong h, double d) => Mix(h, (ulong)BitConverter.DoubleToInt64Bits(d));

        public static int CountNonEmpty(VoxelGrid g, int yMin, int yMaxInclusive)
        {
            int n = 0;
            for (int y = yMin; y <= yMaxInclusive; y++)
                for (int z = 0; z < g.SizeZ; z++)
                    for (int x = 0; x < g.SizeX; x++)
                        if (!g.Get(x, y, z).IsEmpty) n++;
            return n;
        }

        /// <summary>
        /// Lowest feet height where the player's box clears the terrain under it, by bisection.
        /// Valid because columns are solid from the bottom up (no overhangs), so overlap is monotone in height.
        /// </summary>
        public static bool DropToGround(VoxelGrid g, PlayerBody p, double lo = -4.0, double hi = 7.0)
        {
            var at = new Func<double, bool>(y => VoxelGeometry.BoxOverlapsSolid(g, p.BoundsAt(new Vec3d(p.Position.X, y, p.Position.Z))));
            if (at(hi)) return false;
            if (!at(lo)) { p.Position = p.Position.WithY(lo); return true; }
            for (int i = 0; i < 60; i++)
            {
                double mid = (lo + hi) * 0.5;
                if (at(mid)) lo = mid; else hi = mid;
            }
            p.Position = p.Position.WithY(hi);
            return !at(hi);
        }

        public static bool IsInsideTerrain(VoxelGrid g, PlayerBody p) => VoxelGeometry.BoxOverlapsSolid(g, p.Bounds);

        /// <summary>Exact point-in-block test written independently of VoxelGeometry (closed cell, wedge for ramps).</summary>
        public static bool PointInBlock(Block b, Int3 cell, Vec3d p, double tol = 0)
        {
            if (b.IsEmpty) return false;
            Aabb c = VoxelGrid.CellBounds(cell);
            if (p.X < c.Min.X - tol || p.X > c.Max.X + tol || p.Y < c.Min.Y - tol || p.Y > c.Max.Y + tol || p.Z < c.Min.Z - tol || p.Z > c.Max.Z + tol) return false;
            if (!b.IsRamp) return true;
            double s; // metres from the high edge toward the low edge
            switch (b.RampLow)
            {
                case Dir.East: s = p.X - c.Min.X; break;
                case Dir.West: s = c.Max.X - p.X; break;
                case Dir.North: s = p.Z - c.Min.Z; break;
                default: s = c.Max.Z - p.Z; break;
            }
            return (p.Y - c.Min.Y) + s <= B + tol * 2;
        }
    }
}

namespace Sculpt.Core.Tests.Fuzz
{
    /// <summary>
    /// Plays like a twitchy human: holds an intent (direction, sprint, crouch, shoot) for a while, drifts
    /// the view, taps jump and crouch at random so slides, slide-jumps, slide-lands, vaults and mantles happen.
    /// </summary>
    internal sealed class InputBot
    {
        private readonly Rng _r;
        private int _nextChange;
        private double _moveX, _moveZ, _yaw;
        private bool _sprint, _crouch, _shoot, _crouchPrev;

        public InputBot(Rng r)
        {
            _r = r;
            _yaw = r.Range(0, 2 * System.Math.PI);
        }

        public MoveInput Next(int tick)
        {
            if (tick >= _nextChange)
            {
                _moveZ = _r.Pick(new[] { 1.0, 1.0, 1.0, 0.0, -1.0, 0.6 });
                _moveX = _r.Pick(new[] { 0.0, 0.0, 1.0, -1.0, 0.4 });
                _sprint = _r.Chance(0.6);
                _shoot = _r.Chance(0.1);
                if (_r.Chance(0.3)) _yaw = _r.Range(0, 2 * System.Math.PI);
                _nextChange = tick + _r.Int(5, 70);
            }
            _yaw += _r.Range(-0.06, 0.06);
            if (_r.Chance(_crouch ? 0.06 : 0.03)) _crouch = !_crouch;
            bool jump = _r.Chance(0.04);

            var i = new MoveInput
            {
                MoveX = _moveX,
                MoveZ = _moveZ,
                Yaw = _yaw,
                SprintHeld = _sprint,
                CrouchHeld = _crouch,
                CrouchPressed = _crouch && !_crouchPrev,
                JumpPressed = jump,
                JumpHeld = jump,
                WantsToShoot = _shoot,
            };
            _crouchPrev = _crouch;
            return i;
        }
    }

    /// <summary>Movement invariants that must hold on every tick, whatever the input.</summary>
    internal sealed class MotorChecker
    {
        public const double B = VoxelGrid.BlockSize;
        private readonly MovementConfig _c;
        private int _lastSprintTick = int.MinValue / 2;
        private int _lastFlatBoostTick = int.MinValue / 2;
        public int Jumps, Slides, SlideLands, Vaults, Mantles, Landings, RampLaunches;

        public MotorChecker(MovementConfig c) { _c = c; }

        public static bool Inside(ITerrain g, MovementConfig c, in MotorState s) =>
            VoxelGeometry.BoxOverlapsSolid(g, PlayerMovement.Box(s.Position, c.HalfWidth, PlayerMovement.HeightOf(s, c)));

        public void Check(ITerrain g, in MotorState before, in MotorState s, List<MoveEvent> events, string ctx, bool worldStatic)
        {
            Assert.That(Inside(g, _c, s), NUnit.Framework.Is.False, $"{ctx}: inside terrain at {s.Position} mode {s.Mode} crouched {s.Crouched}");
            Assert.That(s.Tick, NUnit.Framework.Is.EqualTo(before.Tick + 1), ctx + ": tick advances by one");

            Vec3d d = s.Position - before.Position;
            Assert.That(d.HorizontalLength, NUnit.Framework.Is.LessThanOrEqualTo(_c.SlideMaxSpeed * _c.Dt + 1e-6), $"{ctx}: horizontal jump of {d.HorizontalLength} m in one tick ({before.Mode} -> {s.Mode})");
            if (worldStatic)
                Assert.That(Math.Abs(d.Y), NUnit.Framework.Is.LessThanOrEqualTo(0.75), $"{ctx}: vertical jump of {d.Y} m in one tick ({before.Mode} -> {s.Mode})");

            Assert.That(s.Velocity.HorizontalLength, NUnit.Framework.Is.LessThanOrEqualTo(_c.SlideMaxSpeed + 1e-6), $"{ctx}: horizontal speed {s.Velocity.HorizontalLength} over the 14 m/s cap ({s.Mode})");
            Assert.That(s.Velocity.Y, NUnit.Framework.Is.GreaterThanOrEqualTo(-_c.MaxFallSpeed - 1e-9), ctx + ": fall speed cap");
            Assert.That(double.IsNaN(s.Position.X + s.Position.Y + s.Position.Z + s.Velocity.X + s.Velocity.Y + s.Velocity.Z), NUnit.Framework.Is.False, ctx + ": NaN");

            // Shooting rules: never while sprinting or mantling, and not within 0.15 s of leaving a sprint.
            if (s.Sprinting) _lastSprintTick = s.Tick;
            if (s.Tick - _lastSprintTick <= _c.SprintToAimTicks)
                Assert.That(s.CanShoot, NUnit.Framework.Is.False, $"{ctx}: can shoot {s.Tick - _lastSprintTick} ticks after sprinting");
            if (s.Mode == MoveMode.Mantle) Assert.That(s.CanShoot, NUnit.Framework.Is.False, ctx + ": can shoot while mantling");
            if (s.Sprinting) Assert.That(s.Crouched, NUnit.Framework.Is.False, ctx + ": sprinting while crouched");
            if (s.Mode == MoveMode.Slide) Assert.That(s.Crouched, NUnit.Framework.Is.True, ctx + ": sliding with a standing box");

            foreach (var e in events)
            {
                Assert.That(e.Tick, NUnit.Framework.Is.EqualTo(s.Tick), ctx + ": event tick");
                switch (e.Type)
                {
                    case MoveEventType.Jumped: Jumps++; break;
                    case MoveEventType.SlideLandSuccess: SlideLands++; break;
                    case MoveEventType.Landed:
                        Landings++;
                        Assert.That(e.Value, NUnit.Framework.Is.GreaterThanOrEqualTo(0));
                        if (e.Amount <= _c.FallDamageFreeLevels) Assert.That(e.Value, NUnit.Framework.Is.EqualTo(0), ctx + ": fall damage below 6 levels");
                        break;
                    case MoveEventType.Vaulted:
                        Vaults++;
                        Assert.That(s.ActionTargetY - s.ActionStartY, NUnit.Framework.Is.LessThanOrEqualTo(B + 0.06 + 1e-9), ctx + ": vault over more than one level");
                        break;
                    case MoveEventType.MantleStarted:
                        Mantles++;
                        Assert.That(s.ActionTargetY - s.ActionStartY, NUnit.Framework.Is.LessThanOrEqualTo(_c.MantleMaxHeight + 0.01 + 1e-9), ctx + ": mantle higher than two levels");
                        break;
                    case MoveEventType.SlideStarted:
                        Slides++;
                        if (e.Value == 1)
                        {
                            bool downhill = s.GroundNormal.X * s.Velocity.X + s.GroundNormal.Z * s.Velocity.Z > 1e-3;
                            if (!downhill)
                            {
                                Assert.That(s.Tick - _lastFlatBoostTick, NUnit.Framework.Is.GreaterThanOrEqualTo(_c.SlideBoostCooldownTicks), ctx + ": flat slide boost inside the 1.5 s cooldown");
                                _lastFlatBoostTick = s.Tick;
                            }
                        }
                        break;
                }
            }
            if (before.Mode == MoveMode.Ground && s.Mode == MoveMode.Air && s.Velocity.Y > 0.5 && !events.Exists(e => e.Type == MoveEventType.Jumped)) RampLaunches++;
        }

        public static ulong Hash(in MotorState s)
        {
            ulong h = 1469598103934665603UL;
            h = Inspect.MixDouble(h, s.Position.X); h = Inspect.MixDouble(h, s.Position.Y); h = Inspect.MixDouble(h, s.Position.Z);
            h = Inspect.MixDouble(h, s.Velocity.X); h = Inspect.MixDouble(h, s.Velocity.Y); h = Inspect.MixDouble(h, s.Velocity.Z);
            h = Inspect.Mix(h, (ulong)s.Mode); h = Inspect.Mix(h, (ulong)s.Tick);
            h = Inspect.Mix(h, (s.Crouched ? 1UL : 0) | (s.Sprinting ? 2UL : 0) | (s.Grounded ? 4UL : 0));
            h = Inspect.Mix(h, (ulong)(uint)s.ShootReadyTick);
            return h;
        }
    }
}
