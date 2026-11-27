using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Settings
{
    /// <summary>
    /// The auto-sort on pickup (Ben 2026-11-26: "you can choose a preference in settings (default pump 1, AR 2 and heals
    /// 5) ... auto-sort only applies when you pick an item up"). Saved on this PC; Esc settings changes it. The server
    /// gets it in the Prefs message after joining and whenever it changes (it decides where a pickup goes).
    /// </summary>
    public static class SlotPrefsStore
    {
        private const string Key = "sculpt.slotPrefs";

        public static SlotPrefs Load()
        {
            string s = PlayerPrefs.GetString(Key, "");
            if (s.Length != Items.Slots) return SlotPrefs.Default;
            var p = new SlotPrefs();
            for (int k = 0; k < Items.Slots; k++)
            {
                int v = s[k] - '0';
                p[k] = v >= 0 && v <= (int)SlotPrefs.Last ? (SlotPref)v : SlotPref.Any;
            }
            return p;
        }

        public static void Save(in SlotPrefs p)
        {
            var c = new char[Items.Slots];
            for (int k = 0; k < Items.Slots; k++) c[k] = (char)('0' + (int)p[k]);
            PlayerPrefs.SetString(Key, new string(c));
            PlayerPrefs.Save();
        }

        /// <summary>The next choice for a slot (Anything, Pump, SMG, AR, Pistol, Heals, Grenades, C4, Launcher, round again).</summary>
        public static SlotPref Next(SlotPref p) => p >= SlotPrefs.Last ? SlotPref.Any : p + 1;
    }
}
