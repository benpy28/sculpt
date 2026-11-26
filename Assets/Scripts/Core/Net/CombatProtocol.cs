using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    public enum CombatEventType : byte
    {
        /// <summary>Someone fired (sent to everyone but the shooter, who predicted it). Pellets rebuild from the seed.</summary>
        Shot = 0,
        /// <summary>Damage landed on a player (one per victim per shot, throw hit or fall).</summary>
        Hit = 1,
        /// <summary>A player died.</summary>
        Kill = 2,
        /// <summary>A grenade, C4 or launcher round was thrown, stuck, went off or fizzled (sent to everyone but the
        /// thrower, who predicted it). Source says which kind.</summary>
        Grenade = 3,
    }

    /// <summary>What did the damage.</summary>
    public enum DamageSource : byte
    {
        Pump = 0, Smg = 1, Ar = 2, Pistol = 3,
        Throw = 4,
        Fall = 5,
        Grenade = 6,
        Punch = 7,
        C4 = 8,
        Launcher = 9,
        /// <summary>Battle royale: standing outside the circle.</summary>
        Storm = 10,
    }

    public struct CombatEvent
    {
        public CombatEventType Type;
        /// <summary>Shooter / attacker (0 = nobody, e.g. a fall).</summary>
        public int Attacker;
        /// <summary>Hit and Kill: who was hit.</summary>
        public int Victim;
        public DamageSource Source;
        // Shot
        public Vec3d Origin;
        public Vec3d Aim;
        public float Spread;
        public uint Seed;
        // Hit
        public int Damage;
        public byte Pellets;
        public byte HeadPellets;
        public Vec3d Point;
        public bool Killed;
        /// <summary>Hit: the victim's health after it (only filled in the copy sent to the attacker and victim).</summary>
        public int HealthAfter;
        // Grenade (Source = Grenade, C4 or Launcher): thrown (Origin = start, Aim = launch velocity), or exploded, stuck or
        // fizzled (Point), and the thrower's throw number.
        public GrenadeEventType Nade;
        public byte NadeSeq;

        public bool Headshot => HeadPellets > 0;

        public WeaponShot ToShot(int tick) => new WeaponShot
        {
            Tick = tick, ShooterId = Attacker, Weapon = (Weapon)Source, Origin = Origin, Aim = Aim, Spread = Spread, Seed = Seed,
        };
    }

    public struct CombatMsg
    {
        public int Tick;
        public List<CombatEvent> Events;
    }

    public static class CombatProtocol
    {
        public const int MaxEvents = 255;

        public static void Write(ByteWriter w, in CombatMsg m)
        {
            int n = m.Events.Count;
            if (n > MaxEvents) throw new System.ArgumentException("Too many combat events.");
            w.U8((byte)MsgType.Combat);
            w.I32(m.Tick);
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                CombatEvent e = m.Events[i];
                w.U8((byte)e.Type);
                w.U16((ushort)e.Attacker);
                w.U8((byte)e.Source);
                switch (e.Type)
                {
                    case CombatEventType.Shot:
                        w.VecF(e.Origin);
                        w.VecF(e.Aim);
                        w.F32(e.Spread);
                        w.I32(unchecked((int)e.Seed));
                        break;
                    case CombatEventType.Hit:
                        w.U16((ushort)e.Victim);
                        w.U16((ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, e.Damage)));
                        w.U8(e.Pellets);
                        w.U8(e.HeadPellets);
                        w.VecF(e.Point);
                        w.Bool(e.Killed);
                        w.I16((short)System.Math.Max(short.MinValue, System.Math.Min(short.MaxValue, e.HealthAfter)));
                        break;
                    case CombatEventType.Kill:
                        w.U16((ushort)e.Victim);
                        w.U8(e.HeadPellets);
                        break;
                    case CombatEventType.Grenade:
                        w.U8((byte)e.Nade);
                        w.U8(e.NadeSeq);
                        if (e.Nade == GrenadeEventType.Thrown) { w.Vec(e.Origin); w.Vec(e.Aim); }
                        else w.VecF(e.Point);
                        break;
                }
            }
        }

        private static bool Finite(Vec3d v) => !(double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z) ||
                                                 double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z));

        public static CombatMsg Read(ref ByteReader r, List<CombatEvent> into)
        {
            if (r.U8() != (byte)MsgType.Combat) throw new NetFormatException("Unexpected message type.");
            into.Clear();
            var m = new CombatMsg { Tick = r.I32(), Events = into };
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                byte t = r.U8();
                if (t > (byte)CombatEventType.Grenade) throw new NetFormatException("Bad combat event.");
                var e = new CombatEvent { Type = (CombatEventType)t, Attacker = r.U16() };
                byte src = r.U8();
                if (src > (byte)DamageSource.Storm) throw new NetFormatException("Bad damage source.");
                e.Source = (DamageSource)src;
                switch (e.Type)
                {
                    case CombatEventType.Shot:
                        if (src > (byte)DamageSource.Pistol) throw new NetFormatException("Bad weapon.");
                        e.Origin = r.VecFinite();
                        e.Aim = r.VecFinite();
                        e.Spread = r.F32();
                        if (float.IsNaN(e.Spread) || float.IsInfinity(e.Spread)) throw new NetFormatException("Bad spread.");
                        e.Seed = unchecked((uint)r.I32());
                        break;
                    case CombatEventType.Hit:
                        e.Victim = r.U16();
                        e.Damage = r.U16();
                        e.Pellets = r.U8();
                        e.HeadPellets = r.U8();
                        e.Point = r.VecFinite();
                        e.Killed = r.Bool();
                        e.HealthAfter = r.I16();
                        break;
                    case CombatEventType.Kill:
                        e.Victim = r.U16();
                        e.HeadPellets = r.U8();
                        break;
                    case CombatEventType.Grenade:
                    {
                        byte k = r.U8();
                        if (k > (byte)GrenadeEventType.Fizzled) throw new NetFormatException("Bad grenade event.");
                        if (e.Source != DamageSource.Grenade && e.Source != DamageSource.C4 && e.Source != DamageSource.Launcher)
                            throw new NetFormatException("Bad grenade kind.");
                        e.Nade = (GrenadeEventType)k;
                        e.NadeSeq = r.U8();
                        if (e.Nade == GrenadeEventType.Thrown)
                        {
                            e.Origin = r.Vec();
                            e.Aim = r.Vec();
                            if (!Finite(e.Origin) || !Finite(e.Aim)) throw new NetFormatException("Bad grenade.");
                        }
                        else e.Point = r.VecFinite();
                        break;
                    }
                }
                into.Add(e);
            }
            return m;
        }
    }
}
