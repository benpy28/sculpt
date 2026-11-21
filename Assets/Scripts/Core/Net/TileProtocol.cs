using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>Wall tile changes of one server tick (or, with Join, the state of every tile that ever changed).</summary>
    public struct TilesMsg
    {
        public int Tick;
        public bool Join;
        public List<TileChange> Changes;
    }

    /// <summary>
    /// Wire format of <see cref="TilesMsg"/>: per change the tile id, new state, health, cause and who caused it
    /// (7 bytes). The From state is not sent: the client reads it from its own copy when it applies the change.
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
                w.U8((byte)c.To);
                w.U16((ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, c.Health)));
                w.U8((byte)c.Cause);
                w.U16((ushort)c.By);
            }
        }

        public static TilesMsg Read(ref ByteReader r, List<TileChange> into)
        {
            if (r.U8() != (byte)MsgType.Tiles) throw new NetFormatException("Unexpected message type.");
            into.Clear();
            var m = new TilesMsg { Tick = r.I32(), Join = r.Bool(), Changes = into };
            int n = r.U16();
            for (int i = 0; i < n; i++)
            {
                var c = new TileChange { Tick = m.Tick, Tile = r.U16() };
                byte s = r.U8();
                if (s > (byte)TileState.Reinforced) throw new NetFormatException("Bad tile state.");
                c.To = (TileState)s;
                c.Health = r.U16();
                byte cause = r.U8();
                if (cause > (byte)TileCause.Server) throw new NetFormatException("Bad tile cause.");
                c.Cause = (TileCause)cause;
                c.By = r.U16();
                into.Add(c);
            }
            return m;
        }
    }
}
