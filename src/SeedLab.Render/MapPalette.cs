using System;
using SeedLab.WorldGen;

namespace SeedLab.Render
{
    /// <summary>Which colour table a map is painted with.</summary>
    public enum PaletteMode
    {
        /// <summary>
        /// SeedLab's readable palette: the game's own measured biome bytes for the six biomes the
        /// game's map can distinguish, plus a separated Mountain / Deep North pair and depth-shaded
        /// water. This is the default and it is NOT byte-identical to the in-game map.
        /// </summary>
        SeedLab,

        /// <summary>
        /// <c>Minimap.GetPixelColor</c> exactly: the measured prefab colours, with Ocean, Mountain and
        /// Deep North all pure white. Combined with <c>--plain</c> this reproduces the game's own
        /// <c>cacheMinimapBiome</c> texture byte for byte, which is what the acceptance check compares.
        /// </summary>
        Game,
    }

    /// <summary>
    /// The biome colours, measured from the real <c>cacheMinimapBiome</c> of seed -1772362158 and
    /// cross-checked on seed 319486907 (05-validation.md section 2.1; the same bytes as
    /// <c>SeedLab.Saves.MinimapColors</c>). These are the prefab's serialized values, NOT the code
    /// defaults in <c>Minimap</c> - e.g. Meadows is (146,167,92), not the code's (115,255,110).
    /// </summary>
    public static class MapPalette
    {
        // --- the game's measured table -------------------------------------------------------------
        public static readonly Rgb Meadows = new Rgb(146, 167, 92);
        public static readonly Rgb Swamp = new Rgb(163, 114, 88);
        public static readonly Rgb BlackForest = new Rgb(107, 116, 63);
        public static readonly Rgb Plains = new Rgb(231, 171, 120);
        public static readonly Rgb Mistlands = new Rgb(51, 51, 51);
        public static readonly Rgb AshLands = new Rgb(123, 32, 32);
        public static readonly Rgb White = new Rgb(255, 255, 255);

        // --- SeedLab's two deliberate deviations, recorded in the map legend -----------------------
        /// <summary>Mountain keeps the game's white.</summary>
        public static readonly Rgb Mountain = new Rgb(255, 255, 255);

        /// <summary>
        /// Deep North. The game paints it pure white, identical to Mountain and Ocean, which makes an
        /// offline map unreadable; 07-features.md section 6.1 chooses #E8F0FF for it instead.
        /// </summary>
        public static readonly Rgb DeepNorth = new Rgb(232, 240, 255);

        /// <summary>Shallow water, at exactly the 30 m shoreline.</summary>
        public static readonly Rgb WaterShallow = Rgb.FromHex("#3E6E8C");

        /// <summary>Deep water, at 120 m below the shoreline and beyond.</summary>
        public static readonly Rgb WaterDeep = Rgb.FromHex("#10203A");

        /// <summary>Outside the 10500 m water edge, where <c>GetBiomeHeight</c> returns the -400 constant.</summary>
        public static readonly Rgb Void = Rgb.FromHex("#080D14");

        /// <summary>Ashlands lava, painted where the Ashlands mask alpha exceeds 0.6.</summary>
        public static readonly Rgb Lava = Rgb.FromHex("#FF5A1E");

        /// <summary>The game's water level, hard-coded in Minimap.GetMaskColor and AltBiomeWorldData.</summary>
        public const float WaterLevel = 30f;

        /// <summary><c>Minimap.GetPixelColor(biome)</c>, byte for byte.</summary>
        public static Rgb GameColor(Biome b) => b switch
        {
            Biome.Meadows => Meadows,
            Biome.Swamp => Swamp,
            Biome.BlackForest => BlackForest,
            Biome.Plains => Plains,
            Biome.Mistlands => Mistlands,
            Biome.AshLands => AshLands,
            Biome.Mountain => White,
            Biome.DeepNorth => White,
            Biome.Ocean => White,
            _ => White,                       // GetPixelColor's own default arm
        };

        /// <summary>SeedLab's land palette (Deep North separated from Mountain and Ocean).</summary>
        public static Rgb LandColor(Biome b) => b switch
        {
            Biome.Meadows => Meadows,
            Biome.Swamp => Swamp,
            Biome.BlackForest => BlackForest,
            Biome.Plains => Plains,
            Biome.Mistlands => Mistlands,
            Biome.AshLands => AshLands,
            Biome.Mountain => Mountain,
            Biome.DeepNorth => DeepNorth,
            Biome.Ocean => WaterShallow,
            _ => White,
        };

        /// <summary>
        /// Depth shading for anything below the 30 m water line:
        /// <c>t = clamp01((30 - h) / 120)</c>, lerped shallow to deep (07-features.md section 6.1).
        /// </summary>
        public static Rgb WaterColor(float height)
        {
            double t = Math.Clamp((WaterLevel - height) / 120.0, 0.0, 1.0);
            return Rgb.Lerp(WaterShallow, WaterDeep, t);
        }

        public static string Name(Biome b) => b switch
        {
            Biome.Meadows => "Meadows",
            Biome.Swamp => "Swamp",
            Biome.Mountain => "Mountain",
            Biome.BlackForest => "Black Forest",
            Biome.Plains => "Plains",
            Biome.AshLands => "Ashlands",
            Biome.DeepNorth => "Deep North",
            Biome.Ocean => "Ocean",
            Biome.Mistlands => "Mistlands",
            _ => "None",
        };

        /// <summary>The nine real biomes in the order the legend lists them.</summary>
        public static readonly Biome[] LegendOrder =
        {
            Biome.Meadows, Biome.BlackForest, Biome.Swamp, Biome.Mountain, Biome.Plains,
            Biome.Mistlands, Biome.AshLands, Biome.DeepNorth, Biome.Ocean,
        };
    }
}
