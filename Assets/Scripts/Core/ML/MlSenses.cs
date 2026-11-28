using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.ML
{
    /// <summary>What a learned bot knows about one enemy: only what it saw or heard, a reaction time late.</summary>
    public struct MlKnown
    {
        public int Id;
        /// <summary>In sight in the (delayed) moment it is acting on.</summary>
        public bool Seen;
        public int LastSeenTick, LastHeardTick;
        /// <summary>Best known position (seen: exact; heard: rough) and velocity (seen only, else zero).</summary>
        public Vec3d Pos, Vel;
        public double Height;
        public bool Crouched;
        public Item Held;
    }

    /// <summary>
    /// A learned bot's eyes and ears. Every tick it records, for each enemy, whether a player in its place could see them
    /// (inside the view cone with a clear line to the head, chest or knees: terrain only, like bullets) or hear them
    /// (gunshots, footsteps by speed, being hit, the anti-hiding ping), then hands the bot only what it recorded a
    /// reaction time ago. Nothing else about enemies ever reaches the network, so it can't wallhack or react
    /// instantly. Teammates, the storm and the map are not secret (a player sees those on the HUD).
    /// </summary>
    public sealed class MlSenses
    {
        private struct Frame
        {
            public int Tick;
            public bool Seen, Heard;
            public Vec3d Pos, Vel, HeardPos;
            public double Height;
            public bool Crouched;
            public Item Held;
        }

        private sealed class Track
        {
            public readonly Frame[] Ring = new Frame[RingSize];
            public int PrevShots = -1;
            public bool SeenCarry;
            public MlKnown Known;
            public bool Alive;
        }

        private const int RingSize = 128; // ticks; must exceed the slowest reaction
        public const double HearShots = 60, HearSprint = 34, HearRun = 24, HearCrouch = 7, ForgetSeconds = 12;
        /// <summary>Sight is re-tested every this many ticks (60 times a second: cheaper, and no slower than a screen).</summary>
        public const int SightEvery = 2;

        private readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();
        private readonly List<MlKnown> _known = new List<MlKnown>();
        private readonly MlRng _rng;
        private int _lastHurtTick = int.MinValue;

        public MlSenses(ulong seed) { _rng = new MlRng(seed); }

        /// <summary>Enemies it knows of now, nearest first.</summary>
        public IReadOnlyList<MlKnown> Known => _known;

        public void Clear() { _tracks.Clear(); _known.Clear(); }

        /// <summary>Records this tick and works out what it acts on (from <paramref name="skill"/>'s reaction time ago).</summary>
        public void Update(NetServer server, ServerPlayer me, double viewYaw, MlSkill skill)
        {
            int tick = server.Tick;
            MotorState s = me.State;
            MovementConfig mc = server.Movement;
            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
            bool hurtNow = me.LastHurtTick != _lastHurtTick && me.LastHurtTick > tick - 4;
            _lastHurtTick = me.LastHurtTick;
            bool sightTick = ((tick + me.Id) % SightEvery) == 0;

            foreach (Track t in _tracks.Values) t.Alive = false;
            foreach (ServerPlayer o in server.Players)
            {
                if (o == me || o.Team == me.Team) continue;
                if (!_tracks.TryGetValue(o.Id, out Track tr)) { tr = new Track(); _tracks[o.Id] = tr; }
                tr.Alive = true;
                MotorState e = o.State;
                var f = new Frame { Tick = tick };
                int shots = e.Weapon.ShotCount;
                bool fired = tr.PrevShots >= 0 && shots != tr.PrevShots;
                tr.PrevShots = shots;
                if (!s.Dead && !e.Dead && (!server.Match.IsBr || server.InBrMatch(o.Id) || !server.InBrMatch(me.Id)))
                {
                    double h = PlayerMovement.HeightOf(e, mc);
                    Vec3d to = e.Position - s.Position;
                    double d = to.Length;
                    if (sightTick)
                    {
                        double off = Math.Abs(Wrap(Math.Atan2(to.X, to.Z) - viewYaw)) * 180 / Math.PI;
                        bool inCone = off <= skill.ViewConeDeg * 0.5 || to.HorizontalLength < 2.5;
                        tr.SeenCarry = inCone && d < 220 && CanSee(server.Grid, eye, e.Position, h);
                    }
                    if (tr.SeenCarry)
                    {
                        f.Seen = true;
                        f.Pos = e.Position;
                        f.Vel = e.Velocity;
                        f.Height = h;
                        f.Crouched = e.Crouched;
                        f.Held = e.Hand.HeldItem;
                    }
                    else
                    {
                        double hear = 0, noise = 1.5;
                        if (fired) { hear = HearShots; noise = 1.5 + d * 0.04; }
                        else if (e.Grounded)
                        {
                            double v = e.Velocity.HorizontalLength;
                            hear = v > 8 ? HearSprint : v > 5 ? HearRun : v > 1.5 ? (e.Crouched ? HearCrouch : HearRun * 0.6) : 0;
                        }
                        if (hurtNow && me.LastHurtBy == o.Id) { hear = double.MaxValue; noise = 2.0; }
                        if (server.IsRevealed(me.Id, o.Id)) { hear = double.MaxValue; noise = 0; }
                        if (d < hear)
                        {
                            f.Heard = true;
                            f.HeardPos = e.Position + new Vec3d((_rng.NextDouble() * 2 - 1) * noise, 0, (_rng.NextDouble() * 2 - 1) * noise);
                            f.Height = h;
                        }
                    }
                }
                tr.Ring[tick & (RingSize - 1)] = f;
            }

            // Act on the moment a reaction time ago.
            int then = tick - Math.Max(1, Math.Min(RingSize - 1, skill.ReactionTicks));
            int forget = (int)(ForgetSeconds * SimTime.TicksPerSecond);
            _known.Clear();
            List<int> gone = null;
            foreach (KeyValuePair<int, Track> kv in _tracks)
            {
                Track tr = kv.Value;
                if (!tr.Alive) { (gone ??= new List<int>()).Add(kv.Key); continue; }
                Frame f = tr.Ring[then & (RingSize - 1)];
                ref MlKnown k = ref tr.Known;
                if (k.Id == 0) { k.Id = kv.Key; k.LastSeenTick = k.LastHeardTick = int.MinValue / 2; }
                k.Seen = false;
                if (f.Tick == then && f.Seen)
                {
                    k.Seen = true;
                    k.LastSeenTick = then;
                    k.Pos = f.Pos; k.Vel = f.Vel; k.Height = f.Height; k.Crouched = f.Crouched; k.Held = f.Held;
                }
                else if (f.Tick == then && f.Heard)
                {
                    k.LastHeardTick = then;
                    // A sound only moves the estimate when it is newer than the last sighting.
                    if (then - k.LastSeenTick > SimTime.TicksPerSecond / 2) { k.Pos = f.HeardPos; k.Vel = Vec3d.Zero; if (k.Height <= 0) k.Height = f.Height; }
                }
                int last = Math.Max(k.LastSeenTick, k.LastHeardTick);
                if (tick - last < forget && last > int.MinValue / 2) _known.Add(k);
            }
            if (gone != null) foreach (int id in gone) _tracks.Remove(id);
            Vec3d at = s.Position;
            _known.Sort((a, b) => (a.Pos - at).LengthSquared.CompareTo((b.Pos - at).LengthSquared));
        }

        /// <summary>A clear line from the eye to the target's head, chest or knees (terrain only, like bullets).</summary>
        public static bool CanSee(ITerrain t, Vec3d eye, Vec3d feet, double height)
        {
            double a = height - 0.15, b = height * 0.6;
            return Clear(t, eye, feet, a) || Clear(t, eye, feet, b) || Clear(t, eye, feet, 0.35);
        }

        private static bool Clear(ITerrain t, Vec3d eye, Vec3d feet, double up)
        {
            Vec3d d = feet + new Vec3d(0, up, 0) - eye;
            double len = d.Length;
            if (len < 1e-3) return true;
            TerrainHit hit = t.Raycast(eye, d / len, len);
            return !hit.Hit || hit.Distance >= len - 0.05;
        }

        public static double Wrap(double a)
        {
            a = Math.IEEERemainder(a, 2 * Math.PI);
            return a;
        }
    }
}
