using System;

namespace Sculpt.Core.Net
{
    /// <summary>Thrown when a packet is truncated or malformed. The receiver drops the packet.</summary>
    public sealed class NetFormatException : Exception
    {
        public NetFormatException(string message) : base(message) { }
    }

    /// <summary>Little-endian binary writer over a growable buffer. Reuse one per sender; copy out with ToArray.</summary>
    public sealed class ByteWriter
    {
        private byte[] _buf;
        public int Length { get; private set; }

        public ByteWriter(int capacity = 256) { _buf = new byte[capacity]; }

        public void Clear() => Length = 0;
        public byte[] Buffer => _buf;

        public byte[] ToArray()
        {
            var a = new byte[Length];
            System.Buffer.BlockCopy(_buf, 0, a, 0, Length);
            return a;
        }

        private void Ensure(int extra)
        {
            if (Length + extra <= _buf.Length) return;
            int n = Math.Max(_buf.Length * 2, Length + extra);
            Array.Resize(ref _buf, n);
        }

        public void U8(byte v) { Ensure(1); _buf[Length++] = v; }
        public void I8(sbyte v) => U8(unchecked((byte)v));
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);

        public void U16(ushort v)
        {
            Ensure(2);
            _buf[Length++] = (byte)v;
            _buf[Length++] = (byte)(v >> 8);
        }

        public void I32(int v)
        {
            Ensure(4);
            uint u = unchecked((uint)v);
            _buf[Length++] = (byte)u;
            _buf[Length++] = (byte)(u >> 8);
            _buf[Length++] = (byte)(u >> 16);
            _buf[Length++] = (byte)(u >> 24);
        }

        public void I64(long v)
        {
            I32(unchecked((int)v));
            I32(unchecked((int)(v >> 32)));
        }

        public void F32(float v) => I32(BitConverter.SingleToInt32Bits(v));
        public void F64(double v) => I64(BitConverter.DoubleToInt64Bits(v));

        public void Vec(Vec3d v) { F64(v.X); F64(v.Y); F64(v.Z); }
        public void VecF(Vec3d v) { F32((float)v.X); F32((float)v.Y); F32((float)v.Z); }
    }

    /// <summary>Bounds-checked little-endian reader. Any overrun throws NetFormatException.</summary>
    public struct ByteReader
    {
        private readonly byte[] _buf;
        private readonly int _end;
        public int Position { get; private set; }

        public ByteReader(byte[] buf, int offset, int count)
        {
            if (buf == null || offset < 0 || count < 0 || offset + count > buf.Length)
                throw new NetFormatException("Bad packet bounds.");
            _buf = buf;
            Position = offset;
            _end = offset + count;
        }

        public int Remaining => _end - Position;

        private void Need(int n)
        {
            if (Position + n > _end) throw new NetFormatException("Packet truncated.");
        }

        public byte U8() { Need(1); return _buf[Position++]; }
        public sbyte I8() => unchecked((sbyte)U8());

        public bool Bool()
        {
            byte b = U8();
            if (b > 1) throw new NetFormatException("Bad bool.");
            return b == 1;
        }

        public ushort U16()
        {
            Need(2);
            int v = _buf[Position] | (_buf[Position + 1] << 8);
            Position += 2;
            return (ushort)v;
        }

        public int I32()
        {
            Need(4);
            uint v = (uint)_buf[Position] | ((uint)_buf[Position + 1] << 8) | ((uint)_buf[Position + 2] << 16) | ((uint)_buf[Position + 3] << 24);
            Position += 4;
            return unchecked((int)v);
        }

        public long I64()
        {
            uint lo = unchecked((uint)I32());
            uint hi = unchecked((uint)I32());
            return unchecked((long)(((ulong)hi << 32) | lo));
        }

        public float F32() => BitConverter.Int32BitsToSingle(I32());
        public double F64() => BitConverter.Int64BitsToDouble(I64());

        public Vec3d Vec() => new Vec3d(F64(), F64(), F64());
        public Vec3d VecF() => new Vec3d(F32(), F32(), F32());
    }
}
