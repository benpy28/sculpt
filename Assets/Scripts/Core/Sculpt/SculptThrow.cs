using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>A piece in flight. Fixed point: positions in 1/65536 m, velocity in 1/65536 m per tick.</summary>
    public sealed class ThrownPiece
    {
        public int Id;
        public int Owner;
        public int Team;
        public ushort PieceId;
        public int SubCells;
        public long X, Y, Z;
        public long VX, VY, VZ;
        /// <summary>Half extents of the flight box, fixed point.</summary>
        public long HX, HY, HZ;
        public double LaunchSpeed;
        public int LaunchTick;
        public int[] IgnorePlayers = Array.Empty<int>();

        public const double Unit = 1.0 / 65536.0;

        public Vec3d Position => new Vec3d(X * Unit, Y * Unit, Z * Unit);
        public Vec3d Velocity => new Vec3d(VX * Unit * SimTime.TicksPerSecond, VY * Unit * SimTime.TicksPerSecond, VZ * Unit * SimTime.TicksPerSecond);
        public Aabb BoundsAt(long x, long y, long z) => new Aabb(
            new Vec3d((x - HX) * Unit, (y - HY) * Unit, (z - HZ) * Unit), new Vec3d((x + HX) * Unit, (y + HY) * Unit, (z + HZ) * Unit));
        public Aabb Bounds => BoundsAt(X, Y, Z);
    }

    public sealed partial class SculptSystem
    {
        private readonly List<ThrownPiece> _thrown = new List<ThrownPiece>();
        private int _nextThrowId = 1;

        public IReadOnlyList<ThrownPiece> Thrown => _thrown;

        private static long Fx(double metres) => (long)Math.Round(metres * 65536.0, MidpointRounding.AwayFromZero);

        /// <summary>Launch velocity in m/s: the look direction tipped up by ThrowLiftDegrees, at the size's launch speed.</summary>
        private Vec3d LaunchVelocity(Vec3d look, int subCells)
        {
            Vec3d d = look.Normalized;
            if (d.LengthSquared == 0) d = new Vec3d(0, 0, 1);
            double pitch = Math.Asin(Math.Max(-1, Math.Min(1, d.Y)));
            double lifted = Math.Min(Math.PI * 0.49, pitch + Config.ThrowLiftDegrees * Math.PI / 180.0);
            double hl = d.HorizontalLength;
            Vec3d h = hl > 1e-9 ? new Vec3d(d.X / hl, 0, d.Z / hl) : new Vec3d(0, 0, 0);
            double speed = Config.ThrowSpeed(subCells);
            return new Vec3d(h.X * Math.Cos(lifted) * speed, Math.Sin(lifted) * speed, h.Z * Math.Cos(lifted) * speed);
        }

        private long GravityPerTick => Fx(Config.ThrowGravity / (SimTime.TicksPerSecond * (double)SimTime.TicksPerSecond));

        /// <summary>The flight box and launch point for a piece: its bounds shrunk by ThrowShrink, lifted clear of its socket.</summary>
        private ThrownPiece MakeProjectile(PlayerBody p, Int3[] cells, Vec3d look, ITerrain world)
        {
            Aabb b = CellsBounds(cells, Int3.Zero);
            Vec3d c = b.Center;
            Vec3d half = (b.Max - b.Min) * 0.5;
            double s = Config.ThrowShrink;
            var t = new ThrownPiece
            {
                Owner = p.Id,
                Team = p.Team,
                SubCells = cells.Length,
                X = Fx(c.X), Y = Fx(c.Y), Z = Fx(c.Z),
                HX = Fx(Math.Max(0.15, half.X - s)), HY = Fx(Math.Max(0.15, half.Y - s)), HZ = Fx(Math.Max(0.15, half.Z - s)),
            };
            // Out of the socket: lift in sub-cell steps until the box is clear (at most one level above its own height).
            double maxLift = SubGrid.TileSize + (b.Max.Y - b.Min.Y);
            for (double lift = 0; lift <= maxLift + 1e-9; lift += SubGrid.SubSize)
            {
                long y = Fx(c.Y + lift);
                if (!world.BoxOverlapsSolid(t.BoundsAt(t.X, y, t.Z))) { t.Y = y; break; }
            }
            Vec3d v = LaunchVelocity(look, cells.Length);
            t.LaunchSpeed = v.Length;
            t.VX = Fx(v.X / SimTime.TicksPerSecond);
            t.VY = Fx(v.Y / SimTime.TicksPerSecond);
            t.VZ = Fx(v.Z / SimTime.TicksPerSecond);
            return t;
        }

        private void Launch(PlayerBody p, SculptAction a, SculptCheck c, PieceInfo info)
        {
            Int3[] cells = info.Cells.ToArray();
            Vec3d v = LaunchVelocity(a.LookDir, cells.Length);
            Vec3d kickDir = new Vec3d(v.X, 0, v.Z).Normalized;

            // Riders are pushed along the throw and drop off; the piece never carries them.
            var riders = new List<int>();
            foreach (var pl in _players)
            {
                if (!StandsOn(pl, cells)) continue;
                riders.Add(pl.Id);
                pl.Kick += kickDir * Config.ThrowRiderKick;
                Events.Add(new SculptEvent { Type = SculptEventType.PlayerDragged, Tick = Tick, PlayerId = pl.Id, Action = a, Check = c, Piece = info.Id });
            }

            Pieces.Remove(info.Id); // leaves the gap at launch
            ThrownPiece t = MakeProjectile(p, cells, a.LookDir, Grid);
            t.Id = _nextThrowId++;
            t.PieceId = info.Id;
            t.LaunchTick = Tick;
            riders.Sort();
            t.IgnorePlayers = riders.ToArray();
            _thrown.Add(t);
            Events.Add(new SculptEvent { Type = SculptEventType.Thrown, Tick = Tick, PlayerId = p.Id, Action = a, Check = c, Piece = info.Id, Value = t.Id, Point = t.Position });
        }

        private bool Ignores(ThrownPiece t, PlayerBody pl, int tick)
        {
            if (pl.Team == t.Team) return true; // no friendly fire, and never the thrower
            if (tick - t.LaunchTick <= Config.ThrowRiderIgnoreTicks)
                foreach (int id in t.IgnorePlayers) if (id == pl.Id) return true;
            return false;
        }

        /// <summary>
        /// Moves a projectile one tick in sub-steps short enough not to tunnel. Returns true on contact, with the
        /// sub-step position and the player hit (if any, lowest id).
        /// </summary>
        private bool Advance(ThrownPiece t, int tick, ITerrain world, bool hitPlayers, out PlayerBody victim)
        {
            victim = null;
            t.VY -= GravityPerTick;
            double stepMetres = Math.Sqrt((double)t.VX * t.VX + (double)t.VY * t.VY + (double)t.VZ * t.VZ) * ThrownPiece.Unit;
            int n = Math.Max(1, (int)Math.Ceiling(stepMetres / 0.15));
            long x0 = t.X, y0 = t.Y, z0 = t.Z;
            for (int i = 1; i <= n; i++)
            {
                long x = x0 + t.VX * i / n, y = y0 + t.VY * i / n, z = z0 + t.VZ * i / n;
                Aabb box = t.BoundsAt(x, y, z);
                if (hitPlayers)
                {
                    foreach (var pl in _players)
                    {
                        if (Ignores(t, pl, tick) || !pl.Bounds.Overlaps(box)) continue;
                        if (victim == null || pl.Id < victim.Id) victim = pl;
                    }
                }
                if (victim != null || world.BoxOverlapsSolid(box))
                {
                    t.X = x; t.Y = y; t.Z = z;
                    return true;
                }
            }
            t.X = x0 + t.VX; t.Y = y0 + t.VY; t.Z = z0 + t.VZ;
            return false;
        }

        private void StepThrown()
        {
            if (_thrown.Count == 0) return;
            for (int i = 0; i < _thrown.Count; i++)
            {
                ThrownPiece t = _thrown[i];
                bool hit = Advance(t, Tick, Grid, true, out PlayerBody victim);
                bool expired = Tick - t.LaunchTick > Config.ThrowMaxFlightTicks || t.Y * ThrownPiece.Unit < -30;
                if (!hit && !expired) continue;
                _thrown.RemoveAt(i);
                i--;
                if (hit) Impact(t, victim);
            }
        }

        private void Impact(ThrownPiece t, PlayerBody direct)
        {
            double impactSpeed = t.Velocity.Length;
            double factor = t.LaunchSpeed > 1e-9 ? impactSpeed / t.LaunchSpeed : 1;
            int damage = (int)Math.Round(Config.ThrowDamage(t.SubCells, factor), MidpointRounding.AwayFromZero);
            Aabb box = t.Bounds;
            Events.Add(new SculptEvent { Type = SculptEventType.ThrowImpact, Tick = Tick, PlayerId = t.Owner, Piece = t.PieceId, Value = t.Id, Point = t.Position });

            var hits = new List<KeyValuePair<PlayerBody, int>>();
            if (direct != null) hits.Add(new KeyValuePair<PlayerBody, int>(direct, damage));
            Aabb splash = box.Expand(Config.ThrowSplashRadius);
            int splashDamage = (int)Math.Round(damage * Config.ThrowSplashFraction, MidpointRounding.AwayFromZero);
            foreach (var pl in _players)
            {
                if (pl == direct || pl.Team == t.Team || !pl.Bounds.Overlaps(splash)) continue;
                hits.Add(new KeyValuePair<PlayerBody, int>(pl, splashDamage));
            }
            hits.Sort((a, b) => a.Key.Id.CompareTo(b.Key.Id));
            foreach (var h in hits)
                Events.Add(new SculptEvent
                {
                    Type = SculptEventType.ThrowHit,
                    Tick = Tick,
                    PlayerId = h.Key.Id,
                    Piece = t.PieceId,
                    Value = h.Value,
                    Disarm = Config.DisarmTicks(h.Value),
                    Point = t.Position,
                });
        }

        /// <summary>Would this throw's path (terrain only, ignoring players) pass close to the player? For the lane warning.</summary>
        private bool InThrowLane(PlayerBody sculptor, SculptAction a, SculptCheck c, PlayerBody target)
        {
            // Predict from the terrain as it will be at launch: the piece's own sub-cells gone.
            _overlay.Clear();
            foreach (var x in c.Cells) _overlay.Set(x, SubId.Empty);
            ThrownPiece t = MakeProjectile(sculptor, c.Cells, a.LookDir, _overlay);
            const double laneRadius = 1.5;
            for (int tick = 0; tick < 180; tick++)
            {
                if (t.Bounds.Expand(laneRadius).Overlaps(target.Bounds)) return true;
                if (Advance(t, tick, _overlay, false, out _)) return t.Bounds.Expand(laneRadius).Overlaps(target.Bounds);
                if (t.Y * ThrownPiece.Unit < -30) return false;
            }
            return false;
        }

        /// <summary>Predicted flight of a throw from where it stands now, for the client ghost (positions every tick until impact).</summary>
        public List<Vec3d> PredictThrowPath(PlayerBody sculptor, SculptCheck c, Vec3d look, int maxTicks = 180)
        {
            var path = new List<Vec3d>();
            if (c.Cells == null || c.Cells.Length == 0) return path;
            _overlay.Clear();
            foreach (var x in c.Cells) _overlay.Set(x, SubId.Empty);
            ThrownPiece t = MakeProjectile(sculptor, c.Cells, look, _overlay);
            path.Add(t.Position);
            for (int tick = 0; tick < maxTicks; tick++)
            {
                bool hit = Advance(t, tick, _overlay, false, out _);
                path.Add(t.Position);
                if (hit || t.Y * ThrownPiece.Unit < -30) break;
            }
            return path;
        }
    }
}
