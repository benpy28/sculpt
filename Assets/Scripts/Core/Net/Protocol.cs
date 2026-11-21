using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// First byte of every packet. Kept as one flat list so later traffic (grid deltas, sculpt commands,
    /// thrown-block entities, weapons) slots in as new message types without touching the movement path.
    /// </summary>
    public enum MsgType : byte
    {
        Welcome = 1,   // server -> client, reliable: who you are and where you spawn
        Inputs = 2,    // client -> server, unreliable: newest N input commands
        Snapshot = 3,  // server -> client, unreliable: authoritative state for one server tick
        World = 4,     // server -> client, reliable: piece ops and sculpt events of one tick, or the late-join state
        // Sculpt confirms ride inside Inputs (same tick, same redundancy). Thrown pieces fly deterministically from
        // their Thrown event, so they need no entity stream.
        Combat = 7,    // server -> client, reliable: shots, hits and kills of one tick
        Tiles = 8,     // server -> client, reliable: wall tile changes of one tick, or every changed tile for a late joiner
    }

    public struct WelcomeMsg
    {
        public byte ProtocolVersion;
        public int PlayerId;
        public int Team;
        public int ServerTick;
        public Vec3d Spawn;
        public double SpawnYaw;
    }

    public struct InputsMsg
    {
        /// <summary>Newest server snapshot tick the client has received (for future delta compression).</summary>
        public int AckSnapshotTick;
        /// <summary>Oldest first, consecutive ticks.</summary>
        public List<InputCmd> Cmds;
    }

    /// <summary>What every client sees of another player. Floats are plenty for drawing.</summary>
    public struct PlayerSnap
    {
        public int Id;
        public int Team;
        public Vec3d Position;
        public Vec3d Velocity;
        public float Yaw;     // radians
        public float Pitch;   // degrees
        public MoveMode Mode;
        public bool Crouched;
        public bool Dead;
        /// <summary>Gun in hand, and whether they are in sculpt mode etc. (a visible tell).</summary>
        public Weapon Held;
        public HandMode Hand;
    }

    public struct SnapshotMsg
    {
        public int ServerTick;
        public bool HasSelf;
        /// <summary>True if the server had the client's real command for this tick (false = it repeated the last one).</summary>
        public bool SelfInputWasReal;
        /// <summary>Worst lead (ticks early) of this client's commands over the feedback window. sbyte.MinValue = none arrived.</summary>
        public int SelfLead;
        public MotorState Self;
        /// <summary>The receiver's sculpt bar at this tick (basis points).</summary>
        public int SelfBar;
        public List<PlayerSnap> Others;
    }

    public static class Protocol
    {
        public const byte Version = 9;
        public const int NoLead = sbyte.MinValue;
        public const int MaxCmdsPerPacket = 32;
        public const int MaxPlayersPerSnapshot = 64;

        public static MsgType PeekType(byte[] data, int offset, int count)
        {
            if (data == null || count < 1) throw new NetFormatException("Empty packet.");
            return (MsgType)data[offset];
        }

        // ---------------------------------------------------------------- Welcome

        public static void Write(ByteWriter w, in WelcomeMsg m)
        {
            w.U8((byte)MsgType.Welcome);
            w.U8(m.ProtocolVersion);
            w.I32(m.PlayerId);
            w.I32(m.Team);
            w.I32(m.ServerTick);
            w.Vec(m.Spawn);
            w.F64(m.SpawnYaw);
        }

        public static WelcomeMsg ReadWelcome(ref ByteReader r)
        {
            Expect(ref r, MsgType.Welcome);
            return new WelcomeMsg
            {
                ProtocolVersion = r.U8(),
                PlayerId = r.I32(),
                Team = r.I32(),
                ServerTick = r.I32(),
                Spawn = r.Vec(),
                SpawnYaw = r.F64(),
            };
        }

        // ---------------------------------------------------------------- Inputs

        public static void Write(ByteWriter w, in InputsMsg m)
        {
            int n = m.Cmds.Count;
            if (n < 1 || n > MaxCmdsPerPacket) throw new System.ArgumentException("Bad command count.");
            w.U8((byte)MsgType.Inputs);
            w.I32(m.AckSnapshotTick);
            w.U8((byte)n);
            w.I32(m.Cmds[n - 1].Tick);
            for (int i = 0; i < n; i++) m.Cmds[i].Write(w);
        }

        /// <summary>Reads into the given list (cleared first). Commands come out oldest first with consecutive ticks.</summary>
        public static InputsMsg ReadInputs(ref ByteReader r, List<InputCmd> into)
        {
            Expect(ref r, MsgType.Inputs);
            var m = new InputsMsg { AckSnapshotTick = r.I32(), Cmds = into };
            into.Clear();
            int n = r.U8();
            if (n < 1 || n > MaxCmdsPerPacket) throw new NetFormatException("Bad command count.");
            int newest = r.I32();
            if (newest < 0 || newest > int.MaxValue - 1) throw new NetFormatException("Bad tick.");
            for (int i = 0; i < n; i++)
                into.Add(InputCmd.Read(ref r, newest - (n - 1) + i));
            return m;
        }

        // ---------------------------------------------------------------- Snapshot

        public static void Write(ByteWriter w, in SnapshotMsg m)
        {
            w.U8((byte)MsgType.Snapshot);
            w.I32(m.ServerTick);
            w.Bool(m.HasSelf);
            if (m.HasSelf)
            {
                w.Bool(m.SelfInputWasReal);
                int lead = m.SelfLead < sbyte.MinValue ? sbyte.MinValue : (m.SelfLead > sbyte.MaxValue ? sbyte.MaxValue : m.SelfLead);
                w.I8((sbyte)lead);
                WriteMotor(w, m.Self);
                w.U16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, m.SelfBar)));
            }
            int n = m.Others?.Count ?? 0;
            if (n > MaxPlayersPerSnapshot) throw new System.ArgumentException("Too many players.");
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                PlayerSnap p = m.Others[i];
                w.U16((ushort)p.Id);
                w.U8((byte)p.Team);
                w.VecF(p.Position);
                w.VecF(p.Velocity);
                w.F32(p.Yaw);
                w.F32(p.Pitch);
                w.U8((byte)p.Mode);
                w.U8((byte)((p.Crouched ? 1 : 0) | (p.Dead ? 2 : 0) | ((byte)p.Held << 2) | ((byte)p.Hand << 4)));
            }
        }

        public static SnapshotMsg ReadSnapshot(ref ByteReader r, List<PlayerSnap> othersInto)
        {
            Expect(ref r, MsgType.Snapshot);
            var m = new SnapshotMsg { ServerTick = r.I32(), Others = othersInto };
            othersInto.Clear();
            m.HasSelf = r.Bool();
            if (m.HasSelf)
            {
                m.SelfInputWasReal = r.Bool();
                m.SelfLead = r.I8();
                m.Self = ReadMotor(ref r);
                m.SelfBar = r.U16();
            }
            int n = r.U8();
            if (n > MaxPlayersPerSnapshot) throw new NetFormatException("Too many players.");
            for (int i = 0; i < n; i++)
            {
                var p = new PlayerSnap
                {
                    Id = r.U16(),
                    Team = r.U8(),
                    Position = r.VecF(),
                    Velocity = r.VecF(),
                    Yaw = r.F32(),
                    Pitch = r.F32(),
                    Mode = ReadMode(ref r),
                };
                byte pf = r.U8();
                p.Crouched = (pf & 1) != 0;
                p.Dead = (pf & 2) != 0;
                p.Held = (Weapon)((pf >> 2) & 3);
                int hm = pf >> 4;
                if (hm > (int)HandMode.Disarmed) throw new NetFormatException("Bad hand mode.");
                p.Hand = (HandMode)hm;
                othersInto.Add(p);
            }
            return m;
        }

        // ---------------------------------------------------------------- MotorState (exact, every field)

        public static void WriteMotor(ByteWriter w, in MotorState s)
        {
            w.Vec(s.Position);
            w.Vec(s.Velocity);
            w.U8((byte)s.Mode);
            byte f = 0;
            if (s.Crouched) f |= 1;
            if (s.Sprinting) f |= 2;
            if (s.Grounded) f |= 4;
            if (s.LandSlideUsed) f |= 8;
            if (s.Braking) f |= 16;
            w.U8(f);
            w.Vec(s.GroundNormal);
            w.I32(s.Tick);
            w.I32(s.LastGroundedTick);
            w.I32(s.JumpBufferedTick);
            w.I32(s.LastSlideBoostTick);
            w.I32(s.LandTick);
            w.Vec(s.LandVelocity);
            w.F64(s.AirPeakY);
            w.I32(s.UphillTicks);
            w.I32(s.ActionTick);
            w.F64(s.ActionStartY);
            w.F64(s.ActionTargetY);
            w.Vec(s.ActionForward);
            w.Vec(s.ActionKeepVelocity);
            w.I32(s.ShootReadyTick);
            w.I32(s.WallHitTick);
            w.F64(s.WallHitSpeed);
            w.Vec(s.WallHitNormal);
            w.I32(s.WallKicks);
            w.I32(s.LastWallKickTick);
            w.Vec(s.LastWallKickNormal);
            w.I32(s.LastSlideJumpTick);
            w.U8((byte)s.Hand.Mode);
            w.U8((byte)((byte)s.Hand.Held | (s.Hand.HeldReady ? 0x40 : 0) | (s.Hand.ExitQueued ? 0x80 : 0)));
            w.I32(s.Hand.ReadyTick);
            w.I32(s.Hand.SculptLockUntil);
            WeaponState g = s.Weapon;
            w.I64(g.NextPump); w.I64(g.NextSmg); w.I64(g.NextAr); w.I64(g.NextPistol);
            w.I32(g.LastShotTick); w.I32(g.Streak); w.I32(g.ShotCount);
            w.F64(g.Bloom); w.F64(g.RecoilPitch); w.F64(g.RecoilYaw);
            w.Bool(g.TriggerWasHeld); w.I32(g.BufferedPressTick);
            w.U8((byte)((byte)g.BloomGun | ((byte)g.ReloadGun << 2) | (g.Reloading ? 0x10 : 0)));
            w.U8(g.UsedPump); w.U8(g.UsedSmg); w.U8(g.UsedAr); w.U8(g.UsedPistol);
            w.I32(g.ReloadTick);
            w.I32(s.Damage); w.Bool(s.Dead); w.I32(s.RespawnTick);
        }

        public static MotorState ReadMotor(ref ByteReader r)
        {
            var s = new MotorState();
            s.Position = r.Vec();
            s.Velocity = r.Vec();
            s.Mode = ReadMode(ref r);
            byte f = r.U8();
            s.Crouched = (f & 1) != 0;
            s.Sprinting = (f & 2) != 0;
            s.Grounded = (f & 4) != 0;
            s.LandSlideUsed = (f & 8) != 0;
            s.Braking = (f & 16) != 0;
            s.GroundNormal = r.Vec();
            s.Tick = r.I32();
            s.LastGroundedTick = r.I32();
            s.JumpBufferedTick = r.I32();
            s.LastSlideBoostTick = r.I32();
            s.LandTick = r.I32();
            s.LandVelocity = r.Vec();
            s.AirPeakY = r.F64();
            s.UphillTicks = r.I32();
            s.ActionTick = r.I32();
            s.ActionStartY = r.F64();
            s.ActionTargetY = r.F64();
            s.ActionForward = r.Vec();
            s.ActionKeepVelocity = r.Vec();
            s.ShootReadyTick = r.I32();
            s.WallHitTick = r.I32();
            s.WallHitSpeed = r.F64();
            s.WallHitNormal = r.Vec();
            s.WallKicks = r.I32();
            s.LastWallKickTick = r.I32();
            s.LastWallKickNormal = r.Vec();
            s.LastSlideJumpTick = r.I32();
            byte hm = r.U8();
            if (hm > (byte)HandMode.Disarmed) throw new NetFormatException("Bad hand mode.");
            s.Hand.Mode = (HandMode)hm;
            byte hw = r.U8();
            if ((hw & 0x3F) > (byte)Weapon.Pistol) throw new NetFormatException("Bad weapon.");
            s.Hand.Held = (Weapon)(hw & 0x3F);
            s.Hand.HeldReady = (hw & 0x40) != 0;
            s.Hand.ExitQueued = (hw & 0x80) != 0;
            s.Hand.ReadyTick = r.I32();
            s.Hand.SculptLockUntil = r.I32();
            var g = new WeaponState
            {
                NextPump = r.I64(), NextSmg = r.I64(), NextAr = r.I64(), NextPistol = r.I64(),
                LastShotTick = r.I32(), Streak = r.I32(), ShotCount = r.I32(),
                Bloom = r.F64(), RecoilPitch = r.F64(), RecoilYaw = r.F64(),
                TriggerWasHeld = r.Bool(), BufferedPressTick = r.I32(),
            };
            byte gw = r.U8();
            g.BloomGun = (Weapon)(gw & 3);
            g.ReloadGun = (Weapon)((gw >> 2) & 3);
            g.Reloading = (gw & 0x10) != 0;
            g.UsedPump = r.U8(); g.UsedSmg = r.U8(); g.UsedAr = r.U8(); g.UsedPistol = r.U8();
            g.ReloadTick = r.I32();
            s.Weapon = g;
            s.Damage = r.I32();
            s.Dead = r.Bool();
            s.RespawnTick = r.I32();
            return s;
        }

        /// <summary>Bitwise equality of every field. Prediction is reconciled whenever this is false.</summary>
        public static bool SameMotor(in MotorState a, in MotorState b)
        {
            return a.Position.Equals(b.Position) && a.Velocity.Equals(b.Velocity) && a.Mode == b.Mode &&
                   a.Crouched == b.Crouched && a.Sprinting == b.Sprinting && a.Grounded == b.Grounded &&
                   a.LandSlideUsed == b.LandSlideUsed && a.GroundNormal.Equals(b.GroundNormal) && a.Tick == b.Tick &&
                   a.LastGroundedTick == b.LastGroundedTick && a.JumpBufferedTick == b.JumpBufferedTick &&
                   a.LastSlideBoostTick == b.LastSlideBoostTick && a.LandTick == b.LandTick &&
                   a.LandVelocity.Equals(b.LandVelocity) && a.AirPeakY.Equals(b.AirPeakY) && a.UphillTicks == b.UphillTicks && a.ActionTick == b.ActionTick &&
                   a.ActionStartY.Equals(b.ActionStartY) && a.ActionTargetY.Equals(b.ActionTargetY) &&
                   a.ActionForward.Equals(b.ActionForward) && a.ActionKeepVelocity.Equals(b.ActionKeepVelocity) &&
                   a.ShootReadyTick == b.ShootReadyTick && a.Braking == b.Braking &&
                   a.WallHitTick == b.WallHitTick && a.WallHitSpeed.Equals(b.WallHitSpeed) && a.WallHitNormal.Equals(b.WallHitNormal) &&
                   a.WallKicks == b.WallKicks && a.LastWallKickTick == b.LastWallKickTick && a.LastWallKickNormal.Equals(b.LastWallKickNormal) && a.LastSlideJumpTick == b.LastSlideJumpTick &&
                   a.Hand.Equals(b.Hand) && a.Weapon.Equals(b.Weapon) &&
                   a.Damage == b.Damage && a.Dead == b.Dead && a.RespawnTick == b.RespawnTick;
        }

        private static MoveMode ReadMode(ref ByteReader r)
        {
            byte b = r.U8();
            if (b > (byte)MoveMode.Mantle) throw new NetFormatException("Bad move mode.");
            return (MoveMode)b;
        }

        private static void Expect(ref ByteReader r, MsgType t)
        {
            if (r.U8() != (byte)t) throw new NetFormatException("Unexpected message type.");
        }
    }
}
