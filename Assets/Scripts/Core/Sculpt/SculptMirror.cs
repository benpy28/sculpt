using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Hooks for a client's mirror of the sculpt system. A client never runs Step(): it applies the server's piece ops
    /// to its own grid, mirrors everyone's windups (for locks and Busy), and uses Evaluate() for the ghost and for its
    /// own prediction, so the client and server validate with the very same code.
    /// </summary>
    public sealed partial class SculptSystem
    {
        /// <summary>Sets the tick Evaluate() uses (reinforcement expiry). Client mirrors only.</summary>
        public void SetTick(int tick) => Tick = tick;

        /// <summary>The sub-cells a windup locks: the piece and everything it will sweep through. Sorted Y, Z, X.</summary>
        public static Int3[] LockedCellsOf(Int3[] cells, Int3 delta)
        {
            var set = new HashSet<Int3>(cells);
            int steps = Math.Abs(delta.X) + Math.Abs(delta.Y) + Math.Abs(delta.Z);
            Int3 unit = steps == 0 ? Int3.Zero : new Int3(Math.Sign(delta.X), Math.Sign(delta.Y), Math.Sign(delta.Z));
            foreach (var x in cells)
                for (int k = 1; k <= steps; k++) set.Add(x + unit * k);
            var list = new List<Int3>(set);
            list.Sort(Int3.CompareYZX);
            return list.ToArray();
        }

        /// <summary>Registers a windup someone else's system started (locks its sub-cells, makes that player Busy).
        /// Replaces any earlier mirrored windup of the same player. No bar, no carving, no events.</summary>
        public void MirrorPending(int playerId, SculptAction a, SculptCheck c, int startTick, int completeTick)
        {
            ClearMirrorPending(playerId);
            var pend = new PendingSculpt
            {
                PlayerId = playerId,
                Action = a,
                Check = c,
                StartTick = startTick,
                CompleteTick = completeTick,
                LockedCells = LockedCellsOf(c.Cells ?? Array.Empty<Int3>(), c.Delta),
            };
            foreach (var cell in pend.LockedCells) _locks[cell] = playerId;
            _pending[playerId] = pend;
        }

        public void ClearMirrorPending(int playerId)
        {
            if (!_pending.TryGetValue(playerId, out var pend)) return;
            ReleaseLocks(pend);
            _pending.Remove(playerId);
        }

        /// <summary>Forgets the support checks a mirror's grid writes queue up (a mirror never runs support).</summary>
        /// <summary>
        /// Client prediction of its own sculpt landing (ISSUE-8): runs the server's resolution check for this player and
        /// says where it will put them (shove or lift) and what friction kick it gives a rider. False if the server would
        /// cancel it. Call right before applying the landing, with the player's pending windup already cleared.
        /// </summary>
        public bool PlanLanding(PlayerBody p, SculptAction a, out bool moved, out Vec3d moveTo, out Vec3d kick)
        {
            moved = false;
            moveTo = p.Position;
            kick = Vec3d.Zero;
            SculptCheck c = EvaluateInternal(p, a, true);
            bool ok = c.IsValid;
            if (ok)
            {
                foreach (var s in _shoves) if (s.Key == p) { moved = true; moveTo = s.Value; }
                foreach (var d in _drags) if (d == p) kick += a.Direction.ToVec() * Config.RiderDragSpeed;
            }
            _shoves.Clear();
            _drags.Clear();
            ClearSupportQueue();
            return ok;
        }

        public void ClearSupportQueue()
        {
            _checkPieces.Clear();
            _checkNatural.Clear();
        }

        /// <summary>The projectile a throw of these sub-cells would launch, against the given terrain (the piece already gone).</summary>
        public ThrownPiece PredictProjectile(PlayerBody thrower, Int3[] cells, Vec3d look, ITerrain world)
        {
            ThrownPiece t = MakeProjectile(thrower, cells, look, world);
            t.Owner = thrower.Id;
            t.Team = thrower.Team;
            return t;
        }

        /// <summary>One tick of flight against terrain only (players ignored). True when it touches terrain.</summary>
        public bool AdvanceProjectile(ThrownPiece t, int tick, ITerrain world) => Advance(t, tick, world, false, out _);

        /// <summary>Projectile gravity per tick in fixed point (for clients that fly server-launched pieces).</summary>
        public long ProjectileGravityPerTick => GravityPerTick;
    }
}
