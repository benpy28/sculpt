using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Gun state inside <see cref="MotorState"/>: predicted, sent and reconciled exactly like movement.
    /// Each gun keeps its own next-fire time, so the pump's 54-tick cycle keeps running through swaps and sculpt mode.
    /// </summary>
    public struct WeaponState
    {
        /// <summary>Next fire time per gun, in milli-ticks (tick x 1000).</summary>
        public long NextPump, NextSmg, NextAr, NextPistol;
        public int LastShotTick;
        /// <summary>Shots since the recoil last fully recovered (indexes the recoil pattern).</summary>
        public int Streak;
        /// <summary>Counts every shot; part of the spread seed.</summary>
        public int ShotCount;
        public double Bloom;
        /// <summary>Recoil offset added to the view, degrees. Pitch is up (subtracted from the view pitch).</summary>
        public double RecoilPitch, RecoilYaw;
        public bool TriggerWasHeld;
        /// <summary>Tick of a semi-auto press waiting for the gun to be ready.</summary>
        public int BufferedPressTick;
        /// <summary>The gun the current bloom belongs to (bloom is per gun; a swap replaces it with the new gun's swap penalty).</summary>
        public Weapon BloomGun;
        /// <summary>Rounds fired from each magazine since it was last full (0 = full), so a fresh state has full mags.</summary>
        public byte UsedPump, UsedSmg, UsedAr, UsedPistol;
        /// <summary>A reload is running for <see cref="ReloadGun"/>; the mag (or the next shell) is in at ReloadTick.</summary>
        public bool Reloading;
        public Weapon ReloadGun;
        public int ReloadTick;

        public static WeaponState Initial => new WeaponState { LastShotTick = int.MinValue / 2, BufferedPressTick = int.MinValue / 2 };

        public int UsedOf(Weapon w) => w == Weapon.Pump ? UsedPump : w == Weapon.Smg ? UsedSmg : w == Weapon.Ar ? UsedAr : UsedPistol;

        public void SetUsed(Weapon w, int v)
        {
            byte b = (byte)Math.Max(0, Math.Min(255, v));
            if (w == Weapon.Pump) UsedPump = b;
            else if (w == Weapon.Smg) UsedSmg = b;
            else if (w == Weapon.Ar) UsedAr = b;
            else UsedPistol = b;
        }

        /// <summary>Rounds left in a gun's magazine.</summary>
        public int AmmoOf(Weapon w, WeaponsConfig cfg) => Math.Max(0, cfg.Of(w).MagSize - UsedOf(w));

        public long NextOf(Weapon w) => w == Weapon.Pump ? NextPump : w == Weapon.Smg ? NextSmg : w == Weapon.Ar ? NextAr : NextPistol;

        public void SetNext(Weapon w, long v)
        {
            if (w == Weapon.Pump) NextPump = v;
            else if (w == Weapon.Smg) NextSmg = v;
            else if (w == Weapon.Ar) NextAr = v;
            else NextPistol = v;
        }

        public bool Equals(in WeaponState o) =>
            NextPump == o.NextPump && NextSmg == o.NextSmg && NextAr == o.NextAr && NextPistol == o.NextPistol &&
            LastShotTick == o.LastShotTick && Streak == o.Streak && ShotCount == o.ShotCount && Bloom.Equals(o.Bloom) &&
            RecoilPitch.Equals(o.RecoilPitch) && RecoilYaw.Equals(o.RecoilYaw) && TriggerWasHeld == o.TriggerWasHeld &&
            BufferedPressTick == o.BufferedPressTick && BloomGun == o.BloomGun &&
            UsedPump == o.UsedPump && UsedSmg == o.UsedSmg && UsedAr == o.UsedAr && UsedPistol == o.UsedPistol &&
            Reloading == o.Reloading && ReloadGun == o.ReloadGun && ReloadTick == o.ReloadTick;
    }

    /// <summary>One shot leaving a gun. Pellet directions are rebuilt from it deterministically (<see cref="WeaponRules.PelletDir"/>).</summary>
    public struct WeaponShot
    {
        public int Tick;
        public int ShooterId;
        public Weapon Weapon;
        public Vec3d Origin;
        /// <summary>Unit aim direction including recoil.</summary>
        public Vec3d Aim;
        /// <summary>Random cone half-angle for this shot, degrees.</summary>
        public double Spread;
        public uint Seed;
        /// <summary>Rarity of the copy that fired (battle royale damage), fixed at the shot: the bag can change before the
        /// server resolves it (a pickup or drop on the same tick, or a death that drops the bag).</summary>
        public Rarity Rarity;
    }

    public static class WeaponRules
    {
        /// <summary>Fixed sideways recoil pattern, learnable: a slight right drift that swings left after a few shots.</summary>
        public static readonly double[] YawPattern = { 0.0, 0.4, 0.7, 0.3, -0.4, -0.9, -0.6, 0.2, 0.8, 0.5, -0.3, -0.7 };

        /// <summary>Pump pellet pattern on the unit disk: centre, inner diamond, outer square. Fixed so it is readable.</summary>
        public static readonly double[] PatternX = { 0, 0.45, 0, -0.45, 0, 0.75, -0.75, -0.75, 0.75 };
        public static readonly double[] PatternY = { 0, 0, 0.45, 0, -0.45, 0.75, 0.75, -0.75, -0.75 };

        public static long ToMilli(int tick) => tick * 1000L;

        /// <summary>
        /// One tick of the held gun, after hand and movement: bloom and recoil recovery, reloads, then firing.
        /// Appends a shot when it fires.
        /// </summary>
        public static void Step(ref MotorState s, int tick, in MoveInput input, double pitchDegrees, MovementConfig mc, List<WeaponShot> shots, bool reloadPressed = false)
        {
            WeaponsConfig cfg = mc.Weapons;
            ref WeaponState w = ref s.Weapon;
            // No gun in hand (a grenade or empty hands): recoil still recovers by the last gun's numbers.
            bool hasGun = s.Hand.HasGun;
            Weapon held = hasGun ? s.Hand.Held : w.BloomGun;
            WeaponStats st = cfg.Of(held);

            // Bloom is per gun: a different gun in hand replaces it with that gun's small swap penalty, which only starts
            // recovering once the gun is out (not during the draw).
            if (hasGun && w.BloomGun != held)
            {
                w.BloomGun = held;
                w.Bloom = st.SwapBloom;
            }

            // Recovery. Recoil is the aim itself and recovers on its own whatever is in hand.
            int since = tick - w.LastShotTick;
            double dt = 1.0 / SimTime.TicksPerSecond;
            if (since > st.BloomRecoveryDelayTicks && w.Bloom > 0 && s.Hand.Mode == HandMode.Weapon)
                w.Bloom = Math.Max(0, w.Bloom - st.BloomRecoveryPerSecond * dt);
            if (since > st.RecoilRecoveryDelayTicks && (w.RecoilPitch != 0 || w.RecoilYaw != 0))
            {
                double step = st.RecoilRecoveryPerSecond * dt;
                double len = Math.Sqrt(w.RecoilPitch * w.RecoilPitch + w.RecoilYaw * w.RecoilYaw);
                if (len <= step) { w.RecoilPitch = 0; w.RecoilYaw = 0; w.Streak = 0; }
                else { double k = (len - step) / len; w.RecoilPitch *= k; w.RecoilYaw *= k; }
            }

            bool pressed = input.FirePressed || (input.FireHeld && !w.TriggerWasHeld);
            w.TriggerWasHeld = input.FireHeld;
            if (pressed && !s.Hand.InSculptMode && hasGun) w.BufferedPressTick = tick; // a sculpt click or a throw is never a buffered shot

            if (s.Dead) { w.Reloading = false; return; }
            Reload(ref s, tick, st, reloadPressed, mc.Weapons);
            if (!s.Hand.CanFire || !s.CanShoot) return;

            long now = ToMilli(tick);
            long next = w.NextOf(held);
            if (now < next) return;
            bool want = st.Automatic
                ? input.FireHeld || tick - w.BufferedPressTick <= st.FireBufferTicks
                : tick - w.BufferedPressTick <= st.FireBufferTicks;
            if (!want) return;
            if (w.UsedOf(held) >= st.MagSize) return; // empty: the auto-reload has started (Reload)
            if (w.Reloading)
            {
                // A shell reload is interrupted by firing a loaded shell; a magazine reload can't be fired through.
                if (!st.ShellReload) return;
                w.Reloading = false;
            }

            // Fire. Continuous auto fire keeps the exact average rate; otherwise the cycle starts now.
            w.SetNext(s.Hand.Held, (now - next < 1000 ? next : now) + st.FireIntervalMilliTicks);
            w.BufferedPressTick = int.MinValue / 2;

            Vec3d eye = s.Position + new Vec3d(0, PlayerMovement.EyeOf(s, mc), 0);
            var shot = new WeaponShot
            {
                Tick = tick,
                Weapon = s.Hand.Held,
                Origin = eye,
                Aim = AimDir(input.Yaw, pitchDegrees, w.RecoilPitch, w.RecoilYaw),
                Spread = Spread(s, mc, st),
                Seed = Hash((uint)tick, (uint)w.ShotCount, (uint)s.Hand.Held),
                Rarity = s.Hand.Bag.RarityOfGun(held, s.Hand.Slot),
            };
            shots?.Add(shot);

            w.SetUsed(held, w.UsedOf(held) + 1);
            w.ShotCount++;
            w.LastShotTick = tick;
            w.Bloom = Math.Min(st.BloomMax, w.Bloom + st.BloomPerShot);
            double kick = st.RecoilPitchPerShot + (w.Streak == 0 ? st.RecoilFirstShotPitch : 0);
            w.RecoilPitch = Math.Min(st.RecoilMaxPitch, w.RecoilPitch + kick);
            w.RecoilYaw += st.RecoilYawPerShot * YawPattern[w.Streak % YawPattern.Length];
            w.Streak++;
        }

        /// <summary>
        /// Reload rules. A reload starts on the reload press when the held gun's magazine isn't full, or by itself when it
        /// is empty; only with the gun out (not drawing, in sculpt mode or disarmed). Swapping, sculpt mode or a disarm
        /// cancels it (a magazine reload loses its progress; shells already loaded stay in). Sprinting, sliding and
        /// jumping don't stop it.
        /// </summary>
        public static void Reload(ref MotorState s, int tick, WeaponStats st, bool pressed, WeaponsConfig wc = null)
        {
            ref WeaponState w = ref s.Weapon;
            if (!s.Hand.HasGun) { w.Reloading = false; return; }
            Weapon held = s.Hand.Held;
            if (w.Reloading && (w.ReloadGun != held || s.Hand.Mode != HandMode.Weapon)) w.Reloading = false;

            if (w.Reloading && tick >= w.ReloadTick)
            {
                if (st.ShellReload)
                {
                    w.SetUsed(held, w.UsedOf(held) - 1);
                    if (w.UsedOf(held) > 0) w.ReloadTick = tick + Scaled(st.ShellTicks, s, held, wc);
                    else w.Reloading = false;
                }
                else
                {
                    w.SetUsed(held, 0);
                    w.Reloading = false;
                }
            }

            if (w.Reloading || s.Hand.Mode != HandMode.Weapon) return;
            int used = w.UsedOf(held);
            bool empty = used >= st.MagSize;
            if (used <= 0 || !(pressed || empty)) return;
            // An empty gun waits for its fire cycle (the pump's rack) before reloading by itself.
            if (empty && !pressed && ToMilli(tick) < w.NextOf(held)) return;
            w.Reloading = true;
            w.ReloadGun = held;
            w.ReloadTick = tick + Scaled(st.ShellReload ? st.ShellStartTicks + st.ShellTicks : st.ReloadTicks, s, held, wc);
        }

        /// <summary>A reload time scaled by the held gun's rarity (legendary reloads 5% faster).</summary>
        private static int Scaled(int ticks, in MotorState s, Weapon held, WeaponsConfig wc) =>
            wc == null ? ticks : wc.ScaleReload(ticks, s.Hand.Bag.RarityOfGun(held, s.Hand.Slot));

        /// <summary>Spread cone half-angle (degrees) for a shot fired now, from the movement state.</summary>
        public static double Spread(in MotorState s, MovementConfig mc, WeaponStats st)
        {
            double speed = Math.Min(1, s.Velocity.HorizontalLength / Math.Max(0.1, mc.RunSpeed));
            double a = st.BaseSpread + st.MoveSpread * speed + s.Weapon.Bloom;
            switch (s.Mode)
            {
                case MoveMode.Slide: return a + st.SlideSpread;
                case MoveMode.Air:
                case MoveMode.Vault: return a + st.AirSpread;
                default: return s.Crouched ? a * st.CrouchFactor : a;
            }
        }

        /// <summary>Unit look direction from yaw (radians, 0 = north) and pitch (degrees, positive = down), plus recoil.</summary>
        public static Vec3d AimDir(double yaw, double pitchDegrees, double recoilPitch, double recoilYaw)
        {
            double y = yaw + recoilYaw * (Math.PI / 180);
            double p = Math.Max(-89.9, Math.Min(89.9, pitchDegrees - recoilPitch)) * (Math.PI / 180);
            double c = Math.Cos(p);
            return new Vec3d(Math.Sin(y) * c, -Math.Sin(p), Math.Cos(y) * c);
        }

        /// <summary>Direction of pellet <paramref name="i"/> of a shot. Same on every machine.</summary>
        public static Vec3d PelletDir(in WeaponShot shot, WeaponStats st, int i)
        {
            Vec3d f = shot.Aim;
            Vec3d right = new Vec3d(f.Z, 0, -f.X);
            if (right.LengthSquared < 1e-12) right = new Vec3d(1, 0, 0);
            right = right.Normalized;
            Vec3d up = new Vec3d(right.Y * f.Z - right.Z * f.Y, right.Z * f.X - right.X * f.Z, right.X * f.Y - right.Y * f.X);
            // up = right x forward points up for a level forward.
            if (up.Y < 0) up = -up;

            double ox = 0, oy = 0;
            if (st.Pellets > 1 && i < PatternX.Length)
            {
                ox = PatternX[i] * st.PatternSpread;
                oy = PatternY[i] * st.PatternSpread;
            }
            if (shot.Spread > 0)
            {
                uint h = Hash(shot.Seed, (uint)i, 0x9E3779B9u);
                double u = (h & 0xFFFF) / 65536.0, v = (h >> 16) / 65536.0;
                double r = Math.Sqrt(u) * shot.Spread, t = v * 2 * Math.PI;
                ox += r * Math.Cos(t);
                oy += r * Math.Sin(t);
            }
            if (ox == 0 && oy == 0) return f;
            const double D = Math.PI / 180;
            return (f + right * Math.Tan(ox * D) + up * Math.Tan(oy * D)).Normalized;
        }

        public static uint Hash(uint a, uint b, uint c)
        {
            unchecked
            {
                uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u) * 0x85EBCA77u ^ (c + 0x165667B1u) * 0xC2B2AE3Du;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return h;
            }
        }

        /// <summary>Applies damage to a living player. Returns true if it killed them.</summary>
        public static bool ApplyDamage(ref MotorState s, int damage, int tick, WeaponsConfig cfg)
        {
            if (s.Dead || damage <= 0) return false;
            s.Damage = Math.Min(cfg.MaxHealth, s.Damage + damage);
            if (s.Damage < cfg.MaxHealth) return false;
            Kill(ref s, tick, cfg);
            return true;
        }

        public static void Kill(ref MotorState s, int tick, WeaponsConfig cfg)
        {
            s.Damage = cfg.MaxHealth;
            s.Dead = true;
            s.RespawnTick = tick + cfg.RespawnDelayTicks;
            s.Velocity = Vec3d.Zero;
            s.Sprinting = false;
        }
    }
}
