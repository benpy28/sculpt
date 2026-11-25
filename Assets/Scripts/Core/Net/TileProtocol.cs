using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>Wall tile changes of one server tick (or, with Join, the state of every tile that ever changed).</summary>
    public struct TilesMsg
    {
        public int Tick;
        public bool Join;
        public List<TileChange> Changes;
        /// <summary>Heals and reinforces that began, finished, were cancelled or refused this tick (with Join: every heal
        /// still running, as Began). May be null when writing (none).</summary>
        public List<HealEvent> Heals;
    }

    /// <summary>
    /// Wire format of <see cref="TilesMsg"/>: per change the tile id, new state, health, cause and who caused it
    /// (8 bytes), plus the tile's blown-out cells (8 more) when it has any (flag 0x80 on the state byte). The From
    /// state is not sent: the client reads it from its own copy when it applies the change.
    /// Then the heal events: type and kind, player, tile, ticks to the heal's end, result (8 bytes each).
    /// </summary>
    public static class TileProtocol
    {
        public const int MaxChanges = ushort.MaxValue;

        public static void Write(ByteWriter w, in TilesMsg m)
        {
            int n = m.Changes.Count;
            if (n > MaxChanges) throw new System.ArgumentException("Too many tile changes.");
            w.U8((byte)MsgType.Tiles);
            w.I32(m.Tick);
            w.Bool(m.Join);
            w.U16((ushort)n);
            for (int i = 0; i < n; i++)
            {
                TileChange c = m.Changes[i];
                w.U16((ushort)c.Tile);
                w.U8((byte)((byte)c.To | (c.Open != 0 ? 0x80 : 0)));
                w.U16((ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, c.Health)));
                w.U8((byte)c.Cause);
                w.U16((ushort)c.By);
                if (c.Open != 0)
                {
                    w.I64(unchecked((long)c.Open));
                }
            }
            int hn = m.Heals?.Count ?? 0;
            if (hn > MaxChanges) throw new System.ArgumentException("Too many heal events.");
            w.U16((ushort)hn);
            for (int i = 0; i < hn; i++)
            {
                HealEvent e = m.Heals[i];
                w.U8((byte)((byte)e.Type | ((byte)e.Kind << 4)));
                w.U16((ushort)e.Player);
                w.U16((ushort)e.Tile);
                w.U16((ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, e.EndTick - m.Tick)));
                w.U8((byte)e.Result);
            }
        }

        public static TilesMsg Read(ref ByteReader r, List<TileChange> into, List<HealEvent> heals)
        {
            if (r.U8() != (byte)MsgType.Tiles) throw new NetFormatException("Unexpected message type.");
            into.Clear();
            heals.Clear();
            var m = new TilesMsg { Tick = r.I32(), Join = r.Bool(), Changes = into, Heals = heals };
            int n = r.U16();
            for (int i = 0; i < n; i++)
            {
                var c = new TileChange { Tick = m.Tick, Tile = r.U16() };
                byte s = r.U8();
                bool holed = (s & 0x80) != 0;
                s &= 0x7F;
                if (s > (byte)TileState.Reinforced) throw new NetFormatException("Bad tile state.");
                c.To = (TileState)s;
                c.Health = r.U16();
                byte cause = r.U8();
                if (cause > (byte)TileCause.Server) throw new NetFormatException("Bad tile cause.");
                c.Cause = (TileCause)cause;
                c.By = r.U16();
                if (holed)
                {
                    c.Open = unchecked((ulong)r.I64());
                }
                into.Add(c);
            }
            int hn = r.U16();
            for (int i = 0; i < hn; i++)
            {
                byte tk = r.U8();
                var e = new HealEvent { Type = (HealEventType)(tk & 15), Kind = (HealKind)(tk >> 4), Tick = m.Tick };
                if (e.Type > HealEventType.Rejected || e.Kind > HealKind.Reinforce) throw new NetFormatException("Bad heal event.");
                e.Player = r.U16();
                e.Tile = r.U16();
                e.EndTick = m.Tick + r.U16();
                byte res = r.U8();
                if (res > (byte)HealResult.Replaced) throw new NetFormatException("Bad heal result.");
                e.Result = (HealResult)res;
                heals.Add(e);
            }
            return m;
        }
    }
}
