using System.Globalization;

namespace Sculpt.Core.Settings
{
    /// <summary>
    /// Stable names for key binds in saved bind overrides. The Input System saves an override under the binding's id,
    /// and the game builds its actions from code, so every binding gets a new random id each run: a saved override (or a
    /// settings code from another PC) would never find its binding again. Saved overrides use
    /// <c>sculpt:&lt;action name&gt;#&lt;binding index&gt;</c> instead, which is the same every run and on every PC.
    /// </summary>
    public static class BindIds
    {
        public const string Prefix = "sculpt:";

        public static string Key(string actionName, int bindingIndex) =>
            Prefix + actionName + "#" + bindingIndex.ToString(CultureInfo.InvariantCulture);

        /// <summary>The action name and binding index in a key made by <see cref="Key"/>; false for anything else.</summary>
        public static bool TryParse(string key, out string actionName, out int bindingIndex)
        {
            actionName = null;
            bindingIndex = -1;
            if (key == null || !key.StartsWith(Prefix, System.StringComparison.Ordinal)) return false;
            int hash = key.LastIndexOf('#');
            if (hash <= Prefix.Length) return false;
            string digits = key.Substring(hash + 1);
            if (digits.Length == 0 || digits.Length > 4) return false;
            foreach (char c in digits) if (c < '0' || c > '9') return false;
            bindingIndex = int.Parse(digits, CultureInfo.InvariantCulture);
            actionName = key.Substring(Prefix.Length, hash - Prefix.Length);
            return true;
        }
    }
}
