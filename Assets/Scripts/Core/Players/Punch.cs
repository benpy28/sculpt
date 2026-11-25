using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The punch (Ben 2026-11-21: its own bind, not a hotbar item; half an unreinforced wall, 30 to players). Wall damage
    /// lives in <see cref="BreachConfig"/>. Timings in seconds (Ben 2026-11-25: "too spammable", it wants a slightly
    /// longer time to land and a longer recovery, like a pickaxe swing), metres.
    /// </summary>
    public sealed class PunchConfig
    {
        /// <summary>Reach from the eye along the crosshair.</summary>
        public double Reach = 2.0;
        /// <summary>The hit lands this long after the press (the wind-up you can see; was 0.1 s).</summary>
        public double HitSeconds = 0.2;
        /// <summary>The whole swing: the gun can't fire and you can't punch again until it ends (was 0.45 s).</summary>
        public double TotalSeconds = 0.8;
        public int PlayerDamage = 30;
        /// <summary>Besides the centre ray, a ring of rays this many degrees out finds players (a punch is forgiving
        /// against players; walls only take the centre ray, so you always hit the spot under the crosshair).</summary>
        public double PlayerConeDegrees = 6;
        /// <summary>A press up to this long before the punch is ready is kept and fires when it is.</summary>
        public double BufferSeconds = 0.1;

        public int HitTicks { get => SimTime.SecondsToTicks(HitSeconds); set => HitSeconds = value / (double)SimTime.TicksPerSecond; }
        public int TotalTicks { get => Math.Max(1, SimTime.SecondsToTicks(TotalSeconds)); set => TotalSeconds = value / (double)SimTime.TicksPerSecond; }
        public int BufferTicks { get => SimTime.SecondsToTicks(BufferSeconds); set => BufferSeconds = value / (double)SimTime.TicksPerSecond; }
    }

    /// <summary>One punch landing: from the eye along the aim at the hit tick. The server resolves what it hits.</summary>
    public struct PunchSwing
    {
        public int Tick;
        public int PlayerId;
        public Vec3d Origin;
        public Vec3d Aim;
    }

    public static class PunchRules
    {
        /// <summary>True while a punch is playing (gun locked, no new punch).</summary>
        public static bool Punching(in MotorState s, int tick, PunchConfig c) => tick - s.PunchTick < c.TotalTicks;

        /// <summary>
        /// One tick of the punch, inside the player's simulation after movement and before guns: a press starts it (or is
        /// buffered), and the hit tick emits a swing into <paramref name="swings"/> (null while replaying).
        /// Not while dead, disarmed, in sculpt mode or mantling. A reload keeps running through it.
        /// </summary>
        public static void Step(ref MotorState s, int tick, bool pressed, double yaw, double pitchDegrees, MovementConfig mc, List<PunchSwing> swings)
        {
            PunchConfig c = mc.Punch;
            if (s.Dead) return;
            if (tick - s.PunchTick == c.HitTicks)
            {
                Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
                Vec3d aim = WeaponRules.AimDir(yaw, pitchDegrees, s.Weapon.RecoilPitch, s.Weapon.RecoilYaw);
                swings?.Add(new PunchSwing { Tick = tick, Origin = eye, Aim = aim });
            }

            if (pressed) s.PunchBufferTick = tick;
            if (tick - s.PunchBufferTick > c.BufferTicks) return;
            if (Punching(s, tick, c)) return;
            if (s.Hand.Mode == HandMode.Disarmed || s.Hand.InSculptMode || s.Mode == MoveMode.Mantle) return;
            s.PunchTick = tick;
            s.PunchBufferTick = int.MinValue / 2;
            s.ShootReadyTick = Math.Max(s.ShootReadyTick, s.Tick + c.TotalTicks); // on the motor's own clock (CanShoot reads s.Tick, which restarts at a respawn)
        }

        /// <summary>What a punch hit: the nearest player within reach (centre ray or the forgiving ring), else the terrain
        /// under the centre ray. <paramref name="tile"/> is the wall tile hit (-1 if none or a player was hit).</summary>
        public static BulletHit Resolve(ITerrain terrain, TileWalls walls, in PunchSwing p, PunchConfig c, IReadOnlyList<HitTarget> targets,
            int shooterTeam, bool friendlyFire, out int tile)
        {
            tile = -1;
            BulletHit centre = Hitscan.Trace(terrain, p.Origin, p.Aim, c.Reach, targets, p.PlayerId, shooterTeam, friendlyFire);
            BulletHit best = centre;
            if (centre.Kind != HitKind.Player && targets != null && targets.Count > 0 && c.PlayerConeDegrees > 0)
            {
                Basis(p.Aim, out Vec3d right, out Vec3d up);
                double t = Math.Tan(c.PlayerConeDegrees * Math.PI / 180);
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    Vec3d d = (p.Aim + right * (Math.Cos(a) * t) + up * (Math.Sin(a) * t)).Normalized;
                    BulletHit h = Hitscan.Trace(terrain, p.Origin, d, c.Reach, targets, p.PlayerId, shooterTeam, friendlyFire);
                    if (h.Kind == HitKind.Player && (best.Kind != HitKind.Player || h.Distance < best.Distance)) best = h;
                }
            }
            if (best.Kind == HitKind.Terrain && walls != null && best.Brush > 0) tile = walls.TileOfBrush(best.Brush - 1);
            return best;
        }

        /// <summary>Wall damage of one punch (reinforced wall simply has more health: four punches instead of two).</summary>
        public static int TileDamage(TileState state, BreachConfig b) => b.PunchTileDamage;

        private static void Basis(Vec3d f, out Vec3d right, out Vec3d up)
        {
            Vec3d worldUp = Math.Abs(f.Y) > 0.99 ? new Vec3d(1, 0, 0) : new Vec3d(0, 1, 0);
            right = Cross(worldUp, f).Normalized;
            up = Cross(f, right);
        }

        private static Vec3d Cross(Vec3d a, Vec3d b) => new Vec3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
