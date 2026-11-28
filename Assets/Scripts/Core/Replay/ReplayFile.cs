using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Sculpt.Core.Net;

namespace Sculpt.Core.Replay
{
    /// <summary>What a replay is of. Plain data; written at the start of the file.</summary>
    public sealed class ReplayHeader
    {
        /// <summary>Replay format version (separate from the network protocol, which must also match to play it back).</summary>
        public const ushort FormatVersion = 2; // 2: anti-cheat flags (records and footer)
        public byte Protocol = Net.Protocol.Version;
        public int TicksPerSecond = SimTime.TicksPerSecond;
        /// <summary>Map name the Game layer rebuilds the world from ("Duel", "Breach").</summary>
        public string Map = "";
        /// <summary>Mode label ("1v1", "Offline", "Vs bot").</summary>
        public string Mode = "";
        /// <summary>The game's build (Application.version); replays play back on the build that made them.</summary>
        public string Build = "";
        /// <summary>When recording started, UTC, in .NET ticks (100 ns).</summary>
        public long StartedUtcTicks;
        public int KeyEveryTicks;
        public int SnapEveryTicks;
        public bool HasInputs;

        internal void Write(ByteWriter w)
        {
            w.U8(Protocol);
            w.VarU((ulong)TicksPerSecond);
            SpecProtocol.Str(w, Map);
            SpecProtocol.Str(w, Mode);
            SpecProtocol.Str(w, Build);
            w.I64(StartedUtcTicks);
            w.VarU((ulong)KeyEveryTicks);
            w.VarU((ulong)SnapEveryTicks);
            w.Bool(HasInputs);
        }

        internal static ReplayHeader Read(ref ByteReader r, byte[] buf) => new ReplayHeader
        {
            Protocol = r.U8(),
            TicksPerSecond = r.VarCount(100000),
            Map = SpecProtocol.Str(ref r, buf),
            Mode = SpecProtocol.Str(ref r, buf),
            Build = SpecProtocol.Str(ref r, buf),
            StartedUtcTicks = r.I64(),
            KeyEveryTicks = r.VarCount(int.MaxValue),
            SnapEveryTicks = r.VarCount(int.MaxValue),
            HasInputs = r.Bool(),
        };
    }

    /// <summary>
    /// Saves a match stream to a file as it goes: header, then each segment deflated as it closes, then an index and
    /// the kill markers when finished. A file cut short (crash, power) still plays up to its last whole segment.
    /// Layout: "SCLR", u16 format, header; blocks 'S' (index, first tick, last tick, raw length, packed length, bytes);
    /// 'F' footer (segment offsets and ticks, markers, first and last tick), its offset as i64, "SCLE".
    /// </summary>
    public sealed class ReplayWriter : IDisposable
    {
        private readonly Stream _out;
        private readonly MatchStream _stream;
        private readonly List<ReplayFile.SegmentInfo> _index = new List<ReplayFile.SegmentInfo>();
        private bool _done;
        public long BytesWritten { get { lock (_gate) return _out.Position; } }
        public int SegmentsWritten { get { lock (_gate) return _index.Count; } }

        public ReplayWriter(Stream output, ReplayHeader header, MatchStream stream)
        {
            _out = output ?? throw new ArgumentNullException(nameof(output));
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            header.KeyEveryTicks = stream.Config.KeyEveryTicks;
            header.SnapEveryTicks = stream.Config.SnapEveryTicks;
            header.HasInputs = stream.Config.RecordInputs;
            var w = new ByteWriter(128);
            w.U8((byte)'S'); w.U8((byte)'C'); w.U8((byte)'L'); w.U8((byte)'R');
            w.U16(ReplayHeader.FormatVersion);
            var h = new ByteWriter(128);
            header.Write(h);
            w.VarU((ulong)h.Length);
            w.Bytes(h.Buffer, 0, h.Length);
            _out.Write(w.Buffer, 0, w.Length);
            stream.SegmentClosed += OnSegment;
            // Segments that closed before the writer was attached are still in memory: save them first.
            foreach (StreamSegment s in stream.Segments) if (s.Closed) OnSegment(s);
        }

        /// <summary>
        /// Pack and write each closed segment on a worker thread instead of the caller's (the server's tick). Packing ten
        /// seconds of a 32-player match took 13 ms in .NET and 30-40 ms in the Editor, a slow tick every 10 s. Segments
        /// are still written in order; <see cref="Finish"/> waits for them. Set before the first segment closes.
        /// </summary>
        public bool Background;

        private readonly object _gate = new object();
        private System.Threading.Tasks.Task _work = System.Threading.Tasks.Task.CompletedTask;
        private Exception _failed;

        private void OnSegment(StreamSegment seg)
        {
            if (_done) return;
            lock (_gate)
            {
                foreach (int i in _queued) if (i == seg.Index) return;
                _queued.Add(seg.Index);
            }
            // A closed segment is never written to again (the stream appends to the next one), so the worker can read it.
            int index = seg.Index, first = seg.FirstTick, last = seg.LastTick;
            byte[] raw = seg.Raw.Buffer;
            int length = seg.Raw.Length;
            if (!Background) { WriteSegment(index, first, last, raw, length); return; }
            lock (_gate)
                _work = _work.ContinueWith(_ =>
                {
                    try { WriteSegment(index, first, last, raw, length); }
                    catch (Exception e) { lock (_gate) _failed ??= e; }
                }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private readonly List<int> _queued = new List<int>();

        private void WriteSegment(int index, int first, int last, byte[] raw, int length)
        {
            byte[] packed = Pack(raw, length);
            var w = new ByteWriter(32);
            w.U8((byte)'S');
            w.I32(index);
            w.I32(first);
            w.I32(last);
            w.VarU((ulong)length);
            w.VarU((ulong)packed.Length);
            lock (_gate)
            {
                long at = _out.Position;
                _out.Write(w.Buffer, 0, w.Length);
                _out.Write(packed, 0, packed.Length);
                _out.Flush();
                _index.Add(new ReplayFile.SegmentInfo { Index = index, Offset = at, FirstTick = first, LastTick = last });
            }
        }

        /// <summary>Waits until every segment closed so far is in the file (immediate when not <see cref="Background"/>).</summary>
        public void WaitWritten()
        {
            System.Threading.Tasks.Task t;
            lock (_gate) t = _work;
            t.Wait();
            lock (_gate) if (_failed != null) throw new IOException("A replay segment could not be written.", _failed);
        }

        /// <summary>Closes the stream's last segment and writes the index. The output stays open (the caller owns it).</summary>
        public void Finish()
        {
            if (_done) return;
            if (!_stream.Finished) _stream.Finish();
            _stream.SegmentClosed -= OnSegment;
            WaitWritten();
            _done = true;
            var f = new ByteWriter(256);
            f.VarU((ulong)_index.Count);
            foreach (ReplayFile.SegmentInfo i in _index)
            {
                f.I64(i.Offset);
                f.I32(i.FirstTick);
                f.I32(i.LastTick);
            }
            f.VarU((ulong)_stream.Markers.Count);
            foreach (ReplayMarker m in _stream.Markers)
            {
                f.I32(m.Tick);
                f.VarU((ulong)Math.Max(0, m.Killer));
                f.VarU((ulong)Math.Max(0, m.Victim));
                f.U8((byte)m.Source);
                f.Bool(m.Headshot);
            }
            f.VarU((ulong)_stream.Flags.Count);
            foreach (AntiCheat.Flag fl in _stream.Flags) AntiCheat.FlagLog.Write(f, fl);
            var w = new ByteWriter(f.Length + 32);
            long at = _out.Position;
            w.U8((byte)'F');
            w.VarU((ulong)f.Length);
            w.Bytes(f.Buffer, 0, f.Length);
            w.I64(at);
            w.U8((byte)'S'); w.U8((byte)'C'); w.U8((byte)'L'); w.U8((byte)'E');
            _out.Write(w.Buffer, 0, w.Length);
            _out.Flush();
        }

        public void Dispose() => Finish();

        internal static byte[] Pack(byte[] raw, int count)
        {
            using (var ms = new MemoryStream(count / 3 + 64))
            {
                using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(raw, 0, count);
                return ms.ToArray();
            }
        }
    }

    /// <summary>A replay read into memory: header, where each segment is, and the kill markers.</summary>
    public sealed class ReplayFile
    {
        public struct SegmentInfo
        {
            public int Index;
            public long Offset;
            public int FirstTick, LastTick;
            internal int PackedAt, PackedLength, RawLength;
        }

        /// <summary>One record of a segment: its kind and where its body sits in the segment's raw bytes.</summary>
        public struct Record
        {
            public byte Kind;
            public int Offset, Length;
        }

        public ReplayHeader Header { get; private set; }
        public readonly List<SegmentInfo> Segments = new List<SegmentInfo>();
        public readonly List<ReplayMarker> Markers = new List<ReplayMarker>();
        /// <summary>Anti-cheat flags the recording server logged, in order (a reviewer jumps to each).</summary>
        public readonly List<AntiCheat.Flag> Flags = new List<AntiCheat.Flag>();
        /// <summary>True when the file ended cleanly (index present); false = cut short, rebuilt by scanning.</summary>
        public bool Complete { get; private set; }
        public int FirstTick => Segments.Count > 0 ? Segments[0].FirstTick : 0;
        public int LastTick => Segments.Count > 0 ? Segments[Segments.Count - 1].LastTick : 0;
        public double Seconds => (LastTick - FirstTick + 1) / (double)Math.Max(1, Header.TicksPerSecond);

        private byte[] _data;

        /// <summary>Why a file can't be played on this build, or null if it can.</summary>
        public string Incompatible =>
            Header.Protocol != Protocol.Version ? $"made by another build of the game (protocol {Header.Protocol}, this build {Protocol.Version})" :
            Header.TicksPerSecond != SimTime.TicksPerSecond ? "recorded at another tick rate" : null;

        public static ReplayFile Read(byte[] data)
        {
            if (data == null || data.Length < 7 || data[0] != 'S' || data[1] != 'C' || data[2] != 'L' || data[3] != 'R')
                throw new NetFormatException("Not a SCULPT replay.");
            var f = new ReplayFile { _data = data };
            var r = new ByteReader(data, 4, data.Length - 4);
            ushort version = r.U16();
            if (version != ReplayHeader.FormatVersion && version != 1) throw new NetFormatException($"Replay format {version}, this build reads {ReplayHeader.FormatVersion}.");
            int hl = r.VarCount(1 << 16);
            int ha = r.Skip(hl);
            var hr = new ByteReader(data, ha, hl);
            f.Header = ReplayHeader.Read(ref hr, data);
            int blocksAt = r.Position;

            bool footer = false;
            if (data.Length >= blocksAt + 12 && data[data.Length - 4] == 'S' && data[data.Length - 3] == 'C' && data[data.Length - 2] == 'L' && data[data.Length - 1] == 'E')
            {
                var tr = new ByteReader(data, data.Length - 12, 8);
                long at = tr.I64();
                if (at >= blocksAt && at < data.Length - 12 && data[at] == 'F')
                {
                    try { f.ReadFooter((int)at, version); footer = true; }
                    catch (NetFormatException) { f.Segments.Clear(); f.Markers.Clear(); f.Flags.Clear(); }
                }
            }
            if (!footer) f.Scan(blocksAt);
            f.Complete = footer;
            for (int i = 0; i < f.Segments.Count; i++) f.Segments[i] = f.ReadBlockHeader(f.Segments[i].Offset);
            return f;
        }

        private void ReadFooter(int at, ushort version)
        {
            var r = new ByteReader(_data, at + 1, _data.Length - at - 1);
            int len = r.VarCount(_data.Length);
            int body = r.Skip(len);
            var b = new ByteReader(_data, body, len);
            int n = b.VarCount(1 << 24);
            for (int i = 0; i < n; i++)
            {
                long off = b.I64();
                if (off < 0 || off >= at) throw new NetFormatException("Bad segment offset.");
                Segments.Add(new SegmentInfo { Offset = off, FirstTick = b.I32(), LastTick = b.I32() });
            }
            int m = b.VarCount(1 << 24);
            for (int i = 0; i < m; i++)
                Markers.Add(new ReplayMarker { Tick = b.I32(), Killer = (int)b.VarCount(int.MaxValue), Victim = (int)b.VarCount(int.MaxValue), Source = (DamageSource)b.U8(), Headshot = b.Bool() });
            if (version < 2) return;
            int nf = b.VarCount(1 << 24);
            for (int i = 0; i < nf; i++) Flags.Add(AntiCheat.FlagLog.Read(ref b));
        }

        /// <summary>No index (the file was cut short): walk the blocks, keep every whole one, find the kills in them.</summary>
        private void Scan(int at)
        {
            while (at < _data.Length && _data[at] == 'S')
            {
                SegmentInfo s;
                try { s = ReadBlockHeader(at); }
                catch (NetFormatException) { break; }
                if ((long)s.PackedAt + s.PackedLength > _data.Length) break;
                byte[] raw;
                try { raw = Unpack(s); }
                catch (Exception) { break; } // a half-written block
                Segments.Add(s);
                FindKills(raw);
                at = s.PackedAt + s.PackedLength;
            }
        }

        private void FindKills(byte[] raw)
        {
            var frames = new List<KeyValuePair<int, Record>>();
            var events = new List<CombatEvent>();
            foreach (Record rec in Records(raw))
            {
                if (rec.Kind == ReplayRecord.Flag)
                {
                    var fr = new ByteReader(raw, rec.Offset, rec.Length);
                    Flags.Add(AntiCheat.FlagLog.Read(ref fr));
                    continue;
                }
                if (rec.Kind != ReplayRecord.Frame) continue;
                var r = new ByteReader(raw, rec.Offset, rec.Length);
                SpecProtocol.FrameHeader h = SpecProtocol.ReadFrameHeader(ref r);
                for (int k = 0; k < h.Count; k++)
                {
                    SpecProtocol.NextPacket(ref r, out int po, out int pl);
                    if (raw[po] != (byte)MsgType.Combat) continue;
                    var cr = new ByteReader(raw, po, pl);
                    CombatMsg cm = CombatProtocol.Read(ref cr, events);
                    foreach (CombatEvent e in cm.Events)
                        if (e.Type == CombatEventType.Kill)
                            Markers.Add(new ReplayMarker { Tick = h.Tick, Killer = e.Attacker, Victim = e.Victim, Source = e.Source, Headshot = e.HeadPellets > 0 });
                }
            }
        }

        private SegmentInfo ReadBlockHeader(long offset)
        {
            if (offset < 0 || offset >= _data.Length || _data[offset] != 'S') throw new NetFormatException("Bad segment block.");
            var r = new ByteReader(_data, (int)offset + 1, _data.Length - (int)offset - 1);
            var s = new SegmentInfo { Offset = offset, Index = r.I32(), FirstTick = r.I32(), LastTick = r.I32() };
            s.RawLength = r.VarCount(1 << 30);
            s.PackedLength = r.VarCount(1 << 30);
            s.PackedAt = r.Position;
            if ((long)s.PackedAt + s.PackedLength > _data.Length) throw new NetFormatException("Segment runs past the end.");
            return s;
        }

        /// <summary>The raw records of segment i (decompressed on each call; the player caches the current one).</summary>
        public byte[] SegmentBytes(int i) => Unpack(Segments[i]);

        private byte[] Unpack(in SegmentInfo s)
        {
            var raw = new byte[s.RawLength];
            using (var ms = new MemoryStream(_data, s.PackedAt, s.PackedLength, false))
            using (var z = new DeflateStream(ms, CompressionMode.Decompress))
            {
                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n <= 0) throw new NetFormatException("Segment shorter than it says.");
                    got += n;
                }
            }
            return raw;
        }

        /// <summary>Walks a segment's records in order.</summary>
        public static IEnumerable<Record> Records(byte[] raw)
        {
            int at = 0;
            while (at < raw.Length)
            {
                var r = new ByteReader(raw, at, raw.Length - at);
                byte kind = r.U8();
                int len = r.VarCount(raw.Length);
                int body = r.Skip(len);
                at = body + len;
                yield return new Record { Kind = kind, Offset = body, Length = len };
            }
        }

        /// <summary>The newest segment whose key frame (state at the end of FirstTick - 1) is at or before `tick`.</summary>
        public int SegmentFor(double tick)
        {
            int best = 0;
            for (int i = 0; i < Segments.Count; i++)
                if (Segments[i].FirstTick - 1 <= tick) best = i;
            return best;
        }
    }
}
