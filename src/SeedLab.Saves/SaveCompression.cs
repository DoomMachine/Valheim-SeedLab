using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace SeedLab.Saves
{
    /// <summary>
    /// The game's <c>Utils.Compress</c>/<c>Utils.Decompress</c> framing: plain <b>gzip</b> (RFC 1952),
    /// no length prefix and no ZPackage wrapper - the compressed file <i>is</i> the gzip stream.
    /// <code>
    /// // Utils.Decompress (assembly_utils, decompiled)
    /// using MemoryStream stream = new MemoryStream(inputArray);
    /// using GZipStream gZipStream = new GZipStream(stream, CompressionMode.Decompress);
    /// using MemoryStream memoryStream = new MemoryStream();
    /// gZipStream.CopyTo(memoryStream);
    /// return memoryStream.ToArray();
    /// </code>
    /// <para>
    /// <c>Utils.Compress2</c>/<c>Decompress2</c> are <b>Brotli</b> and are not used by any file this
    /// assembly reads (05-validation.md section 1.2).
    /// </para>
    /// <para>Only decompression exists here. See <see cref="IValheimReader"/>: nothing is ever written.</para>
    /// </summary>
    public static class SaveCompression
    {
        /// <summary>gzip magic + deflate method, as observed on all six cache buffers: <c>1f 8b 08</c>.</summary>
        public static bool LooksLikeGzip(ReadOnlySpan<byte> data) =>
            data.Length >= 3 && data[0] == 0x1F && data[1] == 0x8B && data[2] == 0x08;

        /// <summary>
        /// Upper bound on a trailer-derived capacity hint. The largest buffer the game writes is the
        /// 2048 x 2048 Color32 minimap texture at 16 MiB; the .fch map blob is 2 * 2048^2 + 9. A
        /// hostile or corrupt trailer must not be able to ask for a huge allocation, so the hint is
        /// only used when it is plausible.
        /// </summary>
        public const int MaxTrailerHint = 128 * 1024 * 1024;

        public static byte[] Decompress(byte[] input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return Decompress(new ReadOnlySpan<byte>(input));
        }

        public static byte[] Decompress(ReadOnlySpan<byte> input)
        {
            if (!LooksLikeGzip(input))
                throw new InvalidDataException(
                    "Not a gzip stream (expected 1f 8b 08). Valheim writes these buffers with " +
                    "Utils.Compress, which is plain gzip.");

            // RFC 1952: the last four bytes are ISIZE, the uncompressed size mod 2^32. Safe to use as
            // a capacity hint here (05-validation.md section 1.2) but never as a trusted length.
            int hint = 0;
            if (input.Length >= 4)
            {
                uint isize = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(input.Length - 4, 4));
                if (isize <= MaxTrailerHint) hint = (int)isize;
            }

            byte[] rented = input.ToArray();
            using (MemoryStream source = new MemoryStream(rented, writable: false))
            using (GZipStream gz = new GZipStream(source, CompressionMode.Decompress))
            using (MemoryStream target = hint > 0 ? new MemoryStream(hint) : new MemoryStream())
            {
                gz.CopyTo(target, 1 << 16);
                return target.ToArray();
            }
        }
    }
}
