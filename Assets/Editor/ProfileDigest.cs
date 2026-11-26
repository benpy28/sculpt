using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

namespace Sculpt.EditorTools
{
    /// <summary>
    /// Reads a profiler capture made by a development build (-profiler-enable -profiler-log-file file.raw) and writes a
    /// plain text digest: per sample name, the self time and the garbage (GC.Alloc) it made, summed over all frames, plus
    /// the slowest frames' top samples. Command line (Editor closed or another project folder):
    /// Unity -batchmode -quit -projectPath . -executeMethod Sculpt.EditorTools.ProfileDigest.Run -profile file.raw -digest out.txt
    /// </summary>
    public static class ProfileDigest
    {
        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string raw = Arg(args, "-profile"), output = Arg(args, "-digest") ?? Path.ChangeExtension(raw, ".txt");
            if (raw == null || !File.Exists(raw)) { Debug.LogError("[SCULPT] ProfileDigest: no -profile file"); return; }
            if (!ProfilerDriver.LoadProfile(raw, false)) { Debug.LogError("[SCULPT] ProfileDigest: could not load " + raw); return; }
            int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
            var self = new Dictionary<string, double>();
            var gc = new Dictionary<string, double>();
            var frameMs = new List<(int frame, double ms, string top)>();
            long gcTotal = 0;
            int frames = 0;
            for (int f = first; f <= last; f++)
            {
                var top = new List<(string, double)>();
                double frameTime = 0;
                bool any = false;
                for (int th = 0; th < 64; th++)
                {
                    using (HierarchyFrameDataView v = ProfilerDriver.GetHierarchyFrameDataView(f, th, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnSelfTime, false))
                    {
                        if (v == null || !v.valid) { if (th > 0) break; continue; }
                        string tn = th == 0 ? "" : "[" + v.threadName + "] ";
                        if (th == 0) { frameTime = v.frameTimeMs; any = true; }
                        Walk(v, v.GetRootItemID(), tn, tn, self, gc, top, ref gcTotal);
                    }
                }
                if (!any) continue;
                frames++;
                top.Sort((a, b) => b.Item2.CompareTo(a.Item2));
                frameMs.Add((f, frameTime, string.Join(", ", top.Take(8).Select(t => $"{t.Item1} {t.Item2:0.0}"))));
            }
            var sb = new StringBuilder();
            sb.AppendLine($"Profile {raw}: {frames} frames, garbage {gcTotal / 1024.0 / Math.Max(1, frames):0.00} KB per frame");
            sb.AppendLine("Garbage by sample (KB per frame, self):");
            foreach (var kv in gc.OrderByDescending(k => k.Value).Take(40)) sb.AppendLine($"  {kv.Value / 1024.0 / frames,9:0.000}  {kv.Key}");
            sb.AppendLine("Self time by sample (ms per frame):");
            foreach (var kv in self.OrderByDescending(k => k.Value).Take(50)) sb.AppendLine($"  {kv.Value / frames,9:0.000}  {kv.Key}");
            sb.AppendLine("Slowest frames:");
            foreach (var fr in frameMs.OrderByDescending(x => x.ms).Take(25)) sb.AppendLine($"  frame {fr.frame} {fr.ms:0.0} ms: {fr.top}");
            File.WriteAllText(output, sb.ToString());
            Debug.Log("[SCULPT] ProfileDigest written: " + output);
        }

        private static void Walk(HierarchyFrameDataView v, int id, string tn, string parent, Dictionary<string, double> self, Dictionary<string, double> gc,
            List<(string, double)> top, ref long gcTotal)
        {
            var children = new List<int>();
            v.GetItemChildren(id, children);
            foreach (int c in children)
            {
                string name = tn + v.GetItemName(c);
                double s = v.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnSelfTime);
                // GC.Alloc samples are leaves; their size is the gc memory column. Attribute to the parent sample.
                if (name == "GC.Alloc")
                {
                    double bytes = v.GetItemColumnDataAsDouble(c, HierarchyFrameDataView.columnGcMemory);
                    gcTotal += (long)bytes;
                    gc[parent] = (gc.TryGetValue(parent, out double g) ? g : 0) + bytes;
                    continue;
                }
                self[name] = (self.TryGetValue(name, out double t) ? t : 0) + s;
                if (s > 0.5) top.Add((name, s));
                Walk(v, c, tn, name, self, gc, top, ref gcTotal);
            }
        }

        private static string Arg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
