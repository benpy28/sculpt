using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Config
{
    /// <summary>
    /// The one place for the destruction, healing and bar numbers (Ben 2026-11-21: "all in ONE ScriptableObject so I
    /// can tune without code changes"). Create one with Assets > Create > SCULPT > Breach Settings and drag it onto the
    /// Bootstrap object's Breach Settings slot. Without one, these code defaults are used. Movement numbers stay in
    /// Movement Settings.
    /// Step 1 holds only the parked block-moving switch; tile, grenade, punch, heal and bar numbers arrive with their steps.
    /// </summary>
    [CreateAssetMenu(menuName = "SCULPT/Breach Settings", fileName = "BreachSettings")]
    public sealed class BreachSettings : ScriptableObject
    {
        [Header("Parked")]
        [Tooltip("The old six-direction block moving (push, pull, raise, sink, throw). Off since the 2026-11-21 pivot.")]
        public bool blockMovesEnabled = false;

        public void ApplyTo(SculptConfig sculpt)
        {
            sculpt.BlockMovesEnabled = blockMovesEnabled;
        }
    }
}
