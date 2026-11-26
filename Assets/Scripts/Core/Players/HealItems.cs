using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Bandage and first-aid kit numbers (Ben 2026-11-26: bandage +25, first-aid kit +100; use times, movement and
    /// carry by Claude). Times in seconds.
    /// </summary>
    public sealed class HealItemConfig
    {
        public int BandageHeal = 25;
        /// <summary>A bandage is quick: short enough to squeeze in behind cover between peeks, long enough that it is
        /// never free in the middle of a fight.</summary>
        public double BandageSeconds = 1.0;
        public int MedkitHeal = 100;
        /// <summary>A first-aid kit is a real commitment: you want a wall between you and them first.</summary>
        public double MedkitSeconds = 3.5;
        /// <summary>Walking speed while using one (of run speed). No sprint and no new slide start while using one.</summary>
        public double MoveSpeedScale = 0.6;

        public int HealOf(Item i) => i == Item.Medkit ? MedkitHeal : i == Item.Bandage ? BandageHeal : 0;
        public int TicksOf(Item i) => SimTime.SecondsToTicks(i == Item.Medkit ? MedkitSeconds : BandageSeconds);
    }

    /// <summary>
    /// Using bandages and first-aid kits. Part of the predicted player step (<see cref="Net.NetSim"/>), so it is exact on
    /// the client at any ping and re-runs exactly from a replay.
    /// - Select the slot and press or hold fire to start one (only when hurt). Holding fire chains the next one.
    /// - It takes effect at the end: health goes up (never past max) and one leaves the stack. Nothing is used up early.
    /// - While using one you walk (no sprint, no new slides). Jumping, looking and taking damage don't stop it.
    /// - Swapping slots, punching, a disarm or death cancels it, and nothing is used.
    /// </summary>
    public static class HealItemRules
    {
        /// <summary>One tick. Call after the hand has stepped (so a slot swap this tick has already cancelled it).</summary>
        public static void Step(ref MotorState s, int tick, in MoveInput move, bool punch, MovementConfig mc)
        {
            ref HandState h = ref s.Hand;
            HealItemConfig c = mc.Items.Heal;
            Item held = h.HeldItem;
            if (h.Using)
            {
                bool keep = !s.Dead && h.Mode == HandMode.Weapon && Items.IsHeal(held) && h.Bag.CountIn(h.Slot) > 0 && !punch;
                if (!keep) { Cancel(ref h); return; }
                if (tick < h.UseUntil) return;
                int max = mc.Weapons.MaxHealth;
                s.Damage = Math.Max(0, s.Damage - c.HealOf(held));
                if (s.Damage > max) s.Damage = max;
                h.Bag.SetCount(h.Slot, h.Bag.Count(h.Slot) - 1);
                Cancel(ref h);
                if (h.Bag.Count(h.Slot) == 0)
                {
                    h.Bag.Clear(h.Slot);
                    HandRules.OnHeldEmptied(ref h, tick, mc.Hand);
                }
                return; // the next one (fire still held) starts next tick
            }
            if (s.Dead || punch || h.Mode != HandMode.Weapon || !Items.IsHeal(held) || h.Bag.CountIn(h.Slot) <= 0) return;
            if (!(move.FirePressed || move.FireHeld)) return;
            if (s.Damage <= 0) return; // full health: nothing to heal (nothing is used)
            h.UseStart = tick;
            h.UseUntil = tick + Math.Max(1, c.TicksOf(held));
        }

        public static void Cancel(ref HandState h)
        {
            h.UseUntil = 0;
            h.UseStart = 0;
        }

        /// <summary>0..1 through the current use (0 when not using one).</summary>
        public static double Progress(in HandState h, double tick)
        {
            if (!h.Using) return 0;
            double len = Math.Max(1, h.UseUntil - h.UseStart);
            return Math.Max(0, Math.Min(1, (tick - h.UseStart) / len));
        }
    }
}
