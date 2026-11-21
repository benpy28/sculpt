using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Config
{
    /// <summary>
    /// The one place for the destruction, healing and bar numbers (Ben 2026-11-21: "all in ONE ScriptableObject so I
    /// can tune without code changes"). Create one with Assets > Create > SCULPT > Breach Settings and drag it onto the
    /// Bootstrap object's Breach Settings slot. Without one, these code defaults are used. Movement numbers stay in
    /// Movement Settings.
    /// Item, grenade and punch numbers are here too. Charge, heal and bar numbers arrive with their steps. Read when the map is built (enter Play mode
    /// again after changing tile numbers).
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
        }

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
            b.PunchTileDamage = punchTileDamage;
            b.PunchReinforcedTileDamage = punchReinforcedTileDamage;
        }
    }
}
