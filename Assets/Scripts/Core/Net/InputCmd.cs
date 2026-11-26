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
        /// <summary>Server only, never on the wire: a respawn the server itself ordered (match countdown), honoured even when dev respawns aren't.</summary>
        public bool ForcedRespawn;
        /// <summary>Dev only: put the player at the dev warp point (the movement course start).</summary>
        public bool Warp;
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
            if (c.Hand.WeaponKey > Items.Slots) c.Hand.WeaponKey = 0;
            if (!c.Hand.Pickup || c.Hand.PickId < 0) { c.Hand.Pickup = false; c.Hand.PickId = 0; }
            if (!c.Hand.Heal || c.Hand.HealTile < 0 || c.Hand.HealTile > ushort.MaxValue) { c.Hand.Heal = false; c.Hand.HealTile = 0; c.Hand.HealCell = 0; c.Hand.HealReinforce = false; }
            else c.Hand.HealCell = Math.Max(0, Math.Min(63, c.Hand.HealCell));
            if (!c.Hand.Door || c.Hand.DoorTile < 0 || c.Hand.DoorTile > ushort.MaxValue) { c.Hand.Door = false; c.Hand.DoorTile = 0; c.Hand.DoorOpen = false; }
            if (!c.Hand.Swap || c.Hand.SwapA >= Items.Slots || c.Hand.SwapB >= Items.Slots || c.Hand.SwapA == c.Hand.SwapB) { c.Hand.Swap = false; c.Hand.SwapA = 0; c.Hand.SwapB = 0; }
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
        private const byte XToggle = 1, XSculpt = 2, XReinforce = 4, XReload = 8, XWarp = 128;
        private const byte GFireHeld = 1, GFirePressed = 2, GAimPressed = 4;
        private const byte YPickup = 1, YDrop = 2, YPunch = 4, YHeal = 8, YHealReinforce = 16, YDoor = 32, YDoorOpen = 64, YSwap = 128;

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
            bool ext = Hand.SculptToggle || Hand.WeaponKey != 0 || Hand.Reload || HasSculpt || Warp || Hand.Pickup || Hand.Drop || Hand.Punch || Hand.Heal || Hand.Door || Hand.Swap;
            if (ext) f |= FExt;
            w.U8(f);
            byte g = 0;
            if (Move.FireHeld) g |= GFireHeld;
            if (Move.FirePressed) g |= GFirePressed;
            if (Move.AimPressed) g |= GAimPressed;
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
            if (Warp) x |= XWarp;
            if (HasSculpt) x |= XSculpt;
            if (HasSculpt && Sculpt.Reinforce) x |= XReinforce;
            w.U8(x);
            byte y = 0;
            if (Hand.Pickup) y |= YPickup;
            if (Hand.Drop) y |= YDrop;
            if (Hand.Punch) y |= YPunch;
            if (Hand.Heal) y |= YHeal;
            if (Hand.Heal && Hand.HealReinforce) y |= YHealReinforce;
            if (Hand.Door) y |= YDoor;
            if (Hand.Door && Hand.DoorOpen) y |= YDoorOpen;
            if (Hand.Swap) y |= YSwap;
            w.U8(y);
            if (Hand.Pickup) w.I32(Hand.PickId);
            if (Hand.Heal) { w.U16((ushort)Hand.HealTile); w.U8((byte)Hand.HealCell); }
            if (Hand.Door) w.U16((ushort)Hand.DoorTile);
            if (Hand.Swap) w.U8((byte)(Hand.SwapA | (Hand.SwapB << 4)));
            if (!HasSculpt) return;
            w.U8(SculptSeq);
            w.U8((byte)((byte)Sculpt.Type | ((byte)Sculpt.Direction << 4)));
            w.Cell(Sculpt.Target);
            w.VecF(Sculpt.AimPoint);
            if (Sculpt.Type == SculptActionType.Throw) w.VecF(Sculpt.LookDir);
        }

        /// <summary>Commands read whose view angles had to be wrapped or clamped (an honest client never sends those). The server reads it per packet.</summary>
        [ThreadStatic] internal static int Sanitized;

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
            c.Move.AimPressed = (g & GAimPressed) != 0;
            c.Move.MoveX = AxisFromByte(r.I8());
            c.Move.MoveZ = AxisFromByte(r.I8());
            float yaw = r.F32();
            float pitch = r.F32();
            if (float.IsNaN(yaw) || float.IsInfinity(yaw) || float.IsNaN(pitch) || float.IsInfinity(pitch))
                throw new NetFormatException("Non-finite view angle.");
            if (Math.Abs((double)yaw) > Math.PI || Math.Abs((double)pitch) > MaxPitch) Sanitized++;
            c.Move.Yaw = Math.Abs((double)yaw) <= Math.PI ? yaw : QuantizeYaw(yaw); // honest clients are already wrapped
            c.Pitch = (float)Clamp(pitch, -MaxPitch, MaxPitch);
            c.ViewTick = tick - r.U8();
            c.ViewFrac = r.U8();
            if ((f & FExt) == 0) return c;
            byte x = r.U8();
            c.Hand.SculptToggle = (x & XToggle) != 0;
            c.Hand.Reload = (x & XReload) != 0;
            c.Warp = (x & XWarp) != 0;
            c.Hand.WeaponKey = (byte)((x >> 4) & 7);
            if (c.Hand.WeaponKey > Items.Slots) throw new NetFormatException("Bad weapon key.");
            byte y = r.U8();
            c.Hand.Pickup = (y & YPickup) != 0;
            c.Hand.Drop = (y & YDrop) != 0;
            c.Hand.Punch = (y & YPunch) != 0;
            if (c.Hand.Pickup)
            {
                c.Hand.PickId = r.I32();
                if (c.Hand.PickId < 0) throw new NetFormatException("Bad item id.");
            }
            c.Hand.Heal = (y & YHeal) != 0;
            if (c.Hand.Heal)
            {
                c.Hand.HealReinforce = (y & YHealReinforce) != 0;
                c.Hand.HealTile = r.U16();
                c.Hand.HealCell = r.U8();
                if (c.Hand.HealCell > 63) throw new NetFormatException("Bad heal cell.");
            }
            else if ((y & YHealReinforce) != 0) throw new NetFormatException("Reinforce bit without a heal.");
            c.Hand.Door = (y & YDoor) != 0;
            if (c.Hand.Door)
            {
                c.Hand.DoorOpen = (y & YDoorOpen) != 0;
                c.Hand.DoorTile = r.U16();
            }
            else if ((y & YDoorOpen) != 0) throw new NetFormatException("Door-open bit without a door.");
            c.Hand.Swap = (y & YSwap) != 0;
            if (c.Hand.Swap)
            {
                byte ab = r.U8();
                c.Hand.SwapA = (byte)(ab & 15);
                c.Hand.SwapB = (byte)(ab >> 4);
                if (c.Hand.SwapA >= Items.Slots || c.Hand.SwapB >= Items.Slots || c.Hand.SwapA == c.Hand.SwapB) throw new NetFormatException("Bad slot swap.");
            }
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
            c.Move.AimPressed = false;
            c.Respawn = false;
            c.Warp = false;
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
        /// Order inside a tick: death/respawn timer, hand, movement (fall damage), own grenades (flights, blast push,
        /// throw), then the gun. Grenade events go into <paramref name="grenades"/> (null while replaying). Punch hits go into <paramref name="punches"/> (null while replaying). <paramref name="charge"/> breaks tiles you charge through (null: no charging). Shots fired go into
        /// <paramref name="shots"/> (null while replaying). Damage from other players is applied by the server only.
        /// </summary>
        public static void Simulate(ITerrain grid, MovementConfig mc, NetConfig nc, ref MotorState s, in InputCmd cmd, Vec3d spawn, List<MoveEvent> events, List<WeaponShot> shots = null,
            List<GrenadeEvent> grenades = null, List<PunchSwing> punches = null, IChargeBreaker charge = null)
        {
            if (cmd.Respawn && (nc.AllowDevRespawn || cmd.ForcedRespawn))
            {
                Respawn(ref s, spawn, cmd.Tick, events, grenades);
                return;
            }
            if (cmd.Warp && nc.AllowDevWarp)
            {
                Respawn(ref s, nc.DevWarpPoint, cmd.Tick, events, grenades);
                return;
            }
            if (s.Dead)
            {
                GrenadeRules.Step(grid, mc, ref s, cmd.Tick, cmd.Move, cmd.Pitch, grenades); // grenades already thrown still fly
                if (cmd.Tick >= s.RespawnTick) Respawn(ref s, spawn, cmd.Tick, events, grenades);
                return; // frozen where they fell
            }
            HandRules.Step(ref s.Hand, cmd.Tick, cmd.Hand, mc.Hand);
            if (cmd.Hand.Swap) HandRules.SwapSlots(ref s.Hand, cmd.Hand.SwapA, cmd.Hand.SwapB); // inventory screen (the hand follows its item)
            HealItemRules.Step(ref s, cmd.Tick, cmd.Move, cmd.Hand.Punch, mc); // bandages and first-aid kits
            // A quick heal press takes the gun out of action for a moment (predicted like a punch; the heal itself is
            // the server's).
            if (cmd.Hand.Heal && mc.Hand.QuickHeal && mc.Hand.CanHeal(s.Hand))
                s.ShootReadyTick = Math.Max(s.ShootReadyTick, s.Tick + 1 + mc.Hand.HealLockTicks); // motor clock: s.Tick steps to this tick in the movement below

            ChargeRules.Step(grid, mc, ref s, cmd.Tick, charge, events); // breaks the tile before this tick's movement reaches it
            List<MoveEvent> ev = events ?? (_scratch ??= new List<MoveEvent>());
            int before = ev.Count;
            MoveInput move = cmd.Move;
            if (s.Hand.Using) move.SprintHeld = false; // you walk while using a heal (PlayerMovement slows it too)
            PlayerMovement.Step(grid, mc, ref s, move, ev);
            for (int i = before; i < ev.Count; i++)
            {
                MoveEvent e = ev[i];
                if (e.Type != MoveEventType.Landed || e.Value <= 0) continue;
                if (WeaponRules.ApplyDamage(ref s, e.Value, cmd.Tick, mc.Weapons))
                    ev.Add(new MoveEvent { Type = MoveEventType.Died, Tick = cmd.Tick });
            }
            if (events == null) ev.Clear();

            if (s.Position.Y < nc.KillY) { Respawn(ref s, spawn, cmd.Tick, events, grenades); return; }
            if (s.Dead) return;
            GrenadeRules.Step(grid, mc, ref s, cmd.Tick, cmd.Move, cmd.Pitch, grenades);
            PunchRules.Step(ref s, cmd.Tick, cmd.Hand.Punch, cmd.Move.Yaw, cmd.Pitch, mc, punches);
            WeaponRules.Step(ref s, cmd.Tick, cmd.Move, cmd.Pitch, mc, shots, cmd.Hand.Reload);
        }

        private static void Respawn(ref MotorState s, Vec3d spawn, int tick, List<MoveEvent> events, List<GrenadeEvent> grenades)
        {
            GrenadeRules.Fizzle(ref s, tick, grenades, false); // anything still out is gone (observers are told)
            s = MotorState.At(spawn);
            events?.Add(new MoveEvent { Type = MoveEventType.Respawned, Tick = tick });
        }
    }
}
