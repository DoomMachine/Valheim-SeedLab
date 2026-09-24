using System;
using SeedLab.WorldGen;

namespace SeedLab.Saves
{
    /// <summary>
    /// A decoded minimap cache: the game's own <c>GetBiome</c> / <c>GetBiomeHeight</c> output for
    /// every pixel of the world map, which makes it the primary oracle for the port
    /// (05-validation.md tests T2, T3 and T4).
    /// <para>
    /// The game writes it in <c>Minimap.GenerateWorldMap</c> -&gt; <c>SaveMapTextureDataToDisk</c> as
    /// four files with no extension in
    /// <c>World.GetSaveDirectory(FileHelpers.FileSource.Local)/&lt;worldName&gt;/</c> - <b>always the
    /// Local directory, even for a Steam Cloud world</b> (<c>Minimap.Start</c>, line 571), which is
    /// why a cache exists under <c>worlds_local</c> for a world whose save lives in Steam Cloud.
    /// </para>
    /// <para>This object owns plain arrays and no file handles. It is immutable in practice; the
    /// buffers are exposed directly so a 4-million-pixel comparison does not have to copy them.</para>
    /// </summary>
    public sealed class MinimapCache
    {
        public MinimapCache(int seed, int cacheVersion, MinimapGeometry geometry,
                            byte[] biomeRgba, byte[] maskRgba, ushort[] heightHalf,
                            string? sourceDirectory = null)
        {
            Seed = seed;
            CacheVersion = cacheVersion;
            Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
            BiomeRgba = biomeRgba ?? throw new ArgumentNullException(nameof(biomeRgba));
            MaskRgba = maskRgba ?? throw new ArgumentNullException(nameof(maskRgba));
            HeightHalf = heightHalf ?? throw new ArgumentNullException(nameof(heightHalf));
            SourceDirectory = sourceDirectory;
        }

        /// <summary>
        /// The world seed recorded in <c>cacheMinimapMeta</c>. The game refuses the cache when this
        /// does not equal <c>ZNet.World.m_seed</c> (<c>Minimap.TryLoadMinimapTextureData</c>), so a
        /// mismatch means the cache is stale, not that the seed is different.
        /// </summary>
        public int Seed { get; }

        /// <summary><c>Version.CachedMinimap.Original</c> = 1. Any other value and the game regenerates.</summary>
        public int CacheVersion { get; }

        public MinimapGeometry Geometry { get; }

        /// <summary>
        /// <c>cacheMinimapBiome</c>: N*N*4 bytes, R,G,B,A per pixel, from
        /// <c>Minimap.GetPixelColor(biome)</c>. Alpha is 255 on every pixel of both ground-truth
        /// worlds. Decode with <see cref="MinimapColors"/>; remember the white collision.
        /// </summary>
        public byte[] BiomeRgba { get; }

        /// <summary>
        /// <c>cacheMinimapMask</c>: N*N*4 bytes, R,G,B,A per pixel, from
        /// <c>Minimap.GetMaskColor(wx, wy, height, biome)</c>. Channel meanings (05-validation.md
        /// section 2.4):
        /// <list type="bullet">
        /// <item><b>R</b> in {0,255} - "forest here": 255 for all BlackForest land, Meadows land with
        /// <c>InForest</c> (<c>GetForestFactor &lt; 1.15</c>), and Plains land with
        /// <c>GetForestFactor &lt; 0.8</c>.</item>
        /// <item><b>G</b> in [0,255] - Mistlands land only:
        /// <c>1 - Utils.SmoothStep(1.1f, 1.3f, GetForestFactor)</c> (the all-float <c>Utils</c>
        /// version, not <c>DUtils</c>).</item>
        /// <item><b>B</b> in [0,255] - two different quantities. For <i>any</i> pixel with height
        /// &lt; 30 m it is <c>Clamp01(GetAshlandsOceanGradient(wx, wy))</c>, a closed-form noise-free
        /// ramp; for AshLands <i>land</i> it is the alpha of
        /// <c>GetAshlandsHeight(..., cheap: true)</c>'s mask.</item>
        /// <item><b>A</b> = 0 on all 4 194 304 pixels of both worlds.</item>
        /// </list>
        /// </summary>
        public byte[] MaskRgba { get; }

        /// <summary>
        /// <c>cacheMinimapHeight</c>: N*N IEEE binary16 bit patterns, little-endian on disk, from
        /// <c>Utils.FloatsToCompressedHalfBuffer(GetBiomeHeight(...))</c>.
        /// <para>
        /// <b>Kept as raw <c>ushort</c> deliberately.</b> Comparisons must be on bit patterns:
        /// subnormal codes occur (363 pixels in <c>asdasdasd</c>, 392 in <c>testworldclaude</c>) and
        /// <c>-0.0</c> occurs (0x8000, once and twice respectively), and a decoder that compares
        /// decoded floats cannot see a signed-zero error at all (05-validation.md section 5.5).
        /// </para>
        /// </summary>
        public ushort[] HeightHalf { get; }

        /// <summary>Where this cache was read from, for diagnostics. Null when parsed from memory.</summary>
        public string? SourceDirectory { get; }

        public int PixelCount => Geometry.PixelCount;

        public Rgba32 GetBiomeColor(int index)
        {
            int o = index * 4;
            return new Rgba32(BiomeRgba[o], BiomeRgba[o + 1], BiomeRgba[o + 2], BiomeRgba[o + 3]);
        }

        public Rgba32 GetMaskColor(int index)
        {
            int o = index * 4;
            return new Rgba32(MaskRgba[o], MaskRgba[o + 1], MaskRgba[o + 2], MaskRgba[o + 3]);
        }

        /// <summary>The raw binary16 bit pattern. Compare these, not decoded floats.</summary>
        public ushort GetHeightBits(int index) => HeightHalf[index];

        /// <summary>The stored height in metres, half-precision quantised.</summary>
        public float GetHeight(int index) => Half16.ToSingle(HeightHalf[index]);

        /// <summary>
        /// The biome(s) this pixel's colour can mean. Returns <c>false</c> when the colour is not in
        /// the measured table. For white, <paramref name="candidates"/> has three bits set.
        /// </summary>
        public bool TryGetBiome(int index, out Biome candidates)
        {
            int o = index * 4;
            return MinimapColors.TryDecode(BiomeRgba[o], BiomeRgba[o + 1], BiomeRgba[o + 2], out candidates);
        }

        /// <summary>
        /// The "forest here" bit of the mask's red channel.
        /// </summary>
        public bool MaskIsForest(int index) => MaskRgba[index * 4] != 0;

        /// <summary>The mask's green channel, 0..255 - Mistlands mist-forest density; 0 elsewhere.</summary>
        public byte MaskMistDensity(int index) => MaskRgba[index * 4 + 1];

        /// <summary>
        /// The mask's blue channel, 0..255. Below 30 m this is the Ashlands ocean gradient; on
        /// AshLands land it is the terrain mask alpha. The two cases are told apart by the height and
        /// the biome, not by the byte.
        /// </summary>
        public byte MaskBlue(int index) => MaskRgba[index * 4 + 2];

        /// <summary>
        /// Height at a world position, or <c>null</c> when the position is off the map. Uses
        /// <see cref="MinimapGeometry.TryWorldToSampleIndex"/>, because the question is "what did the
        /// generator produce here", not "which pixel would the game light up" - the two differ by
        /// half a pixel, see <see cref="MinimapGeometry.WorldToPixel"/>.
        /// </summary>
        public float? GetHeightAtWorld(float worldX, float worldZ) =>
            Geometry.TryWorldToSampleIndex(worldX, worldZ, out int i) ? Half16.ToSingle(HeightHalf[i]) : (float?)null;

        /// <summary>
        /// Biome candidates at a world position. <c>false</c> when off the map or the colour is
        /// unknown; for white pixels the result is <see cref="MinimapColors.WhiteCandidates"/>.
        /// Uses the sample mapping, as <see cref="GetHeightAtWorld"/> does.
        /// </summary>
        public bool TryGetBiomeAtWorld(float worldX, float worldZ, out Biome candidates)
        {
            if (!Geometry.TryWorldToSampleIndex(worldX, worldZ, out int i))
            {
                candidates = Biome.None;
                return false;
            }
            return TryGetBiome(i, out candidates);
        }

        /// <summary>
        /// Decodes the whole biome buffer to one byte per pixel: 0..8 for
        /// <c>Heightmap.BiomeIndex</c>, <see cref="MinimapBiomeIndex.AmbiguousWhite"/> (255) for the
        /// Ocean/Mountain/DeepNorth white, and <see cref="MinimapBiomeIndex.Unknown"/> (254) for a
        /// colour outside the measured table. Row-major, row 0 south, exactly like the cache.
        /// </summary>
        public byte[] DecodeBiomeIndices()
        {
            byte[] result = new byte[PixelCount];
            for (int i = 0; i < result.Length; i++)
            {
                int o = i * 4;
                if (!MinimapColors.TryDecode(BiomeRgba[o], BiomeRgba[o + 1], BiomeRgba[o + 2], out Biome b))
                {
                    result[i] = MinimapBiomeIndex.Unknown;
                }
                else if (MinimapColors.IsAmbiguous(b))
                {
                    result[i] = MinimapBiomeIndex.AmbiguousWhite;
                }
                else
                {
                    result[i] = (byte)b.ToIndex();
                }
            }
            return result;
        }

        /// <summary>
        /// Decodes the whole height buffer to float32. For a comparison against the port, prefer
        /// <see cref="HeightHalf"/> and compare bit patterns; this is for display and for the
        /// float32 ground-truth dumps.
        /// </summary>
        public float[] DecodeHeights()
        {
            float[] result = new float[PixelCount];
            for (int i = 0; i < result.Length; i++) result[i] = Half16.ToSingle(HeightHalf[i]);
            return result;
        }

        /// <summary>
        /// Recovers the interval that <c>m_pixelSize</c> must lie in, <b>from this file's own
        /// contents</b> - P is not stored anywhere, and the Unity field's code default (64f) is wrong.
        /// <para>
        /// Method (05-validation.md section 1.5). <c>WorldGenerator.GetBiomeHeight</c> line 1032
        /// begins <c>if (DUtils.Length(wx, wy) &gt; 10500f) return -2f * GetHeightMultiplier();</c>
        /// = -400, so the set of pixels storing exactly -400 is exactly the set with world radius
        /// above 10 500 m. With <c>u(i,j) = hypot(j - N/2 + 0.5, i - N/2 + 0.5)</c> the pixel radius
        /// in pixel units and radius = P*u, every -400 pixel gives <c>P &gt; 10500/u</c> and every
        /// other pixel gives <c>P &lt;= 10500/u</c>. Measured on both ground-truth worlds:
        /// max u inside = 874.999714, min u outside = 875.000857, so P is in
        /// (11.999988, 12.000004) - which brackets 12.0 and excludes every other plausible value.
        /// </para>
        /// <para>This is an analysis of the data, not a port of game code, so it computes in double.</para>
        /// </summary>
        public PixelSizeBracket DerivePixelSizeBracket()
        {
            const double waterEdge = 10500.0; // WorldGenerator.waterEdge = 10500f (line 174)

            int n = Geometry.TextureSize;
            double half = n / 2;
            double maxInside = 0.0;
            double minOutside = double.PositiveInfinity;
            long edgeCount = 0;

            for (int row = 0; row < n; row++)
            {
                double dy = row - half + 0.5;
                double dy2 = dy * dy;
                int rowBase = row * n;
                for (int col = 0; col < n; col++)
                {
                    double dx = col - half + 0.5;
                    double u = Math.Sqrt(dx * dx + dy2);
                    bool outside = Half16.ToSingle(HeightHalf[rowBase + col]) == Half16.WorldEdgeHeight;
                    if (outside)
                    {
                        edgeCount++;
                        if (u < minOutside) minOutside = u;
                    }
                    else if (u > maxInside)
                    {
                        maxInside = u;
                    }
                }
            }

            if (edgeCount == 0)
                return new PixelSizeBracket(0.0, 0.0, maxInside, 0.0, 0);

            double lower = waterEdge / minOutside;   // P must exceed this
            double upper = maxInside > 0.0 ? waterEdge / maxInside : double.PositiveInfinity;
            return new PixelSizeBracket(lower, upper, maxInside, minOutside, edgeCount);
        }
    }
}
