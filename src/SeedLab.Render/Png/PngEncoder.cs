using System;
using System.IO;
using System.IO.Compression;

namespace SeedLab.Render.Png
{
    /// <summary>
    /// A minimal PNG writer: 8-bit truecolour (colour type 2), no interlace, one IDAT.
    /// Hand-written because SeedLab takes no NuGet packages and System.Drawing is Windows-only;
    /// the only external machinery is <see cref="DeflateStream"/>, which gives raw deflate, wrapped
    /// here in the two-byte zlib header and four-byte Adler-32 trailer that RFC 1950 requires.
    /// </summary>
    public static class PngEncoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        /// <summary>Encodes <paramref name="rgb"/> (width*height*3 bytes, row 0 at the top) to a PNG file.</summary>
        public static void WriteFile(string path, byte[] rgb, int width, int height,
                                     CompressionLevel level = CompressionLevel.Optimal)
        {
            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            Write(fs, rgb, width, height, level);
        }

        public static void Write(Stream output, byte[] rgb, int width, int height,
                                 CompressionLevel level = CompressionLevel.Optimal)
        {
            if (rgb is null) throw new ArgumentNullException(nameof(rgb));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image must be non-empty.");
            long need = (long)width * height * 3;
            if (rgb.Length < need)
            {
                throw new ArgumentException($"Pixel buffer holds {rgb.Length} bytes, {need} needed for {width}x{height} RGB.", nameof(rgb));
            }

            output.Write(Signature, 0, Signature.Length);

            byte[] ihdr = new byte[13];
            WriteBigEndian(ihdr, 0, (uint)width);
            WriteBigEndian(ihdr, 4, (uint)height);
            ihdr[8] = 8;        // bit depth
            ihdr[9] = 2;        // colour type 2 = truecolour RGB
            ihdr[10] = 0;       // deflate
            ihdr[11] = 0;       // adaptive filtering
            ihdr[12] = 0;       // no interlace
            WriteChunk(output, "IHDR", ihdr);

            byte[] filtered = Filter(rgb, width, height);
            byte[] idat = ZlibCompress(filtered, level);
            WriteChunk(output, "IDAT", idat);
            WriteChunk(output, "IEND", Array.Empty<byte>());
        }

        private static byte[] ZlibCompress(byte[] raw, CompressionLevel level)
        {
            using MemoryStream ms = new MemoryStream(raw.Length / 3 + 64);
            // RFC 1950 header: CM=8 (deflate), CINFO=7 (32K window) -> 0x78; FLG chosen so that
            // (CMF<<8 | FLG) % 31 == 0 and FDICT = 0. 0x9C is the "default compression" pair.
            ms.WriteByte(0x78);
            ms.WriteByte(0x9C);
            using (DeflateStream ds = new DeflateStream(ms, level, leaveOpen: true))
            {
                ds.Write(raw, 0, raw.Length);
            }

            uint adler = Adler32.Compute(raw);
            ms.WriteByte((byte)(adler >> 24));
            ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8));
            ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        /// <summary>
        /// Per-scanline adaptive filtering with the PNG specification's own "minimum sum of absolute
        /// differences" heuristic over the five filter types. It costs one extra pass over the image
        /// and typically halves the file next to filter 0.
        /// </summary>
        private static byte[] Filter(byte[] rgb, int width, int height)
        {
            const int bpp = 3;
            int stride = width * bpp;
            byte[] outBuf = new byte[(long)height * (stride + 1) <= int.MaxValue
                ? height * (stride + 1)
                : throw new ArgumentOutOfRangeException(nameof(width), "Image too large for a single buffer.")];
            byte[] prior = new byte[stride];
            byte[][] cand = new byte[5][];
            for (int t = 0; t < 5; t++) cand[t] = new byte[stride];

            int op = 0;
            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                int best = 0;
                long bestScore = long.MaxValue;
                for (int t = 0; t < 5; t++)
                {
                    byte[] c = cand[t];
                    long score = 0;
                    for (int x = 0; x < stride; x++)
                    {
                        byte raw = rgb[rowStart + x];
                        byte a = x >= bpp ? rgb[rowStart + x - bpp] : (byte)0;
                        byte b = prior[x];
                        byte cc = x >= bpp ? prior[x - bpp] : (byte)0;
                        byte v = t switch
                        {
                            0 => raw,
                            1 => (byte)(raw - a),
                            2 => (byte)(raw - b),
                            3 => (byte)(raw - (byte)((a + b) >> 1)),
                            _ => (byte)(raw - Paeth(a, b, cc)),
                        };
                        c[x] = v;
                        score += v < 128 ? v : 256 - v;      // signed magnitude, the spec's heuristic
                    }

                    if (score < bestScore) { bestScore = score; best = t; }
                }

                outBuf[op++] = (byte)best;
                Buffer.BlockCopy(cand[best], 0, outBuf, op, stride);
                op += stride;
                Buffer.BlockCopy(rgb, rowStart, prior, 0, stride);
            }

            return outBuf;
        }

        internal static byte Paeth(byte a, byte b, byte c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            Span<byte> len = stackalloc byte[4];
            WriteBigEndian(len, 0, (uint)data.Length);
            s.Write(len);

            Span<byte> tag = stackalloc byte[4];
            for (int i = 0; i < 4; i++) tag[i] = (byte)type[i];
            s.Write(tag);
            s.Write(data, 0, data.Length);

            uint crc = Crc32.Compute(data, Crc32.Compute(tag));
            Span<byte> c = stackalloc byte[4];
            WriteBigEndian(c, 0, Crc32.Finish(crc));
            s.Write(c);
        }

        private static void WriteBigEndian(Span<byte> dst, int off, uint v)
        {
            dst[off] = (byte)(v >> 24);
            dst[off + 1] = (byte)(v >> 16);
            dst[off + 2] = (byte)(v >> 8);
            dst[off + 3] = (byte)v;
        }
    }
}
