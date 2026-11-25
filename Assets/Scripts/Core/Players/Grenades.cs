using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>What a thrown or fired projectile is. Each kind has its own <see cref="GrenadeConfig"/>.</summary>
    public enum ThrowKind : byte
    {
        Grenade = 0,
        /// <summary>Remote C4: sticks to the first surface it touches, goes off when you detonate it or 8 s after it landed.</summary>
        C4 = 1,
        /// <summary>Grenade launcher round: fast and flat, but a dud if it hits something before it has armed.</summary>
        Launcher = 2,
    }

    /// <summary>
    /// Flight and blast numbers of one projectile kind (Ben 2026-11-21: slot 5, carry 2; your own grenades do no damage
    /// but give a lot of momentum, enemy grenades do damage and knock you less). Tile damage lives in
    /// <see cref="BreachConfig"/>. Values are Claude's starting tuning. Ticks at 60 Hz, metres, m/s.
    /// The plain constructor gives the hand grenade; <see cref="DefaultC4"/> and <see cref="DefaultLauncher"/> the others.
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
        public int MaxFuseTicks = 180 * SimTime.Per60;
        /// <summary>Time from a throw until the next one is ready (the launcher: between rounds).</summary>
        public int ThrowRecoveryTicks = 24 * SimTime.Per60;
        /// <summary>Most hand grenades of one player in the air at once (the old fixed limit; <see cref="MaxActive"/> is per kind).</summary>
        public const int MaxInFlight = 2;
        /// <summary>Most of this kind one player has out at once (flying or stuck).</summary>
        public int MaxActive = 2;

        /// <summary>Players within this distance of the blast (to the chest, or to the box for <see cref="MeasureToBox"/>) are affected.</summary>
        public double Radius = 4.0;
        /// <summary>Full effect inside this distance, then linear down to nothing at <see cref="Radius"/>.</summary>
        public double FullRadius = 1.0;
        /// <summary>Damage to an enemy at full effect (your own and your team's never hurt you).</summary>
        public double EnemyDamage = 70;
        /// <summary>Speed added to you by your own blast at full effect.</summary>
        public double SelfBoost = 15;
        /// <summary>Speed added to anyone else at full effect.</summary>
        public double OtherKnock = 5;
        /// <summary>Added to the push direction's height before normalising, so blasts lift a little.</summary>
        public double UpBias = 0.35;
        /// <summary>Tiles whose box is within this distance of the blast along every axis take this kind's tile damage
        /// (measured per axis so the hole is always a full rectangle: 3 x 3 tiles when it lands mid-tile, 2 x 2 at a
        /// seam, never smaller or ragged).</summary>
        public double TileRadius = 1.2;

        /// <summary>Distances are measured to the nearest point of the player's box instead of the chest (C4 on the
        /// floor: standing on it is full effect).</summary>
        public bool MeasureToBox;
        /// <summary>Sticks to the first surface it touches instead of going off (C4).</summary>
        public bool Sticks;
        /// <summary>Sticky kinds go off by themselves this long after they first landed (C4: 8 s, Ben).</summary>
        public int StuckFuseTicks = 480 * SimTime.Per60;
        /// <summary>Sticky kinds can be set off this long after the throw at the earliest (a short tell, no instant blasts).</summary>
        public int DetonateArmTicks = 15 * SimTime.Per60;
        /// <summary>A round that hits something before flying this far is a dud: no blast at all (launcher: no point-blank use).</summary>
        public double ArmDistance;
        /// <summary>Firing follows the gun rules: not while sprinting, and the sprint-to-aim delay applies (launcher).</summary>
        public bool NeedsAim;
        /// <summary>Run and sprint speed are scaled by this while the item is in hand (the launcher is heavy).</summary>
        public double MoveSpeedScale = 1.0;

        /// <summary>
        /// Remote C4 (Ben: a breaching tool for attacking a defender, not a kill weapon; it should only kill someone
        /// careless who walks over it on low health). Slow lob, sticks, small radius and at most 45 damage, but it breaks
        /// even reinforced tiles (tile damage in <see cref="BreachConfig.C4TileDamage"/>).
        /// </summary>
        public static GrenadeConfig DefaultC4() => new GrenadeConfig
        {
            ThrowSpeed = 12, ThrowUpDegrees = 8, InheritVelocity = 1.0, Gravity = 15, MaxFuseTicks = 180 * SimTime.Per60,
            ThrowRecoveryTicks = 30 * SimTime.Per60, MaxActive = 2,
            Radius = 2.5, FullRadius = 0.6, EnemyDamage = 45, SelfBoost = 12, OtherKnock = 4, UpBias = 0.35, TileRadius = 1.2,
            MeasureToBox = true, Sticks = true, StuckFuseTicks = 480 * SimTime.Per60, DetonateArmTicks = 15 * SimTime.Per60,
        };

        /// <summary>
        /// Grenade launcher (Ben: balanced, slow and clunky so you usually lose with it up close; strong for tricky plays
        /// and longer-range attacks). Fast flat rounds with a long reach, but they arm only after 8 m, one round every
        /// 1.1 s, a 0.6 s draw, no firing while sprinting and a little slower to run with.
        /// </summary>
        public static GrenadeConfig DefaultLauncher() => new GrenadeConfig
        {
            ThrowSpeed = 32, ThrowUpDegrees = 2, InheritVelocity = 0.5, Gravity = 9, MaxFuseTicks = 150 * SimTime.Per60,
            ThrowRecoveryTicks = 66 * SimTime.Per60, MaxActive = 2,
            Radius = 3.5, FullRadius = 0.75, EnemyDamage = 65, SelfBoost = 9, OtherKnock = 6, UpBias = 0.35, TileRadius = 0.8,
            ArmDistance = 8, NeedsAim = true, MoveSpeedScale = 0.9,
        };
    }

    /// <summary>One of your own projectiles out in the world. Part of <see cref="MotorState"/>, so its flight and your own
    /// blast push are predicted exactly like movement.</summary>
    public struct GrenadeFlight
    {
        public bool Active;
        public ThrowKind Kind;
        public Vec3d Position;
        public Vec3d Velocity;
        public int ThrowTick;
        /// <summary>Per-player throw number (matches the predicted and the server's copy for drawing).</summary>
        public byte Seq;
        /// <summary>Sticky kinds: resting on a surface (<see cref="Normal"/> points away from it).</summary>
        public bool Stuck;
        /// <summary>Sticky kinds: has landed at least once, at <see cref="StuckTick"/> (its fuse runs from then on).</summary>
        public bool Landed;
        public int StuckTick;
        public Vec3d Normal;
        /// <summary>Distance flown so far (arming).</summary>
        public double Travel;

        public bool Equals(in GrenadeFlight o) => Active == o.Active && (!Active || (Kind == o.Kind && Position.Equals(o.Position) &&
            Velocity.Equals(o.Velocity) && ThrowTick == o.ThrowTick && Seq == o.Seq && Stuck == o.Stuck && Landed == o.Landed &&
            StuckTick == o.StuckTick && Normal.Equals(o.Normal) && Travel.Equals(o.Travel)));
    }

    public enum GrenadeEventType : byte
    {
        Thrown = 0,
        Exploded = 1,
        /// <summary>A C4 charge stuck to a surface (Position).</summary>
        Stuck = 2,
        /// <summary>Gone without a blast: a launcher dud, or C4 whose owner died or respawned.</summary>
        Fizzled = 3,
    }

    public struct GrenadeEvent
    {
        public GrenadeEventType Type;
        public ThrowKind Kind;
        public int Tick;
        public byte Seq;
        public int PlayerId;
        /// <summary>Thrown: the start point. Exploded: the blast point. Stuck and Fizzled: where it is.</summary>
        public Vec3d Position;
        /// <summary>Thrown: the launch velocity.</summary>
        public Vec3d Velocity;
    }

    public static class GrenadeRules
    {
        public const int ThrowBufferTicks = 6 * SimTime.Per60;
        /// <summary>Flight slots per player (shared by every kind; each kind also has its own <see cref="GrenadeConfig.MaxActive"/>).</summary>
        public const int Flights = 4;
        /// <summary>A stuck charge checks this far behind it for its surface; with nothing there it drops.</summary>
        public const double StuckProbe = 0.2;

        public static ref GrenadeFlight Flight(ref MotorState s, int i)
        {
            switch (i)
            {
                case 0: return ref s.Nade0;
                case 1: return ref s.Nade1;
                case 2: return ref s.Nade2;
                default: return ref s.Nade3;
            }
        }

        public static GrenadeFlight FlightAt(in MotorState s, int i) => i == 0 ? s.Nade0 : i == 1 ? s.Nade1 : i == 2 ? s.Nade2 : s.Nade3;

        /// <summary>How many of your projectiles of this kind are out.</summary>
        public static int ActiveOf(in MotorState s, ThrowKind k)
        {
            int n = 0;
            for (int i = 0; i < Flights; i++) { GrenadeFlight f = FlightAt(s, i); if (f.Active && f.Kind == k) n++; }
            return n;
        }

        /// <summary>
        /// One tick of throwables for one player, inside their simulation after movement: own flights move, stick and go
        /// off (pushing you if you are close), then the held item acts: a fire press throws a grenade or C4 or fires a
        /// launcher round; with C4 in hand the aim button (or fire with no charge left in hand) sets off your charges.
        /// </summary>
        public static void Step(ITerrain g, MovementConfig mc, ref MotorState s, int tick, in MoveInput input, double pitchDegrees, List<GrenadeEvent> events)
        {
            for (int i = 0; i < Flights; i++) StepFlight(g, mc, ref s, ref Flight(ref s, i), tick, events);
            if (s.Dead)
            {
                Fizzle(ref s, tick, events, true); // a dead player's charges are gone (nobody holds the detonator)
                return;
            }
            ReleaseDetonator(ref s, tick, mc);

            Item held = s.Hand.HeldItem;
            if (!Items.Throws(held)) return;
            ThrowKind kind = Items.KindOf(held);
            GrenadeConfig c = mc.Items.Of(kind);
            bool sculpt = s.Hand.InSculptMode;
            bool pressed = input.FirePressed || (input.FireHeld && !s.Weapon.TriggerWasHeld);
            if (pressed && !sculpt) s.Weapon.BufferedPressTick = tick;
            bool buffered = tick - s.Weapon.BufferedPressTick <= ThrowBufferTicks;
            int count = s.Hand.Bag.Count(s.Hand.Slot);

            if (kind == ThrowKind.C4 && !sculpt && s.Hand.Mode != HandMode.Disarmed)
            {
                // The aim button always detonates; with no charge left in hand the C4 slot is the detonator, so fire does too.
                bool want = input.AimPressed || (count == 0 && buffered);
                if (want && Detonate(g, mc, ref s, tick, events) && count == 0) s.Weapon.BufferedPressTick = int.MinValue / 2;
                if (count == 0) return;
            }

            if (!s.Hand.ItemReady || s.Mode == MoveMode.Mantle || PunchRules.Punching(s, tick, mc.Punch)) return;
            if (!buffered || count <= 0) return;
            if (c.NeedsAim && !s.CanShoot) return;
            if (kind == ThrowKind.Launcher && tick < s.LauncherReadyTick) return;
            if (ActiveOf(s, kind) >= c.MaxActive) return;
            int free = -1;
            for (int i = 0; i < Flights && free < 0; i++) if (!FlightAt(s, i).Active) free = i;
            if (free < 0) return;
            Throw(mc, ref s, ref Flight(ref s, free), kind, c, count, tick, input.Yaw, pitchDegrees, events);
        }

        private static void Throw(MovementConfig mc, ref MotorState s, ref GrenadeFlight slot, ThrowKind kind, GrenadeConfig c, int count,
            int tick, double yaw, double pitchDegrees, List<GrenadeEvent> events)
        {
            s.Weapon.BufferedPressTick = int.MinValue / 2;
            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
            Vec3d dir = WeaponRules.AimDir(yaw, pitchDegrees - c.ThrowUpDegrees, 0, 0);
            Vec3d vel = dir * c.ThrowSpeed + s.Velocity * c.InheritVelocity;
            s.NadeSeq++;
            slot = new GrenadeFlight { Active = true, Kind = kind, Position = eye, Velocity = vel, ThrowTick = tick, Seq = s.NadeSeq };
            events?.Add(new GrenadeEvent { Type = GrenadeEventType.Thrown, Kind = kind, Tick = tick, Seq = slot.Seq, Position = eye, Velocity = vel });

            int held = s.Hand.Slot;
            int left = count - 1;
            s.Hand.Bag.SetCount(held, left);
            if (kind == ThrowKind.Launcher)
            {
                // The launcher stays up between rounds (no lowering): the next round is ready after its cycle.
                s.LauncherReadyTick = tick + c.ThrowRecoveryTicks;
                if (left > 0) return;
                s.Hand.Bag.Clear(held);
                HandRules.OnHeldEmptied(ref s.Hand, tick, mc.Hand);
                return;
            }
            if (left == 0 && kind == ThrowKind.Grenade)
            {
                s.Hand.Bag.Clear(held);
                HandRules.OnHeldEmptied(ref s.Hand, tick, mc.Hand);
                return;
            }
            // The next grenade or charge (or the C4 detonator) comes up after the throw.
            s.Hand.Mode = HandMode.Drawing;
            s.Hand.ReadyTick = tick + c.ThrowRecoveryTicks;
        }

        /// <summary>Sets off every one of your C4 charges that has armed. Returns true if any went off.</summary>
        public static bool Detonate(ITerrain g, MovementConfig mc, ref MotorState s, int tick, List<GrenadeEvent> events)
        {
            GrenadeConfig c = mc.Items.C4;
            bool any = false;
            for (int i = 0; i < Flights; i++)
            {
                ref GrenadeFlight f = ref Flight(ref s, i);
                if (!f.Active || f.Kind != ThrowKind.C4 || tick - f.ThrowTick < c.DetonateArmTicks) continue;
                Explode(g, mc, ref s, ref f, f.Position, tick, events);
                any = true;
            }
            return any;
        }

        /// <summary>The C4 slot at 0 charges is only the detonator: once none of your charges is out, it goes.</summary>
        private static void ReleaseDetonator(ref MotorState s, int tick, MovementConfig mc)
        {
            int k = s.Hand.Bag.SlotOf(Item.C4);
            if (k < 0 || s.Hand.Bag.Count(k) > 0 || ActiveOf(s, ThrowKind.C4) > 0) return;
            s.Hand.Bag.Clear(k);
            if (k == s.Hand.Slot) HandRules.OnHeldEmptied(ref s.Hand, tick, mc.Hand);
        }

        /// <summary>Removes your projectiles without a blast (only C4 with <paramref name="onlyC4"/>), telling everyone.</summary>
        public static void Fizzle(ref MotorState s, int tick, List<GrenadeEvent> events, bool onlyC4)
        {
            for (int i = 0; i < Flights; i++)
            {
                ref GrenadeFlight f = ref Flight(ref s, i);
                if (!f.Active || (onlyC4 && f.Kind != ThrowKind.C4)) continue;
                f.Active = false;
                events?.Add(new GrenadeEvent { Type = GrenadeEventType.Fizzled, Kind = f.Kind, Tick = tick, Seq = f.Seq, Position = f.Position });
            }
        }

        /// <summary>
        /// Moves one flight a tick. Grenades go off on the first surface they touch, launcher rounds too once armed (else
        /// they are duds), C4 sticks; every kind goes off when its fuse runs out. A stuck charge whose surface is gone
        /// (the tile broke) drops and sticks again where it lands.
        /// </summary>
        private static void StepFlight(ITerrain g, MovementConfig mc, ref MotorState s, ref GrenadeFlight f, int tick, List<GrenadeEvent> events)
        {
            if (!f.Active || tick <= f.ThrowTick) return; // it leaves the hand on the throw tick and starts moving next tick
            GrenadeConfig c = mc.Items.Of(f.Kind);
            bool fuse = f.Landed ? tick - f.StuckTick >= c.StuckFuseTicks : tick - f.ThrowTick >= c.MaxFuseTicks;
            if (f.Stuck)
            {
                if (fuse) { Explode(g, mc, ref s, ref f, f.Position, tick, events); return; }
                if (g != null && !g.Raycast(f.Position, f.Normal * -1, StuckProbe).Hit)
                {
                    f.Stuck = false;
                    f.Velocity = Vec3d.Zero;
                }
                return;
            }

            double dt = mc.Dt;
            Vec3d v = f.Velocity + new Vec3d(0, -c.Gravity * dt, 0);
            Vec3d step = v * dt;
            double len = step.Length;
            Vec3d at = f.Position + step;
            bool boom = fuse;
            if (len > 1e-9 && g != null)
            {
                TerrainHit h = g.Raycast(f.Position, step * (1.0 / len), len);
                if (h.Hit)
                {
                    at = h.Point + h.Normal * 0.05;
                    f.Travel += (h.Point - f.Position).Length;
                    if (c.Sticks)
                    {
                        f.Position = at;
                        f.Velocity = Vec3d.Zero;
                        f.Stuck = true;
                        f.Normal = h.Normal;
                        if (!f.Landed) { f.Landed = true; f.StuckTick = tick; }
                        events?.Add(new GrenadeEvent { Type = GrenadeEventType.Stuck, Kind = f.Kind, Tick = tick, Seq = f.Seq, Position = at });
                        return;
                    }
                    if (f.Travel < c.ArmDistance)
                    {
                        f.Active = false;
                        f.Position = at;
                        events?.Add(new GrenadeEvent { Type = GrenadeEventType.Fizzled, Kind = f.Kind, Tick = tick, Seq = f.Seq, Position = at });
                        return;
                    }
                    boom = true;
                }
                else f.Travel += len;
            }
            else f.Travel += len;
            f.Position = at;
            f.Velocity = v;
            if (boom) Explode(g, mc, ref s, ref f, at, tick, events);
        }

        private static void Explode(ITerrain g, MovementConfig mc, ref MotorState s, ref GrenadeFlight f, Vec3d at, int tick, List<GrenadeEvent> events)
        {
            GrenadeConfig c = mc.Items.Of(f.Kind);
            f.Active = false;
            f.Position = at;
            events?.Add(new GrenadeEvent { Type = GrenadeEventType.Exploded, Kind = f.Kind, Tick = tick, Seq = f.Seq, Position = at });
            if (s.Dead) return;
            Vec3d push = Push(g, at, s.Position, PlayerMovement.HeightOf(s, mc), c, c.SelfBoost, mc.HalfWidth);
            if (push.LengthSquared > 0) Knock(ref s, push);
        }

        /// <summary>
        /// One tick of a flight as an observer draws it (other players' throws): the same gravity, ray and stick/dud rules
        /// as <see cref="StepFlight"/>, without blasts or pushes (those come from the server). Returns true once it has
        /// come to rest (stuck, landed waiting for the server's blast, or a dud).
        /// </summary>
        public static bool ObserveStep(ITerrain g, GrenadeConfig c, double dt, ref Vec3d pos, ref Vec3d vel, ref double travel)
        {
            Vec3d v = vel + new Vec3d(0, -c.Gravity * dt, 0);
            Vec3d step = v * dt;
            double len = step.Length;
            vel = v;
            if (len <= 1e-9 || g == null) { pos += step; travel += len; return false; }
            TerrainHit h = g.Raycast(pos, step * (1.0 / len), len);
            if (!h.Hit) { pos += step; travel += len; return false; }
            travel += (h.Point - pos).Length;
            pos = h.Point + h.Normal * 0.05;
            vel = Vec3d.Zero;
            return true;
        }

        /// <summary>0..1 strength of a blast at this distance (full inside FullRadius, linear to 0 at Radius).</summary>
        public static double Falloff(double d, GrenadeConfig c)
        {
            if (d <= c.FullRadius) return 1;
            if (d >= c.Radius) return 0;
            return 1 - (d - c.FullRadius) / (c.Radius - c.FullRadius);
        }

        /// <summary>The chest point blasts aim their push at.</summary>
        public static Vec3d Chest(Vec3d feet, double height) => feet + new Vec3d(0, height * 0.5, 0);

        /// <summary>The distance a blast's falloff is measured over: to the chest, or to the nearest point of the box.</summary>
        public static double BlastDistance(Vec3d blast, Vec3d feet, double height, GrenadeConfig c, double halfWidth)
        {
            if (!c.MeasureToBox) return (Chest(feet, height) - blast).Length;
            var box = new Aabb(feet - new Vec3d(halfWidth, 0, halfWidth), feet + new Vec3d(halfWidth, height, halfWidth));
            return DistanceToBox(box, blast);
        }

        /// <summary>
        /// Velocity a blast at <paramref name="blast"/> adds to a player (zero if out of range or a wall is in the way).
        /// Direction: blast to chest with a little lift.
        /// </summary>
        public static Vec3d Push(ITerrain g, Vec3d blast, Vec3d feet, double height, GrenadeConfig c, double strength, double halfWidth = 0.35)
        {
            Vec3d chest = Chest(feet, height);
            Vec3d d = chest - blast;
            double dist = d.Length;
            double k = Falloff(BlastDistance(blast, feet, height, c, halfWidth), c);
            if (k <= 0 || !Reaches(g, blast, feet, height)) return Vec3d.Zero;
            Vec3d dir = dist < 1e-6 ? new Vec3d(0, 1, 0) : d * (1.0 / dist);
            dir = new Vec3d(dir.X, dir.Y + c.UpBias, dir.Z).Normalized;
            return dir * (strength * k);
        }

        /// <summary>Damage a blast does to an enemy at this point (0 out of range or behind a wall).</summary>
        public static int Damage(ITerrain g, Vec3d blast, Vec3d feet, double height, GrenadeConfig c, double halfWidth = 0.35)
        {
            double k = Falloff(BlastDistance(blast, feet, height, c, halfWidth), c);
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
