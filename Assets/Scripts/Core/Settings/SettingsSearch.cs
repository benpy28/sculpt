using System;
using System.Collections.Generic;
using System.Text;

namespace Sculpt.Core.Settings
{
    /// <summary>
    /// The settings menu's search. A query matches an entry when every word of the query starts a word of the entry's
    /// text (its name, section and extra keywords), ignoring case and punctuation, so "vsync", "v-sync" and "v sync" all
    /// find "V-Sync", and "sens" finds "Mouse sensitivity". Words joined by punctuation also count as one word
    /// ("vsync" matches "V-Sync"). An empty query matches everything.
    /// </summary>
    public static class SettingsSearch
    {
        /// <summary>Lower-case words of <paramref name="text"/>, plus each run of words joined by punctuation glued
        /// together ("v-sync" gives "v", "sync" and "vsync").</summary>
        public static List<string> Words(string text)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(text)) return words;
            var word = new StringBuilder();
            var glued = new StringBuilder();
            int partsInGlued = 0;
            void EndWord()
            {
                if (word.Length == 0) return;
                words.Add(word.ToString());
                glued.Append(word);
                partsInGlued++;
                word.Clear();
            }
            void EndGlued()
            {
                if (partsInGlued > 1) words.Add(glued.ToString());
                glued.Clear();
                partsInGlued = 0;
            }
            foreach (char ch in text)
            {
                if (char.IsLetterOrDigit(ch)) { word.Append(char.ToLowerInvariant(ch)); continue; }
                EndWord();
                if (char.IsWhiteSpace(ch)) EndGlued(); // a space ends a glued run; '-', '/', '.' etc. keep it going
            }
            EndWord();
            EndGlued();
            return words;
        }

        /// <summary>The query's words (lower case, punctuation dropped, no glued forms).</summary>
        public static List<string> QueryWords(string query)
        {
            var q = new List<string>();
            if (string.IsNullOrEmpty(query)) return q;
            var w = new StringBuilder();
            foreach (char ch in query)
            {
                if (char.IsLetterOrDigit(ch)) { w.Append(char.ToLowerInvariant(ch)); continue; }
                if (w.Length > 0) { q.Add(w.ToString()); w.Clear(); }
            }
            if (w.Length > 0) q.Add(w.ToString());
            return q;
        }

        /// <summary>True if every query word starts some word of the entry.</summary>
        public static bool Matches(IReadOnlyList<string> queryWords, IReadOnlyList<string> entryWords)
        {
            if (queryWords == null || queryWords.Count == 0) return true;
            if (entryWords == null) return false;
            foreach (string q in queryWords)
            {
                bool found = false;
                foreach (string e in entryWords)
                    if (e.StartsWith(q, StringComparison.Ordinal)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        public static bool Matches(string query, string entryText) => Matches(QueryWords(query), Words(entryText));
    }
}
