using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SeedLab.Saves
{
    /// <summary>One row of <c>_main.&lt;N&gt;.chunks</c>: which ZDO chunk files exist and how big they are.</summary>
    public sealed class ChunkInfo
    {
        public ChunkInfo(ushort chunk, byte chunkSize, uint version, int numZdos)
        {
            Chunk = chunk;
            ChunkSize = chunkSize;
            Version = version;
            NumZdos = numZdos;
        }

        /// <summary><c>chunk = cx + (cy &lt;&lt; 8)</c> (<c>ZoneSystem.ChunkIndexFromXY</c>, line 3277).</summary>
        public ushort Chunk { get; }

        public byte ChunkSize { get; }
        public uint Version { get; }
        public int NumZdos { get; }

        public int ChunkX => Chunk & 0xFF;
        public int ChunkY => Chunk >> 8;

        /// <summary>
        /// The chunk's corner zone: <c>((Chunk &amp; 0xFF)*8 - 256, (Chunk &gt;&gt; 8)*8 - 256)</c>
        /// (<c>ZoneSystem.GetZoneFromChunk</c>, line 3079, with
        /// <c>c_ZonesPerChunk = 8</c> on a 512x512 zone grid).
        /// </summary>
        public (int X, int Y) CornerZone => (ChunkX * 8 - 256, ChunkY * 8 - 256);

        /// <summary>
        /// <c>ChunkSaveMapping.GetChunkFilename</c>:
        /// <c>{cy:x2}_{cx:x2}__{size}_{version}.chunk</c>.
        /// </summary>
        public string FileName => string.Format(CultureInfo.InvariantCulture, "{0:x2}_{1:x2}__{2}_{3}.chunk",
            ChunkY, ChunkX, ChunkSize, Version);

        public override string ToString() => FileName + " (" + NumZdos + " ZDOs)";
    }

    /// <summary>A parsed <c>_main.&lt;N&gt;.chunks</c>.</summary>
    public sealed class ChunkMapping
    {
        public ChunkMapping(int version, int totalZdos, IReadOnlyList<ChunkInfo> chunks)
        {
            Version = version;
            TotalZdos = totalZdos;
            Chunks = chunks;
        }

        /// <summary>Written as <c>(short)41</c> and read back with <c>ReadUShort</c>.</summary>
        public int Version { get; }

        /// <summary>
        /// The sum of <c>m_numZDOs</c> over <b>all</b> chunks, including ones omitted from the list
        /// because their size changed (<c>SaveChunk =&gt; !m_sizeChanged</c>), so it can exceed the sum
        /// of the listed <see cref="ChunkInfo.NumZdos"/>. Do not assert equality
        /// (05-validation.md section 5.3).
        /// </summary>
        public int TotalZdos { get; }

        public IReadOnlyList<ChunkInfo> Chunks { get; }
    }

    /// <summary>
    /// Reads <c>_main.&lt;N&gt;.chunks</c>. <b>Read-only</b>; see <see cref="IValheimReader"/>.
    /// <para>
    /// <c>ChunkSaveMapping.Save</c>/<c>Load</c> (decompiled). It is a bare ZPackage payload with
    /// <b>no int32 length prefix</b>, unlike the <c>.fwl2</c>:
    /// <code>
    /// int16  version        // 41, written as (short)41, read with ReadUShort
    /// int32  totalZDOs
    /// int32  count          // only chunks with SaveChunk == !m_sizeChanged
    /// { uint16 chunk; byte size; uint32 version; int32 numZDOs } x count     // 11 bytes each
    /// </code>
    /// Verified: both ground-truth <c>.chunks</c> files are 54 bytes = 10 + 4*11, 0 left over.
    /// </para>
    /// <para>
    /// Nothing in world generation lives in ZDOs, so this reader exists only to complete the save-set
    /// picture. The <c>*.chunk</c> bodies themselves are <b>not</b> parsed: <c>ZDO.Save</c>/<c>Load</c>
    /// was not decompiled and the layout is <b>Unverified</b> (05-validation.md section 6 item 6).
    /// </para>
    /// </summary>
    public sealed class ChunkMappingReader : IValheimReader
    {
        public static ChunkMapping Read(string path)
        {
            byte[] bytes = SaveFileAccess.ReadAllBytes(path);
            return Parse(bytes, path);
        }

        public static ChunkMapping Parse(byte[] fileBytes, string? path = null)
        {
            string what = path ?? ".chunks";
            PackageReader p = new PackageReader(fileBytes);

            int version = p.ReadUShort();
            int totalZdos = p.ReadInt();
            int count = p.ReadInt();

            const int bytesPerRow = 11;
            if (count < 0 || (long)count * bytesPerRow > p.Remaining)
                throw new InvalidDataException(
                    what + ": implausible chunk count " + count + " with only " + p.Remaining + " bytes left.");

            List<ChunkInfo> chunks = new List<ChunkInfo>(count);
            for (int i = 0; i < count; i++)
            {
                ushort chunk = p.ReadUShort();
                byte size = p.ReadByte();
                uint chunkVersion = p.ReadUInt();
                int numZdos = p.ReadInt();
                chunks.Add(new ChunkInfo(chunk, size, chunkVersion, numZdos));
            }

            p.ExpectEnd(what);
            return new ChunkMapping(version, totalZdos, chunks);
        }
    }
}
