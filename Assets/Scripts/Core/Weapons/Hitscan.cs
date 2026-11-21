using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>A player as bullets see it: the collision box, with the head zone on top (what is drawn is what is hit).</summary>
    public struct HitTarget
    {
        public int Id;
        public int Team;
        public Aabb Box;
        /// <summary>Hits at or above this height are headshots.</summary>
        public double HeadMinY;

        public static HitTarget Of(int id, int team, Vec3d feet, double halfWidth, double height, WeaponsConfig cfg) => new HitTarget
        {
            Id = id,
            Team = team,
            Box = new Aabb(new Vec3d(feet.X - halfWidth, feet.Y, feet.Z - halfWidth), new Vec3d(feet.X + halfWidth, feet.Y + height, feet.Z + halfWidth)),
            HeadMinY = feet.Y + height - cfg.HeadZone(height),
        };
    }

    public enum HitKind : byte { None = 0, Terrain = 1, Player = 2 }

    public struct BulletHit
    {
        public HitKind Kind;
        public double Distance;
        public Vec3d Point;
        public Vec3d Normal;
        /// <summary>Index into the target list (Player hits).</summary>
        public int Target;
        public bool Head;
        /// <summary>Terrain hits: brush index + 1 in a building world (0 = ground or no brush).</summary>
        public int Brush;
    }

    /// <summary>
    /// Bullet tracing. Terrain uses the one chamfered surface (same as rendering and movement), so cover stops exactly
    /// what it shows. Players are boxes. Nearest hit wins; blocks stop bullets completely.
    /// </summary>
    public static class Hitscan
    {
        /// <param name="shooterTeam">With friendly fire off, teammates are skipped (bullets pass through them).</param>
        public static BulletHit Trace(ITerrain terrain, Vec3d origin, Vec3d dir, double range, IReadOnlyList<HitTarget> targets,
                                      int shooterId, int shooterTeam, bool friendlyFire)
        {
            var best = new BulletHit { Kind = HitKind.None, Distance = range, Point = origin + dir * range };
            TerrainHit th = terrain.Raycast(origin, dir, range);
            if (th.Hit)
            {
                best.Kind = HitKind.Terrain;
                best.Distance = th.Distance;
                best.Point = th.Point;
                best.Normal = th.Normal;
                best.Brush = th.Brush;
            }
            if (targets == null) return best;
            for (int i = 0; i < targets.Count; i++)
            {
                HitTarget t = targets[i];
                if (t.Id == shooterId) continue;
                if (!friendlyFire && t.Team == shooterTeam) continue;
                if (!RayBox(origin, dir, t.Box, out double d, out Vec3d n)) continue;
                if (d >= best.Distance) continue;
                Vec3d p = origin + dir * d;
                best = new BulletHit { Kind = HitKind.Player, Distance = d, Point = p, Normal = n, Target = i, Head = p.Y >= t.HeadMinY };
            }
            return best;
        }

        /// <summary>Slab test. A ray starting inside the box hits at distance 0.</summary>
        public static bool RayBox(Vec3d o, Vec3d d, Aabb b, out double dist, out Vec3d normal)
        {
            double tmin = 0, tmax = double.MaxValue;
            int axis = -1;
            double sign = 0;
            for (int a = 0; a < 3; a++)
            {
                double oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
                double da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
                double lo = a == 0 ? b.Min.X : a == 1 ? b.Min.Y : b.Min.Z;
                double hi = a == 0 ? b.Max.X : a == 1 ? b.Max.Y : b.Max.Z;
                if (Math.Abs(da) < 1e-12)
                {
                    if (oa < lo || oa > hi) { dist = 0; normal = default; return false; }
                    continue;
                }
                double t1 = (lo - oa) / da, t2 = (hi - oa) / da;
                double s = -1;
                if (t1 > t2) { double x = t1; t1 = t2; t2 = x; s = 1; }
                if (t1 > tmin) { tmin = t1; axis = a; sign = s; }
                if (t2 < tmax) tmax = t2;
                if (tmin > tmax) { dist = 0; normal = default; return false; }
            }
            dist = tmin;
            normal = axis == 0 ? new Vec3d(sign, 0, 0) : axis == 1 ? new Vec3d(0, sign, 0) : axis == 2 ? new Vec3d(0, 0, sign) : -d;
            return true;
        }

        /// <summary>
        /// Resolves one shot against terrain and players: per-pellet traces, damage per victim (falloff by distance,
        /// head pellets at head damage), rounded once per victim per shot. Results go into <paramref name="victims"/>.
        /// </summary>
        public static void Resolve(ITerrain terrain, in WeaponShot shot, WeaponStats st, WeaponsConfig cfg, int shooterTeam,
                                   IReadOnlyList<HitTarget> targets, List<ShotVictim> victims, List<BulletHit> pellets = null)
        {
            victims.Clear();
            pellets?.Clear();
            for (int i = 0; i < Math.Max(1, st.Pellets); i++)
            {
                Vec3d dir = WeaponRules.PelletDir(shot, st, i);
                BulletHit h = Trace(terrain, shot.Origin, dir, st.Range, targets, shot.ShooterId, shooterTeam, cfg.FriendlyFire);
                pellets?.Add(h);
                if (h.Kind != HitKind.Player) continue;
                double dmg = (h.Head ? st.HeadDamage : st.BodyDamage) * st.DamageFactorAt(h.Distance);
                int id = targets[h.Target].Id;
                int k = victims.FindIndex(v => v.Id == id);
                if (k < 0)
                {
                    victims.Add(new ShotVictim { Id = id, Raw = dmg, Pellets = 1, HeadPellets = h.Head ? 1 : 0, Point = h.Point });
                    continue;
                }
                ShotVictim sv = victims[k];
                sv.Raw += dmg;
                sv.Pellets++;
                if (h.Head) sv.HeadPellets++;
                victims[k] = sv;
            }
        }
    }

    /// <summary>What one shot did to one player.</summary>
    public struct ShotVictim
    {
        public int Id;
        public double Raw;
        public int Pellets;
        public int HeadPellets;
        /// <summary>Where the first pellet hit.</summary>
        public Vec3d Point;
        public int Damage => WeaponsConfig.Round(Raw);
    }
}
