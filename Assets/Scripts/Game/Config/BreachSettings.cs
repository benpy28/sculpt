using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Config
{
    /// <summary>
    /// The one place for the destruction, healing and bar numbers (Ben 2026-11-21: "all in ONE ScriptableObject so I
    /// can tune without code changes"). Create one with Assets > Create > SCULPT > Breach Settings and drag it onto the
    /// Bootstrap object's Breach Settings slot. Without one, these code defaults are used. Movement numbers stay in
    /// Movement Settings.
    /// Item, grenade, punch, charge, heal and bar numbers are here too. Read when the map is built (enter Play mode
    /// again after changing these).
    /// </summary>
    [CreateAssetMenu(menuName = "SCULPT/Breach Settings", fileName = "BreachSettings")]
    public sealed class BreachSettings : ScriptableObject
    {
        [Header("Parked")]
        [Tooltip("The old six-direction block moving (push, pull, raise, sink, throw). Off since the 2026-11-21 pivot.")]
        public bool blockMovesEnabled = false;

        [Header("Wall tiles (health in points)")]
        [Min(1)] public int intactHealth = 100;
        [Tooltip("An intact tile below this fraction of its health shows cracks (Damaged): it can then be charged through and healed.")]
        [Range(0.05f, 0.95f)] public float damagedBelowFraction = 0.5f;
        [Tooltip("Healed tiles come back Patched with this max health.")]
        [Min(1)] public int patchedHealth = 50;
        [Min(1)] public int reinforcedHealth = 200;

        [Header("Tile damage per bullet (pump: per pellet). Flat, no falloff.")]
        [Min(0)] public int pumpPelletTileDamage = 6;
        [Min(0)] public int smgTileDamage = 7;
        [Min(0)] public int arTileDamage = 13;
        [Min(0)] public int pistolTileDamage = 17;

        [Header("Items (pick up: interact on an item; drop: hold interact)")]
        [Min(0)] public int grenadeCarry = 2;
        [Tooltip("Pickup reach from the eye, metres.")]
        [Min(0.5f)] public float pickupReach = 2.5f;
        [Tooltip("How close to the crosshair an item must be to count as aimed at, degrees.")]
        [Range(5f, 60f)] public float pickupConeDegrees = 25f;
        [Tooltip("Hold interact this long (seconds) while not aiming at an item to drop what you hold.")]
        [Range(0.1f, 1.5f)] public float dropHoldSeconds = 0.3f;
        [Min(1)] public int maxGroundItems = 64;

        [Header("Grenades (own: no damage, big push; enemy: damage, smaller push)")]
        [Tooltip("Throw speed along the look direction, m/s (your own velocity is added).")]
        [Min(1f)] public float grenadeThrowSpeed = 20f;
        [Tooltip("The throw leaves this many degrees above the crosshair.")]
        [Range(0f, 20f)] public float grenadeThrowUpDegrees = 6f;
        [Min(0f)] public float grenadeGravity = 15f;
        [Tooltip("It goes off on the first thing it touches, or after this many seconds in the air.")]
        [Range(0.5f, 6f)] public float grenadeFuseSeconds = 3f;
        [Tooltip("Time from a throw until the next grenade is ready, seconds.")]
        [Range(0.1f, 2f)] public float grenadeRecoverySeconds = 0.4f;
        [Tooltip("Players within this distance of the blast (to the chest) are affected, metres.")]
        [Min(0.5f)] public float grenadeRadius = 4f;
        [Tooltip("Full effect inside this distance, then linear to nothing at the radius.")]
        [Min(0f)] public float grenadeFullRadius = 1f;
        [Min(0f)] public float grenadeEnemyDamage = 70f;
        [Tooltip("Speed your own grenade adds to you at full effect, m/s (15 at your feet lifts you about 3.7 m).")]
        [Min(0f)] public float grenadeSelfBoost = 15f;
        [Tooltip("Speed a grenade adds to anyone else at full effect, m/s.")]
        [Min(0f)] public float grenadeOtherKnock = 5f;
        [Tooltip("Tiles within this distance of the blast along every axis take the grenade tile damage (1.2 m: a 3 x 3 hole mid-tile, 2 x 2 at a seam).")]
        [Range(0.76f, 2.2f)] public float grenadeTileRadius = 1.2f;
        [Tooltip("Damage to every tile in the blast: 150 breaks anything but Reinforced (200).")]
        [Min(0)] public int grenadeTileDamage = 150;

        [Header("Remote C4 (a breaching tool, not a kill weapon: fire throws, aim detonates)")]
        [Min(0)] public int c4Carry = 2;
        [Min(1f)] public float c4ThrowSpeed = 12f;
        [Tooltip("It goes off by itself this long after it first landed, seconds.")]
        [Range(1f, 30f)] public float c4FuseSeconds = 8f;
        [Tooltip("The earliest you can set it off after the throw, seconds (a short tell).")]
        [Range(0f, 2f)] public float c4ArmSeconds = 0.25f;
        [Tooltip("Players within this distance of the charge (to the nearest point of their body) are affected, metres.")]
        [Min(0.5f)] public float c4Radius = 2.5f;
        [Min(0f)] public float c4FullRadius = 0.6f;
        [Tooltip("Ben: it should only kill someone careless who walks over it on low health.")]
        [Min(0f)] public float c4EnemyDamage = 45f;
        [Min(0f)] public float c4SelfBoost = 12f;
        [Min(0f)] public float c4OtherKnock = 4f;
        [Range(0.76f, 2.2f)] public float c4TileRadius = 1.2f;
        [Tooltip("Damage to every tile in the blast: 220 breaks Reinforced (200) too.")]
        [Min(0)] public int c4TileDamage = 220;

        [Header("Grenade launcher (slow and clunky up close, strong at range)")]
        [Tooltip("Rounds carried (a launcher pickup comes with this many).")]
        [Min(1)] public int launcherRounds = 4;
        [Min(1f)] public float launcherRoundSpeed = 32f;
        [Min(0f)] public float launcherGravity = 9f;
        [Tooltip("A round that hits something before flying this far is a dud, metres.")]
        [Min(0f)] public float launcherArmDistance = 8f;
        [Tooltip("Time between rounds, seconds.")]
        [Range(0.2f, 3f)] public float launcherCycleSeconds = 1.1f;
        [Min(0.5f)] public float launcherRadius = 3.5f;
        [Min(0f)] public float launcherFullRadius = 0.75f;
        [Min(0f)] public float launcherEnemyDamage = 65f;
        [Min(0f)] public float launcherSelfBoost = 9f;
        [Min(0f)] public float launcherOtherKnock = 6f;
        [Range(0.5f, 2.2f)] public float launcherTileRadius = 0.8f;
        [Tooltip("110 breaks Intact, Damaged and Patched tiles; Reinforced keeps 90.")]
        [Min(0)] public int launcherTileDamage = 110;
        [Tooltip("Run and sprint speed while it is in hand.")]
        [Range(0.5f, 1f)] public float launcherMoveSpeedScale = 0.9f;

        [Header("Punch (own bind, not an item)")]
        [Tooltip("Reach from the eye along the crosshair, metres.")]
        [Range(1f, 3.5f)] public float punchReach = 2f;
        [Tooltip("The hit lands this long after the press, seconds.")]
        [Range(0f, 0.4f)] public float punchHitSeconds = 0.1f;
        [Tooltip("The whole punch, seconds: the gun can't fire and you can't punch again until it ends.")]
        [Range(0.15f, 1.5f)] public float punchSeconds = 0.45f;
        [Min(0)] public int punchPlayerDamage = 30;
        [Tooltip("Ben: half an unreinforced wall tile.")]
        [Min(0)] public int punchTileDamage = 50;
        [Min(0)] public int punchReinforcedTileDamage = 25;

        [Header("Charge (run through Damaged or Patched tiles)")]
        [Tooltip("Share of top sprint speed you need to break a cracked tile by running into it.")]
        [Range(0.3f, 1.5f)] public float chargeSpeedFraction = 0.8f;
        [Tooltip("Share of your horizontal speed kept when you charge through.")]
        [Range(0.3f, 1f)] public float chargeKeep = 0.85f;
        [Tooltip("You must hit the wall within this many degrees of head-on.")]
        [Range(10f, 89f)] public float chargeMaxAngleDegrees = 60f;

        [Header("Healing (the sculpt key: aim at a Damaged or Broken tile, click; hold Reinforce to reinforce)")]
        [Tooltip("How long a heal takes, seconds. The hole stays open until it finishes.")]
        [Range(0.1f, 3f)] public float healSeconds = 0.8f;
        [Tooltip("How long reinforcing takes, seconds.")]
        [Range(0.1f, 3f)] public float reinforceSeconds = 0.8f;
        [Tooltip("Bar a heal costs, percent.")]
        [Range(0f, 100f)] public float healCostPercent = 10f;
        [Tooltip("Bar reinforcing costs, percent.")]
        [Range(0f, 100f)] public float reinforceCostPercent = 25f;
        [Tooltip("Sculpt range, metres from your eye to the nearest point of the tile.")]
        [Range(2f, 12f)] public float sculptRange = 6f;
        [Tooltip("A running heal is cancelled (and refunded) once you are this much further away than the range, metres.")]
        [Range(0f, 5f)] public float healLeash = 1.5f;

        [Header("Sculpt bar")]
        [Tooltip("The bar refills by itself at this many percent per second.")]
        [Range(0f, 20f)] public float barRefillPercentPerSecond = 2f;
        [Tooltip("Extra bar per 10 damage you deal to enemy players, percent.")]
        [Range(0f, 10f)] public float barPercentPer10Damage = 1f;

        [Header("Practice dummy (stands in the sealed room in the middle, heals its broken walls)")]
        public bool practiceDummy = true;
        [Tooltip("A wall tile of its room has to stay broken this long before it heals it, seconds.")]
        [Range(0.5f, 20f)] public float dummyHealDelaySeconds = 3f;
        [Tooltip("Also heal cracked tiles (off: leaves them so you can practise charging through).")]
        public bool dummyHealsCracks = false;

        public void ApplyTo(DummyConfig d)
        {
            d.HealDelayTicks = Ticks(dummyHealDelaySeconds);
            d.HealDamaged = dummyHealsCracks;
        }

        public void ApplyTo(ItemConfig items)
        {
            items.GrenadeCarry = grenadeCarry;
            items.Reach = pickupReach;
            items.AimConeDegrees = pickupConeDegrees;
            items.DropHoldTicks = Mathf.Max(1, Mathf.RoundToInt(dropHoldSeconds * SimTime.TicksPerSecond));
            items.MaxGroundItems = maxGroundItems;
            GrenadeConfig g = items.Grenade;
            g.ThrowSpeed = grenadeThrowSpeed;
            g.ThrowUpDegrees = grenadeThrowUpDegrees;
            g.Gravity = grenadeGravity;
            g.MaxFuseTicks = Mathf.Max(1, Mathf.RoundToInt(grenadeFuseSeconds * SimTime.TicksPerSecond));
            g.ThrowRecoveryTicks = Mathf.Max(1, Mathf.RoundToInt(grenadeRecoverySeconds * SimTime.TicksPerSecond));
            g.Radius = grenadeRadius;
            g.FullRadius = Mathf.Min(grenadeFullRadius, grenadeRadius - 0.01f);
            g.EnemyDamage = grenadeEnemyDamage;
            g.SelfBoost = grenadeSelfBoost;
            g.OtherKnock = grenadeOtherKnock;
            g.TileRadius = grenadeTileRadius;

            items.C4Carry = c4Carry;
            GrenadeConfig c = items.C4;
            c.ThrowSpeed = c4ThrowSpeed;
            c.StuckFuseTicks = Ticks(c4FuseSeconds);
            c.DetonateArmTicks = Mathf.Max(0, Mathf.RoundToInt(c4ArmSeconds * SimTime.TicksPerSecond));
            c.Radius = c4Radius;
            c.FullRadius = Mathf.Min(c4FullRadius, c4Radius - 0.01f);
            c.EnemyDamage = c4EnemyDamage;
            c.SelfBoost = c4SelfBoost;
            c.OtherKnock = c4OtherKnock;
            c.TileRadius = c4TileRadius;

            items.LauncherCarry = launcherRounds;
            GrenadeConfig l = items.Launcher;
            l.ThrowSpeed = launcherRoundSpeed;
            l.Gravity = launcherGravity;
            l.ArmDistance = launcherArmDistance;
            l.ThrowRecoveryTicks = Ticks(launcherCycleSeconds);
            l.Radius = launcherRadius;
            l.FullRadius = Mathf.Min(launcherFullRadius, launcherRadius - 0.01f);
            l.EnemyDamage = launcherEnemyDamage;
            l.SelfBoost = launcherSelfBoost;
            l.OtherKnock = launcherOtherKnock;
            l.TileRadius = launcherTileRadius;
            l.MoveSpeedScale = launcherMoveSpeedScale;
        }

        public void ApplyTo(PunchConfig p)
        {
            p.Reach = punchReach;
            p.TotalTicks = Mathf.Max(1, Mathf.RoundToInt(punchSeconds * SimTime.TicksPerSecond));
            p.HitTicks = Mathf.Clamp(Mathf.RoundToInt(punchHitSeconds * SimTime.TicksPerSecond), 0, p.TotalTicks - 1);
            p.PlayerDamage = punchPlayerDamage;
        }

        public void ApplyTo(SculptConfig sculpt)
        {
            sculpt.BlockMovesEnabled = blockMovesEnabled;
            var b = new BreachConfig();
            ApplyTo(b);
            b.ApplyBarTo(sculpt); // the bars tick with the sculpt config
            if (!blockMovesEnabled) sculpt.RangeMeters = sculptRange; // the faint glow and sculpt ripple use it
        }

        private static int Pct(float percent) => Mathf.Clamp(Mathf.RoundToInt(percent * 100f), 0, SculptConfig.BarMax);
        private static int Ticks(float seconds) => Mathf.Max(1, Mathf.RoundToInt(seconds * SimTime.TicksPerSecond));

        public void ApplyTo(BreachConfig b)
        {
            b.IntactHealth = intactHealth;
            b.DamagedBelowFraction = damagedBelowFraction;
            b.PatchedHealth = patchedHealth;
            b.ReinforcedHealth = reinforcedHealth;
            b.PumpPelletTileDamage = pumpPelletTileDamage;
            b.SmgTileDamage = smgTileDamage;
            b.ArTileDamage = arTileDamage;
            b.PistolTileDamage = pistolTileDamage;
            b.GrenadeTileDamage = grenadeTileDamage;
            b.C4TileDamage = c4TileDamage;
            b.LauncherTileDamage = launcherTileDamage;
            b.PunchTileDamage = punchTileDamage;
            b.PunchReinforcedTileDamage = punchReinforcedTileDamage;
            b.ChargeSpeedFraction = chargeSpeedFraction;
            b.ChargeKeep = chargeKeep;
            b.ChargeMaxAngleDegrees = chargeMaxAngleDegrees;
            b.HealTicks = Ticks(healSeconds);
            b.ReinforceTicks = Ticks(reinforceSeconds);
            b.HealCost = Pct(healCostPercent);
            b.ReinforceCost = Pct(reinforceCostPercent);
            b.HealRange = sculptRange;
            b.HealLeash = healLeash;
            b.BarRefillPerSecond = Pct(barRefillPercentPerSecond);
            b.BarPer10Damage = Pct(barPercentPer10Damage);
        }
    }
}
