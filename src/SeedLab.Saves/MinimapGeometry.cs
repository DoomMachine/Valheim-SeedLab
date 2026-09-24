using System;

namespace SeedLab.Saves
{
    /// <summary>
    /// The minimap cache's pixel grid and its mapping to world coordinates.
    /// <para>
    /// <c>Minimap.GenerateWorldMap</c> (decompiled) writes, with <c>N = m_textureSize</c> and
    /// <c>P = m_pixelSize</c>:
    /// <code>
    /// int num = m_textureSize / 2;            // 1024
    /// float num2 = m_pixelSize / 2f;          // 6
    /// for (int i = 0; i &lt; m_textureSize; i++) {
    ///     float wy = (float)(i - num) * m_pixelSize + num2;
    ///     for (int j = 0; j &lt; m_textureSize; j++) {
    ///         float wx = (float)(j - num) * m_pixelSize + num2;
    ///         ...
    ///         int num3 = i * m_textureSize + j;
    /// </code>
    /// so index <c>k = i*N + j</c>, <b>row 0 is the southernmost row</b> and column 0 the westernmost
    /// (Unity's texture convention; <c>SetPixels32</c> fills bottom-up). The orientation is proved,
    /// not assumed: <c>WorldGenerator.IsAshlands</c> is asymmetric in both x and y and, with this
    /// mapping, is true for exactly the 1 361 539 pixels carrying the Ashlands colour and no other
    /// (05-validation.md sections 1.4 and 2.2). Any transposition or flip destroys that.
    /// </para>
    /// <para>
    /// Neither N nor P is stored in the cache, and <b>both Unity fields have wrong code defaults</b>
    /// (<c>m_textureSize = 256</c>, <c>m_pixelSize = 64f</c> - they are prefab overrides). N is
    /// derived from the buffer lengths and P is checked against the file by
    /// <see cref="MinimapCache.DerivePixelSizeBracket"/>; neither is taken on trust.
    /// </para>
    /// </summary>
    public sealed class MinimapGeometry
    {
        /// <summary>The value measured on this build, for both worlds. Derived, then asserted - never assumed.</summary>
        public const int MeasuredTextureSize = 2048;

        /// <summary>
        /// The value measured on this build: 12.0 m per pixel. Bracketed to (11.999988, 12.000004) by
        /// the world-edge discontinuity in the height buffer (05-validation.md section 1.5) and
        /// corroborated by <c>AltBiomeWorldData.c_pixelSize = 12f</c> (decompiled, line 18).
        /// </summary>
        public const float MeasuredPixelSize = 12f;

        public MinimapGeometry(int textureSize, float pixelSize)
        {
            if (textureSize <= 0 || (textureSize & 1) != 0)
                throw new ArgumentOutOfRangeException(nameof(textureSize), textureSize, "Texture size must be positive and even.");
            if (!(pixelSize > 0f))
                throw new ArgumentOutOfRangeException(nameof(pixelSize), pixelSize, "Pixel size must be positive.");

            TextureSize = textureSize;
            PixelSize = pixelSize;
        }

        /// <summary>N - pixels per side. Derived from the cache buffer lengths.</summary>
        public int TextureSize { get; }

        /// <summary>P - metres per pixel.</summary>
        public float PixelSize { get; }

        /// <summary>N/2, the game's <c>num</c>.</summary>
        public int HalfTextureSize => TextureSize / 2;

        /// <summary>N*N.</summary>
        public int PixelCount => TextureSize * TextureSize;

        /// <summary>Half a pixel, the game's <c>num2 = m_pixelSize / 2f</c>. A float divide, as in the game.</summary>
        public float HalfPixelSize => PixelSize / 2f;

        /// <summary>Metres across the whole image (N * P): 24 576 m at 2048 x 12.</summary>
        public float WorldSpan => TextureSize * PixelSize;

        /// <summary><c>k = i*N + j</c> for row <c>i</c> (south to north) and column <c>j</c> (west to east).</summary>
        public int Index(int row, int col) => row * TextureSize + col;

        /// <summary>The row of a linear index.</summary>
        public int RowOf(int index) => index / TextureSize;

        /// <summary>The column of a linear index.</summary>
        public int ColumnOf(int index) => index % TextureSize;

        /// <summary>
        /// The world x at which column <c>col</c>'s stored value was sampled:
        /// <c>(float)(j - N/2) * P + P/2</c>, exactly as <c>GenerateWorldMap</c> computes it. This is
        /// the argument the biome, height and mask buffers were produced with, so it is the one the
        /// acceptance tests must use.
        /// </summary>
        public float SampleWorldX(int col) => (float)(col - HalfTextureSize) * PixelSize + HalfPixelSize;

        /// <summary>
        /// The world z at which row <c>row</c>'s stored value was sampled:
        /// <c>(float)(i - N/2) * P + P/2</c>. Named "wy" in <c>GenerateWorldMap</c> because
        /// <c>GetBiome(wx, wy)</c> takes the world's <b>z</b> as its second argument.
        /// </summary>
        public float SampleWorldZ(int row) => (float)(row - HalfTextureSize) * PixelSize + HalfPixelSize;

        /// <summary>
        /// The world x that column <c>col</c> is <i>placed</i> at by the game's own inverse mapping:
        /// <c>(col - N/2) * P</c>, with <b>no</b> half-pixel term. See the remarks on
        /// <see cref="WorldToPixel"/> - this differs from <see cref="SampleWorldX"/> by P/2.
        /// </summary>
        /// <remarks>
        /// <c>Minimap.MapPointToWorld</c> (decompiled, lines 1805-1812) ends
        /// <c>mx -= (float)num; mx *= m_pixelSize;</c>, i.e. exactly this expression.
        /// </remarks>
        public float AnchorWorldX(int col) => (float)(col - HalfTextureSize) * PixelSize;

        /// <summary>The world z counterpart of <see cref="AnchorWorldX"/>.</summary>
        public float AnchorWorldZ(int row) => (float)(row - HalfTextureSize) * PixelSize;

        /// <summary>
        /// <c>Minimap.WorldToPixel</c> (decompiled, lines 1815-1819), ported literally:
        /// <code>
        /// int num = m_textureSize / 2;
        /// px = Utils.RoundToInt(p.x / m_pixelSize + (float)num);
        /// py = Utils.RoundToInt(p.z / m_pixelSize + (float)num);
        /// </code>
        /// <b>Uses <see cref="ValheimRounding.RoundToInt"/>, not <c>Math.Round</c>.</b> Because the
        /// bias is added in float, this is only round-half-up to within about 1/128 of a pixel; do not
        /// substitute an exact rounding rule (05-validation.md section 1.4). The result is not
        /// clamped, exactly as in the game - it can fall outside [0, N).
        /// <para>
        /// <b>Correction to 05-validation.md section 1.4.</b> The spec states that
        /// "<c>WorldToPixel(centre of pixel k) == k</c> holds". <b>It does not.</b> For the sample
        /// point of column j, <c>wx / P + N/2</c> is exactly <c>j + 0.5</c> (the value is
        /// <c>12j - 12288 + 6</c>, and both the divide and the add are exact in float), and
        /// <c>RoundToInt(j + 0.5f) = (int)(j + 64001.0f) - 64000 = j + 1</c>. So
        /// <c>WorldToPixel(SampleWorldX(j), SampleWorldZ(i)) == (j + 1, i + 1)</c> for <i>every</i>
        /// pixel - measured over all 2048^2 (see <see cref="TryWorldToSampleIndex"/> for the inverse
        /// that does round-trip).
        /// </para>
        /// <para>
        /// This is a genuine half-pixel (P/2 = 6 m) inconsistency <b>inside the game</b>, not a port
        /// error: <c>GenerateWorldMap</c> samples pixel j at <c>(j - N/2)*P + P/2</c> while
        /// <c>WorldToPixel</c> and <c>MapPointToWorld</c> both place pixel j at <c>(j - N/2)*P</c>.
        /// It is invisible in play (half a pixel of fog) and it is faithfully reproduced by indexing
        /// the explore bitmap and the biome buffer with the same k, which is what the game does
        /// (<c>Minimap.Explore(Vector3, float)</c> calls <c>WorldToPixel</c> and then indexes
        /// <c>m_explored[y * m_textureSize + x]</c>, lines 1836-1866). Use this method for anything
        /// that must agree with the game's own pixel choice - pins, exploration - and
        /// <see cref="TryWorldToSampleIndex"/> for "what does the cache say about this world point".
        /// </para>
        /// </summary>
        public void WorldToPixel(float worldX, float worldZ, out int col, out int row)
        {
            int num = HalfTextureSize;
            col = ValheimRounding.RoundToInt(worldX / PixelSize + (float)num);
            row = ValheimRounding.RoundToInt(worldZ / PixelSize + (float)num);
        }

        /// <summary><see cref="WorldToPixel"/> plus a bounds check.</summary>
        public bool TryWorldToIndex(float worldX, float worldZ, out int index)
        {
            WorldToPixel(worldX, worldZ, out int col, out int row);
            if ((uint)col >= (uint)TextureSize || (uint)row >= (uint)TextureSize)
            {
                index = -1;
                return false;
            }
            index = Index(row, col);
            return true;
        }

        /// <summary>
        /// The pixel whose <b>stored sample</b> represents this world point, i.e. the inverse of
        /// <see cref="SampleWorldX"/>/<see cref="SampleWorldZ"/>: <c>floor(x/P + N/2)</c>, so pixel j
        /// owns <c>[SampleWorldX(j) - P/2, SampleWorldX(j) + P/2)</c>.
        /// <para>
        /// This is <b>not</b> a port of any game method - the game has no inverse of
        /// <c>GenerateWorldMap</c>'s sampling - so it uses ordinary <c>MathF.Floor</c> rather than
        /// <see cref="ValheimRounding.RoundToInt"/>. It is the right mapping for asking the cache
        /// what it holds at a world position; use <see cref="WorldToPixel"/> when you need the pixel
        /// the game itself would have touched.
        /// </para>
        /// </summary>
        public bool TryWorldToSampleIndex(float worldX, float worldZ, out int index)
        {
            int col = (int)MathF.Floor(worldX / PixelSize + TextureSize * 0.5f);
            int row = (int)MathF.Floor(worldZ / PixelSize + TextureSize * 0.5f);
            if ((uint)col >= (uint)TextureSize || (uint)row >= (uint)TextureSize)
            {
                index = -1;
                return false;
            }
            index = Index(row, col);
            return true;
        }

        public override string ToString() => TextureSize + "x" + TextureSize + " @ " + PixelSize + " m/px";
    }

    /// <summary>
    /// The interval the cache's own contents allow for <c>m_pixelSize</c>, and the two measurements
    /// that bound it. See <see cref="MinimapCache.DerivePixelSizeBracket"/>.
    /// </summary>
    public readonly struct PixelSizeBracket
    {
        public PixelSizeBracket(double lower, double upper, double maxInsideRadius, double minOutsideRadius, long edgePixelCount)
        {
            Lower = lower;
            Upper = upper;
            MaxInsideRadiusPx = maxInsideRadius;
            MinOutsideRadiusPx = minOutsideRadius;
            EdgePixelCount = edgePixelCount;
        }

        /// <summary>Exclusive lower bound on P.</summary>
        public double Lower { get; }

        /// <summary>Inclusive upper bound on P.</summary>
        public double Upper { get; }

        /// <summary>Largest pixel radius (in pixel units) among pixels whose height is not -400.</summary>
        public double MaxInsideRadiusPx { get; }

        /// <summary>Smallest pixel radius (in pixel units) among pixels whose height is exactly -400.</summary>
        public double MinOutsideRadiusPx { get; }

        /// <summary>How many pixels held exactly -400. 1 788 980 in both ground-truth worlds - pure geometry, so seed-independent.</summary>
        public long EdgePixelCount { get; }

        /// <summary>False when the cache has no world-edge pixels at all, so nothing can be derived.</summary>
        public bool IsDetermined => EdgePixelCount > 0 && Lower < Upper;

        public bool Contains(double pixelSize) => IsDetermined && pixelSize > Lower && pixelSize <= Upper;

        public override string ToString() => IsDetermined
            ? "P in (" + Lower.ToString("F6") + ", " + Upper.ToString("F6") + ")"
            : "P undetermined (no world-edge pixels)";
    }
}
