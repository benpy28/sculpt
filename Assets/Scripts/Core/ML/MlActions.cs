using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.ML
{
    /// <summary>
    /// What a learned bot can do each decision (30 a second), as independent choices ("heads"). These are the same keys
    /// and mouse a person has: move keys, a mouse turn (in steps from a nudge to a flick), fire, jump, crouch, sprint, a
    /// slot key, and one of reload / interact / punch / aim.
    /// </summary>
    public static class MlActions
    {
        public const int DecisionTicks = 4; // 30 decisions a second at 120 Hz

        public enum Head { Move = 0, Yaw = 1, Pitch = 2, Fire = 3, Jump = 4, Crouch = 5, Sprint = 6, Slot = 7, Misc = 8 }
        public enum Misc { None = 0, Reload = 1, Interact = 2, Punch = 3, Aim = 4 }

        /// <summary>Mouse turn per decision, degrees (right / down positive).</summary>
        public static readonly double[] YawSteps = { 0, 0.3, -0.3, 1, -1, 2.5, -2.5, 6, -6, 15, -15, 40, -40 };
        public static readonly double[] PitchSteps = { 0, 0.3, -0.3, 1, -1, 3, -3, 10, -10 };
        public static readonly int[] HeadSizes = { 9, YawSteps.Length, PitchSteps.Length, 2, 2, 2, 2, Items.Slots + 1, 5 };
        public static readonly int Heads = HeadSizes.Length;
        public static readonly int TotalLogits = Sum(HeadSizes);

        private static int Sum(int[] a) { int s = 0; foreach (int v in a) s += v; return s; }

        public static int Offset(int head) { int o = 0; for (int i = 0; i < head; i++) o += HeadSizes[i]; return o; }

        /// <summary>Move head index -> (strafe, forward), each -1, 0 or 1.</summary>
        public static void MoveOf(int a, out int x, out int z) { x = a % 3 - 1; z = a / 3 - 1; }
        public static int MoveIndex(int x, int z) => (z + 1) * 3 + (x + 1);

        /// <summary>The nearest step to a turn (for labelling a teacher's turns).</summary>
        public static int Nearest(double[] steps, double v)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < steps.Length; i++) { double d = Math.Abs(steps[i] - v); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        /// <summary>
        /// The action a teacher's four commands amount to (behaviour cloning from the scripted bot, or later from a person's
        /// replay): its turn over the decision, the move keys it mostly held, any press.
        /// </summary>
        public static void FromCommands(double yawBefore, double pitchBefore, IReadOnlyList<InputCmd> cmds, int[] into)
        {
            InputCmd last = cmds[cmds.Count - 1];
            double dyaw = MlSenses.Wrap(last.Move.Yaw - yawBefore) * 180 / Math.PI;
            double dpitch = last.Pitch - pitchBefore;
            double mx = 0, mz = 0;
            bool fire = false, jump = false, crouch = false; int sprint = 0;
            int slot = 0; Misc misc = Misc.None;
            foreach (InputCmd c in cmds)
            {
                mx += c.Move.MoveX; mz += c.Move.MoveZ;
                fire |= c.Move.FireHeld;
                jump |= c.Move.JumpPressed;
                crouch |= c.Move.CrouchHeld;
                if (c.Move.SprintHeld) sprint++;
                if (c.Hand.WeaponKey != 0) slot = c.Hand.WeaponKey;
                if (c.Hand.Reload) misc = Misc.Reload;
                else if ((c.Hand.Pickup || c.Hand.Door) && misc == Misc.None) misc = Misc.Interact;
                else if (c.Hand.Punch && misc == Misc.None) misc = Misc.Punch;
                else if (c.Move.AimPressed && misc == Misc.None) misc = Misc.Aim;
            }
            mx /= cmds.Count; mz /= cmds.Count;
            into[(int)Head.Move] = MoveIndex(mx > 0.33 ? 1 : mx < -0.33 ? -1 : 0, mz > 0.33 ? 1 : mz < -0.33 ? -1 : 0);
            into[(int)Head.Yaw] = Nearest(YawSteps, dyaw);
            into[(int)Head.Pitch] = Nearest(PitchSteps, dpitch);
            into[(int)Head.Fire] = fire ? 1 : 0;
            into[(int)Head.Jump] = jump ? 1 : 0;
            into[(int)Head.Crouch] = crouch ? 1 : 0;
            into[(int)Head.Sprint] = sprint * 2 > cmds.Count ? 1 : 0;
            into[(int)Head.Slot] = slot;
            into[(int)Head.Misc] = (int)misc;
        }
    }

    /// <summary>
    /// The bot's hands: turns one decision into four tick commands the way a person's hand would. The turn is spread
    /// over the decision and capped at the skill's turn speed, a slow smooth wobble rides on the aim, presses go out on
    /// the first tick. Recoil is not compensated for it: the network sees its recoil and has to pull down itself.
    /// </summary>
    public sealed class MlHands
    {
        private double _yaw, _pitch;
        private double _wobYaw, _wobPitch, _wobYawV, _wobPitchV;
        private bool _firedLast, _crouchLast;
        private readonly int[] _act = new int[MlActions.Heads];
        private int _step;
        private readonly MlRng _rng;
        private readonly List<int> _near = new List<int>();

        public MlHands(ulong seed) { _rng = new MlRng(seed); }

        /// <summary>Where it is looking, before the wobble (radians, degrees).</summary>
        public double Yaw => _yaw;
        public double Pitch => _pitch;
        public IReadOnlyList<int> Action => _act;

        public void Reset(double yaw, double pitch) { _yaw = yaw; _pitch = pitch; _wobYaw = _wobPitch = _wobYawV = _wobPitchV = 0; _firedLast = false; _step = MlActions.DecisionTicks; }

        public void Set(int[] action) { Array.Copy(action, _act, _act.Length); _step = 0; }

        /// <summary>This tick's command from the current decision.</summary>
        public InputCmd Tick(NetServer server, ServerPlayer me, MlSkill skill)
        {
            MotorState s = me.State;
            MovementConfig mc = server.Movement;
            bool first = _step == 0;
            int n = MlActions.DecisionTicks;
            var cmd = new InputCmd { Tick = server.Tick, ViewTick = server.Tick };
            if (_step >= n) { _act[(int)MlActions.Head.Fire] = 0; _act[(int)MlActions.Head.Jump] = 0; }

            double cap = skill.TurnDegPerSec / SimTime.TicksPerSecond;
            double dyaw = MlActions.YawSteps[_act[(int)MlActions.Head.Yaw]] / n;
            double dpitch = MlActions.PitchSteps[_act[(int)MlActions.Head.Pitch]] / n;
            if (_step >= n) { dyaw = 0; dpitch = 0; }
            dyaw = Math.Max(-cap, Math.Min(cap, dyaw));
            dpitch = Math.Max(-cap, Math.Min(cap, dpitch));
            _yaw = MlSenses.Wrap(_yaw + dyaw * Math.PI / 180);
            _pitch = Math.Max(-85, Math.Min(85, _pitch + dpitch));

            // A slow, smooth hand wobble (a damped random walk, about a third of a second long).
            double k = 1.0 / (0.3 * SimTime.TicksPerSecond);
            double sig = skill.AimNoiseDeg * Math.Sqrt(2 * k); // the walk's spread settles at AimNoiseDeg
            _wobYawV += -k * _wobYawV + sig * _rng.Gauss(); _wobYaw += (_wobYawV - _wobYaw) * k * 3;
            _wobPitchV += -k * _wobPitchV + sig * 0.6 * _rng.Gauss(); _wobPitch += (_wobPitchV - _wobPitch) * k * 3;

            MlActions.MoveOf(_act[(int)MlActions.Head.Move], out int mx, out int mz);
            cmd.Move.MoveX = mx;
            cmd.Move.MoveZ = mz;
            bool fire = _act[(int)MlActions.Head.Fire] == 1;
            bool jump = _act[(int)MlActions.Head.Jump] == 1;
            bool crouch = _act[(int)MlActions.Head.Crouch] == 1;
            cmd.Move.FireHeld = fire;
            cmd.Move.FirePressed = fire && !_firedLast;
            cmd.Move.WantsToShoot = fire;
            _firedLast = fire;
            cmd.Move.JumpPressed = jump && first;
            cmd.Move.JumpHeld = jump;
            cmd.Move.CrouchHeld = crouch;
            cmd.Move.CrouchPressed = crouch && !_crouchLast;
            _crouchLast = crouch;
            cmd.Move.SprintHeld = _act[(int)MlActions.Head.Sprint] == 1 && !fire;
            if (first)
            {
                int slot = _act[(int)MlActions.Head.Slot];
                if (slot > 0 && slot - 1 != s.Hand.Slot) cmd.Hand.WeaponKey = (byte)slot;
                switch ((MlActions.Misc)_act[(int)MlActions.Head.Misc])
                {
                    case MlActions.Misc.Reload: cmd.Hand.Reload = true; break;
                    case MlActions.Misc.Punch: cmd.Hand.Punch = true; break;
                    case MlActions.Misc.Aim: cmd.Move.AimPressed = true; break;
                    case MlActions.Misc.Interact: Interact(server, me, ref cmd); break;
                }
            }
            cmd.Move.Yaw = _yaw + _wobYaw * Math.PI / 180;
            cmd.Pitch = (float)Math.Max(-InputCmd.MaxPitch, Math.Min(InputCmd.MaxPitch, _pitch + _wobPitch));
            _step++;
            return InputCmd.Quantize(cmd);
        }

        /// <summary>Interact like a person pressing E: the ground item nearest the crosshair in reach, else a door in front.</summary>
        private void Interact(NetServer server, ServerPlayer me, ref InputCmd cmd)
        {
            MotorState s = me.State;
            MovementConfig mc = server.Movement;
            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
            Vec3d look = WeaponRules.AimDir(_yaw, _pitch, 0, 0);
            ItemConfig ic = mc.Items;
            double cosCone = Math.Cos(ic.AimConeDegrees * Math.PI / 180);
            int best = -1; double bestDot = cosCone;
            GroundItems items = server.Items;
            for (int i = 0; i < items.Count; i++)
            {
                GroundItem g = items[i];
                Vec3d d = g.Position + new Vec3d(0, 0.15, 0) - eye;
                double len = d.Length;
                if (len > ic.Reach || len < 1e-4) continue;
                double dot = look.Dot(d / len);
                if (dot > bestDot) { bestDot = dot; best = g.Id; }
            }
            if (best >= 0) { cmd.Hand.Pickup = true; cmd.Hand.PickId = best; return; }
            TileWalls walls = TileWalls.Find(server.Grid);
            if (walls == null) return;
            Vec3d f = new Vec3d(Math.Sin(_yaw), 0, Math.Cos(_yaw));
            Vec3d p = s.Position + f * 0.9;
            var box = new Aabb(new Vec3d(p.X - 0.4, s.Position.Y + 0.5, p.Z - 0.4), new Vec3d(p.X + 0.4, s.Position.Y + 1.6, p.Z + 0.4));
            walls.TilesNear(box, _near);
            foreach (int t in _near)
            {
                WallTile tile = walls[t];
                if (tile.Kind != TileKind.Door || tile.State == TileState.Broken) continue;
                cmd.Hand.Door = true; cmd.Hand.DoorTile = t; cmd.Hand.DoorOpen = tile.State != TileState.DoorOpen;
                return;
            }
        }
    }
}
