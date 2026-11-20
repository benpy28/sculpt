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
        public int FireBufferTicks = 6;
        public double Range = 250;

        /// <summary>Damage factor by distance: piecewise linear through these points, flat outside them.</summary>
        public double[] FalloffDistances = { 0 };
        public double[] FalloffFactors = { 1 };

        // ---- Spread: cone half-angle of each shot ----
        public double BaseSpread;
        /// <summary>Added at run speed, scaled by horizontal speed / run speed (capped at 1).</summary>
        public double MoveSpread;
        /// <summary>Multiplies base + move + bloom while crouched on the ground.</summary>
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

        // ---- Recoil: moves the real aim (and the camera with it), recovers on its own ----
        public double RecoilPitchPerShot;
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
        public int RespawnDelayTicks = 180;
        /// <summary>Off: bullets pass through teammates and never hurt them (open question for Ben).</summary>
        public bool FriendlyFire = false;

        /// <summary>Head hit zone: the top of the box, this tall (the drawn head), at most HeadFraction of the box.</summary>
        public double HeadHeight = 0.36;
        public double HeadFraction = 0.3;

        /// <summary>Lag compensation: targets are rewound to what the shooter saw, at most this many ticks back.</summary>
        public int MaxRewindTicks = 18;

        public WeaponStats[] Stats =
        {
            // Pump: 9 x 11, head pellets 1.5x, 0.9 s cycle, falloff from 5 m, heavy past 12 m; fairly accurate in air/slide.
            new WeaponStats
            {
                Name = "Pump", Pellets = 9, BodyDamage = 11, HeadDamage = 16.5, FireIntervalMilliTicks = 54000, Automatic = true,
                FireBufferTicks = 6, Range = 60,
                FalloffDistances = new double[] { 5, 12, 25 }, FalloffFactors = new double[] { 1.0, 0.65, 0.2 },
                BaseSpread = 0.0, MoveSpread = 0.3, CrouchFactor = 1, SlideSpread = 0.6, AirSpread = 0.8, PatternSpread = 2.6,
                RecoilPitchPerShot = 3.5, RecoilYawPerShot = 0.6, RecoilMaxPitch = 6, RecoilRecoveryPerSecond = 11, RecoilRecoveryDelayTicks = 8,
            },
            // SMG: 15, 12 shots/s, heavy falloff past 10 m.
            new WeaponStats
            {
                Name = "SMG", BodyDamage = 15, HeadDamage = 15, FireIntervalMilliTicks = 5000, Automatic = true, Range = 120,
                FalloffDistances = new double[] { 10, 20, 35 }, FalloffFactors = new double[] { 1.0, 0.6, 0.4 },
                BaseSpread = 0.9, MoveSpread = 0.7, CrouchFactor = 0.8, SlideSpread = 1.2, AirSpread = 1.8,
                BloomPerShot = 0.12, BloomMax = 1.6, BloomRecoveryPerSecond = 8, BloomRecoveryDelayTicks = 7,
                RecoilPitchPerShot = 0.28, RecoilYawPerShot = 0.2, RecoilMaxPitch = 5, RecoilRecoveryPerSecond = 10, RecoilRecoveryDelayTicks = 7,
            },
            // AR: 25 body / 40 head, 5.5 shots/s, first shot accurate then spread builds; strongly penalised sliding/airborne.
            new WeaponStats
            {
                Name = "AR", BodyDamage = 25, HeadDamage = 40, FireIntervalMilliTicks = 10909, Automatic = true, Range = 250,
                BaseSpread = 0.0, MoveSpread = 0.9, CrouchFactor = 0.75, SlideSpread = 3.0, AirSpread = 4.0,
                BloomPerShot = 0.45, BloomMax = 3.0, BloomRecoveryPerSecond = 6, BloomRecoveryDelayTicks = 13,
                RecoilPitchPerShot = 0.7, RecoilYawPerShot = 0.3, RecoilMaxPitch = 7, RecoilRecoveryPerSecond = 9, RecoilRecoveryDelayTicks = 13,
            },
            // Pistol: 30 body / 50 head, semi-auto, max 5 shots/s, accurate; falloff from 25 m to 70% at 50 m.
            new WeaponStats
            {
                Name = "Pistol", BodyDamage = 30, HeadDamage = 50, FireIntervalMilliTicks = 12000, Automatic = false,
                FireBufferTicks = 6, Range = 250,
                FalloffDistances = new double[] { 25, 50 }, FalloffFactors = new double[] { 1.0, 0.7 },
                BaseSpread = 0.1, MoveSpread = 0.5, CrouchFactor = 0.8, SlideSpread = 1.5, AirSpread = 2.0,
                BloomPerShot = 0.5, BloomMax = 2.0, BloomRecoveryPerSecond = 8, BloomRecoveryDelayTicks = 14,
                RecoilPitchPerShot = 1.4, RecoilYawPerShot = 0.25, RecoilMaxPitch = 6, RecoilRecoveryPerSecond = 14, RecoilRecoveryDelayTicks = 4,
            },
        };

        public WeaponStats Of(Weapon w) => Stats[(int)w];

        /// <summary>Height of the head zone for a box of this height.</summary>
        public double HeadZone(double boxHeight) => Math.Min(HeadHeight, boxHeight * HeadFraction);

        public static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
    }
}
