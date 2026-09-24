using System;

namespace SeedLab.Render.Png
{
    /// <summary>
    /// The CRC-32 that PNG chunks carry (ISO 3309 / ITU-T V.42, polynomial 0xEDB88320 reflected),
    /// exactly as the PNG specification's own sample implementation computes it. The BCL has no
    /// public CRC-32 on this target framework and SeedLab takes no NuGet packages, so it is here.
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            uint[] t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                t[n] = c;
            }

            return t;
        }

        public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0xFFFFFFFFu)
        {
            uint c = seed;
            for (int i = 0; i < data.Length; i++)
            {
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }

            return c;
        }

        public static uint Finish(uint running) => running ^ 0xFFFFFFFFu;
    }

    /// <summary>
    /// Adler-32 (RFC 1950 section 9), the checksum in the zlib trailer that wraps the deflate stream
    /// inside an IDAT chunk. <see cref="System.IO.Compression.DeflateStream"/> emits raw deflate with
    /// no zlib header or trailer, so both have to be written by hand.
    /// </summary>
    internal static class Adler32
    {
        private const uint Base = 65521;

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                // 5552 is the largest block that cannot overflow the 32-bit accumulators (RFC 1950).
                int n = Math.Min(5552, data.Length - i);
                for (int k = 0; k < n; k++)
                {
                    a += data[i + k];
                    b += a;
                }

                a %= Base;
                b %= Base;
                i += n;
            }

            return (b << 16) | a;
        }
    }
}
