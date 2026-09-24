using System;
using System.Buffers.Binary;
using System.IO;

namespace SeedLab.Saves
{
    /// <summary>What <c>cacheMinimapMeta</c> holds: eight raw bytes, not a ZPackage and not compressed.</summary>
    /// <remarks>
    /// <c>Minimap.SaveMapTextureDataToDisk</c> (decompiled) writes
    /// <c>BitConverter.GetBytes(ZNet.World.m_seed)</c> followed by <c>BitConverter.GetBytes(1)</c>.
    /// Observed for <c>asdasdasd</c>: <c>52 e6 5b 96 | 01 00 00 00</c> = (-1772362158, 1).
    /// </remarks>
    public sealed class MinimapCacheMeta
    {
        public MinimapCacheMeta(int seed, int cacheVersion)
        {
            Seed = seed;
            CacheVersion = cacheVersion;
        }

        public int Seed { get; }
        public int CacheVersion { get; }

        /// <summary><c>Version.CachedMinimap.Original</c> = 1 - the only version the game accepts.</summary>
        public bool IsCurrentVersion => CacheVersion == MinimapCacheReader.CurrentCacheVersion;

        public override string ToString() => "seed " + Seed + ", cache version " + CacheVersion;
    }

    /// <summary>Knobs for <see cref="MinimapCacheReader"/>. The defaults are the strict, loud ones.</summary>
    public sealed class MinimapCacheOptions
    {
        /// <summary>
        /// When set, the meta seed must equal this or the read fails. Pass the <c>.fwl2</c>'s
        /// <c>m_seed</c>: that is exactly the game's own staleness test in
        /// <c>Minimap.TryLoadMinimapTextureData</c>.
        /// </summary>
        public int? ExpectedSeed { get; set; }

        /// <summary>Reject a cache version other than 1, as the game does. Default true.</summary>
        public bool RequireCurrentCacheVersion { get; set; } = true;

        /// <summary>
        /// The metres-per-pixel to attach to the geometry. Not stored in the file, so it is supplied
        /// and then <i>checked against the file</i> when <see cref="VerifyPixelSize"/> is set.
        /// </summary>
        public float PixelSize { get; set; } = MinimapGeometry.MeasuredPixelSize;

        /// <summary>
        /// Derive the pixel size bracket from the height buffer's world-edge discontinuity and fail
        /// if <see cref="PixelSize"/> falls outside it. Default true: it is one pass over N*N halves
        /// (about 4 M compares) and it is the only thing standing between this reader and a silent
        /// coordinate error if a future build changes the prefab.
        /// </summary>
        public bool VerifyPixelSize { get; set; } = true;

        /// <summary>
        /// When set, the derived texture size must equal this. Leave null to accept whatever the
        /// buffer lengths imply (they are self-consistent across the three files regardless).
        /// </summary>
        public int? ExpectedTextureSize { get; set; }
    }

    /// <summary>
    /// Reads the four minimap-cache files. <b>Read-only</b>; see <see cref="IValheimReader"/>.
    /// <para>
    /// Layout (05-validation.md sections 1.1-1.5):
    /// <code>
    /// cacheMinimapBiome   gzip -&gt; Color32[N*N]   (R,G,B,A)
    /// cacheMinimapMask    gzip -&gt; Color32[N*N]   (R,G,B,A)
    /// cacheMinimapHeight  gzip -&gt; ushort[N*N]    (IEEE binary16, little-endian)
    /// cacheMinimapMeta    8 raw bytes: int32 seed, int32 cacheVersion(1)
    /// </code>
    /// The file names are string constants (<c>Minimap</c> lines 398-404) and there is no extension.
    /// </para>
    /// <para>
    /// <b>N is derived, never assumed.</b> 4 bytes per pixel is forced by <c>Color32</c> and 2 by
    /// <c>half</c>, so N = isqrt(len(biome)/4), and the reader asserts
    /// <c>N*N*4 == len(biome) == len(mask)</c> and <c>N*N*2 == len(height)</c>. Both Unity fields
    /// have wrong code defaults (<c>m_textureSize = 256</c>, <c>m_pixelSize = 64f</c>); they are
    /// prefab overrides, which is also why they could change in a future build without any code
    /// change (05-validation.md section 7 item 2).
    /// </para>
    /// </summary>
    public sealed class MinimapCacheReader : IValheimReader
    {
        public const string BiomeFileName = "cacheMinimapBiome";
        public const string MaskFileName = "cacheMinimapMask";
        public const string HeightFileName = "cacheMinimapHeight";
        public const string MetaFileName = "cacheMinimapMeta";

        /// <summary><c>Version.CachedMinimap.Original</c>.</summary>
        public const int CurrentCacheVersion = 1;

        /// <summary>True when all four cache files are present in <paramref name="directory"/>.</summary>
        public static bool Exists(string directory) =>
            File.Exists(Path.Combine(directory, BiomeFileName))
            && File.Exists(Path.Combine(directory, MaskFileName))
            && File.Exists(Path.Combine(directory, HeightFileName))
            && File.Exists(Path.Combine(directory, MetaFileName));

        /// <summary>Reads just <c>cacheMinimapMeta</c> - eight bytes, no decompression.</summary>
        public static MinimapCacheMeta ReadMeta(string directory)
        {
            string path = Path.Combine(directory, MetaFileName);
            byte[] bytes = SaveFileAccess.ReadAllBytes(path);
            return ParseMeta(bytes, path);
        }

        public static MinimapCacheMeta ParseMeta(byte[] bytes, string? path = null)
        {
            if (bytes.Length < 8)
                throw new InvalidDataException(
                    (path ?? MetaFileName) + ": expected 8 bytes (int32 seed, int32 version), found " + bytes.Length + ".");

            int seed = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4));
            int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4));
            return new MinimapCacheMeta(seed, version);
        }

        /// <summary>
        /// Reads and decodes a whole cache directory
        /// (<c>.../worlds_local/&lt;worldName&gt;/</c>).
        /// </summary>
        /// <exception cref="FileNotFoundException">One of the four files is missing.</exception>
        /// <exception cref="InvalidDataException">
        /// The gzip framing, the buffer lengths, the cache version, the seed or the pixel size do not
        /// hold up. All of these are "the file is not what we think it is" and none is recoverable by
        /// guessing.
        /// </exception>
        public static MinimapCache Read(string directory, MinimapCacheOptions? options = null)
        {
            options ??= new MinimapCacheOptions();

            MinimapCacheMeta meta = ReadMeta(directory);
            if (options.RequireCurrentCacheVersion && !meta.IsCurrentVersion)
                throw new InvalidDataException(
                    directory + ": minimap cache version is " + meta.CacheVersion + ", expected " +
                    CurrentCacheVersion + " (Version.CachedMinimap.Original). The game would " +
                    "regenerate this cache; it must not be trusted as ground truth.");

            if (options.ExpectedSeed.HasValue && meta.Seed != options.ExpectedSeed.Value)
                throw new InvalidDataException(
                    directory + ": minimap cache holds seed " + meta.Seed + " but the world's seed is " +
                    options.ExpectedSeed.Value + ". The cache is stale - the game itself rejects it " +
                    "in Minimap.TryLoadMinimapTextureData.");

            byte[] biome = Inflate(directory, BiomeFileName);
            byte[] mask = Inflate(directory, MaskFileName);
            byte[] heightBytes = Inflate(directory, HeightFileName);

            int textureSize = DeriveTextureSize(directory, biome.Length, mask.Length, heightBytes.Length);
            if (options.ExpectedTextureSize.HasValue && textureSize != options.ExpectedTextureSize.Value)
                throw new InvalidDataException(
                    directory + ": minimap texture size is " + textureSize + ", expected " +
                    options.ExpectedTextureSize.Value + ".");

            ushort[] heights = ToUInt16LittleEndian(heightBytes);

            MinimapGeometry geometry = new MinimapGeometry(textureSize, options.PixelSize);
            MinimapCache cache = new MinimapCache(meta.Seed, meta.CacheVersion, geometry, biome, mask, heights, directory);

            if (options.VerifyPixelSize)
            {
                PixelSizeBracket bracket = cache.DerivePixelSizeBracket();
                if (bracket.IsDetermined && !bracket.Contains(options.PixelSize))
                    throw new InvalidDataException(
                        directory + ": the height buffer's world-edge discontinuity brackets " +
                        "m_pixelSize to " + bracket + ", which excludes the assumed " +
                        options.PixelSize + " m/px. Either this build changed Minimap.m_pixelSize " +
                        "or the pixel-to-world mapping is wrong; every coordinate from this cache " +
                        "would be wrong, so this read fails rather than continuing.");
            }

            return cache;
        }

        private static byte[] Inflate(string directory, string name)
        {
            string path = Path.Combine(directory, name);
            byte[] raw = SaveFileAccess.ReadAllBytes(path);
            if (!SaveCompression.LooksLikeGzip(raw))
                throw new InvalidDataException(
                    path + ": not a gzip stream. Valheim writes this buffer with Utils.Compress " +
                    "(plain gzip, no ZPackage wrapper and no length prefix).");
            return SaveCompression.Decompress(raw);
        }

        /// <summary>
        /// N from the buffer lengths. 4 bytes per pixel is forced by <c>Color32</c> and 2 by
        /// <c>half</c>, so all three files agree on N or the cache is not what we think it is.
        /// </summary>
        private static int DeriveTextureSize(string directory, int biomeLen, int maskLen, int heightLen)
        {
            if (biomeLen != maskLen)
                throw new InvalidDataException(
                    directory + ": biome buffer is " + biomeLen + " bytes but mask buffer is " +
                    maskLen + "; both are Color32[N*N] and must match.");

            if (biomeLen % 4 != 0)
                throw new InvalidDataException(
                    directory + ": biome buffer is " + biomeLen + " bytes, not a multiple of 4 (Color32).");

            long pixels = biomeLen / 4;
            int n = (int)Math.Round(Math.Sqrt(pixels));
            if ((long)n * n != pixels || n <= 0)
                throw new InvalidDataException(
                    directory + ": biome buffer holds " + pixels + " pixels, which is not a square.");

            if ((long)n * n * 2 != heightLen)
                throw new InvalidDataException(
                    directory + ": height buffer is " + heightLen + " bytes, expected " +
                    ((long)n * n * 2) + " for a " + n + "x" + n + " half buffer.");

            if ((n & 1) != 0)
                throw new InvalidDataException(
                    directory + ": texture size " + n + " is odd; the game's pixel-to-world mapping " +
                    "uses the integer division m_textureSize / 2.");

            return n;
        }

        private static ushort[] ToUInt16LittleEndian(byte[] bytes)
        {
            ushort[] result = new ushort[bytes.Length / 2];
            if (BitConverter.IsLittleEndian)
            {
                Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            }
            else
            {
                for (int i = 0; i < result.Length; i++)
                    result[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
            }
            return result;
        }
    }
}
