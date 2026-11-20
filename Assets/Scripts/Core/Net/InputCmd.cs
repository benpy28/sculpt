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
        /// <summary>Hand presses this tick: sculpt-mode toggle, weapon key.</summary>
        public HandInput Hand;
        /// <summary>A sculpt confirm this tick (the direction press). The server re-validates all of it.</summary>
        public bool HasSculpt;
        /// <summary>Client sequence number of the confirm; the server echoes it in Began, Rejected, Completed and Cancelled.</summary>
        public byte SculptSeq;
        public SculptAction Sculpt;

        public const double MaxPitch = 89.0;
        private const int MoveSteps = 127;

        /// <summary>Rounds the command to exactly what the wire carries. Idempotent.</summary>
        public static InputCmd Quantize(InputCmd c)
        {
            c.Move.MoveX = QuantizeAxis(c.Move.MoveX);
            c.Move.MoveZ = QuantizeAxis(c.Move.MoveZ);
            c.Move.Yaw = QuantizeYaw(c.Move.Yaw);
            c.Pitch = (float)Clamp(double.IsNaN(c.Pitch) ? 0 : c.Pitch, -MaxPitch, MaxPitch);
            if (c.Hand.WeaponKey > 4) c.Hand.WeaponKey = 0;
            if (c.HasSculpt && !TargetFits(c.Sculpt.Target)) c.HasSculpt = false;
            if (c.HasSculpt)
            {
                c.Sculpt.AimPoint = QuantizeVec(c.Sculpt.AimPoint);
                c.Sculpt.LookDir = c.Sculpt.Type == SculptActionType.Throw ? QuantizeVec(c.Sculpt.LookDir) : Vec3d.Zero;
                if (c.Sculpt.Type != SculptActionType.Move) c.Sculpt.Direction = Dir.East;
                if (c.Sculpt.Type == SculptActionType.Throw) c.Sculpt.Reinforce = false;
            }
            else { c.SculptSeq = 0; c.Sculpt = default; }
            return c;
        }

        /// <summary>The wire carries sculpt targets as 16-bit sub-cell coordinates.</summary>
        public static bool TargetFits(Int3 c) => Fits(c.X) && Fits(c.Y) && Fits(c.Z);
        private static bool Fits(int v) => v >= short.MinValue && v <= short.MaxValue;

        public static Vec3d QuantizeVec(Vec3d v) => new Vec3d((float)v.X, (float)v.Y, (float)v.Z);

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

        private const byte FJumpPressed = 1, FJumpHeld = 2, FCrouchHeld = 4, FCrouchPressed = 8, FSprint = 16, FShoot = 32, FRespawn = 64, FExt = 128;
        private const byte XToggle = 1, XSculpt = 2, XReinforce = 4, XReload = 8;
        private const byte GFireHeld = 1, GFirePressed = 2;

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
            bool ext = Hand.SculptToggle || Hand.WeaponKey != 0 || Hand.Reload || HasSculpt;
            if (ext) f |= FExt;
            w.U8(f);
            byte g = 0;
            if (Move.FireHeld) g |= GFireHeld;
            if (Move.FirePressed) g |= GFirePressed;
            w.U8(g);
            w.I8(AxisToByte(Move.MoveX));
            w.I8(AxisToByte(Move.MoveZ));
            w.F32((float)Move.Yaw);
            w.F32(Pitch);
            int vd = Tick - ViewTick;
            w.U8((byte)(vd < 0 ? 0 : vd > 255 ? 255 : vd));
            w.U8(ViewFrac);
            if (!ext) return;
            byte x = (byte)((Hand.WeaponKey & 7) << 4);
            if (Hand.SculptToggle) x |= XToggle;
            if (Hand.Reload) x |= XReload;
            if (HasSculpt) x |= XSculpt;
            if (HasSculpt && Sculpt.Reinforce) x |= XReinforce;
            w.U8(x);
            if (!HasSculpt) return;
            w.U8(SculptSeq);
            w.U8((byte)((byte)Sculpt.Type | ((byte)Sculpt.Direction << 4)));
            w.Cell(Sculpt.Target);
            w.VecF(Sculpt.AimPoint);
            if (Sculpt.Type == SculptActionType.Throw) w.VecF(Sculpt.LookDir);
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
            byte g = r.U8();
            c.Move.FireHeld = (g & GFireHeld) != 0;
            c.Move.FirePressed = (g & GFirePressed) != 0;
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
            if ((f & FExt) == 0) return c;
            byte x = r.U8();
            c.Hand.SculptToggle = (x & XToggle) != 0;
            c.Hand.Reload = (x & XReload) != 0;
            c.Hand.WeaponKey = (byte)((x >> 4) & 7);
            if (c.Hand.WeaponKey > 4) throw new NetFormatException("Bad weapon key.");
            if ((x & XSculpt) == 0) return c;
            c.HasSculpt = true;
            c.SculptSeq = r.U8();
            byte td = r.U8();
            var type = (SculptActionType)(td & 15);
            var dir = (Dir)(td >> 4);
            if (type > SculptActionType.Throw || dir > Dir.South) throw new NetFormatException("Bad sculpt action.");
            c.Sculpt = new SculptAction { Type = type, Direction = dir, Reinforce = (x & XReinforce) != 0, Target = r.Cell(), AimPoint = r.VecFinite() };
            if (type == SculptActionType.Throw) c.Sculpt.LookDir = r.VecFinite();
            return c;
        }

        /// <summary>The command the server assumes when one is missing: keep holding, but no new presses.</summary>
        public InputCmd AsRepeat(int tick)
        {
            InputCmd c = this;
            c.Tick = tick;
            c.Move.JumpPressed = false;
            c.Move.CrouchPressed = false;
            c.Move.FirePressed = false;
            c.Respawn = false;
            c.Hand = default;
            c.HasSculpt = false;
            c.SculptSeq = 0;
            c.Sculpt = default;
            return c;
        }
    }

    /// <summary>The one function both client prediction and the server use to advance a player by one command.</summary>
    public static class NetSim
    {
        [ThreadStatic] private static List<MoveEvent> _scratch;

        /// <summary>
        /// Order inside a tick: death/respawn timer, hand, movement (fall damage), then the gun. Shots fired go into
        /// <paramref name="shots"/> (null while replaying). Damage from other players is applied by the server only.
        /// </summary>
        public static void Simulate(ITerrain grid, MovementConfig mc, NetConfig nc, ref MotorState s, in InputCmd cmd, Vec3d spawn, List<MoveEvent> events, List<WeaponShot> shots = null)
        {
            if (cmd.Respawn)
            {
                Respawn(ref s, spawn, cmd.Tick, events);
                return;
            }
            if (s.Dead)
            {
                if (cmd.Tick >= s.RespawnTick) Respawn(ref s, spawn, cmd.Tick, events);
                return; // frozen where they fell
            }
            HandRules.Step(ref s.Hand, cmd.Tick, cmd.Hand, mc.Hand);

            List<MoveEvent> ev = events ?? (_scratch ??= new List<MoveEvent>());
            int before = ev.Count;
            PlayerMovement.Step(grid, mc, ref s, cmd.Move, ev);
            for (int i = before; i < ev.Count; i++)
            {
                MoveEvent e = ev[i];
                if (e.Type != MoveEventType.Landed || e.Value <= 0) continue;
                if (WeaponRules.ApplyDamage(ref s, e.Value, cmd.Tick, mc.Weapons))
                    ev.Add(new MoveEvent { Type = MoveEventType.Died, Tick = cmd.Tick });
            }
            if (events == null) ev.Clear();

            if (s.Position.Y < nc.KillY) { Respawn(ref s, spawn, cmd.Tick, events); return; }
            if (s.Dead) return;
            WeaponRules.Step(ref s, cmd.Tick, cmd.Move, cmd.Pitch, mc, shots, cmd.Hand.Reload);
        }

        private static void Respawn(ref MotorState s, Vec3d spawn, int tick, List<MoveEvent> events)
        {
            s = MotorState.At(spawn);
            events?.Add(new MoveEvent { Type = MoveEventType.Respawned, Tick = tick });
        }
    }
}
