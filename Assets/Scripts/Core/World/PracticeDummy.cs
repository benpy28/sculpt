using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>Practice dummy numbers (Breach Settings > Practice dummy).</summary>
    public sealed class DummyConfig
    {
        /// <summary>A watched tile has to stay broken this long before the dummy heals it (3 s).</summary>
        public int HealDelayTicks = 180 * SimTime.Per60;
        /// <summary>After a press (refused, cancelled, or one that worked) the dummy waits this long before its next one.</summary>
        public int RetryTicks = 45 * SimTime.Per60;
        /// <summary>Also heal cracked (Damaged) tiles, not only broken ones. Off: you can practise charging through cracks.</summary>
        public bool HealDamaged = false;
        /// <summary>Knocked further than this from its post, it walks back.</summary>
        public double PostSlack = 0.4;
    }

    /// <summary>
    /// The practice dummy's brain (Ben 2026-11-21, step 7): a server-side player with no connection that stands at its
    /// post in a room and heals the room's broken tiles after a delay, through the normal rules (sculpt mode, heal
    /// press, bar, range, "nobody inside the tile", one at a time). So you can practise break, push through and the
    /// heal window alone. It can be shot, punched, blown about (it walks back to its post) and killed (it respawns at
    /// its post like anyone). It only produces normal input commands; the server checks them like any player's.
    /// </summary>
    public sealed class PracticeDummy
    {
        public readonly Vec3d Post;
        public readonly double PostYaw;
        public readonly DummyConfig Config;
        private readonly int[] _tiles;
        private readonly Dictionary<int, int> _brokenSince = new Dictionary<int, int>();
        private int _nextPress = int.MinValue;
        private double _yaw;
        private float _pitch;

        public PracticeDummy(Vec3d post, double postYaw, IEnumerable<int> watchedTiles, DummyConfig config = null)
        {
            Post = post;
            PostYaw = postYaw;
            _yaw = postYaw;
            Config = config ?? new DummyConfig();
            _tiles = new List<int>(watchedTiles).ToArray();
        }

        public IReadOnlyList<int> Watched => _tiles;

        /// <summary>The tick a watched tile was first seen needing a heal, or int.MinValue.</summary>
        public int NeedsHealSince(int tile) => _brokenSince.TryGetValue(tile, out int t) ? t : int.MinValue;

        /// <summary>This tick's command for the dummy's player.</summary>
        public Net.InputCmd Next(int tick, in MotorState me, TileWalls walls, TileHealing healing, int myId, MovementConfig mc)
        {
            var cmd = new Net.InputCmd { Tick = tick, ViewTick = tick };
            if (me.Dead) { _brokenSince.Clear(); _yaw = PostYaw; return Finish(cmd); }
            if (walls == null || healing == null) return Finish(cmd);

            // Which watched tiles need healing, and since when.
            foreach (int t in _tiles)
            {
                // Broken, or with a hole blown in it (blast holes leave the rest of a tile standing, cracked).
                bool needs = walls[t].State == TileState.Broken || walls[t].Holed || (Config.HealDamaged && walls[t].State == TileState.Damaged);
                if (!needs) _brokenSince.Remove(t);
                else if (!_brokenSince.ContainsKey(t)) _brokenSince[t] = tick;
            }

            // Knocked off its post: walk back (facing the way it walks).
            Vec3d off = new Vec3d(Post.X - me.Position.X, 0, Post.Z - me.Position.Z);
            bool away = off.Length > Config.PostSlack;
            if (away)
            {
                _yaw = Math.Atan2(off.X, off.Z);
                cmd.Move.MoveZ = Math.Min(1, off.Length / 1.5 + 0.35);
            }

            Vec3d eye = me.Position + new Vec3d(0, PlayerMovement.EyeOf(me, mc), 0);
            if (healing.TryGetJob(myId, out HealJob job)) { if (!away) Face(walls, job.Tile, job.Cell, eye); return Finish(cmd); }

            int pick = -1, oldest = int.MaxValue;
            foreach (var kv in _brokenSince)
            {
                if (tick - kv.Value < Config.HealDelayTicks || healing.HealerOf(kv.Key) != 0) continue;
                if (kv.Value < oldest || (kv.Value == oldest && kv.Key < pick)) { oldest = kv.Value; pick = kv.Key; }
            }
            if (pick < 0) { if (!away) { _yaw = PostYaw; _pitch = 0; } return Finish(cmd); }
            int cell = CellOf(walls, pick);
            if (!away) Face(walls, pick, cell, eye);
            if (mc.Hand.QuickHeal)
            {
                if (me.Hand.InSculptMode) { cmd.Hand.SculptToggle = true; return Finish(cmd); }
                if (!mc.Hand.CanHeal(me.Hand) || tick < _nextPress) return Finish(cmd);
            }
            else
            {
                if (!me.Hand.InSculptMode) { cmd.Hand.SculptToggle = true; return Finish(cmd); }
                if (!me.Hand.CanConfirmSculpt || tick < _nextPress) return Finish(cmd);
            }
            cmd.Hand.Heal = true;
            cmd.Hand.HealTile = pick;
            cmd.Hand.HealCell = cell;
            _nextPress = tick + Config.RetryTicks;
            return Finish(cmd);
        }

        /// <summary>The cell to aim a heal at: the first open cell (a hole), else the middle (a broken or cracked tile).</summary>
        private static int CellOf(TileWalls walls, int tile)
        {
            if (!walls.HasCells(tile)) return 0;
            ulong open = walls[tile].Open;
            for (int c = 0; c < 64; c++) if (((open >> c) & 1) != 0) return c;
            return TileWalls.MiddleCell;
        }

        private void Face(TileWalls walls, int tile, int cell, Vec3d eye)
        {
            Aabb b = walls.HasCells(tile) ? walls.CellBox(tile, cell) : walls.BoundsOf(tile);
            Vec3d d = (b.Min + b.Max) * 0.5 - eye;
            _yaw = Math.Atan2(d.X, d.Z);
            double flat = Math.Sqrt(d.X * d.X + d.Z * d.Z);
            _pitch = (float)(-Math.Atan2(d.Y, Math.Max(1e-6, flat)) * 180 / Math.PI);
        }

        private Net.InputCmd Finish(Net.InputCmd cmd)
        {
            cmd.Move.Yaw = _yaw;
            cmd.Pitch = _pitch;
            return Net.InputCmd.Quantize(cmd);
        }
    }
}
