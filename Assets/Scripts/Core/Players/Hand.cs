using System;

namespace Sculpt.Core
{
    /// <summary>The four guns (their <see cref="Item"/> is this + 1). The standard loadout has them in hotbar 1-4 in this order.</summary>
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
        /// <summary>Leaving sculpt mode to the held weapon: its exit time if it was out when you entered, else its draw time. Ready at ReadyTick.</summary>
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
        /// <summary>The selected hotbar slot, 0-4 (the one you return to after sculpt mode).</summary>
        public byte Slot;
        /// <summary>The slot selected before it (drawn when the held slot empties, and the Last Weapon key).</summary>
        public byte PrevSlot;
        /// <summary>What is in the five slots.</summary>
        public Bag Bag;
        /// <summary>When the current transition (draw, enter, exit, disarm) ends.</summary>
        public int ReadyTick;
        /// <summary>Gun lock: you cannot leave sculpt mode before this tick (attack and throw windups).</summary>
        public int SculptLockUntil;
        /// <summary>Leave sculpt mode as soon as the lock ends (toggle or a weapon key pressed while locked).</summary>
        public bool ExitQueued;
        /// <summary>
        /// The held weapon was out and ready when you went into sculpt mode, so leaving goes back to it for its short
        /// exit time. Picking another gun in sculpt mode, or entering mid-draw, clears it: leaving then pays the full draw
        /// (ISSUE-11: a Q tap must never make a swap faster).
        /// </summary>
        public bool HeldReady;

        public static HandState Initial => new HandState { Mode = HandMode.Weapon, Slot = 0, PrevSlot = 1, Bag = Bag.Standard, ReadyTick = int.MinValue / 2, SculptLockUntil = int.MinValue / 2 };

        /// <summary>The item in the selected slot (None = empty hands).</summary>
        public Item HeldItem => Bag[Slot];
        /// <summary>A gun is in the selected slot.</summary>
        public bool HasGun => Items.IsGun(HeldItem);

        /// <summary>
        /// The gun in the selected slot. Only meaningful when <see cref="HasGun"/> is true (otherwise Pump; callers check
        /// HasGun first). Setting it selects the slot that holds that gun (tests and tools).
        /// </summary>
        public Weapon Held
        {
            get => HasGun ? Items.GunOf(HeldItem) : Weapon.Pump;
            set
            {
                int k = Bag.SlotOf(Items.Of(value));
                if (k < 0) throw new InvalidOperationException($"No {value} in the bag.");
                Slot = (byte)k;
            }
        }

        public bool InSculptMode => Mode == HandMode.SculptEnter || Mode == HandMode.Sculpt;
        /// <summary>Sculpt confirms are accepted this tick.</summary>
        public bool CanConfirmSculpt => Mode == HandMode.Sculpt;
        /// <summary>The held gun can fire this tick (a gun is in hand and out).</summary>
        public bool CanFire => Mode == HandMode.Weapon && HasGun;
        /// <summary>The held item (gun or not) is out and ready.</summary>
        public bool ItemReady => Mode == HandMode.Weapon && HeldItem != Item.None;

        public bool Equals(in HandState o) => Mode == o.Mode && Slot == o.Slot && PrevSlot == o.PrevSlot && Bag.Equals(o.Bag) &&
                                               ReadyTick == o.ReadyTick && SculptLockUntil == o.SculptLockUntil &&
                                               ExitQueued == o.ExitQueued && HeldReady == o.HeldReady;
    }

    /// <summary>Per-tick hand input. Presses only; a weapon key is a hotbar slot 1-5 (0 = none).</summary>
    public struct HandInput
    {
        public bool SculptToggle;
        public byte WeaponKey;
        /// <summary>Reload press (R by default).</summary>
        public bool Reload;
        /// <summary>Interact press aimed at a ground item: pick up item <see cref="PickId"/> (the server checks it).</summary>
        public bool Pickup;
        public int PickId;
        /// <summary>Drop the held slot (the interact key held while not aiming at an item).</summary>
        public bool Drop;
        /// <summary>Punch press (its own bind, F by default).</summary>
        public bool Punch;
        /// <summary>Sculpt press on a wall tile: heal it (or reinforce it with <see cref="HealReinforce"/>). The server checks it.</summary>
        public bool Heal;
        public int HealTile;
        public bool HealReinforce;
    }

    /// <summary>Hand timings (Ben approved 2026-11-20). Ticks at 60 Hz.</summary>
    public sealed class HandConfig
    {
        public int SculptEnterTicks = 6;
        public int[] DrawTicks = { 24, 18, 21, 12 };  // pump, SMG, AR, pistol
        public int[] ExitTicks = { 12, 9, 10, 6 };    // sculpt mode back to that weapon
        /// <summary>Grenades out of the bag, and back out of sculpt mode (tuning, Claude 2026-11-21).</summary>
        public int GrenadeDrawTicks = 12;
        public int GrenadeExitTicks = 6;

        public int Draw(Weapon w) => DrawTicks[(int)w];
        public int Exit(Weapon w) => ExitTicks[(int)w];

        /// <summary>Draw time of any item; empty hands are ready at once.</summary>
        public int Draw(Item i) => Items.IsGun(i) ? Draw(Items.GunOf(i)) : i == Item.Grenade ? GrenadeDrawTicks : 0;
        public int Exit(Item i) => Items.IsGun(i) ? Exit(Items.GunOf(i)) : i == Item.Grenade ? GrenadeExitTicks : 0;
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
                h.ReadyTick = tick + cfg.Draw(h.HeldItem);
            }
            if (h.Mode == HandMode.Disarmed) return; // nothing works while disarmed

            // A slot key picks that slot; an empty slot can't be picked (the key does nothing).
            int pick = input.WeaponKey >= 1 && input.WeaponKey <= Items.Slots && h.Bag[input.WeaponKey - 1] != Item.None ? input.WeaponKey - 1 : -1;

            if (h.InSculptMode)
            {
                if (input.SculptToggle || pick >= 0)
                {
                    if (pick >= 0 && pick != h.Slot) { Select(ref h, pick); h.HeldReady = false; }
                    h.ExitQueued = true;
                }
                if (h.ExitQueued && tick >= h.SculptLockUntil)
                {
                    h.ExitQueued = false;
                    h.Mode = HandMode.SculptExit;
                    h.ReadyTick = tick + (h.HeldReady ? cfg.Exit(h.HeldItem) : cfg.Draw(h.HeldItem));
                }
                return;
            }

            // Weapon, Drawing or SculptExit.
            if (input.SculptToggle)
            {
                // Out and ready only if the gun was drawn, or you were on your way back to a gun that was.
                h.HeldReady = h.Mode == HandMode.Weapon || (h.Mode == HandMode.SculptExit && h.HeldReady);
                h.Mode = HandMode.SculptEnter;
                h.ReadyTick = tick + cfg.SculptEnterTicks;
                h.ExitQueued = false;
                return;
            }
            if (pick >= 0 && (pick != h.Slot || h.Mode == HandMode.SculptExit))
            {
                Select(ref h, pick);
                h.Mode = HandMode.Drawing;
                h.ReadyTick = tick + cfg.Draw(h.HeldItem);
            }
        }

        private static void Select(ref HandState h, int slot)
        {
            if (slot == h.Slot) return;
            h.PrevSlot = h.Slot;
            h.Slot = (byte)slot;
        }

        /// <summary>
        /// The held slot just got a new item (a pickup into it): draw it. In sculpt mode nothing is in hand, so leaving
        /// later pays its full draw.
        /// </summary>
        public static void Redraw(ref HandState h, int tick, HandConfig cfg)
        {
            if (h.InSculptMode) { h.HeldReady = false; return; }
            if (h.Mode == HandMode.Disarmed) return; // the forced redraw at the end of the disarm draws it
            h.Mode = HandMode.Drawing;
            h.ReadyTick = tick + cfg.Draw(h.HeldItem);
        }

        /// <summary>
        /// The held slot emptied (dropped, or the last grenade thrown): draw the previous slot if it has something, else
        /// the first slot that does, else stay empty-handed.
        /// </summary>
        public static void OnHeldEmptied(ref HandState h, int tick, HandConfig cfg)
        {
            if (h.HeldItem != Item.None) return;
            int next = -1;
            if (h.PrevSlot != h.Slot && h.Bag[h.PrevSlot] != Item.None) next = h.PrevSlot;
            for (int k = 0; next < 0 && k < Items.Slots; k++) if (h.Bag[k] != Item.None) next = k;
            if (next < 0) { if (!h.InSculptMode && h.Mode != HandMode.Disarmed) h.Mode = HandMode.Weapon; return; }
            byte emptied = h.Slot;
            h.Slot = (byte)next;
            h.PrevSlot = emptied;
            Redraw(ref h, tick, cfg);
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
