using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>Items on the ground, as of one server tick: the whole list, or only what changed since the last message.</summary>
    public struct ItemsMsg
    {
        public int Tick;
        /// <summary>True: <see cref="Items"/> is every item on the ground (joiners, key frames). False: a delta.</summary>
        public bool Full;
        /// <summary>Full: every item. Delta: items added or changed (replace by id, or add).</summary>
        public List<GroundItem> Items;
        /// <summary>Delta only: ids of items that are gone.</summary>
        public List<int> Removed;
    }

    /// <summary>
    /// Wire format of <see cref="ItemsMsg"/>. Joiners and key frames get the whole list; after that the server sends only
    /// what changed each tick (battle royale maps hold hundreds of items and chests, so resending the whole list on every
    /// pickup would cost ~7 KB per pickup per player). A delta is upserts plus removed ids, so applying one twice (a
    /// joiner whose full list already had the change) changes nothing.
    /// Per item: id, kind, count, ammo used, rarity, position (floats), who dropped it and when (24 bytes).
    /// </summary>
    public static class ItemsProtocol
    {
        public const int MaxItems = 4096;

        public static void Write(ByteWriter w, in ItemsMsg m)
        {
            int n = m.Items?.Count ?? 0;
            int gone = m.Full ? 0 : m.Removed?.Count ?? 0;
            if (n > MaxItems || gone > MaxItems) throw new System.ArgumentException("Too many items.");
            w.U8((byte)MsgType.Items);
            w.I32(m.Tick);
            w.U8((byte)(m.Full ? 1 : 0));
            w.U16((ushort)n);
            for (int i = 0; i < n; i++)
            {
                GroundItem g = m.Items[i];
                w.I32(g.Id);
                w.U8((byte)g.Kind);
                w.U8((byte)System.Math.Max(0, System.Math.Min(255, g.Count)));
                w.U8((byte)System.Math.Max(0, System.Math.Min(255, g.AmmoUsed)));
                w.U8((byte)g.Rarity);
                w.VecF(g.Position);
                w.U16((ushort)System.Math.Max(0, System.Math.Min(ushort.MaxValue, g.DroppedBy)));
                w.I32(g.DroppedTick);
            }
            if (m.Full) return;
            w.U16((ushort)gone);
            for (int i = 0; i < gone; i++) w.I32(m.Removed[i]);
        }

        public static ItemsMsg Read(ref ByteReader r, List<GroundItem> into, List<int> removed = null)
        {
            if (r.U8() != (byte)MsgType.Items) throw new NetFormatException("Unexpected message type.");
            into.Clear();
            removed?.Clear();
            var m = new ItemsMsg { Tick = r.I32(), Items = into, Removed = removed };
            byte flags = r.U8();
            if ((flags & ~1) != 0) throw new NetFormatException("Bad items flags.");
            m.Full = (flags & 1) != 0;
            int n = r.U16();
            if (n > MaxItems) throw new NetFormatException("Too many items.");
            for (int i = 0; i < n; i++)
            {
                var g = new GroundItem { Id = r.I32() };
                byte kind = r.U8();
                if (kind == 0 || kind > (byte)Sculpt.Core.Items.LastGround) throw new NetFormatException("Bad item.");
                g.Kind = (Item)kind;
                g.Count = r.U8();
                g.AmmoUsed = r.U8();
                byte rar = r.U8();
                if (rar > (byte)Rarities.Last) throw new NetFormatException("Bad rarity.");
                g.Rarity = (Rarity)rar;
                g.Position = r.VecFinite();
                g.DroppedBy = r.U16();
                g.DroppedTick = r.I32();
                if (g.Id <= 0 || g.Count <= 0) throw new NetFormatException("Bad item.");
                into.Add(g);
            }
            if (m.Full) return m;
            int gone = r.U16();
            if (gone > MaxItems) throw new NetFormatException("Too many items.");
            for (int i = 0; i < gone; i++)
            {
                int id = r.I32();
                if (id <= 0) throw new NetFormatException("Bad item.");
                removed?.Add(id);
            }
            return m;
        }

        /// <summary>Applies a message to a client's copy (full: replace everything; delta: upserts and removals).</summary>
        public static void Apply(GroundItems items, in ItemsMsg m)
        {
            if (m.Full) { items.ReplaceAll(m.Items); return; }
            foreach (GroundItem g in m.Items) items.Upsert(g);
            if (m.Removed != null) foreach (int id in m.Removed) items.Remove(id);
        }

        /// <summary>
        /// What changed between what was last sent (<paramref name="sent"/>, id to item) and the list now: fills
        /// <paramref name="upserts"/> and <paramref name="removed"/> and brings <paramref name="sent"/> up to date.
        /// </summary>
        public static void Diff(Dictionary<int, GroundItem> sent, IReadOnlyList<GroundItem> now, List<GroundItem> upserts, List<int> removed, HashSet<int> seenScratch)
        {
            upserts.Clear();
            removed.Clear();
            seenScratch.Clear();
            foreach (GroundItem g in now)
            {
                seenScratch.Add(g.Id);
                if (sent.TryGetValue(g.Id, out GroundItem old) && Same(old, g)) continue;
                sent[g.Id] = g;
                upserts.Add(g);
            }
            foreach (int id in sent.Keys) if (!seenScratch.Contains(id)) removed.Add(id);
            foreach (int id in removed) sent.Remove(id);
        }

        private static bool Same(in GroundItem a, in GroundItem b) =>
            a.Kind == b.Kind && a.Count == b.Count && a.AmmoUsed == b.AmmoUsed && a.Rarity == b.Rarity && a.DroppedBy == b.DroppedBy &&
            a.DroppedTick == b.DroppedTick && (float)a.Position.X == (float)b.Position.X && (float)a.Position.Y == (float)b.Position.Y && (float)a.Position.Z == (float)b.Position.Z;
    }
}
