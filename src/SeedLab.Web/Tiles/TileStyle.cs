using System;
using System.Globalization;
using SeedLab.Render;

namespace SeedLab.Web.Tiles
{
    /// <summary>
    /// The layer switches a tile is painted with. Part of the cache key, so flipping a switch in the
    /// UI produces a different tile rather than a stale one.
    /// </summary>
    public sealed class TileStyle : IEquatable<TileStyle>
    {
        public static readonly TileStyle Default = new TileStyle();

        /// <summary>
        /// <c>Minimap.GetPixelColor</c> exactly. With <see cref="Plain"/> this is the byte-for-byte
        /// reproduction of the game's own biome texture - the mode the tiles are verified in.
        /// </summary>
        public bool GamePalette { get; init; }

        /// <summary>Flat biome fill, one sample per pixel, nothing else.</summary>
        public bool Plain { get; init; }

        /// <summary>Hillshade strength; 0 turns it off.</summary>
        public double Shade { get; init; } = 0.35;

        public double Exaggeration { get; init; } = 1.5;

        /// <summary>Depth-shade below the 30 m water line instead of painting a biome colour.</summary>
        public bool Water { get; init; } = true;

        public bool Lava { get; init; } = true;

        public MapOptions ToMapOptions() => new MapOptions
        {
            Palette = GamePalette ? PaletteMode.Game : PaletteMode.SeedLab,
            Plain = Plain,
            Shade = Plain ? 0.0 : Shade,
            Exaggeration = Exaggeration,
            Water = Water,
            Lava = Lava,
            // A tile carries terrain and nothing else. Rings, the grid, the scale bar, the north
            // arrow and markers are drawn by the browser over the tiles, where they stay one pixel
            // wide at every zoom instead of being baked in at 256-pixel granularity.
            Rivers = false,
            Rings = false,
            Grid = false,
            Legend = false,
            Scale = 1,
        };

        /// <summary>True when the tile needs the Ashlands lava mask sampled as well.</summary>
        public bool NeedsLava => Lava && !Plain;

        public string CacheKey =>
            (GamePalette ? "g" : "s")
            + (Plain ? "p" : "-")
            + (Water ? "w" : "-")
            + (Lava ? "l" : "-")
            + Shade.ToString("0.###", CultureInfo.InvariantCulture)
            + "x" + Exaggeration.ToString("0.###", CultureInfo.InvariantCulture);

        public bool Equals(TileStyle? other) => other != null && CacheKey == other.CacheKey;

        public override bool Equals(object? obj) => Equals(obj as TileStyle);

        public override int GetHashCode() => CacheKey.GetHashCode(StringComparison.Ordinal);
    }
}
