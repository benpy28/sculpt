using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.ML
{
    /// <summary>
    /// Map knowledge a person has (they know the map): the next step of a walkable route to a goal, from the bot nav grid.
    /// Re-planned every half second (staggered by player) so 32 bots never plan on the same tick.
    /// </summary>
    public sealed class MlRoute
    {
        private readonly List<int> _path = new List<int>();
        private Vec3d _goal;
        private int _planTick = int.MinValue / 2;
        private bool _ok;
        public const int ReplanTicks = 60;

        public bool Valid => _ok;

        public void Clear() { _path.Clear(); _ok = false; _planTick = int.MinValue / 2; }

        /// <summary>The next waypoint toward <paramref name="goal"/> (at least 2.5 m along the route when it can), and the route's length left.</summary>
        public bool Next(BotNav nav, Vec3d from, Vec3d goal, int tick, int stagger, out Vec3d waypoint, out double left)
        {
            waypoint = goal; left = (goal - from).Length;
            if (nav == null) { _ok = false; return false; }
            bool moved = (goal - _goal).HorizontalLength > 3;
            if (moved || tick - _planTick >= ReplanTicks + (stagger & 7))
            {
                _planTick = tick;
                _goal = goal;
                _path.Clear();
                int a = nav.Nearest(from), b = nav.Nearest(goal, 6);
                _ok = a >= 0 && b >= 0 && nav.FindPath(a, b, _path, 20000);
            }
            if (!_ok || _path.Count == 0) return false;
            // Skip the steps already reached (the nearest point on the route), then look a little ahead.
            int near = 0; double nd = double.MaxValue;
            for (int i = 0; i < Math.Min(_path.Count, 40); i++)
            {
                double d = (nav.Feet(_path[i]) - from).LengthSquared;
                if (d < nd) { nd = d; near = i; }
            }
            if (near > 0) _path.RemoveRange(0, near);
            int k = 0;
            while (k < _path.Count - 1 && (nav.Feet(_path[k]) - from).HorizontalLength < 2.5) k++;
            waypoint = nav.Feet(_path[k]);
            left = (waypoint - from).Length + (_path.Count - 1 - k) * nav.Cell;
            return true;
        }
    }

    /// <summary>
    /// The numbers a learned bot sees each decision. Only what a player could know: its own state and bag, what its senses
    /// recorded about enemies (a reaction time late), teammates, ground items it has a clear line to, the walls around it
    /// (a ring of distance rays, like what it sees on screen), the storm and match on the HUD, and the route to a goal
    /// (it knows the map). Directions are in its own view frame (forward, right, up), so the model learns once for every
    /// heading. Every value is scaled to roughly -1..1.
    /// </summary>
    public static class MlObs
    {
        public const int EnemySlots = 4, MateSlots = 3, LootSlots = 3, RayDirs = 16;
        public const int ItemKinds = 11, HandModes = 8;
        public const double RayRange = 20;

        public const int SelfSize = 1 + 3 + 1 + 3 + 5 + 1 + 2 + ItemKinds + HandModes + 3 + Items.Slots + Items.Slots * (ItemKinds + 2);
        public const int RaySize = RayDirs * 2 + 2 + 2;
        public const int EnemySize = 4 + 3 + 2 + 2 + 2 + 1 + 2 + 7;
        public const int MateSize = 6;
        public const int HurtSize = 4;
        public const int MatchSize = 20;
        public const int RouteSize = 12;
        public const int LootSize = ItemKinds + 1 + 3 + 2;
        public const int SkillSize = 4;
        public const int PrevSize = 4;
        public const int Size = SelfSize + RaySize + EnemySlots * EnemySize + MateSlots * MateSize + HurtSize + MatchSize + RouteSize + LootSlots * LootSize + SkillSize + PrevSize;

        /// <summary>Changes whenever the layout does (a model trained on another layout is refused).</summary>
        public static uint SpecHash
        {
            get
            {
                uint h = 2166136261;
                void Mix(int v) { h = (h ^ (uint)v) * 16777619; }
                Mix(Size); Mix(2); // layout revision
                foreach (int s in MlActions.HeadSizes) Mix(s);
                Mix(MlActions.DecisionTicks);
                return h;
            }
        }

        /// <summary>Goals the bot's routes lead to, worked out by its brain (what a person would head for).</summary>
        public struct Goals
        {
            public bool HasMain, HasLoot;
            public Vec3d Main, Loot;
        }

        public sealed class Context
        {
            public readonly MlSenses Senses;
            public readonly MlRoute MainRoute = new MlRoute(), LootRoute = new MlRoute();
            public readonly List<GroundItem> Loot = new List<GroundItem>();
            public BotNav Nav;
            public int[] PrevAction = new int[MlActions.Heads];
            /// <summary>Wall distances along the 16 compass directions (knee, head), half refreshed each decision.</summary>
            public readonly float[] Rays = new float[RayDirs * 2];
            public bool RaysValid;
            public int RayPhase;
            public Context(MlSenses senses) { Senses = senses; }
        }

        public static void Build(NetServer server, ServerPlayer me, double viewYaw, double viewPitch, MlSkill skill, Context ctx, in Goals goals, float[] o)
        {
            if (o.Length < Size) throw new ArgumentException("observation buffer too small");
            int i = 0;
            MotorState s = me.State;
            MovementConfig mc = server.Movement;
            WeaponsConfig wc = mc.Weapons;
            int tick = server.Tick;
            double sy = Math.Sin(viewYaw), cy = Math.Cos(viewYaw);
            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
            void Put(double v) { o[i++] = (float)(double.IsNaN(v) ? 0 : v < -5 ? -5 : v > 5 ? 5 : v); }
            void PutBool(bool b) { o[i++] = b ? 1f : 0f; }
            double Fwd(Vec3d v) => v.X * sy + v.Z * cy;
            double Right(Vec3d v) => v.X * cy - v.Z * sy;
            void OneHot(int v, int n) { for (int k = 0; k < n; k++) o[i++] = k == v ? 1f : 0f; }

            // ---- Self.
            int start = i;
            Put(s.Health(wc) / (double)wc.MaxHealth);
            Put(Fwd(s.Velocity) / 10); Put(Right(s.Velocity) / 10); Put(s.Velocity.Y / 10);
            Put(s.Velocity.HorizontalLength / 15);
            PutBool(s.Grounded); PutBool(s.Crouched); PutBool(s.Sprinting);
            OneHot((int)s.Mode, 5);
            Put(viewPitch / 90);
            Put(s.Weapon.RecoilPitch / 5); Put(s.Weapon.RecoilYaw / 5);
            OneHot(Math.Min(ItemKinds - 1, (int)s.Hand.HeldItem), ItemKinds);
            OneHot(Math.Min(HandModes - 1, (int)s.Hand.Mode), HandModes);
            if (s.Hand.HasGun)
            {
                Weapon w = s.Hand.Held;
                Put(s.Weapon.AmmoOf(w, wc) / (double)Math.Max(1, wc.Of(w).MagSize));
            }
            else Put(0);
            PutBool(s.Weapon.Reloading);
            PutBool(s.CanShoot && s.Hand.ItemReady);
            OneHot(s.Hand.Slot, Items.Slots);
            for (int k = 0; k < Items.Slots; k++)
            {
                Item it = s.Hand.Bag[k];
                OneHot(Math.Min(ItemKinds - 1, (int)it), ItemKinds);
                Put(s.Hand.Bag.Count(k) / 6.0);
                Put((int)s.Hand.Bag.RarityOf(k) / 5.0);
            }
            Check(i - start, SelfSize);

            // ---- Walls around it: distance rays at knee and head height in 16 directions, the drop ahead, the crosshair.
            start = i;
            ITerrain t = server.Grid;
            // The rays point along 16 fixed compass directions, half of them re-cast each decision (alternately), and are
            // handed over starting from the one nearest the view, so they still read as "ahead, ahead-right, ...".
            float[] cache = ctx.Rays;
            for (int r = 0; r < RayDirs; r++)
            {
                if (ctx.RaysValid && (r & 1) != (ctx.RayPhase & 1)) continue;
                double a = r * (2 * Math.PI / RayDirs);
                var d = new Vec3d(Math.Sin(a), 0, Math.Cos(a));
                cache[r * 2] = (float)(Ray(t, s.Position + new Vec3d(0, 0.5, 0), d, RayRange) / RayRange);
                cache[r * 2 + 1] = (float)(Ray(t, s.Position + new Vec3d(0, 1.5, 0), d, RayRange) / RayRange);
            }
            ctx.RaysValid = true;
            ctx.RayPhase++;
            int shift = (int)Math.Round(viewYaw / (2 * Math.PI / RayDirs));
            for (int j = 0; j < RayDirs; j++)
            {
                int r = ((shift + j) % RayDirs + RayDirs) % RayDirs;
                Put(cache[r * 2]); Put(cache[r * 2 + 1]);
            }
            var f = new Vec3d(sy, 0, cy);
            Put(Drop(t, s.Position + f * 1.5) / 5);
            Put(Drop(t, s.Position + f * 3.0) / 5);
            Vec3d look = WeaponRules.AimDir(viewYaw, viewPitch, 0, 0);
            TerrainHit ch = t.Raycast(eye, look, 60);
            Put(ch.Hit ? ch.Distance / 60 : 1);
            TileWalls walls = TileWalls.Find(t);
            PutBool(ch.Hit && walls != null && walls.TileOfHit(ch) >= 0);
            Check(i - start, RaySize);

            // ---- Enemies it knows of (from its senses only).
            start = i;
            IReadOnlyList<MlKnown> known = ctx.Senses.Known;
            for (int e = 0; e < EnemySlots; e++)
            {
                if (e >= known.Count) { for (int k = 0; k < EnemySize; k++) o[i++] = 0; continue; }
                MlKnown k1 = known[e];
                PutBool(true);
                PutBool(k1.Seen);
                PutBool(tick - k1.LastHeardTick < SimTime.TicksPerSecond);
                Put((tick - Math.Max(k1.LastSeenTick, k1.LastHeardTick)) / (5.0 * SimTime.TicksPerSecond));
                Vec3d rel = k1.Pos - s.Position;
                Put(Fwd(rel) / 40); Put(Right(rel) / 40); Put(rel.Y / 10);
                double dist = rel.Length;
                Put(dist / 60); Put(Math.Log(1 + dist) / 4);
                double h = k1.Height > 0 ? k1.Height : 1.8;
                AimError(eye, k1.Pos + new Vec3d(0, h * 0.62, 0), viewYaw, viewPitch, out double ey, out double ep);
                AimError(eye, k1.Pos + new Vec3d(0, h - 0.17, 0), viewYaw, viewPitch, out _, out double eph);
                Put(ey / 3); Put(ey / 30);
                Put(ep / 3); Put(ep / 30);
                Put(eph / 3);
                Put(Fwd(k1.Vel) / 10); Put(Right(k1.Vel) / 10);
                PutBool(k1.Crouched);
                int held = k1.Seen && Items.IsGun(k1.Held) ? (int)k1.Held : k1.Seen && k1.Held != Item.None ? 5 : 0;
                OneHot(held, 6);
            }
            Check(i - start, EnemySlots * EnemySize);

            // ---- Teammates (a player sees them on the HUD).
            start = i;
            int mates = 0;
            foreach (ServerPlayer p in server.Players)
            {
                if (mates >= MateSlots) break;
                if (p == me || p.Team != me.Team || p.State.Dead) continue;
                if (server.Match.IsBr && !server.InBrMatch(p.Id)) continue;
                Vec3d rel = p.State.Position - s.Position;
                PutBool(true); Put(Fwd(rel) / 40); Put(Right(rel) / 40); Put(rel.Y / 10); Put(rel.Length / 60);
                Put(p.State.Health(wc) / (double)wc.MaxHealth);
                mates++;
            }
            for (; mates < MateSlots; mates++) for (int k = 0; k < MateSize; k++) o[i++] = 0;
            Check(i - start, MateSlots * MateSize);

            // ---- Hurt: when, and roughly from where (a hit marker on screen).
            start = i;
            int since = tick - me.LastHurtTick;
            PutBool(since < SimTime.TicksPerSecond);
            Put(Math.Min(since, 5 * SimTime.TicksPerSecond) / (5.0 * SimTime.TicksPerSecond));
            ServerPlayer by = since < 3 * SimTime.TicksPerSecond ? FindById(server, me.LastHurtBy) : null;
            if (by != null)
            {
                Vec3d rel = (by.State.Position - s.Position).WithY(0);
                double len = Math.Max(1e-3, rel.Length);
                Put(Fwd(rel) / len); Put(Right(rel) / len);
            }
            else { Put(0); Put(0); }
            Check(i - start, HurtSize);

            // ---- Match and storm.
            start = i;
            MatchState m = server.Match;
            PutBool(m.IsBr);
            OneHot((int)m.Phase, 5);
            Put((tick - m.PhaseStartTick) / (60.0 * SimTime.TicksPerSecond));
            Put(m.AlivePlayers / 32.0); Put(m.AliveTeams / 32.0);
            StormState st = m.Storm;
            bool storm = m.IsBr && st.Active;
            PutBool(storm);
            if (storm)
            {
                Put(st.OutsideBy(s.Position, tick) / 50);
                Put(st.RadiusAt(tick) / 300); Put(st.ToRadius / 300);
                Put((st.ShrinkStart - tick) / (60.0 * SimTime.TicksPerSecond));
                PutBool(tick >= st.ShrinkStart && tick < st.ShrinkEnd);
                Put(st.Dps / 10.0);
                var c = new Vec3d(st.ToX - s.Position.X, 0, st.ToZ - s.Position.Z);
                double len = Math.Max(1e-3, c.Length);
                Put(Fwd(c) / len); Put(Right(c) / len); Put(len / 300);
                Put((len - st.ToRadius) / 100);
            }
            else for (int k = 0; k < 10; k++) o[i++] = 0;
            Check(i - start, MatchSize);

            // ---- Routes (it knows the map): to the main goal and to loot.
            start = i;
            PutRoute(ctx.MainRoute, ctx.Nav, goals.HasMain, goals.Main);
            PutRoute(ctx.LootRoute, ctx.Nav, goals.HasLoot, goals.Loot);
            Check(i - start, RouteSize);

            // ---- Ground items it can see, nearest first (filled by the brain: ctx.Loot).
            start = i;
            for (int l = 0; l < LootSlots; l++)
            {
                if (l >= ctx.Loot.Count) { for (int k = 0; k < LootSize; k++) o[i++] = 0; continue; }
                GroundItem g = ctx.Loot[l];
                OneHot(Math.Min(ItemKinds - 1, (int)g.Kind), ItemKinds);
                Put((int)g.Rarity / 5.0);
                Vec3d rel = g.Position - s.Position;
                Put(Fwd(rel) / 25); Put(Right(rel) / 25); Put(rel.Y / 5);
                Vec3d d = g.Position + new Vec3d(0, 0.15, 0) - eye;
                double len = d.Length;
                PutBool(len < mc.Items.Reach && look.Dot(d / Math.Max(1e-4, len)) > Math.Cos(mc.Items.AimConeDegrees * Math.PI / 180));
                PutBool(Wanted(g, s.Hand.Bag, mc));
            }
            Check(i - start, LootSlots * LootSize);

            // ---- Its own limits (so one model can play every level) and what it did last.
            Put(skill.ReactionTicks / 48.0); Put(skill.AimNoiseDeg / 3); Put(skill.TurnDegPerSec / 1000); Put(skill.ViewConeDeg / 180);
            int[] pa = ctx.PrevAction;
            Put(MlActions.YawSteps[pa[(int)MlActions.Head.Yaw]] / 40);
            Put(MlActions.PitchSteps[pa[(int)MlActions.Head.Pitch]] / 10);
            Put(pa[(int)MlActions.Head.Fire]);
            Put(pa[(int)MlActions.Head.Move] / 8.0);
            Check(i, Size);

            void PutRoute(MlRoute route, BotNav nav, bool has, Vec3d goal)
            {
                if (has && route.Next(nav, s.Position, goal, tick, me.Id, out Vec3d wp, out double left))
                {
                    Vec3d rel = wp - s.Position;
                    double len = Math.Max(1e-3, rel.WithY(0).Length);
                    PutBool(true); Put(Fwd(rel) / len); Put(Right(rel) / len); Put(rel.Y / 3); Put(len / 10); Put(left / 100);
                }
                else if (has)
                {
                    // No route (off the grid): straight at it.
                    Vec3d rel = goal - s.Position;
                    double len = Math.Max(1e-3, rel.WithY(0).Length);
                    PutBool(false); Put(Fwd(rel) / len); Put(Right(rel) / len); Put(rel.Y / 3); Put(Math.Min(len, 10) / 10); Put(len / 100);
                }
                else for (int k = 0; k < 6; k++) o[i++] = 0;
            }
        }

        /// <summary>Something it would pick up: a gun it lacks with room for it, a better copy, a heal or utility with room.</summary>
        public static bool Wanted(in GroundItem g, Bag bag, MovementConfig mc)
        {
            if (g.Kind == Item.Chest) return true;
            if (Items.IsGun(g.Kind))
            {
                int have = bag.SlotOf(g.Kind);
                if (have < 0) return bag.FirstEmpty() >= 0;
                return g.Rarity > bag.RarityOf(have);
            }
            if (!Items.CanCarry(g.Kind)) return false;
            int at = bag.SlotOf(g.Kind);
            if (at >= 0) return bag.Count(at) < mc.Items.Carry(g.Kind);
            return bag.FirstEmpty() >= 0;
        }

        /// <summary>Degrees the view must turn (right, down) to look at a point.</summary>
        public static void AimError(Vec3d eye, Vec3d at, double yaw, double pitch, out double dyawDeg, out double dpitchDeg)
        {
            Vec3d d = at - eye;
            double want = Math.Atan2(d.X, d.Z);
            dyawDeg = MlSenses.Wrap(want - yaw) * 180 / Math.PI;
            double len = Math.Max(1e-6, d.Length);
            double wantPitch = -Math.Asin(Math.Max(-1, Math.Min(1, d.Y / len))) * 180 / Math.PI;
            dpitchDeg = wantPitch - pitch;
        }

        private static double Ray(ITerrain t, Vec3d from, Vec3d dir, double max)
        {
            TerrainHit h = t.Raycast(from, dir, max);
            return h.Hit ? h.Distance : max;
        }

        /// <summary>How far the ground falls away at a point (0 = level; positive = a drop; capped at 5 m).</summary>
        private static double Drop(ITerrain t, Vec3d feet)
        {
            TerrainHit h = t.Raycast(feet + new Vec3d(0, 1.0, 0), new Vec3d(0, -1, 0), 6);
            return h.Hit ? h.Distance - 1.0 : 5;
        }

        private static ServerPlayer FindById(NetServer server, int id)
        {
            foreach (ServerPlayer p in server.Players) if (p.Id == id) return p;
            return null;
        }

        private static void Check(int got, int want)
        {
            if (got != want) throw new InvalidOperationException($"observation section is {got} numbers, the layout says {want}");
        }
    }
}
