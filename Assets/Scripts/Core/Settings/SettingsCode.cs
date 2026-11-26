using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Sculpt.Core.Settings
{
    /// <summary>
    /// A settings code: every setting packed into one line of text you can copy to another PC and paste in
    /// (Settings > Profile). Until accounts exist this is how settings follow you. Format: <c>SCULPT1-</c> then
    /// URL-safe base64 of the deflated UTF-8 lines <c>key=value</c>. Decoding never throws: junk, a wrong prefix, a
    /// bad key or a code over <see cref="MaxBytes"/> gives null, and nothing is changed.
    /// </summary>
    public static class SettingsCode
    {
        public const string Prefix = "SCULPT1-";
        /// <summary>Most bytes a decoded code may hold (stops a pasted bomb).</summary>
        public const int MaxBytes = 64 * 1024;

        public static bool ValidKey(string k)
        {
            if (string.IsNullOrEmpty(k) || k.Length > 64) return false;
            foreach (char c in k)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '_')) return false;
            return true;
        }

        public static string Encode(IEnumerable<KeyValuePair<string, string>> values)
        {
            var sb = new StringBuilder();
            var keys = new List<KeyValuePair<string, string>>(values);
            keys.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key)); // the same settings always give the same code
            foreach (var kv in keys)
            {
                if (!ValidKey(kv.Key)) throw new ArgumentException("Bad settings key: " + kv.Key);
                sb.Append(kv.Key).Append('=').Append(Escape(kv.Value ?? "")).Append('\n');
            }
            byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            return Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>The settings in a code, or null if it isn't a valid code.</summary>
        public static Dictionary<string, string> Decode(string code)
        {
            if (code == null) return null;
            code = code.Trim();
            if (!code.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            string b64 = code.Substring(Prefix.Length).Replace('-', '+').Replace('_', '/');
            if (b64.Length == 0 || b64.Length > MaxBytes * 2) return null;
            while (b64.Length % 4 != 0) b64 += "=";
            try
            {
                byte[] packed = Convert.FromBase64String(b64);
                using var z = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                var buf = new byte[4096];
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    outMs.Write(buf, 0, n);
                    if (outMs.Length > MaxBytes) return null;
                }
                string text = Encoding.UTF8.GetString(outMs.ToArray());
                var map = new Dictionary<string, string>();
                foreach (string line in text.Split('\n'))
                {
                    if (line.Length == 0) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) return null;
                    string k = line.Substring(0, eq);
                    if (!ValidKey(k) || map.ContainsKey(k)) return null;
                    string v = Unescape(line.Substring(eq + 1));
                    if (v == null) return null;
                    map[k] = v;
                }
                return map.Count > 0 ? map : null; // a real code always holds settings
            }
            catch (Exception) { return null; } // FormatException, InvalidDataException and friends: not a code
        }

        private static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");

        private static string Unescape(string v)
        {
            if (v.IndexOf('\\') < 0) return v;
            var sb = new StringBuilder(v.Length);
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c != '\\') { sb.Append(c); continue; }
                if (++i >= v.Length) return null;
                char e = v[i];
                if (e == '\\') sb.Append('\\');
                else if (e == 'n') sb.Append('\n');
                else if (e == 'r') sb.Append('\r');
                else return null;
            }
            return sb.ToString();
        }
    }
}
