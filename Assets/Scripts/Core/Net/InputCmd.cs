using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// One tick of a player's input as it travels over the network. Tick is the server tick it is meant for.
    /// The client quantizes before predicting, so client and server step with bit-identical input.
    /// </summary>
    public struct InputCmd
    {
        public int Tick;
        public MoveInput Move;
        /// <summary>View pitch in degrees, -89 (up) .. 89 (down). Used by hit checks later.</summary>
        public float Pitch;
        public bool Respawn;
        /// <summary>Server tick (with fraction) the client was drawing other players at. For lag compensation (M5+).</summary>
        public int ViewTick;
        public byte ViewFrac;

        public const double MaxPitch = 89.0;
        private const int MoveSteps = 127;

        /// <summary>Rounds the command to exactly what the wire carries. Idempotent.</summary>
        public static InputCmd Quantize(InputCmd c)
        {
            c.Move.MoveX = QuantizeAxis(c.Move.MoveX);
            c.Move.MoveZ = QuantizeAxis(c.Move.MoveZ);
            c.Move.Yaw = QuantizeYaw(c.Move.Yaw);
            c.Pitch = (float)Clamp(double.IsNaN(c.Pitch) ? 0 : c.Pitch, -MaxPitch, MaxPitch);
            return c;
        }

        public static double QuantizeAxis(double v) => AxisFromByte(AxisToByte(v));

        public static sbyte AxisToByte(double v)
        {
            if (double.IsNaN(v)) return 0;
            return (sbyte)Math.Round(Clamp(v, -1, 1) * MoveSteps, MidpointRounding.AwayFromZero);
        }

        public static double AxisFromByte(sbyte b) => Math.Max(-MoveSteps, (int)b) / (double)MoveSteps;

        /// <summary>Wraps to [-pi, pi) and rounds to float precision.</summary>
        public static double QuantizeYaw(double yaw)
        {
            if (double.IsNaN(yaw) || double.IsInfinity(yaw)) return 0;
            double t = Math.IEEERemainder(yaw, 2 * Math.PI);
            float f = (float)t;
            // (float)PI is slightly above PI; keep |f| <= PI so wrapping again is a no-op (idempotent).
            if (f > Math.PI) f = BelowPi;
            else if (f < -Math.PI) f = -BelowPi;
            return f;
        }

        private const float BelowPi = 3.1415925f;

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private const byte FJumpPressed = 1, FJumpHeld = 2, FCrouchHeld = 4, FCrouchPressed = 8, FSprint = 16, FShoot = 32, FRespawn = 64;

        internal void Write(ByteWriter w)
        {
            byte f = 0;
            if (Move.JumpPressed) f |= FJumpPressed;
            if (Move.JumpHeld) f |= FJumpHeld;
            if (Move.CrouchHeld) f |= FCrouchHeld;
            if (Move.CrouchPressed) f |= FCrouchPressed;
            if (Move.SprintHeld) f |= FSprint;
            if (Move.WantsToShoot) f |= FShoot;
            if (Respawn) f |= FRespawn;
            w.U8(f);
            w.I8(AxisToByte(Move.MoveX));
            w.I8(AxisToByte(Move.MoveZ));
            w.F32((float)Move.Yaw);
            w.F32(Pitch);
            int vd = Tick - ViewTick;
            w.U8((byte)(vd < 0 ? 0 : vd > 255 ? 255 : vd));
            w.U8(ViewFrac);
        }

        /// <summary>Reads and sanitizes one command. Non-finite angles throw (the packet is dropped).</summary>
        internal static InputCmd Read(ref ByteReader r, int tick)
        {
            var c = new InputCmd { Tick = tick };
            byte f = r.U8();
            c.Move.JumpPressed = (f & FJumpPressed) != 0;
            c.Move.JumpHeld = (f & FJumpHeld) != 0;
            c.Move.CrouchHeld = (f & FCrouchHeld) != 0;
            c.Move.CrouchPressed = (f & FCrouchPressed) != 0;
            c.Move.SprintHeld = (f & FSprint) != 0;
            c.Move.WantsToShoot = (f & FShoot) != 0;
            c.Respawn = (f & FRespawn) != 0;
            c.Move.MoveX = AxisFromByte(r.I8());
            c.Move.MoveZ = AxisFromByte(r.I8());
            float yaw = r.F32();
            float pitch = r.F32();
            if (float.IsNaN(yaw) || float.IsInfinity(yaw) || float.IsNaN(pitch) || float.IsInfinity(pitch))
                throw new NetFormatException("Non-finite view angle.");
            c.Move.Yaw = Math.Abs((double)yaw) <= Math.PI ? yaw : QuantizeYaw(yaw); // honest clients are already wrapped
            c.Pitch = (float)Clamp(pitch, -MaxPitch, MaxPitch);
            c.ViewTick = tick - r.U8();
            c.ViewFrac = r.U8();
            return c;
        }

        /// <summary>The command the server assumes when one is missing: keep holding, but no new presses.</summary>
        public InputCmd AsRepeat(int tick)
        {
            InputCmd c = this;
            c.Tick = tick;
            c.Move.JumpPressed = false;
            c.Move.CrouchPressed = false;
            c.Respawn = false;
            return c;
        }
    }

    /// <summary>The one function both client prediction and the server use to advance a player by one command.</summary>
    public static class NetSim
    {
        public static void Simulate(VoxelGrid grid, MovementConfig mc, NetConfig nc, ref MotorState s, in InputCmd cmd, Vec3d spawn, List<MoveEvent> events)
        {
            if (cmd.Respawn)
            {
                Respawn(ref s, spawn, cmd.Tick, events);
                return;
            }
            PlayerMovement.Step(grid, mc, ref s, cmd.Move, events);
            if (s.Position.Y < nc.KillY) Respawn(ref s, spawn, cmd.Tick, events);
        }

        private static void Respawn(ref MotorState s, Vec3d spawn, int tick, List<MoveEvent> events)
        {
            s = MotorState.At(spawn);
            events?.Add(new MoveEvent { Type = MoveEventType.Respawned, Tick = tick });
        }
    }
}
