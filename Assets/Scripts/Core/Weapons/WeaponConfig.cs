using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Every number of one gun. Angles are degrees (cone half-angles for spread), distances metres, times 60 Hz ticks.
    /// Fire intervals are in milli-ticks (1000 = one tick) so rates like 5.5 shots/s stay exact on average.
    /// </summary>
    public sealed class WeaponStats
    {
        public string Name = "";
        public int Pellets = 1;
        public double BodyDamage;
        public double HeadDamage;
        public long FireIntervalMilliTicks;
        /// <summary>Holding fire keeps shooting. False = semi-auto: one shot per press.</summary>
        public bool Automatic;
        /// <summary>A press this many ticks before the gun is ready still fires (on the first ready tick).</summary>
        public int FireBufferTicks = 6 * SimTime.Per60;
        public double Range = 250;

        /// <summary>Damage factor by distance: piecewise linear through these points, flat outside them.</summary>
        public double[] FalloffDistances = { 0 };
        public double[] FalloffFactors = { 1 };

        // ---- Spread: cone half-angle of each shot ----
        public double BaseSpread;
        /// <summary>Added at run speed, scaled by horizontal speed / run speed (capped at 1).</summary>
        public double MoveSpread;
        /// <summary>Multiplies base + move + bloom while crouched on the ground. 1 on every gun since balance pass 2
        /// (2026-11-26): crouching makes you a smaller target but no longer steadies your aim, so planting in a crouch
        /// stops out-shooting a player who keeps moving.</summary>
        public double CrouchFactor = 1;
        /// <summary>Added while sliding / airborne (mantling and sprinting can't shoot).</summary>
        public double SlideSpread;
        public double AirSpread;
        public double BloomPerShot;
        public double BloomMax;
        public double BloomRecoveryPerSecond;
        /// <summary>Bloom only starts recovering this long after a shot (longer than the fire interval for autos).</summary>
        public int BloomRecoveryDelayTicks;

        /// <summary>Multi-pellet guns: radius of the fixed pellet pattern (the outer ring), degrees.</summary>
        public double PatternSpread;
        /// <summary>
        /// Bloom this gun starts with after a swap (bloom is per gun; Ben 2026-11-20: "small penalty" on swapping). It does
        /// not recover during the draw, only once the gun is out.
        /// </summary>
        public double SwapBloom;

        // ---- Ammo (Ben 2026-11-20: ammo and reloads; numbers are Claude's tuning, unlimited reserve) ----
        public int MagSize = 30;
        /// <summary>Magazine guns: ticks for a full reload (the mag is full at the end; cancelled by a swap or sculpt mode).</summary>
        public int ReloadTicks = 96 * SimTime.Per60;
        /// <summary>Shell-by-shell reload (pump): first shell after ShellStartTicks + ShellTicks, then one per ShellTicks.
        /// Firing with at least one shell loaded interrupts it; shells already in stay in.</summary>
        public bool ShellReload;
        public int ShellStartTicks;
        public int ShellTicks;

        /// <summary>Right-mouse zoom: field of view multiplier (1 = no zoom; camera only, no accuracy change).</summary>
        public double AimFovScale = 1;

        // ---- Recoil: moves the real aim (and the camera with it), recovers on its own ----
        public double RecoilPitchPerShot;
        /// <summary>Extra pitch on the first shot of a burst (recoil fully recovered): a heavy, fixed, learnable kick
        /// (Ben 2026-11-25). The first bullet still goes where you aimed; only the follow-up needs pulling down.</summary>
        public double RecoilFirstShotPitch;
        /// <summary>Sideways kick per shot, scaled by a fixed learnable pattern (<see cref="WeaponRules.YawPattern"/>).</summary>
        public double RecoilYawPerShot;
        public double RecoilMaxPitch = 10;
        public double RecoilRecoveryPerSecond;
        public int RecoilRecoveryDelayTicks;

        public double DamageFactorAt(double distance)
        {
            double[] d = FalloffDistances, f = FalloffFactors;
            if (d == null || f == null || d.Length == 0 || d.Length != f.Length) return 1;
            if (distance <= d[0]) return f[0];
            for (int i = 1; i < d.Length; i++)
            {
                if (distance > d[i]) continue;
                double span = d[i] - d[i - 1];
                double t = span > 0 ? (distance - d[i - 1]) / span : 1;
                return f[i - 1] + (f[i] - f[i - 1]) * t;
            }
            return f[f.Length - 1];
        }

        public WeaponStats Clone() => (WeaponStats)MemberwiseClone();
    }

    /// <summary>
    /// The fixed loadout, health and hit rules. Defaults are the spec numbers (Ben approved 2026-11-20); Game fills it
    /// from a ScriptableObject. Values marked "tuning" are Claude's starting values for feel, not spec rules.
    /// </summary>
    public sealed class WeaponsConfig
    {
        public int MaxHealth = 150;
        /// <summary>Ticks from death to respawn (tuning; the 5v5 mode has one life per round and won't use it).</summary>
        public int RespawnDelayTicks = 180 * SimTime.Per60;
        /// <summary>Off: bullets pass through teammates and never hurt them (open question for Ben).</summary>
        public bool FriendlyFire = false;

        /// <summary>Head hit zone: the top of the box, this tall (the drawn head), at most HeadFraction of the box.</summary>
        public double HeadHeight = 0.33;
        /// <summary>Head width (m), centred on the box; capped at the box width. Ben 2026-11-25: head a little smaller
        /// (was 0.36 m tall and the full 0.7 m width).</summary>
        public double HeadWidth = 0.6;
        public double HeadFraction = 0.3;

        /// <summary>Lag compensation: targets are rewound to what the shooter saw, at most this many ticks back.</summary>
        public int MaxRewindTicks = 18 * SimTime.Per60;

        public WeaponStats[] Stats =
        {
            // Pump: 9 x 9.4, head pellets 1.5x (Ben 2026-11-25: gun damage ~15% lower; was 11), 0.75 s cycle (balance pass 2026-11-25, was 0.9: it lost to the SMG even at 3 m), falloff from 5 m, heavy past 12 m; wider in the air and sliding.
            new WeaponStats
            {
                Name = "Pump", Pellets = 9, BodyDamage = 9.4, HeadDamage = 14.1, FireIntervalMilliTicks = 45000 * SimTime.Per60, Automatic = true,
                FireBufferTicks = 6 * SimTime.Per60, Range = 60,
                FalloffDistances = new double[] { 5, 12, 25 }, FalloffFactors = new double[] { 1.0, 0.65, 0.2 },
                BaseSpread = 0.0, MoveSpread = 0.3, CrouchFactor = 1, SlideSpread = 1.5, AirSpread = 1.5, PatternSpread = 2.6, // slide/air 1.5 (balance pass 2026-11-25, were 0.6/0.8): running stays accurate, slide-pumping is a risk
                SwapBloom = 0.3, BloomRecoveryPerSecond = 4,
                MagSize = 5, ShellReload = true, ShellStartTicks = 6 * SimTime.Per60, ShellTicks = 30 * SimTime.Per60,
                RecoilPitchPerShot = 3.5, RecoilFirstShotPitch = 0.5, RecoilYawPerShot = 0.6, RecoilMaxPitch = 6, RecoilRecoveryPerSecond = 11, RecoilRecoveryDelayTicks = 8 * SimTime.Per60,
            },
            // SMG: 12.75 (was 15), 11 shots/s, falloff from 7 m. Head 1.5x = 19.125, 8 heads to kill (balance pass 2
            // 2026-11-26, was 1.25x = 15.94, 10 heads: at pro aim the AR's 1.58x head out-damaged it, so it had no range of its
            // own; body damage is unchanged, so chest-aiming players see little difference).
            new WeaponStats
            {
                Name = "SMG", BodyDamage = 12.75, HeadDamage = 19.125, MagSize = 30, ReloadTicks = 96 * SimTime.Per60, SwapBloom = 0.3, FireIntervalMilliTicks = 5455 * SimTime.Per60, Automatic = true, Range = 120,
                FalloffDistances = new double[] { 7, 18, 35 }, FalloffFactors = new double[] { 1.0, 0.6, 0.4 }, // falloff from 7 m, 0.6 at 18 m (balance pass 2, was 10 / 20 m): the bigger head bonus must not make it a 10-15 m gun
                BaseSpread = 0.9, MoveSpread = 0.7, CrouchFactor = 1, SlideSpread = 1.2, AirSpread = 1.8, // crouch bonus 0.8 -> 1 (balance pass 2, 2026-11-26)
                BloomPerShot = 0.12, BloomMax = 1.6, BloomRecoveryPerSecond = 8, BloomRecoveryDelayTicks = 7 * SimTime.Per60,
                RecoilPitchPerShot = 0.28, RecoilFirstShotPitch = 0.25, RecoilYawPerShot = 0.2, RecoilMaxPitch = 5, RecoilRecoveryPerSecond = 10, RecoilRecoveryDelayTicks = 7 * SimTime.Per60,
            },
            // AR: 21.5 body / 34 head (was 25/40; 7 body shots kill instead of 6), 5.5 shots/s, first shot accurate then spread builds; strongly penalised sliding/airborne.
            new WeaponStats
            {
                Name = "AR", BodyDamage = 21.5, HeadDamage = 34, MagSize = 25, ReloadTicks = 114 * SimTime.Per60, SwapBloom = 0.4, AimFovScale = 0.8, FireIntervalMilliTicks = 10909 * SimTime.Per60, Automatic = true, Range = 250,
                BaseSpread = 0.0, MoveSpread = 0.9, CrouchFactor = 1, SlideSpread = 3.0, AirSpread = 4.0, // crouch bonus 0.75 -> 1 (balance pass 2, 2026-11-26)
                BloomPerShot = 0.45, BloomMax = 3.0, BloomRecoveryPerSecond = 6, BloomRecoveryDelayTicks = 13 * SimTime.Per60,
                RecoilPitchPerShot = 0.7, RecoilFirstShotPitch = 0.6, RecoilYawPerShot = 0.3, RecoilMaxPitch = 7, RecoilRecoveryPerSecond = 9, RecoilRecoveryDelayTicks = 13 * SimTime.Per60,
            },
            // Pistol: 25.5 body / 37 head (balance pass 2026-11-25: 5 heads to kill, was 42.5; bloom 0.8/shot cap 2.5, was 0.5/2.0; it beat the AR at 10-25 m), semi-auto, max 5 shots/s, accurate; falloff from 25 m to 70% at 50 m.
            new WeaponStats
            {
                Name = "Pistol", BodyDamage = 25.5, HeadDamage = 37, MagSize = 12, ReloadTicks = 78 * SimTime.Per60, SwapBloom = 0.3, AimFovScale = 0.87, FireIntervalMilliTicks = 12000 * SimTime.Per60, Automatic = false,
                FireBufferTicks = 6 * SimTime.Per60, Range = 250,
                FalloffDistances = new double[] { 25, 50 }, FalloffFactors = new double[] { 1.0, 0.7 },
                BaseSpread = 0.1, MoveSpread = 0.5, CrouchFactor = 1, SlideSpread = 1.5, AirSpread = 2.0, // crouch bonus 0.8 -> 1 (balance pass 2, 2026-11-26)
                BloomPerShot = 0.8, BloomMax = 2.5, BloomRecoveryPerSecond = 8, BloomRecoveryDelayTicks = 14 * SimTime.Per60,
                RecoilPitchPerShot = 1.4, RecoilFirstShotPitch = 0.45, RecoilYawPerShot = 0.25, RecoilMaxPitch = 6, RecoilRecoveryPerSecond = 14, RecoilRecoveryDelayTicks = 4 * SimTime.Per60,
            },
        };

        public WeaponStats Of(Weapon w) => Stats[(int)w];

        /// <summary>Height of the head zone for a box of this height.</summary>
        public double HeadZone(double boxHeight) => Math.Min(HeadHeight, boxHeight * HeadFraction);

        public static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    }
}
