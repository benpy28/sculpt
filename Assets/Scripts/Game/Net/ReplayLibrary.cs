using System;
using System.Collections.Generic;
using System.IO;
using Sculpt.Core.Replay;
using UnityEngine;

namespace Sculpt.Game.Net
{
    /// <summary>
    /// Saved replays on this PC: the newest 20 are kept automatically, older ones are deleted, and starred ones (moved
    /// into Replays/Kept) are never deleted (Ben's home screen roadmap: auto last 20 + star to keep).
    /// Files live in the game's persistent data folder (on Windows: %USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;\Replays).
    /// </summary>
    public static class ReplayLibrary
    {
        public const int KeepRecent = 20;
        public const string Extension = ".sclr";

        public static string Folder => Path.Combine(Application.persistentDataPath, "Replays");
        public static string KeptFolder => Path.Combine(Folder, "Kept");

        public struct Entry
        {
            public string Path;
            public bool Kept;
            public DateTime When;
            public string Map, Mode;
            public double Seconds;
            public int Kills;
            /// <summary>Why it can't be played on this build, or null.</summary>
            public string Problem;

            public string Label => Problem != null ? $"{When:d MMM HH:mm}  ·  can't play ({Problem})"
                : $"{When:d MMM HH:mm}  ·  {Mode}  ·  {(int)(Seconds / 60)}:{(int)Seconds % 60:00}  ·  {Kills} kill{(Kills == 1 ? "" : "s")}";
        }

        /// <summary>A new file name for a recording starting now.</summary>
        public static string NewPath(string map)
        {
            Directory.CreateDirectory(Folder);
            string name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Safe(map)}";
            string path = Path.Combine(Folder, name + Extension);
            for (int k = 2; File.Exists(path); k++) path = Path.Combine(Folder, $"{name}_{k}{Extension}");
            return path;
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "map";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        /// <summary>Every saved replay, newest first (reads each file; they are small).</summary>
        public static List<Entry> List()
        {
            var list = new List<Entry>();
            Add(list, Folder, false);
            Add(list, KeptFolder, true);
            list.Sort((a, b) => b.When.CompareTo(a.When));
            return list;
        }

        private static void Add(List<Entry> list, string folder, bool kept)
        {
            if (!Directory.Exists(folder)) return;
            foreach (string path in Directory.GetFiles(folder, "*" + Extension))
            {
                var e = new Entry { Path = path, Kept = kept, When = File.GetLastWriteTime(path) };
                try
                {
                    ReplayFile f = ReplayFile.Read(File.ReadAllBytes(path));
                    e.Map = f.Header.Map;
                    e.Mode = f.Header.Mode.Length > 0 ? f.Header.Mode : f.Header.Map;
                    e.Seconds = f.Seconds;
                    e.Kills = f.Markers.Count;
                    if (f.Header.StartedUtcTicks > 0) e.When = new DateTime(f.Header.StartedUtcTicks, DateTimeKind.Utc).ToLocalTime();
                    e.Problem = f.Incompatible != null ? "another build" : null;
                }
                catch (Exception ex)
                {
                    e.Problem = "damaged";
                    Debug.LogWarning($"[SCULPT] Replay {Path.GetFileName(path)} can't be read: {ex.Message}");
                }
                list.Add(e);
            }
        }

        /// <summary>Deletes all but the newest <see cref="KeepRecent"/> unstarred replays.</summary>
        public static void Prune()
        {
            if (!Directory.Exists(Folder)) return;
            var files = new List<FileInfo>(new DirectoryInfo(Folder).GetFiles("*" + Extension));
            files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = KeepRecent; i < files.Count; i++)
            {
                try { files[i].Delete(); }
                catch (Exception ex) { Debug.LogWarning($"[SCULPT] Couldn't delete old replay {files[i].Name}: {ex.Message}"); }
            }
        }

        /// <summary>Stars (keeps forever) or unstars a replay. Returns its new path.</summary>
        public static string SetKept(string path, bool kept)
        {
            string dir = kept ? KeptFolder : Folder;
            Directory.CreateDirectory(dir);
            string to = Path.Combine(dir, Path.GetFileName(path));
            if (string.Equals(Path.GetFullPath(to), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return path;
            File.Move(path, to);
            return to;
        }
    }
}
