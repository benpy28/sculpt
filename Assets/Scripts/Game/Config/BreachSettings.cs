using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Config
{
    /// <summary>
    /// The one place for the destruction, healing and bar numbers (Ben 2026-11-21: "all in ONE ScriptableObject so I
    /// can tune without code changes"). Create one with Assets > Create > SCULPT > Breach Settings and drag it onto the
    /// Bootstrap object's Breach Settings slot. Without one, these code defaults are used. Movement numbers stay in
    /// Movement Settings.
    /// Item numbers are here too. Grenade, punch, charge, heal and bar numbers arrive with their steps. Read when the map is built (enter Play mode
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

        public void ApplyTo(ItemConfig items)
        {
            items.GrenadeCarry = grenadeCarry;
            items.Reach = pickupReach;
            items.AimConeDegrees = pickupConeDegrees;
            items.DropHoldTicks = Mathf.Max(1, Mathf.RoundToInt(dropHoldSeconds * SimTime.TicksPerSecond));
            items.MaxGroundItems = maxGroundItems;
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
        }
    }
}
