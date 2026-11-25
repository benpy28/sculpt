using System;
using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Config
{
    /// <summary>
    /// Tunable gun numbers. Create one with Assets > Create > SCULPT > Weapon Settings and assign it to the Bootstrap
    /// object. New assets start from the Core defaults (the spec numbers). Edits apply live in Play mode in the Editor
    /// (to that Editor's server and client; all players must run the same numbers).
    /// </summary>
    [CreateAssetMenu(menuName = "SCULPT/Weapon Settings", fileName = "WeaponSettings")]
    public sealed class WeaponSettings : ScriptableObject
    {
        [Serializable]
        public sealed class Gun
        {
            public string name;
            [Header("Damage")]
            public int pellets = 1;
            public float bodyDamage;
            public float headDamage;
            [Tooltip("Damage factor by distance: piecewise linear through these (metres, factor) points.")]
            public Vector2[] falloff = { new Vector2(0, 1) };
            public float range = 250;
            [Header("Rate")]
            [Tooltip("Shots per second.")] public float fireRate;
            public bool automatic;
            public int fireBufferTicks = 6;
            [Header("Spread (degrees, cone half-angle)")]
            public float baseSpread;
            public float moveSpread;
            public float crouchFactor = 1;
            public float slideSpread;
            public float airSpread;
            public float bloomPerShot, bloomMax, bloomRecoveryPerSecond;
            public int bloomRecoveryDelayTicks;
            public float patternSpread;
            [Header("Recoil (degrees, moves the aim)")]
            public float recoilPitchPerShot;
            public float recoilYawPerShot;
            public float recoilMaxPitch = 10;
            public float recoilRecoveryPerSecond;
            public int recoilRecoveryDelayTicks;

            public static Gun From(WeaponStats s)
            {
                var g = new Gun
                {
                    name = s.Name, pellets = s.Pellets, bodyDamage = (float)s.BodyDamage, headDamage = (float)s.HeadDamage, range = (float)s.Range,
                    fireRate = (float)(SimTime.TicksPerSecond * 1000.0 / s.FireIntervalMilliTicks), automatic = s.Automatic, fireBufferTicks = s.FireBufferTicks,
                    baseSpread = (float)s.BaseSpread, moveSpread = (float)s.MoveSpread, crouchFactor = (float)s.CrouchFactor, slideSpread = (float)s.SlideSpread,
                    airSpread = (float)s.AirSpread, bloomPerShot = (float)s.BloomPerShot, bloomMax = (float)s.BloomMax, bloomRecoveryPerSecond = (float)s.BloomRecoveryPerSecond,
                    bloomRecoveryDelayTicks = s.BloomRecoveryDelayTicks, patternSpread = (float)s.PatternSpread, recoilPitchPerShot = (float)s.RecoilPitchPerShot,
                    recoilYawPerShot = (float)s.RecoilYawPerShot, recoilMaxPitch = (float)s.RecoilMaxPitch, recoilRecoveryPerSecond = (float)s.RecoilRecoveryPerSecond,
                    recoilRecoveryDelayTicks = s.RecoilRecoveryDelayTicks,
                };
                g.falloff = new Vector2[s.FalloffDistances.Length];
                for (int i = 0; i < g.falloff.Length; i++) g.falloff[i] = new Vector2((float)s.FalloffDistances[i], (float)s.FalloffFactors[i]);
                return g;
            }

            public void ApplyTo(WeaponStats s)
            {
                s.Pellets = Math.Max(1, pellets);
                s.BodyDamage = bodyDamage;
                s.HeadDamage = headDamage;
                s.Range = range;
                // Exact for the spec rates: 12/s = 5 ticks, 5.5/s = 10.909, 5/s = 12, pump 54 ticks (1.111/s).
                s.FireIntervalMilliTicks = Math.Max(1000, (long)Math.Round(SimTime.TicksPerSecond * 1000.0 / Math.Max(0.01, fireRate)));
                s.Automatic = automatic;
                s.FireBufferTicks = fireBufferTicks;
                if (falloff != null && falloff.Length > 0)
                {
                    s.FalloffDistances = new double[falloff.Length];
                    s.FalloffFactors = new double[falloff.Length];
                    for (int i = 0; i < falloff.Length; i++) { s.FalloffDistances[i] = falloff[i].x; s.FalloffFactors[i] = falloff[i].y; }
                }
                s.BaseSpread = baseSpread;
                s.MoveSpread = moveSpread;
                s.CrouchFactor = crouchFactor;
                s.SlideSpread = slideSpread;
                s.AirSpread = airSpread;
                s.BloomPerShot = bloomPerShot;
                s.BloomMax = bloomMax;
                s.BloomRecoveryPerSecond = bloomRecoveryPerSecond;
                s.BloomRecoveryDelayTicks = bloomRecoveryDelayTicks;
                s.PatternSpread = patternSpread;
                s.RecoilPitchPerShot = recoilPitchPerShot;
                s.RecoilYawPerShot = recoilYawPerShot;
                s.RecoilMaxPitch = recoilMaxPitch;
                s.RecoilRecoveryPerSecond = recoilRecoveryPerSecond;
                s.RecoilRecoveryDelayTicks = recoilRecoveryDelayTicks;
            }
        }

        [Header("Health and rules")]
        public int maxHealth = 150;
        public int respawnDelayTicks = 180;
        public bool friendlyFire = false;
        public float headHeight = 0.33f;
        public float headWidth = 0.6f;
        public int maxRewindTicks = 18;

        [Tooltip("Pump, SMG, AR, Pistol (hotbar 1-4).")]
        public Gun[] guns;

        private void Reset() => FillDefaults();

        private void OnEnable()
        {
            if (guns == null || guns.Length != 4) FillDefaults();
        }

        private void FillDefaults()
        {
            var d = new WeaponsConfig();
            maxHealth = d.MaxHealth;
            respawnDelayTicks = d.RespawnDelayTicks;
            friendlyFire = d.FriendlyFire;
            headHeight = (float)d.HeadHeight;
            headWidth = (float)d.HeadWidth;
            maxRewindTicks = d.MaxRewindTicks;
            guns = new Gun[4];
            for (int i = 0; i < 4; i++) guns[i] = Gun.From(d.Stats[i]);
        }

        public void ApplyTo(WeaponsConfig c)
        {
            c.MaxHealth = Math.Max(1, maxHealth);
            c.RespawnDelayTicks = Math.Max(0, respawnDelayTicks);
            c.FriendlyFire = friendlyFire;
            c.HeadHeight = headHeight;
            c.HeadWidth = Math.Max(0.1, headWidth);
            c.MaxRewindTicks = Math.Max(0, maxRewindTicks);
            if (guns == null) return;
            for (int i = 0; i < 4 && i < guns.Length; i++) guns[i]?.ApplyTo(c.Stats[i]);
        }
    }
}
