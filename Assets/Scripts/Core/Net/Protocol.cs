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
        // Reserved: GridDelta = 4, SculptCommand = 5, Entities = 6, WeaponEvents = 7
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
        public List<PlayerSnap> Others;
    }

    public static class Protocol
    {
        public const byte Version = 1;
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
                w.Bool(p.Crouched);
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
                    Crouched = r.Bool(),
                };
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
            w.U8(f);
            w.Vec(s.GroundNormal);
            w.I32(s.Tick);
            w.I32(s.LastGroundedTick);
            w.I32(s.JumpBufferedTick);
            w.I32(s.LastSlideBoostTick);
            w.I32(s.LandTick);
            w.Vec(s.LandVelocity);
            w.F64(s.AirPeakY);
            w.I32(s.ActionTick);
            w.F64(s.ActionStartY);
            w.F64(s.ActionTargetY);
            w.Vec(s.ActionForward);
            w.Vec(s.ActionKeepVelocity);
            w.I32(s.ShootReadyTick);
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
            s.GroundNormal = r.Vec();
            s.Tick = r.I32();
            s.LastGroundedTick = r.I32();
            s.JumpBufferedTick = r.I32();
            s.LastSlideBoostTick = r.I32();
            s.LandTick = r.I32();
            s.LandVelocity = r.Vec();
            s.AirPeakY = r.F64();
            s.ActionTick = r.I32();
            s.ActionStartY = r.F64();
            s.ActionTargetY = r.F64();
            s.ActionForward = r.Vec();
            s.ActionKeepVelocity = r.Vec();
            s.ShootReadyTick = r.I32();
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
                   a.LandVelocity.Equals(b.LandVelocity) && a.AirPeakY.Equals(b.AirPeakY) && a.ActionTick == b.ActionTick &&
                   a.ActionStartY.Equals(b.ActionStartY) && a.ActionTargetY.Equals(b.ActionTargetY) &&
                   a.ActionForward.Equals(b.ActionForward) && a.ActionKeepVelocity.Equals(b.ActionKeepVelocity) &&
                   a.ShootReadyTick == b.ShootReadyTick;
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
