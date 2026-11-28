using System;

namespace Sculpt.Core.Diagnostics
{
    /// <summary>
    /// Named sections of the server tick and the client frame, for the game's profiler (Core has no Unity references, so
    /// the game plugs in Unity profiler markers here in development builds). Null hooks cost one branch each.
    /// </summary>
    public static class Prof
    {
        public enum Section
        {
            ServerTick, Bots, Players, Simulate, Shots, Grenades, Healing, Interest, Send, Snapshots, Replay, BrMatch,
            ClientReceive, ClientSnapshot, ClientTiles, ClientItems, ClientMatch, ClientOther, Count,
        }

        public static Action<int> BeginHook;
        public static Action<int> EndHook;

        public static void Begin(Section s) => BeginHook?.Invoke((int)s);
        public static void End(Section s) => EndHook?.Invoke((int)s);
    }
}
