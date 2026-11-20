using System;

namespace Sculpt.Core
{
    /// <summary>The fixed loadout. Hotbar 1-4 in this order.</summary>
    public enum Weapon : byte
    {
        Pump = 0,
        Smg = 1,
        Ar = 2,
        Pistol = 3,
    }

    public enum HandMode : byte
    {
        /// <summary>The held weapon is out and ready.</summary>
        Weapon = 0,
        /// <summary>Drawing the held weapon (swap, forced redraw after a disarm). Ready at ReadyTick.</summary>
        Drawing = 1,
        /// <summary>Going into sculpt mode: aiming and the ghost work, confirms wait until ReadyTick.</summary>
        SculptEnter = 2,
        /// <summary>In sculpt mode, confirms allowed.</summary>
        Sculpt = 3,
        /// <summary>Leaving sculpt mode back to the held weapon (its exit time). Ready at ReadyTick.</summary>
        SculptExit = 4,
        /// <summary>A throw hit knocked weapons and sculpting out. Redraws the held weapon at ReadyTick.</summary>
        Disarmed = 5,
    }

    /// <summary>
    /// Weapon/sculpt hand state: a plain struct inside <see cref="MotorState"/>, so the netcode predicts, sends and
    /// reconciles it exactly like movement. Ticks are absolute sim ticks.
    /// </summary>
    public struct HandState
    {
        public HandMode Mode;
        /// <summary>The weapon you return to (last held).</summary>
        public Weapon Held;
        /// <summary>When the current transition (draw, enter, exit, disarm) ends.</summary>
        public int ReadyTick;
        /// <summary>Gun lock: you cannot leave sculpt mode before this tick (attack and throw windups).</summary>
        public int SculptLockUntil;
        /// <summary>Leave sculpt mode as soon as the lock ends (toggle or a weapon key pressed while locked).</summary>
        public bool ExitQueued;

        public static HandState Initial => new HandState { Mode = HandMode.Weapon, Held = Weapon.Pump, ReadyTick = int.MinValue / 2, SculptLockUntil = int.MinValue / 2 };

        public bool InSculptMode => Mode == HandMode.SculptEnter || Mode == HandMode.Sculpt;
        /// <summary>Sculpt confirms are accepted this tick.</summary>
        public bool CanConfirmSculpt => Mode == HandMode.Sculpt;
        public bool CanFire => Mode == HandMode.Weapon;

        public bool Equals(in HandState o) => Mode == o.Mode && Held == o.Held && ReadyTick == o.ReadyTick &&
                                               SculptLockUntil == o.SculptLockUntil && ExitQueued == o.ExitQueued;
    }

    /// <summary>Per-tick hand input. Presses only; a weapon key is 1-4 (0 = none).</summary>
    public struct HandInput
    {
        public bool SculptToggle;
        public byte WeaponKey;
    }

    /// <summary>Hand timings (Ben approved 2026-11-20). Ticks at 60 Hz.</summary>
    public sealed class HandConfig
    {
        public int SculptEnterTicks = 6;
        public int[] DrawTicks = { 24, 18, 21, 12 };  // pump, SMG, AR, pistol
        public int[] ExitTicks = { 12, 9, 10, 6 };    // sculpt mode back to that weapon

        public int Draw(Weapon w) => DrawTicks[(int)w];
        public int Exit(Weapon w) => ExitTicks[(int)w];
    }

    public static class HandRules
    {
        /// <summary>Advances the hand by one tick (call with that tick's number, after its presses are known).</summary>
        public static void Step(ref HandState h, int tick, in HandInput input, HandConfig cfg)
        {
            // Transitions that finish this tick.
            if ((h.Mode == HandMode.Drawing || h.Mode == HandMode.SculptExit) && tick >= h.ReadyTick) h.Mode = HandMode.Weapon;
            if (h.Mode == HandMode.SculptEnter && tick >= h.ReadyTick) h.Mode = HandMode.Sculpt;
            if (h.Mode == HandMode.Disarmed && tick >= h.ReadyTick)
            {
                h.Mode = HandMode.Drawing;
                h.ReadyTick = tick + cfg.Draw(h.Held);
            }
            if (h.Mode == HandMode.Disarmed) return; // nothing works while disarmed

            Weapon? pick = input.WeaponKey >= 1 && input.WeaponKey <= 4 ? (Weapon)(input.WeaponKey - 1) : (Weapon?)null;

            if (h.InSculptMode)
            {
                if (input.SculptToggle || pick.HasValue)
                {
                    if (pick.HasValue) h.Held = pick.Value;
                    h.ExitQueued = true;
                }
                if (h.ExitQueued && tick >= h.SculptLockUntil)
                {
                    h.ExitQueued = false;
                    h.Mode = HandMode.SculptExit;
                    h.ReadyTick = tick + cfg.Exit(h.Held);
                }
                return;
            }

            // Weapon, Drawing or SculptExit.
            if (input.SculptToggle)
            {
                h.Mode = HandMode.SculptEnter;
                h.ReadyTick = tick + cfg.SculptEnterTicks;
                h.ExitQueued = false;
                return;
            }
            if (pick.HasValue && (pick.Value != h.Held || h.Mode == HandMode.SculptExit))
            {
                h.Held = pick.Value;
                h.Mode = HandMode.Drawing;
                h.ReadyTick = tick + cfg.Draw(h.Held);
            }
        }

        /// <summary>A throw hit: no weapons and no sculpting for <paramref name="ticks"/>, then a forced redraw.</summary>
        public static void Disarm(ref HandState h, int tick, int ticks)
        {
            if (ticks <= 0) return;
            int until = tick + ticks;
            if (h.Mode == HandMode.Disarmed) { h.ReadyTick = Math.Max(h.ReadyTick, until); return; }
            h.Mode = HandMode.Disarmed;
            h.ReadyTick = until;
            h.ExitQueued = false;
        }

        /// <summary>A sculpt windup started that keeps you in sculpt mode until <paramref name="untilTick"/>.</summary>
        public static void LockGun(ref HandState h, int untilTick)
        {
            if (untilTick > h.SculptLockUntil) h.SculptLockUntil = untilTick;
        }
    }
}
