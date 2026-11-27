using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Sculpt.Game.Settings
{
    /// <summary>
    /// One list of every saved setting of a settings class: its PlayerPrefs key and type, default, legal range and
    /// group (the settings page it is on). Load, save, defaults, reset-a-page and the settings code all go through it,
    /// so a new setting is one line and can't be forgotten by one of them. Keys and value types are the ones already in
    /// players' PlayerPrefs, so nobody loses settings.
    /// </summary>
    public sealed class PrefTable<T>
    {
        private abstract class Pref
        {
            public string Key, Group;
            public abstract void Load(T s);
            public abstract void Save(T s);
            public abstract void Default(T s);
            public abstract string Get(T s);
            public abstract bool Set(T s, string v);
        }

        private sealed class BoolPref : Pref
        {
            public bool Def; public Func<T, bool> G; public Action<T, bool> S;
            public override void Load(T s) => S(s, PlayerPrefs.GetInt(Key, Def ? 1 : 0) == 1);
            public override void Save(T s) => PlayerPrefs.SetInt(Key, G(s) ? 1 : 0);
            public override void Default(T s) => S(s, Def);
            public override string Get(T s) => G(s) ? "1" : "0";
            public override bool Set(T s, string v) { if (v != "0" && v != "1") return false; S(s, v == "1"); return true; }
        }

        private sealed class FloatPref : Pref
        {
            public float Def, Min, Max; public Func<T, float> G; public Action<T, float> S;
            private float Clamp(float v) => float.IsNaN(v) || float.IsInfinity(v) ? Def : Mathf.Clamp(v, Min, Max);
            public override void Load(T s) => S(s, Clamp(PlayerPrefs.GetFloat(Key, Def)));
            public override void Save(T s) => PlayerPrefs.SetFloat(Key, G(s));
            public override void Default(T s) => S(s, Def);
            public override string Get(T s) => G(s).ToString("R", CultureInfo.InvariantCulture);
            public override bool Set(T s, string v)
            {
                if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return false;
                S(s, Clamp(f));
                return true;
            }
        }

        private sealed class IntPref : Pref
        {
            public int Def, Min, Max; public Func<T, int> G; public Action<T, int> S;
            public override void Load(T s) => S(s, Mathf.Clamp(PlayerPrefs.GetInt(Key, Def), Min, Max));
            public override void Save(T s) => PlayerPrefs.SetInt(Key, G(s));
            public override void Default(T s) => S(s, Def);
            public override string Get(T s) => G(s).ToString(CultureInfo.InvariantCulture);
            public override bool Set(T s, string v)
            {
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return false;
                S(s, Mathf.Clamp(i, Min, Max));
                return true;
            }
        }

        private readonly List<Pref> _prefs = new List<Pref>();

        public PrefTable<T> Bool(string group, string key, bool def, Func<T, bool> get, Action<T, bool> set)
        {
            _prefs.Add(new BoolPref { Group = group, Key = key, Def = def, G = get, S = set });
            return this;
        }

        public PrefTable<T> Float(string group, string key, float def, float min, float max, Func<T, float> get, Action<T, float> set)
        {
            _prefs.Add(new FloatPref { Group = group, Key = key, Def = def, Min = min, Max = max, G = get, S = set });
            return this;
        }

        public PrefTable<T> Int(string group, string key, int def, int min, int max, Func<T, int> get, Action<T, int> set)
        {
            _prefs.Add(new IntPref { Group = group, Key = key, Def = def, Min = min, Max = max, G = get, S = set });
            return this;
        }

        public void Load(T s) { foreach (Pref p in _prefs) p.Load(s); }
        public void Save(T s) { foreach (Pref p in _prefs) p.Save(s); PlayerPrefs.Save(); }
        public void Defaults(T s) { foreach (Pref p in _prefs) p.Default(s); }

        /// <summary>Puts every setting of one group (settings page) back to its default.</summary>
        public void Defaults(T s, string group) { foreach (Pref p in _prefs) if (p.Group == group) p.Default(s); }

        public void Write(T s, IDictionary<string, string> into) { foreach (Pref p in _prefs) into[p.Key] = p.Get(s); }

        /// <summary>Takes every known key from <paramref name="from"/> (unknown keys and bad values are skipped).
        /// Returns how many were taken.</summary>
        public int Read(T s, IReadOnlyDictionary<string, string> from)
        {
            int n = 0;
            foreach (Pref p in _prefs)
                if (from.TryGetValue(p.Key, out string v) && p.Set(s, v)) n++;
            return n;
        }
    }
}
