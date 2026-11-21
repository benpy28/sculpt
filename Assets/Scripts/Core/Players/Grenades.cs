using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Grenade numbers (Ben 2026-11-21: slot 5, carry 2; your own grenades do no damage but give a lot of momentum,
    /// enemy grenades do damage and knock you less). Tile damage lives in <see cref="BreachConfig"/>. Values are Claude's
    /// starting tuning. Ticks at 60 Hz, metres, m/s.
    /// </summary>
    public sealed class GrenadeConfig
    {
        /// <summary>Throw speed along the look direction (plus your own velocity).</summary>
        public double ThrowSpeed = 20;
        /// <summary>The throw leaves this many degrees above the crosshair (so a level throw arcs a little).</summary>
        public double ThrowUpDegrees = 6;
        /// <summary>Share of your velocity the grenade keeps (1 = all: a throw while sliding flies further).</summary>
        public double InheritVelocity = 1.0;
        public double Gravity = 15;
        /// <summary>It goes off on the first thing it touches, or after this long in the air.</summary>
        public int MaxFuseTicks = 180;
        /// <summary>Time from a throw until the next grenade is ready.</summary>
        public int ThrowRecoveryTicks = 24;
        /// <summary>Most of your own grenades in the air at once.</summary>
        public const int MaxInFlight = 2;

        /// <summary>Players within this distance of the blast (to the chest) are affected.</summary>
        public double Radius = 4.0;
        /// <summary>Full effect inside this distance, then linear down to nothing at <see cref="Radius"/>.</summary>
        public double FullRadius = 1.0;
        /// <summary>Damage to an enemy at full effect (your own and your team's grenades never hurt you).</summary>
        public double EnemyDamage = 70;
        /// <summary>Speed added to you by your own grenade at full effect.</summary>
        public double SelfBoost = 15;
        /// <summary>Speed added to anyone else at full effect.</summary>
        public double OtherKnock = 5;
        /// <summary>Added to the push direction's height before normalising, so blasts lift a little.</summary>
        public double UpBias = 0.35;
        /// <summary>Tiles whose box is within this distance of the blast along every axis take grenade tile damage
        /// (measured per axis so the hole is always a full rectangle: 3 x 3 tiles when it lands mid-tile, 2 x 2 at a
        /// seam, never smaller or ragged).</summary>
        public double TileRadius = 1.2;
    }

    /// <summary>One of your own grenades in the air. Part of <see cref="MotorState"/>, so its flight and your own blast
    /// boost are predicted exactly like movement.</summary>
    public struct GrenadeFlight
    {
        public bool Active;
        public Vec3d Position;
        public Vec3d Velocity;
        public int ThrowTick;
        /// <summary>Per-player throw number (matches the predicted and the server's copy for drawing).</summary>
        public byte Seq;

        public bool Equals(in GrenadeFlight o) => Active == o.Active && (!Active || (Position.Equals(o.Position) &&
            Velocity.Equals(o.Velocity) && ThrowTick == o.ThrowTick && Seq == o.Seq));
    }

    public enum GrenadeEventType : byte
    {
        Thrown = 0,
        Exploded = 1,
    }

    public struct GrenadeEvent
    {
        public GrenadeEventType Type;
        public int Tick;
        public byte Seq;
        public int PlayerId;
        /// <summary>Thrown: the start point. Exploded: the blast point.</summary>
        public Vec3d Position;
        /// <summary>Thrown: the launch velocity.</summary>
        public Vec3d Velocity;
    }

    public static class GrenadeRules
    {
        public const int ThrowBufferTicks = 6;

        /// <summary>
        /// One tick of grenades for one player, inside their simulation after movement: own flights move and go off
        /// (pushing you if you are close), then a fire press with a grenade out throws one.
        /// </summary>
        public static void Step(ITerrain g, MovementConfig mc, ref MotorState s, int tick, in MoveInput input, double pitchDegrees, List<GrenadeEvent> events)
        {
            StepFlight(g, mc, ref s, ref s.Nade0, tick, events);
            StepFlight(g, mc, ref s, ref s.Nade1, tick, events);
            if (s.Dead) return;

            bool pressed = input.FirePressed || (input.FireHeld && !s.Weapon.TriggerWasHeld);
            if (s.Hand.HeldItem != Item.Grenade) return;
            if (pressed && !s.Hand.InSculptMode) s.Weapon.BufferedPressTick = tick;
            if (!s.Hand.ItemReady || s.Mode == MoveMode.Mantle) return;
            if (tick - s.Weapon.BufferedPressTick > ThrowBufferTicks) return;
            if (s.Hand.Bag.Grenades == 0) return;
            if (s.Nade0.Active && s.Nade1.Active) return;
            Throw(g, mc, ref s, tick, input.Yaw, pitchDegrees, events);
        }

        private static void Throw(ITerrain g, MovementConfig mc, ref MotorState s, int tick, double yaw, double pitchDegrees, List<GrenadeEvent> events)
        {
            GrenadeConfig c = mc.Items.Grenade;
            s.Weapon.BufferedPressTick = int.MinValue / 2;
            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
            Vec3d dir = WeaponRules.AimDir(yaw, pitchDegrees - c.ThrowUpDegrees, 0, 0);
            Vec3d vel = dir * c.ThrowSpeed + s.Velocity * c.InheritVelocity;
            s.NadeSeq++;
            var f = new GrenadeFlight { Active = true, Position = eye, Velocity = vel, ThrowTick = tick, Seq = s.NadeSeq };
            events?.Add(new GrenadeEvent { Type = GrenadeEventType.Thrown, Tick = tick, Seq = f.Seq, Position = eye, Velocity = vel });
            if (!s.Nade0.Active) s.Nade0 = f; else s.Nade1 = f;

            int slot = s.Hand.Slot;
            s.Hand.Bag.Grenades--;
            if (s.Hand.Bag.Grenades == 0)
            {
                s.Hand.Bag.Clear(slot);
                HandRules.OnHeldEmptied(ref s.Hand, tick, mc.Hand);
            }
            else
            {
                s.Hand.Mode = HandMode.Drawing;
                s.Hand.ReadyTick = tick + c.ThrowRecoveryTicks;
            }
        }

        /// <summary>Moves one flight a tick; it goes off on the first surface it touches, or when its fuse runs out.</summary>
        private static void StepFlight(ITerrain g, MovementConfig mc, ref MotorState s, ref GrenadeFlight f, int tick, List<GrenadeEvent> events)
        {
            if (!f.Active || tick <= f.ThrowTick) return; // it leaves the hand on the throw tick and starts moving next tick
            GrenadeConfig c = mc.Items.Grenade;
            double dt = mc.Dt;
            Vec3d v = f.Velocity + new Vec3d(0, -c.Gravity * dt, 0);
            Vec3d step = v * dt;
            double len = step.Length;
            Vec3d at = f.Position + step;
            bool boom = tick - f.ThrowTick >= c.MaxFuseTicks;
            if (len > 1e-9 && g != null)
            {
                TerrainHit h = g.Raycast(f.Position, step * (1.0 / len), len);
                if (h.Hit)
                {
                    at = h.Point + h.Normal * 0.05;
                    boom = true;
                }
            }
            f.Position = at;
            f.Velocity = v;
            if (!boom) return;
            f.Active = false;
            events?.Add(new GrenadeEvent { Type = GrenadeEventType.Exploded, Tick = tick, Seq = f.Seq, Position = at });
            if (s.Dead) return;
            Vec3d push = Push(g, at, s.Position, PlayerMovement.HeightOf(s, mc), c, c.SelfBoost);
            if (push.LengthSquared > 0) Knock(ref s, push);
        }

        /// <summary>0..1 strength of a blast at this distance (full inside FullRadius, linear to 0 at Radius).</summary>
        public static double Falloff(double d, GrenadeConfig c)
        {
            if (d <= c.FullRadius) return 1;
            if (d >= c.Radius) return 0;
            return 1 - (d - c.FullRadius) / (c.Radius - c.FullRadius);
        }

        /// <summary>The chest point blasts measure to.</summary>
        public static Vec3d Chest(Vec3d feet, double height) => feet + new Vec3d(0, height * 0.5, 0);

        /// <summary>
        /// Velocity a blast at <paramref name="blast"/> adds to a player (zero if out of range or a wall is in the way).
        /// Direction: blast to chest with a little lift.
        /// </summary>
        public static Vec3d Push(ITerrain g, Vec3d blast, Vec3d feet, double height, GrenadeConfig c, double strength)
        {
            Vec3d chest = Chest(feet, height);
            Vec3d d = chest - blast;
            double dist = d.Length;
            double k = Falloff(dist, c);
            if (k <= 0 || !Reaches(g, blast, feet, height)) return Vec3d.Zero;
            Vec3d dir = dist < 1e-6 ? new Vec3d(0, 1, 0) : d * (1.0 / dist);
            dir = new Vec3d(dir.X, dir.Y + c.UpBias, dir.Z).Normalized;
            return dir * (strength * k);
        }

        /// <summary>Damage a blast does to an enemy at this point (0 out of range or behind a wall).</summary>
        public static int Damage(ITerrain g, Vec3d blast, Vec3d feet, double height, GrenadeConfig c)
        {
            double k = Falloff((Chest(feet, height) - blast).Length, c);
            if (k <= 0 || !Reaches(g, blast, feet, height)) return 0;
            return WeaponsConfig.Round(c.EnemyDamage * k);
        }

        /// <summary>A blast reaches a player if its chest, head or feet can be seen from it (walls shield).</summary>
        public static bool Reaches(ITerrain g, Vec3d blast, Vec3d feet, double height)
        {
            if (g == null) return true;
            Vec3d[] pts = { Chest(feet, height), feet + new Vec3d(0, height * 0.9, 0), feet + new Vec3d(0, 0.1, 0) };
            foreach (Vec3d p in pts)
                if (ItemRules.Visible(g, blast, p)) return true;
            return false;
        }

        /// <summary>
        /// Adds a blast's velocity to a player. A push with any real lift (or any push during a vault or mantle) puts
        /// them in the air; a flat push on the ground just adds speed that the ground then bleeds off.
        /// </summary>
        public static void Knock(ref MotorState s, Vec3d dv)
        {
            s.Velocity += dv;
            bool lift = dv.Y > 1.0 || s.Mode == MoveMode.Vault || s.Mode == MoveMode.Mantle;
            if (!lift || s.Mode == MoveMode.Air) return;
            if (s.Mode == MoveMode.Vault || s.Mode == MoveMode.Mantle) s.ActionTick = 0;
            s.Mode = MoveMode.Air;
            s.Grounded = false;
            s.AirPeakY = s.Position.Y;
            s.LastGroundedTick = int.MinValue / 2; // no coyote jump off a blast
        }

        /// <summary>
        /// The tiles a blast breaks or chips: every tile whose box is within <see cref="GrenadeConfig.TileRadius"/> of the
        /// blast point (Ben: always a large, roughly square or round hole).
        /// </summary>
        public static void TilesInBlast(TileWalls walls, Vec3d blast, GrenadeConfig c, List<int> into)
        {
            into.Clear();
            if (walls == null) return;
            double r = c.TileRadius;
            var box = new Aabb(blast - new Vec3d(r, r, r), blast + new Vec3d(r, r, r));
            walls.TilesNear(box, into);
            for (int i = into.Count - 1; i >= 0; i--)
                if (AxisDistanceToBox(walls.BoundsOf(into[i]), blast) > r) into.RemoveAt(i);
            into.Sort();
        }

        /// <summary>Largest per-axis gap from a point to a box (0 inside). Using this instead of the straight-line distance
        /// keeps blast holes rectangular: a round test would leave the corner tiles of a 3 x 3 standing.</summary>
        public static double AxisDistanceToBox(Aabb b, Vec3d p)
        {
            double dx = Math.Max(0, Math.Max(b.Min.X - p.X, p.X - b.Max.X));
            double dy = Math.Max(0, Math.Max(b.Min.Y - p.Y, p.Y - b.Max.Y));
            double dz = Math.Max(0, Math.Max(b.Min.Z - p.Z, p.Z - b.Max.Z));
            return Math.Max(dx, Math.Max(dy, dz));
        }

        public static double DistanceToBox(Aabb b, Vec3d p)
        {
            double dx = Math.Max(0, Math.Max(b.Min.X - p.X, p.X - b.Max.X));
            double dy = Math.Max(0, Math.Max(b.Min.Y - p.Y, p.Y - b.Max.Y));
            double dz = Math.Max(0, Math.Max(b.Min.Z - p.Z, p.Z - b.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
