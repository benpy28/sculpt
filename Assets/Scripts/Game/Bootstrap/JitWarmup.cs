using System;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace Sculpt.Game.Bootstrap
{
    /// <summary>
    /// Mono compiles each method the first time it runs, so the first seconds of a match paid for compiling the whole
    /// simulation (server tick, bots, sight checks, shots) inside play: 100-250 ms frames. This compiles every method
    /// of the simulation (Sculpt.Core, which has no Unity code) on a worker thread while the map loads, so play starts
    /// warm. Asking Mono for a method's function pointer compiles it without running it. Generic methods and methods
    /// of generic types are left to compile on first use (each instantiation is its own code). Not needed (and a no-op
    /// in effect) under IL2CPP, where everything is compiled ahead of time.
    /// </summary>
    public static class JitWarmup
    {
        private static Thread _thread;
        public static volatile bool Done;
        public static int Compiled, Skipped;
        public static long Milliseconds;

        public static void Start(Assembly core)
        {
            if (_thread != null) return;
            _thread = new Thread(() => Run(core)) { IsBackground = true, Name = "JIT warm-up", Priority = System.Threading.ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private static void Run(Assembly core)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            try
            {
                // Closed generic types the simulation holds in its fields (CowList<WallTile>, Dictionary<long, int>, List<Brush>
                // ...): each is its own code, compiled on first use otherwise, which was mid-match (the first fights).
                var generic = new System.Collections.Generic.HashSet<Type>();
                foreach (Type t in core.GetTypes())
                {
                    if (t.ContainsGenericParameters) { Skipped++; continue; }
                    foreach (MethodInfo m in t.GetMethods(all)) Prepare(m);
                    foreach (ConstructorInfo c in t.GetConstructors(all)) if (!c.IsStatic) Prepare(c);
                    foreach (FieldInfo f in t.GetFields(all)) Collect(f.FieldType, generic);
                }
                foreach (Type g in generic)
                {
                    foreach (MethodInfo m in g.GetMethods(all)) Prepare(m);
                    foreach (ConstructorInfo c in g.GetConstructors(all)) if (!c.IsStatic) Prepare(c);
                }
            }
            catch (Exception e) { Debug.LogWarning("[SCULPT] JIT warm-up stopped: " + e.Message); }
            Milliseconds = sw.ElapsedMilliseconds;
            Done = true;
            Debug.Log($"[SCULPT] JIT warm-up: {Compiled} simulation methods compiled in {Milliseconds} ms ({Skipped} generic or abstract left for first use).");
        }

        /// <summary>A closed generic type and the closed generic types inside it (a dictionary's key and value lists).</summary>
        private static void Collect(Type t, System.Collections.Generic.HashSet<Type> into)
        {
            if (t.IsArray) { Collect(t.GetElementType(), into); return; }
            if (!t.IsGenericType || t.ContainsGenericParameters || !into.Add(t)) return;
            foreach (Type a in t.GetGenericArguments()) Collect(a, into);
            foreach (Type n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (n.IsGenericTypeDefinition && n.GetGenericArguments().Length == t.GetGenericArguments().Length)
                    try { Collect(n.MakeGenericType(t.GetGenericArguments()), into); } catch { }
        }

        private static void Prepare(MethodBase m)
        {
            if (m.IsAbstract || m.ContainsGenericParameters || (m.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0) { Skipped++; return; }
            try
            {
                m.MethodHandle.GetFunctionPointer();
                Compiled++;
            }
            catch { Skipped++; }
        }
    }
}
