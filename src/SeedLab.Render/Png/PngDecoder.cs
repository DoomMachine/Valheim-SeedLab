using System;
using System.IO;
using System.IO.Compression;

namespace SeedLab.Render.Png
{
    /// <summary>One decoded 8-bit truecolour image.</summary>
    public sealed class DecodedPng
    {
        public DecodedPng(int width, int height, byte[] rgb)
        {
            Width = width;
            Height = height;
            Rgb = rgb;
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>width*height*3 bytes, row 0 at the top.</summary>
        public byte[] Rgb { get; }
    }

    /// <summary>
    /// Reads back what <see cref="PngEncoder"/> writes: 8-bit colour type 2, no interlace.
    /// It exists so <c>vseed selftest --map</c> can compare the bytes of a PNG that was actually
    /// written to disk against the ground-truth oracle, rather than comparing the in-memory buffer
    /// the encoder was handed - that would leave the encoder itself unverified.
    /// </summary>
    public static class PngDecoder
    {
        public static DecodedPng ReadFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Decode(bytes);
        }

        public static DecodedPng Decode(byte[] bytes)
        {
            if (bytes.Length < 8 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71)
            {
                throw new InvalidDataException("Not a PNG (signature mismatch).");
            }

            int p = 8;
            int width = 0, height = 0, bitDepth = 0, colourType = -1, interlace = 0;
            using MemoryStream idat = new MemoryStream();

            while (p + 8 <= bytes.Length)
            {
                int len = ReadBigEndian(bytes, p);
                string type = "" + (char)bytes[p + 4] + (char)bytes[p + 5] + (char)bytes[p + 6] + (char)bytes[p + 7];
                int dataStart = p + 8;
                if (dataStart + len + 4 > bytes.Length) throw new InvalidDataException("Truncated PNG chunk " + type + ".");

                uint want = (uint)ReadBigEndian(bytes, dataStart + len);
                uint got = Crc32.Finish(Crc32.Compute(bytes.AsSpan(p + 4, 4 + len)));
                if (want != got) throw new InvalidDataException($"CRC mismatch in chunk {type}.");

                switch (type)
                {
                    case "IHDR":
                        width = ReadBigEndian(bytes, dataStart);
                        height = ReadBigEndian(bytes, dataStart + 4);
                        bitDepth = bytes[dataStart + 8];
                        colourType = bytes[dataStart + 9];
                        interlace = bytes[dataStart + 12];
                        break;
                    case "IDAT":
                        idat.Write(bytes, dataStart, len);
                        break;
                    case "IEND":
                        p = bytes.Length;
                        break;
                }

                if (type == "IEND") break;
                p = dataStart + len + 4;
            }

            if (bitDepth != 8 || colourType != 2 || interlace != 0)
            {
                throw new NotSupportedException(
                    $"Only 8-bit non-interlaced truecolour PNG is supported (got depth {bitDepth}, colour type {colourType}, interlace {interlace}).");
            }

            byte[] zlib = idat.ToArray();
            if (zlib.Length < 6) throw new InvalidDataException("Empty IDAT.");
            byte[] raw;
            // Skip the 2-byte zlib header and the 4-byte Adler trailer; DeflateStream wants raw deflate.
            using (MemoryStream src = new MemoryStream(zlib, 2, zlib.Length - 6))
            using (DeflateStream ds = new DeflateStream(src, CompressionMode.Decompress))
            using (MemoryStream dst = new MemoryStream(width * height * 3 + height))
            {
                ds.CopyTo(dst);
                raw = dst.ToArray();
            }

            uint adlerWant = (uint)ReadBigEndian(zlib, zlib.Length - 4);
            uint adlerGot = Adler32.Compute(raw);
            if (adlerWant != adlerGot) throw new InvalidDataException("Adler-32 mismatch in the zlib stream.");

            const int bpp = 3;
            int stride = width * bpp;
            if (raw.Length != (long)height * (stride + 1))
            {
                throw new InvalidDataException($"Inflated {raw.Length} bytes, expected {(long)height * (stride + 1)}.");
            }

            byte[] rgb = new byte[(long)height * stride <= int.MaxValue ? height * stride : throw new InvalidDataException("Image too large.")];
            byte[] prior = new byte[stride];
            byte[] cur = new byte[stride];
            int rp = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = raw[rp++];
                Buffer.BlockCopy(raw, rp, cur, 0, stride);
                rp += stride;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? cur[x - bpp] : 0;
                    int b = prior[x];
                    int c = x >= bpp ? prior[x - bpp] : 0;
                    int add = filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => PngEncoder.Paeth((byte)a, (byte)b, (byte)c),
                        _ => throw new InvalidDataException("Unknown PNG filter type " + filter + "."),
                    };
                    cur[x] = (byte)(cur[x] + add);
                }

                Buffer.BlockCopy(cur, 0, rgb, y * stride, stride);
                Buffer.BlockCopy(cur, 0, prior, 0, stride);
            }

            return new DecodedPng(width, height, rgb);
        }

        private static int ReadBigEndian(byte[] b, int o) =>
            (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    }
}
