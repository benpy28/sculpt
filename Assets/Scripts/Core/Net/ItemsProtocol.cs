using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>Every item on the ground, as of one server tick.</summary>
    public struct ItemsMsg
    {
        public int Tick;
        public List<GroundItem> Items;
    }

    /// <summary>
    /// Wire format of <see cref="ItemsMsg"/>. The server sends the whole list (it is small: at most
    /// <see cref="ItemConfig.MaxGroundItems"/>) on any change and to late joiners, so a client can never drift.
    /// Per item: id, kind, count, ammo used, position (floats), who dropped it and when (23 bytes).
    /// </summary>
    public static class ItemsProtocol
    {
        public const int MaxItems = 1024;

        public static void Write(ByteWriter w, in ItemsMsg m)
        {
            int n = m.Items?.Count ?? 0;
            if (n > MaxItems) throw new System.ArgumentException("Too many items.");
            w.U8((byte)MsgType.Items);
            w.I32(m.Tick);
            w.U16((ushort)n);
            for (int i = 0; i < n; i++)
            {
                GroundItem g = m.Items[i];
                w.I32(g.Id);
                w.U8((byte)g.Kind);
                w.U8((byte)System.Math.Max(0, System.Math.Min(255, g.Count)));
                w.U8((byte)System.Math.Max(0, System.Math.Min(255, g.AmmoUsed)));
                w.VecF(g.Position);
                w.U16((ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, g.DroppedBy)));
                w.I32(g.DroppedTick);
            }
        }

        public static ItemsMsg Read(ref ByteReader r, List<GroundItem> into)
        {
            if (r.U8() != (byte)MsgType.Items) throw new NetFormatException("Unexpected message type.");
            into.Clear();
            var m = new ItemsMsg { Tick = r.I32(), Items = into };
            int n = r.U16();
            if (n > MaxItems) throw new NetFormatException("Too many items.");
            for (int i = 0; i < n; i++)
            {
                var g = new GroundItem { Id = r.I32() };
                byte kind = r.U8();
                if (kind == 0 || kind > (byte)Sculpt.Core.Items.Last) throw new NetFormatException("Bad item.");
                g.Kind = (Item)kind;
                g.Count = r.U8();
                g.AmmoUsed = r.U8();
                g.Position = r.VecFinite();
                g.DroppedBy = r.U16();
                g.DroppedTick = r.I32();
                if (g.Id <= 0 || g.Count <= 0) throw new NetFormatException("Bad item.");
                into.Add(g);
            }
            return m;
        }
    }
}
