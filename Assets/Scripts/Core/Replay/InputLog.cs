using System;
using System.Collections.Generic;
using Sculpt.Core.Net;

namespace Sculpt.Core.Replay
{
    /// <summary>
    /// Compact per-tick command log for replays: each player's command is written as the bytes that changed since their
    /// previous one (view angles as the difference of their float bits, which is small for small turns). Exact: the
    /// decoder rebuilds the same wire bytes and reads them with <see cref="InputCmd"/>'s own reader. Each segment starts
    /// afresh, so any segment decodes on its own.
    /// </summary>
    public sealed class InputLogCodec
    {
        private const byte MHead = 1, MYaw = 2, MPitch = 4, MView = 8, MExt = 16;
        private const int Fixed = 14; // flags, fire flags, two axes, yaw, pitch, view delta, view fraction
        private readonly Dictionary<int, byte[]> _prev = new Dictionary<int, byte[]>();
        private readonly ByteWriter _cmd = new ByteWriter(64);
        private static readonly byte[] Zero = new byte[Fixed];

        public void Reset() => _prev.Clear();

        public void Write(ByteWriter w, int playerId, in InputCmd cmd)
        {
            _cmd.Clear();
            cmd.Write(_cmd);
            byte[] a = _cmd.Buffer;
            if (!_prev.TryGetValue(playerId, out byte[] p)) p = Zero;
            byte m = 0;
            if (a[0] != p[0] || a[1] != p[1] || a[2] != p[2] || a[3] != p[3]) m |= MHead;
            int yaw = I32(a, 4), pyaw = I32(p, 4), pitch = I32(a, 8), ppitch = I32(p, 8);
            if (yaw != pyaw) m |= MYaw;
            if (pitch != ppitch) m |= MPitch;
            if (a[12] != p[12] || a[13] != p[13]) m |= MView;
            int ext = _cmd.Length - Fixed;
            if (ext > 0) m |= MExt;
            w.VarU((ulong)playerId);
            w.U8(m);
            if ((m & MHead) != 0) w.Bytes(a, 0, 4);
            if ((m & MYaw) != 0) w.VarS((long)yaw - pyaw);
            if ((m & MPitch) != 0) w.VarS((long)pitch - ppitch);
            if ((m & MView) != 0) w.Bytes(a, 12, 2);
            if ((m & MExt) != 0) { w.VarU((ulong)ext); w.Bytes(a, Fixed, ext); }
            if (p == Zero) _prev[playerId] = p = new byte[Fixed];
            Buffer.BlockCopy(a, 0, p, 0, Fixed);
        }

        public InputCmd Read(ref ByteReader r, int tick, out int playerId)
        {
            ulong id = r.VarU();
            if (id > int.MaxValue) throw new NetFormatException("Bad id.");
            playerId = (int)id;
            byte m = r.U8();
            if ((m & ~(MHead | MYaw | MPitch | MView | MExt)) != 0) throw new NetFormatException("Bad input mask.");
            if (!_prev.TryGetValue(playerId, out byte[] p)) _prev[playerId] = p = new byte[Fixed];
            var a = new byte[Fixed + 64];
            Buffer.BlockCopy(p, 0, a, 0, Fixed);
            ByteReader src = r;
            if ((m & MHead) != 0) for (int i = 0; i < 4; i++) a[i] = r.U8();
            if ((m & MYaw) != 0) Put(a, 4, (int)(I32(p, 4) + r.VarS()));
            if ((m & MPitch) != 0) Put(a, 8, (int)(I32(p, 8) + r.VarS()));
            if ((m & MView) != 0) { a[12] = r.U8(); a[13] = r.U8(); }
            int len = Fixed;
            if ((m & MExt) != 0)
            {
                int ext = r.VarCount(64);
                int at = r.Skip(ext);
                Buffer.BlockCopy(src.BufferOf(), at, a, Fixed, ext);
                len += ext;
            }
            Buffer.BlockCopy(a, 0, p, 0, Fixed);
            var cr = new ByteReader(a, 0, len);
            InputCmd c = InputCmd.Read(ref cr, tick);
            if (cr.Remaining != 0) throw new NetFormatException("Input has trailing bytes.");
            return c;
        }

        private static int I32(byte[] b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

        private static void Put(byte[] b, int at, int v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }
    }
}
