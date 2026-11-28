using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Doors you open and close (Ben 2026-11-25). A door is one or two leaves (<see cref="TileKind.Door"/>) that open
    /// and close together with the interact key. Open leaves slide into the frame: <see cref="TileState.DoorOpen"/>, no
    /// collision, health kept. A door that is open can't be damaged (there is nothing in the doorway to hit); a shut one
    /// breaks as easily as ever. Closing is refused while anyone stands in a leaf's space, so nobody is ever shut inside
    /// one. Server-authoritative: the client applies its own press at once and the server's answer replaces it.
    /// </summary>
    public static class DoorRules
    {
        /// <summary>Is this tile a door leaf that is still there (open or shut)?</summary>
        public static bool IsLeaf(TileWalls w, int tile) =>
            tile >= 0 && tile < w.Count && w[tile].Kind == TileKind.Door && w[tile].State != TileState.Broken;

        public static bool IsOpen(TileWalls w, int tile) => w[tile].State == TileState.DoorOpen;

        /// <summary>The door's leaves that are still there (the tile and its partner).</summary>
        public static void Leaves(TileWalls w, int tile, List<int> into)
        {
            into.Clear();
            if (IsLeaf(w, tile)) into.Add(tile);
            int p = w.DoorPartner(tile);
            if (IsLeaf(w, p)) into.Add(p);
        }

        /// <summary>
        /// The door leaf the player is aiming at within <paramref name="reach"/> of the eye, open or shut: the nearest
        /// leaf box the look ray passes through with nothing solid in front of it. -1 if none.
        /// </summary>
        public static int FindTarget(TileWalls w, ITerrain terrain, Vec3d eye, Vec3d dir, double reach)
        {
            int best = -1;
            double bestDist = double.MaxValue;
            IReadOnlyList<int> doors = w.Doors;
            for (int i = 0; i < doors.Count; i++)
            {
                int t = doors[i];
                if (w[t].State == TileState.Broken) continue;
                Aabb b = w.BoundsOf(t).Expand(0.05);
                if (!Hitscan.RayBox(eye, dir, b, out double d, out _) || d > reach || d >= bestDist) continue;
                // Nothing solid before it (a shut leaf is itself solid: the ray may stop on it).
                TerrainHit h = terrain.Raycast(eye, dir, d + 0.02);
                if (h.Hit && h.Distance < d - 0.02 && w.TileOfHit(h) != t) continue;
                best = t;
                bestDist = d;
            }
            return best;
        }

        /// <summary>Distance from a point to the nearest point of a box.</summary>
        public static double DistanceTo(Aabb b, Vec3d p)
        {
            double dx = Math.Max(0, Math.Max(b.Min.X - p.X, p.X - b.Max.X));
            double dy = Math.Max(0, Math.Max(b.Min.Y - p.Y, p.Y - b.Max.Y));
            double dz = Math.Max(0, Math.Max(b.Min.Z - p.Z, p.Z - b.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Can the door be set to <paramref name="open"/>? Closing needs every leaf's space empty of <paramref name="bodies"/>.</summary>
        public static bool CanSet(TileWalls w, int tile, bool open, IReadOnlyList<Aabb> bodies, List<int> scratch)
        {
            Leaves(w, tile, scratch);
            if (scratch.Count == 0) return false;
            bool any = false;
            foreach (int l in scratch)
            {
                if (IsOpen(w, l) == open) continue;
                any = true;
                if (open || bodies == null) continue;
                Aabb b = w.BoundsOf(l);
                for (int k = 0; k < bodies.Count; k++)
                    if (bodies[k].Overlaps(b)) return false;
            }
            return any;
        }

        /// <summary>
        /// Is the door still cooling down from a press (<see cref="BreachConfig.DoorToggleSeconds"/>) on
        /// <paramref name="tick"/>? Then any press on it is refused quietly. Checked on the server and in the client's
        /// own prediction, so both agree.
        /// </summary>
        public static bool Cooling(TileWalls w, int tile, int tick, List<int> scratch)
        {
            Leaves(w, tile, scratch);
            int cd = SimTime.SecondsToTicks(w.Config.DoorToggleSeconds);
            foreach (int l in scratch)
            {
                int at = w.DoorToggledAt(l);
                if (at != int.MinValue && tick - at < cd) return true;
            }
            return false;
        }

        /// <summary>Opens or shuts every leaf of the door (health kept). Returns how many leaves changed.</summary>
        public static int Set(TileWalls w, int tile, bool open, TileCause cause, int by, List<int> scratch)
        {
            Leaves(w, tile, scratch);
            int n = 0;
            foreach (int l in scratch)
            {
                WallTile t = w[l];
                if ((t.State == TileState.DoorOpen) == open) continue;
                TileState to = open ? TileState.DoorOpen : w.ShutStateOf(l);
                w.Set(l, to, t.Health, cause, by);
                n++;
            }
            return n;
        }

        /// <summary>The state a leaf with this health is in when shut.</summary>
        public static TileState ShutState(BreachConfig c, int health) =>
            health > c.IntactHealth ? TileState.Reinforced
            : health < c.IntactHealth * c.DamagedBelowFraction ? TileState.Damaged
            : TileState.Intact;
    }

    /// <summary>
    /// The client's own door presses: applied to its copy of the walls on the tick pressed (so you walk through a door
    /// you just opened at any ping), then the server's Tiles message makes it official. If no answer about that door
    /// comes within the timeout (the server refused), the door goes back to what the server last said.
    /// </summary>
    public sealed class PredictedDoors
    {
        private struct Pending { public int Leaf, Tick; }
        private readonly List<Pending> _pending = new List<Pending>();
        private readonly Dictionary<int, (TileState State, int Health)> _server = new Dictionary<int, (TileState, int)>();
        private readonly List<int> _leaves = new List<int>();

        public int PendingCount => _pending.Count;

        /// <summary>Your press on <paramref name="tick"/>: changes the local walls at once.</summary>
        public void Press(TileWalls w, int tile, bool open, int tick, int by, IReadOnlyList<Aabb> bodies)
        {
            if (!DoorRules.CanSet(w, tile, open, bodies, _leaves)) return;
            if (DoorRules.Cooling(w, tile, tick, _leaves)) return; // the server refuses it too: nothing to show
            foreach (int l in _leaves)
            {
                bool pending = false;
                foreach (Pending p in _pending) if (p.Leaf == l) { pending = true; break; }
                if (!pending && !_server.ContainsKey(l)) _server[l] = (w[l].State, w[l].Health);
                _pending.Add(new Pending { Leaf = l, Tick = tick });
            }
            int keep = w.Tick;
            w.Tick = tick;
            DoorRules.Set(w, tile, open, TileCause.Door, by, _leaves);
            w.Tick = keep;
        }

        /// <summary>
        /// Is there a press of yours on this leaf newer than <paramref name="msgTick"/>? Then a server change from that
        /// tick (the answer to an older press) must not overwrite what your newer press shows; it is only remembered.
        /// </summary>
        public bool HasNewerPress(int tile, int msgTick)
        {
            foreach (Pending p in _pending) if (p.Leaf == tile && p.Tick > msgTick) return true;
            return false;
        }

        /// <summary>A tile change from the server (call for every change in a Tiles message for <paramref name="msgTick"/>).</summary>
        public void OnServerChange(TileWalls w, int tile, TileState state, int health, int msgTick)
        {
            if (tile < 0 || tile >= w.Count || w[tile].Kind != TileKind.Door) return;
            _server[tile] = (state, health);
            for (int i = _pending.Count - 1; i >= 0; i--)
                if (_pending[i].Leaf == tile && _pending[i].Tick <= msgTick) _pending.RemoveAt(i);
        }

        /// <summary>Presses older than <paramref name="timeoutTicks"/> with no answer: put the door back as the server has it.</summary>
        public void Expire(TileWalls w, int tick, int timeoutTicks)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                if (tick - p.Tick < timeoutTicks) continue;
                _pending.RemoveAt(i);
                bool newer = false;
                foreach (Pending q in _pending) if (q.Leaf == p.Leaf) { newer = true; break; }
                if (newer || !_server.TryGetValue(p.Leaf, out var s)) continue;
                if (w[p.Leaf].State != s.State || w[p.Leaf].Health != s.Health) w.Set(p.Leaf, s.State, s.Health, TileCause.Server);
            }
        }

        public void Clear() { _pending.Clear(); _server.Clear(); }
    }
}
