using System;
using SeedLab.Render;

namespace SeedLab.Web.Tiles
{
    /// <summary>
    /// The slippy-map tile scheme, chosen so that it lines up exactly with the game's own sampling
    /// grid instead of merely resembling it.
    ///
    /// <para>The tiled square is x, z in [-12288, 12288]: 2048 * 12 m on a side, which is exactly the
    /// extent <c>Minimap.GenerateWorldMap</c> covers. Zoom 0 is one 256 px tile over the whole square
    /// (96 m per pixel); each further level halves the metres per pixel. At <b>zoom 3</b> a pixel is
    /// 12 m and the sample points are bit-identical to G12's - a z=3 tile is a crop of the grid the
    /// game itself samples, which is what makes the tiles checkable against the minimap-cache oracle
    /// (see the verification note in <see cref="TileRenderer"/>).</para>
    ///
    /// <para>Every centre and spacing this type produces is a dyadic rational that is exactly
    /// representable in float, so <see cref="FieldGrid.WorldX"/> reproduces the intended world
    /// coordinate with no rounding of its own at any zoom.</para>
    /// </summary>
    public static class TileGrid
    {
        /// <summary>2048 * 12 m - the side of the square the game's own minimap texture covers.</summary>
        public const double WorldSpanM = 24576.0;

        /// <summary>West / south edge of the tiled square.</summary>
        public const double MinXZ = -WorldSpanM / 2.0;

        /// <summary>East / north edge of the tiled square.</summary>
        public const double MaxXZ = WorldSpanM / 2.0;

        public const int TilePixels = 256;

        /// <summary>
        /// Deepest zoom served. z=9 is 0.1875 m per pixel; the game's own terrain mesh has a vertex
        /// every metre, so past about z=7 the extra pixels are interpolation of the same field rather
        /// than new detail. Kept at 9 so a user can still zoom past the mesh resolution if they want.
        /// </summary>
        public const int MaxZoom = 9;

        /// <summary>The zoom whose pixel is 12 m, i.e. the game's own grid.</summary>
        public const int GameGridZoom = 3;

        public static int TilesPerAxis(int z) => 1 << z;

        public static double TileSpanM(int z) => WorldSpanM / (1 << z);

        /// <summary>Metres per tile pixel at this zoom: 96 / 2^z.</summary>
        public static double MetresPerPixel(int z) => TileSpanM(z) / TilePixels;

        /// <summary>
        /// Tile (x, y) at zoom z. x grows EAST, y grows SOUTH (the usual web-map convention), so a
        /// tile image can be blitted straight onto a north-up canvas.
        /// </summary>
        public static (double CentreX, double CentreZ) TileCentre(int z, int x, int y)
        {
            double s = TileSpanM(z);
            return (MinXZ + (x + 0.5) * s, MaxXZ - (y + 0.5) * s);
        }

        public static bool InRange(int z, int x, int y)
        {
            if (z < 0 || z > MaxZoom) return false;
            int n = TilesPerAxis(z);
            return x >= 0 && x < n && y >= 0 && y < n;
        }

        /// <summary>
        /// The sampling grid for one tile, with a one-pixel apron on every side.
        ///
        /// <para>The apron is not padding for its own sake: the hillshade reads the four neighbours of
        /// every cell, and at a tile border there are none, so a tile rendered at exactly 256 x 256
        /// would clamp its edge gradients and every tile seam would show as a bright or dark hairline.
        /// Sampling 258 x 258 and cropping the border away gives every surviving pixel real
        /// neighbours, and the tiles join invisibly.</para>
        /// </summary>
        public static FieldGrid SamplingGrid(int z, int x, int y)
        {
            (double cx, double cz) = TileCentre(z, x, y);
            return new FieldGrid(TilePixels + 2, (float)MetresPerPixel(z), (float)cx, (float)cz);
        }
    }
}
