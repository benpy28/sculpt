using Sculpt.Core;
using Sculpt.Core.Net;
using UnityEngine;

namespace Sculpt.Game.Settings
{
    /// <summary>
    /// The battle royale locker gun (Ben's BR design: everyone starts with a common pump plus one basic gun picked in
    /// their locker). Saved on this PC; the home screen's Royale card changes it. Used by every local or hosted server
    /// for the local player. Players joining someone else's game start with that server's default (the AR) until the
    /// join message carries the pick.
    /// </summary>
    public static class LockerPick
    {
        private const string Key = "sculpt.lockerGun";

        public static Item Load()
        {
            Item g = (Item)PlayerPrefs.GetInt(Key, (int)Item.Ar);
            return NetServer.IsLockerChoice(g) ? g : Item.Ar;
        }

        public static void Save(Item gun)
        {
            if (!NetServer.IsLockerChoice(gun)) return;
            PlayerPrefs.SetInt(Key, (int)gun);
            PlayerPrefs.Save();
        }

        /// <summary>The next locker gun after <paramref name="gun"/> (SMG, AR, pistol, round again).</summary>
        public static Item Next(Item gun)
        {
            int i = System.Array.IndexOf(BrConfig.LockerChoices, gun);
            return BrConfig.LockerChoices[(i + 1) % BrConfig.LockerChoices.Length];
        }

        public static string Name(Item gun) => gun == Item.Smg ? "SMG" : gun == Item.Pistol ? "Pistol" : gun == Item.Ar ? "AR" : gun.ToString();
    }
}
