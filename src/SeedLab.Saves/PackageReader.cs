using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SeedLab.Saves
{
    /// <summary>
    /// Reads the game's <c>ZPackage</c> encoding.
    /// <para>
    /// A ZPackage is a <c>MemoryStream</c> driven by <c>System.IO.BinaryWriter</c>/<c>BinaryReader</c>
    /// (<c>ZPackage</c>, decompiled, assembly_valheim), so every primitive has exactly the semantics
    /// of .NET's own <c>BinaryReader</c>. The top level of a <c>.db2</c> is a bare
    /// <c>BinaryWriter</c> rather than a ZPackage (<c>ZNet.SaveWorldThread</c>), which makes no
    /// difference: the encodings are identical. Table: 05-validation.md section 5.0.
    /// </para>
    /// <para>All values are little-endian on disk regardless of the host's endianness.</para>
    /// </summary>
    public sealed class PackageReader
    {
        private readonly byte[] _buffer;
        private readonly int _start;
        private readonly int _end;
        private int _position;

        public PackageReader(byte[] data)
            : this(data ?? throw new ArgumentNullException(nameof(data)), 0, data.Length)
        {
        }

        public PackageReader(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));

            _buffer = data;
            _start = offset;
            _end = offset + count;
            _position = offset;
        }

        /// <summary>Bytes consumed so far, relative to the start of this package.</summary>
        public int Position => _position - _start;

        /// <summary>Total length of this package.</summary>
        public int Length => _end - _start;

        /// <summary>Bytes not yet consumed.</summary>
        public int Remaining => _end - _position;

        public ReadOnlySpan<byte> ReadRaw(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count > Remaining)
                throw new EndOfStreamException(
                    "Truncated package: wanted " + count + " bytes at offset " + Position +
                    " but only " + Remaining + " remain.");

            ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(_buffer, _position, count);
            _position += count;
            return span;
        }

        /// <summary><c>ZPackage.ReadByteArray(int)</c> - n raw bytes with no length prefix.</summary>
        public byte[] ReadByteArray(int count) => ReadRaw(count).ToArray();

        /// <summary><c>ZPackage.ReadByteArray()</c> - <c>int32</c> length then that many bytes.</summary>
        public byte[] ReadByteArray()
        {
            int n = ReadInt();
            if (n < 0) throw new InvalidDataException("Negative byte-array length " + n + " at offset " + Position + ".");
            return ReadByteArray(n);
        }

        public byte ReadByte() => ReadRaw(1)[0];

        public sbyte ReadSByte() => (sbyte)ReadRaw(1)[0];

        /// <summary><c>BinaryWriter.Write(bool)</c> - one byte, 0 or 1.</summary>
        public bool ReadBool() => ReadRaw(1)[0] != 0;

        public short ReadShort() => BinaryPrimitives.ReadInt16LittleEndian(ReadRaw(2));

        public ushort ReadUShort() => BinaryPrimitives.ReadUInt16LittleEndian(ReadRaw(2));

        public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(ReadRaw(4));

        public uint ReadUInt() => BinaryPrimitives.ReadUInt32LittleEndian(ReadRaw(4));

        public long ReadLong() => BinaryPrimitives.ReadInt64LittleEndian(ReadRaw(8));

        public ulong ReadULong() => BinaryPrimitives.ReadUInt64LittleEndian(ReadRaw(8));

        public float ReadSingle() => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(ReadRaw(4)));

        public double ReadDouble() => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(ReadRaw(8)));

        /// <summary>
        /// <c>BinaryWriter.Write(string)</c>: a 7-bit-encoded <c>int</c> (LEB128, 1-5 bytes) giving the
        /// UTF-8 <b>byte</b> count, then those bytes. Not a byte prefix - the
        /// <c>preset ...</c> global key in <c>asdasdasd</c> is 87 bytes and a longer one would spill
        /// into a second length byte (05-validation.md section 5.0).
        /// </summary>
        public string ReadString()
        {
            int byteCount = Read7BitEncodedInt();
            if (byteCount < 0)
                throw new InvalidDataException("Negative string length " + byteCount + " at offset " + Position + ".");
            if (byteCount == 0) return string.Empty;
            return Encoding.UTF8.GetString(ReadRaw(byteCount));
        }

        /// <summary>
        /// <c>BinaryWriter.Write(char)</c> - UTF-8, 1-3 bytes. Never assume 2
        /// (05-validation.md section 5.0). Nothing in the world or character formats uses it; it is
        /// here so the ZPackage table is complete. Like <c>BinaryReader.ReadChar</c>, a surrogate
        /// pair (4-byte UTF-8) is not a single char and throws.
        /// </summary>
        public char ReadChar()
        {
            byte b0 = ReadByte();
            int extra = b0 < 0x80 ? 0 : (b0 & 0xE0) == 0xC0 ? 1 : (b0 & 0xF0) == 0xE0 ? 2 : -1;
            if (extra < 0)
                throw new InvalidDataException("Not a BMP UTF-8 char at offset " + (Position - 1) + ".");

            Span<byte> bytes = stackalloc byte[3];
            bytes[0] = b0;
            for (int i = 0; i < extra; i++) bytes[i + 1] = ReadByte();

            Span<char> chars = stackalloc char[2];
            int n = Encoding.UTF8.GetChars(bytes.Slice(0, extra + 1), chars);
            if (n != 1) throw new InvalidDataException("Malformed char at offset " + Position + ".");
            return chars[0];
        }

        /// <summary><c>ZPackage.ReadVector3</c> - three floats, x then y then z.</summary>
        public Vec3f ReadVector3()
        {
            float x = ReadSingle();
            float y = ReadSingle();
            float z = ReadSingle();
            return new Vec3f(x, y, z);
        }

        /// <summary><c>ZPackage.ReadVector2i</c> - two ints.</summary>
        public (int X, int Y) ReadVector2i() => (ReadInt(), ReadInt());

        /// <summary><c>ZPackage.ReadVector2s</c> - two shorts.</summary>
        public (short X, short Y) ReadVector2s() => (ReadShort(), ReadShort());

        /// <summary><c>ZPackage.Read(ZPackage)</c> - <c>int32</c> length then that many bytes.</summary>
        public PackageReader ReadPackage()
        {
            int n = ReadInt();
            if (n < 0) throw new InvalidDataException("Negative package length " + n + " at offset " + Position + ".");
            ReadRaw(n); // bounds-check and advance
            return new PackageReader(_buffer, _position - n, n);
        }

        /// <summary>
        /// <c>ZPackage.ReadCompressedPackage()</c> - <c>int32</c> gzip length, then a
        /// <c>Utils.Compress</c> (plain gzip) blob holding the inner package.
        /// </summary>
        public PackageReader ReadCompressedPackage()
        {
            int n = ReadInt();
            if (n < 0) throw new InvalidDataException("Negative compressed length " + n + " at offset " + Position + ".");
            byte[] inflated = SaveCompression.Decompress(ReadRaw(n));
            return new PackageReader(inflated);
        }

        /// <summary>
        /// <c>ZPackage.ReadNumItems</c>: <c>b = ReadByte(); if ((b &amp; 0x80) != 0) b = ((b &amp; 0x7F) &lt;&lt; 8) | ReadByte();</c>
        /// Maximum 32767 (05-validation.md section 5.0). Used by inventory/ZDO payloads, not by the
        /// world or character layouts parsed here.
        /// </summary>
        public int ReadNumItems()
        {
            int b = ReadByte();
            if ((b & 0x80) != 0) b = ((b & 0x7F) << 8) | ReadByte();
            return b;
        }

        /// <summary>
        /// Throws unless the package is fully consumed. Every block of every ground-truth file ends
        /// with 0 bytes left over (05-validation.md T9(a)); a non-zero remainder means the layout is
        /// wrong, and guessing past it would be worse than failing.
        /// </summary>
        public void ExpectEnd(string what)
        {
            if (Remaining != 0)
                throw new InvalidDataException(
                    what + ": " + Remaining + " byte(s) left over after " + Position + " consumed. " +
                    "The layout does not match this file.");
        }

        /// <summary>The bytes not yet consumed, copied out. Used to record blocks we deliberately do not decode.</summary>
        public byte[] ReadRemaining() => ReadRaw(Remaining).ToArray();

        /// <summary><c>BinaryReader.Read7BitEncodedInt</c>: LEB128, at most 5 bytes.</summary>
        private int Read7BitEncodedInt()
        {
            int result = 0;
            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = ReadByte();
                // The fifth byte may only carry the remaining 4 bits.
                if (i == 4 && (b & 0xF0) != 0)
                    throw new InvalidDataException("Malformed 7-bit-encoded int at offset " + (Position - 1) + ".");
                result |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
            throw new InvalidDataException("Malformed 7-bit-encoded int at offset " + Position + ".");
        }
    }
}
